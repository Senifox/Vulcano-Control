namespace Vulcano.Core.Models;

/// <summary>How the chart's time axis frames what it shows.</summary>
public enum TimeAxisMode
{
    /// <summary>Default: the axis snaps to the running ramp - start at the left, end at the right,
    /// "now" travelling through. Without a running ramp it falls back to a rolling 30 min window.</summary>
    FollowRun,
    Fixed5,
    Fixed15,
    Fixed60,
    Session
}
