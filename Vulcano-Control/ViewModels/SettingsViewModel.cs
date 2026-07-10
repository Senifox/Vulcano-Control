using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const int MinDeviceTemperatureCelsius = 40;
    private const int MaxDeviceTemperatureCelsius = 230;

    private readonly SettingsService _settingsService;

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? Cancelled;

    [ObservableProperty]
    private int historyRetentionMinutes;

    [ObservableProperty]
    private int rampPushThresholdCelsius;

    [ObservableProperty]
    private bool soundEnabled;

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>User-maintained shortlist offered in the main window's Zieltemperatur combo box.</summary>
    public ObservableCollection<int> PredefinedTemperatures { get; } = new();

    [ObservableProperty]
    private int newPredefinedTemperature = 185;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePredefinedTemperatureCommand))]
    private int? selectedPredefinedTemperature;

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
        SoundEnabled = settings.SoundEnabled;
        ErrorMessage = null;

        PredefinedTemperatures.Clear();
        foreach (var temperature in settings.PredefinedTemperatures.OrderBy(t => t))
        {
            PredefinedTemperatures.Add(temperature);
        }
    }

    [RelayCommand]
    private void AddPredefinedTemperature()
    {
        if (NewPredefinedTemperature < MinDeviceTemperatureCelsius || NewPredefinedTemperature > MaxDeviceTemperatureCelsius)
        {
            ErrorMessage = $"Wert muss zwischen {MinDeviceTemperatureCelsius}°C und {MaxDeviceTemperatureCelsius}°C liegen.";
            return;
        }
        if (PredefinedTemperatures.Contains(NewPredefinedTemperature))
        {
            ErrorMessage = "Dieser Wert ist bereits in der Liste.";
            return;
        }

        var insertIndex = 0;
        while (insertIndex < PredefinedTemperatures.Count && PredefinedTemperatures[insertIndex] < NewPredefinedTemperature)
        {
            insertIndex++;
        }
        PredefinedTemperatures.Insert(insertIndex, NewPredefinedTemperature);
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemovePredefinedTemperature))]
    private void RemovePredefinedTemperature() => PredefinedTemperatures.Remove(SelectedPredefinedTemperature!.Value);

    private bool CanRemovePredefinedTemperature() => SelectedPredefinedTemperature is not null;

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

        // Load fresh from disk first so an in-between change to another setting (e.g. Theme,
        // saved independently via the View menu) isn't clobbered by this save.
        var settings = _settingsService.Load();
        settings.HistoryRetentionMinutes = HistoryRetentionMinutes;
        settings.RampPushThresholdCelsius = RampPushThresholdCelsius;
        settings.SoundEnabled = SoundEnabled;
        settings.PredefinedTemperatures = PredefinedTemperatures.ToList();
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
