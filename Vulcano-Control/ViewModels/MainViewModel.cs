using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Services;
using ConnectionState = Vulcano_Control.Models.ConnectionState;

namespace Vulcano_Control.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly VolcanoBluetoothService _service = new();
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    [ObservableProperty]
    private ConnectionState connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private double currentTemperature;

    [ObservableProperty]
    private double targetTemperature = 180.0;

    [ObservableProperty]
    private bool isHeaterOn;

    [ObservableProperty]
    private bool isPumpOn;

    [ObservableProperty]
    private string statusMessage = "Nicht verbunden";

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public MainViewModel()
    {
        _service.ConnectionStateChanged += OnServiceConnectionStateChanged;
        _service.ErrorOccurred += OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged += OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged += OnServiceActivityChanged;
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

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ApplyTargetTemperatureAsync()
    {
        await _service.SetTargetTemperatureAsync(TargetTemperature);
    }

    private bool CanConnect() =>
        ConnectionState is ConnectionState.Disconnected or ConnectionState.Error;

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
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleHeaterCommand.NotifyCanExecuteChanged();
        TogglePumpCommand.NotifyCanExecuteChanged();
        ApplyTargetTemperatureCommand.NotifyCanExecuteChanged();
    }

    private void OnServiceConnectionStateChanged(object? sender, ConnectionState state) =>
        _dispatcher.Invoke(() => ConnectionState = state);

    private void OnServiceErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() => StatusMessage = message);

    private void OnServiceCurrentTemperatureChanged(object? sender, double celsius) =>
        _dispatcher.BeginInvoke(() => CurrentTemperature = celsius);

    private void OnServiceActivityChanged(object? sender, ushort activity) =>
        _dispatcher.BeginInvoke(() =>
        {
            IsHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;
            IsPumpOn = (activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
        });

    public async ValueTask DisposeAsync()
    {
        _service.ConnectionStateChanged -= OnServiceConnectionStateChanged;
        _service.ErrorOccurred -= OnServiceErrorOccurred;
        _service.CurrentTemperatureChanged -= OnServiceCurrentTemperatureChanged;
        _service.ActivityChanged -= OnServiceActivityChanged;
        await _service.DisposeAsync();
    }
}
