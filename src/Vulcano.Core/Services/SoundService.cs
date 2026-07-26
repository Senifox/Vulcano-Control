using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

public enum AppSound
{
    /// <summary>The device reached its target temperature.</summary>
    HeatReached,

    /// <summary>The device's auto shut-off kicked in, or a ramp finished.</summary>
    Shutdown
}

/// <summary>
/// Plays one of the app's bundled sounds. WPF's MediaPlayer does not exist outside Windows, so the
/// actual playback lives in the platform layer and only this interface reaches the core.
/// </summary>
public interface ISoundPlayer
{
    void Play(AppSound sound);
}

/// <summary>Used until a real player is wired up, and whenever audio output is unavailable.</summary>
public sealed class NullSoundPlayer : ISoundPlayer
{
    public void Play(AppSound sound)
    {
    }
}

/// <summary>Owns the "are sounds switched on" decision and the logging around it, so callers can
/// just say what happened and not worry about whether it should be audible.</summary>
public sealed class SoundService
{
    private readonly ISoundPlayer _player;
    private readonly LogService _logService;

    public bool SoundEnabled { get; set; }

    public SoundService(ISoundPlayer player, LogService logService)
    {
        _player = player;
        _logService = logService;
    }

    public void PlayHeatReached() => Play(AppSound.HeatReached);

    public void PlayShutdown() => Play(AppSound.Shutdown);

    private void Play(AppSound sound)
    {
        if (!SoundEnabled)
        {
            _logService.Log(Strings.Get("Log.Sound.Skipped", sound), LogLevel.Debug);
            return;
        }

        _logService.Log(Strings.Get("Log.Sound.Playing", sound), LogLevel.Debug);
        _player.Play(sound);
    }
}
