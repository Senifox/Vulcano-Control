using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

public sealed class SettingsService
{
    // %LocalAppData%, not AppContext.BaseDirectory: Velopack installs each update into a new
    // versioned folder, so anything written next to the exe (the previous location of this file)
    // is silently left behind - and lost - on every update. LocalApplicationData is a stable
    // location independent of the currently installed version.
    private static readonly string SettingsFilePath = GetSettingsFilePath();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string GetSettingsFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vulcano-Control");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        // One-time migration, mainly for the portable .zip release (unpacked once and reused in
        // place across manual updates, unlike the Setup.exe installer where Velopack always
        // starts a fresh version folder with nothing to migrate from anyway) - copies over a
        // settings.json still sitting next to the exe from before this fix.
        if (!File.Exists(path))
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, path); } catch { /* best-effort */ }
            }
        }

        return path;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Best-effort persistence; failing to save a settings change should not crash the app.
        }
    }
}
