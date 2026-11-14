using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace RemoteMic.Platform.Windows.Audio;

public sealed record AudioEndpointInfo(string Id, string Name);

public sealed record AudioRouteResult(
    bool Success,
    string Message,
    AudioEndpointInfo? Microphone = null,
    AudioEndpointInfo? VirtualCable = null);

/// <summary>
/// Routes the local microphone and decoded ATVV PCM to a virtual-cable render
/// endpoint. The capture side of that cable then appears to applications as a
/// microphone. The built-in sink recognises both the future RemoteMic driver
/// endpoint and an independently installed VB-CABLE endpoint.
/// </summary>
public sealed class AudioGraphRouter : IAsyncDisposable
{
    private const int OutputRate = 48_000;
    private const int OutputChannels = 2;
    private const double RemoteGain = 10.0; // +20 dB, matching the macOS app.

    private readonly ConcurrentQueue<short> _remoteSamples = new();
    private readonly object _gate = new();
    private readonly HashSet<object> _activeRemoteSources = [];

    private AudioGraph? _graph;
    private AudioDeviceInputNode? _localInput;
    private AudioFrameOutputNode? _localMonitor;
    private AudioFrameInputNode? _remoteInput;
    private AudioDeviceOutputNode? _virtualOutput;
    private bool _remoteActive;
    private bool _remoteEnabled = true;
    private bool _localEnabled = true;
    private bool _captureAlwaysOn;
    private bool _localCaptureActive;
    private CancellationTokenSource? _localIdleStop;
    private long _lastLocalLevelAt;

    public event EventHandler<double>? RemoteLevelChanged;
    public event EventHandler<double>? LocalLevelChanged;
    public event EventHandler<bool>? CaptureActivityChanged;
    public event EventHandler<string>? Diagnostic;

    public bool IsRunning => _graph is not null;
    public bool IsRemoteActive
    {
        get
        {
            lock (_gate)
            {
                return _remoteActive;
            }
        }
    }
    public string? ActiveMicrophoneName { get; private set; }
    public string? ActiveVirtualCableName { get; private set; }

