using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services.Relay;

/// <summary>What the host's client list shows for one connected participant.</summary>
/// <param name="Latency">The last measured round trip to this client, or null when there has not
/// been one. Null is also what a client from before the host could time its clients looks like -
/// it never answers the request, so there is nothing to report rather than something bad.</param>
public sealed record RelayClientInfo(
    Guid Id,
    string Name,
    string Address,
    RelayClientRole Role,
    DateTime ConnectedAt)
{
    public TimeSpan? Latency { get; init; }
}

/// <summary>One client's freshly measured round trip, as announced by the host.</summary>
public sealed record RelayClientLatency(Guid Id, TimeSpan? Latency);

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

        /// <summary>
        /// Replies this server is waiting for, by message id. The client has had one of these all
        /// along; the server needed none until it started asking clients something, which is what
        /// timing them requires.
        /// </summary>
        public Dictionary<string, TaskCompletionSource<RelayMessage>> Pending { get; } = new();

        public object PendingLock { get; } = new();

        /// <summary>The last round trip to this client, or null when none has come back.</summary>
        public TimeSpan? Latency { get; set; }

        /// <summary>
        /// Set once a ping to this client has gone unanswered, so the log says it a single time.
        /// A client from before this existed never answers, and it must not turn into a warning
        /// every few seconds for as long as it stays connected.
        /// </summary>
        public bool ReportedSilent { get; set; }

        public CancellationTokenSource? PingCts { get; set; }
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

    /// <summary>The plan the clients have already been told about, so it is sent once per run and
    /// not with every tick.</summary>
    private TemperatureRampPlan? _announcedPlan;

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

    /// <summary>Raised when one client's round trip has been measured again, on a background
    /// thread. Separate from <see cref="ClientsChanged"/> because it fires every few seconds per
    /// client, and rebuilding the whole list that often would throw away what the list is for.</summary>
    public event EventHandler<RelayClientLatency>? ClientLatencyChanged;

    public IReadOnlyList<RelayClientInfo> Clients
    {
        get
        {
            lock (_clientsLock)
            {
                // The latency is read off the session here rather than kept in the record, so a
                // snapshot taken at any moment carries the current number without the record having
                // to be replaced every time one arrives.
                return _clients.Select(c => c.Info with { Latency = c.Latency }).ToArray();
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
            StartPinging(session);

            while (true)
            {
                var message = await connection.ReceiveAsync(connection.Closed);
                if (message is null) break;

                // Answers to what this server asked - the only messages that ever travel this way
                // besides requests, and the reason the loop no longer skips everything else.
                if (message.Kind == RelayMessageKind.Response)
                {
                    CompletePending(session, message);
                    continue;
                }

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
                StopPinging(session);

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

    // --- Timing each client ---

    /// <summary>How often each connected client is timed.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    /// <summary>Past this a client is not slow, it is not answering - and a client old enough not
    /// to know the request will never answer at all.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(4);

    private void StartPinging(ClientSession session)
    {
        session.PingCts = new CancellationTokenSource();
        _ = RunPingLoopAsync(session, session.PingCts.Token);
    }

    private void StopPinging(ClientSession session)
    {
        var cts = session.PingCts;
        session.PingCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task RunPingLoopAsync(ClientSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var measured = await MeasureAsync(session, ct);
                if (ct.IsCancellationRequested) break;

                if (measured is null && !session.ReportedSilent)
                {
                    session.ReportedSilent = true;
                    _logService.Log(Strings.Get("Log.ClientNotTimed", session.Info.Name), LogLevel.Debug);
                }

                session.Latency = measured;
                ClientLatencyChanged?.Invoke(this, new RelayClientLatency(session.Info.Id, measured));

                await Task.Delay(PingInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The client left.
        }
    }

    /// <summary>
    /// One round trip to a client. Null when it did not come back, which covers both a client that
    /// has gone quiet and one built before it knew how to answer.
    /// </summary>
    private async Task<TimeSpan?> MeasureAsync(ClientSession session, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RelayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (session.PendingLock)
        {
            session.Pending[id] = tcs;
        }

        var started = Stopwatch.GetTimestamp();

        session.Connection.Send(new RelayMessage
        {
            Id = id,
            Kind = RelayMessageKind.Request,
            Method = RelayMethods.Ping,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Connection.Closed);
        timeout.CancelAfter(PingTimeout);
        using var registration = timeout.Token.Register(
            static state => ((TaskCompletionSource<RelayMessage>)state!).TrySetCanceled(), tcs);

        try
        {
            var response = await tcs.Task;
            return response.Error is null ? Stopwatch.GetElapsedTime(started) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (session.PendingLock)
            {
                session.Pending.Remove(id);
            }
        }
    }

    private static void CompletePending(ClientSession session, RelayMessage response)
    {
        TaskCompletionSource<RelayMessage>? tcs;
        lock (session.PendingLock)
        {
            session.Pending.TryGetValue(response.Id, out tcs);
        }
        tcs?.TrySetResult(response);
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
            // First, and deliberately not touching the device: this exists to be timed, and a
            // reply that waited on Bluetooth would measure the Volcano rather than the network.
            case RelayMethods.Ping:
                return (Ok(), null);

            case RelayMethods.ReadTargetTemperature:
                return (ToElement(await _device.ReadTargetTemperatureAsync()), null);

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
        // Neither interface has a "ramp started" event to hang the plan on, so it goes out as soon as
        // progress arrives for a plan nobody has been told about yet - which is the start of a run.
        // Reference equality is the right test: one plan object per run.
        if (!ReferenceEquals(_announcedPlan, _ramp.ActivePlan))
        {
            _announcedPlan = _ramp.ActivePlan;
            if (_announcedPlan is { } plan)
            {
                Broadcast(RelayEvents.RampPlanChanged, ToPlanPayload(plan));
            }
        }

        _lastRampProgress = progress;
        Broadcast(RelayEvents.RampProgressChanged, progress);
    }

    private static RampPlanPayload ToPlanPayload(TemperatureRampPlan plan) =>
        new(plan.Points, plan.HoldDuration);

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        Broadcast<object?>(RelayEvents.RampWarmupCompleted, null);

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius)
    {
        _lastRampProgress = null;
        _announcedPlan = null;
        Broadcast(RelayEvents.RampCompleted, new RampCompletedPayload(resetTemperatureCelsius));
    }

    private void OnRampErrorOccurred(object? sender, string message)
    {
        _lastRampProgress = null;
        _announcedPlan = null;
        Broadcast(RelayEvents.RampErrorOccurred, new ErrorOccurredPayload(message));
    }

    private void OnRampStopped(object? sender, EventArgs e)
    {
        _lastRampProgress = null;
        _announcedPlan = null;
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
            // Shape before numbers: the client builds its strip from the plan, and a progress event
            // arriving first would have it draw the bare fallback and rebuild a tick later.
            if (_ramp.ActivePlan is { } plan)
            {
                connection.Send(BuildEvent(RelayEvents.RampPlanChanged, ToPlanPayload(plan)));
            }

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
