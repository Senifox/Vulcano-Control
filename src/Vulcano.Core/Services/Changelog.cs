using System.Reflection;

namespace Vulcano.Core.Services;

/// <summary>One released version and what changed in it.</summary>
/// <param name="Version">As written in the heading - "2.3.0", or "Unreleased" for the section
/// collecting what is not out yet.</param>
/// <param name="Date">The date beside it, or empty when there is none.</param>
public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Items)
{
    /// <summary>True for the section describing work that has not been released.</summary>
    public bool IsUnreleased => Version.Equals("Unreleased", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads CHANGELOG.md, which ships inside the assembly.
///
/// Embedded rather than fetched: the app updates itself in the background now, so the one thing a
/// changelog must get right is describing the version that is actually running. A copy pulled from
/// somewhere at display time would sooner or later show notes for a release this install is not.
///
/// The format is deliberately the plain markdown the file already had to be, so it stays readable
/// on its own and can be written by hand: a "## " heading per version, "- " lines under it.
/// Anything else in the file - the title, the note at the top - is skipped rather than reported as
/// a problem, because a changelog with a paragraph of prose in it is still a good changelog.
/// </summary>
public static class Changelog
{
    private const string ResourceName = "Vulcano.Core.CHANGELOG.md";

    private static readonly Lazy<IReadOnlyList<ChangelogEntry>> Loaded = new(LoadFromAssembly);

    /// <summary>Every version in the file, newest first - the order they are written in.</summary>
    public static IReadOnlyList<ChangelogEntry> Entries => Loaded.Value;

    /// <summary>What changed in one version, or null when it is not in the file. Used for the note
    /// shown once after an update, which must say nothing rather than something vague.</summary>
    public static ChangelogEntry? For(string version) =>
        Entries.FirstOrDefault(e => e.Version == version);

    /// <summary>
    /// Splits a changelog into its versions. Public so it can be tested against text a test writes,
    /// rather than only against the file that happens to ship today.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();

        string? version = null;
        var date = "";
        var items = new List<string>();

        void Flush()
        {
            if (version is null) return;

            entries.Add(new ChangelogEntry(version, date, items));
            items = new List<string>();
        }

        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                (version, date) = SplitHeading(line[3..].Trim());
                continue;
            }

            // A heading of any other level ends the current version without starting one - the
            // file's own title, for instance, which comes before anything worth reporting.
            if (line.StartsWith('#'))
            {
                Flush();
                version = null;
                continue;
            }

            if (version is null) continue;

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(line[2..].Trim());
            }
            else if (line.Length > 0 && items.Count > 0)
            {
                // A wrapped continuation of the line above: the file is written to a column width,
                // and an item split across two lines is one item, not one item and some debris.
                items[^1] = $"{items[^1]} {line}";
            }
        }

        Flush();

        return entries;
    }

    /// <summary>"2.3.0 - 2026-07-27" into its two halves, tolerating any of the dashes a person
    /// might type and a heading with no date at all.</summary>
    private static (string Version, string Date) SplitHeading(string heading)
    {
        // The dash has to have a space in front of it to be a separator: "2.0.0-preview.3" has one
        // inside the version, and taking the first one found would cut the version in half.
        for (var i = 1; i < heading.Length; i++)
        {
            if (heading[i] is not ('—' or '–' or '-')) continue;
            if (!char.IsWhiteSpace(heading[i - 1])) continue;

            return (heading[..i].Trim(), heading[(i + 1)..].Trim());
        }

        return (heading, "");
    }

    private static IReadOnlyList<ChangelogEntry> LoadFromAssembly()
    {
        try
        {
            using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return [];

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            // A missing or unreadable changelog is a missing changelog, not a reason to fail a start.
            return [];
        }
    }
}
