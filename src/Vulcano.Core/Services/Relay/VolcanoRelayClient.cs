using System.Net.Sockets;
using System.Text.Json;
using Vulcano.Core.Models;

namespace Vulcano.Core.Services.Relay;

/// <summary>
/// Client-side <see cref="IVolcanoDevice"/> that talks to a <see cref="VolcanoRelayServer"/> over
/// a single TCP connection instead of directly to the device over Bluetooth. RPC calls (all
/// <see cref="IVolcanoDevice"/> methods except <see cref="ScanAndConnectAsync"/>/
/// <see cref="DisconnectAsync"/>, which open/close this TCP link rather than the underlying BLE
/// connection) are GUID-correlated request/response pairs; the 5 device events are pushed
/// unprompted by the server and re-raised here directly from the read loop, on that background
/// thread - callers marshal onto the UI thread themselves, exactly as for the local BLE device.
///
/// The same TCP connection also carries ramp control/events for the paired
/// <see cref="RemoteRampController"/>, exposed here only via internal members since ramp control
/// is not part of <see cref="IVolcanoDevice"/>.
/// </summary>
public sealed class VolcanoRelayClient : IVolcanoDevice
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    private readonly string _host;
    private readonly int _port;
    private readonly string _pin;
    private readonly RelayClientRole _role;
    private readonly LogService _logService;

    private readonly object _pendingLock = new();
    private readonly Dictionary<string, TaskCompletionSource<RelayMessage>> _pending = new();

    private RelayConnection? _connection;
    private Task? _readLoop;
    private volatile bool _disconnecting;
    private ConnectionState _state = ConnectionState.Disconnected;

    public VolcanoRelayClient(
        string host,
        int port,
        string pin,
        RelayClientRole role,
        LogService logService)
    {
        _host = host;
        _port = port;
        _pin = pin;
        _role = role;
        _logService = logService;
    }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            ConnectionStateChanged?.Invoke(this, value);
        }
    }

    public bool IsRemote => true;

    public string? HostName => _host;

    /// <summary>What this client asked to be allowed to do when it joined.</summary>
    public RelayClientRole Role => _role;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

    internal event EventHandler<RampProgressEventArgs>? RampProgressChanged;
    internal event EventHandler? RampWarmupCompleted;
    internal event EventHandler<double>? RampCompleted;
    internal event EventHandler<string>? RampErrorOccurred;
    internal event EventHandler? RampStopped;

    public async Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Connected) return true;

        _disconnecting = false;
        State = ConnectionState.Connecting;

        TcpClient tcpClient;
        try
        {
            tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_host, _port, ct);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            ErrorOccurred?.Invoke(this, Strings.Get("Error.HostUnreachable", ex.Message));
            return false;
        }

        _connection = new RelayConnection(tcpClient);
        _readLoop = RunReadLoopAsync(_connection);

        try
        {
            var response = await SendRequestAsync(
                RelayMethods.Hello,
                new HelloArgs(_pin, Environment.MachineName, _role),
                HandshakeTimeout);

            if (response.Error is not null)
            {
                State = ConnectionState.Error;
                ErrorOccurred?.Invoke(this, response.Error);
                await TeardownAsync();
                return false;
            }
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            ErrorOccurred?.Invoke(this, Strings.Get("Error.ConnectToHostFailed", ex.Message));
            await TeardownAsync();
            return false;
        }

        _logService.Log(Strings.Get("Log.JoinedHost", _host, _port, _role));
        State = ConnectionState.Connected;
        return true;
    }

    public async Task DisconnectAsync()
    {
        _disconnecting = true;
        await TeardownAsync();
        State = ConnectionState.Disconnected;
    }

    public Task<double?> ReadTargetTemperatureAsync() =>
        SendReadRequestAsync<double>(RelayMethods.ReadTargetTemperature);

    public Task SetTargetTemperatureAsync(double celsius) =>
        SendVoidRequestAsync(RelayMethods.SetTargetTemperature, new SetTargetTemperatureArgs(celsius));

    public Task SetHeaterAsync(bool on) =>
        SendVoidRequestAsync(RelayMethods.SetHeater, new SetHeaterArgs(on));

    public Task SetPumpAsync(bool on) =>
        SendVoidRequestAsync(RelayMethods.SetPump, new SetPumpArgs(on));

    public Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync() =>
        SendReadRequestAsync<VolcanoDeviceInfo>(RelayMethods.ReadDeviceInfo);

    public Task<int?> ReadBrightnessAsync() =>
        SendReadRequestAsync<int>(RelayMethods.ReadBrightness);

    public Task SetBrightnessAsync(int level) =>
        SendVoidRequestAsync(RelayMethods.SetBrightness, new SetBrightnessArgs(level));

    public Task<int?> ReadAutoOffMinutesAsync() =>
        SendReadRequestAsync<int>(RelayMethods.ReadAutoOffMinutes);

    public Task SetAutoOffMinutesAsync(int minutes) =>
        SendVoidRequestAsync(RelayMethods.SetAutoOffMinutes, new SetAutoOffMinutesArgs(minutes));

    public async Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync()
    {
        var response = await SendRequestAsync(RelayMethods.ReadDisplayFlags, null, RequestTimeout);
        if (response.Error is not null)
        {
            ErrorOccurred?.Invoke(this, response.Error);
            return null;
        }
        if (response.Result is not { } result || result.ValueKind == JsonValueKind.Null) return null;

        var flags = result.Deserialize<RelayDisplayFlags>(RelayJson.Options)!;
        return (flags.Fahrenheit, flags.DisplayOnCooling);
    }

    public Task SetFahrenheitAsync(bool enabled) =>
        SendVoidRequestAsync(RelayMethods.SetFahrenheit, new SetFahrenheitArgs(enabled));

    public Task SetDisplayOnCoolingAsync(bool enabled) =>
        SendVoidRequestAsync(RelayMethods.SetDisplayOnCooling, new SetDisplayOnCoolingArgs(enabled));

    public Task<bool?> ReadVibrationAsync() =>
        SendReadRequestAsync<bool>(RelayMethods.ReadVibration);

    public Task SetVibrationAsync(bool enabled) =>
        SendVoidRequestAsync(RelayMethods.SetVibration, new SetVibrationArgs(enabled));

    internal Task<RelayMessage> SendRequestAsync(string method, object? args) =>
        SendRequestAsync(method, args, RequestTimeout);

    private async Task SendVoidRequestAsync(string method, object? args)
    {
        var response = await SendRequestAsync(method, args, RequestTimeout);
        if (response.Error is not null)
        {
            ErrorOccurred?.Invoke(this, response.Error);
        }
    }

    private async Task<T?> SendReadRequestAsync<T>(string method) where T : struct
    {
        var response = await SendRequestAsync(method, null, RequestTimeout);
        if (response.Error is not null)
        {
            ErrorOccurred?.Invoke(this, response.Error);
            return null;
        }
        if (response.Result is not { } result || result.ValueKind == JsonValueKind.Null) return null;

        return result.Deserialize<T>(RelayJson.Options);
    }

    private async Task<RelayMessage> SendRequestAsync(string method, object? args, TimeSpan timeout)
    {
        var connection = _connection ?? throw new InvalidOperationException("Not connected to a host.");

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RelayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        connection.Send(new RelayMessage
        {
            Id = id,
            Kind = RelayMessageKind.Request,
            Method = method,
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, RelayJson.Options),
        });

        using var cts = new CancellationTokenSource(timeout);
        using var registration = cts.Token.Register(static state => ((TaskCompletionSource<RelayMessage>)state!).TrySetCanceled(), tcs);

        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task RunReadLoopAsync(RelayConnection connection)
    {
        try
        {
            while (true)
            {
                var message = await connection.ReceiveAsync(connection.Closed);
                if (message is null) break;

                switch (message.Kind)
                {
                    case RelayMessageKind.Response:
                        DispatchResponse(message);
                        break;
                    case RelayMessageKind.Event:
                        DispatchEvent(message);
                        break;
                }
            }
        }
        finally
        {
            HandleReadLoopEnded();
        }
    }

    private void DispatchResponse(RelayMessage message)
    {
        TaskCompletionSource<RelayMessage>? tcs;
        lock (_pendingLock)
        {
            _pending.TryGetValue(message.Id, out tcs);
        }
        tcs?.TrySetResult(message);
    }

    private void DispatchEvent(RelayMessage message)
    {
        // The two events that carry no payload are sent with a JSON null, and a JSON null read back
        // into a JsonElement? is not an element holding null - it is no element at all. So they have
        // to be dispatched before any guard that reads a missing payload as nothing to do, or the
        // client silently never hears that warm-up finished or that the ramp was stopped.
        switch (message.Method)
        {
            case RelayEvents.RampWarmupCompleted:
                RampWarmupCompleted?.Invoke(this, EventArgs.Empty);
                return;

            case RelayEvents.RampStopped:
                RampStopped?.Invoke(this, EventArgs.Empty);
                return;
        }

        if (message.Args is not { } args) return;

        switch (message.Method)
        {
            case RelayEvents.ConnectionStateChanged:
                State = args.Deserialize<ConnectionStateChangedPayload>(RelayJson.Options)!.State;
                break;

            case RelayEvents.ErrorOccurred:
                ErrorOccurred?.Invoke(this, args.Deserialize<ErrorOccurredPayload>(RelayJson.Options)!.Message);
                break;

            case RelayEvents.CurrentTemperatureChanged:
                CurrentTemperatureChanged?.Invoke(this, args.Deserialize<CurrentTemperatureChangedPayload>(RelayJson.Options)!.Celsius);
                break;

            case RelayEvents.ActivityChanged:
                ActivityChanged?.Invoke(this, args.Deserialize<ActivityChangedPayload>(RelayJson.Options)!.Activity);
                break;

            case RelayEvents.RemainingAutoOffSecondsChanged:
                RemainingAutoOffSecondsChanged?.Invoke(this, args.Deserialize<RemainingAutoOffSecondsChangedPayload>(RelayJson.Options)!.Seconds);
                break;

            case RelayEvents.RampProgressChanged:
                RampProgressChanged?.Invoke(this, args.Deserialize<RampProgressEventArgs>(RelayJson.Options)!);
                break;

            case RelayEvents.RampCompleted:
                RampCompleted?.Invoke(this, args.Deserialize<RampCompletedPayload>(RelayJson.Options)!.ResetTemperatureCelsius);
                break;

            case RelayEvents.RampErrorOccurred:
                RampErrorOccurred?.Invoke(this, args.Deserialize<ErrorOccurredPayload>(RelayJson.Options)!.Message);
                break;
        }
    }

    private void HandleReadLoopEnded()
    {
        List<TaskCompletionSource<RelayMessage>> pendingCopy;
        lock (_pendingLock)
        {
            pendingCopy = new List<TaskCompletionSource<RelayMessage>>(_pending.Values);
            _pending.Clear();
        }
        foreach (var tcs in pendingCopy)
        {
            tcs.TrySetException(new IOException("The connection to the host was closed."));
        }

        if (_disconnecting) return;

        State = ConnectionState.Error;
        ErrorOccurred?.Invoke(this, Strings.Get("Error.ConnectionToHostLost"));
    }

    private async Task TeardownAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        if (_readLoop is not null)
        {
            try { await _readLoop; }
            catch { /* best-effort shutdown */ }
            _readLoop = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disconnecting = true;
        await TeardownAsync();
    }
}
