using System.Collections.ObjectModel;
using System.Windows.Data;
using RemoteMic.Core.Mapping;
using RemoteMic.Windows.Properties;

namespace RemoteMic.Windows.ViewModels;

public enum ConsolePage
{
    Audio,
    Mapping,
}

public enum StatusTone
{
    Good,
    Warning,
    Error,
}

public sealed class RemoteMicViewModel : ObservableObject
{
    private ConsolePage _page;
    private RemoteDeviceViewModel _selectedRemote;
    private bool _localMuted;
    private bool _remoteMuted;
    private bool _localSolo;
    private bool _remoteSolo;
    private bool _captureAlwaysOn;
    private string _voiceTrigger = "Right Ctrl";
    private double _localLevel;
    private double _remoteLevel;
    private double _outputLevel;
    private string _localLevelText = "−∞";
    private string _remoteLevelText = "−∞";
    private string _outputLevelText = "−∞";
    private string _localState = App.Text("OnDemandStandby");
    private string _remoteState = App.Text("Disconnected");
    private string _outputState = App.Text("WaitingAudio");
    private string _connectionSummary = App.Text("Connecting");
    private string _outputEndpointName = "RemoteMic 2ch";
    private bool _localCaptureActive;
    private bool _isStreaming;

    public RemoteMicViewModel()
    {
        MappingOptions = new ObservableCollection<MappingOptionViewModel>(
        [
            new("disabled", "Disabled"),
            new("doubaoVoice", "DoubaoVoice"),
            new("scrollUp", "ScrollUp"),
            new("scrollDown", "ScrollDown"),
            new("arrowUp", "Up"),
            new("arrowDown", "Down"),
            new("arrowLeft", "Left"),
            new("arrowRight", "Right"),
            new("return", "Return"),
            new("escape", "Escape"),
            new("delete", "Delete"),
            new("space", "Space"),
            new("showDesktop", "ShowDesktop"),
            new("mute", "SystemMute"),
            new("volumeUp", "VolumeUp"),
            new("volumeDown", "VolumeDown"),
            new("pageUp", "PageUpAction"),
            new("pageDown", "PageDownAction"),
            new("search", "SearchAction"),
            new("focusInput", "FocusInput"),
            new("openApp", "OpenApp"),
        ]);

        TriggerKeys = new ObservableCollection<string>(
            ["Right Ctrl", "Left Ctrl", "Right Alt", "Left Alt", "Shift", "Win"]);
        Devices = new ObservableCollection<RemoteDeviceViewModel>
        {
            CreateChromecast(),
            CreateX6(),
        };
        _selectedRemote = Devices[0];
        LoadSettings();
        WireSettingsPersistence();

        StatusRows = new ObservableCollection<StatusRowViewModel>
        {
            new("InputDeviceStatus", "NotRecording", StatusTone.Warning),
            new("AudioDriverStatus", "Checking", StatusTone.Warning, "OpenSound"),
            new("RemoteStatus", "HidBleDisconnected", StatusTone.Warning, "BluetoothSettings"),
            new("MicrophoneStatus", "Checking", StatusTone.Warning),
            new("BluetoothStatus", "Checking", StatusTone.Warning),
            new("HidStatus", "ObserveOnly", StatusTone.Warning),
        };
    }

    public ObservableCollection<MappingOptionViewModel> MappingOptions { get; }
    public ObservableCollection<string> TriggerKeys { get; }
    public ObservableCollection<RemoteDeviceViewModel> Devices { get; }
    public ObservableCollection<StatusRowViewModel> StatusRows { get; }

