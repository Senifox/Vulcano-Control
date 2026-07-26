using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>The seven views the window can show. Run only appears while a ramp is running.</summary>
public enum AppTab
{
    Control,
    Ramp,
    Run,
    Device,
    Network,
    Log,
    Settings
}

/// <summary>
/// Owns the window itself: which tab is showing, the connection state in the title bar, and the
/// one action next to it (Connect / Disconnect / Leave). The per-tab view models hang off this one
/// - this replaces the WPF version's single 972-line MainViewModel.
///
/// Every device event arrives on a background thread, so everything that touches observable state
/// goes through the dispatcher first.
/// </summary>
public partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly LogService _log;

    [ObservableProperty]
    private AppTab _selectedTab = AppTab.Control;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private string _serialNumber = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunTabVisible))]
    private bool _isRampRunning;

    [ObservableProperty]
    private bool _isAlwaysOnTop;

    public ShellViewModel(VolcanoDeviceOrchestrator device, AppSettings settings, LogService log)
    {
        _device = device;
        _log = log;

        Control = new ControlViewModel(device, settings);

        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.ProgressChanged += OnRampProgressChanged;
        _device.Completed += OnRampEnded;
        _device.Stopped += OnRampEnded;
    }

    /// <summary>The cockpit. Owns everything about live temperature, heater, pump and target.</summary>
    public ControlViewModel Control { get; }

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    /// <summary>Scanning or connecting - the point at which both buttons should be unavailable.</summary>
    public bool IsBusy => ConnectionState is ConnectionState.Scanning or ConnectionState.Connecting;

    /// <summary>True while this instance is driving someone else's device over the LAN relay.</summary>
    public bool IsRemote => _device.IsRemote;

    public string? HostName => _device.HostName;

    /// <summary>A remote client leaves the relay; it does not disconnect a Bluetooth link it never had.</summary>
    public string DisconnectLabel => IsRemote ? "Leave" : "Disconnect";

    public string ConnectionText => ConnectionState switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Scanning => "Searching for device…",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Error => "Connection lost",
        _ => "Not connected",
    };

    /// <summary>The Run tab is only offered while there is a run to look at.</summary>
    public bool IsRunTabVisible => IsRampRunning;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (await _device.ScanAndConnectAsync())
        {
            var info = await _device.ReadDeviceInfoAsync();
            if (info is { } deviceInfo)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SerialNumber = deviceInfo.SerialNumber);
            }
        }
    }

    private bool CanConnect() => !IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (IsRemote)
        {
            await _device.DisconnectFromServerAsync();
        }
        else
        {
            await _device.DisconnectAsync();
        }

        await Dispatcher.UIThread.InvokeAsync(() => SerialNumber = "");
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand]
    private void ShowTab(AppTab tab) => SelectedTab = tab;

    // --- Device events, all arriving off the UI thread ---

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionState = state;
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(HostName));
            OnPropertyChanged(nameof(DisconnectLabel));
        });

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsRampRunning) return;

            IsRampRunning = true;
            Control.IsRampRunning = true;
            // A ramp that starts anywhere - here, or on another machine through the relay - brings
            // the Run tab up by itself.
            SelectedTab = AppTab.Run;
        });

    private void OnRampEnded(object? sender, EventArgs e) => HandleRampEnded();

    private void OnRampEnded(object? sender, double resetTemperatureCelsius) => HandleRampEnded();

    private void HandleRampEnded() =>
        Dispatcher.UIThread.Post(() =>
        {
            IsRampRunning = false;
            Control.IsRampRunning = false;
            if (SelectedTab == AppTab.Run) SelectedTab = AppTab.Control;
        });

    public async ValueTask DisposeAsync()
    {
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
        _device.ProgressChanged -= OnRampProgressChanged;
        _device.Completed -= OnRampEnded;
        _device.Stopped -= OnRampEnded;
        Control.Dispose();

        _log.Log("Shutting down");
        _device.Dispose();
        await _device.DisposeAsync();
    }
}
