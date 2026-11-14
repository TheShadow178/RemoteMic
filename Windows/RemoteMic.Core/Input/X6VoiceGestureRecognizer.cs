namespace RemoteMic.Core.Input;

/// <summary>
/// Reconstructs X6 short/long voice gestures from Consumer Search (report 2)
/// and the hold-only keyboard usage AA (report 1). The delayed release matches
/// the device's observed report transition without sleeping in tests.
/// </summary>
public sealed class X6VoiceGestureRecognizer
{
    public static readonly TimeSpan ConsumerReleaseDelay = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan LongPressConfirmation = TimeSpan.FromMilliseconds(280);

    private bool _gestureActive;
    private bool _keyboardHeld;
    private bool _longConfirmed;
    private DateTimeOffset? _pendingShortAt;
    private DateTimeOffset? _longConfirmationAt;

    public event Action? ShortPressed;
    public event Action? LongPressEnded;

    public void Handle(ReadOnlySpan<byte> report, DateTimeOffset now)
    {
        Advance(now);
        if (report.IsEmpty)
        {
            return;
        }

        switch (report[0])
        {
            case 0x02 when report.Length >= 3:
                HandleConsumer((ushort)(report[1] | (report[2] << 8)), now);
                break;
            case 0x01 when report.Length >= 3:
                HandleKeyboard(report[3..].Contains((byte)0xAA), now);
                break;
        }
    }

    public void Advance(DateTimeOffset now)
    {
        if (_keyboardHeld && !_longConfirmed &&
            _longConfirmationAt is { } longAt && now >= longAt)
        {
            _longConfirmed = true;
            _longConfirmationAt = null;
        }

        if (!_keyboardHeld && _pendingShortAt is { } shortAt && now >= shortAt)
        {
            _pendingShortAt = null;
            _gestureActive = false;
            ShortPressed?.Invoke();
        }
    }

    public void Reset()
    {
        _gestureActive = false;
        _keyboardHeld = false;
        _longConfirmed = false;
        _pendingShortAt = null;
        _longConfirmationAt = null;
    }

    private void HandleConsumer(ushort usage, DateTimeOffset now)
    {
        if (usage == 0x0221)
        {
            _gestureActive = true;
            _pendingShortAt = null;
        }
        else if (usage == 0 && _gestureActive && !_keyboardHeld)
        {
            _pendingShortAt = now + ConsumerReleaseDelay;
        }
    }

    private void HandleKeyboard(bool held, DateTimeOffset now)
    {
        if (held == _keyboardHeld)
        {
            return;
        }

        _keyboardHeld = held;
        if (held)
        {
            _gestureActive = true;
            _pendingShortAt = null;
            _longConfirmed = false;
            _longConfirmationAt = now + LongPressConfirmation;
            return;
        }

        _longConfirmationAt = null;
        if (!_gestureActive)
        {
            return;
        }

        _gestureActive = false;
        if (_longConfirmed)
        {
            _longConfirmed = false;
            LongPressEnded?.Invoke();
        }
        else
        {
            ShortPressed?.Invoke();
        }
    }
}
