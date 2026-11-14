using RemoteMic.Core.Atvv;
using RemoteMic.Platform.Windows.Audio;

namespace RemoteMic.Platform.Windows.Bluetooth;

public readonly record struct AtvvAudioStarted(byte Reason, AtvvCodec Codec, byte StreamId);

/// <summary>
/// Joins the WinRT GATT transport, platform-independent ATVV state machine and
/// the Windows audio route. One instance owns one physical remote.
/// </summary>
public sealed class AtvvSessionController : IAsyncDisposable
{
    private readonly AtvvGattClient _transport;
    private readonly AudioGraphRouter _audio;
    private readonly AtvvProtocol _protocol = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private readonly object _controlGate = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];

    private CancellationTokenSource? _keepAlive;
    private CancellationTokenSource? _connectionLifetime;
    private Task? _capabilitiesRequest;
    private Task? _staleReset;
    private DateTimeOffset _lastStartSearch = DateTimeOffset.MinValue;
    private long _streamGeneration;
    private byte _streamId;
    private bool _streaming;
    private bool _disposed;

    public AtvvSessionController(AtvvGattClient transport, AudioGraphRouter audio)
    {
        _transport = transport;
        _audio = audio;
        _transport.AudioReceived += OnAudioReceived;
        _transport.ControlReceived += OnControlReceived;
        _transport.ConnectionChanged += OnConnectionChanged;
        _transport.Diagnostic += ForwardDiagnostic;
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<bool>? StreamingChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<AtvvCapabilities>? CapabilitiesReady;
    public event EventHandler<AtvvAudioStarted>? AudioStarted;
    public event EventHandler<byte>? AudioStopped;
    public event EventHandler<ushort>? MicrophoneOpenFailed;

    public bool IsStreaming
    {
        get
        {
            lock (_stateGate)
            {
                return _streaming;
            }
        }
    }

    public bool IsConnected => _transport.IsConnected;

    public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _transport.ConnectAsync(deviceId, cancellationToken);
        var request = EnsureCapabilitiesRequested();
        TrackBackground(request, "请求 ATVV capabilities");
        await request.WaitAsync(cancellationToken);
    }

    public async Task OpenMicrophoneAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Task? staleReset;
        lock (_stateGate)
        {
            staleReset = _staleReset;
        }

        if (staleReset is not null)
        {
            await staleReset.WaitAsync(cancellationToken);
        }

        await _commands.WaitAsync(cancellationToken);
        try
        {
            byte[] command;
            lock (_stateGate)
            {
                if (_streaming)
                {
                    return;
                }

                _protocol.PrepareForAudioStream();
                command = _protocol.CreateMicrophoneOpenCommand();
            }

            await _transport.WriteCommandAsync(command, cancellationToken);
            Diagnostic?.Invoke(this, "已发送 MIC_OPEN。");
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task CloseMicrophoneAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(cancellationToken);
        try
        {
            byte[]? command;
            long generation;
            lock (_stateGate)
            {
                command = _protocol.Capabilities is null
                    ? null
                    : _protocol.CreateMicrophoneCloseCommand(_streamId);
                generation = _streamGeneration;
            }

            if (command is null)
            {
                FinishStreamLocally();
                return;
            }

            await _transport.WriteCommandAsync(command, cancellationToken);
            Diagnostic?.Invoke(this, "已发送 MIC_CLOSE。");

            // Some firmware never sends AUDIO_STOP after close. The captured
            // generation prevents an old timeout from ending a later stream.
            TrackBackground(
                FinishAfterCloseTimeoutAsync(generation, _lifetime.Token),
                "等待 AUDIO_STOP");
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task ToggleMicrophoneAsync(CancellationToken cancellationToken = default)
    {
        if (IsStreaming)
        {
            await CloseMicrophoneAsync(cancellationToken);
        }
        else
        {
            await OpenMicrophoneAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_controlGate)
        {
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }
        }

        _transport.AudioReceived -= OnAudioReceived;
        _transport.ControlReceived -= OnControlReceived;
        _transport.ConnectionChanged -= OnConnectionChanged;
        _transport.Diagnostic -= ForwardDiagnostic;

        _lifetime.Cancel();
        CancelConnectionLifetime();
        StopKeepAlive();
        FinishStreamLocally();

        Task[] background;
        lock (_backgroundGate)
        {
            background = _backgroundTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(background);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Each tracked task reports its own diagnostic before removal.
        }

        await _transport.DisposeAsync();
        _commands.Dispose();
        _lifetime.Dispose();
    }

    private void OnAudioReceived(object? sender, ReadOnlyMemory<byte> packet)
    {
        AtvvAudioFrame? frame;
        int sampleRate;
        lock (_stateGate)
        {
            if (_disposed || !_streaming || _protocol.Codec is null)
            {
                return;
            }

            frame = _protocol.DecodeAudio(packet.Span);
            sampleRate = _protocol.Codec.Value.SampleRate();
        }

        if (frame is { Samples.Length: > 0 })
        {
            _audio.FeedRemotePcm(frame.Value.Samples, sampleRate);
        }
    }

    private void OnControlReceived(object? sender, ReadOnlyMemory<byte> packet)
    {
        try
        {
            lock (_controlGate)
            {
                AtvvControlEvent controlEvent;
                lock (_stateGate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    controlEvent = _protocol.ParseControl(packet.Span);
                }

                HandleControl(controlEvent);
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"ATVV 控制事件处理失败：{exception.Message}");
        }
    }

    private void HandleControl(AtvvControlEvent controlEvent)
    {
        switch (controlEvent)
        {
            case AtvvControlEvent.Capabilities capabilities:
                HandleCapabilities(capabilities.Value);
                break;

            case AtvvControlEvent.StartSearch:
                HandleStartSearch();
                break;

            case AtvvControlEvent.AudioStart start:
                HandleAudioStart(start);
                break;

            case AtvvControlEvent.AudioStop stop:
                Diagnostic?.Invoke(this, $"AUDIO_STOP reason=0x{stop.Reason:X2}");
                // A Chromecast short press ends its physical stream with
                // reason 02 just before the coordinator opens the persistent
                // host stream. Keep the route and queued speech alive across
                // that hand-off so words spoken immediately after the press
                // are not discarded before the recognizer is ready.
                FinishStreamLocally(preserveAudioRoute: stop.Reason == 0x02);
                AudioStopped?.Invoke(this, stop.Reason);
                break;

            case AtvvControlEvent.AudioSync sync:
                lock (_stateGate)
                {
                    _protocol.ApplyAudioSync(sync.Codec, sync.Sequence, sync.Predictor, sync.StepIndex);
                }
                break;

            case AtvvControlEvent.MicrophoneOpenError error:
                MicrophoneOpenFailed?.Invoke(this, error.Code);
                Diagnostic?.Invoke(this, $"MIC_OPEN 错误：0x{error.Code:X4}");
                FinishStreamLocally();
                break;
        }
    }

    private void HandleCapabilities(AtvvCapabilities capabilities)
    {
        Task reset;
        lock (_stateGate)
        {
            _protocol.AcceptCapabilities(capabilities);
            _staleReset ??= ResetStaleSessionAsync(CurrentConnectionToken());
            reset = _staleReset;
        }

        CapabilitiesReady?.Invoke(this, capabilities);
        Diagnostic?.Invoke(
            this,
            $"ATVV {capabilities.Version} · {capabilities.SelectedCodec} · {capabilities.FrameSize} bytes");
        TrackBackground(reset, "重置遥控器残留语音会话");
    }

    private void HandleStartSearch()
    {
        var shouldOpen = false;
        var now = DateTimeOffset.UtcNow;
        lock (_stateGate)
        {
            if (!_streaming && now - _lastStartSearch >= TimeSpan.FromMilliseconds(500))
            {
                _lastStartSearch = now;
                shouldOpen = true;
            }
        }

        if (shouldOpen)
        {
            TrackBackground(OpenMicrophoneSafelyAsync(CurrentConnectionToken()), "响应 START_SEARCH");
        }
    }

    private void HandleAudioStart(AtvvControlEvent.AudioStart start)
    {
        var becameStreaming = false;
        lock (_stateGate)
        {
            if (!_streaming)
            {
                _protocol.BeginAudioStream(start.Codec);
                _streamId = start.StreamId;
                _streaming = true;
                _streamGeneration++;
                becameStreaming = true;
            }
        }

        if (!becameStreaming)
        {
            // A physical Chromecast press can emit reason 03 while a
            // host-opened persistent stream is already active. The decoder
            // must not restart, but the voice-session coordinator still needs
            // that edge so the matching reason-02 STOP can toggle recognition.
            AudioStarted?.Invoke(this, new AtvvAudioStarted(start.Reason, start.Codec, start.StreamId));
            Diagnostic?.Invoke(
                this,
                $"已忽略重复 AUDIO_START reason=0x{start.Reason:X2} stream={start.StreamId}");
            return;
        }

        _audio.SetRemoteActive(this, true);
        StartKeepAlive();
        StreamingChanged?.Invoke(this, true);
        if (start.Reason == 0x03)
        {
            // A physical Chromecast voice-button press announces AUDIO_START
            // before the host acknowledges it with MIC_OPEN. Publish the edge
            // that wakes the target recognizer only after the command succeeds;
            // otherwise the recognizer opens with no ADPCM stream behind it.
            TrackBackground(
                AcknowledgePhysicalAudioStartAsync(start, CurrentConnectionToken()),
                "响应物理 AUDIO_START");
        }
        else
        {
            AudioStarted?.Invoke(
                this,
                new AtvvAudioStarted(start.Reason, start.Codec, start.StreamId));
        }
        Diagnostic?.Invoke(
            this,
            $"AUDIO_START reason=0x{start.Reason:X2} stream={start.StreamId}");
    }

    private async Task AcknowledgePhysicalAudioStartAsync(
        AtvvControlEvent.AudioStart start,
        CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken);
        try
        {
            byte[] command;
            lock (_stateGate)
            {
                if (_disposed || !_streaming)
                {
                    return;
                }

                command = _protocol.CreateMicrophoneOpenCommand();
            }

            await _transport.WriteCommandAsync(command, cancellationToken);
            Diagnostic?.Invoke(this, "已响应物理 AUDIO_START 并发送 MIC_OPEN。");
        }
        finally
        {
            _commands.Release();
        }

        AudioStarted?.Invoke(
            this,
            new AtvvAudioStarted(start.Reason, start.Codec, start.StreamId));
    }

    private Task EnsureCapabilitiesRequested()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _capabilitiesRequest ??= RequestCapabilitiesAsync(CurrentConnectionToken());
            return _capabilitiesRequest;
        }
    }

    private async Task RequestCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await SendCommandSerializedAsync(AtvvProtocol.GetCapabilitiesCommand, cancellationToken);
        Diagnostic?.Invoke(this, "已请求 ATVV capabilities。");
    }

    private async Task ResetStaleSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] command;
            lock (_stateGate)
            {
                command = _protocol.CreateMicrophoneCloseCommand(0);
            }
            await SendCommandSerializedAsync(command, cancellationToken);
            Diagnostic?.Invoke(this, "已重置遥控器残留语音会话。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"重置语音会话失败：{exception.Message}");
        }
    }

    private async Task OpenMicrophoneSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await OpenMicrophoneAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"打开遥控器麦克风失败：{exception.Message}");
        }
    }

    private async Task SendCommandSerializedAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken);
        try
        {
            await _transport.WriteCommandAsync(command, cancellationToken);
        }
        finally
        {
            _commands.Release();
        }
    }

    private void StartKeepAlive()
    {
        var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous;
        lock (_stateGate)
        {
            if (!_streaming)
            {
                keepAlive.Dispose();
                return;
            }

            previous = _keepAlive;
            _keepAlive = keepAlive;
        }
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        TrackBackground(KeepAliveLoopAsync(keepAlive.Token), "ATVV keep-alive");
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                byte[] command;
                lock (_stateGate)
                {
                    if (!_streaming)
                    {
                        return;
                    }
                    command = _protocol.CreateKeepAliveCommand(_streamId);
                }
                await SendCommandSerializedAsync(command, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"ATVV keep-alive 失败：{exception.Message}");
        }
    }

    private async Task FinishAfterCloseTimeoutAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        if (FinishStreamLocally(generation))
        {
            Diagnostic?.Invoke(this, "未收到 AUDIO_STOP，已本地结束会话。");
        }
    }

    private bool FinishStreamLocally(
        long? expectedGeneration = null,
        bool preserveAudioRoute = false)
    {
        bool wasStreaming;
        lock (_stateGate)
        {
            if (expectedGeneration is not null &&
                (_streamGeneration != expectedGeneration || !_streaming))
            {
                return false;
            }

            wasStreaming = _streaming;
            _streaming = false;
            _streamGeneration++;
            _protocol.EndAudioStream();
        }

        StopKeepAlive();
        if (!preserveAudioRoute)
        {
            _audio.SetRemoteActive(this, false);
        }
        if (wasStreaming)
        {
            StreamingChanged?.Invoke(this, false);
        }
        return wasStreaming;
    }

    private void StopKeepAlive()
    {
        CancellationTokenSource? keepAlive;
        lock (_stateGate)
        {
            keepAlive = _keepAlive;
            _keepAlive = null;
        }
        if (keepAlive is not null)
        {
            keepAlive.Cancel();
            keepAlive.Dispose();
        }
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (connected)
        {
            try
            {
                lock (_stateGate)
                {
                    _connectionLifetime ??=
                        CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                }
                var request = EnsureCapabilitiesRequested();
                TrackBackground(request, "重新请求 ATVV capabilities");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
        else
        {
            CancelConnectionLifetime();
            lock (_stateGate)
            {
                _capabilitiesRequest = null;
                _staleReset = null;
                _lastStartSearch = DateTimeOffset.MinValue;
            }
            FinishStreamLocally();
        }

        ConnectionChanged?.Invoke(this, connected);
    }

    private CancellationToken CurrentConnectionToken()
    {
        lock (_stateGate)
        {
            return _connectionLifetime?.Token ?? _lifetime.Token;
        }
    }

    private void CancelConnectionLifetime()
    {
        CancellationTokenSource? connection;
        lock (_stateGate)
        {
            connection = _connectionLifetime;
            _connectionLifetime = null;
        }
        if (connection is not null)
        {
            connection.Cancel();
            connection.Dispose();
        }
    }

    private void TrackBackground(Task task, string operation)
    {
        lock (_backgroundGate)
        {
            if (!_backgroundTasks.Add(task))
            {
                return;
            }
        }

        _ = ObserveBackgroundAsync(task, operation);
    }

    private async Task ObserveBackgroundAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"{operation}失败：{exception.Message}");
        }
        finally
        {
            lock (_backgroundGate)
            {
                _backgroundTasks.Remove(task);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private void ForwardDiagnostic(object? sender, string message) =>
        Diagnostic?.Invoke(this, message);
}
