namespace RemoteMic.Core.Mapping;

public enum SupportedRemoteId
{
    Chromecast,
    X6,
}

public enum RemoteGesture
{
    Single,
    Double,
    Long,
}

public enum RemoteMappingTarget
{
    Disabled,
    VoiceInput,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    ScrollUp,
    ScrollDown,
    FocusInput,
    Return,
    Escape,
    DeleteBackward,
    Tab,
    Space,
    Home,
    End,
    PageUp,
    PageDown,
    VolumeUp,
    VolumeDown,
    Mute,
    PlayPause,
    ShowDesktop,
    Search,
    Copy,
    Paste,
    Undo,
    CustomShortcut,
    InsertText,
    Sequence,
    OpenApplication,
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
}

public sealed record RemoteShortcut(ushort VirtualKey, KeyModifiers Modifiers, string Label);
public sealed record RemoteApplication(string Target, string Label);

public sealed record RemoteButtonDefinition(
    string Id,
    string ChineseTitle,
    string EnglishTitle,
    string Symbol,
    RemoteMappingTarget DefaultTarget,
    bool VoiceControlled = false,
    bool Remappable = true);

public readonly record struct RemoteMappingKey(
    SupportedRemoteId Remote,
    string ButtonId,
    RemoteGesture Gesture);

public sealed record RemoteMappingBinding(
    RemoteMappingTarget Target,
    RemoteShortcut? Shortcut = null,
    RemoteApplication? Application = null,
    string? Text = null,
    RemoteActionSequence? Sequence = null);

public static class RemoteProfiles
{
    public const ushort ChromecastVendorId = 0x18D1;
    public const ushort ChromecastProductId = 0x9450;
    public const ushort X6VendorId = 0x1D5A;
    public const ushort X6ProductId = 0xC081;

    public static IReadOnlyList<RemoteButtonDefinition> ChromecastButtons { get; } =
    [
        new("03", "方向上", "Up", "arrow.up", RemoteMappingTarget.ArrowUp),
        new("04", "方向下", "Down", "arrow.down", RemoteMappingTarget.ArrowDown),
        new("05", "方向左", "Left", "arrow.left", RemoteMappingTarget.ArrowLeft),
        new("06", "方向右", "Right", "arrow.right", RemoteMappingTarget.ArrowRight),
        new("07", "确认", "Select", "circle.inset.filled", RemoteMappingTarget.Return),
        new("0B", "返回", "Back", "chevron.backward", RemoteMappingTarget.Escape),
        new("0A", "Home", "Home", "house", RemoteMappingTarget.ShowDesktop),
        new("0E", "YouTube", "YouTube", "play.rectangle", RemoteMappingTarget.Disabled),
        new("voice", "语音", "Voice", "mic", RemoteMappingTarget.VoiceInput, true),
        new("08", "静音", "Mute", "speaker.slash", RemoteMappingTarget.Mute),
        new("0F", "Netflix", "Netflix", "n.square", RemoteMappingTarget.Disabled),
        new("01", "电源", "Power", "power", RemoteMappingTarget.Disabled),
        new("11", "信源", "Input", "rectangle.on.rectangle", RemoteMappingTarget.Disabled),
        new("0C", "音量＋", "Volume Up", "speaker.plus", RemoteMappingTarget.VolumeUp),
        new("0D", "音量－", "Volume Down", "speaker.minus", RemoteMappingTarget.VolumeDown),
    ];

