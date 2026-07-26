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

    /// <summary>Set while the profile list is being rewritten to show a new name, so the selection
    /// bouncing through null does not reload the editor from underneath the person using it.</summary>
    private bool _suspendProfileReload;

    [ObservableProperty]
    private RampProfile? _selectedProfile;

    /// <summary>The name in the box, which is the selected profile's until somebody types over it.
    /// Applied by saving, so renaming and editing are one action rather than two.</summary>
    [ObservableProperty]
    private string _profileName = "";

    /// <summary>Why the last save or delete did not happen, or empty.</summary>
    [ObservableProperty]
    private string _profileMessage = "";

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

    /// <summary>Empty unless some segment asks for more than the device can deliver.</summary>
    [ObservableProperty]
    private string _reachabilityNote = "";

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
        SelectedPoint is { IsLast: false } point ? Strings.Get("Ramp.Segment", point.Number, point.Number + 1)
            : Strings.Get("Ramp.SegmentEmpty");

    public string TotalDurationText
    {
        get
        {
            if (Points.Count < 2) return "";

            var total = Points[^1].TimeMinutes;
            return HoldMinutes > 0
                ? Strings.Get("Ramp.HoldSuffix", Formatting.Minutes(total), Formatting.Minutes(HoldMinutes))
                : Formatting.Minutes(total);
        }
    }

    private double _currentTemperature = double.NaN;

    partial void OnSelectedProfileChanged(RampProfile? value)
    {
        if (_suspendProfileReload) return;

        LoadProfile(value);
        ProfileName = value?.Name ?? "";
        ProfileMessage = "";
        DeleteProfileCommand.NotifyCanExecuteChanged();

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

        // Renumber has to come after the selection, because it is what marks the selected point -
        // but the labels that spell out a segment read the point's number, and setting the selection
        // is what makes them read it. So they are asked again here: too early otherwise, and not at
        // all when the selection was already 0 and did not change. That is how a freshly loaded
        // profile came to announce "SEGMENT 0 to 1".
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(SegmentTitle));
        OnPropertyChanged(nameof(HasSegment));

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
        UpdateReachabilityNote();
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Says which segments the device could not follow, and how long each would really take.
    ///
    /// A warning rather than a refusal. The figures behind it are measurements from one device in
    /// one room, and drawing a climb steeper than the device can manage is a perfectly reasonable
    /// way of saying "as fast as you can" - it arrives late and nothing is harmed. A fall is the
    /// other story: the Volcano has no cooling, so a segment that drops 190 K in a minute is off by
    /// the best part of an hour, and that is worth knowing before starting rather than after.
    /// </summary>
    private void UpdateReachabilityNote()
    {
        if (Plan is not { } plan)
        {
            ReachabilityNote = "";
            return;
        }

        var problems = RampFeasibility.OutOfReach(plan);

        ReachabilityNote = string.Join(
            Environment.NewLine,
            problems.Select(s => Strings.Get(
                s.IsCooling ? "Ramp.TooFast.Cooling" : "Ramp.TooFast.Heating",
                s.SegmentNumber,
                RoughMinutes(s.Needed),
                Formatting.Minutes((int)s.Allowed.TotalMinutes))));
    }

    /// <summary>
    /// Whole minutes, or seconds below one. The estimate comes from a table measured on one device
    /// in one room, so "about 55 min" is the honest amount of precision - "54:10" would claim to
    /// know the ten seconds.
    /// </summary>
    private static string RoughMinutes(TimeSpan value) =>
        value.TotalMinutes < 1
            ? Formatting.WithUnit(((int)Math.Round(value.TotalSeconds)).ToString(), "s")
            : Formatting.Minutes((int)Math.Round(value.TotalMinutes));

    /// <summary>Raised whenever the curve itself changed, so the editor can redraw.</summary>
    public event EventHandler? PlanChanged;

    /// <summary>The plan the editor draws and the start button runs, or null while it is invalid.</summary>
    public TemperatureRampPlan? Plan =>
        TemperatureRampPlan.TryCreate(
            Points.Select(p => p.ToPoint()).ToList(), TimeSpan.FromMinutes(HoldMinutes), out var plan, out _)
            ? plan
            : null;

    /// <summary>The key follows the issue name, so a new validation issue cannot be added
    /// without a message - the string table test fails until one exists.</summary>
    private static string Describe(RampValidationError error) =>
        Strings.Get($"Ramp.Invalid.{error.Issue}");

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
            WarmUpNote = Strings.Get("Ramp.WarmUp.AlreadyThere", Formatting.Celsius(start));
            return;
        }

        // Matches the simulator's and the real device's rough climb rate; it is a hint, not a promise.
        var seconds = (start - _currentTemperature) / 3.5;
        WarmUpNote = Strings.Get(
            "Ramp.WarmUp.Needed",
            Formatting.Celsius(start),
            Formatting.Duration(TimeSpan.FromSeconds(seconds)),
            Formatting.Celsius(_currentTemperature));
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

    /// <summary>
    /// Writes the edited points, the hold and the name back into the selected profile and saves.
    /// The name goes through the same door as the rest: there is no separate rename, because a
    /// profile whose name was changed but whose points were not saved would be a state nobody asked
    /// for and everybody would eventually hit.
    /// </summary>
    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfile is not { } profile) return;

        var rename = RampProfileLibrary.Rename(Profiles, profile, ProfileName);
        if (rename is not ProfileNameIssue.None)
        {
            ProfileMessage = Strings.Get($"Ramp.Profile.{rename}");
            return;
        }

        profile.Points = Points.Select(p => p.ToPoint()).ToList();
        profile.HoldMinutes = HoldMinutes;

        ProfileName = profile.Name;
        ProfileMessage = Strings.Get("Ramp.Profile.Saved", profile.Name);
        RedrawProfileInList(profile);

        _settings.RampProfiles = Profiles.ToList();
        _settings.ActiveRampProfileName = profile.Name;
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Adds a profile alongside the current one, carrying its points over: a new ramp is far more
    /// often "that one but a bit different" than a blank sheet, and the blank sheet is one Remove
    /// away.
    /// </summary>
    [RelayCommand]
    private void NewProfile()
    {
        var added = RampProfileLibrary.Add(
            Profiles,
            ProfileName.Trim().Length > 0 ? ProfileName : Strings.Get("Ramp.Profile.NewName"),
            copyOf: SelectedProfile);

        SelectedProfile = added;
        ProfileMessage = Strings.Get("Ramp.Profile.Added", added.Name);

        _settings.RampProfiles = Profiles.ToList();
        _settingsService.Save(_settings);
    }

    private bool CanDeleteProfile() => Profiles.Count > 1 && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } profile) return;

        var name = profile.Name;
        if (RampProfileLibrary.Remove(Profiles, profile) is not { } next) return;

        SelectedProfile = next;
        ProfileMessage = Strings.Get("Ramp.Profile.Deleted", name);

        _settings.RampProfiles = Profiles.ToList();
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Puts a renamed profile back into the list so the drop-down shows the new name. RampProfile is
    /// a plain settings model with no change notification - deliberately, it is written to a file -
    /// so the collection has to be told, and taking the item out and putting it back is what tells
    /// it. Selection is restored on the way out because removing the selected item clears it.
    /// </summary>
    private void RedrawProfileInList(RampProfile profile)
    {
        var index = Profiles.IndexOf(profile);
        if (index < 0) return;

        _suspendProfileReload = true;
        try
        {
            Profiles.RemoveAt(index);
            Profiles.Insert(index, profile);
            SelectedProfile = profile;
        }
        finally
        {
            _suspendProfileReload = false;
        }
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

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

