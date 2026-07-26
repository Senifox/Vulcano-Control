using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in <see cref="AppPaths.DataDirectory"/>.
///
/// Reads from wherever settings have ever lived and writes to exactly one place. Each earlier home
/// is tried in turn until one answers, and none of them is ever written back to: the first save goes
/// to the current location, so an older copy stays intact for anyone who wants to go back to the
/// version that wrote it.
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
    private readonly IReadOnlyList<string> _previousFilePaths;

    /// <param name="previousFilePaths">Where to look when the current file is not there, in order.
    /// Null means the real ones; an empty list means nowhere, which is what a test wants so it
    /// cannot accidentally read the settings of whoever is running it.</param>
    public SettingsService(string? settingsFilePath = null, IReadOnlyList<string>? previousFilePaths = null)
    {
        _settingsFilePath = settingsFilePath ?? AppPaths.SettingsFilePath;
        _previousFilePaths = previousFilePaths ?? AppPaths.PreviousSettingsFilePaths;
    }

    public string SettingsFilePath => _settingsFilePath;

    public AppSettings Load()
    {
        var json = ReadOwnFile() ?? ReadPreviousFile();
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

    /// <summary>The newest of the earlier homes that still holds something, tried in order.</summary>
    private string? ReadPreviousFile()
    {
        foreach (var path in _previousFilePaths)
        {
            if (TryRead(path) is { } json) return json;
        }

        return null;
    }

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
