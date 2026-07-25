using System.Text.Json.Serialization;

namespace Vulcano.Core.Models;

/// <summary>UI language. English is the default; the JSON names match the culture codes the
/// resource lookup uses, so settings.json reads "en"/"de" rather than "English"/"German".</summary>
public enum AppLanguage
{
    [JsonStringEnumMemberName("en")]
    English,

    [JsonStringEnumMemberName("de")]
    German
}
