using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Vulcano.Bluetooth.Windows;

/// <summary>
/// Bluetooth LE through WinRT. The only place in the app that knows what a
/// <see cref="GattCharacteristic"/> is; everything above it works in UUIDs and byte arrays.
///
/// Two hard-won details from the WPF version are kept deliberately:
///
/// - The GATT timeout is generous. Some Windows 10 machines take single-digit seconds per call
///   where a typical Windows 11 machine takes well under one, and the calls still succeed.
/// - Characteristics are fetched per service in one unfiltered call and looked up locally.
///   GetCharacteristicsForUuidAsync was found to reliably time out on at least one Windows 10
///   machine while the unfiltered call and GetGattServicesForUuidAsync both worked. Doing it once
///   per service also means one round trip instead of seventeen.
/// </summary>
public sealed class WinRtVolcanoTransport : IVolcanoTransport
{
    private static readonly TimeSpan GattTimeout = TimeSpan.FromSeconds(30);

    private readonly LogService _logService;

    public WinRtVolcanoTransport(LogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// Bridges the advertisement watcher's callbacks into an async stream. Unbounded and
    /// duplicate-suppressed: a device advertises several times a second, and the caller only wants
    /// to hear about each one once.
    /// </summary>
    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<DiscoveredDevice>();
        var seen = new HashSet<ulong>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name)) return;

            lock (seen)
            {
                if (!seen.Add(args.BluetoothAddress)) return;
            }

            // The address as a hex string is the id ConnectAsync takes back.
            channel.Writer.TryWrite(new DiscoveredDevice(args.BluetoothAddress.ToString("X12"), name));
        }

        watcher.Received += OnReceived;

        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            // No adapter, or Bluetooth switched off at the OS level.
            watcher.Received -= OnReceived;
            _logService.Log(Strings.Get("Error.ScanFailed", ex.Message), LogLevel.Error);
            yield break;
        }

        try
        {
            await foreach (var device in channel.Reader.ReadAllAsync(ct))
            {
                yield return device;
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                watcher.Stop();
            }
        }
    }

    public async Task<IVolcanoConnection?> ConnectAsync(
        string deviceId,
        IReadOnlyList<Guid> services,
        CancellationToken ct)
    {
        if (!ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            return null;
        }

        _logService.Log(Strings.Get("Log.BleConnecting", deviceId), LogLevel.Debug);

        var device = await WithTimeoutAsync(
            BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(), "FromBluetoothAddress");

        if (device is null) return null;

        var characteristics = new Dictionary<Guid, GattCharacteristic>();
        var resolvedServices = new List<GattDeviceService>();

        foreach (var serviceUuid in services)
        {
            var service = await ResolveServiceAsync(device, serviceUuid);
            if (service is null)
            {
                foreach (var open in resolvedServices) open.Dispose();
                device.Dispose();
                return null;
            }

            resolvedServices.Add(service);

            foreach (var characteristic in await ResolveCharacteristicsAsync(service, serviceUuid))
            {
                characteristics[characteristic.Uuid] = characteristic;
            }
        }

        return new WinRtConnection(device, resolvedServices, characteristics, _logService);
    }

    private async Task<GattDeviceService?> ResolveServiceAsync(BluetoothLEDevice device, Guid serviceUuid)
    {
        var result = await WithTimeoutAsync(
            device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask(),
            $"service {serviceUuid}");

        if (result is null) return null;

        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            _logService.Log(
                Strings.Get("Log.BleServiceMissing", serviceUuid, result.Status), LogLevel.Warning);
            return null;
        }

        return result.Services[0];
    }

    private async Task<IReadOnlyList<GattCharacteristic>> ResolveCharacteristicsAsync(
        GattDeviceService service, Guid serviceUuid)
    {
        var result = await WithTimeoutAsync(
            service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(),
            $"characteristics of {serviceUuid}");

        if (result is null || result.Status != GattCommunicationStatus.Success)
        {
            return [];
        }

        _logService.Log(
            Strings.Get("Log.BleServiceFound", serviceUuid, result.Characteristics.Count), LogLevel.Debug);

        return result.Characteristics;
    }

    /// <summary>
    /// WinRT's GATT calls have no timeout of their own and have been seen to hang outright. Racing
    /// them against a delay turns that into a null rather than an app that never finishes connecting.
    /// </summary>
    private async Task<T?> WithTimeoutAsync<T>(Task<T> task, string what) where T : class
    {
        var completed = await Task.WhenAny(task, Task.Delay(GattTimeout));

        if (completed != task)
        {
            _logService.Log(
                Strings.Get("Log.BleServiceTimeout", what, (int)GattTimeout.TotalSeconds), LogLevel.Error);
            return null;
        }

        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _logService.Log(Strings.Get("Error.ConnectFailed", ex.Message), LogLevel.Error);
            return null;
        }
    }
}
