using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Services;

/// <summary>
/// <see cref="IUpdateSource"/> over Velopack, reading this project's GitHub Releases.
///
/// Every call is wrapped: an update check runs unasked at startup, and a repository that is
/// unreachable, rate-limiting, or answering with something unexpected must not be able to take the
/// app down on its way up. Failures come back as text for the interface and a line in the log.
/// </summary>
public sealed class VelopackUpdateSource : IUpdateSource
{
    private const string RepoUrl = "https://github.com/Senifox/Vulcano-Control";

    private readonly LogService _log;
    private readonly UpdateManager _manager;

    /// <summary>What the last successful check found, kept because downloading and applying both
    /// need the asset it describes.</summary>
    private UpdateInfo? _found;

    public VelopackUpdateSource(LogService log)
    {
        _log = log;

        // No token: the repository is public, and an update check is not a reason to want one.
        // Prereleases are excluded - the preview builds during the rewrite were exactly the kind of
        // thing nobody should be moved onto by a background check.
        _manager = new UpdateManager(new GithubSource(RepoUrl, string.Empty, false));
    }

    public bool IsSupported => _manager.IsInstalled;

    public string? PendingVersion => _manager.UpdatePendingRestart?.Version?.ToString();

    public async Task<UpdateCheck> CheckAsync()
    {
        if (!IsSupported) return UpdateCheck.UpToDate;

        try
        {
            _found = await _manager.CheckForUpdatesAsync();

            if (_found is null)
            {
                _log.Log(Strings.Get("Log.Update.None"), LogLevel.Debug);
                return UpdateCheck.UpToDate;
            }

            var version = _found.TargetFullRelease.Version.ToString();
            _log.Log(Strings.Get("Log.Update.Found", version));
            return UpdateCheck.Found(version);
        }
        catch (Exception ex)
        {
            _log.Log(Strings.Get("Log.Update.Failed", ex.Message), LogLevel.Warning);
            return UpdateCheck.Failed(ex.Message);
        }
    }

    public async Task<bool> DownloadAsync()
    {
        if (_found is null) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(_found);
            _log.Log(Strings.Get("Log.Update.Downloaded", _found.TargetFullRelease.Version.ToString()));
            return true;
        }
        catch (Exception ex)
        {
            _log.Log(Strings.Get("Log.Update.Failed", ex.Message), LogLevel.Warning);
            return false;
        }
    }

    public void ApplyOnExit()
    {
        // Either the one just downloaded, or one from a session that ended before it was applied.
        var asset = _found?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (asset is null) return;

        try
        {
            _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
            _log.Log(Strings.Get("Log.Update.OnExit", asset.Version.ToString()));
        }
        catch (Exception ex)
        {
            _log.Log(Strings.Get("Log.Update.Failed", ex.Message), LogLevel.Warning);
        }
    }

    public void ApplyAndRestart()
    {
        var asset = _found?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (asset is null) return;

        try
        {
            _log.Log(Strings.Get("Log.Update.Restarting", asset.Version.ToString()));
            _manager.ApplyUpdatesAndRestart(asset);
        }
        catch (Exception ex)
        {
            _log.Log(Strings.Get("Log.Update.Failed", ex.Message), LogLevel.Warning);
        }
    }
}
