using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services.Relay;

/// <summary>What the host's client list shows for one connected participant.</summary>
public sealed record RelayClientInfo(
    Guid Id,
    string Name,
    string Address,
    RelayClientRole Role,
    DateTime ConnectedAt);

/// <summary>
/// Hosts a TCP listener that exposes an already-connected <see cref="IVolcanoDevice"/> and its
/// single shared <see cref="IRampSessionController"/> to other instances of this app on the LAN,
/// so they can control the same physical device as if directly connected. Runs additively
/// alongside the local process's own use of that device/ramp controller - starting or stopping
/// hosting never touches the underlying Bluetooth connection.
///
/// Every request from every client is forwarded onto this one shared <see cref="IRampSessionController"/>
/// instance (also used for the local UI's own "Start ramp" button), so its pre-existing
/// "IsRunning already? -> ignore" guard and unconditional Stop() give "first to start wins, anyone
/// can stop" for free across all participants.
/// </summary>
public sealed class VolcanoRelayServer : IAsyncDisposable
{
    private sealed class ClientSession
    {
        public required RelayConnection Connection { get; init; }
        public required RelayClientInfo Info { get; init; }
    }

    private readonly IVolcanoDevice _device;
    private readonly IRampSessionController _ramp;
    private readonly LogService _logService;

    private readonly List<ClientSession> _clients = new();
    private readonly object _clientsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private string _pin = string.Empty;

    private ConnectionState _lastConnectionState;
    private double _lastCurrentTemperature;
    private ushort _lastActivity;
    private int _lastRemainingAutoOffSeconds;
    private RampProgressEventArgs? _lastRampProgress;

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    /// <summary>Raised whenever a client joins, leaves or is revoked, on a background thread.</summary>
    public event EventHandler? ClientsChanged;

    public VolcanoRelayServer(IVolcanoDevice device, IRampSessionController ramp, LogService logService)
    {
        _device = device;
        _ramp = ramp;
        _logService = logService;
    }

