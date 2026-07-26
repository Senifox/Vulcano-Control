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
    private readonly TimeSpan _repeatWindow;

    private AppSound? _lastPlayed;
    private DateTime _lastPlayedAtUtc;

    public bool SoundEnabled { get; set; }

    /// <param name="repeatWindow">How close together the same sound has to be asked for twice before
    /// the second one is treated as an echo of the first. One event can be noticed in two places -
    /// a ramp finishing also switches the heater off, and both mean "it is done" - and the result
    /// was the chime restarting itself a few milliseconds in.</param>
    public SoundService(ISoundPlayer player, LogService logService, TimeSpan? repeatWindow = null)
    {
        _player = player;
        _logService = logService;
        _repeatWindow = repeatWindow ?? TimeSpan.FromSeconds(1);
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

        var now = DateTime.UtcNow;
        if (_lastPlayed == sound && now - _lastPlayedAtUtc < _repeatWindow)
        {
            _logService.Log(Strings.Get("Log.Sound.Echo", sound), LogLevel.Debug);
            return;
        }

        _lastPlayed = sound;
        _lastPlayedAtUtc = now;

        _logService.Log(Strings.Get("Log.Sound.Playing", sound), LogLevel.Debug);
        _player.Play(sound);
    }
}