    public ConsolePage Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(IsAudioPage));
                Raise(nameof(IsMappingPage));
            }
        }
    }

    public bool IsAudioPage => Page == ConsolePage.Audio;
    public bool IsMappingPage => Page == ConsolePage.Mapping;

    public RemoteDeviceViewModel SelectedRemote
    {
        get => _selectedRemote;
        set
        {
            if (Set(ref _selectedRemote, value))
            {
                Raise(nameof(MappingEnabled));
            }
        }
    }

    public bool MappingEnabled
    {
        get => SelectedRemote.MappingEnabled;
        set
        {
            if (SelectedRemote.MappingEnabled == value)
            {
                return;
            }

            SelectedRemote.MappingEnabled = value;
            SettingsStore.Write($"MappingEnabled.{SelectedRemote.Id}", value ? "1" : "0");
            Raise();
        }
    }

    public bool LocalMuted
    {
        get => _localMuted;
        set => Set(ref _localMuted, value);
    }

    public bool RemoteMuted
    {
        get => _remoteMuted;
        set => Set(ref _remoteMuted, value);
    }

    public bool LocalSolo
    {
        get => _localSolo;
        set
        {
            if (Set(ref _localSolo, value) && value)
            {
                RemoteSolo = false;
            }
        }
    }

    public bool RemoteSolo
    {
        get => _remoteSolo;
        set
        {
            if (Set(ref _remoteSolo, value) && value)
            {
                LocalSolo = false;
            }
        }
    }

    public bool CaptureAlwaysOn
    {
        get => _captureAlwaysOn;
        set => Set(ref _captureAlwaysOn, value);
    }

    public string VoiceTrigger
    {
        get => _voiceTrigger;
        set
        {
            if (Set(ref _voiceTrigger, value))
            {
                SettingsStore.Write("VoiceTrigger", value);
            }
        }
    }

    public double LocalLevel
    {
        get => _localLevel;
        set => Set(ref _localLevel, value);
    }

    public double RemoteLevel
    {
        get => _remoteLevel;
        set => Set(ref _remoteLevel, value);
    }

    public double OutputLevel
    {
        get => _outputLevel;
        set => Set(ref _outputLevel, value);
    }

    public string LocalLevelText
    {
        get => _localLevelText;
        set => Set(ref _localLevelText, value);
    }

    public string RemoteLevelText
    {
        get => _remoteLevelText;
        set => Set(ref _remoteLevelText, value);
    }

    public string OutputLevelText
    {
        get => _outputLevelText;
        set => Set(ref _outputLevelText, value);
    }

    public string LocalState
    {
        get => _localState;
        set => Set(ref _localState, value);
    }

    public string RemoteState
    {
        get => _remoteState;
        set => Set(ref _remoteState, value);
    }

    public string OutputState
    {
        get => _outputState;
        set => Set(ref _outputState, value);
    }

    public string ConnectionSummary
    {
        get => _connectionSummary;
        set => Set(ref _connectionSummary, value);
    }

    public string OutputEndpointName
    {
        get => _outputEndpointName;
        set => Set(ref _outputEndpointName, value);
    }

    public void SetLevel(bool remote, double db)
    {
        var normalized = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        var text = db <= -59.9 ? "−∞" : $"{db:0} dB";
        if (remote)
        {
            RemoteLevel = normalized;
            RemoteLevelText = text;
        }
        else
        {
            LocalLevel = normalized;
            LocalLevelText = text;
        }
    }

    public void SetOutputLevel(double db)
    {
        OutputLevel = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        OutputLevelText = db <= -59.9 ? "−∞" : $"{db:0} dB";
    }

    public bool IsMappingEnabled(string remoteId) =>
        Devices.FirstOrDefault(device => device.Id == remoteId)?.MappingEnabled == true;

    public void SetLocalCaptureActive(bool active)
    {
        _localCaptureActive = active;
        LocalState = App.Text(active ? "SoundPresent" : "OnDemandStandby");
        if (!active)
        {
            SetLevel(remote: false, -60);
        }
    }

    public void SetRemoteConnected(string id, bool connected)
    {
        var device = Devices.FirstOrDefault(item => item.Id == id);
        if (device is not null)
        {
            device.Connected = connected;
        }

        var anyConnected = Devices.Any(item => item.Connected);
        ConnectionSummary = App.Text(_isStreaming ? "StopRecording" : anyConnected ? "Running" : "Connecting");
        RemoteState = App.Text(_isStreaming ? "SoundPresent" : anyConnected ? "Connected" : "Disconnected");
    }

    public void SetStreaming(bool streaming)
    {
        _isStreaming = streaming;
        var anyConnected = Devices.Any(item => item.Connected);
        ConnectionSummary = App.Text(streaming ? "StopRecording" : anyConnected ? "Running" : "Connecting");
        RemoteState = App.Text(streaming ? "SoundPresent" : anyConnected ? "Connected" : "Disconnected");
        OutputState = App.Text(streaming ? "SoundPresent" : "WaitingAudio");
        StatusRows[0].Update(streaming ? "Recording" : "NotRecording", streaming ? StatusTone.Good : StatusTone.Warning);
        if (!streaming)
        {
            SetLevel(remote: true, -60);
            SetOutputLevel(-60);
        }
    }

    public void UpdateStatus(int index, string valueKey, StatusTone tone) =>
        StatusRows[index].Update(valueKey, tone);

    public void ResetMappings()
    {
        foreach (var row in SelectedRemote.Buttons)
        {
            row.Reset();
            foreach (var gesture in Enum.GetValues<RemoteGesture>())
            {
                var setting = $"Application.{SelectedRemote.Id}.{row.ButtonId}.{gesture}";
                var target = row.GetApplicationTarget(gesture);
                if (target is null)
                {
                    SettingsStore.Delete(setting);
                }
                else
                {
                    SettingsStore.Write(setting, target);
                }
            }
        }
    }

    public void RefreshLocalizedText()
    {
        foreach (var option in MappingOptions)
        {
            option.Raise(nameof(MappingOptionViewModel.DisplayName));
        }
        foreach (var status in StatusRows)
        {
            status.Refresh();
        }
        foreach (var device in Devices)
        {
            device.Refresh();
        }
        CollectionViewSource.GetDefaultView(MappingOptions).Refresh();
        RefreshRuntimeText();
    }

    private static RemoteDeviceViewModel CreateChromecast()
    {
        return new RemoteDeviceViewModel(
            "chromecast",
            "Chromecast Voice Remote",
            "18D1 · 9450",
            "pack://application:,,,/Assets/chromecast-voice-remote.png",
            false,
            new ObservableCollection<MappingRowViewModel>
            {
                new("↑", "Up", "scrollUp", buttonId: "03"),
                new("↓", "Down", "scrollDown", buttonId: "04"),
                new("←", "Left", "arrowLeft", "arrowLeft", buttonId: "05"),
                new("→", "Right", "arrowRight", "arrowRight", buttonId: "06"),
                new("●", "Select", "return", longKey: "space", buttonId: "07"),
                new("‹", "Back", "delete", longKey: "focusInput", buttonId: "0B"),
                new("⌂", "Home", "showDesktop", buttonId: "0A"),
                new("▶", "YouTube", "openApp", buttonId: "0E", applicationTarget: "chrome.exe"),
                new("●", "Voice", "doubaoVoice", remappable: false, buttonId: "voice"),
                new("◖", "Mute", "mute", buttonId: "08"),
                new("N", "Netflix", "openApp", buttonId: "0F", applicationTarget: "ChatGPT.exe"),
                new("⏻", "Power", "openApp", buttonId: "01", applicationTarget: "trae.exe"),
                new("▣", "Input", "openApp", buttonId: "11", applicationTarget: "RemoteMic.exe"),
                new("＋", "VolumeUp", "volumeUp", buttonId: "0C"),
                new("−", "VolumeDown", "volumeDown", buttonId: "0D"),
            });
    }

    private static RemoteDeviceViewModel CreateX6()
    {
        return new RemoteDeviceViewModel(
            "x6",
            "X6 Remote",
            "1D5A · C081",
            "pack://application:,,,/Assets/x6-remote.png",
            false,
            new ObservableCollection<MappingRowViewModel>
            {
                new("⌫", "Delete", "delete", buttonId: "k2A"),
                new("◖", "Mute", "mute", buttonId: "cE2"),
                new("‹", "Back", "escape", buttonId: "c224"),
                new("☰", "Menu", "disabled", buttonId: "k65"),
                new("⌕", "Search", "search", buttonId: "c196"),
                new("↑", "Up", "arrowUp", buttonId: "k52"),
                new("↓", "Down", "arrowDown", buttonId: "k51"),
                new("←", "Left", "arrowLeft", buttonId: "k50"),
                new("→", "Right", "arrowRight", buttonId: "k4F"),
                new("●", "OK", "return", buttonId: "k28"),
                new("⇞", "Page Up", "pageUp", buttonId: "k4B"),
                new("⇟", "Page Down", "pageDown", buttonId: "k4E"),
                new("●", "Voice", "doubaoVoice", remappable: false, buttonId: "voice"),
                new("＋", "VolumeUp", "volumeUp", buttonId: "cE9"),
                new("−", "VolumeDown", "volumeDown", buttonId: "cEA"),
                new("⏻", "Power", "disabled", buttonId: "s01"),
            });
    }

    private void LoadSettings()
    {
        var savedTrigger = SettingsStore.Read("VoiceTrigger");
        if (savedTrigger is "Alt" or "Right Alt" or "Ctrl")
        {
            _voiceTrigger = "Right Ctrl";
            SettingsStore.Write("VoiceTrigger", _voiceTrigger);
        }
        else if (savedTrigger is not null && TriggerKeys.Contains(savedTrigger))
        {
            _voiceTrigger = savedTrigger;
        }

        var knownMappings = MappingOptions.Select(option => option.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var device in Devices)
        {
            device.MappingEnabled = SettingsStore.Read($"MappingEnabled.{device.Id}") != "0";
            foreach (var row in device.Buttons)
            {
                LoadMapping(row, nameof(row.SingleKey));
                LoadMapping(row, nameof(row.DoubleKey));
                LoadMapping(row, nameof(row.LongKey));
                foreach (var gesture in Enum.GetValues<RemoteGesture>())
                {
                    var target = SettingsStore.Read(
                        $"Application.{device.Id}.{row.ButtonId}.{gesture}");
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        row.SetApplicationTarget(gesture, target);
                    }
                }

                void LoadMapping(MappingRowViewModel target, string gesture)
                {
                    var value = SettingsStore.Read($"Mapping.{device.Id}.{target.ButtonId}.{gesture}");
                    if (value is null || !knownMappings.Contains(value))
                    {
                        return;
                    }

                    switch (gesture)
                    {
                        case nameof(target.SingleKey):
                            target.SingleKey = value;
                            break;
                        case nameof(target.DoubleKey):
                            target.DoubleKey = value;
                            break;
                        case nameof(target.LongKey):
                            target.LongKey = value;
                            break;
                    }
                }
            }
        }
    }

    private void WireSettingsPersistence()
    {
        foreach (var device in Devices)
        {
            foreach (var row in device.Buttons)
            {
                row.PropertyChanged += (_, args) =>
                {
                    var value = args.PropertyName switch
                    {
                        nameof(row.SingleKey) => row.SingleKey,
                        nameof(row.DoubleKey) => row.DoubleKey,
                        nameof(row.LongKey) => row.LongKey,
                        _ => null,
                    };
                    if (value is not null)
                    {
                        SettingsStore.Write(
                            $"Mapping.{device.Id}.{row.ButtonId}.{args.PropertyName}",
                            value);
                    }
                };
            }
        }
    }

    private void RefreshRuntimeText()
    {
        var anyConnected = Devices.Any(item => item.Connected);
        LocalState = App.Text(_localCaptureActive ? "SoundPresent" : "OnDemandStandby");
        ConnectionSummary = App.Text(_isStreaming ? "StopRecording" : anyConnected ? "Running" : "Connecting");
        RemoteState = App.Text(_isStreaming ? "SoundPresent" : anyConnected ? "Connected" : "Disconnected");
        OutputState = App.Text(_isStreaming ? "SoundPresent" : "WaitingAudio");
    }

    public void SetApplicationTarget(
        string remoteId,
        MappingRowViewModel row,
        RemoteGesture gesture,
        string target)
    {
        row.SetApplicationTarget(gesture, target);
        SettingsStore.Write($"Application.{remoteId}.{row.ButtonId}.{gesture}", target);
    }
}

