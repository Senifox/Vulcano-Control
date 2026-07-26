using System.Runtime.InteropServices.WindowsRuntime;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace Vulcano.Bluetooth.Windows;

/// <summary>
/// One open GATT connection, with every characteristic of the requested services already resolved
/// into a lookup. Reads and writes answer with null/false instead of throwing: above this, "the
/// device does not have it" is an ordinary outcome for half of them.
/// </summary>
internal sealed class WinRtConnection : IVolcanoConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly List<GattDeviceService> _services;
    private readonly Dictionary<Guid, GattCharacteristic> _characteristics;
    private readonly LogService _logService;

    /// <summary>
    /// Kept so the handlers can be detached again on dispose. WinRT holds the subscription on the
    /// characteristic, and leaving handlers attached to a disposed service is how you get callbacks
    /// into a torn-down object.
    /// </summary>
    private readonly List<(GattCharacteristic Characteristic, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler)> _subscriptions = new();

    private bool _disposed;

    public WinRtConnection(
        BluetoothLEDevice device,
        List<GattDeviceService> services,
        Dictionary<Guid, GattCharacteristic> characteristics,
        LogService logService)
    {
        _device = device;
        _services = services;
        _characteristics = characteristics;
        _logService = logService;

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public event EventHandler<DisconnectReason>? Disconnected;

    public bool Supports(Guid characteristic) => _characteristics.ContainsKey(characteristic);

    public async Task<byte[]?> ReadAsync(Guid characteristic, CancellationToken ct = default)
    {
        if (!_characteristics.TryGetValue(characteristic, out var target)) return null;

        try
        {
            // Uncached throughout: the point of every read here is the device's current state, and
            // Windows will happily hand back a value from minutes ago otherwise.
            var result = await target.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            return result.Status == GattCommunicationStatus.Success ? result.Value.ToArray() : null;
        }
        catch (Exception ex)
        {
            _logService.Log($"Read {characteristic} failed: {ex.Message}", LogLevel.Debug);
            return null;
        }
    }

    public async Task<bool> WriteAsync(Guid characteristic, byte[] value, CancellationToken ct = default)
    {
        if (!_characteristics.TryGetValue(characteristic, out var target)) return false;

        try
        {
            var status = await target.WriteValueAsync(value.AsBuffer()).AsTask(ct);
            return status == GattCommunicationStatus.Success;
        }
        catch (Exception ex)
        {
            _logService.Log($"Write {characteristic} failed: {ex.Message}", LogLevel.Debug);
            return false;
        }
    }

    public async Task<bool> SubscribeAsync(Guid characteristic, Action<byte[]> onValue, CancellationToken ct = default)
    {
        if (!_characteristics.TryGetValue(characteristic, out var target)) return false;

        void Handler(GattCharacteristic sender, GattValueChangedEventArgs args) =>
            onValue(args.CharacteristicValue.ToArray());

        target.ValueChanged += Handler;

        GattCommunicationStatus status;
        try
        {
            status = await target.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        }
        catch (Exception)
        {
            // Not every characteristic supports notify; the caller decides whether that matters.
            status = GattCommunicationStatus.Unreachable;
        }

        if (status != GattCommunicationStatus.Success)
        {
            target.ValueChanged -= Handler;
            return false;
        }

        _subscriptions.Add((target, Handler));
        return true;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        if (_disposed) return;

        Disconnected?.Invoke(this, DisconnectReason.Lost);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        foreach (var (characteristic, handler) in _subscriptions)
        {
            characteristic.ValueChanged -= handler;
        }
        _subscriptions.Clear();

        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        // Disposing the services is what actually closes the link; the device object alone does not.
        foreach (var service in _services)
        {
            service.Dispose();
        }
        _services.Clear();
        _characteristics.Clear();

        _device.Dispose();

        Disconnected?.Invoke(this, DisconnectReason.Requested);
        return ValueTask.CompletedTask;
    }
}
