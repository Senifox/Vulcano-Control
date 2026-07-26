using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>
/// The ramp editor: a list of time/temperature points, a curve per segment, a hold at the end, and
/// the profiles they are saved under. Everything the old five text boxes used to be.
/// </summary>
public partial class RampViewModel : ObservableObject, IDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    private bool _isHeaterOn;
    private bool _suspendRebuild;

    [ObservableProperty]
    private RampProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPoint))]
    [NotifyPropertyChangedFor(nameof(SegmentTitle))]
    [NotifyPropertyChangedFor(nameof(HasSegment))]
    [NotifyCanExecuteChangedFor(nameof(RemovePointCommand))]
    private int _selectedIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDurationText))]
    private int _holdMinutes = 5;

    [ObservableProperty]
    private string _validationMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDurationText))]
    [NotifyCanExecuteChangedFor(nameof(StartRampCommand))]
    private bool _isValid = true;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isRampRunning;

    [ObservableProperty]
    private string _warmUpNote = "";

    public RampViewModel(
        VolcanoDeviceOrchestrator device,
        SettingsService settingsService,
        AppSettings settings)
    {
        _device = device;
        _settingsService = settingsService;
        _settings = settings;

        Profiles = new ObservableCollection<RampProfile>(settings.RampProfiles);
        Points = new ObservableCollection<RampPointViewModel>();
        Points.CollectionChanged += (_, _) => Revalidate();

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == settings.ActiveRampProfileName)
                          ?? Profiles.FirstOrDefault();

        _device.ActivityChanged += OnActivityChanged;
        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public ObservableCollection<RampProfile> Profiles { get; }

    public ObservableCollection<RampPointViewModel> Points { get; }

    /// <summary>The four the design settles on - no more, no fewer.</summary>
    public IReadOnlyList<CurveOptionViewModel> Curves { get; } =
    [
        new(CurveKind.Linear),
        new(CurveKind.Exponential),
        new(CurveKind.Steep),
        new(CurveKind.EaseInOut),
    ];

    public RampPointViewModel? SelectedPoint =>
        SelectedIndex >= 0 && SelectedIndex < Points.Count ? Points[SelectedIndex] : null;

    /// <summary>True while a point other than the last one is selected, i.e. while there is a
    /// segment leaving it whose curve can be chosen.</summary>
    public bool HasSegment => SelectedPoint is { IsLast: false };

    public string SegmentTitle =>
        SelectedPoint is { IsLast: false } point ? $"SEGMENT {point.Number} → {point.Number + 1}" : "SEGMENT";

    public string TotalDurationText
    {
        get
        {
            if (Points.Count < 2) return "";

            var total = Points[^1].TimeMinutes;
            return HoldMinutes > 0
                ? $"{Formatting.Minutes(total)} + {Formatting.Minutes(HoldMinutes)} hold"
                : Formatting.Minutes(total);
        }
    }

    private double _currentTemperature = double.NaN;

    partial void OnSelectedProfileChanged(RampProfile? value)
    {
        LoadProfile(value);
        _settings.ActiveRampProfileName = value?.Name ?? "";
        _settingsService.Save(_settings);
    }

    partial void OnHoldMinutesChanged(int value) => Revalidate();

    partial void OnSelectedIndexChanged(int value)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i].IsSelected = i == value;
        }

        SyncCurveOptions();
    }

    /// <summary>Marks the chip that matches the selected segment's curve.</summary>
    private void SyncCurveOptions()
    {
        var current = SelectedPoint is { IsLast: false } point ? point.CurveToNext : (CurveKind?)null;

        foreach (var option in Curves)
        {
            option.IsSelected = current == option.Kind;
        }
    }

    private void LoadProfile(RampProfile? profile)
    {
        _suspendRebuild = true;

        foreach (var point in Points) point.PropertyChanged -= OnPointChanged;
        Points.Clear();

        if (profile is not null)
        {
            foreach (var point in profile.Points)
            {
                Add(new RampPointViewModel(point));
            }
            HoldMinutes = profile.HoldMinutes;
        }

        _suspendRebuild = false;
        SelectedIndex = Points.Count > 0 ? 0 : -1;
        Renumber();
        Revalidate();
    }

    private void Add(RampPointViewModel point)
    {
        point.PropertyChanged += OnPointChanged;
        Points.Add(point);
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the values that make up the ramp; IsSelected and friends change constantly and
        // would otherwise revalidate on every click.
        if (e.PropertyName is nameof(RampPointViewModel.TimeMinutes)
            or nameof(RampPointViewModel.Celsius)
            or nameof(RampPointViewModel.CurveToNext))
        {
            Revalidate();
            SyncCurveOptions();
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i].Number = i + 1;
            Points[i].IsLast = i == Points.Count - 1;
            Points[i].IsSelected = i == SelectedIndex;
        }
    }

    /// <summary>
    /// Rebuilds the plan from the current points and reports what is wrong, if anything. Called on
    /// every edit - the editor is meant to say "that is not a ramp" while you are making it, not
    /// when you press start.
    /// </summary>
    public void Revalidate()
    {
        if (_suspendRebuild) return;

        var points = Points.Select(p => p.ToPoint()).ToList();
        var errors = RampValidation.Validate(points, HoldMinutes);

        foreach (var point in Points) point.HasError = false;
        foreach (var error in errors.Where(e => e.PointIndex >= 0 && e.PointIndex < Points.Count))
        {
            Points[error.PointIndex].HasError = true;
        }

        IsValid = errors.Count == 0;
        ValidationMessage = errors.Count == 0 ? "" : Describe(errors[0]);

        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(Plan));
        UpdateWarmUpNote();
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised whenever the curve itself changed, so the editor can redraw.</summary>
    public event EventHandler? PlanChanged;

    /// <summary>The plan the editor draws and the start button runs, or null while it is invalid.</summary>
    public TemperatureRampPlan? Plan =>
        TemperatureRampPlan.TryCreate(
            Points.Select(p => p.ToPoint()).ToList(), TimeSpan.FromMinutes(HoldMinutes), out var plan, out _)
            ? plan
            : null;

    private static string Describe(RampValidationError error) => error.Issue switch
    {
        RampValidationIssue.TooFewPoints => "A ramp needs at least two points.",
        RampValidationIssue.FirstPointNotAtZero => "The first point has to be at minute 0.",
        RampValidationIssue.TimeNotIncreasing => "Each point has to come after the one before it.",
        RampValidationIssue.TemperatureOutOfRange => "must be between 40 and 230 °C",
        RampValidationIssue.NegativeHold => "The hold cannot be negative.",
        _ => "",
    };

    private void UpdateWarmUpNote()
    {
        if (Points.Count == 0 || double.IsNaN(_currentTemperature))
        {
            WarmUpNote = "";
            return;
        }

        var start = Points[0].Celsius;
        if (_currentTemperature >= start - 0.5)
        {
            WarmUpNote = $"The device is already at {Formatting.Celsius(start)}, so the ramp starts right away.";
            return;
        }

        // Matches the simulator's and the real device's rough climb rate; it is a hint, not a promise.
        var seconds = (start - _currentTemperature) / 3.5;
        WarmUpNote =
            $"Warm-up to {Formatting.Celsius(start)} takes about {Formatting.Duration(TimeSpan.FromSeconds(seconds))} " +
            $"from {Formatting.Celsius(_currentTemperature)} - it runs before the ramp and is not counted in it.";
    }

    // --- Commands ---

    /// <summary>Adds a point halfway along the selected segment, which is where "add" means
    /// something; at the end it extends the ramp instead.</summary>
    [RelayCommand]
    private void AddPoint()
    {
        if (Points.Count == 0)
        {
            Add(new RampPointViewModel(new RampPoint(0, 185)));
            Renumber();
            SelectedIndex = 0;
            return;
        }

        var index = Math.Clamp(SelectedIndex, 0, Points.Count - 1);

        if (index == Points.Count - 1)
        {
            var last = Points[^1];
            Add(new RampPointViewModel(new RampPoint(last.TimeMinutes + 10, last.Celsius)));
        }
        else
        {
            var from = Points[index];
            var to = Points[index + 1];
            var middle = new RampPoint(
                (from.TimeMinutes + to.TimeMinutes) / 2,
                Math.Round((from.Celsius + to.Celsius) / 2),
                from.CurveToNext);

            var vm = new RampPointViewModel(middle);
            vm.PropertyChanged += OnPointChanged;
            Points.Insert(index + 1, vm);
        }

        Renumber();
        SelectedIndex = Math.Min(index + 1, Points.Count - 1);
        Revalidate();
    }

    private bool CanRemovePoint() => Points.Count > 2 && SelectedIndex >= 0;

    [RelayCommand(CanExecute = nameof(CanRemovePoint))]
    private void RemovePoint()
    {
        if (!CanRemovePoint()) return;

        var index = SelectedIndex;
        Points[index].PropertyChanged -= OnPointChanged;
        Points.RemoveAt(index);

        Renumber();
        SelectedIndex = Math.Clamp(index, 0, Points.Count - 1);
        Revalidate();
    }

    [RelayCommand]
    private void SetCurve(CurveOptionViewModel option)
    {
        if (SelectedPoint is { IsLast: false } point)
        {
            point.CurveToNext = option.Kind;
        }

        SyncCurveOptions();
    }

    /// <summary>Writes the edited points back into the selected profile and saves.</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfile is not { } profile) return;

        profile.Points = Points.Select(p => p.ToPoint()).ToList();
        profile.HoldMinutes = HoldMinutes;

        _settings.RampProfiles = Profiles.ToList();
        _settingsService.Save(_settings);
    }

    private bool CanStartRamp() => IsValid && IsConnected && !IsRampRunning;

    [RelayCommand(CanExecute = nameof(CanStartRamp))]
    private async Task StartRampAsync()
    {
        if (Plan is not { } plan) return;

        await _device.StartAsync(plan, _isHeaterOn);
    }

    // --- Device events ---

    private void OnActivityChanged(object? sender, ushort activity) =>
        _isHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        Dispatcher.UIThread.Post(() =>
        {
            _currentTemperature = celsius;
            UpdateWarmUpNote();
        });

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = state == ConnectionState.Connected;
            StartRampCommand.NotifyCanExecuteChanged();
        });

    partial void OnIsRampRunningChanged(bool value) => StartRampCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        _device.ActivityChanged -= OnActivityChanged;
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
