using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class RampProfileLibraryTests
{
    private static List<RampProfile> Library(params string[] names) =>
        names.Select(RampProfileLibrary.CreateDefault).ToList();

    // --- Adding ---

    [Fact]
    public void A_new_profile_is_valid_as_it_stands()
    {
        var profiles = Library();

        var added = RampProfileLibrary.Add(profiles, "Evening");

        Assert.Equal("Evening", added.Name);
        Assert.True(
            TemperatureRampPlan.TryCreate(added.Points, TimeSpan.FromMinutes(added.HoldMinutes), out _, out var errors),
            $"a fresh profile should not need fixing, but: {string.Join(", ", errors.Select(e => e.Issue))}");
    }

    [Fact]
    public void A_second_profile_of_the_same_name_is_numbered()
    {
        var profiles = Library("Ramp");

        var second = RampProfileLibrary.Add(profiles, "Ramp");
        var third = RampProfileLibrary.Add(profiles, "Ramp");

        Assert.Equal("Ramp 2", second.Name);
        Assert.Equal("Ramp 3", third.Name);
    }

    /// <summary>
    /// Copying a copy counts on instead of growing another number. Reported from a real session as
    /// "Imported 2 2 2" after three copies, which is what appending blindly produces.
    /// </summary>
    [Fact]
    public void Copying_a_copy_counts_on_rather_than_appending_again()
    {
        var profiles = Library("Imported");

        var second = RampProfileLibrary.Add(profiles, "Imported");
        var third = RampProfileLibrary.Add(profiles, second.Name);
        var fourth = RampProfileLibrary.Add(profiles, third.Name);

        Assert.Equal(["Imported", "Imported 2", "Imported 3", "Imported 4"], profiles.Select(p => p.Name));
        Assert.Equal("Imported 4", fourth.Name);
    }

    [Fact]
    public void A_number_that_is_part_of_the_word_is_left_alone()
    {
        // "Mix2" is a name, not a numbered copy - the space is what makes it a number.
        var profiles = Library("Mix2");

        Assert.Equal("Mix2 2", RampProfileLibrary.Add(profiles, "Mix2").Name);
    }

    [Fact]
    public void Counting_on_skips_the_numbers_already_taken()
    {
        var profiles = Library("Evening", "Evening 2", "Evening 3");

        Assert.Equal("Evening 4", RampProfileLibrary.Add(profiles, "Evening 2").Name);
    }

    [Fact]
    public void Copying_takes_the_points_and_the_hold_but_not_the_name()
    {
        var profiles = Library("Evening");
        profiles[0].Points = [new RampPoint(0, 190, CurveKind.Steep), new RampPoint(20, 215, CurveKind.Linear)];
        profiles[0].HoldMinutes = 8;

        var copy = RampProfileLibrary.Add(profiles, "Evening", copyOf: profiles[0]);

        Assert.Equal("Evening 2", copy.Name);
        Assert.Equal(8, copy.HoldMinutes);
        Assert.Equal(profiles[0].Points, copy.Points);

        // A copy, not a second reference: editing one must not edit the other.
        copy.Points.Add(new RampPoint(30, 220, CurveKind.Linear));
        Assert.Equal(2, profiles[0].Points.Count);
    }

    // --- Renaming ---

    [Fact]
    public void Renaming_trims_and_keeps_the_profile()
    {
        var profiles = Library("Evening");

        var issue = RampProfileLibrary.Rename(profiles, profiles[0], "  Late evening  ");

        Assert.Equal(ProfileNameIssue.None, issue);
        Assert.Equal("Late evening", profiles[0].Name);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        var profiles = Library("Evening");

        Assert.Equal(ProfileNameIssue.Empty, RampProfileLibrary.Rename(profiles, profiles[0], "   "));
        Assert.Equal("Evening", profiles[0].Name);
    }

    [Fact]
    public void A_name_another_profile_already_has_is_refused()
    {
        var profiles = Library("Evening", "Morning");

        Assert.Equal(ProfileNameIssue.AlreadyTaken, RampProfileLibrary.Rename(profiles, profiles[1], "evening"));
        Assert.Equal("Morning", profiles[1].Name);
    }

    /// <summary>Fixing the capitalisation of a profile's own name is not a collision with itself.</summary>
    [Fact]
    public void A_profile_may_be_renamed_to_a_different_spelling_of_itself()
    {
        var profiles = Library("evening");

        Assert.Equal(ProfileNameIssue.None, RampProfileLibrary.Rename(profiles, profiles[0], "Evening"));
        Assert.Equal("Evening", profiles[0].Name);
    }

    // --- Removing ---

    [Fact]
    public void Removing_selects_the_next_profile_along()
    {
        var profiles = Library("One", "Two", "Three");

        var next = RampProfileLibrary.Remove(profiles, profiles[1]);

        Assert.Equal("Three", next!.Name);
        Assert.Equal(["One", "Three"], profiles.Select(p => p.Name));
    }

    [Fact]
    public void Removing_the_last_one_falls_back_to_the_one_before_it()
    {
        var profiles = Library("One", "Two");

        var next = RampProfileLibrary.Remove(profiles, profiles[1]);

        Assert.Equal("One", next!.Name);
    }

    /// <summary>
    /// The ramp editor always edits something. An empty list would leave the tab showing nothing,
    /// with no way to get a profile back except by editing the settings file.
    /// </summary>
    [Fact]
    public void The_only_profile_cannot_be_removed()
    {
        var profiles = Library("One");

        Assert.Null(RampProfileLibrary.Remove(profiles, profiles[0]));
        Assert.Single(profiles);
    }

    [Fact]
    public void Removing_something_that_is_not_in_the_list_changes_nothing()
    {
        var profiles = Library("One", "Two");

        Assert.Null(RampProfileLibrary.Remove(profiles, RampProfileLibrary.CreateDefault("Elsewhere")));
        Assert.Equal(2, profiles.Count);
    }
}
