using System;
using System.ComponentModel;
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
    private bool _isFalling;

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
        _device.ProgressChanged += OnRampProgressChanged;
        _device.Completed += OnRampCompleted;
        _device.Stopped += OnRampStopped;
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
        HeatState.Heating => Strings.Get("State.Heating"),
        HeatState.AtTarget => Strings.Get("State.AtTarget"),
        HeatState.Cooling => Strings.Get("State.Cooling"),
        _ => Strings.Get("State.Idle"),
    };

    /// <summary>"on · 6:12" once the heater has been on for a while, otherwise just on or off.
    /// Computed rather than assigned, so it reads correctly before the first device event arrives
    /// instead of sitting empty next to a pump that already says "off".</summary>
    public string HeaterDetailText => IsHeaterOn && _heaterOnSince is { } since
        ? Strings.Get("Control.HeaterFor", Formatting.Duration(DateTime.UtcNow - since))
        : Strings.Get(IsHeaterOn ? "State.On" : "State.Off");

    public string PumpDetailText => Strings.Get(IsPumpOn ? "State.On" : "State.Off");

    public string AutoShutOffText => Formatting.Duration(RemainingAutoOffSeconds);

    public string AutoShutOffNote =>
        IsRampRunning ? Strings.Get("Control.AutoShutOff.Extended") : "";

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
            _isFalling = !double.IsNaN(_previousTemperature) && celsius < _previousTemperature - 0.05;
            _previousTemperature = celsius;
            UpdateHeatState();
        });

    /// <summary>
    /// Derives the chip from the three things it depends on - heater, temperature, target - and is
    /// therefore called whenever any of them changes, not just on a new temperature. The device only
    /// reports a temperature when it actually moves, so a chip that waited for one read "heating"
    /// for seconds after the heater had been switched off.
    /// </summary>
    private void UpdateHeatState()
    {
        if (IsHeaterOn)
        {
            HeatState = Math.Abs(TargetTemperature - _previousTemperature) <= AtTargetToleranceCelsius
                ? HeatState.AtTarget
                : HeatState.Heating;
            return;
        }

        // With the heater off the device is either giving off heat or sitting at room temperature;
        // "cooling" is only worth saying while the number is actually still moving.
        HeatState = _isFalling ? HeatState.Cooling : HeatState.Idle;
    }

    partial void OnTargetTemperatureChanged(double value) => UpdateHeatState();

    private void OnActivityChanged(object? sender, ushort activity) =>
        Dispatcher.UIThread.Post(() =>
        {
            var heaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;

            if (heaterOn && !IsHeaterOn) _heaterOnSince = DateTime.UtcNow;
            if (!heaterOn) _heaterOnSince = null;

            IsHeaterOn = heaterOn;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
            UpdateHeatState();
        });

    /// <summary>
    /// While a ramp runs it owns the target, so the cockpit shows what the ramp is asking for rather
    /// than the value the device happened to hold when we connected. Without this the Control tab sat
    /// at 225 °C through a ramp that drove the device from 180 to 195 - the delta underneath was
    /// nonsense and the number contradicted the Run tab.
    /// </summary>
    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() => TargetTemperature = Math.Round(e.CurrentComputedTarget));

    // The ramp has let go of the target: ask the device what it now holds instead of guessing.
    // A finished ramp resets it, a stopped one leaves the last pushed value - reading covers both.
    private void OnRampCompleted(object? sender, double resetTemperature) => RereadTarget();
    private void OnRampStopped(object? sender, EventArgs e) => RereadTarget();

    private void RereadTarget() =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (await _device.ReadTargetTemperatureAsync() is { } target) TargetTemperature = target;
        });

    private void OnRemainingAutoOffSecondsChanged(object? sender, int seconds) =>
        Dispatcher.UIThread.Post(() =>
        {
            RemainingAutoOffSeconds = seconds;
            // The heater's "on for 6:12" line has no event of its own; it rides along with the
            // auto shut-off tick, which arrives once a second anyway.
            OnPropertyChanged(nameof(HeaterDetailText));
        });


    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(async () =>
        {
            IsConnected = state == ConnectionState.Connected;

            // The device keeps its own target between sessions. Reading it means the number on
            // screen is the device's, not one this app invented.
            if (IsConnected && await _device.ReadTargetTemperatureAsync() is { } target)
            {
                TargetTemperature = target;
            }

            if (!IsConnected)
            {
                HeatState = HeatState.Idle;
                _heaterOnSince = null;
                _previousTemperature = double.NaN;
                _isFalling = false;
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
        _device.ProgressChanged -= OnRampProgressChanged;
        _device.Completed -= OnRampCompleted;
        _device.Stopped -= OnRampStopped;
    }

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

