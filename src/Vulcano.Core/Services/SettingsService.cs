using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in <see cref="AppPaths.DataDirectory"/>.
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

    public SettingsService(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? AppPaths.SettingsFilePath;
    }

    public string SettingsFilePath => _settingsFilePath;

    public AppSettings Load()
    {
        string? json = null;
        AppSettings settings;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                json = File.ReadAllText(_settingsFilePath);
            }

            settings = json is null
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        // The raw JSON goes along because a migration may need fields that no longer exist on
        // AppSettings - the WPF version's five ramp properties, for one.
        if (AppSettingsMigration.Apply(settings, json) || json is null)
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
}
