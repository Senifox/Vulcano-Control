namespace Vulcano.Core.Services;

/// <summary>A device seen while scanning. <paramref name="Id"/> is whatever the platform needs to
/// connect again - a Bluetooth address on Windows, a D-Bus object path under BlueZ.</summary>
public sealed record DiscoveredDevice(string Id, string Name);

public enum DisconnectReason
{
    /// <summary>We closed it.</summary>
    Requested,

    /// <summary>It went away: switched off, out of range, or taken over by another client.</summary>
    Lost
}

/// <summary>
/// Bluetooth as the rest of the app needs it: find a device, open a connection, read and write
/// characteristics by UUID. Deliberately narrow and free of platform types - a WinRT
/// <c>GattCharacteristic</c> or a BlueZ D-Bus proxy must never appear in a signature here, because
/// the whole point is that <see cref="BluetoothVolcanoDevice"/> above it knows nothing about either.
/// </summary>
public interface IVolcanoTransport
{
    /// <summary>
    /// Streams devices as they are seen, until the token is cancelled. Filtering by name is the
    /// caller's business: which names belong to a Volcano is device knowledge, not platform
    /// knowledge.
    /// </summary>
    IAsyncEnumerable<DiscoveredDevice> ScanAsync(CancellationToken ct);

    /// <summary>
    /// Connects and resolves the given services, returning null when the device cannot be reached
    /// or does not have them. The service list is passed in so the transport can fetch every
    /// characteristic of each in one round trip and answer later lookups from that.
    /// </summary>
    Task<IVolcanoConnection?> ConnectAsync(
        string deviceId,
        IReadOnlyList<Guid> services,
        CancellationToken ct);
}

/// <summary>
/// One open connection. Reads and writes return null/false rather than throwing on a protocol-level
/// failure: half of the characteristics here are optional, and "this device does not have it" is an
/// ordinary answer that leaves one setting unavailable, not an error worth unwinding a stack for.
/// </summary>
public interface IVolcanoConnection : IAsyncDisposable
{
    /// <summary>Whether the device turned out to have this characteristic at all.</summary>
    bool Supports(Guid characteristic);

    Task<byte[]?> ReadAsync(Guid characteristic, CancellationToken ct = default);

    Task<bool> WriteAsync(Guid characteristic, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to notifications. False means the device would not have it - which for some
    /// optional characteristics is expected, and the caller falls back to polling.
    /// </summary>
    Task<bool> SubscribeAsync(Guid characteristic, Action<byte[]> onValue, CancellationToken ct = default);

    /// <summary>Raised once when the connection ends, from a platform thread.</summary>
    event EventHandler<DisconnectReason>? Disconnected;
}
