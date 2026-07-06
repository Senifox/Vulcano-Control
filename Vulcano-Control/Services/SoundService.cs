using System.Windows.Media;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

public sealed class SoundService
{
    private readonly LogService _logService;
    private readonly MediaPlayer _heatReachedPlayer = new();
    private readonly MediaPlayer _shutdownPlayer = new();

    public bool SoundEnabled { get; set; }

    public SoundService(LogService logService)
    {
        _logService = logService;

        _heatReachedPlayer.MediaOpened += (_, _) => _logService.Log("Sound geladen: Heat-Reached.mp3.", LogLevel.Debug);
        _heatReachedPlayer.MediaFailed += (_, e) =>
            _logService.Log($"Sound konnte nicht geladen werden (Heat-Reached.mp3): {e.ErrorException.Message}", LogLevel.Warning);

        _shutdownPlayer.MediaOpened += (_, _) => _logService.Log("Sound geladen: Shutdown.mp3.", LogLevel.Debug);
        _shutdownPlayer.MediaFailed += (_, e) =>
            _logService.Log($"Sound konnte nicht geladen werden (Shutdown.mp3): {e.ErrorException.Message}", LogLevel.Warning);

        _heatReachedPlayer.Open(new Uri("pack://siteoforigin:,,,/Sounds/Heat-Reached.mp3"));
        _shutdownPlayer.Open(new Uri("pack://siteoforigin:,,,/Sounds/Shutdown.mp3"));
    }

    public void PlayHeatReached() => Play(_heatReachedPlayer, "Heat-Reached.mp3");

    public void PlayShutdown() => Play(_shutdownPlayer, "Shutdown.mp3");

    private void Play(MediaPlayer player, string name)
    {
        if (!SoundEnabled)
        {
            _logService.Log($"Sound übersprungen (Sound-Effekte deaktiviert): {name}.", LogLevel.Debug);
            return;
        }

        _logService.Log($"Sound abspielen: {name}.", LogLevel.Debug);
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }
}
