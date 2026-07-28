using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

/// <summary>
/// Reading CHANGELOG.md. The file is written by hand, in markdown, and has to stay readable on
/// GitHub - so the parser meets whatever a person types rather than a format a tool guarantees.
/// </summary>
public sealed class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Some prose about the file, which is not a version and not an item.

        ## Unreleased

        - Something not out yet.

        ## 2.3.0 — 2026-07-27

        - The host shows the round trip to each machine.
        - A second thing.

        ## 2.1.0 — 2026-07-27

        - Updates install themselves when the app closes.
        """;

    [Fact]
    public void Every_version_is_found_in_the_order_it_is_written()
    {
        var entries = Changelog.Parse(Sample);

        Assert.Equal(["Unreleased", "2.3.0", "2.1.0"], entries.Select(e => e.Version));
    }

    [Fact]
    public void A_version_carries_its_date_and_its_items()
    {
        var entry = Changelog.Parse(Sample)[1];

        Assert.Equal("2.3.0", entry.Version);
        Assert.Equal("2026-07-27", entry.Date);
        Assert.Equal(2, entry.Items.Count);
        Assert.Equal("The host shows the round trip to each machine.", entry.Items[0]);
    }

    /// <summary>The prose at the top of the file belongs to no version and must not be swept into
    /// the first one.</summary>
    [Fact]
    public void Text_that_belongs_to_no_version_is_left_out()
    {
        var entries = Changelog.Parse(Sample);

        Assert.DoesNotContain(entries, e => e.Items.Any(i => i.Contains("prose")));
    }

    [Fact]
    public void The_unreleased_section_says_so()
    {
        var entries = Changelog.Parse(Sample);

        Assert.True(entries[0].IsUnreleased);
        Assert.False(entries[1].IsUnreleased);
    }

    /// <summary>
    /// The file is written to a column width, so a long item wraps. It is still one item - joining
    /// it back is the difference between a readable note and a sentence cut in half with the rest
    /// hanging under it as a second bullet.
    /// </summary>
    [Fact]
    public void An_item_wrapped_across_lines_stays_one_item()
    {
        var entries = Changelog.Parse("""
            ## 1.0.0 — 2026-01-01

            - A long sentence that did not fit on one line and
              carries on underneath it.
            - A short one.
            """);

        var items = entries[0].Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("A long sentence that did not fit on one line and carries on underneath it.", items[0]);
    }

    [Theory]
    [InlineData("## 2.3.0 — 2026-07-27", "2.3.0", "2026-07-27")]
    [InlineData("## 2.3.0 - 2026-07-27", "2.3.0", "2026-07-27")]
    [InlineData("## 2.3.0", "2.3.0", "")]
    [InlineData("## 2.0.0-preview.3 — 2026-07-01", "2.0.0-preview.3", "2026-07-01")]
    public void A_heading_splits_into_a_version_and_a_date(string heading, string version, string date)
    {
        var entry = Assert.Single(Changelog.Parse($"{heading}\n\n- A change."));

        Assert.Equal(version, entry.Version);
        Assert.Equal(date, entry.Date);
    }

    /// <summary>
    /// An Unreleased heading with nothing under it is what the file looks like from the moment a
    /// release is cut until the next change lands. It is not an entry and not a mistake.
    /// </summary>
    [Fact]
    public void A_heading_with_nothing_under_it_is_not_an_entry()
    {
        var entries = Changelog.Parse("""
            ## Unreleased

            ## 2.4.0 — 2026-07-27

            - Something that shipped.
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("2.4.0", entry.Version);
    }

    [Fact]
    public void Nothing_at_all_is_no_entries_rather_than_a_failure()
    {
        Assert.Empty(Changelog.Parse(""));
        Assert.Empty(Changelog.Parse("# Changelog\n\nNothing has happened yet."));
    }

    // --- The file that actually ships ---

    /// <summary>
    /// The changelog is embedded in this assembly, so this is both a check of the loading and of the
    /// file itself: a heading typed wrongly, or a version section left without items, shows up here
    /// rather than as an empty panel in the app.
    /// </summary>
    [Fact]
    public void The_shipped_changelog_reads()
    {
        var entries = Changelog.Entries;

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.NotEmpty(e.Items));
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
    }

    /// <summary>Every released version has a date; only the unreleased section may be without one.</summary>
    [Fact]
    public void Every_released_version_in_the_shipped_file_is_dated()
    {
        foreach (var entry in Changelog.Entries.Where(e => !e.IsUnreleased))
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Date), $"{entry.Version} has no date");
        }
    }

    [Fact]
    public void A_version_that_is_not_in_the_file_is_null_rather_than_a_guess()
    {
        Assert.Null(Changelog.For("99.0.0"));
    }
}
