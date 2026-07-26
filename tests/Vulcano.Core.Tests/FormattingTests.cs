using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(72, "1:12")]
    [InlineData(372, "6:12")]
    [InlineData(1634, "27:14")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(45296, "12:34:56")]
    public void Durations_never_carry_a_leading_zero_hour(int seconds, string expected)
    {
        Assert.Equal(expected, Formatting.Duration(seconds));
    }

    [Fact]
    public void A_negative_duration_reads_as_zero_rather_than_counting_backwards()
    {
        Assert.Equal("0:00", Formatting.Duration(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void A_value_and_its_unit_are_joined_by_a_narrow_no_break_space()
    {
        Assert.Equal($"195{Formatting.NarrowNoBreakSpace}°C", Formatting.Celsius(195));
        Assert.DoesNotContain(' ', Formatting.Celsius(195));
    }

    [Theory]
    [InlineData(193.4, "193")]
    [InlineData(193.5, "194")]
    public void Temperatures_are_shown_in_whole_degrees(double value, string expected)
    {
        Assert.StartsWith(expected, Formatting.Celsius(value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "+2")]
    [InlineData(-3, "-3")]
    [InlineData(0, "0")]
    public void A_temperature_difference_keeps_its_sign(double delta, string expected)
    {
        Assert.StartsWith(expected, Formatting.Kelvin(delta), StringComparison.Ordinal);
    }
}
