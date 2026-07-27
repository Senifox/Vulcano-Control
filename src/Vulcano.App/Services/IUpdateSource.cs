using System.Threading.Tasks;

namespace Vulcano.App.Services;

/// <summary>What came back from asking whether there is a newer version.</summary>
/// <param name="Version">The version on offer, or null when this copy is current.</param>
/// <param name="Error">Why the question could not be answered, or null when it could. A failed
/// check is an ordinary outcome - the machine may simply be offline - so it is a value here rather
/// than an exception.</param>
public sealed record UpdateCheck(string? Version, string? Error)
{
    public static readonly UpdateCheck UpToDate = new(null, null);

    public static UpdateCheck Found(string version) => new(version, null);

    public static UpdateCheck Failed(string error) => new(null, error);

    public bool Available => Version is not null;

    public bool DidFail => Error is not null;
}

/// <summary>
/// Where new versions come from, as much of it as the app has an opinion about.
///
/// An interface rather than the packaging library directly, for two reasons. The library only works
/// from inside an installed copy, so with it the update logic could not be exercised at all - not in
/// a test, not from the debugger. And the decisions worth getting right are not its: whether to
/// check at all, whether now is a moment to restart, what to say when the answer never arrives.
/// Those belong to the app and are tested against a fake of this.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// False when this copy cannot update itself - run from the debugger, or the portable build.
    /// Everything else here is then pointless, and the interface says so instead of failing later.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>A version downloaded during an earlier session and waiting to be applied, or null.
    /// Read at startup so a pending update is not downloaded a second time.</summary>
    string? PendingVersion { get; }

    Task<UpdateCheck> CheckAsync();

    /// <summary>Fetches whatever the last check found. False when it did not arrive.</summary>
    Task<bool> DownloadAsync();

    /// <summary>
    /// Arranges for the downloaded version to be installed once this app has exited, without
    /// restarting it. The quiet path, and the only one safe to take on our own: applying an update
    /// means stopping the app, and stopping the app mid-ramp means a device left heating with
    /// nothing watching it.
    /// </summary>
    void ApplyOnExit();

    /// <summary>Installs it now and comes back up. Only ever on request.</summary>
    void ApplyAndRestart();
}

/// <summary>
/// The source for a copy that has no update mechanism at all - a test, or the app built without one.
/// Says so through <see cref="IsSupported"/> and does nothing else, which spares every caller a
/// null check for a case that is not exceptional.
/// </summary>
public sealed class NoUpdateSource : IUpdateSource
{
    public bool IsSupported => false;

    public string? PendingVersion => null;

    public Task<UpdateCheck> CheckAsync() => Task.FromResult(UpdateCheck.UpToDate);

    public Task<bool> DownloadAsync() => Task.FromResult(false);

    public void ApplyOnExit() { }

    public void ApplyAndRestart() { }
}
