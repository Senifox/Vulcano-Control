using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public sealed class SoundServiceTests : IDisposable
{
    private sealed class RecordingPlayer : ISoundPlayer
    {
        public List<AppSound> Played { get; } = new();

        public void Play(AppSound sound) => Played.Add(sound);
    }

    private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"vulcano-sound-{Guid.NewGuid():N}.log");
    private readonly RecordingPlayer _player = new();
    private readonly LogService _log;
    private readonly SoundService _sound;

    public SoundServiceTests()
    {
        _log = new LogService(_logFile);
        _sound = new SoundService(_player, _log, TimeSpan.FromMilliseconds(80)) { SoundEnabled = true };
    }

    public void Dispose()
    {
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_sound_is_played_when_sounds_are_on()
    {
        _sound.PlayHeatReached();

        Assert.Equal(AppSound.HeatReached, Assert.Single(_player.Played));
    }

    [Fact]
    public void Nothing_is_played_when_sounds_are_off()
    {
        _sound.SoundEnabled = false;

        _sound.PlayHeatReached();
        _sound.PlayShutdown();

        Assert.Empty(_player.Played);
    }

    /// <summary>
    /// A finishing ramp is noticed twice - the ramp reports it, and the heater it switched off
    /// reports it again - and both mean the same thing. Two calls a millisecond apart used to restart
    /// the chime a few milliseconds in, which sounds like a fault rather than a notification.
    /// </summary>
    [Fact]
    public void The_same_sound_twice_in_a_row_is_played_once()
    {
        _sound.PlayShutdown();
        _sound.PlayShutdown();

        Assert.Equal(AppSound.Shutdown, Assert.Single(_player.Played));
    }

    [Fact]
    public void A_different_sound_is_not_swallowed()
    {
        _sound.PlayShutdown();
        _sound.PlayHeatReached();

        Assert.Equal([AppSound.Shutdown, AppSound.HeatReached], _player.Played);
    }

    [Fact]
    public async Task The_same_sound_plays_again_once_the_moment_has_passed()
    {
        _sound.PlayShutdown();
        await Task.Delay(140);
        _sound.PlayShutdown();

        Assert.Equal([AppSound.Shutdown, AppSound.Shutdown], _player.Played);
    }
}
