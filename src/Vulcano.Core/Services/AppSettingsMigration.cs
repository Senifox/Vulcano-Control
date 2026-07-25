using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Brings older settings files up to the current shape. Runs once per version step, guarded by
/// <see cref="AppSettings.SettingsVersion"/>, so deleting all ramp profiles does not resurrect an
/// imported one on the next start.
/// </summary>
public static class AppSettingsMigration
{
    /// <summary>The shape this version of the app writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Name given to the profile built from the WPF version's single ramp.</summary>
    public const string ImportedProfileName = "Imported";

    /// <summary>
    /// The five top-level ramp fields the WPF version wrote. They no longer exist on
    /// <see cref="AppSettings"/>, so they are read straight out of the raw JSON here and then
    /// disappear from the file the first time it is saved.
    /// </summary>
    private sealed record LegacyRampShape
    {
        public int RampDurationMinutes { get; init; } = 40;
        public int RampStartTemperatureCelsius { get; init; } = 185;
        public int RampEndTemperatureCelsius { get; init; } = 225;
        public LegacyInterpolationMethod RampInterpolationMethod { get; init; } = LegacyInterpolationMethod.Linear;
        public int RampHoldMinutes { get; init; } = 5;
    }

    /// <summary>The old curve names, kept only so the values in an existing settings.json still
    /// parse. <c>SteepExponential</c> is what is now called <see cref="CurveKind.Steep"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LegacyInterpolationMethod>))]
    private enum LegacyInterpolationMethod
    {
        Linear,
        Exponential,
        SteepExponential,
        EaseInOut
    }

    /// <summary>
    /// Applies every outstanding migration. Returns true when something changed and the caller
    /// should save.
    /// </summary>
    public static bool Apply(AppSettings settings, string? rawJson)
    {
        if (settings.SettingsVersion >= CurrentVersion) return false;

        ImportLegacyRamp(settings, rawJson);

        settings.SettingsVersion = CurrentVersion;
        return true;
    }

    private static void ImportLegacyRamp(AppSettings settings, string? rawJson)
    {
        // Nothing to import into a file that already has profiles, and nothing to import from a
        // fresh install - which gets the default profile instead.
        if (settings.RampProfiles.Count > 0) return;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            settings.RampProfiles.Add(CreateDefaultProfile());
            settings.ActiveRampProfileName = settings.RampProfiles[0].Name;
            return;
        }

        LegacyRampShape? legacy = null;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyRampShape>(rawJson, LegacyJsonOptions);
        }
        catch
        {
            // A settings file we cannot read the old ramp out of just gets the default profile.
        }

        settings.RampProfiles.Add(legacy is null ? CreateDefaultProfile() : ToProfile(legacy));
        settings.ActiveRampProfileName = settings.RampProfiles[0].Name;
    }

    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The old ramp was a single curve from start to end, which is exactly a two-point
    /// profile: minute 0 at the start temperature, minute <c>duration</c> at the end temperature.</summary>
    private static RampProfile ToProfile(LegacyRampShape legacy) => new()
    {
        Name = ImportedProfileName,
        HoldMinutes = Math.Max(legacy.RampHoldMinutes, 0),
        Points =
        {
            new RampPoint(0, legacy.RampStartTemperatureCelsius, ToCurveKind(legacy.RampInterpolationMethod)),
            new RampPoint(Math.Max(legacy.RampDurationMinutes, 1), legacy.RampEndTemperatureCelsius),
        },
    };

    private static RampProfile CreateDefaultProfile() => new()
    {
        Name = ImportedProfileName,
        HoldMinutes = 5,
        Points =
        {
            new RampPoint(0, 185, CurveKind.Linear),
            new RampPoint(40, 225),
        },
    };

    private static CurveKind ToCurveKind(LegacyInterpolationMethod method) => method switch
    {
        LegacyInterpolationMethod.Linear => CurveKind.Linear,
        LegacyInterpolationMethod.Exponential => CurveKind.Exponential,
        LegacyInterpolationMethod.SteepExponential => CurveKind.Steep,
        LegacyInterpolationMethod.EaseInOut => CurveKind.EaseInOut,
        _ => CurveKind.Linear
    };
}
