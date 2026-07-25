using System.Text.Json;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class AppSettingsMigrationTests
{
    /// <summary>A settings.json as the WPF version wrote it.</summary>
    private const string LegacyJson = """
        {
          "Theme": "Dark",
          "HistoryRetentionMinutes": 120,
          "RampPushThresholdCelsius": 1,
          "SoundEnabled": true,
          "RampDurationMinutes": 25,
          "RampStartTemperatureCelsius": 190,
          "RampEndTemperatureCelsius": 215,
          "RampInterpolationMethod": "SteepExponential",
          "RampHoldMinutes": 3,
          "PredefinedTemperatures": [ 180, 190 ],
          "RelayServerPort": 58642,
          "RelayPin": "4711",
          "RelayLastHostAddress": "192.168.1.42"
        }
        """;

    [Fact]
    public void The_old_single_ramp_becomes_a_two_point_profile()
    {
        var settings = new AppSettings();

        var changed = AppSettingsMigration.Apply(settings, LegacyJson);

        Assert.True(changed);
        var profile = Assert.Single(settings.RampProfiles);
        Assert.Equal(AppSettingsMigration.ImportedProfileName, profile.Name);
        Assert.Equal(3, profile.HoldMinutes);
        Assert.Collection(
            profile.Points,
            first =>
            {
                Assert.Equal(0, first.TimeMinutes);
                Assert.Equal(190, first.Celsius);
                Assert.Equal(CurveKind.Steep, first.CurveToNext);
            },
            last =>
            {
                Assert.Equal(25, last.TimeMinutes);
                Assert.Equal(215, last.Celsius);
            });
    }

    [Fact]
    public void The_imported_profile_is_the_one_that_opens()
    {
        var settings = new AppSettings();

        AppSettingsMigration.Apply(settings, LegacyJson);

        Assert.Equal(AppSettingsMigration.ImportedProfileName, settings.ActiveRampProfileName);
    }

    [Fact]
    public void The_imported_profile_is_a_valid_ramp()
    {
        var settings = new AppSettings();

        AppSettingsMigration.Apply(settings, LegacyJson);

        Assert.True(RampValidation.IsValid(settings.RampProfiles[0].Points, settings.RampProfiles[0].HoldMinutes));
    }

    [Fact]
    public void Migration_runs_once_and_leaves_a_version_behind()
    {
        var settings = new AppSettings();

        AppSettingsMigration.Apply(settings, LegacyJson);
        Assert.Equal(AppSettingsMigration.CurrentVersion, settings.SettingsVersion);

        // Deleting every profile afterwards must not resurrect the imported one.
        settings.RampProfiles.Clear();
        Assert.False(AppSettingsMigration.Apply(settings, LegacyJson));
        Assert.Empty(settings.RampProfiles);
    }

    [Fact]
    public void A_fresh_install_gets_a_usable_default_profile()
    {
        var settings = new AppSettings();

        AppSettingsMigration.Apply(settings, rawJson: null);

        var profile = Assert.Single(settings.RampProfiles);
        Assert.True(RampValidation.IsValid(profile.Points, profile.HoldMinutes));
    }

    [Fact]
    public void Existing_profiles_are_left_alone()
    {
        var settings = new AppSettings
        {
            RampProfiles =
            {
                new RampProfile { Name = "Mine", Points = { new RampPoint(0, 100), new RampPoint(5, 120) } },
            },
        };

        AppSettingsMigration.Apply(settings, LegacyJson);

        Assert.Equal("Mine", Assert.Single(settings.RampProfiles).Name);
    }

    [Fact]
    public void The_legacy_ramp_fields_disappear_from_the_saved_file()
    {
        var settings = new AppSettings();
        AppSettingsMigration.Apply(settings, LegacyJson);

        var saved = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("RampStartTemperatureCelsius", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("RampInterpolationMethod", saved, StringComparison.Ordinal);
    }
}