public sealed class MappingOptionViewModel(string key, string resourceKey) : ObservableObject
{
    public string Key { get; } = key;
    public string DisplayName => App.Text(resourceKey);
}

public sealed class RemoteDeviceViewModel(
    string id,
    string title,
    string signature,
    string imageUri,
    bool connected,
    ObservableCollection<MappingRowViewModel> buttons) : ObservableObject
{
    private bool _connected = connected;
    private bool _mappingEnabled = true;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Signature { get; } = signature;
    public string ImageUri { get; } = imageUri;
    public ObservableCollection<MappingRowViewModel> Buttons { get; } = buttons;

    public bool Connected
    {
        get => _connected;
        set
        {
            if (Set(ref _connected, value))
            {
                Raise(nameof(ConnectionText));
            }
        }
    }

    public bool MappingEnabled
    {
        get => _mappingEnabled;
        set => Set(ref _mappingEnabled, value);
    }

    public string ConnectionText => App.Text(Connected ? "Connected" : "Disconnected");
    public string MappableButtonCountText => string.Format(
        App.Text("MappableButtonsFormat"),
        Buttons.Count(button => button.Remappable));

    public void Refresh()
    {
        Raise(nameof(ConnectionText));
        Raise(nameof(MappableButtonCountText));
        foreach (var button in Buttons)
        {
            button.Refresh();
        }
    }
}

