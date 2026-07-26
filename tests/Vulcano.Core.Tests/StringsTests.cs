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
    }
}
