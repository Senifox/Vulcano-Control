using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
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

    private readonly VolcanoBluetoothService _service = new();
    private readonly RampSessionController _rampController;
    private readonly ThemeService _themeService;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

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
    private PlotModel rampPlotModel = null!;

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool IsLightMode => CurrentTheme == AppTheme.Light;

    public bool IsDarkMode => CurrentTheme == AppTheme.Dark;

    public IReadOnlyList<InterpolationMethod> InterpolationMethods { get; } = Enum.GetValues<InterpolationMethod>();

    public MainViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        currentTheme = _themeService.CurrentTheme;

        _rampController = new RampSessionController(_service);

        _service.ConnectionStateChanged += OnServiceConnectionStateChanged;
        _service.ErrorOccurred += OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged += OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged += OnServiceActivityChanged;

        _rampController.ProgressChanged += OnRampProgressChanged;
        _rampController.Completed += OnRampCompleted;
        _rampController.ErrorOccurred += OnRampErrorOccurred;

        RampPlotModel = BuildEmptyPlotModel();
        ApplyChartTheme();
        RebuildPlotCurve();
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

        ClearIstHistory();

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

    partial void OnRampDurationMinutesChanged(int value) => RebuildPlotCurve();
    partial void OnRampStartTemperatureChanged(int value) => RebuildPlotCurve();
    partial void OnRampEndTemperatureChanged(int value) => RebuildPlotCurve();
    partial void OnRampInterpolationMethodChanged(InterpolationMethod value) => RebuildPlotCurve();

    partial void OnRampElapsedChanged(TimeSpan value)
    {
        UpdatePlotMarkers();
        AppendIstHistoryPoint();
    }

    partial void OnRampCurrentTargetChanged(int value) => UpdatePlotMarkers();
    partial void OnCurrentTemperatureChanged(int value) => UpdatePlotMarkers();
    partial void OnIsRampRunningChanged(bool value) => UpdatePlotMarkers();

    private static PlotModel BuildEmptyPlotModel()
    {
        var model = new PlotModel();

        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Zeit (min)", Minimum = 0 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Temperatur (°C)", Minimum = 30, Maximum = 235 });

        model.Series.Add(new LineSeries { Title = "Soll (geplant)", Color = SollColor, StrokeThickness = 2, TrackerFormatString = TrackerFormat });
        model.Series.Add(new ScatterSeries { Title = "Soll (aktuell)", MarkerType = MarkerType.Circle, MarkerFill = SollColor, MarkerSize = 6, TrackerFormatString = TrackerFormat });
        model.Series.Add(new ScatterSeries { Title = "Ist (gemessen)", MarkerType = MarkerType.Circle, MarkerFill = IstColor, MarkerSize = 6, TrackerFormatString = TrackerFormat });
        model.Series.Add(new LineSeries { Title = "Ist (Verlauf)", Color = IstColor, StrokeThickness = 2, TrackerFormatString = TrackerFormat });

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
            axis.MinorGridlineColor = border;
        }

        RampPlotModel.InvalidatePlot(false);
    }

    private void RebuildPlotCurve()
    {
        if (RampDurationMinutes <= 0) return;

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

        var samples = RampCurveSampler.Sample(plan);

        var curveSeries = (LineSeries)RampPlotModel.Series[0];
        curveSeries.Points.Clear();
        foreach (var (minutes, celsius) in samples)
        {
            curveSeries.Points.Add(new DataPoint(minutes, celsius));
        }

        RampPlotModel.InvalidatePlot(true);

        UpdatePlotMarkers();
    }

    private void UpdatePlotMarkers()
    {
        var sollMarker = (ScatterSeries)RampPlotModel.Series[1];
        var istMarker = (ScatterSeries)RampPlotModel.Series[2];

        sollMarker.Points.Clear();
        istMarker.Points.Clear();

        if (IsRampRunning)
        {
            var elapsedMinutes = RampElapsed.TotalMinutes;
            sollMarker.Points.Add(new ScatterPoint(elapsedMinutes, RampCurrentTarget));
            istMarker.Points.Add(new ScatterPoint(elapsedMinutes, CurrentTemperature));
        }

        RampPlotModel.InvalidatePlot(false);
    }

    private void AppendIstHistoryPoint()
    {
        if (!IsRampRunning) return;

        var istHistory = (LineSeries)RampPlotModel.Series[3];
        istHistory.Points.Add(new DataPoint(RampElapsed.TotalMinutes, CurrentTemperature));

        RampPlotModel.InvalidatePlot(false);
    }

    private void ClearIstHistory()
    {
        ((LineSeries)RampPlotModel.Series[3]).Points.Clear();
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
