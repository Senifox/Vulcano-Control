using Vulcano_Control.Models;
using Vulcano_Control.Services.Relay;

namespace Vulcano_Control.Services;

/// <summary>
/// Implements both <see cref="IVolcanoDevice"/> and <see cref="IRampSessionController"/> by
/// delegating to a swappable inner device/ramp-controller pair, so <c>MainViewModel</c> and
/// <c>DeviceSettingsViewModel</c> can depend on this one stable object without knowing whether the
/// process is currently talking to the Volcano directly over Bluetooth or through a
/// <see cref="VolcanoRelayServer"/>/<see cref="VolcanoRelayClient"/>.
///
/// Starts out exactly like the app did before this feature existed: a local
/// <see cref="VolcanoBluetoothService"/> plus its own <see cref="RampSessionController"/>.
/// <see cref="StartHostingAsync"/>/<see cref="StopHostingAsync"/> wrap that pair additively (no
/// reconnect, no interruption - "while already connected and running" is the whole point).
/// <see cref="ConnectToServerAsync"/>/<see cref="DisconnectFromServerAsync"/> are the more
/// disruptive operation: they swap the inner pair for a <see cref="VolcanoRelayClient"/>/
/// <see cref="RemoteRampController"/>, unsubscribing the old pair and resubscribing the new one so
/// external subscribers see nothing but the expected ConnectionStateChanged transitions.
/// </summary>
public sealed class VolcanoDeviceOrchestrator : IVolcanoDevice, IRampSessionController
{
    private readonly LogService _logService;

    private IVolcanoDevice _device;
    private IRampSessionController _ramp;
    private VolcanoRelayServer? _relayServer;
    private bool _isRemoteClient;

    private EventHandler<string>? _deviceErrorOccurred;
    private EventHandler<string>? _rampErrorOccurred;

    public VolcanoDeviceOrchestrator(LogService logService)
    {
        _logService = logService;

        _device = new VolcanoBluetoothService(logService);
        _ramp = new RampSessionController(_device, logService);
        SubscribeDevice(_device);
        SubscribeRamp(_ramp);
    }

    /// <summary>True once <see cref="StartHostingAsync"/> has successfully started a LAN server.</summary>
    public bool IsHosting => _relayServer is { IsRunning: true };

    /// <summary>The port the LAN server is listening on, or null if not currently hosting.</summary>
    public int? HostingPort => IsHosting ? _relayServer!.Port : null;

    /// <summary>True while this instance is a relay client of someone else's server rather than
    /// talking to a Volcano directly over Bluetooth.</summary>
    public bool IsRemoteClient => _isRemoteClient;

    // --- IVolcanoDevice ---

    public ConnectionState State => _device.State;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    event EventHandler<string>? IVolcanoDevice.ErrorOccurred
    {
        add => _deviceErrorOccurred += value;
        remove => _deviceErrorOccurred -= value;
    }

    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

    public Task<bool> ScanAndConnectAsync(CancellationToken ct = default) => _device.ScanAndConnectAsync(ct);
    public Task DisconnectAsync() => _device.DisconnectAsync();

    public Task SetTargetTemperatureAsync(double celsius) => _device.SetTargetTemperatureAsync(celsius);
    public Task SetHeaterAsync(bool on) => _device.SetHeaterAsync(on);
    public Task SetPumpAsync(bool on) => _device.SetPumpAsync(on);

    public Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync() => _device.ReadDeviceInfoAsync();
    public Task<int?> ReadBrightnessAsync() => _device.ReadBrightnessAsync();
    public Task SetBrightnessAsync(int level) => _device.SetBrightnessAsync(level);
    public Task<int?> ReadAutoOffMinutesAsync() => _device.ReadAutoOffMinutesAsync();
    public Task SetAutoOffMinutesAsync(int minutes) => _device.SetAutoOffMinutesAsync(minutes);
    public Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync() => _device.ReadDisplayFlagsAsync();
    public Task SetFahrenheitAsync(bool enabled) => _device.SetFahrenheitAsync(enabled);
    public Task SetDisplayOnCoolingAsync(bool enabled) => _device.SetDisplayOnCoolingAsync(enabled);
    public Task<bool?> ReadVibrationAsync() => _device.ReadVibrationAsync();
    public Task SetVibrationAsync(bool enabled) => _device.SetVibrationAsync(enabled);