    public static IReadOnlyList<RemoteButtonDefinition> X6Buttons { get; } =
    [
        new("mouseMode", "鼠标模式", "Mouse Mode", "cursorarrow.motionlines", RemoteMappingTarget.Disabled, Remappable: false),
        new("k2A", "Delete", "Delete", "delete.left", RemoteMappingTarget.DeleteBackward),
        new("cE2", "静音", "Mute", "speaker.slash", RemoteMappingTarget.Mute),
        new("c224", "返回", "Back", "chevron.backward", RemoteMappingTarget.Escape),
        new("k65", "菜单", "Menu", "line.3.horizontal", RemoteMappingTarget.Disabled),
        new("c196", "浏览器/搜索", "Browser / Search", "magnifyingglass", RemoteMappingTarget.Search),
        new("k52", "方向上", "Up", "arrow.up", RemoteMappingTarget.ArrowUp),
        new("k51", "方向下", "Down", "arrow.down", RemoteMappingTarget.ArrowDown),
        new("k50", "方向左", "Left", "arrow.left", RemoteMappingTarget.ArrowLeft),
        new("k4F", "方向右", "Right", "arrow.right", RemoteMappingTarget.ArrowRight),
        new("k28", "OK", "OK", "circle.inset.filled", RemoteMappingTarget.Return),
        new("k4B", "PG+", "PG+", "arrow.up.to.line", RemoteMappingTarget.PageUp),
        new("k4E", "PG−", "PG−", "arrow.down.to.line", RemoteMappingTarget.PageDown),
        new("voice", "语音", "Voice", "mic", RemoteMappingTarget.VoiceInput, true),
        new("cE9", "音量＋", "Volume Up", "speaker.plus", RemoteMappingTarget.VolumeUp),
        new("cEA", "音量－", "Volume Down", "speaker.minus", RemoteMappingTarget.VolumeDown),
        new("s01", "电源", "Power", "power", RemoteMappingTarget.Disabled),
    ];

    public static IReadOnlyList<RemoteButtonDefinition> ButtonsFor(SupportedRemoteId remote) =>
        remote == SupportedRemoteId.Chromecast ? ChromecastButtons : X6Buttons;
}

public static class RemoteMappingDefaults
{
    public static IReadOnlyDictionary<RemoteMappingKey, RemoteMappingBinding> CreateWindows()
    {
        var result = new Dictionary<RemoteMappingKey, RemoteMappingBinding>();
        foreach (var remote in Enum.GetValues<SupportedRemoteId>())
        {
            foreach (var button in RemoteProfiles.ButtonsFor(remote))
            {
                result[new(remote, button.Id, RemoteGesture.Single)] = new(button.DefaultTarget);
                result[new(remote, button.Id, RemoteGesture.Double)] = new(RemoteMappingTarget.Disabled);
                result[new(remote, button.Id, RemoteGesture.Long)] = new(RemoteMappingTarget.Disabled);
            }
        }

        void Set(string id, RemoteGesture gesture, RemoteMappingBinding binding) =>
            result[new(SupportedRemoteId.Chromecast, id, gesture)] = binding;

        Set("03", RemoteGesture.Single, new(RemoteMappingTarget.ScrollUp));
        Set("04", RemoteGesture.Single, new(RemoteMappingTarget.ScrollDown));
        Set("05", RemoteGesture.Single, new(
            RemoteMappingTarget.CustomShortcut,
            new RemoteShortcut(0x25, KeyModifiers.Control | KeyModifiers.Windows, "Ctrl+Win+Left")));
        Set("05", RemoteGesture.Double, new(RemoteMappingTarget.ArrowLeft));
        Set("06", RemoteGesture.Single, new(
            RemoteMappingTarget.CustomShortcut,
            new RemoteShortcut(0x27, KeyModifiers.Control | KeyModifiers.Windows, "Ctrl+Win+Right")));
        Set("06", RemoteGesture.Double, new(RemoteMappingTarget.ArrowRight));
        Set("07", RemoteGesture.Single, new(RemoteMappingTarget.Return));
        Set("07", RemoteGesture.Long, new(RemoteMappingTarget.Space));
        Set("0B", RemoteGesture.Single, new(RemoteMappingTarget.DeleteBackward));
        Set("0B", RemoteGesture.Long, new(RemoteMappingTarget.FocusInput));
        Set("01", RemoteGesture.Single, new(
            RemoteMappingTarget.OpenApplication, Application: new("trae.exe", "TRAE")));
        Set("0E", RemoteGesture.Single, new(
            RemoteMappingTarget.OpenApplication, Application: new("chrome.exe", "Google Chrome")));
        Set("0F", RemoteGesture.Single, new(
            RemoteMappingTarget.OpenApplication, Application: new("ChatGPT.exe", "ChatGPT")));
        Set("11", RemoteGesture.Single, new(
            RemoteMappingTarget.OpenApplication, Application: new("RemoteMic.exe", "RemoteMic")));
        return result;
    }
}
