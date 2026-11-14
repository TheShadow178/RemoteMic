using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace RemoteMic.Platform.Windows.Input;

/// <summary>
/// Sends boot-keyboard reports through the optional RemoteMic VHF driver.
/// This is required for global-shortcut handlers that intentionally reject
/// Win32 SendInput events. The desktop app keeps SendInput as its no-driver
/// fallback for preview and clean-machine behavior.
/// </summary>
internal sealed class VirtualHidKeyboard : IDisposable
{
    private static readonly Guid InterfaceId =
        new("D935FB95-3E8F-44C8-B460-1AAB331FB9B1");

    private readonly SafeFileHandle _handle;

    private VirtualHidKeyboard(SafeFileHandle handle) => _handle = handle;

    public static VirtualHidKeyboard? TryOpen()
    {
        var interfaceId = InterfaceId;
        var result = CM_Get_Device_Interface_List_Size(
            out var characterCount,
            ref interfaceId,
            null,
            CmGetDeviceInterfaceListPresent);
        if (result != CrSuccess || characterCount <= 1)
        {
            return null;
        }

        var buffer = new char[characterCount];
        result = CM_Get_Device_Interface_List(
            ref interfaceId,
            null,
            buffer,
            characterCount,
            CmGetDeviceInterfaceListPresent);
        if (result != CrSuccess)
        {
            return null;
        }

        var terminator = Array.IndexOf(buffer, '\0');
        if (terminator <= 0)
        {
            return null;
        }

        var path = new string(buffer, 0, terminator);
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return new VirtualHidKeyboard(handle);
    }

    public bool TrySubmit(WindowsKey key, bool keyUp)
    {
        if (!TryTranslate(key, out var modifier, out var usage))
        {
            return false;
        }

        var report = new byte[8];
        if (!keyUp)
        {
            report[0] = modifier;
            report[2] = usage;
        }

        return DeviceIoControl(
            _handle,
            IoctlSubmitKeyboardReport,
            report,
            report.Length,
            null,
            0,
            out _,
            IntPtr.Zero);
    }

    public void Dispose() => _handle.Dispose();

    private static bool TryTranslate(
        WindowsKey key,
        out byte modifier,
        out byte usage)
    {
        modifier = key switch
        {
            WindowsKey.LeftControl => 0x01,
            WindowsKey.Shift => 0x02,
            WindowsKey.LeftAlt => 0x04,
            WindowsKey.LeftWindows => 0x08,
            WindowsKey.RightControl => 0x10,
            WindowsKey.RightAlt => 0x40,
            _ => 0,
        };
        usage = key switch
        {
            WindowsKey.A => 0x04,
            WindowsKey.C => 0x06,
            WindowsKey.F => 0x09,
            WindowsKey.L => 0x0F,
            WindowsKey.V => 0x19,
            WindowsKey.X => 0x1B,
            WindowsKey.Y => 0x1C,
            WindowsKey.Z => 0x1D,
            WindowsKey.Enter => 0x28,
            WindowsKey.Escape => 0x29,
            WindowsKey.Backspace => 0x2A,
            WindowsKey.Tab => 0x2B,
            WindowsKey.Space => 0x2C,
            WindowsKey.Delete => 0x4C,
            WindowsKey.End => 0x4D,
            WindowsKey.PageDown => 0x4E,
            WindowsKey.Right => 0x4F,
            WindowsKey.Left => 0x50,
            WindowsKey.Down => 0x51,
            WindowsKey.Up => 0x52,
            WindowsKey.Home => 0x4A,
            WindowsKey.PageUp => 0x4B,
            _ => 0,
        };
        return modifier != 0 || usage != 0;
    }

    private const int CrSuccess = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlSubmitKeyboardReport =
        (0x22U << 16) | (2U << 14) | (0x800U << 2);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(
        out uint length,
        ref Guid interfaceClassGuid,
        string? deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(
        ref Guid interfaceClassGuid,
        string? deviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[]? outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
