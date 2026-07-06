namespace Vulcano_Control.Models;

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public int HistoryRetentionMinutes { get; set; } = 120;
    public int RampPushThresholdCelsius { get; set; } = 1;
    public bool SoundEnabled { get; set; } = true;
}