    // --- IRampSessionController ---

    public int PushThresholdCelsius
    {
        get => _ramp.PushThresholdCelsius;
        set => _ramp.PushThresholdCelsius = value;
    }

    public bool IsRunning => _ramp.IsRunning;

    public event EventHandler<RampProgressEventArgs>? ProgressChanged;
    public event EventHandler? WarmupCompleted;
    public event EventHandler<double>? Completed;

    event EventHandler<string>? IRampSessionController.ErrorOccurred
    {
        add => _rampErrorOccurred += value;
        remove => _rampErrorOccurred -= value;
    }

    public event EventHandler? Stopped;

    public Task StartAsync(
        double startTemperatureCelsius,
        double endTemperatureCelsius,
        TimeSpan duration,
        InterpolationMethod method,
        TimeSpan holdDuration,
        bool heaterCurrentlyOn) =>
        _ramp.StartAsync(startTemperatureCelsius, endTemperatureCelsius, duration, method, holdDuration, heaterCurrentlyOn);

    public void Stop() => _ramp.Stop();

    // --- Hosting (additive - wraps the currently active local device/ramp pair) ---

    public void StartHosting(int port, string pin)
    {
        if (IsHosting) return;
        if (_isRemoteClient)
        {
            throw new InvalidOperationException(
                "Es kann nicht gehostet werden, während man selbst mit einem fremden Server verbunden ist.");
        }

        _relayServer ??= new VolcanoRelayServer(_device, _ramp, _logService);
        _relayServer.Start(port, pin);
    }

    public async Task StopHostingAsync()
    {
        if (_relayServer is null) return;
        await _relayServer.StopAsync();
    }

    // --- Client role switching (disruptive - swaps the inner device/ramp pair) ---

    public async Task<bool> ConnectToServerAsync(string host, int port, string pin)
    {
        if (IsHosting)
        {
            throw new InvalidOperationException(
                "Es kann nicht einem Server beigetreten werden, während man selbst gerade hostet.");
        }

        var previousDevice = _device;
        var previousRamp = _ramp;
        var previousWasRemoteClient = _isRemoteClient;

        UnsubscribeDevice(previousDevice);
        UnsubscribeRamp(previousRamp);

        var client = new VolcanoRelayClient(host, port, pin, _logService);
        var remoteRamp = new RemoteRampController(client);

        _device = client;
        _ramp = remoteRamp;
        _isRemoteClient = true;

        SubscribeDevice(_device);
        SubscribeRamp(_ramp);

        var connected = await client.ScanAndConnectAsync();

        if (!connected)
        {
            // Roll back so a failed join doesn't cost the user a working local connection.
            UnsubscribeDevice(_device);
            UnsubscribeRamp(_ramp);
            await client.DisposeAsync();
            remoteRamp.Dispose();

            _device = previousDevice;
            _ramp = previousRamp;
            _isRemoteClient = previousWasRemoteClient;

            SubscribeDevice(_device);
            SubscribeRamp(_ramp);
            ConnectionStateChanged?.Invoke(this, _device.State);
            return false;
        }

        if (previousWasRemoteClient)
        {
            await previousDevice.DisposeAsync();
        }
        else
        {
            await previousDevice.DisconnectAsync();
        }
        previousRamp.Dispose();

        return true;
    }

