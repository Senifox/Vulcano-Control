using Avalonia;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Services;

/// <summary>
/// Puts the string table into the application's resources under an <c>S.</c> prefix, so a label can
/// say <c>{DynamicResource S.Tab.Control}</c> and follow a language change without being rebuilt.
///
/// A DynamicResource re-reads its value when the entry is reassigned, which is exactly what
/// switching languages needs - the alternative, a binding to an indexer, cannot carry dotted keys
/// through Avalonia's binding path syntax, and dotted keys are what match the design document.
/// </summary>
public static class Loc
{
    public const string Prefix = "S.";

    /// <summary>
    /// Raised after the table has been swapped. Labels bound with DynamicResource update themselves;
    /// this is for the strings a view model composes - a state name, a formatted sentence - which
    /// sit behind ordinary bindings and would otherwise keep the words they were built with.
    /// </summary>
    public static event EventHandler? LanguageChanged;

    public static void Apply(AppLanguage language)
    {
        Strings.Use(language);

        if (Application.Current is { } app)
        {
            foreach (var (key, value) in Strings.All())
            {
                app.Resources[Prefix + key] = value;
            }
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
}
