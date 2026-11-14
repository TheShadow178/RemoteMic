namespace RemoteMic.Core.Input;

/// <summary>
/// Normalizes the X6 keyboard (report 1), Consumer Control (report 2) and
/// System Control (report 3) collections into the model-scoped button IDs used
/// by RemoteMic mappings. Mouse report 4 remains owned by Windows.
/// </summary>
public sealed class X6HidReportNormalizer
{
    private static readonly HashSet<byte> KnownKeyboardUsages =
    [
        0x2A, 0x65, 0x52, 0x51, 0x50, 0x4F, 0x28, 0x4B, 0x4E,
    ];

    private readonly HashSet<byte> _keyboardUsages = [];
    private ushort _consumerUsage;
    private byte _systemUsage;

    public IReadOnlyList<HidButtonEvent> Normalize(ReadOnlySpan<byte> report)
    {
        if (report.IsEmpty)
        {
            return [];
        }

        var reportId = report[0];
        var payload = report[1..];
        return reportId switch
        {
            0x01 => NormalizeKeyboard(payload),
            0x02 => NormalizeConsumer(payload),
            0x03 => NormalizeSystem(payload),
            _ => [],
        };
    }

    public IReadOnlyList<HidButtonEvent> Reset()
    {
        var events = new List<HidButtonEvent>();
        events.AddRange(_keyboardUsages.Select(usage =>
            new HidButtonEvent($"k{usage:X2}", false, usage)));
        _keyboardUsages.Clear();
        if (_consumerUsage != 0 && _consumerUsage != 0x0221)
        {
            events.Add(new HidButtonEvent($"c{_consumerUsage:X}", false, _consumerUsage));
        }
        _consumerUsage = 0;
        if (_systemUsage != 0)
        {
            events.Add(new HidButtonEvent($"s{_systemUsage:X2}", false, _systemUsage));
            _systemUsage = 0;
        }
        return events;
    }

    private IReadOnlyList<HidButtonEvent> NormalizeKeyboard(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return [];
        }

        var next = payload[2..]
            .ToArray()
            .Where(usage => usage != 0 && KnownKeyboardUsages.Contains(usage))
            .ToHashSet();
        var events = new List<HidButtonEvent>();
        foreach (var released in _keyboardUsages.Except(next).Order())
        {
            events.Add(new HidButtonEvent($"k{released:X2}", false, released));
        }
        foreach (var pressed in next.Except(_keyboardUsages).Order())
        {
            events.Add(new HidButtonEvent($"k{pressed:X2}", true, pressed));
        }

        _keyboardUsages.Clear();
        _keyboardUsages.UnionWith(next);
        return events;
    }

    private IReadOnlyList<HidButtonEvent> NormalizeConsumer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return [];
        }

        var usage = (ushort)(payload[0] | (payload[1] << 8));
        if (usage == _consumerUsage)
        {
            return [];
        }

        var events = new List<HidButtonEvent>(2);
        if (_consumerUsage != 0 && _consumerUsage != 0x0221)
        {
            events.Add(new HidButtonEvent($"c{_consumerUsage:X}", false, _consumerUsage));
        }
        if (usage != 0 && usage != 0x0221)
        {
            events.Add(new HidButtonEvent($"c{usage:X}", true, usage));
        }
        _consumerUsage = usage;
        return events;
    }

    private IReadOnlyList<HidButtonEvent> NormalizeSystem(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] == _systemUsage)
        {
            return [];
        }

        var usage = payload[0];
        var events = new List<HidButtonEvent>(2);
        if (_systemUsage != 0)
        {
            events.Add(new HidButtonEvent($"s{_systemUsage:X2}", false, _systemUsage));
        }
        if (usage != 0)
        {
            events.Add(new HidButtonEvent($"s{usage:X2}", true, usage));
        }
        _systemUsage = usage;
        return events;
    }
}
