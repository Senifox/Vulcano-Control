using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>Why a name was refused, so the caller can say which of the two it was.</summary>
public enum ProfileNameIssue
{
    None,
    Empty,
    AlreadyTaken
}

/// <summary>
/// The rules for keeping a set of named ramp profiles: adding one, renaming one, throwing one away.
/// Kept away from the view model because none of it is about the screen - which name a duplicate
/// gets, what happens to the selection when the profile under it disappears, and the fact that the
/// list must never run empty are decisions, and decisions are worth testing.
///
/// Operates on the caller's list in place. The view model holds an ObservableCollection so the
/// window follows along, and the settings hold a plain list; both are the same objects.
/// </summary>
public static class RampProfileLibrary
{
    /// <summary>What a profile starts as when there is nothing to copy: a short, valid ramp that can
    /// be run as it stands, so a new profile is never a validation error waiting to be fixed.</summary>
    public static RampProfile CreateDefault(string name) => new()
    {
        Name = name,
        Points =
        [
            new RampPoint(0, 180, CurveKind.Linear),
            new RampPoint(10, 200, CurveKind.Linear),
        ],
        HoldMinutes = 5,
    };

    /// <summary>
    /// Adds a profile and returns it. With <paramref name="copyOf"/> the points and hold are taken
    /// from that profile - "new" is far more often "this one but a bit different" than a blank one.
    /// </summary>
    public static RampProfile Add(IList<RampProfile> profiles, string desiredName, RampProfile? copyOf = null)
    {
        var name = MakeUnique(profiles, desiredName, exclude: null);

        var profile = copyOf is null
            ? CreateDefault(name)
            : new RampProfile { Name = name, Points = copyOf.Points.ToList(), HoldMinutes = copyOf.HoldMinutes };

        profiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// Renames a profile, or says why not. A name that only differs in case from the profile's own is
    /// accepted - that is a correction, not a collision.
    /// </summary>
    public static ProfileNameIssue Rename(IList<RampProfile> profiles, RampProfile profile, string newName)
    {
        var trimmed = newName.Trim();

        if (trimmed.Length == 0) return ProfileNameIssue.Empty;

        if (profiles.Any(p => !ReferenceEquals(p, profile) && Matches(p.Name, trimmed)))
        {
            return ProfileNameIssue.AlreadyTaken;
        }

        profile.Name = trimmed;
        return ProfileNameIssue.None;
    }

    /// <summary>
    /// Removes a profile and returns the one that should be selected in its place - the next one
    /// along, or the previous when the last was removed.
    ///
    /// Refuses to remove the only profile and returns null: the ramp editor always edits something,
    /// and an empty list would mean a tab with nothing in it and no way back.
    /// </summary>
    public static RampProfile? Remove(IList<RampProfile> profiles, RampProfile profile)
    {
        if (profiles.Count <= 1) return null;

        var index = profiles.IndexOf(profile);
        if (index < 0) return null;

        profiles.RemoveAt(index);

        return profiles[Math.Min(index, profiles.Count - 1)];
    }

    /// <summary>
    /// A name nothing else is using: "Ramp", then "Ramp 2", "Ramp 3". Counting from 2 because the
    /// first one has no number and "Ramp 1" beside a plain "Ramp" reads like a different scheme.
    ///
    /// A name that already ends in a number counts on from it rather than growing another one, so
    /// copying "Ramp 2" gives "Ramp 3". Appending blindly is how somebody ended up with
    /// "Imported 2 2 2" after three copies.
    /// </summary>
    public static string MakeUnique(IEnumerable<RampProfile> profiles, string desired, RampProfile? exclude)
    {
        var taken = profiles.Where(p => !ReferenceEquals(p, exclude)).Select(p => p.Name).ToList();
        var trimmed = desired.Trim();
        if (trimmed.Length == 0) trimmed = "Ramp";

        if (!taken.Any(n => Matches(n, trimmed))) return trimmed;

        var (stem, next) = SplitTrailingNumber(trimmed);

        for (var i = next; ; i++)
        {
            var candidate = $"{stem} {i}";
            if (!taken.Any(n => Matches(n, candidate))) return candidate;
        }
    }

    /// <summary>
    /// Splits "Ramp 2" into "Ramp" and the number to try next. A name without a trailing number
    /// keeps all of itself and starts at 2. The space is required, so "Mix2" stays "Mix2" - a digit
    /// stuck to a word is part of the word.
    /// </summary>
    private static (string Stem, int Next) SplitTrailingNumber(string name)
    {
        var space = name.LastIndexOf(' ');
        if (space <= 0) return (name, 2);

        var tail = name[(space + 1)..];

        return int.TryParse(tail, out var number) && number > 0
            ? (name[..space], number + 1)
            : (name, 2);
    }

    /// <summary>Names are compared the way a person would compare them: case and surrounding space
    /// are not what makes two profiles different.</summary>
    private static bool Matches(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
