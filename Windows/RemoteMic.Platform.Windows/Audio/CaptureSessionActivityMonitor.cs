using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteMic.Platform.Windows.Audio;

/// <summary>
/// Observes whether the target input-method process is actively capturing from
/// the default Windows recording endpoint. This keeps the ATVV transport state
/// synchronized with the real recognizer instead of assuming SendInput always
/// toggled it successfully.
/// </summary>
public sealed class CaptureSessionActivityMonitor : IDisposable
{
    private static readonly HashSet<string> TargetProcesses = new(
        ["ImeService", "DoubaoImeSettings"],
        StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private int _polling;
    private int _available;
    private int _recording;
    private bool? _lastPublished;

    public event EventHandler<bool>? ActivityChanged;
    public event EventHandler<string>? Diagnostic;

    public bool IsAvailable => Volatile.Read(ref _available) != 0;
    public bool IsRecording => Volatile.Read(ref _recording) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        GC.SuppressFinalize(this);
    }

    private void Poll(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var active = ReadTargetActivity();
            Volatile.Write(ref _available, 1);
            Volatile.Write(ref _recording, active ? 1 : 0);
            if (_lastPublished != active)
            {
                _lastPublished = active;
                ActivityChanged?.Invoke(this, active);
                Diagnostic?.Invoke(
                    this,
                    $"豆包录音会话 {(active ? "Active" : "Inactive")}");
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _available, 0) != 0)
            {
                Diagnostic?.Invoke(this, $"录音会话监测不可用：{exception.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static bool ReadTargetActivity()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Capture,
                ERole.Console,
                out device));

            var managerId = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref managerId,
                ClsCtx.All,
                IntPtr.Zero,
                out var managerObject));
            manager = (IAudioSessionManager2)managerObject;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(index, out control));
                    if (control is not IAudioSessionControl2 control2 ||
                        control.GetState(out var sessionState) < 0 ||
                        sessionState != AudioSessionState.Active ||
                        control2.GetProcessId(out var processId) < 0 ||
                        processId == 0)
                    {
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        if (TargetProcesses.Contains(process.ProcessName))
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
                finally
                {
                    ReleaseComObject(control);
                }
            }

            return false;
        }
        finally
        {
            ReleaseComObject(sessions);
            ReleaseComObject(manager);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private bool _disposed;

    private enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    private enum AudioSessionState
    {
        Inactive,
        Active,
        Expired,
    }

    [Flags]
    private enum ClsCtx : uint
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InprocServer | InprocHandler | LocalServer | RemoteServer,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            ClsCtx classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(ref Guid sessionId, uint streamFlags, out IntPtr control);

        [PreserveSig]
        int GetSimpleAudioVolume(ref Guid sessionId, uint streamFlags, out IntPtr volume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        new int GetState(out AudioSessionState state);

        [PreserveSig]
        new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        new int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        new int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        new int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        new int UnregisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
