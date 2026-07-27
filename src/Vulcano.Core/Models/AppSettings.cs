namespace Vulcano.Core.Models;

public sealed class AppSettings
{
    /// <summary>Bumped by <see cref="Services.AppSettingsMigration"/> when it has brought an older
    /// file up to date, so a migration runs once and not again after the user edits things.</summary>
    public int SettingsVersion { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.English;

    public int HistoryRetentionMinutes { get; set; } = 120;
    public int RampPushThresholdCelsius { get; set; } = 1;
    public TimeAxisMode TimeAxisMode { get; set; } = TimeAxisMode.FollowRun;

    public bool SoundEnabled { get; set; } = true;

    /// <summary>Notify through the desktop when a target is reached while the window is minimised.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>
    /// Look for a new version at startup and fetch it, to be installed the next time the app is
    /// closed. On by default: an app that talks to a heating element is one where a fix should not
    /// wait for somebody to think of looking. Nothing is ever installed while it is running.
    /// </summary>
    public bool AutomaticUpdates { get; set; } = true;

    /// <summary>Saved multi-point ramps, in the order shown in the profile picker.</summary>
    public List<RampProfile> RampProfiles { get; set; } = new();

    /// <summary>Which profile the Ramp tab opens with. Empty means the first one.</summary>
    public string ActiveRampProfileName { get; set; } = "";

    // User-maintained shortlist offered as quick-select chips next to the target temperature.
    public List<int> PredefinedTemperatures { get; set; } = new() { 180, 185, 190, 195, 200, 210, 220 };

    // LAN-relay convenience persistence, so hosting and joining don't start from empty fields
    // every time. Deliberately plain text - the PIN is only a door lock for the trusted home
    // network, not real security.
    public int RelayServerPort { get; set; } = 58642;
    public string RelayPin { get; set; } = "";
    public string RelayLastHostAddress { get; set; } = "";
    public bool HostOnStart { get; set; }
}
