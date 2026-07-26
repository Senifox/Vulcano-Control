using System.Text.RegularExpressions;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class StringsTests
{
    [Fact]
    public void English_is_the_default()
    {
        Strings.Use(AppLanguage.English);

        Assert.Equal("Control", Strings.Get("Tab.Control"));
        Assert.Equal("Start ramp", Strings.Get("Action.StartRamp"));
    }

    [Fact]
    public void German_comes_from_the_satellite_resources()
    {
        Strings.Use(AppLanguage.German);

        Assert.Equal("Steuerung", Strings.Get("Tab.Control"));
        Assert.Equal("Rampe starten", Strings.Get("Action.StartRamp"));

        Strings.Use(AppLanguage.English);
    }

    [Fact]
    public void A_missing_key_shows_itself_rather_than_nothing()
    {
        Assert.Equal("Not.A.Real.Key", Strings.Get("Not.A.Real.Key"));
    }

    [Fact]
    public void Placeholders_are_filled_in()
    {
        Strings.Use(AppLanguage.English);

        Assert.Equal("SEGMENT 2 → 3", Strings.Get("Ramp.Segment", 2, 3));
    }

    /// <summary>
    /// The two files have to agree on both the set of keys and the placeholders in each value.
    /// A German string with an extra {1} throws at runtime, in whatever screen happens to use it.
    /// </summary>
    [Fact]
    public void Both_languages_have_the_same_keys_and_the_same_placeholders()
    {
        Strings.Use(AppLanguage.English);
        var english = Strings.All().ToDictionary(p => p.Key, p => p.Value);

        Strings.Use(AppLanguage.German);
        var german = Strings.All().ToDictionary(p => p.Key, p => p.Value);

        Strings.Use(AppLanguage.English);

        Assert.Equal(english.Keys.OrderBy(k => k), german.Keys.OrderBy(k => k));

        foreach (var (key, value) in english)
        {
            Assert.Equal(Placeholders(value), Placeholders(german[key]));
        }
    }

    /// <summary>
    /// Reads the source and checks that every key handed to <see cref="Strings.Get(string)"/>
    /// actually exists. A missing key is not a crash - it renders as the key itself - so nothing
    /// catches it until someone reads the screen, and one of them (Log.TargetSet) got as far as a
    /// log line during a ramp on a real device before anyone noticed.
    /// </summary>
    [Fact]
    public void Every_key_used_in_the_source_exists_in_the_table()
    {
        Strings.Use(AppLanguage.English);
        var table = Strings.All().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (var match in Regex.Matches(File.ReadAllText(file), @"Strings\.Get\(""([^""]+)""").Cast<Match>())
            {
                var key = match.Groups[1].Value;
                if (!table.Contains(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>The repo's <c>src</c> directory, found by walking up from the test assembly - the
    /// build output sits several levels below it and the depth differs per configuration.</summary>
    private static string SourceRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vulcano-Control.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "src");
        }
    }

    private static IEnumerable<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(p => p);

    [Fact]
    public void The_table_covers_every_curve_and_every_validation_issue()
    {
        Strings.Use(AppLanguage.English);

        foreach (var curve in Enum.GetValues<CurveKind>())
        {
            Assert.NotEqual($"Ramp.Curve.{curve}", Strings.Get($"Ramp.Curve.{curve}"));
        }

        foreach (var issue in Enum.GetValues<RampValidationIssue>())
        {
            Assert.NotEqual($"Ramp.Invalid.{issue}", Strings.Get($"Ramp.Invalid.{issue}"));
        }

        // None is never shown - it is the answer when nothing went wrong - so it needs no text.
        foreach (var issue in Enum.GetValues<ProfileNameIssue>().Where(i => i != ProfileNameIssue.None))
        {
            Assert.NotEqual($"Ramp.Profile.{issue}", Strings.Get($"Ramp.Profile.{issue}"));
        }
    }
}