    public static async Task<IReadOnlyList<AudioEndpointInfo>> GetVirtualCableCandidatesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        return devices
            .Where(IsVirtualCableRenderEndpoint)
            .Select(device => new AudioEndpointInfo(device.Id, device.Name))
            .ToArray();
    }

    public static async Task<IReadOnlyList<AudioEndpointInfo>> GetMicrophonesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
        return devices.Select(device => new AudioEndpointInfo(device.Id, device.Name)).ToArray();
    }

    public async Task<AudioRouteResult> StartAsync(
        string? microphoneId = null,
        string? virtualCableId = null,
        CancellationToken cancellationToken = default)
    {
        await StopAsync();

        var renderDevices = await DeviceInformation
            .FindAllAsync(MediaDevice.GetAudioRenderSelector())
            .AsTask(cancellationToken);
        var virtualCable = SelectDevice(
            renderDevices,
            virtualCableId,
            IsVirtualCableRenderEndpoint);
        if (virtualCable is null)
        {
            return new AudioRouteResult(
                false,
                "未找到 RemoteMic Input 或 CABLE Input。请安装虚拟麦克风驱动，或单独安装 VB-CABLE。");
        }

        var graphSettings = new AudioGraphSettings(AudioRenderCategory.Communications)
        {
            EncodingProperties = AudioEncodingProperties.CreatePcm(
                OutputRate,
                OutputChannels,
                16),
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
            DesiredSamplesPerQuantum = 480,
            PrimaryRenderDevice = virtualCable,
        };

        var graphResult = await AudioGraph.CreateAsync(graphSettings).AsTask(cancellationToken);
        if (graphResult.Status != AudioGraphCreationStatus.Success)
        {
            return new AudioRouteResult(false, $"无法创建音频图：{graphResult.Status}");
        }

        var graph = graphResult.Graph;
        try
        {
            var outputResult = await graph
                .CreateDeviceOutputNodeAsync()
                .AsTask(cancellationToken);
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                return new AudioRouteResult(false, $"无法打开虚拟音频端点：{outputResult.Status}");
            }

            var captureDevices = await DeviceInformation
                .FindAllAsync(MediaDevice.GetAudioCaptureSelector())
                .AsTask(cancellationToken);
            var microphone = SelectDevice(
                captureDevices,
                microphoneId,
                device => !IsVirtualCableCaptureEndpoint(device));

            AudioDeviceInputNode? localInput = null;
            AudioFrameOutputNode? localMonitor = null;
            if (microphone is not null)
            {
                var inputResult = await graph.CreateDeviceInputNodeAsync(
                        MediaCategory.Speech,
                        graph.EncodingProperties,
                        microphone)
                    .AsTask(cancellationToken);
                if (inputResult.Status == AudioDeviceNodeCreationStatus.Success)
                {
                    localInput = inputResult.DeviceInputNode;
                    localMonitor = graph.CreateFrameOutputNode(graph.EncodingProperties);
                }
            }

            var remoteInput = graph.CreateFrameInputNode(graph.EncodingProperties);
            var output = outputResult.DeviceOutputNode;
            remoteInput.AddOutgoingConnection(output);
            if (localInput is not null)
            {
                localInput.AddOutgoingConnection(output);
                localInput.AddOutgoingConnection(localMonitor!);
                graph.QuantumStarted += OnGraphQuantumStarted;
            }

            remoteInput.QuantumStarted += OnRemoteQuantumStarted;
            remoteInput.AudioFrameCompleted += OnRemoteAudioFrameCompleted;
            remoteInput.OutgoingGain = 0;
            // Match the Microsoft AudioCreation frame-input pattern: keep the
            // node stopped while the graph starts, then start it when a remote
            // stream becomes active. This transition is what begins quantum
            // requests on desktop AudioGraph hosts.
            remoteInput.Stop();
            if (localInput is not null)
            {
                localInput.OutgoingGain = 1;
                localInput.Stop();
            }

            lock (_gate)
            {
                _graph = graph;
                _virtualOutput = output;
                _remoteInput = remoteInput;
                _localInput = localInput;
                _localMonitor = localMonitor;
                ActiveMicrophoneName = microphone?.Name;
                ActiveVirtualCableName = virtualCable.Name;
            }

            graph.Start();
            CaptureActivityChanged?.Invoke(this, false);
            return new AudioRouteResult(
                true,
                localInput is null
                    ? "虚拟麦克风已就绪；本机麦克风不可用，当前只转发遥控器音频。"
                    : "RemoteMic 2ch 已就绪。",
                microphone is null ? null : new AudioEndpointInfo(microphone.Id, microphone.Name),
                new AudioEndpointInfo(virtualCable.Id, virtualCable.Name));
        }
        catch
        {
            graph.Dispose();
            throw;
        }
    }

    public void SetRemoteActive(object source, bool active)
    {
        ArgumentNullException.ThrowIfNull(source);

        AudioFrameInputNode? remote;
        AudioDeviceInputNode? local;
        bool effectiveActive;
        bool remoteEnabled;
        bool localEnabled;
        lock (_gate)
        {
            var wasActive = _remoteActive;
            if (active)
            {
                _activeRemoteSources.Add(source);
            }
            else
            {
                _activeRemoteSources.Remove(source);
            }

            effectiveActive = _activeRemoteSources.Count > 0;
            _remoteActive = effectiveActive;
            if (wasActive == effectiveActive)
            {
                return;
            }

            remote = _remoteInput;
            local = _localInput;
            remoteEnabled = _remoteEnabled;
            localEnabled = _localEnabled;
        }

        if (effectiveActive)
        {
            StartLocalCapture();
        }
        if (remote is not null)
        {
            remote.OutgoingGain = effectiveActive && remoteEnabled ? 1 : 0;
            if (effectiveActive && remoteEnabled)
            {
                remote.Start();
            }
            else
            {
                remote.Stop();
                remote.DiscardQueuedFrames();
                DrainRemoteQueue();
            }
        }

        if (local is not null)
        {
            // Remote-first routing: never mix the live local mic into a remote
            // voice stream. When the remote stops, fall back immediately.
            local.OutgoingGain = (effectiveActive && remoteEnabled) || !localEnabled ? 0 : 1;
        }

        if (!effectiveActive && !_captureAlwaysOn)
        {
            ScheduleLocalIdleStop();
        }
    }

    public void SetLocalInputEnabled(bool enabled)
    {
        AudioDeviceInputNode? local;
        bool suppressForRemote;
        lock (_gate)
        {
            _localEnabled = enabled;
            local = _localInput;
            suppressForRemote = _remoteActive && _remoteEnabled;
        }

        if (local is not null)
        {
            local.OutgoingGain = enabled && !suppressForRemote ? 1 : 0;
        }
    }

    public void SetRemoteInputEnabled(bool enabled)
    {
        AudioFrameInputNode? remote;
        AudioDeviceInputNode? local;
        bool remoteActive;
        bool localEnabled;
        lock (_gate)
        {
            _remoteEnabled = enabled;
            remote = _remoteInput;
            local = _localInput;
            remoteActive = _remoteActive;
            localEnabled = _localEnabled;
        }

        if (remote is not null)
        {
            remote.OutgoingGain = enabled && remoteActive ? 1 : 0;
            if (enabled && remoteActive)
            {
                remote.Start();
            }
            else
            {
                remote.Stop();
                remote.DiscardQueuedFrames();
                DrainRemoteQueue();
            }
        }
        if (local is not null)
        {
            local.OutgoingGain = localEnabled && (!remoteActive || !enabled) ? 1 : 0;
        }
    }

    public void SetCaptureAlwaysOn(bool alwaysOn)
    {
        _captureAlwaysOn = alwaysOn;
        if (alwaysOn)
        {
            StartLocalCapture();
        }
        else if (!_remoteActive)
        {
            ScheduleLocalIdleStop();
        }
    }

    public void PrewarmLocalCapture() => StartLocalCapture();

    public void FeedRemotePcm(ReadOnlySpan<short> monoPcm, int inputSampleRate)
    {
        if (monoPcm.IsEmpty || inputSampleRate <= 0)
        {
            return;
        }

        var peak = 0;
        var stereo = ResampleMonoToStereo(monoPcm, inputSampleRate, OutputRate);
        foreach (var sample in stereo)
        {
            _remoteSamples.Enqueue(sample);
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        var db = peak == 0 ? -60 : Math.Max(-60, 20 * Math.Log10(peak / 32768.0));
        RemoteLevelChanged?.Invoke(this, db);

        // Bound backlog to roughly 250 ms. If the renderer stalls, prefer
        // recent speech over accumulating latency.
        var maximumSamples = OutputRate * OutputChannels / 4;
        while (_remoteSamples.Count > maximumSamples && _remoteSamples.TryDequeue(out _))
        {
        }
    }

    public async Task StopAsync()
    {
        AudioGraph? graph;
        AudioFrameInputNode? remote;
        var hadLocalMonitor = false;
        lock (_gate)
        {
            graph = _graph;
            remote = _remoteInput;
            _graph = null;
            _remoteInput = null;
            _localInput = null;
            hadLocalMonitor = _localMonitor is not null;
            _localMonitor = null;
            _virtualOutput = null;
            _activeRemoteSources.Clear();
            _remoteActive = false;
            ActiveMicrophoneName = null;
            ActiveVirtualCableName = null;
        }

        CancelLocalIdleStop();
        _localCaptureActive = false;

        if (graph is null)
        {
            return;
        }

        remote!.QuantumStarted -= OnRemoteQuantumStarted;
        remote.AudioFrameCompleted -= OnRemoteAudioFrameCompleted;
        remote.Stop();
        if (hadLocalMonitor)
        {
            graph.QuantumStarted -= OnGraphQuantumStarted;
        }
        graph.Stop();
        await Task.Yield();
        graph.Dispose();
        DrainRemoteQueue();
        CaptureActivityChanged?.Invoke(this, false);
        LocalLevelChanged?.Invoke(this, -60);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private unsafe void OnRemoteQuantumStarted(
        AudioFrameInputNode sender,
        FrameInputNodeQuantumStartedEventArgs args)
    {
        AudioFrame? frame = null;
        try
        {
            var requiredFrames = checked((int)args.RequiredSamples);
            if (requiredFrames <= 0)
            {
                return;
            }

            var sampleCount = checked(requiredFrames * OutputChannels);
            var byteCount = checked(sampleCount * sizeof(short));
            frame = new AudioFrame((uint)byteCount);
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                reference.As<IMemoryBufferByteAccess>().GetBuffer(
                    out var data,
                    out var capacity);
                var destination = new Span<short>(
                    data,
                    Math.Min(sampleCount, checked((int)capacity / sizeof(short))));
                for (var index = 0; index < destination.Length; index++)
                {
                    if (_remoteSamples.TryDequeue(out var sample))
                    {
                        destination[index] = sample;
                    }
                    else
                    {
                        destination[index] = 0;
                    }
                }
            }

            sender.AddFrame(frame);
            frame = null; // AudioFrameCompleted owns disposal after submission.
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(
                this,
                $"render error={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private static void OnRemoteAudioFrameCompleted(
        AudioFrameInputNode sender,
        AudioFrameCompletedEventArgs args)
    {
        args.Frame.Dispose();
    }

    private unsafe void OnGraphQuantumStarted(AudioGraph sender, object args)
    {
        AudioFrameOutputNode? monitor;
        lock (_gate)
        {
            monitor = _localMonitor;
        }
        if (monitor is null)
        {
            return;
        }

        using var frame = monitor.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
        var validBytes = Math.Min(buffer.Length, capacity);
        var samples = new ReadOnlySpan<short>(data, checked((int)validBytes / sizeof(short)));
        var peak = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLocalLevelAt) < 50)
        {
            return;
        }
        Interlocked.Exchange(ref _lastLocalLevelAt, now);
        var db = peak == 0 ? -60 : Math.Max(-60, 20 * Math.Log10(peak / 32768.0));
        LocalLevelChanged?.Invoke(this, db);
    }

    private static short[] ResampleMonoToStereo(
        ReadOnlySpan<short> source,
        int sourceRate,
        int destinationRate)
    {
        var destinationFrames = Math.Max(
            1,
            (int)Math.Round(source.Length * destinationRate / (double)sourceRate));
        var destination = new short[destinationFrames * OutputChannels];
        var ratio = sourceRate / (double)destinationRate;

        for (var frame = 0; frame < destinationFrames; frame++)
        {
            var position = frame * ratio;
            var leftIndex = Math.Min(source.Length - 1, (int)position);
            var rightIndex = Math.Min(source.Length - 1, leftIndex + 1);
            var fraction = position - leftIndex;
            var interpolated = source[leftIndex] +
                               ((source[rightIndex] - source[leftIndex]) * fraction);
            var boosted = Math.Clamp(
                (int)Math.Round(interpolated * RemoteGain),
                short.MinValue,
                short.MaxValue);
            destination[frame * 2] = (short)boosted;
            destination[(frame * 2) + 1] = (short)boosted;
        }

        return destination;
    }

    private static DeviceInformation? SelectDevice(
        IEnumerable<DeviceInformation> devices,
        string? preferredId,
        Func<DeviceInformation, bool> fallbackPredicate)
    {
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var preferred = devices.FirstOrDefault(device => device.Id == preferredId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return devices.FirstOrDefault(fallbackPredicate);
    }

    private static bool IsVirtualCableRenderEndpoint(DeviceInformation device) =>
        device.Name.Contains("RemoteMic Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("CABLE In", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualCableCaptureEndpoint(DeviceInformation device) =>
        device.Name.Contains("RemoteMic 2ch", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("BlackHole", StringComparison.OrdinalIgnoreCase);

    private void DrainRemoteQueue()
    {
        while (_remoteSamples.TryDequeue(out _))
        {
        }
    }

    private void StartLocalCapture()
    {
        CancelLocalIdleStop();
        if (_localCaptureActive || _localInput is not { } local)
        {
            return;
        }

        local.Start();
        _localCaptureActive = true;
        CaptureActivityChanged?.Invoke(this, true);
    }

    private void ScheduleLocalIdleStop()
    {
        CancelLocalIdleStop();
        var cancellation = new CancellationTokenSource();
        _localIdleStop = cancellation;
        _ = StopLocalAfterIdleAsync(cancellation.Token);
    }

    private async Task StopLocalAfterIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            if (!_captureAlwaysOn && !_remoteActive && _localCaptureActive && _localInput is { } local)
            {
                local.Stop();
                _localCaptureActive = false;
                CaptureActivityChanged?.Invoke(this, false);
                LocalLevelChanged?.Invoke(this, -60);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelLocalIdleStop()
    {
        var cancellation = Interlocked.Exchange(ref _localIdleStop, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
