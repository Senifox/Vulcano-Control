using Xunit;

namespace Vulcano.TestSupport;

/// <summary>
/// Polls for something that happens on another thread. Everything under test here - ramp ticks,
/// socket reads, event fan-out - reports on a background thread, so a test that asserts straight
/// after acting is asserting on a race. Polling with a deadline gives a failure that names what it
/// was waiting for instead of an assertion that is wrong roughly one run in twenty.
/// </summary>
public static class Wait
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task ForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }
}