public sealed class MappingRowViewModel : ObservableObject
{
    private string _singleKey;
    private string _doubleKey;
    private string _longKey;
    private readonly Dictionary<RemoteGesture, string> _applicationTargets = [];
    private readonly Dictionary<RemoteGesture, string> _defaultApplicationTargets = [];

    public MappingRowViewModel(
        string symbol,
        string titleKey,
        string singleKey,
        string doubleKey = "disabled",
        string longKey = "disabled",
        bool remappable = true,
        string? buttonId = null,
        string? applicationTarget = null)
    {
        ButtonId = buttonId ?? titleKey;
        Symbol = symbol;
        TitleKey = titleKey;
        DefaultSingleKey = singleKey;
        DefaultDoubleKey = doubleKey;
        DefaultLongKey = longKey;
        _singleKey = singleKey;
        _doubleKey = doubleKey;
        _longKey = longKey;
        Remappable = remappable;
        if (!string.IsNullOrWhiteSpace(applicationTarget))
        {
            _applicationTargets[RemoteGesture.Single] = applicationTarget;
            _defaultApplicationTargets[RemoteGesture.Single] = applicationTarget;
        }
    }

    public string Symbol { get; }
    public string ButtonId { get; }
    public string TitleKey { get; }
    public string Title => App.Text(TitleKey);
    public bool Remappable { get; }
    public string DefaultSingleKey { get; }
    public string DefaultDoubleKey { get; }
    public string DefaultLongKey { get; }

