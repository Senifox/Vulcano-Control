using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

/// <summary>
/// Loading, saving, and the chain of places settings have lived: the current file first, then this
/// app's own earlier file, then the WPF version's - each in a directory of its own here, as they are
/// on a real machine.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"vulcano-settings-{Guid.NewGuid():N}");

    /// <summary>%AppData%\Vulcano-Control\settings.json - the only file ever written.</summary>
    private readonly string _own;

    /// <summary>%LocalAppData%\Vulcano-Control\settings.v2.json - this app before it moved out.</summary>
    private readonly string _previous;

    /// <summary>%LocalAppData%\Vulcano-Control\settings.json - the WPF version.</summary>
    private readonly string _legacy;

    /// <summary>A settings.json as the WPF version writes it.</summary>
    private const string LegacyJson = """
        {
          "Theme": "Dark",
          "HistoryRetentionMinutes": 45,
          "RampPushThresholdCelsius": 2,
          "SoundEnabled": false,
          "RampDurationMinutes": 35,
          "RampStartTemperatureCelsius": 190,
          "RampEndTemperatureCelsius": 215,
          "RampInterpolationMethod": "EaseInOut",
          "RampHoldMinutes": 4,
          "PredefinedTemperatures": [ 181, 191 ],
          "RelayServerPort": 5010,
          "RelayPin": "4827",
          "RelayLastHostAddress": "192.168.1.9"
        }
        """;

    public SettingsServiceTests()
    {
        var current = Path.Combine(_directory, "roaming");
        var old = Path.Combine(_directory, "local");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(old);

        _own = Path.Combine(current, "settings.json");
        _previous = Path.Combine(old, "settings.v2.json");
        _legacy = Path.Combine(old, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
    }

    private SettingsService CreateService() => new(_own, [_previous, _legacy]);

    [Fact]
    public void A_first_start_picks_up_the_WPF_versions_settings()
    {
        File.WriteAllText(_legacy, LegacyJson);

        var settings = CreateService().Load();

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(45, settings.HistoryRetentionMinutes);
        Assert.Equal(2, settings.RampPushThresholdCelsius);
        Assert.False(settings.SoundEnabled);
        Assert.Equal([181, 191], settings.PredefinedTemperatures);
        Assert.Equal("4827", settings.RelayPin);
    }

    [Fact]
    public void The_old_ramp_comes_across_as_a_profile()
    {
        File.WriteAllText(_legacy, LegacyJson);

        var settings = CreateService().Load();

        var profile = Assert.Single(settings.RampProfiles);
        Assert.Equal(4, profile.HoldMinutes);
        Assert.Equal(190, profile.Points[0].Celsius);
        Assert.Equal(CurveKind.EaseInOut, profile.Points[0].CurveToNext);
        Assert.Equal(35, profile.Points[1].TimeMinutes);
        Assert.Equal(215, profile.Points[1].Celsius);
    }

    [Fact]
    public void The_WPF_versions_file_is_never_written_to()
    {
        File.WriteAllText(_legacy, LegacyJson);
        var before = File.ReadAllText(_legacy);

        var service = CreateService();
        var settings = service.Load();
        settings.Theme = AppTheme.Light;
        service.Save(settings);

        Assert.Equal(before, File.ReadAllText(_legacy));
        Assert.True(File.Exists(_own));
    }

    [Fact]
    public void Once_our_own_file_exists_the_old_one_is_ignored()
    {
        File.WriteAllText(_legacy, LegacyJson);
        var service = CreateService();

        var settings = service.Load();
        settings.HistoryRetentionMinutes = 99;
        service.Save(settings);

        // Someone goes back to the WPF app and changes something there - we must not pick it up.
        File.WriteAllText(_legacy, LegacyJson.Replace("\"HistoryRetentionMinutes\": 45", "\"HistoryRetentionMinutes\": 7"));

        Assert.Equal(99, CreateService().Load().HistoryRetentionMinutes);
    }

    /// <summary>
    /// The move out of the install directory: what this app itself last wrote is nearer than what the
    /// WPF version left behind, so a machine that has both must come up with the ramp profiles from
    /// the newer file.
    /// </summary>
    [Fact]
    public void The_apps_own_earlier_file_is_preferred_over_the_WPF_versions()
    {
        File.WriteAllText(_legacy, LegacyJson);
        File.WriteAllText(_previous, """
            { "Theme": "Light", "HistoryRetentionMinutes": 12 }
            """);

        var settings = CreateService().Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.Equal(12, settings.HistoryRetentionMinutes);
    }

    /// <summary>
    /// Nothing in the old home is written to, however it was found - the installer may empty that
    /// folder at any time, and a copy left intact is what somebody going back to the older version
    /// would start from.
    /// </summary>
    [Fact]
    public void The_earlier_file_is_read_once_and_left_alone()
    {
        File.WriteAllText(_previous, """
            { "Theme": "Light" }
            """);
        var before = File.ReadAllText(_previous);

        var service = CreateService();
        var settings = service.Load();
        settings.Theme = AppTheme.Dark;
        service.Save(settings);

        Assert.Equal(before, File.ReadAllText(_previous));
        Assert.True(File.Exists(_own));
        Assert.Equal(AppTheme.Dark, CreateService().Load().Theme);
    }

    [Fact]
    public void A_clean_machine_gets_defaults_and_a_file()
    {
        var settings = CreateService().Load();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(File.Exists(_own));
        Assert.Single(settings.RampProfiles);
    }

    [Fact]
    public void An_unreadable_file_falls_back_to_defaults_rather_than_throwing()
    {
        File.WriteAllText(_own, "{ this is not json");

        var settings = CreateService().Load();

        Assert.Equal(AppTheme.System, settings.Theme);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var service = CreateService();
        var settings = service.Load();
        settings.Language = AppLanguage.German;
        settings.TimeAxisMode = TimeAxisMode.Session;
        settings.HostOnStart = true;
        service.Save(settings);

        var reloaded = CreateService().Load();

        Assert.Equal(AppLanguage.German, reloaded.Language);
        Assert.Equal(TimeAxisMode.Session, reloaded.TimeAxisMode);
        Assert.True(reloaded.HostOnStart);
    }
}
