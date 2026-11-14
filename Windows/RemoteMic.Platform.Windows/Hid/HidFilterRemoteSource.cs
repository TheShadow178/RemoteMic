using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace RemoteMic.Platform.Windows.Hid;

/// <summary>
/// Polls the sideband interface exposed by the optional Chromecast HID filter.
/// The filter copies reports after the lower stack completes each read and
/// forwards the original request unchanged, so this source never owns or
/// suppresses the physical device.
/// </summary>
public sealed class HidFilterRemoteSource : IDisposable
{
    private static readonly Guid InterfaceId =
        new("A88B865D-D5AB-4232-A02D-5DE87735420F");

    private CancellationTokenSource? _lifetime;
    private Task? _pollLoop;
    private SynchronizationContext? _eventContext;
    private int _connected;

    public event EventHandler<RemoteHidReport>? ReportReceived;
    public event EventHandler<RemoteHidDeviceChange>? DeviceConnectionChanged;
    public event EventHandler<string>? Diagnostic;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lifetime is not null)
        {
            return;
        }

        _eventContext = SynchronizationContext.Current;
        _lifetime = new CancellationTokenSource();
        _pollLoop = Task.Run(() => PollAsync(_lifetime.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        GC.SuppressFinalize(this);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var handle = TryOpen();
            if (handle is null)
            {
                SetConnected(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            SetConnected(true);
            Post(() => Diagnostic?.Invoke(
                this,
                "RemoteMic HID Filter 已连接；使用驱动报告后端。"));
            uint lastSequence = 0;
            var output = new byte[ReportEnvelopeSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!DeviceIoControl(
                        handle,
                        IoctlGetLatestReport,
                        null,
                        0,
                        output,
                        output.Length,
                        out var bytesReturned,
                        IntPtr.Zero) ||
                    bytesReturned < ReportEnvelopeSize)
                {
                    break;
                }

                var sequence = BitConverter.ToUInt32(output, 0);
                var reportLength = BitConverter.ToUInt32(output, 4);
                if (sequence != 0 && sequence != lastSequence &&
                    reportLength is > 0 and <= MaxReportBytes)
                {
                    lastSequence = sequence;
                    var bytes = output.AsSpan(8, checked((int)reportLength)).ToArray();
                    Post(() => ReportReceived?.Invoke(
                        this,
                        new RemoteHidReport(
                            WindowsRemoteKind.ChromecastVoiceRemote,
                            "RemoteMic HID Filter",
                            bytes,
                            DateTimeOffset.UtcNow)));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }

            SetConnected(false);
        }
    }

    private void SetConnected(bool connected)
    {
        var value = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _connected, value) == value)
        {
            return;
        }

        Post(() => DeviceConnectionChanged?.Invoke(
            this,
            new RemoteHidDeviceChange(
                WindowsRemoteKind.ChromecastVoiceRemote,
                connected)));
    }

    private void Post(Action action)
    {
        if (_eventContext is null)
        {
            action();
        }
        else
        {
            _eventContext.Post(_ => action(), null);
        }
    }

    private static SafeFileHandle? TryOpen()
    {
        var controlHandle = CreateFile(
            @"\\.\RemoteMicHidFilter",
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (!controlHandle.IsInvalid)
        {
            return controlHandle;
        }
        controlHandle.Dispose();

        var interfaceId = InterfaceId;
        if (CM_Get_Device_Interface_List_Size(
                out var characterCount,
                ref interfaceId,
                null,
                CmGetDeviceInterfaceListPresent) != CrSuccess ||
            characterCount <= 1)
        {
            return null;
        }

        var buffer = new char[characterCount];
        if (CM_Get_Device_Interface_List(
                ref interfaceId,
                null,
                buffer,
                characterCount,
                CmGetDeviceInterfaceListPresent) != CrSuccess)
        {
            return null;
        }

        foreach (var path in new string(buffer).Split(
                     '\0',
                     StringSplitOptions.RemoveEmptyEntries)
                 .OrderByDescending(path => path.EndsWith(
                     "\\RemoteMic",
                     StringComparison.OrdinalIgnoreCase)))
        {
            var handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
        }

        return null;
    }

    private bool _disposed;
    private const int CrSuccess = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int MaxReportBytes = 64;
    private const int ReportEnvelopeSize = 8 + MaxReportBytes;
    private const uint IoctlGetLatestReport =
        (0x22U << 16) | (1U << 14) | (0x801U << 2);

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
        byte[]? inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
