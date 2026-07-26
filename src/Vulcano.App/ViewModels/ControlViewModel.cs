using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>What the device is doing, as the chip next to the big number says it.</summary>
public enum HeatState
{
    Idle,
    Heating,
    AtTarget,
    Cooling
}

/// <summary>
/// The cockpit: live temperature, heater and pump, target temperature and the auto shut-off.
/// One of the pieces the WPF version's single 972-line MainViewModel is split into.
///
/// All device events arrive on background threads and are marshalled here, once, so nothing below
/// this class has to think about it.
/// </summary>
public partial class ControlViewModel : ObservableObject, IDisposable
{
    /// <summary>Within this much of the target counts as "there" - the device's own control loop
    /// oscillates by more than a tenth of a degree, and a chip flickering between "heating" and
    /// "at target" is worse than useless.</summary>
    private const double AtTargetToleranceCelsius = 1.5;

    private readonly VolcanoDeviceOrchestrator _device;

    private DateTime? _heaterOnSince;
    private double _previousTemperature = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTemperatureText))]
    [NotifyPropertyChangedFor(nameof(DeltaToTargetText))]
    [NotifyPropertyChangedFor(nameof(ProgressToTarget))]
    private double _currentTemperature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetTemperatureText))]
    [NotifyPropertyChangedFor(nameof(DeltaToTargetText))]
    [NotifyPropertyChangedFor(nameof(ProgressToTarget))]
    private double _targetTemperature = 185;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeatStateText))]
    private HeatState _heatState = HeatState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaterDetailText))]
    private bool _isHeaterOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PumpDetailText))]
    private bool _isPumpOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoShutOffText))]
    private int _remainingAutoOffSeconds;

    [ObservableProperty]
    private string _heaterDetailText = "off";

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>True while a ramp is driving the target, which is what the auto shut-off note
    /// underneath refers to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoShutOffNote))]
    private bool _isRampRunning;

    public ControlViewModel(VolcanoDeviceOrchestrator device, AppSettings settings)
    {
        _device = device;

        Chart = new ChartViewModel(device, settings);
        QuickTemperatures = new ObservableCollection<int>(settings.PredefinedTemperatures);

        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ActivityChanged += OnActivityChanged;
        _device.RemainingAutoOffSecondsChanged += OnRemainingAutoOffSecondsChanged;
        _device.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <summary>The temperature chart filling the right-hand column.</summary>
    public ChartViewModel Chart { get; }

    /// <summary>The shortlist from the settings, offered as chips next to the stepper.</summary>
    public ObservableCollection<int> QuickTemperatures { get; }

    public string CurrentTemperatureText =>
        IsConnected ? Math.Round(CurrentTemperature).ToString("0") : "—";

    public string TargetTemperatureText => Formatting.Celsius(TargetTemperature);

    /// <summary>How far there is still to go, signed. Empty once it no longer says anything.</summary>
    public string DeltaToTargetText
    {
        get
        {
            if (!IsConnected) return "";

            var delta = TargetTemperature - CurrentTemperature;
            return Math.Abs(delta) < 0.5 ? "" : Formatting.Kelvin(delta);
        }
    }

    /// <summary>Fraction of the way to the target, for the bar under the big number.</summary>
    public double ProgressToTarget =>
        TargetTemperature <= 0 ? 0 : Math.Clamp(CurrentTemperature / TargetTemperature, 0, 1);

    public string HeatStateText => HeatState switch
    {
        HeatState.Heating => "heating",
        HeatState.AtTarget => "at target",
        HeatState.Cooling => "cooling",
        _ => "idle",
    };

    public string PumpDetailText => IsPumpOn ? "on" : "off";

    public string AutoShutOffText => Formatting.Duration(RemainingAutoOffSeconds);

    public string AutoShutOffNote =>
        IsRampRunning ? "Extended automatically while a ramp is running" : "";

    [RelayCommand]
    private async Task ToggleHeaterAsync() => await _device.SetHeaterAsync(!IsHeaterOn);

    [RelayCommand]
    private async Task TogglePumpAsync() => await _device.SetPumpAsync(!IsPumpOn);

    /// <summary>Writes whatever the stepper or a quick chip left in <see cref="TargetTemperature"/>.</summary>
    [RelayCommand]
    private async Task ApplyTargetAsync() => await _device.SetTargetTemperatureAsync(TargetTemperature);

    [RelayCommand]
    private async Task SelectQuickTemperatureAsync(int celsius)
    {
        TargetTemperature = celsius;
        await _device.SetTargetTemperatureAsync(celsius);
    }

    // --- Device events ---

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        Dispatcher.UIThread.Post(() =>
        {
            CurrentTemperature = celsius;
            UpdateHeatState(celsius);
        });

    private void UpdateHeatState(double celsius)
    {
        var falling = !double.IsNaN(_previousTemperature) && celsius < _previousTemperature - 0.05;
        _previousTemperature = celsius;

        if (IsHeaterOn)
        {
            HeatState = Math.Abs(TargetTemperature - celsius) <= AtTargetToleranceCelsius
                ? HeatState.AtTarget
                : HeatState.Heating;
            return;
        }

        // With the heater off the device is either giving off heat or sitting at room temperature;
        // "cooling" is only worth saying while the number is actually still moving.
        HeatState = falling ? HeatState.Cooling : HeatState.Idle;
    }

    private void OnActivityChanged(object? sender, ushort activity) =>
        Dispatcher.UIThread.Post(() =>
        {
            var heaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;

            if (heaterOn && !IsHeaterOn) _heaterOnSince = DateTime.UtcNow;
            if (!heaterOn) _heaterOnSince = null;

            IsHeaterOn = heaterOn;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
            UpdateHeaterDetail();
        });

    private void OnRemainingAutoOffSecondsChanged(object? sender, int seconds) =>
        Dispatcher.UIThread.Post(() =>
        {
            RemainingAutoOffSeconds = seconds;
            // The heater's "on for 6:12" line has no event of its own; it rides along with the
            // auto shut-off tick, which arrives once a second anyway.
            UpdateHeaterDetail();
        });

    private void UpdateHeaterDetail() =>
        HeaterDetailText = IsHeaterOn && _heaterOnSince is { } since
            ? $"on · {Formatting.Duration(DateTime.UtcNow - since)}"
            : IsHeaterOn ? "on" : "off";

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = state == ConnectionState.Connected;

            if (!IsConnected)
            {
                HeatState = HeatState.Idle;
                _heaterOnSince = null;
                _previousTemperature = double.NaN;
            }

            OnPropertyChanged(nameof(CurrentTemperatureText));
            OnPropertyChanged(nameof(DeltaToTargetText));
        });

    public void Dispose()
    {
        Chart.Dispose();
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ActivityChanged -= OnActivityChanged;
        _device.RemainingAutoOffSecondsChanged -= OnRemainingAutoOffSecondsChanged;
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