    public IReadOnlyList<RelayClientInfo> Clients
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Select(c => c.Info).ToArray();
            }
        }
    }

    public void Start(int port, string pin)
    {
        if (IsRunning) return;

        _pin = pin;
        _lastConnectionState = _device.State;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        SubscribeToLocalEvents();

        _acceptCts = new CancellationTokenSource();
        _acceptLoop = RunAcceptLoopAsync(_acceptCts.Token);

        _logService.Log(Strings.Get("Log.LanServerStarted", Port));
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        UnsubscribeFromLocalEvents();

        _acceptCts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch { /* best-effort shutdown */ }
        }

        List<ClientSession> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = new List<ClientSession>(_clients);
            _clients.Clear();
        }

        foreach (var client in clientsToClose)
        {
            await client.Connection.DisposeAsync();
        }

        if (clientsToClose.Count > 0)
        {
            ClientsChanged?.Invoke(this, EventArgs.Empty);
        }

        _acceptCts?.Dispose();
        _listener = null;
        _acceptCts = null;
        _acceptLoop = null;
        Port = 0;

        _logService.Log(Strings.Get("Log.LanServerStopped"));
    }

    /// <summary>Drops one client. Its own read loop notices the closed socket and cleans up the
    /// registration, exactly as it would for any other disconnect.</summary>
    public async Task RevokeAsync(Guid clientId)
    {
        ClientSession? session;
        lock (_clientsLock)
        {
            session = _clients.FirstOrDefault(c => c.Info.Id == clientId);
        }

        if (session is null) return;

        _logService.Log(Strings.Get("Log.ClientRevoked", session.Info.Name));
        await session.Connection.DisposeAsync();
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch
            {
                // Cancellation or listener stopped - either way, the loop is done.
                break;
            }

            _ = HandleClientAsync(tcpClient, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverCt)
    {
        var connection = new RelayConnection(tcpClient);
        var address = SafeDescribe(tcpClient);
        ClientSession? session = null;

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));

            var hello = await connection.ReceiveAsync(handshakeCts.Token);
            if (hello is null || hello.Kind != RelayMessageKind.Request || hello.Method != RelayMethods.Hello)
            {
                return;
            }

            var helloArgs = hello.Args?.Deserialize<HelloArgs>(RelayJson.Options);
            if (helloArgs is null || helloArgs.Pin != _pin)
            {
                connection.Send(new RelayMessage
                {
                    Id = hello.Id,
                    Kind = RelayMessageKind.Response,
                    Error = Strings.Get("Error.WrongPin"),
                });
                // Give the writer pump a chance to flush the rejection before the socket closes.
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                _logService.Log(Strings.Get("Log.ClientRejected", address), LogLevel.Warning);
                return;
            }

            connection.Send(new RelayMessage
            {
                Id = hello.Id,
                Kind = RelayMessageKind.Response,
                Result = JsonSerializer.SerializeToElement(new HelloResult(true, null), RelayJson.Options),
            });

            session = new ClientSession
            {
                Connection = connection,
                Info = new RelayClientInfo(
                    Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(helloArgs.ClientName) ? address : helloArgs.ClientName,
                    address,
                    helloArgs.Role,
                    DateTime.Now),
            };

            lock (_clientsLock)
            {
                _clients.Add(session);
            }
            ClientsChanged?.Invoke(this, EventArgs.Empty);

            _logService.Log(Strings.Get("Log.ClientConnected", session.Info.Name, session.Info.Role));
            SendSnapshot(connection);

            while (true)
            {
                var message = await connection.ReceiveAsync(connection.Closed);
                if (message is null) break;
                if (message.Kind != RelayMessageKind.Request) continue;

                _ = ProcessRequestAsync(session, message);
            }
        }
        catch
        {
            // Transport failure - fall through to cleanup below.
        }
        finally
        {
            if (session is not null)
            {
                lock (_clientsLock)
                {
                    _clients.Remove(session);
                }
                ClientsChanged?.Invoke(this, EventArgs.Empty);
                _logService.Log(Strings.Get("Log.ClientDisconnected", session.Info.Name));
            }

            await connection.DisposeAsync();
        }
    }

    private async Task ProcessRequestAsync(ClientSession session, RelayMessage request)
    {
        JsonElement? result = null;
        string? error;

        try
        {
            (result, error) = await DispatchAsync(session, request);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        session.Connection.Send(new RelayMessage
        {
            Id = request.Id,
            Kind = RelayMessageKind.Response,
            Result = error is null ? result ?? JsonSerializer.SerializeToElement<object?>(null, RelayJson.Options) : null,
            Error = error,
        });
    }

    private async Task<(JsonElement? Result, string? Error)> DispatchAsync(ClientSession session, RelayMessage request)
    {
        if (session.Info.Role == RelayClientRole.Watching &&
            request.Method is { } method &&
            RelayMethods.MutatingMethods.Contains(method))
        {
            _logService.Log(Strings.Get("Log.WatcherRefused", session.Info.Name, method), LogLevel.Warning);
            return (null, Strings.Get("Error.WatcherRefused"));
        }

        switch (request.Method)
        {
            case RelayMethods.SetTargetTemperature:
                await _device.SetTargetTemperatureAsync(RequireArgs<SetTargetTemperatureArgs>(request).Celsius);
                return (Ok(), null);

            case RelayMethods.SetHeater:
                await _device.SetHeaterAsync(RequireArgs<SetHeaterArgs>(request).On);
                return (Ok(), null);

            case RelayMethods.SetPump:
                await _device.SetPumpAsync(RequireArgs<SetPumpArgs>(request).On);
                return (Ok(), null);

            case RelayMethods.ReadDeviceInfo:
                return (ToElement(await _device.ReadDeviceInfoAsync()), null);

            case RelayMethods.ReadBrightness:
                return (ToElement(await _device.ReadBrightnessAsync()), null);

            case RelayMethods.SetBrightness:
                await _device.SetBrightnessAsync(RequireArgs<SetBrightnessArgs>(request).Level);
                return (Ok(), null);

            case RelayMethods.ReadAutoOffMinutes:
                return (ToElement(await _device.ReadAutoOffMinutesAsync()), null);

            case RelayMethods.SetAutoOffMinutes:
                await _device.SetAutoOffMinutesAsync(RequireArgs<SetAutoOffMinutesArgs>(request).Minutes);
                return (Ok(), null);

            case RelayMethods.ReadDisplayFlags:
            {
                var flags = await _device.ReadDisplayFlagsAsync();
                var wire = flags is { } f ? new RelayDisplayFlags(f.Fahrenheit, f.DisplayOnCooling) : null;
                return (ToElement(wire), null);
            }

            case RelayMethods.SetFahrenheit:
                await _device.SetFahrenheitAsync(RequireArgs<SetFahrenheitArgs>(request).Enabled);
                return (Ok(), null);

            case RelayMethods.SetDisplayOnCooling:
                await _device.SetDisplayOnCoolingAsync(RequireArgs<SetDisplayOnCoolingArgs>(request).Enabled);
                return (Ok(), null);

            case RelayMethods.ReadVibration:
                return (ToElement(await _device.ReadVibrationAsync()), null);

            case RelayMethods.SetVibration:
                await _device.SetVibrationAsync(RequireArgs<SetVibrationArgs>(request).Enabled);
                return (Ok(), null);

            case RelayMethods.StartRamp:
            {
                var args = RequireArgs<StartRampArgs>(request);
                if (!TemperatureRampPlan.TryCreate(args.Points, args.HoldDuration, out var plan, out var errors))
                {
                    return (null, Strings.Get("Error.InvalidRamp", string.Join(", ", errors.Select(e => e.Issue))));
                }

                await _ramp.StartAsync(plan!, args.HeaterCurrentlyOn);
                return (Ok(), null);
            }

            case RelayMethods.StopRamp:
                _ramp.Stop();
                return (Ok(), null);

            case RelayMethods.PauseRamp:
                _ramp.Pause();
                return (Ok(), null);

            case RelayMethods.ResumeRamp:
                _ramp.Resume();
                return (Ok(), null);

            case RelayMethods.SkipRampSegment:
                _ramp.SkipSegment();
                return (Ok(), null);

            default:
                return (null, $"Unknown method: {request.Method}");
        }
    }

    private static T RequireArgs<T>(RelayMessage request) =>
        request.Args is { } args
            ? args.Deserialize<T>(RelayJson.Options) ?? throw new InvalidOperationException("Missing arguments.")
            : throw new InvalidOperationException("Missing arguments.");

    private static JsonElement Ok() => JsonSerializer.SerializeToElement(true, RelayJson.Options);

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, RelayJson.Options);

    private void SubscribeToLocalEvents()
    {
        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.ErrorOccurred += OnDeviceErrorOccurred;
        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ActivityChanged += OnActivityChanged;
        _device.RemainingAutoOffSecondsChanged += OnRemainingAutoOffSecondsChanged;

        _ramp.ProgressChanged += OnRampProgressChanged;
        _ramp.WarmupCompleted += OnRampWarmupCompleted;
        _ramp.Completed += OnRampCompleted;
        _ramp.ErrorOccurred += OnRampErrorOccurred;
        _ramp.Stopped += OnRampStopped;
    }

    private void UnsubscribeFromLocalEvents()
    {
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
        _device.ErrorOccurred -= OnDeviceErrorOccurred;
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ActivityChanged -= OnActivityChanged;
        _device.RemainingAutoOffSecondsChanged -= OnRemainingAutoOffSecondsChanged;

        _ramp.ProgressChanged -= OnRampProgressChanged;
        _ramp.WarmupCompleted -= OnRampWarmupCompleted;
        _ramp.Completed -= OnRampCompleted;
        _ramp.ErrorOccurred -= OnRampErrorOccurred;
        _ramp.Stopped -= OnRampStopped;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        _lastConnectionState = state;
        Broadcast(RelayEvents.ConnectionStateChanged, new ConnectionStateChangedPayload(state));
    }

    private void OnDeviceErrorOccurred(object? sender, string message) =>
        Broadcast(RelayEvents.ErrorOccurred, new ErrorOccurredPayload(message));

    private void OnCurrentTemperatureChanged(object? sender, double celsius)
    {
        _lastCurrentTemperature = celsius;
        Broadcast(RelayEvents.CurrentTemperatureChanged, new CurrentTemperatureChangedPayload(celsius));
    }

    private void OnActivityChanged(object? sender, ushort activity)
    {
        _lastActivity = activity;
        Broadcast(RelayEvents.ActivityChanged, new ActivityChangedPayload(activity));
    }

    private void OnRemainingAutoOffSecondsChanged(object? sender, int seconds)
    {
        _lastRemainingAutoOffSeconds = seconds;
        Broadcast(RelayEvents.RemainingAutoOffSecondsChanged, new RemainingAutoOffSecondsChangedPayload(seconds));
    }

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs progress)
    {
        _lastRampProgress = progress;
        Broadcast(RelayEvents.RampProgressChanged, progress);
    }

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        Broadcast<object?>(RelayEvents.RampWarmupCompleted, null);

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius)
    {
        _lastRampProgress = null;
        Broadcast(RelayEvents.RampCompleted, new RampCompletedPayload(resetTemperatureCelsius));
    }

    private void OnRampErrorOccurred(object? sender, string message)
    {
        _lastRampProgress = null;
        Broadcast(RelayEvents.RampErrorOccurred, new ErrorOccurredPayload(message));
    }

    private void OnRampStopped(object? sender, EventArgs e)
    {
        _lastRampProgress = null;
        Broadcast<object?>(RelayEvents.RampStopped, null);
    }

    private void SendSnapshot(RelayConnection connection)
    {
        connection.Send(BuildEvent(RelayEvents.ConnectionStateChanged, new ConnectionStateChangedPayload(_lastConnectionState)));
        connection.Send(BuildEvent(RelayEvents.CurrentTemperatureChanged, new CurrentTemperatureChangedPayload(_lastCurrentTemperature)));
        connection.Send(BuildEvent(RelayEvents.ActivityChanged, new ActivityChangedPayload(_lastActivity)));
        connection.Send(BuildEvent(RelayEvents.RemainingAutoOffSecondsChanged, new RemainingAutoOffSecondsChangedPayload(_lastRemainingAutoOffSeconds)));

        if (_ramp.IsRunning && _lastRampProgress is { } progress)
        {
            connection.Send(BuildEvent(RelayEvents.RampProgressChanged, progress));
        }
    }

    private void Broadcast<T>(string eventName, T payload)
    {
        var message = BuildEvent(eventName, payload);

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Connection.Send(message);
            }
        }
    }

    private static RelayMessage BuildEvent<T>(string eventName, T payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = RelayMessageKind.Event,
        Method = eventName,
        Args = JsonSerializer.SerializeToElement(payload, RelayJson.Options),
    };

    private static string SafeDescribe(TcpClient tcpClient)
    {
        try
        {
            return tcpClient.Client.RemoteEndPoint?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
