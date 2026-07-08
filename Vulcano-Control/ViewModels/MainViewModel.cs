using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using Vulcano_Control.Models;
using Vulcano_Control.Services;
using ConnectionState = Vulcano_Control.Models.ConnectionState;

namespace Vulcano_Control.ViewModels;

public partial class MainViewModel : ObservableValidator, IAsyncDisposable
{
    private static readonly OxyColor SollColor = OxyColor.FromRgb(0xFF, 0x98, 0x00); // matches AccentBrush
    private static readonly OxyColor IstColor = OxyColor.FromRgb(0x21, 0x96, 0xF3);  // contrasting blue

    private const string TrackerFormat = "{0}\n{1}: {2:0.0}\n{3}: {4:0}";

    private static readonly OxyColor LightPlotBackground = OxyColor.FromRgb(0xF5, 0xF5, 0xF5);
    private static readonly OxyColor LightPlotText = OxyColor.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly OxyColor LightPlotBorder = OxyColor.FromRgb(0xB0, 0xB0, 0xB0);

    private static readonly OxyColor DarkPlotBackground = OxyColor.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly OxyColor DarkPlotText = OxyColor.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly OxyColor DarkPlotBorder = OxyColor.FromRgb(0x3F, 0x3F, 0x46);

    private const double PastWindowMinutes = 15.0;
    private const double MinFutureWindowMinutes = 5.0;
    private const double MaxChartWindowMinutes = 24.0 * 60.0;

    // The Volcano's actual temperature range (matches the manual Zieltemperatur slider bounds
    // in MainWindow.xaml) - used to keep the ramp chart preview from drawing physically
    // impossible curves when a Start-/Ziel-Temp textbox briefly holds an out-of-range value.
    private const int MinDeviceTemperatureCelsius = 40;
    private const int MaxDeviceTemperatureCelsius = 230;

    private readonly VolcanoBluetoothService _service;
    private readonly RampSessionController _rampController;
    private readonly ThemeService _themeService;
    private readonly LogService _logService;
    private readonly LogWindow _logWindow;
    private readonly SettingsService _settingsService;
    private readonly SettingsWindow _settingsWindow;
    private readonly SoundService _soundService;
    private readonly UpdateService _updateService;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _chartTimer;
    private readonly List<(DateTime TimeUtc, double Celsius)> _istHistory = new();
    private IReadOnlyList<(double Minutes, double Celsius)> _currentPlanSamples = Array.Empty<(double, double)>();
    private TimeSpan _historyRetention = TimeSpan.FromMinutes(120);
    private bool _suppressLogWindowVisibility;
    private bool _manualTargetSoundArmed;
    private int _pendingManualTargetCelsius;

    // Continuous timeline used only for shifting the chart's Soll-curve, spanning both the
    // Ramping and Holding (Nachlaufzeit) phases without resetting between them - unlike
    // RampElapsed, which intentionally resets to hold-relative time during Holding for the
    // "Restzeit" countdown display.
    private DateTime? _rampTrackStartedAtUtc;

