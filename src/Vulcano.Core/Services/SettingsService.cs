using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in <see cref="AppPaths.DataDirectory"/>.
///
/// Reads from two places and writes to exactly one. On a machine that still has the WPF version
/// installed, its settings.json is the only place the user's preferences exist, so the first start
/// reads them from there - but never writes back, because this app's shape drops properties the
/// old one still needs. See <see cref="AppPaths.SettingsFilePath"/>.
///
/// Every write is best-effort: a settings change failing to persist must never take the app down
/// mid-session, least of all during a running ramp.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsFilePath;
    private readonly string? _legacySettingsFilePath;

    public SettingsService(string? settingsFilePath = null, string? legacySettingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? AppPaths.SettingsFilePath;
        _legacySettingsFilePath = legacySettingsFilePath ?? AppPaths.LegacySettingsFilePath;
    }

    public string SettingsFilePath => _settingsFilePath;

    public AppSettings Load()
    {
        var json = ReadOwnFile() ?? ReadLegacyFile();
        var settings = Deserialize(json);

        // The raw JSON travels along because a migration may need fields that no longer exist on
        // AppSettings - the WPF version's five ramp properties, for one.
        var migrated = AppSettingsMigration.Apply(settings, json);

        // Save on a first start too, so the file exists and the next start is a plain read.
        if (migrated || !File.Exists(_settingsFilePath))
        {
            Save(settings);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Best-effort persistence; failing to save a settings change should not crash the app.
        }
    }

    private string? ReadOwnFile() => TryRead(_settingsFilePath);

    private string? ReadLegacyFile() =>
        _legacySettingsFilePath is null ? null : TryRead(_legacySettingsFilePath);

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static AppSettings Deserialize(string? json)
    {
        if (json is null) return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
