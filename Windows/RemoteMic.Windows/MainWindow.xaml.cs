using Microsoft.Win32;
using RemoteMic.Core.Input;
using RemoteMic.Core.Mapping;
using RemoteMic.Core.Voice;
using RemoteMic.Platform.Windows.Audio;
using RemoteMic.Platform.Windows.Bluetooth;
using RemoteMic.Platform.Windows.Hid;
using RemoteMic.Platform.Windows.Input;
using RemoteMic.Windows.ViewModels;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Channels;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RemoteMic.Windows;

public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RemoteMic";
    private static readonly object DiagnosticLogGate = new();
    private static readonly string DiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteMic",
        "Logs",
        "runtime.log");

    private readonly RemoteMicViewModel _viewModel = new();
    private readonly AudioGraphRouter _audioRouter = new();
    private readonly CaptureSessionActivityMonitor _targetRecordingMonitor = new();
    private readonly RawInputRemoteSource _hidSource = new();
    private readonly HidFilterRemoteSource _filterHidSource = new();
    private readonly ChromecastHidReportNormalizer _chromecastReports = new();
    private readonly X6HidReportNormalizer _x6Reports = new();
    private readonly X6VoiceGestureRecognizer _x6VoiceGestures = new();
    private readonly RemoteGestureRecognizer _gestureRecognizer = new();
    private readonly VoiceSessionCoordinator _voiceSession = new();
    private readonly DispatcherTimer _gestureTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly List<AtvvSessionController> _remoteSessions = [];
    private readonly ConcurrentDictionary<SupportedRemoteId, AtvvSessionController> _remoteSessionsById = new();
    private readonly ConcurrentDictionary<SupportedRemoteId, bool> _bleLinks = new();
    private readonly ConcurrentDictionary<SupportedRemoteId, bool> _hidLinks = new();
    private readonly HashSet<AtvvSessionController> _streamingSessions = [];
    private readonly Channel<VoiceSessionCommand> _voiceCommands = Channel.CreateUnbounded<VoiceSessionCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _voiceCommandLoop;
    private WindowsActionExecutor? _actionExecutor;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _runtimeStarted;

    public MainWindow()
    {
        ResetDiagnosticLog();
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeTray();
        UpdateTabVisuals();
        if (_runtimeStarted)
        {
            return;
        }

        _runtimeStarted = true;
        _actionExecutor = new WindowsActionExecutor(SynchronizationContext.Current);
        ConfigureInputPipeline();
        await InitializeRuntimeAsync(_lifetime.Token);
    }

    private void AudioTab_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Page = ConsolePage.Audio;
        UpdateTabVisuals();
    }

    private void MappingTab_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Page = ConsolePage.Mapping;
        UpdateTabVisuals();
    }

    private void UpdateTabVisuals()
    {
        SetButtonResource(AudioTabButton, "Background",
            _viewModel.IsAudioPage ? "SurfaceBrush" : "TrackBrush");
        SetButtonResource(MappingTabButton, "Background",
            _viewModel.IsMappingPage ? "SurfaceBrush" : "TrackBrush");
        SetButtonResource(AudioTabButton, "Foreground",
            _viewModel.IsAudioPage ? "TextBrush" : "SecondaryBrush");
        SetButtonResource(MappingTabButton, "Foreground",
            _viewModel.IsMappingPage ? "TextBrush" : "SecondaryBrush");
    }

    private void ConfigureInputPipeline()
    {
        _gestureRecognizer.IsGestureEnabled = scopedButtonId =>
            TryParseScopedButtonId(scopedButtonId, out var remoteId, out var buttonId) &&
            _viewModel.IsMappingEnabled(remoteId) && FindMappingRow(remoteId, buttonId) is not null;
        _gestureRecognizer.IsDoubleEnabled = scopedButtonId =>
            TryParseScopedButtonId(scopedButtonId, out var remoteId, out var buttonId) &&
            FindMappingRow(remoteId, buttonId)?.DoubleKey != "disabled";
        _gestureRecognizer.IsLongEnabled = scopedButtonId =>
            TryParseScopedButtonId(scopedButtonId, out var remoteId, out var buttonId) &&
            FindMappingRow(remoteId, buttonId)?.LongKey != "disabled";
        _gestureRecognizer.Gesture += OnGesture;
        _x6VoiceGestures.ShortPressed += () =>
            _voiceSession.RemoteHidShortPress(SupportedRemoteId.X6, DateTimeOffset.UtcNow);
        _x6VoiceGestures.LongPressEnded += () =>
            _voiceSession.RemoteHidLongPressEnded(SupportedRemoteId.X6);
        _voiceSession.CommandIssued += command =>
        {
            WriteDiagnostic($"VOICE command={command}");
            _voiceCommands.Writer.TryWrite(command);
        };
        _voiceCommandLoop = ProcessVoiceCommandsAsync(_lifetime.Token);
        _gestureTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            _gestureRecognizer.Advance(now);
            _x6VoiceGestures.Advance(now);
        };
        _gestureTimer.Start();

        _filterHidSource.ReportReceived += OnHidReport;
        _filterHidSource.DeviceConnectionChanged += (_, change) =>
        {
            SetRemoteLinkState(SupportedRemoteId.Chromecast, hidConnected: change.Connected);
            _viewModel.UpdateStatus(
                5,
                change.Connected ? "HidDriverActive" : "ObserveOnly",
                change.Connected ? StatusTone.Good : StatusTone.Warning);
        };
        _filterHidSource.Diagnostic += (_, message) => Debug.WriteLine(message);
        _filterHidSource.Start();

        _hidSource.ReportReceived += (sender, report) =>
        {
            if (!_filterHidSource.IsConnected)
            {
                OnHidReport(sender, report);
            }
        };
        _hidSource.DeviceConnectionChanged += (_, change) =>
        {
            var remoteId = change.Remote == WindowsRemoteKind.X6Remote
                ? SupportedRemoteId.X6
                : SupportedRemoteId.Chromecast;
            SetRemoteLinkState(remoteId, hidConnected: change.Connected);
        };
        _hidSource.Diagnostic += (_, message) => Debug.WriteLine(message);
        try
        {
            _hidSource.Start(this);
            _viewModel.UpdateStatus(5, "ObserveOnly", StatusTone.Warning);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _viewModel.UpdateStatus(5, "Unavailable", StatusTone.Error);
        }

        _audioRouter.RemoteLevelChanged += (_, db) =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                _viewModel.SetLevel(remote: true, db);
                if (!_viewModel.RemoteMuted && !_viewModel.LocalSolo)
                {
                    _viewModel.SetOutputLevel(db);
                }
            });
        _audioRouter.LocalLevelChanged += (_, db) =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                _viewModel.SetLevel(remote: false, db);
                if (!_viewModel.LocalMuted && !_viewModel.RemoteSolo &&
                    (!_audioRouter.IsRemoteActive || _viewModel.RemoteMuted || _viewModel.LocalSolo))
                {
                    _viewModel.SetOutputLevel(db);
                }
            });
        _audioRouter.CaptureActivityChanged += (_, active) =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                _viewModel.SetLocalCaptureActive(active);
                _viewModel.UpdateStatus(3, "SystemManaged", active ? StatusTone.Good : StatusTone.Warning);
            });
        _audioRouter.Diagnostic += (_, message) =>
            WriteDiagnostic($"AUDIO {message}");
        _targetRecordingMonitor.ActivityChanged += (_, active) =>
        {
            WriteDiagnostic($"TARGET recording={active}");
            _voiceSession.TargetRecordingChanged(active);
        };
        _targetRecordingMonitor.Diagnostic += (_, message) =>
            WriteDiagnostic($"TARGET {message}");
        _targetRecordingMonitor.Start();

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RemoteMicViewModel.LocalMuted) or
                nameof(RemoteMicViewModel.RemoteMuted) or
                nameof(RemoteMicViewModel.LocalSolo) or
                nameof(RemoteMicViewModel.RemoteSolo))
            {
                _audioRouter.SetLocalInputEnabled(!_viewModel.LocalMuted && !_viewModel.RemoteSolo);
                _audioRouter.SetRemoteInputEnabled(!_viewModel.RemoteMuted && !_viewModel.LocalSolo);
            }
            else if (args.PropertyName == nameof(RemoteMicViewModel.CaptureAlwaysOn))
            {
                _audioRouter.SetCaptureAlwaysOn(_viewModel.CaptureAlwaysOn);
            }
        };
    }

    private async Task InitializeRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var route = await _audioRouter.StartAsync(cancellationToken: cancellationToken);
            if (route.Success)
            {
                _viewModel.OutputEndpointName = route.VirtualCable?.Name ?? "RemoteMic 2ch";
                var externalCable = route.VirtualCable?.Name.Contains(
                    "CABLE Input",
                    StringComparison.OrdinalIgnoreCase) == true ||
                    route.VirtualCable?.Name.Contains(
                        "CABLE In",
                        StringComparison.OrdinalIgnoreCase) == true ||
                    route.VirtualCable?.Name.Contains(
                        "VB-Audio Virtual Cable",
                        StringComparison.OrdinalIgnoreCase) == true;
                _viewModel.UpdateStatus(
                    1,
                    externalCable ? "CableExternal" : "CableAvailable",
                    StatusTone.Good);
                _viewModel.UpdateStatus(
                    3,
                    route.Microphone is null ? "Unavailable" : "SystemManaged",
                    route.Microphone is null ? StatusTone.Error : StatusTone.Good);
            }
            else
            {
                _viewModel.UpdateStatus(1, "CableMissing", StatusTone.Error);
                _viewModel.UpdateStatus(3, "SystemManaged", StatusTone.Warning);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _viewModel.UpdateStatus(1, "CableMissing", StatusTone.Error);
            _viewModel.UpdateStatus(3, "Unavailable", StatusTone.Error);
        }

        try
        {
            var candidates = await AtvvGattClient.FindPairedCandidatesAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                _viewModel.UpdateStatus(2, "NoPairedRemote", StatusTone.Warning);
                _viewModel.UpdateStatus(4, "SystemManaged", StatusTone.Warning);
                return;
            }

            _viewModel.UpdateStatus(4, "SystemManaged", StatusTone.Good);
            var connected = 0;
            foreach (var candidate in candidates.Take(8))
            {
                try
                {
                    if (await ConnectRemoteAsync(candidate, cancellationToken))
                    {
                        connected++;
                    }
                    if (connected >= 2)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Skipping BLE candidate {candidate.Name}: {exception}");
                }
            }

            if (connected == 0)
            {
                _viewModel.UpdateStatus(2, "NoPairedRemote", StatusTone.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _viewModel.UpdateStatus(2, "HidBleDisconnected", StatusTone.Error);
            _viewModel.UpdateStatus(4, "Unavailable", StatusTone.Error);
        }
    }

    private async Task<bool> ConnectRemoteAsync(
        AtvvDeviceInfo candidate,
        CancellationToken cancellationToken)
    {
        var modelId = candidate.Name.Contains("x6", StringComparison.OrdinalIgnoreCase)
            ? "x6"
            : "chromecast";
        var remoteId = modelId == "x6"
            ? SupportedRemoteId.X6
            : SupportedRemoteId.Chromecast;
        if (_remoteSessionsById.ContainsKey(remoteId))
        {
            return false;
        }
        var session = new AtvvSessionController(new AtvvGattClient(), _audioRouter);
        session.Diagnostic += (_, message) =>
        {
            Debug.WriteLine($"[{candidate.Name}] {message}");
            WriteDiagnostic($"[{candidate.Name}] {message}");
        };
        session.ConnectionChanged += (_, connected) =>
            SetRemoteLinkState(remoteId, bleConnected: connected);
        session.StreamingChanged += (_, streaming) =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (streaming)
                {
                    _streamingSessions.Add(session);
                }
                else
                {
                    _streamingSessions.Remove(session);
                }
                _viewModel.SetStreaming(_streamingSessions.Count > 0);
            });
        session.MicrophoneOpenFailed += (_, code) =>
            Debug.WriteLine($"[{candidate.Name}] MIC_OPEN error 0x{code:X4}");
        session.AudioStarted += (_, started) =>
        {
            WriteDiagnostic(
                $"[{candidate.Name}] EVENT AudioStarted reason=0x{started.Reason:X2} " +
                $"stream={started.StreamId} phase={_voiceSession.Phase}");
            _voiceSession.RemoteAudioStarted(
                remoteId,
                started.Reason,
                DateTimeOffset.UtcNow,
                supportsPhysicalHoldGesture: remoteId == SupportedRemoteId.Chromecast);
        };
        session.AudioStopped += (_, reason) =>
        {
            WriteDiagnostic(
                $"[{candidate.Name}] EVENT AudioStopped reason=0x{reason:X2} " +
                $"phase={_voiceSession.Phase}");
            _voiceSession.RemoteAudioStopped(remoteId, reason, DateTimeOffset.UtcNow);
        };

        _remoteSessionsById[remoteId] = session;
        try
        {
            await session.ConnectAsync(candidate.Id, cancellationToken);
            _remoteSessions.Add(session);
            SetRemoteLinkState(remoteId, bleConnected: true);
            return true;
        }
        catch
        {
            _remoteSessionsById.TryRemove(remoteId, out _);
            SetRemoteLinkState(remoteId, bleConnected: false);
            await session.DisposeAsync();
            throw;
        }
    }

    private static void ResetDiagnosticLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLogPath)!);
            File.WriteAllText(
                DiagnosticLogPath,
                $"{DateTimeOffset.Now:O} RemoteMic runtime started{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            lock (DiagnosticLogGate)
            {
                File.AppendAllText(
                    DiagnosticLogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private void OnHidReport(object? sender, RemoteHidReport report)
    {
        string remoteId;
        IReadOnlyList<HidButtonEvent> buttonEvents;
        switch (report.Remote)
        {
            case WindowsRemoteKind.ChromecastVoiceRemote
                when _chromecastReports.TryNormalize(report.Bytes.Span, out var buttonEvent):
                remoteId = "chromecast";
                buttonEvents = [buttonEvent];
                break;
            case WindowsRemoteKind.X6Remote:
                remoteId = "x6";
                _x6VoiceGestures.Handle(report.Bytes.Span, report.Timestamp);
                buttonEvents = _x6Reports.Normalize(report.Bytes.Span);
                break;
            default:
                return;
        }

        if (buttonEvents.Count == 0)
        {
            return;
        }

        SetRemoteLinkState(
            remoteId == "x6" ? SupportedRemoteId.X6 : SupportedRemoteId.Chromecast,
            hidConnected: true);
        foreach (var buttonEvent in buttonEvents)
        {
            _gestureRecognizer.Handle(
                ScopeButtonId(remoteId, buttonEvent.ButtonId),
                buttonEvent.IsDown,
                report.Timestamp);
        }
    }

    private async void OnGesture(RemoteGestureEvent gesture)
    {
        if (!gesture.IsDown || _actionExecutor is null)
        {
            return;
        }

        if (!TryParseScopedButtonId(gesture.ButtonId, out var remoteId, out var buttonId))
        {
            return;
        }

        if (!_viewModel.IsMappingEnabled(remoteId))
        {
            return;
        }

        var row = FindMappingRow(remoteId, buttonId);
        if (row is null)
        {
            return;
        }

        var mapping = gesture.Gesture switch
        {
            RemoteGesture.Single => row.SingleKey,
            RemoteGesture.Double => row.DoubleKey,
            RemoteGesture.Long => row.LongKey,
            _ => "disabled",
        };

        try
        {
            await ExecuteMappingAsync(mapping, remoteId, buttonId, gesture.Gesture, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private async Task ProcessVoiceCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _voiceCommands.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    switch (command)
                    {
                        case VoiceSessionCommand.TriggerTap trigger when _actionExecutor is not null:
                            await _actionExecutor.SendKeyAsync(TriggerKey(), cancellationToken);
                            if (_targetRecordingMonitor.IsAvailable)
                            {
                                _ = VerifyTargetStateAfterTriggerAsync(
                                    trigger.OpensRecognition,
                                    cancellationToken);
                            }
                            else
                            {
                                // Keep non-Doubao targets usable when Windows
                                // does not expose a capture session.
                                _voiceSession.TargetRecordingChanged(trigger.OpensRecognition);
                            }
                            break;

                        case VoiceSessionCommand.OpenRemote open
                            when _remoteSessionsById.TryGetValue(open.Remote, out var session):
                            await session.OpenMicrophoneAsync(cancellationToken);
                            break;

                        case VoiceSessionCommand.CloseAllRemotes:
                            foreach (var remote in _remoteSessionsById.Values.Distinct())
                            {
                                try
                                {
                                    await remote.CloseMicrophoneAsync(cancellationToken);
                                }
                                catch (Exception exception) when (
                                    exception is not OperationCanceledException ||
                                    !cancellationToken.IsCancellationRequested)
                                {
                                    Debug.WriteLine(exception);
                                }
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task VerifyTargetStateAfterTriggerAsync(
        bool expectedRecording,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            var actual = _targetRecordingMonitor.IsRecording;
            WriteDiagnostic(
                $"TARGET verify expected={expectedRecording} actual={actual}");
            _voiceSession.TargetRecordingChanged(actual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteMappingAsync(
        string mapping,
        string remoteId,
        string buttonId,
        RemoteGesture gesture,
        CancellationToken cancellationToken)
    {
        var executor = _actionExecutor!;
        switch (mapping)
        {
            case "disabled":
                return;
            case "doubaoVoice":
                await executor.SendKeyAsync(TriggerKey(), cancellationToken);
                return;
            case "scrollUp":
                await executor.ScrollAsync(120, cancellationToken);
                return;
            case "scrollDown":
                await executor.ScrollAsync(-120, cancellationToken);
                return;
            case "arrowUp":
                await executor.SendKeyAsync(WindowsKey.Up, cancellationToken);
                return;
            case "arrowDown":
                await executor.SendKeyAsync(WindowsKey.Down, cancellationToken);
                return;
            case "arrowLeft":
                await executor.SendKeyAsync(WindowsKey.Left, cancellationToken);
                return;
            case "arrowRight":
                await executor.SendKeyAsync(WindowsKey.Right, cancellationToken);
                return;
            case "return":
                await executor.SendKeyAsync(WindowsKey.Enter, cancellationToken);
                return;
            case "escape":
                await executor.SendKeyAsync(WindowsKey.Escape, cancellationToken);
                return;
            case "delete":
                await executor.SendKeyAsync(WindowsKey.Backspace, cancellationToken);
                return;
            case "space":
                await executor.SendKeyAsync(WindowsKey.Space, cancellationToken);
                return;
            case "pageUp":
                await executor.SendKeyAsync(WindowsKey.PageUp, cancellationToken);
                return;
            case "pageDown":
                await executor.SendKeyAsync(WindowsKey.PageDown, cancellationToken);
                return;
            case "showDesktop":
                await executor.ShowDesktopAsync(cancellationToken);
                return;
            case "search":
                await executor.SearchAsync(cancellationToken);
                return;
            case "mute":
                await executor.SendKeyAsync(WindowsKey.VolumeMute, cancellationToken);
                return;
            case "volumeUp":
                await executor.SendKeyAsync(WindowsKey.VolumeUp, cancellationToken);
                return;
            case "volumeDown":
                await executor.SendKeyAsync(WindowsKey.VolumeDown, cancellationToken);
                return;
            case "focusInput":
                await executor.FocusInputAsync(cancellationToken);
                return;
            case "openApp":
                var row = FindMappingRow(remoteId, buttonId);
                var configuredTarget = row?.GetApplicationTarget(gesture);
                if (!string.IsNullOrWhiteSpace(configuredTarget))
                {
                    await executor.OpenAsync(configuredTarget, cancellationToken);
                    return;
                }

                var defaults = RemoteMappingDefaults.CreateWindows();
                var key = new RemoteMappingKey(
                    remoteId == "x6" ? SupportedRemoteId.X6 : SupportedRemoteId.Chromecast,
                    buttonId,
                    gesture);
                if (defaults.TryGetValue(key, out var binding) && binding.Application is { } app)
                {
                    await executor.OpenAsync(app.Target, cancellationToken);
                }
                return;
        }
    }

    private void MappingCombo_DropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox
            {
                SelectedValue: "openApp",
                DataContext: MappingRowViewModel row,
                Tag: string gestureName,
            } ||
            !Enum.TryParse<RemoteGesture>(gestureName, out var gesture))
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = App.CurrentLanguage == "zh-Hans" ? "选择要打开的应用" : "Choose an application",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) == true)
        {
            _viewModel.SetApplicationTarget(
                _viewModel.SelectedRemote.Id,
                row,
                gesture,
                picker.FileName);
        }
        else if (string.IsNullOrWhiteSpace(row.GetApplicationTarget(gesture)))
        {
            row.SetMapping(gesture, "disabled");
        }
    }

    private WindowsKey TriggerKey() => _viewModel.VoiceTrigger switch
    {
        "Left Ctrl" => WindowsKey.LeftControl,
        "Right Alt" => WindowsKey.RightAlt,
        "Left Alt" => WindowsKey.LeftAlt,
        "Shift" => WindowsKey.Shift,
        "Win" => WindowsKey.LeftWindows,
        _ => WindowsKey.RightControl,
    };

    private MappingRowViewModel? FindMappingRow(string remoteId, string buttonId) =>
        _viewModel.Devices
            .FirstOrDefault(device => device.Id == remoteId)?
            .Buttons.FirstOrDefault(row => row.ButtonId == buttonId);

    private void SetRemoteLinkState(
        SupportedRemoteId remote,
        bool? bleConnected = null,
        bool? hidConnected = null)
    {
        if (bleConnected is not null)
        {
            _bleLinks[remote] = bleConnected.Value;
        }
        if (hidConnected is not null)
        {
            _hidLinks[remote] = hidConnected.Value;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            var connected = _bleLinks.GetValueOrDefault(remote) ||
                            _hidLinks.GetValueOrDefault(remote);
            _viewModel.SetRemoteConnected(
                remote == SupportedRemoteId.X6 ? "x6" : "chromecast",
                connected);

            var anyBleConnected = _bleLinks.Values.Any(value => value);
            _viewModel.UpdateStatus(
                2,
                anyBleConnected ? "BleConnected" : "HidBleDisconnected",
                anyBleConnected ? StatusTone.Good : StatusTone.Warning);
        });
    }

    private static string ScopeButtonId(string remoteId, string buttonId) =>
        $"{remoteId}|{buttonId}";

    private static bool TryParseScopedButtonId(
        string scopedButtonId,
        out string remoteId,
        out string buttonId)
    {
        var separator = scopedButtonId.IndexOf('|');
        if (separator <= 0 || separator == scopedButtonId.Length - 1)
        {
            remoteId = string.Empty;
            buttonId = string.Empty;
            return false;
        }

        remoteId = scopedButtonId[..separator];
        buttonId = scopedButtonId[(separator + 1)..];
        return true;
    }

    private static void SetButtonResource(Button button, string property, string resource)
    {
        var dependencyProperty = property == "Background"
            ? Control.BackgroundProperty
            : Control.ForegroundProperty;
        button.SetResourceReference(dependencyProperty, resource);
    }

    private void ResetMappings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetMappings();
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        OpenUri("https://github.com/TheShadow178/RemoteMic#支持开发");
    }

    private void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not StatusRowViewModel status)
        {
            return;
        }

        if (status.Action == App.Text("BluetoothSettings"))
        {
            OpenUri("ms-settings:bluetooth");
        }
        else
        {
            OpenUri("ms-settings:sound");
        }
    }

    private void InitializeTray()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "RemoteMic",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowConsole();
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(MenuItem(App.CurrentLanguage == "zh-Hans" ? "打开控制台" : "Open console", ShowConsole));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var launchItem = MenuItem(
            App.CurrentLanguage == "zh-Hans" ? "登录时启动" : "Start at sign-in",
            ToggleStartAtLogin);
        launchItem.Checked = IsStartAtLoginEnabled();
        menu.Items.Add(launchItem);

        var language = new Forms.ToolStripMenuItem(App.CurrentLanguage == "zh-Hans" ? "语言" : "Language");
        var chinese = MenuItem("简体中文", () => ChangeLanguage("zh-Hans"));
        chinese.Checked = App.CurrentLanguage == "zh-Hans";
        var english = MenuItem("English", () => ChangeLanguage("en"));
        english.Checked = App.CurrentLanguage == "en";
        language.DropDownItems.Add(chinese);
        language.DropDownItems.Add(english);
        menu.Items.Add(language);

        var theme = new Forms.ToolStripMenuItem(App.CurrentLanguage == "zh-Hans" ? "外观" : "Appearance");
        var light = MenuItem(App.CurrentLanguage == "zh-Hans" ? "浅色" : "Light", () => ChangeTheme("Light"));
        light.Checked = App.CurrentTheme == "Light";
        var dark = MenuItem(App.CurrentLanguage == "zh-Hans" ? "深色" : "Dark", () => ChangeTheme("Dark"));
        dark.Checked = App.CurrentTheme == "Dark";
        theme.DropDownItems.Add(light);
        theme.DropDownItems.Add(dark);
        menu.Items.Add(theme);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(MenuItem(App.CurrentLanguage == "zh-Hans" ? "打开日志目录" : "Open logs", OpenLogs));
        menu.Items.Add(MenuItem(App.CurrentLanguage == "zh-Hans" ? "重启" : "Restart", Restart));
        menu.Items.Add(MenuItem(App.CurrentLanguage == "zh-Hans" ? "退出" : "Quit", ExitApplication));
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.ContextMenuStrip = menu;
    }

    private static Forms.ToolStripMenuItem MenuItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/remotemic-icon.png"));
        if (resource is null)
        {
            return (System.Drawing.Icon)SystemIcons.Application.Clone();
        }

        using (resource.Stream)
        using (var bitmap = new Bitmap(resource.Stream))
        {
            var handle = bitmap.GetHicon();
            try
            {
                return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            }
            finally
            {
                _ = DestroyIcon(handle);
            }
        }
    }

    private void ChangeLanguage(string language)
    {
        App.ApplyLanguage(language);
        _viewModel.RefreshLocalizedText();
        RebuildTrayMenu();
    }

    private void ChangeTheme(string theme)
    {
        App.ApplyTheme(theme);
        UpdateTabVisuals();
        RebuildTrayMenu();
    }

    private void ShowConsole()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _trayIcon?.ShowBalloonTip(
            1500,
            "RemoteMic",
            App.CurrentLanguage == "zh-Hans" ? "RemoteMic 仍在后台运行" : "RemoteMic is still running",
            Forms.ToolTipIcon.Info);
    }

    private static bool IsStartAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleStartAtLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue(RunValueName) is string)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else
            {
                var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    key.SetValue(RunValueName, $"\"{executable}\" --background");
                }
            }
        }
        catch
        {
            // Enterprise policy may lock the Run key; keep the app operational.
        }
        RebuildTrayMenu();
    }

    private static void OpenLogs()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteMic",
            "Logs");
        Directory.CreateDirectory(logPath);
        Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
    }

    private void Restart()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            Process.Start(new ProcessStartInfo(
                executable,
                $"--wait-for-pid {Environment.ProcessId}")
            {
                UseShellExecute = true,
            });
        }
        ExitApplication();
    }

    private async void ExitApplication()
    {
        _allowClose = true;
        try
        {
            await ShutdownRuntimeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            _trayIcon?.Icon?.Dispose();
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }

    private async Task ShutdownRuntimeAsync()
    {
        if (!_runtimeStarted)
        {
            return;
        }

        _runtimeStarted = false;
        var requestedRemoteClose = false;
        foreach (var session in _remoteSessions.ToArray())
        {
            if (!session.IsStreaming)
            {
                continue;
            }

            try
            {
                requestedRemoteClose = true;
                await session.CloseMicrophoneAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                WriteDiagnostic($"Shutdown MIC_CLOSE failed: {exception.Message}");
            }
        }
        if (requestedRemoteClose)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }

        _lifetime.Cancel();
        _voiceCommands.Writer.TryComplete();
        _gestureTimer.Stop();
        _gestureRecognizer.CancelPending();
        _x6VoiceGestures.Reset();
        _filterHidSource.Dispose();
        _hidSource.Dispose();
        _targetRecordingMonitor.Dispose();

        if (_voiceCommandLoop is not null)
        {
            try
            {
                await _voiceCommandLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in _remoteSessions.ToArray())
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
        }
        _remoteSessions.Clear();
        _remoteSessionsById.Clear();
        _bleLinks.Clear();
        _hidLinks.Clear();
        _streamingSessions.Clear();
        try
        {
            await _audioRouter.DisposeAsync();
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
