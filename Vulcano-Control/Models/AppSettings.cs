namespace Vulcano_Control.Models;

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public int HistoryRetentionMinutes { get; set; } = 120;
    public double RampPushThresholdCelsius { get; set; } = 0.3;
    public int RampMaxPushIntervalSeconds { get; set; } = 30;
}
