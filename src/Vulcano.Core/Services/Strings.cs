using System.Globalization;
using System.Resources;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// The one string table, for the core as well as the interface. Keys are the dotted names from the
/// handoff (<c>Tab.Control</c>, <c>Log.RampStarted</c>), looked up through a ResourceManager rather
/// than a generated class - a generated class cannot have dots in its property names, and the dotted
/// keys are worth keeping because they match the design document one for one.
///
/// Log lines go through here too. Log *levels* deliberately do not: they appear verbatim in the
/// exported file, and a translated level would not match what anyone quotes in a bug report.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("Vulcano.Core.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>The culture every lookup uses. Set through <see cref="Use"/>.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>
    /// Switches the language for everything that goes through this class, including background
    /// threads - which is where most log lines come from.
    /// </summary>
    public static void Use(AppLanguage language)
    {
        Culture = CultureInfo.GetCultureInfo(language == AppLanguage.German ? "de" : "en");

        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>The string for a key, or the key itself when it is missing - a visible
    /// <c>Ramp.Missing.Key</c> in the interface is easier to chase than an empty label.</summary>
    public static string Get(string key) => Manager.GetString(key, Culture) ?? key;

    /// <summary>Formats a string that has placeholders, in the current culture.</summary>
    public static string Get(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    /// <summary>Every key, for pushing the table into a UI framework's resources in one go.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All()
    {
        var set = Manager.GetResourceSet(Culture, createIfNotExists: true, tryParents: true);
        if (set is null) yield break;

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }
}
