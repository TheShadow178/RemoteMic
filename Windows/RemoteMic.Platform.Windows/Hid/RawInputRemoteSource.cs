using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace RemoteMic.Platform.Windows.Hid;

public enum WindowsRemoteKind
{
    Unknown,
    ChromecastVoiceRemote,
    X6Remote,
}

public sealed record RemoteHidReport(
    WindowsRemoteKind Remote,
    string DevicePath,
    ReadOnlyMemory<byte> Bytes,
    DateTimeOffset Timestamp);

public sealed record RemoteHidDeviceChange(WindowsRemoteKind Remote, bool Connected);

/// <summary>
/// Best-effort user-mode HID observer. Raw Input can expose Consumer Control
/// reports on many Windows builds, but cannot reliably suppress the original
/// action for keyboard-class collections. The production HID-filter backend
/// implements the same event contract and reports CanSuppressOriginal=true.
/// </summary>
public sealed class RawInputRemoteSource : IDisposable
{
    private HwndSource? _source;
    private readonly Dictionary<IntPtr, WindowsRemoteKind> _knownDevices = [];

    public event EventHandler<RemoteHidReport>? ReportReceived;
    public event EventHandler<RemoteHidDeviceChange>? DeviceConnectionChanged;
    public event EventHandler<string>? Diagnostic;

    public bool CanSuppressOriginal => false;
    public bool IsStarted => _source is not null;

    public void Start(Window owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("无法取得 RemoteMic 窗口句柄。");
        _source.AddHook(WindowMessageHook);

        var devices = new[]
        {
            // Consumer Control TLC: media/navigation buttons on REV 011b.
            new RAWINPUTDEVICE(0x000C, 0x0001, RidevInputSink | RidevDevNotify, handle),
            // Keyboard TLC: observed on some REV 0110 firmware. Windows may
            // keep this collection exclusive; registration failure is logged.
            new RAWINPUTDEVICE(0x0001, 0x0006, RidevInputSink | RidevDevNotify, handle),
            // X6 power key is exposed through the Generic Desktop System
            // Control top-level collection (report 3).
            new RAWINPUTDEVICE(0x0001, 0x0080, RidevInputSink | RidevDevNotify, handle),
        };

        if (!RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            throw new Win32Exception(error, "注册遥控器 Raw Input 失败。");
        }

        Diagnostic?.Invoke(
            this,
            "Raw Input 已启动（观察模式；完整按设备拦截需要 RemoteMic HID Filter）。");
    }

    public void Stop()
    {
        if (_source is null)
        {
            return;
        }

        _source.RemoveHook(WindowMessageHook);
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowMessageHook(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmInput)
        {
            ReadInput(lParam);
        }
        else if (message == WmInputDeviceChange)
        {
            HandleDeviceChange(wParam, lParam);
        }

        return IntPtr.Zero;
    }

    private void ReadInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        if (GetRawInputData(
                rawInputHandle,
                RidInput,
                IntPtr.Zero,
                ref size,
                (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != 0 ||
            size == 0)
        {
            return;
        }

        var memory = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetRawInputData(
                    rawInputHandle,
                    RidInput,
                    memory,
                    ref size,
                    (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(memory);
            if (header.Type != RimTypeHid)
            {
                return;
            }

            var path = GetDevicePath(header.Device);
            var remote = Classify(path);
            if (remote == WindowsRemoteKind.Unknown)
            {
                return;
            }

            if (_knownDevices.TryAdd(header.Device, remote))
            {
                DeviceConnectionChanged?.Invoke(this, new RemoteHidDeviceChange(remote, true));
            }

            var dataOffset = Marshal.SizeOf<RAWINPUTHEADER>();
            var reportSize = unchecked((uint)Marshal.ReadInt32(memory, dataOffset));
            var reportCount = unchecked((uint)Marshal.ReadInt32(memory, dataOffset + sizeof(uint)));
            if (reportSize == 0 || reportCount == 0 || reportSize > 4096 || reportCount > 128)
            {
                return;
            }

            var rawDataOffset = dataOffset + (sizeof(uint) * 2);
            var totalBytes = checked((long)reportSize * reportCount);
            if (rawDataOffset + totalBytes > size)
            {
                return;
            }

            for (var reportIndex = 0U; reportIndex < reportCount; reportIndex++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(
                    IntPtr.Add(memory, checked(rawDataOffset + (int)(reportIndex * reportSize))),
                    report,
                    0,
                    report.Length);
                ReportReceived?.Invoke(
                    this,
                    new RemoteHidReport(remote, path, report, DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private void HandleDeviceChange(IntPtr change, IntPtr device)
    {
        if (change.ToInt64() == GidcArrival)
        {
            var remote = Classify(GetDevicePath(device));
            if (remote != WindowsRemoteKind.Unknown && _knownDevices.TryAdd(device, remote))
            {
                DeviceConnectionChanged?.Invoke(this, new RemoteHidDeviceChange(remote, true));
            }
            return;
        }

        if (change.ToInt64() == GidcRemoval && _knownDevices.Remove(device, out var removed))
        {
            DeviceConnectionChanged?.Invoke(this, new RemoteHidDeviceChange(removed, false));
        }
    }

    private static string GetDevicePath(IntPtr device)
    {
        uint characterCount = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, IntPtr.Zero, ref characterCount);
        if (characterCount == 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(checked((int)characterCount));
        return GetRawInputDeviceInfo(
                   device,
                   RidiDeviceName,
                   buffer,
                   ref characterCount) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static WindowsRemoteKind Classify(string path)
    {
        if (HasVidPid(path, "18D1", "9450"))
        {
            return WindowsRemoteKind.ChromecastVoiceRemote;
        }

        if (HasVidPid(path, "1D5A", "C081"))
        {
            return WindowsRemoteKind.X6Remote;
        }

        return WindowsRemoteKind.Unknown;
    }

    private static bool HasVidPid(string path, string vendorId, string productId) =>
        path.Contains($"VID_{vendorId}&PID_{productId}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"VID&01{vendorId}_PID&{productId}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"VID&02{vendorId}_PID&{productId}", StringComparison.OrdinalIgnoreCase);

    private bool _disposed;

    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const long GidcArrival = 1;
    private const long GidcRemoval = 2;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RimTypeHid = 2;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RAWINPUTDEVICE(
        ushort usagePage,
        ushort usage,
        uint flags,
        IntPtr target)
    {
        public readonly ushort UsagePage = usagePage;
        public readonly ushort Usage = usage;
        public readonly uint Flags = flags;
        public readonly IntPtr Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device,
        uint command,
        StringBuilder data,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device,
        uint command,
        IntPtr data,
        ref uint size);
}