    public string SingleKey
    {
        get => _singleKey;
        set => Set(ref _singleKey, value);
    }

    public string DoubleKey
    {
        get => _doubleKey;
        set => Set(ref _doubleKey, value);
    }

    public string LongKey
    {
        get => _longKey;
        set => Set(ref _longKey, value);
    }

    public void Reset()
    {
        SingleKey = DefaultSingleKey;
        DoubleKey = DefaultDoubleKey;
        LongKey = DefaultLongKey;
        _applicationTargets.Clear();
        foreach (var target in _defaultApplicationTargets)
        {
            _applicationTargets[target.Key] = target.Value;
        }
    }

    public string? GetApplicationTarget(RemoteGesture gesture) =>
        _applicationTargets.GetValueOrDefault(gesture);

    public void SetApplicationTarget(RemoteGesture gesture, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        _applicationTargets[gesture] = target;
    }

    public void SetMapping(RemoteGesture gesture, string mapping)
    {
        switch (gesture)
        {
            case RemoteGesture.Single:
                SingleKey = mapping;
                break;
            case RemoteGesture.Double:
                DoubleKey = mapping;
                break;
            case RemoteGesture.Long:
                LongKey = mapping;
                break;
        }
    }

    public void Refresh() => Raise(nameof(Title));
}

public sealed class StatusRowViewModel(
    string titleKey,
    string valueKey,
    StatusTone tone,
    string? actionKey = null) : ObservableObject
{
    private string _valueKey = valueKey;
    private StatusTone _tone = tone;

    public string Title => App.Text(titleKey);
    public string Value => App.Text(_valueKey);
    public string? Action => actionKey is null ? null : App.Text(actionKey);
    public StatusTone Tone
    {
        get => _tone;
        private set => Set(ref _tone, value);
    }
    public bool HasAction => actionKey is not null;

    public void Update(string newValueKey, StatusTone newTone)
    {
        if (_valueKey != newValueKey)
        {
            _valueKey = newValueKey;
            Raise(nameof(Value));
        }
        Tone = newTone;
    }

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Value));
        Raise(nameof(Action));
    }
}
