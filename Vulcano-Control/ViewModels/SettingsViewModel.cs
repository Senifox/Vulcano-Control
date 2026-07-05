using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? Cancelled;

    [ObservableProperty]
    private int historyRetentionMinutes;

    [ObservableProperty]
    private int rampPushThresholdCelsius;

    [ObservableProperty]
    private int rampMaxPushIntervalSeconds;

    [ObservableProperty]
    private bool soundEnabled;

    [ObservableProperty]
    private string? errorMessage;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromDisk();
    }

    public void LoadFromDisk()
    {
        var settings = _settingsService.Load();
        HistoryRetentionMinutes = settings.HistoryRetentionMinutes;
        RampPushThresholdCelsius = settings.RampPushThresholdCelsius;
        RampMaxPushIntervalSeconds = settings.RampMaxPushIntervalSeconds;
        SoundEnabled = settings.SoundEnabled;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (HistoryRetentionMinutes <= 0)
        {
            ErrorMessage = "Verlaufsgröße muss größer als 0 sein.";
            return;
        }
        if (RampPushThresholdCelsius <= 0)
        {
            ErrorMessage = "Update-Schwelle muss größer als 0 sein.";
            return;
        }
        if (RampMaxPushIntervalSeconds <= 0)
        {
            ErrorMessage = "Max. Update-Intervall muss größer als 0 sein.";
            return;
        }

        // Load fresh from disk first so an in-between change to another setting (e.g. Theme,
        // saved independently via the View menu) isn't clobbered by this save.
        var settings = _settingsService.Load();
        settings.HistoryRetentionMinutes = HistoryRetentionMinutes;
        settings.RampPushThresholdCelsius = RampPushThresholdCelsius;
        settings.RampMaxPushIntervalSeconds = RampMaxPushIntervalSeconds;
        settings.SoundEnabled = SoundEnabled;
        _settingsService.Save(settings);

        ErrorMessage = null;
        SettingsSaved?.Invoke(this, settings);
    }

    [RelayCommand]
    private void Cancel()
    {
        LoadFromDisk();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
