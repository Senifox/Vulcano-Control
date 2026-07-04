using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Models;
using Vulcano_Control.Services;
using ConnectionState = Vulcano_Control.Models.ConnectionState;

namespace Vulcano_Control.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
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
