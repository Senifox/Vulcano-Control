using Velopack;
using Velopack.Sources;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Checks the project's GitHub Releases for newer packaged builds via Velopack, and downloads
/// and applies them on request. A no-op outside of a Velopack-installed deployment (e.g. when
/// running via the debugger), since UpdateManager needs to know its own install location.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/Senifox/Vulcano-Control";

    private readonly LogService _logService;
    private readonly UpdateManager _manager;

    public UpdateService(LogService logService)
    {
        _logService = logService;
        _manager = new UpdateManager(new GithubSource(RepoUrl, string.Empty, false));
    }

    /// <summary>
    /// The currently installed Velopack release version (e.g. "v 1.0.5"), or an empty string
    /// outside of a Velopack-installed deployment (e.g. when running via the debugger).
    /// </summary>
    public string CurrentVersionDisplay =>
        _manager.IsInstalled && _manager.CurrentVersion is not null ? $"v {_manager.CurrentVersion}" : string.Empty;

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!_manager.IsInstalled)
        {
            _logService.Log("Update-Check übersprungen (keine Velopack-Installation, z.B. im Debugger).", LogLevel.Debug);
            return null;
        }

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            _logService.Log(info is null
                ? "Kein Update verfügbar."
                : $"Update verfügbar: Version {info.TargetFullRelease.Version}.");
            return info;
        }
        catch (Exception ex)
        {
            _logService.Log($"Update-Check fehlgeschlagen: {ex.Message}", LogLevel.Warning);
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        try
        {
            _logService.Log($"Lade Update {info.TargetFullRelease.Version} herunter...");
            await _manager.DownloadUpdatesAsync(info);
            _logService.Log("Update heruntergeladen, Neustart der Anwendung...");
            _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _logService.Log($"Update-Installation fehlgeschlagen: {ex.Message}", LogLevel.Error);
        }
    }
}
