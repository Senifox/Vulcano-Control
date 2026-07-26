using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Vulcano.Core.Services.Relay;

namespace Vulcano.App.ViewModels;

/// <summary>One row of the host's client list.</summary>
public sealed record ConnectedClientRow(RelayClientInfo Info)
{
    public string Name => Info.Name;

    public string Detail => $"since {Info.ConnectedAt:HH:mm} · {Info.Address}";

    public string Role => Info.Role == RelayClientRole.Controlling ? "controlling" : "watching";
}

/// <summary>
/// Hosting and joining on one page, as the design asks - the two used to be separate dialogs, and
/// which one you wanted was never the question you were actually asking.
/// </summary>
public partial class NetworkViewModel : ObservableObject, IDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly LogService _log;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostAddressText))]
    private bool _isHosting;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private string _pin = "";

    [ObservableProperty]
    private bool _hostOnStart;

    [ObservableProperty]
    private string _joinAddress = "";

    [ObservableProperty]
    private int _joinPort;

    [ObservableProperty]
    private string _joinPin = "";

    /// <summary>Joining to watch rather than to steer. The host sees which, and refuses writes from
    /// a watcher.</summary>
    [ObservableProperty]
    private bool _joinAsWatcher;

    [ObservableProperty]
    private string _joinError = "";

    [ObservableProperty]
    private bool _isJoining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHost))]
    [NotifyPropertyChangedFor(nameof(RemoteBanner))]
    private bool _isRemote;

    public NetworkViewModel(
        VolcanoDeviceOrchestrator device,
        SettingsService settingsService,
        AppSettings settings,
        LogService log)
    {
        _device = device;
        _settingsService = settingsService;
        _settings = settings;
        _log = log;

        _port = settings.RelayServerPort;
        _pin = settings.RelayPin;
        _hostOnStart = settings.HostOnStart;
        _joinAddress = settings.RelayLastHostAddress;
        _joinPort = settings.RelayServerPort;
        _joinPin = settings.RelayPin;

        LocalAddresses = string.Join(", ", FindLocalAddresses());

        _device.HostedClientsChanged += OnHostedClientsChanged;
        _device.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public ObservableCollection<ConnectedClientRow> Clients { get; } = new();

    /// <summary>Every address another machine on the LAN could reach us on.</summary>
    public string LocalAddresses { get; }

    public string HostAddressText => IsHosting
        ? $"{LocalAddresses} : {_device.HostingPort}"
        : LocalAddresses;

    /// <summary>Hosting and being a client are mutually exclusive - one machine holds the Bluetooth
    /// connection, the others borrow it.</summary>
    public bool CanHost => !IsRemote;

    public string RemoteBanner => IsRemote
        ? $"Connected through {_device.HostName}. The device is paired with that machine."
        : "";

    public string ClientsTitle => $"CONNECTED CLIENTS · {Clients.Count}";

    public bool HasClients => Clients.Count > 0;

    [RelayCommand]
    private void StartHosting()
    {
        try
        {
            _device.StartHosting(Port, Pin);
            IsHosting = _device.IsHosting;
            Persist();
            OnPropertyChanged(nameof(HostAddressText));
        }
        catch (Exception ex)
        {
            _log.Log($"Could not start hosting: {ex.Message}", LogLevel.Warning);
        }
    }

    [RelayCommand]
    private async Task StopHostingAsync()
    {
        await _device.StopHostingAsync();
        IsHosting = _device.IsHosting;
        RefreshClients();
    }

    /// <summary>A fresh four-digit PIN. Not a secret - it is a door latch for a home network, and
    /// the protocol says so where it is defined.</summary>
    [RelayCommand]
    private void NewPin()
    {
        Pin = Random.Shared.Next(1000, 10000).ToString();
        Persist();
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        JoinError = "";
        IsJoining = true;

        try
        {
            var role = JoinAsWatcher ? RelayClientRole.Watching : RelayClientRole.Controlling;
            var joined = await _device.ConnectToServerAsync(JoinAddress, JoinPort, JoinPin, role);

            if (!joined)
            {
                JoinError = "Could not join - check the address, the port and the PIN.";
                return;
            }

            _settings.RelayLastHostAddress = JoinAddress;
            Persist();
        }
        catch (Exception ex)
        {
            JoinError = ex.Message;
        }
        finally
        {
            IsJoining = false;
            IsRemote = _device.IsRemote;
        }
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        await _device.DisconnectFromServerAsync();
        IsRemote = _device.IsRemote;
    }

    [RelayCommand]
    private async Task RevokeAsync(ConnectedClientRow row) =>
        await _device.RevokeClientAsync(row.Info.Id);

    partial void OnPortChanged(int value) => Persist();

    partial void OnHostOnStartChanged(bool value) => Persist();

    private void Persist()
    {
        _settings.RelayServerPort = Port;
        _settings.RelayPin = Pin;
        _settings.HostOnStart = HostOnStart;
        _settingsService.Save(_settings);
    }

    private void RefreshClients()
    {
        Clients.Clear();
        foreach (var info in _device.HostedClients)
        {
            Clients.Add(new ConnectedClientRow(info));
        }

        OnPropertyChanged(nameof(ClientsTitle));
        OnPropertyChanged(nameof(HasClients));
    }

    private void OnHostedClientsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshClients);

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsRemote = _device.IsRemote;
            IsHosting = _device.IsHosting;
        });

    /// <summary>
    /// The machine's own LAN addresses. Loopback and link-local are filtered out: nobody else can
    /// reach us on those, so offering them would only produce a failed join.
    /// </summary>
    private static string[] FindLocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Where(a => !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Where(a => !a.StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        _device.HostedClientsChanged -= OnHostedClientsChanged;
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