    [ObservableProperty]
    private ConnectionState connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private int currentTemperature;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyTargetTemperatureCommand))]
    [Range(MinDeviceTemperatureCelsius, MaxDeviceTemperatureCelsius, ErrorMessage = "Wert muss zwischen 40°C und 230°C liegen.")]
    private int targetTemperature = 180;

    [ObservableProperty]
    private bool isHeaterOn;

    [ObservableProperty]
    private bool isPumpOn;

    [ObservableProperty]
    private string statusMessage = "Nicht verbunden";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRampCommand))]
    [Range(1, int.MaxValue, ErrorMessage = "Dauer muss größer als 0 sein.")]
    private int rampDurationMinutes = 40;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRampCommand))]
    [Range(MinDeviceTemperatureCelsius, MaxDeviceTemperatureCelsius, ErrorMessage = "Wert muss zwischen 40°C und 230°C liegen.")]
    private int rampStartTemperature = 185;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRampCommand))]
    [Range(MinDeviceTemperatureCelsius, MaxDeviceTemperatureCelsius, ErrorMessage = "Wert muss zwischen 40°C und 230°C liegen.")]
    private int rampEndTemperature = 225;

    [ObservableProperty]
    private InterpolationMethod rampInterpolationMethod = InterpolationMethod.Linear;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRampCommand))]
    [Range(0, int.MaxValue, ErrorMessage = "Nachlaufzeit darf nicht negativ sein.")]
    private int rampHoldMinutes = 5;

    [ObservableProperty]
    private bool isRampRunning;

    [ObservableProperty]
    private bool isRampWarmingUp;

    [ObservableProperty]
    private bool isRampHolding;

    [ObservableProperty]
    private TimeSpan rampElapsed;

    [ObservableProperty]
    private TimeSpan rampRemaining;

    [ObservableProperty]
    private int rampCurrentTarget;

    [ObservableProperty]
    private double rampFractionComplete;

    [ObservableProperty]
    private AppTheme currentTheme;

    [ObservableProperty]
    private bool isChartVisible = true;

    [ObservableProperty]
    private bool isLogWindowVisible;

    [ObservableProperty]
    private bool isAlwaysOnTop;

    [ObservableProperty]
    private PlotModel rampPlotModel = null!;

    [ObservableProperty]
    private int remainingAutoOffSeconds;

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    // The Volcano reports exactly 0°C when its own display is blank (e.g. cooled down far
    // enough that it stops showing a reading) rather than continuing to report the real,
    // still-decreasing measurement - 0 is otherwise physically impossible here (the device's
    // actual minimum is 40°C), so it's an unambiguous "no reading" sentinel, not a real value.
    public string CurrentTemperatureDisplay => CurrentTemperature > 0 ? $"{CurrentTemperature} °C" : "--";

    public bool IsLightMode => CurrentTheme == AppTheme.Light;

    public bool IsDarkMode => CurrentTheme == AppTheme.Dark;

    public bool IsAutoOffCountingDown => RemainingAutoOffSeconds > 0;

    public TimeSpan RemainingAutoOffTimeSpan => TimeSpan.FromSeconds(RemainingAutoOffSeconds);

    public IReadOnlyList<InterpolationMethod> InterpolationMethods { get; } = Enum.GetValues<InterpolationMethod>();

    public MainViewModel(
        ThemeService themeService,
        LogService logService,
        LogWindow logWindow,
        SettingsService settingsService,
        SettingsWindow settingsWindow,
        SoundService soundService,
        VolcanoBluetoothService service,
        UpdateService updateService)
    {
        _themeService = themeService;
        _logService = logService;
        _logWindow = logWindow;
        _settingsService = settingsService;
        _settingsWindow = settingsWindow;
        _soundService = soundService;
        _updateService = updateService;
        currentTheme = _themeService.CurrentTheme;

        _service = service;
        _rampController = new RampSessionController(_service, _logService);

        var settings = _settingsService.Load();
        ApplySettings(settings);

        _service.ConnectionStateChanged += OnServiceConnectionStateChanged;
        _service.ErrorOccurred += OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged += OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged += OnServiceActivityChanged;
        _service.RemainingAutoOffSecondsChanged += OnServiceRemainingAutoOffSecondsChanged;

        _rampController.ProgressChanged += OnRampProgressChanged;
        _rampController.WarmupCompleted += OnRampWarmupCompleted;
        _rampController.Completed += OnRampCompleted;
        _rampController.ErrorOccurred += OnRampErrorOccurred;

        _logWindow.IsVisibleChanged += OnLogWindowIsVisibleChanged;
        _settingsWindow.ViewModel.SettingsSaved += OnSettingsSaved;

        RampPlotModel = BuildEmptyPlotModel();
        ApplyChartTheme();

        // Loaded once here (after RampPlotModel exists, since assigning these triggers their
        // OnChanged hooks -> RebuildPlotCurve() -> RefreshChart(), which would NullReferenceException
        // against a not-yet-built plot model otherwise) rather than through ApplySettings, since
        // that method also re-runs on every Einstellungen-dialog save (OnSettingsSaved) -
        // re-applying these from a stale disk read there would clobber whatever the user has since
        // typed into the ramp fields but not yet saved.
        RampDurationMinutes = settings.RampDurationMinutes;
        RampStartTemperature = settings.RampStartTemperatureCelsius;
        RampEndTemperature = settings.RampEndTemperatureCelsius;
        RampInterpolationMethod = settings.RampInterpolationMethod;
        RampHoldMinutes = settings.RampHoldMinutes;

        RebuildPlotCurve();

        _chartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _chartTimer.Tick += (_, _) => RefreshChart();
        _chartTimer.Start();

        _ = RunUpdateCheckAsync(silentIfNoneFound: true);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        await _service.ScanAndConnectAsync(ct);
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        await _service.DisconnectAsync();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ToggleHeaterAsync()
    {
        await _service.SetHeaterAsync(!IsHeaterOn);
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task TogglePumpAsync()
    {
        await _service.SetPumpAsync(!IsPumpOn);
    }

    [RelayCommand(CanExecute = nameof(CanApplyManualTarget))]
    private async Task ApplyTargetTemperatureAsync()
    {
        await _service.SetTargetTemperatureAsync(TargetTemperature);
        _pendingManualTargetCelsius = TargetTemperature;
        _manualTargetSoundArmed = true;

        // The device may already be at or above the target (e.g. re-applying a target close to
        // the current reading) - CheckManualTargetReached must run right away too, since
        // OnCurrentTemperatureChanged only fires on the *next* actual change and might never
        // come if the temperature doesn't move again.
        CheckManualTargetReached(CurrentTemperature);
    }

    [RelayCommand(CanExecute = nameof(CanStartRamp))]
    private async Task StartRampAsync()
    {
        if (Math.Abs(RampEndTemperature - RampStartTemperature) < 1)
        {
            StatusMessage = "Start- und Zieltemperatur müssen sich ausreichend unterscheiden.";
            return;
        }

        _manualTargetSoundArmed = false;

        await _rampController.StartAsync(
            RampStartTemperature,
            RampEndTemperature,
            TimeSpan.FromMinutes(RampDurationMinutes),
            RampInterpolationMethod,
            TimeSpan.FromMinutes(RampHoldMinutes),
            heaterCurrentlyOn: IsHeaterOn);

        IsRampRunning = true;
        NotifyRampCommandsCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRampRunning))]
    private void StopRamp()
    {
        _rampController.Stop();
        IsRampRunning = false;
        IsRampWarmingUp = false;
        IsRampHolding = false;
        _rampTrackStartedAtUtc = null;
        NotifyRampCommandsCanExecuteChanged();
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        _themeService.SetTheme(theme);
        CurrentTheme = theme;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _settingsWindow.ViewModel.LoadFromDisk();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    [RelayCommand]
    private void ResetChartView()
    {
        RampPlotModel.ResetAllAxes();
        RampPlotModel.InvalidatePlot(false);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(silentIfNoneFound: false);

    private async Task RunUpdateCheckAsync(bool silentIfNoneFound)
    {
        var info = await _updateService.CheckForUpdatesAsync();
        if (info is null)
        {
            if (!silentIfNoneFound)
            {
                MessageBox.Show("Kein Update verfügbar - du verwendest bereits die neueste Version.",
                    "Vulcano Control", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var result = MessageBox.Show(
            $"Version {info.TargetFullRelease.Version} ist verfügbar. Jetzt herunterladen und die Anwendung neu starten?",
            "Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _updateService.DownloadAndApplyAsync(info);
        }
    }

    private bool CanConnect() =>
        ConnectionState is ConnectionState.Disconnected or ConnectionState.Error;

    private bool CanApplyManualTarget() =>
        IsConnected && !IsRampRunning && !GetErrors(nameof(TargetTemperature)).Any();

    private bool CanStartRamp() =>
        IsConnected && !IsRampRunning &&
        !GetErrors(nameof(RampStartTemperature)).Any() && !GetErrors(nameof(RampEndTemperature)).Any() &&
        !GetErrors(nameof(RampDurationMinutes)).Any() && !GetErrors(nameof(RampHoldMinutes)).Any();

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));

        if (value == ConnectionState.Connected)
        {
            _istHistory.Clear();
        }
        StatusMessage = value switch
        {
            ConnectionState.Disconnected => "Nicht verbunden",
            ConnectionState.Scanning => "Suche Volcano…",
            ConnectionState.Connecting => "Verbinde…",
            ConnectionState.Connected => "Verbunden",
            ConnectionState.Error => StatusMessage,
            _ => StatusMessage
        };

        if (value != ConnectionState.Connected && IsRampRunning)
        {
            _rampController.Stop();
            IsRampRunning = false;
            IsRampWarmingUp = false;
            IsRampHolding = false;
            _rampTrackStartedAtUtc = null;
        }

        if (value != ConnectionState.Connected)
        {
            CurrentTemperature = 0;
            TargetTemperature = 180;
            IsHeaterOn = false;
            IsPumpOn = false;
            RemainingAutoOffSeconds = 0;
            _manualTargetSoundArmed = false;
        }

        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleHeaterCommand.NotifyCanExecuteChanged();
        TogglePumpCommand.NotifyCanExecuteChanged();
        ApplyTargetTemperatureCommand.NotifyCanExecuteChanged();
        NotifyRampCommandsCanExecuteChanged();
    }

    partial void OnRemainingAutoOffSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(IsAutoOffCountingDown));
        OnPropertyChanged(nameof(RemainingAutoOffTimeSpan));
    }

    partial void OnCurrentThemeChanged(AppTheme value)
    {
        OnPropertyChanged(nameof(IsLightMode));
        OnPropertyChanged(nameof(IsDarkMode));
        ApplyChartTheme();
    }

    partial void OnIsLogWindowVisibleChanged(bool value)
    {
        _suppressLogWindowVisibility = true;
        if (value) _logWindow.Show(); else _logWindow.Hide();
        _suppressLogWindowVisibility = false;
    }

    private void OnLogWindowIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_suppressLogWindowVisibility) return;
        IsLogWindowVisible = _logWindow.IsVisible;
    }

    // ObservableValidator does not run [Range] validation automatically on property change - it
    // must be triggered explicitly, which is what makes GetErrors(...) (used by CanApplyManualTarget
    // and CanStartRamp below) actually reflect the current value instead of always being empty.
    partial void OnRampDurationMinutesChanged(int value)
    {
        ValidateProperty(value, nameof(RampDurationMinutes));
        RebuildPlotCurve();
        SaveRampFieldsToSettings();
    }

    partial void OnRampStartTemperatureChanged(int value)
    {
        ValidateProperty(value, nameof(RampStartTemperature));
        RebuildPlotCurve();
        SaveRampFieldsToSettings();
    }

    partial void OnRampEndTemperatureChanged(int value)
    {
        ValidateProperty(value, nameof(RampEndTemperature));
        RebuildPlotCurve();
        SaveRampFieldsToSettings();
    }

    partial void OnRampInterpolationMethodChanged(InterpolationMethod value)
    {
        RebuildPlotCurve();
        SaveRampFieldsToSettings();
    }

    partial void OnRampHoldMinutesChanged(int value)
    {
        ValidateProperty(value, nameof(RampHoldMinutes));
        RebuildPlotCurve();
        SaveRampFieldsToSettings();
    }

    /// <summary>
    /// Persists the current ramp shape (Dauer/Start-/Ziel-Temp/Verlauf/Nachlaufzeit) so it's
    /// restored on the next launch - saved on every change regardless of validity, so "last
    /// entered" reflects what's actually in the fields rather than silently dropping an
    /// in-progress edit. Loads fresh from disk first so an unrelated setting saved independently
    /// in the meantime (e.g. Theme) isn't clobbered.
    /// </summary>
    private void SaveRampFieldsToSettings()
    {
        var settings = _settingsService.Load();
        settings.RampDurationMinutes = RampDurationMinutes;
        settings.RampStartTemperatureCelsius = RampStartTemperature;
        settings.RampEndTemperatureCelsius = RampEndTemperature;
        settings.RampInterpolationMethod = RampInterpolationMethod;
        settings.RampHoldMinutes = RampHoldMinutes;
        _settingsService.Save(settings);
    }

    partial void OnTargetTemperatureChanged(int value) => ValidateProperty(value, nameof(TargetTemperature));

    partial void OnRampCurrentTargetChanged(int value) => UpdatePlotMarkers();
    partial void OnCurrentTemperatureChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTemperatureDisplay));
        UpdatePlotMarkers();
        CheckManualTargetReached(value);
    }

    private void CheckManualTargetReached(int currentValue)
    {
        if (_manualTargetSoundArmed && currentValue >= _pendingManualTargetCelsius)
        {
            _manualTargetSoundArmed = false;
            _soundService.PlayHeatReached();
        }
    }

    partial void OnIsRampRunningChanged(bool value) => UpdatePlotMarkers();

    private static PlotModel BuildEmptyPlotModel()
    {
        var model = new PlotModel();

        // MinorGridlineStyle intentionally stays Solid (not Dot): WPF renders dashed strokes
        // segment-by-segment, which gets noticeably expensive as more of the plot is visible
        // (e.g. zoomed in) - a thin solid line reads as a sub-grid just as well and is cheap.
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Zeit (min, 0 = jetzt)",
            Minimum = -PastWindowMinutes,
            // Maximum is computed and assigned every tick in RefreshChart(), so the axis
            // range never needs OxyPlot's own auto-scan of all series' points.
            // Caps how far the view can be panned/zoomed out in either direction - without this,
            // scrolling could reach arbitrarily far into the past/future, well beyond anything
            // the retained history or a planned ramp could ever actually show.
            AbsoluteMinimum = -MaxChartWindowMinutes,
            AbsoluteMaximum = MaxChartWindowMinutes,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineThickness = 0.5,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Temperatur (°C)",
            Minimum = 30,
            Maximum = 235,
            // Fully static - not just clamped via Absolute bounds, but with zoom/pan disabled
            // outright. It has zero zoom headroom by design (Minimum/Maximum already equal the
            // Absolute bounds), so a zoom-out gesture would otherwise stop dead on this axis while
            // the time axis (which still has plenty of headroom) kept stretching, visibly
            // distorting the whole plot into a squashed sliver.
            AbsoluteMinimum = 30,
            AbsoluteMaximum = 235,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineThickness = 0.5,
        });

        model.Series.Add(new LineSeries { Title = "Soll (geplant)", Color = SollColor, StrokeThickness = 2, TrackerFormatString = TrackerFormat });
        model.Series.Add(new ScatterSeries { Title = "Soll (aktuell)", MarkerType = MarkerType.Circle, MarkerFill = SollColor, MarkerSize = 6, TrackerFormatString = TrackerFormat });
        model.Series.Add(new ScatterSeries { Title = "Ist (gemessen)", MarkerType = MarkerType.Circle, MarkerFill = IstColor, MarkerSize = 6, TrackerFormatString = TrackerFormat });
        model.Series.Add(new LineSeries { Title = "Ist (Verlauf)", Color = IstColor, StrokeThickness = 2, TrackerFormatString = TrackerFormat });

        // Marks x=0 ("jetzt") clearly against the much thinner/lighter gridlines - lives in
        // Annotations rather than a Series, so RefreshChart()'s per-tick Series.Points rebuilds
        // never touch it.
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = 0,
            LineStyle = LineStyle.Solid,
            StrokeThickness = 2,
        });

        return model;
    }

    private void ApplyChartTheme()
    {
        var (background, text, border) = CurrentTheme == AppTheme.Dark
            ? (DarkPlotBackground, DarkPlotText, DarkPlotBorder)
            : (LightPlotBackground, LightPlotText, LightPlotBorder);

        RampPlotModel.Background = background;
        RampPlotModel.TextColor = text;
        RampPlotModel.PlotAreaBorderColor = border;
        RampPlotModel.TitleColor = text;

        foreach (var axis in RampPlotModel.Axes)
        {
            axis.TextColor = text;
            axis.TitleColor = text;
            axis.AxislineColor = border;
            axis.TicklineColor = border;
            axis.MajorGridlineColor = border;
            axis.MinorGridlineColor = OxyColor.FromAColor(96, border);
        }

        // Full-strength text color (vs. the much lighter/thinner gridlines) makes the "jetzt"
        // line clearly stand out as a reference marker rather than just another gridline.
        ((LineAnnotation)RampPlotModel.Annotations[0]).Color = text;

        RampPlotModel.InvalidatePlot(false);
    }

    private void RebuildPlotCurve()
    {
        if (RampDurationMinutes <= 0)
        {
            _currentPlanSamples = Array.Empty<(double, double)>();
            RefreshChart();
            return;
        }

        TemperatureRampPlan plan;
        try
        {
            // Clamped for the preview curve only - RampStartTemperature/RampEndTemperature keep
            // whatever the user actually typed (even if out of range) so the textbox doesn't
            // silently overwrite their input; StartRampCommand is disabled via validation until
            // it's corrected, but the chart should never draw a physically impossible curve.
            plan = new TemperatureRampPlan(
                Math.Clamp(RampStartTemperature, MinDeviceTemperatureCelsius, MaxDeviceTemperatureCelsius),
                Math.Clamp(RampEndTemperature, MinDeviceTemperatureCelsius, MaxDeviceTemperatureCelsius),
                TimeSpan.FromMinutes(RampDurationMinutes),
                RampInterpolationMethod);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var samples = RampCurveSampler.Sample(plan);

        if (RampHoldMinutes > 0)
        {
            // Extend the preview with a flat plateau at the end temperature representing the
            // Nachlaufzeit (hold) phase - two points at the same Y value render as a flat segment.
            var extended = new List<(double Minutes, double Celsius)>(samples)
            {
                (plan.Duration.TotalMinutes + RampHoldMinutes, plan.EndTemperatureCelsius)
            };
            samples = extended;
        }

        _currentPlanSamples = samples;
        RefreshChart();
    }

    private void UpdatePlotMarkers()
    {
        var sollMarker = (ScatterSeries)RampPlotModel.Series[1];
        var istMarker = (ScatterSeries)RampPlotModel.Series[2];

        sollMarker.Points.Clear();
        istMarker.Points.Clear();

        if (IsRampRunning)
        {
            sollMarker.Points.Add(new ScatterPoint(0, RampCurrentTarget));
        }

        if (IsConnected && CurrentTemperature > 0)
        {
            istMarker.Points.Add(new ScatterPoint(0, CurrentTemperature));
        }

        RampPlotModel.InvalidatePlot(false);
    }

    /// <summary>
    /// Redraws the Soll-curve and Ist-history series so that x=0 always represents "now" -
    /// called every second by <see cref="_chartTimer"/>, and immediately after the plan
    /// samples or connection state change for responsiveness between ticks.
    /// </summary>
    private void RefreshChart()
    {
        var nowUtc = DateTime.UtcNow;

        if (IsConnected && CurrentTemperature > 0)
        {
            // Skips recording the device's "display is blank" sentinel (see CurrentTemperatureDisplay)
            // as a data point, rather than plotting a fake plunge to 0°C.
            _istHistory.Add((nowUtc, CurrentTemperature));
        }

        var cutoff = nowUtc - _historyRetention;
        _istHistory.RemoveAll(p => p.TimeUtc < cutoff);

        // Uses the continuous _rampTrackStartedAtUtc timeline rather than RampElapsed, since the
        // latter intentionally resets to hold-relative time once the Nachlaufzeit (hold) phase
        // begins (for the "Restzeit" countdown) - shifting the chart by that reset value would
        // snap the curve back near "now" instead of continuing to scroll it into the past.
        var shiftMinutes = (IsRampRunning && _rampTrackStartedAtUtc is { } startedAtUtc)
            ? (nowUtc - startedAtUtc).TotalMinutes
            : 0.0;

        var curveSeries = (LineSeries)RampPlotModel.Series[0];
        curveSeries.Points.Clear();
        foreach (var (minutes, celsius) in _currentPlanSamples)
        {
            curveSeries.Points.Add(new DataPoint(minutes - shiftMinutes, celsius));
        }

        var istHistorySeries = (LineSeries)RampPlotModel.Series[3];
        istHistorySeries.Points.Clear();
        foreach (var (timeUtc, celsius) in _istHistory)
        {
            istHistorySeries.Points.Add(new DataPoint((timeUtc - nowUtc).TotalMinutes, celsius));
        }

        UpdatePlotMarkers();

        // We already know the plot's data extent (the curve's rightmost sample; the Y axis is
        // fixed), so the time axis's Maximum can be assigned directly instead of asking OxyPlot
        // to rescan every series' points to auto-detect it. That lets us invalidate with
        // updateData:false below, which skips that rescan entirely - the expensive part that
        // made this redraw (every second) increasingly costly as the Ist-history grew, and
        // that compounds badly with rendering more of the plot at higher zoom levels.
        var timeAxis = RampPlotModel.Axes[0];
        timeAxis.Maximum = _currentPlanSamples.Count > 0
            ? Math.Max(_currentPlanSamples[^1].Minutes - shiftMinutes, MinFutureWindowMinutes)
            : MinFutureWindowMinutes;

        RampPlotModel.InvalidatePlot(false);
    }

    private void NotifyRampCommandsCanExecuteChanged()
    {
        StartRampCommand.NotifyCanExecuteChanged();
        StopRampCommand.NotifyCanExecuteChanged();
        ApplyTargetTemperatureCommand.NotifyCanExecuteChanged();
    }

    private void ApplySettings(AppSettings settings)
    {
        _historyRetention = TimeSpan.FromMinutes(settings.HistoryRetentionMinutes);
        _rampController.PushThresholdCelsius = settings.RampPushThresholdCelsius;
        _soundService.SoundEnabled = settings.SoundEnabled;
    }

    private void OnSettingsSaved(object? sender, AppSettings settings) => ApplySettings(settings);

    private void OnServiceConnectionStateChanged(object? sender, ConnectionState state) =>
        _dispatcher.Invoke(() => ConnectionState = state);

    private void OnServiceErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() => StatusMessage = message);

    private void OnServiceCurrentTemperatureChanged(object? sender, double celsius) =>
        _dispatcher.BeginInvoke(() => CurrentTemperature = (int)Math.Round(celsius));

    private void OnServiceActivityChanged(object? sender, ushort activity) =>
        _dispatcher.BeginInvoke(() =>
        {
            var wasHeaterOn = IsHeaterOn;
            IsHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;

            if (wasHeaterOn && !IsHeaterOn)
            {
                _soundService.PlayShutdown();
            }
        });

    private void OnServiceRemainingAutoOffSecondsChanged(object? sender, int seconds) =>
        _dispatcher.BeginInvoke(() => RemainingAutoOffSeconds = seconds);

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            _rampTrackStartedAtUtc = DateTime.UtcNow;
            _soundService.PlayHeatReached();
        });

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            var roundedTarget = (int)Math.Round(e.CurrentComputedTarget);

            RampElapsed = e.Elapsed;
            RampRemaining = e.Remaining;
            RampCurrentTarget = roundedTarget;
            RampFractionComplete = e.FractionComplete;
            IsRampWarmingUp = e.IsWarmingUp;
            IsRampHolding = e.IsHolding;
            TargetTemperature = roundedTarget;
        });

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius) =>
        _dispatcher.Invoke(() =>
        {
            var resetValue = (int)Math.Round(resetTemperatureCelsius);

            IsRampRunning = false;
            IsRampWarmingUp = false;
            IsRampHolding = false;
            RampFractionComplete = 0;
            RampCurrentTarget = resetValue;
            TargetTemperature = resetValue;
            StatusMessage = "Ramp abgeschlossen.";
            _rampTrackStartedAtUtc = null;
            NotifyRampCommandsCanExecuteChanged();
        });

    private void OnRampErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            IsRampRunning = false;
            IsRampWarmingUp = false;
            IsRampHolding = false;
            _rampTrackStartedAtUtc = null;
            NotifyRampCommandsCanExecuteChanged();
        });

    public async ValueTask DisposeAsync()
    {
        _chartTimer.Stop();

        _logWindow.IsVisibleChanged -= OnLogWindowIsVisibleChanged;
        _settingsWindow.ViewModel.SettingsSaved -= OnSettingsSaved;

        _rampController.ProgressChanged -= OnRampProgressChanged;
        _rampController.WarmupCompleted -= OnRampWarmupCompleted;
        _rampController.Completed -= OnRampCompleted;
        _rampController.ErrorOccurred -= OnRampErrorOccurred;
        _rampController.Dispose();

        _service.ConnectionStateChanged -= OnServiceConnectionStateChanged;
        _service.ErrorOccurred -= OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged -= OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged -= OnServiceActivityChanged;
        _service.RemainingAutoOffSecondsChanged -= OnServiceRemainingAutoOffSecondsChanged;
        await _service.DisposeAsync();
    }
}
