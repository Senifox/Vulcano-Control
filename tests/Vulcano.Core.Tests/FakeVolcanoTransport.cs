using Vulcano.Core.Services;
using Chars = Vulcano.Core.Services.VolcanoUuids.Characteristics;

namespace Vulcano.Core.Tests;

/// <summary>
/// A transport with no Bluetooth behind it: it advertises whatever devices the test names, holds a
/// dictionary of characteristic values, records every write, and lets a test push a notification.
///
/// This is what the transport interface was for. Everything about the Volcano protocol - the
/// encodings, the inverted flag polarity, the order of operations on connect - is testable through
/// it without a device, a Bluetooth adapter, or Windows.
/// </summary>
public sealed class FakeVolcanoTransport : IVolcanoTransport
{
    private readonly List<DiscoveredDevice> _advertised = new();

    public FakeConnection? Connection { get; private set; }

    /// <summary>Characteristics the device will claim to have, with their initial values.</summary>
    public Dictionary<Guid, byte[]> Characteristics { get; } = new();

    /// <summary>Characteristics that exist but refuse a notify subscription, as some really do.</summary>
    public HashSet<Guid> NotifyRefused { get; } = new();

    /// <summary>Services the fake will fail to resolve, to exercise the "not a Volcano" path.</summary>
    public bool FailConnect { get; set; }

    public void Advertise(string name, string id = "AA:BB") => _advertised.Add(new DiscoveredDevice(id, name));

    /// <summary>Gives the device every characteristic the app knows about, with plausible values.</summary>
    public void GiveEverything()
    {
        Characteristics[Chars.CurrentTemperature] = Bytes(1930);   // 193.0 °C
        Characteristics[Chars.TargetTemperature] = Bytes(1850);
        Characteristics[Chars.Activity] = Bytes(0);
        Characteristics[Chars.HeaterOn] = [];
        Characteristics[Chars.HeaterOff] = [];
        Characteristics[Chars.PumpOn] = [];
        Characteristics[Chars.PumpOff] = [];
        Characteristics[Chars.Brightness] = Bytes(70);
        Characteristics[Chars.CurrentAutoOffValue] = Bytes(0);
        Characteristics[Chars.ShutoffTime] = Bytes(40 * 60);
        Characteristics[Chars.HoursOfHeating] = Bytes(412);
        Characteristics[Chars.MinutesOfHeating] = Bytes(37);
        Characteristics[Chars.FirmwareVersion] = "V1.63"u8.ToArray();
        Characteristics[Chars.FirmwareBleVersion] = "V1.35"u8.ToArray();
        Characteristics[Chars.SerialNumber] = "VC22C0281"u8.ToArray();
        Characteristics[Chars.Display] = Bytes(0);
        Characteristics[Chars.Vibration] = Bytes(0);
    }

    public static byte[] Bytes(ushort value) => BleEncoding.ToUInt16LEBytes(value);

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var device in _advertised)
        {
            ct.ThrowIfCancellationRequested();
            yield return device;
            await Task.Yield();
        }

        // A real scan keeps running until it is cancelled; mimic that so the caller's timeout is
        // what ends it rather than the stream simply finishing.
        await Task.Delay(Timeout.Infinite, ct);
    }

    public Task<IVolcanoConnection?> ConnectAsync(
        string deviceId, IReadOnlyList<Guid> services, CancellationToken ct)
    {
        if (FailConnect) return Task.FromResult<IVolcanoConnection?>(null);

        Connection = new FakeConnection(Characteristics, NotifyRefused);
        return Task.FromResult<IVolcanoConnection?>(Connection);
    }
}

public sealed class FakeConnection : IVolcanoConnection
{
    private readonly Dictionary<Guid, byte[]> _values;
    private readonly HashSet<Guid> _notifyRefused;
    private readonly Dictionary<Guid, Action<byte[]>> _subscribers = new();

    public FakeConnection(Dictionary<Guid, byte[]> values, HashSet<Guid> notifyRefused)
    {
        _values = values;
        _notifyRefused = notifyRefused;
    }

    /// <summary>Every write, in order, so a test can assert on the exact bytes sent.</summary>
    public List<(Guid Characteristic, byte[] Value)> Writes { get; } = new();

    public bool IsDisposed { get; private set; }

    public event EventHandler<DisconnectReason>? Disconnected;

    public bool Supports(Guid characteristic) => _values.ContainsKey(characteristic);

    public Task<byte[]?> ReadAsync(Guid characteristic, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(characteristic, out var value) ? value : null);

    public Task<bool> WriteAsync(Guid characteristic, byte[] value, CancellationToken ct = default)
    {
        if (!_values.ContainsKey(characteristic)) return Task.FromResult(false);

        Writes.Add((characteristic, value));
        return Task.FromResult(true);
    }

    public Task<bool> SubscribeAsync(Guid characteristic, Action<byte[]> onValue, CancellationToken ct = default)
    {
        if (!_values.ContainsKey(characteristic) || _notifyRefused.Contains(characteristic))
        {
            return Task.FromResult(false);
        }

        _subscribers[characteristic] = onValue;
        return Task.FromResult(true);
    }

    /// <summary>Pretend the device sent a notification.</summary>
    public void Notify(Guid characteristic, ushort raw)
    {
        if (_subscribers.TryGetValue(characteristic, out var handler))
        {
            handler(BleEncoding.ToUInt16LEBytes(raw));
        }
    }

    public bool IsSubscribed(Guid characteristic) => _subscribers.ContainsKey(characteristic);

    /// <summary>Pretend the device went away.</summary>
    public void DropConnection() => Disconnected?.Invoke(this, DisconnectReason.Lost);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
