namespace RemoteMic.Core.Input;

public readonly record struct HidButtonEvent(string ButtonId, bool IsDown, ushort Usage);

/// <summary>
/// Normalizes both Chromecast HOGP report layouts observed on Windows:
/// REV011b consumer reports (02 + little-endian 16-bit usage), translated
/// REV0110 descriptor reports (01 + little-endian 16-bit usage), and REV0110
/// direct reports (optional report id 01 + one-byte RemoteMic usage + zeros).
/// A zero usage is a release for the last pressed button.
/// </summary>
public sealed class ChromecastHidReportNormalizer
{
    private static readonly IReadOnlyDictionary<ushort, string> ConsumerUsageToButton =
        new Dictionary<ushort, string>
        {
            [0x019E] = "01", // Power
            [0x0189] = "11", // Input
            [0x0042] = "03", // Up
            [0x0043] = "04", // Down
            [0x0044] = "05", // Left
            [0x0045] = "06", // Right
            [0x0041] = "07", // Select
            [0x0224] = "0B", // Back
            [0x0223] = "0A", // Home
            [0x00E9] = "0C", // Volume up
            [0x00EA] = "0D", // Volume down
            [0x00E2] = "08", // Mute
            [0x0077] = "0E", // YouTube
            [0x0078] = "0F", // Netflix
        };

    private static readonly HashSet<byte> DirectUsages =
    [
        0x01, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0A, 0x0B,
        0x0C, 0x0D, 0x0E, 0x0F, 0x11,
    ];

    private HidButtonEvent? _active;

    public bool TryNormalize(ReadOnlySpan<byte> report, out HidButtonEvent buttonEvent)
    {
        buttonEvent = default;
        if (report.Length == 3 && report[0] is 0x01 or 0x02)
        {
            var usage = (ushort)(report[1] | (report[2] << 8));
            return NormalizeConsumer(usage, out buttonEvent);
        }

        if (report.Length >= 8)
        {
            var offset = report[0] == 0x01 ? 1 : 0;
            if (offset >= report.Length) return false;
            var usage = report[offset];
            if (usage != 0 && !DirectUsages.Contains(usage)) return false;
            return NormalizeDirect(usage, out buttonEvent);
        }

        return false;
    }

    public HidButtonEvent? Reset()
    {
        if (_active is not { } active) return null;
        _active = null;
        return active with { IsDown = false };
    }

    private bool NormalizeConsumer(ushort usage, out HidButtonEvent buttonEvent)
    {
        if (usage == 0) return Release(out buttonEvent);
        if (!ConsumerUsageToButton.TryGetValue(usage, out var buttonId))
        {
            buttonEvent = default;
            return false;
        }

        return Press(buttonId, usage, out buttonEvent);
    }

    private bool NormalizeDirect(byte usage, out HidButtonEvent buttonEvent)
    {
        if (usage == 0) return Release(out buttonEvent);
        return Press(usage.ToString("X2"), usage, out buttonEvent);
    }

    private bool Press(string buttonId, ushort usage, out HidButtonEvent buttonEvent)
    {
        if (_active is { IsDown: true } active && active.ButtonId == buttonId)
        {
            buttonEvent = default;
            return false;
        }

        buttonEvent = new HidButtonEvent(buttonId, true, usage);
        _active = buttonEvent;
        return true;
    }

    private bool Release(out HidButtonEvent buttonEvent)
    {
        if (_active is not { } active)
        {
            buttonEvent = default;
            return false;
        }

        buttonEvent = active with { IsDown = false };
        _active = null;
        return true;
    }
}
