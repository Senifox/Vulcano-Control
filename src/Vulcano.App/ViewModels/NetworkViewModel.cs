using System;
using System.ComponentModel;
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

/// <summary>
/// One row of the host's client list.
///
/// An observable object rather than the record it used to be, because the latency arrives every few
/// seconds and only for one client at a time: rebuilding the collection at that rate would replace
/// every row, which throws away the selection and makes the list flicker for a number that changed
/// on one line.
/// </summary>
public sealed partial class ConnectedClientRow : ObservableObject
{
    public ConnectedClientRow(RelayClientInfo info)
    {
        Info = info;
        _latency = info.Latency;
    }

    public RelayClientInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    [NotifyPropertyChangedFor(nameof(HasLatency))]
    private TimeSpan? _latency;

    public string Name => Info.Name;

    public string Detail => Strings.Get("Network.Client.Since", Info.ConnectedAt.ToString("HH:mm"), Info.Address);

    public string Role => Strings.Get($"Network.Role.{Info.Role}");

    /// <summary>False for a client that never answers - one from before the host could time its
    /// clients. Nothing is shown for it rather than a dash that invites a bug report.</summary>
    public bool HasLatency => Latency is not null;

    public string LatencyText => Latency is { } latency
        ? Strings.Get("Network.Client.Latency", NetworkViewModel.FormatMilliseconds(latency))
        : "";
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

    /// <summary>
    /// Share this machine's device by itself, once it has one. Stored as HostOnStart because that
    /// is what the setting has always been called in settings.json; what it does is start the
    /// server when this instance connects to a Volcano, not when the app starts. A server with no
    /// device behind it is of no use to anyone who joins it, and starting one at launch would put
    /// this machine on the network before there was anything to share.
    /// </summary>
    [ObservableProperty]
    private bool _hostOnStart;

    /// <summary>Why hosting did not start. Shown in the sharing card rather than announced: it is
    /// read where somebody goes to find out why nobody can join.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostError))]
    private string _hostError = "";

    /// <summary>
    /// True once this session has started hosting by itself, so it happens at most once. Without
    /// it, a connection that drops and comes back would restart hosting that somebody had
    /// deliberately stopped in between.
    /// </summary>
    private bool _hasAutoHosted;

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
    [NotifyPropertyChangedFor(nameof(IsLatencyVisible))]
    private bool _isRemote;

    /// <summary>The last round trip to the host, or null when none came back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    [NotifyPropertyChangedFor(nameof(IsLatencySlow))]
    private TimeSpan? _latency;

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
        _device.RelayLatencyChanged += OnRelayLatencyChanged;
        _device.HostedClientLatencyChanged += OnHostedClientLatencyChanged;
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
        ? Strings.Get("Network.RemoteBanner", _device.HostName)
        : "";

    /// <summary>
    /// Above this, something is wrong with the path rather than merely busy: on a wired or
    /// reasonable wireless LAN this round trip is single-digit milliseconds, and a quarter of a
    /// second is the point at which pressing a button stops feeling like it did anything.
    /// </summary>
    private static readonly TimeSpan SlowLink = TimeSpan.FromMilliseconds(250);

    /// <summary>Only worth showing while this instance is actually borrowing someone's device.</summary>
    public bool IsLatencyVisible => IsRemote;

    public bool IsLatencySlow => Latency is { } latency && latency > SlowLink;

    /// <summary>
    /// The round trip, or that there was not one. Deliberately different sentences: an unanswered
    /// ping is not a slow connection, it is one that has stopped answering, and reading the timeout
    /// as "4000 ms" would say the opposite of what happened.
    /// </summary>
    public string LatencyText => Latency is { } latency
        ? Strings.Get("Network.Latency", FormatMilliseconds(latency))
        : Strings.Get("Network.Latency.NoAnswer");

    /// <summary>Whole milliseconds, and never "0 ms" - on a wired LAN this is genuinely under a
    /// millisecond, and a zero reads as a broken readout rather than a fast one.</summary>
    internal static string FormatMilliseconds(TimeSpan latency) =>
        latency.TotalMilliseconds < 1 ? "<1" : ((int)Math.Round(latency.TotalMilliseconds)).ToString();

    public string ClientsTitle => Strings.Get("Network.Clients", Clients.Count);

    public bool HasClients => Clients.Count > 0;

    [RelayCommand]
    private void StartHosting() => StartHosting(automatic: false);

    /// <param name="automatic">True when this came from the setting rather than the button. Only
    /// changes what is said about a failure: somebody who pressed a button is watching for the
    /// answer, somebody whose device just connected is not.</param>
    private void StartHosting(bool automatic)
    {
        HostError = "";

        try
        {
            _device.StartHosting(Port, Pin);
            IsHosting = _device.IsHosting;
            Persist();
            OnPropertyChanged(nameof(HostAddressText));
        }
        catch (Exception ex)
        {
            HostError = automatic
                ? Strings.Get("Network.Host.AutoFailed", ex.Message)
                : ex.Message;

            _log.Log(Strings.Get("Log.HostingFailed", ex.Message), LogLevel.Warning);
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
                JoinError = Strings.Get("Network.Join.Failed");
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

    /// <summary>One row's number, changed in place. A client that has just left has no row any
    /// more, which is not a problem - the measurement simply lands nowhere.</summary>
    private void OnHostedClientLatencyChanged(object? sender, RelayClientLatency measured) =>
        Dispatcher.UIThread.Post(() =>
        {
            var row = Clients.FirstOrDefault(c => c.Info.Id == measured.Id);
            if (row is not null) row.Latency = measured.Latency;
        });

    /// <summary>Arrives from the client's ping loop, off the UI thread like everything else here.</summary>
    private void OnRelayLatencyChanged(object? sender, TimeSpan? latency) =>
        Dispatcher.UIThread.Post(() => Latency = latency);

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsRemote = _device.IsRemote;
            IsHosting = _device.IsHosting;

            // Leaving takes the number with it: a millisecond count left over from a host this
            // instance is no longer talking to is worse than no number.
            if (!IsRemote) Latency = null;

            AutoHostIfWanted(state);
        });

    /// <summary>
    /// Starts sharing by itself, the moment this machine has a device of its own to share.
    ///
    /// Not at application start, which is what the setting used to be called and what it never in
    /// fact did: hosting before there is a device gives anyone who joins an empty connection to
    /// look at. And not when this instance is a client - it is borrowing someone else's device, and
    /// passing it on is not ours to do.
    /// </summary>
    private void AutoHostIfWanted(ConnectionState state)
    {
        if (!HostOnStart || _hasAutoHosted) return;
        if (state != ConnectionState.Connected || IsRemote || IsHosting) return;

        _hasAutoHosted = true;
        StartHosting(automatic: true);

        if (IsHosting) _log.Log(Strings.Get("Log.HostingAuto", _device.HostingPort ?? Port));
    }

    public bool HasHostError => HostError.Length > 0;

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
        _device.RelayLatencyChanged -= OnRelayLatencyChanged;
        _device.HostedClientLatencyChanged -= OnHostedClientLatencyChanged;
    }

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

