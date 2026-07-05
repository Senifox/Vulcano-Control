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

public partial class MainViewModel : ObservableObject, IAsyncDisposable
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
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromMinutes(120);

    private readonly VolcanoBluetoothService _service;
    private readonly RampSessionController _rampController;
    private readonly ThemeService _themeService;
    private readonly LogService _logService;
    private readonly LogWindow _logWindow;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _chartTimer;
    private readonly List<(DateTime TimeUtc, double Celsius)> _istHistory = new();
    private IReadOnlyList<(double Minutes, double Celsius)> _currentPlanSamples = Array.Empty<(double, double)>();
    private bool _suppressLogWindowVisibility;

    [ObservableProperty]
    private ConnectionState connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private int currentTemperature;

    [ObservableProperty]
    private int targetTemperature = 180;

    [ObservableProperty]
    private bool isHeaterOn;

    [ObservableProperty]
    private bool isPumpOn;

    [ObservableProperty]
    private string statusMessage = "Nicht verbunden";

    [ObservableProperty]
    private int rampDurationMinutes = 40;

    [ObservableProperty]
    private int rampStartTemperature = 185;

    [ObservableProperty]
    private int rampEndTemperature = 225;

    [ObservableProperty]
    private InterpolationMethod rampInterpolationMethod = InterpolationMethod.Linear;

    [ObservableProperty]
    private bool isRampRunning;

    [ObservableProperty]
    private bool isRampWarmingUp;

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

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool IsLightMode => CurrentTheme == AppTheme.Light;

    public bool IsDarkMode => CurrentTheme == AppTheme.Dark;

    public IReadOnlyList<InterpolationMethod> InterpolationMethods { get; } = Enum.GetValues<InterpolationMethod>();

    public MainViewModel(ThemeService themeService, LogService logService, LogWindow logWindow)
    {
        _themeService = themeService;
        _logService = logService;
        _logWindow = logWindow;
        currentTheme = _themeService.CurrentTheme;

        _service = new VolcanoBluetoothService(_logService);
        _rampController = new RampSessionController(_service, _logService);

        _service.ConnectionStateChanged += OnServiceConnectionStateChanged;
        _service.ErrorOccurred += OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged += OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged += OnServiceActivityChanged;

        _rampController.ProgressChanged += OnRampProgressChanged;
        _rampController.Completed += OnRampCompleted;
        _rampController.ErrorOccurred += OnRampErrorOccurred;

        _logWindow.IsVisibleChanged += OnLogWindowIsVisibleChanged;

        RampPlotModel = BuildEmptyPlotModel();
        ApplyChartTheme();
        RebuildPlotCurve();

        _chartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _chartTimer.Tick += (_, _) => RefreshChart();
        _chartTimer.Start();
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
    }

    [RelayCommand(CanExecute = nameof(CanStartRamp))]
    private async Task StartRampAsync()
    {
        if (RampDurationMinutes <= 0)
        {
            StatusMessage = "Dauer muss größer als 0 sein.";
            return;
        }
        if (RampStartTemperature is < 40 or > 230 || RampEndTemperature is < 40 or > 230)
        {
            StatusMessage = "Temperaturen müssen zwischen 40°C und 230°C liegen.";
            return;
        }
        if (Math.Abs(RampEndTemperature - RampStartTemperature) < 1)
        {
            StatusMessage = "Start- und Zieltemperatur müssen sich ausreichend unterscheiden.";
            return;
        }

        await _rampController.StartAsync(
            RampStartTemperature,
            RampEndTemperature,
            TimeSpan.FromMinutes(RampDurationMinutes),
            RampInterpolationMethod,
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
        NotifyRampCommandsCanExecuteChanged();
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        _themeService.SetTheme(theme);
        CurrentTheme = theme;
    }

    private bool CanConnect() =>
        ConnectionState is ConnectionState.Disconnected or ConnectionState.Error;

    private bool CanApplyManualTarget() => IsConnected && !IsRampRunning;

    private bool CanStartRamp() => IsConnected && !IsRampRunning;

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
        }

        if (value != ConnectionState.Connected)
        {
            CurrentTemperature = 0;
            TargetTemperature = 180;
            IsHeaterOn = false;
            IsPumpOn = false;
        }

        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleHeaterCommand.NotifyCanExecuteChanged();
        TogglePumpCommand.NotifyCanExecuteChanged();
        ApplyTargetTemperatureCommand.NotifyCanExecuteChanged();
        NotifyRampCommandsCanExecuteChanged();
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

    partial void OnRampDurationMinutesChanged(int value) => RebuildPlotCurve();
    partial void OnRampStartTemperatureChanged(int value) => RebuildPlotCurve();
    partial void OnRampEndTemperatureChanged(int value) => RebuildPlotCurve();
    partial void OnRampInterpolationMethodChanged(InterpolationMethod value) => RebuildPlotCurve();

    partial void OnRampCurrentTargetChanged(int value) => UpdatePlotMarkers();
    partial void OnCurrentTemperatureChanged(int value) => UpdatePlotMarkers();
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
            plan = new TemperatureRampPlan(
                RampStartTemperature,
                RampEndTemperature,
                TimeSpan.FromMinutes(RampDurationMinutes),
                RampInterpolationMethod);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        _currentPlanSamples = RampCurveSampler.Sample(plan);
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

        if (IsConnected)
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

        if (IsConnected)
        {
            _istHistory.Add((nowUtc, CurrentTemperature));
        }

        var cutoff = nowUtc - HistoryRetention;
        _istHistory.RemoveAll(p => p.TimeUtc < cutoff);

        var shiftMinutes = IsRampRunning ? RampElapsed.TotalMinutes : 0.0;

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

    private void OnServiceConnectionStateChanged(object? sender, ConnectionState state) =>
        _dispatcher.Invoke(() => ConnectionState = state);

    private void OnServiceErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() => StatusMessage = message);

    private void OnServiceCurrentTemperatureChanged(object? sender, double celsius) =>
        _dispatcher.BeginInvoke(() => CurrentTemperature = (int)Math.Round(celsius));

    private void OnServiceActivityChanged(object? sender, ushort activity) =>
        _dispatcher.BeginInvoke(() =>
        {
            IsHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
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
            TargetTemperature = roundedTarget;
        });

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius) =>
        _dispatcher.Invoke(() =>
        {
            var resetValue = (int)Math.Round(resetTemperatureCelsius);

            IsRampRunning = false;
            IsRampWarmingUp = false;
            RampFractionComplete = 0;
            RampCurrentTarget = resetValue;
            TargetTemperature = resetValue;
            StatusMessage = "Ramp abgeschlossen.";
            NotifyRampCommandsCanExecuteChanged();
        });

    private void OnRampErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            IsRampRunning = false;
            IsRampWarmingUp = false;
            NotifyRampCommandsCanExecuteChanged();
        });

    public async ValueTask DisposeAsync()
    {
        _chartTimer.Stop();

        _logWindow.IsVisibleChanged -= OnLogWindowIsVisibleChanged;

        _rampController.ProgressChanged -= OnRampProgressChanged;
        _rampController.Completed -= OnRampCompleted;
        _rampController.ErrorOccurred -= OnRampErrorOccurred;
        _rampController.Dispose();

        _service.ConnectionStateChanged -= OnServiceConnectionStateChanged;
        _service.ErrorOccurred -= OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged -= OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged -= OnServiceActivityChanged;
        await _service.DisposeAsync();
    }
}