    public async Task DisconnectFromServerAsync()
    {
        if (!_isRemoteClient) return;

        UnsubscribeDevice(_device);
        UnsubscribeRamp(_ramp);

        var oldDevice = _device;
        var oldRamp = _ramp;

        await oldDevice.DisposeAsync();
        oldRamp.Dispose();

        _device = new VolcanoBluetoothService(_logService);
        _ramp = new RampSessionController(_device, _logService);
        _isRemoteClient = false;

        SubscribeDevice(_device);
        SubscribeRamp(_ramp);
        ConnectionStateChanged?.Invoke(this, _device.State);
    }

    // --- Event passthrough plumbing ---

    private void SubscribeDevice(IVolcanoDevice device)
    {
        device.ConnectionStateChanged += OnDeviceConnectionStateChanged;
        device.ErrorOccurred += OnDeviceErrorOccurred;
        device.CurrentTemperatureChanged += OnDeviceCurrentTemperatureChanged;
        device.ActivityChanged += OnDeviceActivityChanged;
        device.RemainingAutoOffSecondsChanged += OnDeviceRemainingAutoOffSecondsChanged;
    }

    private void UnsubscribeDevice(IVolcanoDevice device)
    {
        device.ConnectionStateChanged -= OnDeviceConnectionStateChanged;
        device.ErrorOccurred -= OnDeviceErrorOccurred;
        device.CurrentTemperatureChanged -= OnDeviceCurrentTemperatureChanged;
        device.ActivityChanged -= OnDeviceActivityChanged;
        device.RemainingAutoOffSecondsChanged -= OnDeviceRemainingAutoOffSecondsChanged;
    }

    private void OnDeviceConnectionStateChanged(object? sender, ConnectionState state) =>
        ConnectionStateChanged?.Invoke(this, state);

    private void OnDeviceErrorOccurred(object? sender, string message) =>
        _deviceErrorOccurred?.Invoke(this, message);

    private void OnDeviceCurrentTemperatureChanged(object? sender, double celsius) =>
        CurrentTemperatureChanged?.Invoke(this, celsius);

    private void OnDeviceActivityChanged(object? sender, ushort activity) =>
        ActivityChanged?.Invoke(this, activity);

    private void OnDeviceRemainingAutoOffSecondsChanged(object? sender, int seconds) =>
        RemainingAutoOffSecondsChanged?.Invoke(this, seconds);

    private void SubscribeRamp(IRampSessionController ramp)
    {
        ramp.ProgressChanged += OnRampProgressChanged;
        ramp.WarmupCompleted += OnRampWarmupCompleted;
        ramp.Completed += OnRampCompleted;
        ramp.ErrorOccurred += OnRampErrorOccurred;
        ramp.Stopped += OnRampStopped;
    }

    private void UnsubscribeRamp(IRampSessionController ramp)
    {
        ramp.ProgressChanged -= OnRampProgressChanged;
        ramp.WarmupCompleted -= OnRampWarmupCompleted;
        ramp.Completed -= OnRampCompleted;
        ramp.ErrorOccurred -= OnRampErrorOccurred;
        ramp.Stopped -= OnRampStopped;
    }

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) => ProgressChanged?.Invoke(this, e);
    private void OnRampWarmupCompleted(object? sender, EventArgs e) => WarmupCompleted?.Invoke(this, e);
    private void OnRampCompleted(object? sender, double resetTemperatureCelsius) => Completed?.Invoke(this, resetTemperatureCelsius);
    private void OnRampErrorOccurred(object? sender, string message) => _rampErrorOccurred?.Invoke(this, message);
    private void OnRampStopped(object? sender, EventArgs e) => Stopped?.Invoke(this, e);

    // --- Disposal - IRampSessionController.Dispose() and IVolcanoDevice.DisposeAsync() are both
    // called once each by MainViewModel's own DisposeAsync(), matching how it already disposes
    // its RampSessionController and VolcanoBluetoothService separately today. ---

    public void Dispose()
    {
        UnsubscribeRamp(_ramp);
        _ramp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_relayServer is not null)
        {
            await _relayServer.DisposeAsync();
            _relayServer = null;
        }

        UnsubscribeDevice(_device);
        await _device.DisposeAsync();
    }
}
