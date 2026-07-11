using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services.Relay;

/// <summary>
/// Hosts a TCP listener that exposes an already-connected <see cref="IVolcanoDevice"/> and its
/// single shared <see cref="IRampSessionController"/> to other instances of this app on the LAN,
/// so they can control the same physical Volcano as if directly connected. Runs additively
/// alongside the local process's own use of that device/ramp controller - starting or stopping
/// hosting never touches the underlying Bluetooth connection.
///
/// Every request from every client is forwarded onto this one shared <see cref="IRampSessionController"/>
/// instance (also used for the local UI's own "Rampe starten" button), so its pre-existing
/// "IsRunning already? -> ignore" guard and unconditional Stop() give "first to start wins, anyone
/// can stop" for free across all participants.
/// </summary>
public sealed class VolcanoRelayServer : IAsyncDisposable
{
    private readonly IVolcanoDevice _device;
    private readonly IRampSessionController _ramp;
    private readonly LogService _logService;

    private readonly List<RelayConnection> _clients = new();
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

    public VolcanoRelayServer(IVolcanoDevice device, IRampSessionController ramp, LogService logService)
    {
        _device = device;
        _ramp = ramp;
        _logService = logService;
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

        _logService.Log($"LAN-Server gestartet auf Port {Port}.");
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

        List<RelayConnection> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = new List<RelayConnection>(_clients);
            _clients.Clear();
        }

        foreach (var client in clientsToClose)
        {
            await client.DisposeAsync();
        }

        _acceptCts?.Dispose();
        _listener = null;
        _acceptCts = null;
        _acceptLoop = null;
        Port = 0;

        _logService.Log("LAN-Server beendet.");
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
        var clientDescription = SafeDescribe(tcpClient);
        var registered = false;

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
                    Error = "PIN falsch.",
                });
                // Give the writer pump a chance to flush the rejection before the socket closes.
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                _logService.Log($"LAN-Client {clientDescription} abgelehnt: falsche PIN.", LogLevel.Warning);
                return;
            }

            connection.Send(new RelayMessage
            {
                Id = hello.Id,
                Kind = RelayMessageKind.Response,
                Result = JsonSerializer.SerializeToElement(new HelloResult(true, null), RelayJson.Options),
            });

            lock (_clientsLock)
            {
                _clients.Add(connection);
            }
            registered = true;

            _logService.Log($"LAN-Client {clientDescription} verbunden.");
            SendSnapshot(connection);

            while (true)
            {
                var message = await connection.ReceiveAsync(connection.Closed);
                if (message is null) break;
                if (message.Kind != RelayMessageKind.Request) continue;

                _ = ProcessRequestAsync(connection, message);
            }
        }
        catch
        {
            // Transport failure - fall through to cleanup below.
        }
        finally
        {
            if (registered)
            {
                lock (_clientsLock)
                {
                    _clients.Remove(connection);
                }
                _logService.Log($"LAN-Client {clientDescription} getrennt.");
            }

            await connection.DisposeAsync();
        }
    }

    private async Task ProcessRequestAsync(RelayConnection connection, RelayMessage request)
    {
        JsonElement? result = null;
        string? error;

        try
        {
            (result, error) = await DispatchAsync(request);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        connection.Send(new RelayMessage
        {
            Id = request.Id,
            Kind = RelayMessageKind.Response,
            Result = error is null ? result ?? JsonSerializer.SerializeToElement<object?>(null, RelayJson.Options) : null,
            Error = error,
        });
    }

    private async Task<(JsonElement? Result, string? Error)> DispatchAsync(RelayMessage request)
    {
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
                await _ramp.StartAsync(
                    args.StartTemperatureCelsius,
                    args.EndTemperatureCelsius,
                    args.Duration,
                    args.Method,
                    args.HoldDuration,
                    args.HeaterCurrentlyOn);
                return (Ok(), null);
            }

            case RelayMethods.StopRamp:
                _ramp.Stop();
                return (Ok(), null);

            default:
                return (null, $"Unbekannte Methode: {request.Method}");
        }
    }

    private static T RequireArgs<T>(RelayMessage request) =>
        request.Args is { } args
            ? args.Deserialize<T>(RelayJson.Options) ?? throw new InvalidOperationException("Fehlende Argumente.")
            : throw new InvalidOperationException("Fehlende Argumente.");

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
                client.Send(message);
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
