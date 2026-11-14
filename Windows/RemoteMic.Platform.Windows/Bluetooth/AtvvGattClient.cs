using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace RemoteMic.Platform.Windows.Bluetooth;

public static class AtvvGattUuids
{
    public static readonly Guid Service = Guid.Parse("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid Command = Guid.Parse("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid Audio = Guid.Parse("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid Control = Guid.Parse("AB5E0004-5A21-4F05-BC7D-AF01F617B664");
}

public sealed record AtvvDeviceInfo(string Id, string Name, bool IsConnected);

/// <summary>
/// WinRT GATT transport for Google Android TV Voice (ATVV). Protocol parsing
/// and ADPCM decoding live in RemoteMic.Core; this type owns only discovery,
/// notifications, writes and reconnect-safe resource lifetime.
/// </summary>
public sealed class AtvvGattClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _command;
    private GattCharacteristic? _audio;
    private GattCharacteristic? _control;
    private GattSession? _session;
    private bool _disposed;

    public event EventHandler<ReadOnlyMemory<byte>>? AudioReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? ControlReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Diagnostic;

    public string? DeviceId => _device?.DeviceId;
    public string? DeviceName => _device?.Name;
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public static async Task<IReadOnlyList<AtvvDeviceInfo>> FindPairedCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);

        // Name filtering is deliberately permissive. Firmware and Windows can
        // expose the same remote as "Chromecast Remote", "RemoteB..." or
        // "X6-Remote". ConnectAsync performs the authoritative ATVV-service
        // check before accepting a device.
        return devices
            .Where(device =>
                device.Name.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains("chromecast", StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains("google", StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains("x6", StringComparison.OrdinalIgnoreCase))
            .Select(device => new AtvvDeviceInfo(device.Id, device.Name, false))
            .ToArray();
    }

    public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();
            Diagnostic?.Invoke(this, "正在连接已配对遥控器…");

            BluetoothLEDevice? device = null;
            GattDeviceService? service = null;
            GattCharacteristic? audio = null;
            GattCharacteristic? control = null;
            GattSession? session = null;
            var audioNotificationsEnabled = false;
            var controlNotificationsEnabled = false;

            try
            {
                device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cancellationToken)
                    ?? throw new InvalidOperationException("Windows 无法打开这个蓝牙设备；请先在系统设置中完成配对。");
                device.ConnectionStatusChanged += OnConnectionStatusChanged;

                var services = await device
                    .GetGattServicesForUuidAsync(AtvvGattUuids.Service, BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken);
                EnsureSuccess(services.Status, "读取 ATVV 服务");
                service = services.Services.FirstOrDefault()
                    ?? throw new InvalidOperationException("这个遥控器没有 Google ATVV 语音服务。");

                var command = await GetCharacteristicAsync(
                    service,
                    AtvvGattUuids.Command,
                    "命令",
                    cancellationToken);
                audio = await GetCharacteristicAsync(
                    service,
                    AtvvGattUuids.Audio,
                    "音频",
                    cancellationToken);
                control = await GetCharacteristicAsync(
                    service,
                    AtvvGattUuids.Control,
                    "状态",
                    cancellationToken);

                session = await GattSession
                    .FromDeviceIdAsync(device.BluetoothDeviceId)
                    .AsTask(cancellationToken)
                    ?? throw new InvalidOperationException("Windows 无法创建 ATVV GATT 会话。");
                session.MaintainConnection = true;

                audio.ValueChanged += OnAudioValueChanged;
                control.ValueChanged += OnControlValueChanged;

                await EnableNotificationsAsync(audio, "音频", cancellationToken);
                audioNotificationsEnabled = true;
                await EnableNotificationsAsync(control, "状态", cancellationToken);
                controlNotificationsEnabled = true;

                _device = device;
                _service = service;
                _command = command;
                _audio = audio;
                _control = control;
                _session = session;
                Diagnostic?.Invoke(this, $"ATVV 已连接：{device.Name}");
                ConnectionChanged?.Invoke(this, true);
            }
            catch
            {
                if (audio is not null)
                {
                    audio.ValueChanged -= OnAudioValueChanged;
                    if (audioNotificationsEnabled)
                    {
                        await DisableNotificationsBestEffortAsync(audio);
                    }
                }
                if (control is not null)
                {
                    control.ValueChanged -= OnControlValueChanged;
                    if (controlNotificationsEnabled)
                    {
                        await DisableNotificationsBestEffortAsync(control);
                    }
                }
                if (device is not null)
                {
                    device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                }
                session?.Dispose();
                service?.Dispose();
                device?.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task WriteCommandAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var characteristic = _command
                ?? throw new InvalidOperationException("ATVV 尚未连接。");

            using var writer = new DataWriter();
            writer.WriteBytes(command.ToArray());
            var buffer = writer.DetachBuffer();
            var result = await characteristic
                .WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithoutResponse)
                .AsTask(cancellationToken);
            EnsureSuccess(result.Status, "写入 ATVV 命令");
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycle.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
        _lifecycle.Dispose();
        _writes.Dispose();
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        string label,
        CancellationToken cancellationToken)
    {
        var result = await service
            .GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);
        EnsureSuccess(result.Status, $"读取 ATVV {label}特征");
        return result.Characteristics.FirstOrDefault()
            ?? throw new InvalidOperationException($"遥控器缺少 ATVV {label}特征。");
    }

    private static async Task EnableNotificationsAsync(
        GattCharacteristic characteristic,
        string label,
        CancellationToken cancellationToken)
    {
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken);
        EnsureSuccess(status, $"订阅 ATVV {label}通知");
    }

    private static async Task DisableNotificationsBestEffortAsync(
        GattCharacteristic characteristic)
    {
        try
        {
            _ = await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
        }
        catch
        {
            // The link may already be gone. Local handler removal is the
            // important cleanup guarantee in that case.
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await _writes.WaitAsync();
        try
        {
            var hadDevice = _device is not null;
            if (_audio is not null)
            {
                _audio.ValueChanged -= OnAudioValueChanged;
            }

            if (_control is not null)
            {
                _control.ValueChanged -= OnControlValueChanged;
            }

            if (_device is not null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }

            _session?.Dispose();
            _service?.Dispose();
            _device?.Dispose();
            _session = null;
            _service = null;
            _device = null;
            _command = null;
            _audio = null;
            _control = null;
            await Task.Yield();

            if (hadDevice)
            {
                ConnectionChanged?.Invoke(this, false);
                Diagnostic?.Invoke(this, "ATVV 已断开。");
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    private void OnAudioValueChanged(
        GattCharacteristic sender,
        GattValueChangedEventArgs args) =>
        AudioReceived?.Invoke(this, ReadBuffer(args.CharacteristicValue));

    private void OnControlValueChanged(
        GattCharacteristic sender,
        GattValueChangedEventArgs args) =>
        ControlReceived?.Invoke(this, ReadBuffer(args.CharacteristicValue));

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!ReferenceEquals(sender, _device))
        {
            return;
        }

        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        ConnectionChanged?.Invoke(this, connected);
        Diagnostic?.Invoke(this, connected ? "蓝牙链路已恢复。" : "蓝牙链路已断开，等待重连。");
    }

    private static ReadOnlyMemory<byte> ReadBuffer(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void EnsureSuccess(GattCommunicationStatus status, string operation)
    {
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"{operation}失败：{status}");
        }
    }
}
