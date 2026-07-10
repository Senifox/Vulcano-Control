namespace Vulcano_Control.Models;

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public int HistoryRetentionMinutes { get; set; } = 120;
    public int RampPushThresholdCelsius { get; set; } = 1;
    public bool SoundEnabled { get; set; } = true;

    // Last-used ramp shape, so the main window's Temperatur-Rampe fields don't reset to their
    // hardcoded defaults every time the app is restarted.
    public int RampDurationMinutes { get; set; } = 40;
    public int RampStartTemperatureCelsius { get; set; } = 185;
    public int RampEndTemperatureCelsius { get; set; } = 225;
    public InterpolationMethod RampInterpolationMethod { get; set; } = InterpolationMethod.Linear;
    public int RampHoldMinutes { get; set; } = 5;

    // User-maintained shortlist offered in the Zieltemperatur combo box.
    public List<int> PredefinedTemperatures { get; set; } = new() { 180, 185, 190, 195, 200, 210, 220 };
}
