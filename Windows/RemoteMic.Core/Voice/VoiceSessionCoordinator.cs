using RemoteMic.Core.Mapping;

namespace RemoteMic.Core.Voice;

public enum VoiceSessionPhase
{
    Closed,
    Opening,
    Open,
    Closing,
}

public abstract record VoiceSessionCommand
{
    public sealed record TriggerTap(bool OpensRecognition) : VoiceSessionCommand;
    public sealed record OpenRemote(SupportedRemoteId Remote, bool BypassDebounce) : VoiceSessionCommand;
    public sealed record CloseAllRemotes : VoiceSessionCommand;
}

/// <summary>
/// Coordinates the target application's toggle key with ATVV transport state.
/// The state machine is clock-injected and platform-independent so the subtle
/// Chromecast tap/hold and stale-start rules can be tested without hardware.
/// </summary>
public sealed class VoiceSessionCoordinator
{
    public static readonly TimeSpan RemoteHoldThreshold = TimeSpan.FromMilliseconds(550);
    public static readonly TimeSpan X6FirstHidSuppression = TimeSpan.FromSeconds(1);

    private sealed record PhysicalGesture(
        DateTimeOffset BeganAt,
        bool RecognitionWasOpen,
        long Generation);

    private readonly object _gate = new();
    private readonly HashSet<SupportedRemoteId> _streamingRemotes = [];
    private readonly Dictionary<SupportedRemoteId, PhysicalGesture> _physicalGestures = [];
    private VoiceSessionPhase _phase;
    private bool _targetRecording;
    private long _generation;
    private DateTimeOffset _lastX6OpenAt = DateTimeOffset.MinValue;
    private bool _suppressFirstX6HidShort;

    public event Action<VoiceSessionCommand>? CommandIssued;

    public VoiceSessionPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public IReadOnlyCollection<SupportedRemoteId> StreamingRemotes
    {
        get
        {
            lock (_gate)
            {
                return _streamingRemotes.ToArray();
            }
        }
    }

    public void RemoteAudioStarted(
        SupportedRemoteId remote,
        byte reason,
        DateTimeOffset now,
        bool supportsPhysicalHoldGesture)
    {
        List<VoiceSessionCommand> commands = [];
        lock (_gate)
        {
            var inserted = _streamingRemotes.Add(remote);

            if (_phase == VoiceSessionPhase.Closing)
            {
                commands.Add(new VoiceSessionCommand.CloseAllRemotes());
            }
            else
            {
                if (supportsPhysicalHoldGesture && reason == 0x03)
                {
                    var recognitionWasOpen =
                        _phase == VoiceSessionPhase.Open || _targetRecording;
                    _physicalGestures.TryAdd(
                        remote,
                        new PhysicalGesture(now, recognitionWasOpen, _generation));
                    if (recognitionWasOpen)
                    {
                        Emit(commands);
                        return;
                    }
                }

                if (inserted && _phase == VoiceSessionPhase.Closed)
                {
                    // Chromecast reason 0 while closed is a stale host-open
                    // response. X6 uses reason 0 for its real first press.
                    if (remote == SupportedRemoteId.Chromecast && reason == 0x00)
                    {
                        _streamingRemotes.Remove(remote);
                        commands.Add(new VoiceSessionCommand.CloseAllRemotes());
                    }
                    else
                    {
                        _phase = VoiceSessionPhase.Opening;
                        _generation++;
                        if (_physicalGestures.TryGetValue(remote, out var gesture))
                        {
                            _physicalGestures[remote] = gesture with { Generation = _generation };
                        }
                        if (remote == SupportedRemoteId.X6)
                        {
                            _lastX6OpenAt = now;
                            _suppressFirstX6HidShort = true;
                        }
                        commands.Add(new VoiceSessionCommand.TriggerTap(OpensRecognition: true));
                    }
                }
            }
        }

        Emit(commands);
    }

    public void RemoteAudioStopped(
        SupportedRemoteId remote,
        byte reason,
        DateTimeOffset now)
    {
        List<VoiceSessionCommand> commands = [];
        lock (_gate)
        {
            _streamingRemotes.Remove(remote);
            if (reason == 0x02 && _physicalGestures.Remove(remote, out var gesture))
            {
                if (gesture.Generation == _generation)
                {
                    var wasHold = now - gesture.BeganAt >= RemoteHoldThreshold;
                    if (wasHold || gesture.RecognitionWasOpen)
                    {
                        BeginClosing(commands);
                    }
                    else if (_phase is VoiceSessionPhase.Opening or VoiceSessionPhase.Open)
                    {
                        commands.Add(new VoiceSessionCommand.OpenRemote(remote, BypassDebounce: true));
                    }
                }
            }
        }

        Emit(commands);
    }

    public void RemoteHidShortPress(SupportedRemoteId remote, DateTimeOffset now)
    {
        List<VoiceSessionCommand> commands = [];
        lock (_gate)
        {
            if (remote == SupportedRemoteId.X6 && _suppressFirstX6HidShort)
            {
                _suppressFirstX6HidShort = false;
                if (now - _lastX6OpenAt <= X6FirstHidSuppression)
                {
                    return;
                }
            }

            BeginClosing(commands);
        }

        Emit(commands);
    }

    public void RemoteHidLongPressEnded(SupportedRemoteId remote)
    {
        List<VoiceSessionCommand> commands = [];
        lock (_gate)
        {
            if (remote == SupportedRemoteId.X6)
            {
                _suppressFirstX6HidShort = false;
            }
            BeginClosing(commands);
        }

        Emit(commands);
    }

    public void TargetRecordingChanged(bool recording)
    {
        List<VoiceSessionCommand> commands = [];
        lock (_gate)
        {
            _targetRecording = recording;
            if (recording && _phase == VoiceSessionPhase.Opening)
            {
                _phase = VoiceSessionPhase.Open;
            }
            else if (!recording && _phase == VoiceSessionPhase.Closing)
            {
                CompleteClosed();
            }
            else if (!recording && _phase == VoiceSessionPhase.Opening)
            {
                // The toggle key was delivered but the target never opened its
                // recording session. Return the remote to idle instead of
                // leaving a persistent stream that inverts the next press.
                CompleteClosed();
                commands.Add(new VoiceSessionCommand.CloseAllRemotes());
            }
            else if (!recording && _phase == VoiceSessionPhase.Open)
            {
                CompleteClosed();
                commands.Add(new VoiceSessionCommand.CloseAllRemotes());
            }
        }

        Emit(commands);
    }

    private void BeginClosing(List<VoiceSessionCommand> commands)
    {
        if (_phase is not (VoiceSessionPhase.Opening or VoiceSessionPhase.Open))
        {
            return;
        }

        _phase = VoiceSessionPhase.Closing;
        _generation++;
        _physicalGestures.Clear();
        commands.Add(new VoiceSessionCommand.TriggerTap(OpensRecognition: false));
        commands.Add(new VoiceSessionCommand.CloseAllRemotes());
    }

    private void CompleteClosed()
    {
        _phase = VoiceSessionPhase.Closed;
        _generation++;
        _streamingRemotes.Clear();
        _physicalGestures.Clear();
        _suppressFirstX6HidShort = false;
    }

    private void Emit(IEnumerable<VoiceSessionCommand> commands)
    {
        foreach (var command in commands)
        {
            CommandIssued?.Invoke(command);
        }
    }
}
