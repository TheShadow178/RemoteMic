using RemoteMic.Core.Mapping;

namespace RemoteMic.Core.Input;

public readonly record struct RemoteGestureEvent(string ButtonId, RemoteGesture Gesture, bool IsDown);

/// <summary>
/// Deterministic button gesture state machine. Call <see cref="Advance"/> from
/// the platform timer at the next <see cref="NextDeadline"/>; tests can advance
/// a synthetic clock without sleeping.
/// </summary>
public sealed class RemoteGestureRecognizer
{
    public static readonly TimeSpan DoubleTapInterval = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(600);

    private sealed record ActivePress(string ButtonId, DateTimeOffset LongDeadline)
    {
        public bool LongTriggered { get; set; }
    }

    private sealed record PendingSingle(string ButtonId, DateTimeOffset Deadline);

    private ActivePress? _activePress;
    private (string ButtonId, DateTimeOffset At)? _lastRelease;
    private readonly Dictionary<string, PendingSingle> _pendingSingles = new(StringComparer.Ordinal);

    public Func<string, bool> IsGestureEnabled { get; set; } = _ => false;
    public Func<string, bool> IsDoubleEnabled { get; set; } = _ => false;
    public Func<string, bool> IsLongEnabled { get; set; } = _ => false;

    public event Action<RemoteGestureEvent>? Gesture;

    public DateTimeOffset? NextDeadline
    {
        get
        {
            DateTimeOffset? result = _activePress is { LongTriggered: false } press &&
                                     IsLongEnabled(press.ButtonId)
                ? press.LongDeadline
                : null;
            foreach (var pending in _pendingSingles.Values)
            {
                if (result is null || pending.Deadline < result.Value) result = pending.Deadline;
            }

            return result;
        }
    }

    public void Handle(string buttonId, bool isDown, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);
        Advance(now);

        var doubleEnabled = IsDoubleEnabled(buttonId);
        var longEnabled = IsLongEnabled(buttonId);
        if (!IsGestureEnabled(buttonId) || (!doubleEnabled && !longEnabled))
        {
            Emit(buttonId, RemoteGesture.Single, isDown);
            return;
        }

        if (isDown)
        {
            BeginPress(buttonId, now);
        }
        else
        {
            EndPress(buttonId, now, doubleEnabled);
        }
    }

    public void Advance(DateTimeOffset now)
    {
        if (_activePress is { LongTriggered: false } press &&
            IsLongEnabled(press.ButtonId) && now >= press.LongDeadline)
        {
            press.LongTriggered = true;
            Emit(press.ButtonId, RemoteGesture.Long, true);
        }

        var ready = _pendingSingles.Values
            .Where(item => item.Deadline <= now)
            .OrderBy(item => item.Deadline)
            .ToArray();
        foreach (var pending in ready)
        {
            if (!_pendingSingles.Remove(pending.ButtonId)) continue;
            if (_lastRelease is { } last && last.ButtonId == pending.ButtonId)
                _lastRelease = null;
            EmitPulse(pending.ButtonId, RemoteGesture.Single);
        }
    }

    public void CancelPending()
    {
        if (_activePress is { LongTriggered: true } press)
            Emit(press.ButtonId, RemoteGesture.Long, false);
        _activePress = null;
        _lastRelease = null;
        _pendingSingles.Clear();
    }

    private void BeginPress(string buttonId, DateTimeOffset now)
    {
        if (_activePress is { LongTriggered: true } previous)
            Emit(previous.ButtonId, RemoteGesture.Long, false);
        _activePress = new ActivePress(buttonId, now + LongPressDelay);
    }

    private void EndPress(string buttonId, DateTimeOffset now, bool doubleEnabled)
    {
        if (_activePress is not { } press || press.ButtonId != buttonId) return;
        _activePress = null;

        if (press.LongTriggered)
        {
            _lastRelease = null;
            Emit(buttonId, RemoteGesture.Long, false);
            return;
        }

        if (doubleEnabled && _lastRelease is { } last && last.ButtonId == buttonId &&
            now - last.At < DoubleTapInterval)
        {
            _lastRelease = null;
            _pendingSingles.Remove(buttonId);
            EmitPulse(buttonId, RemoteGesture.Double);
            return;
        }

        if (doubleEnabled)
        {
            _lastRelease = (buttonId, now);
            _pendingSingles[buttonId] = new PendingSingle(buttonId, now + DoubleTapInterval);
        }
        else
        {
            _lastRelease = null;
            EmitPulse(buttonId, RemoteGesture.Single);
        }
    }

    private void EmitPulse(string buttonId, RemoteGesture gesture)
    {
        Emit(buttonId, gesture, true);
        Emit(buttonId, gesture, false);
    }

    private void Emit(string buttonId, RemoteGesture gesture, bool isDown) =>
        Gesture?.Invoke(new RemoteGestureEvent(buttonId, gesture, isDown));
}
