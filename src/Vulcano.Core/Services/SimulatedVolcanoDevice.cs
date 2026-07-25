using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// A Volcano that only exists in memory: it heats, cools, counts down its auto shut-off and
/// notifies on all of it, close enough to the real thing that the whole app can be built and
/// looked at without hardware. Also what the ramp tests run against.
///
/// Behaviour worth knowing when reading numbers off it:
/// heating is fast and rate-limited (the real device reaches 180 °C in well under a minute),
/// cooling is a slow exponential decay towards room temperature, and the auto shut-off only runs
/// while the heater is on - the same shape as the device, not the same physics.
/// </summary>
public sealed class SimulatedVolcanoDevice : IVolcanoDevice
{
    private static readonly TimeSpan DefaultNotifyInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultScanDuration = TimeSpan.FromSeconds(1.2);
    private const double AmbientCelsius = 22.0;
    private const double HeatRatePerSecond = 3.5;
    private const double CoolCoefficientPerSecond = 0.012;

    private readonly LogService _logService;
    private readonly TimeSpan _notifyInterval;
    private readonly TimeSpan _scanDuration;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ConnectionState _state = ConnectionState.Disconnected;

    private double _currentCelsius = AmbientCelsius;
    private double _targetCelsius = 185.0;
    private bool _heaterOn;
    private bool _pumpOn;
    private int _brightness = 70;
    private int _autoOffMinutes = 40;
    private int _remainingAutoOffSeconds;
    private bool _fahrenheit;
    private bool _displayOnCooling;
    private bool _vibration = true;

    /// <param name="notifyInterval">How often it reports a temperature. The real device sends
    /// roughly once a second; tests shorten it so a suite does not spend minutes waiting.</param>
    /// <param name="scanDuration">How long a scan pretends to take before connecting.</param>
    public SimulatedVolcanoDevice(
        LogService logService,
        TimeSpan? notifyInterval = null,
        TimeSpan? scanDuration = null)
    {
        _logService = logService;
        _notifyInterval = notifyInterval ?? DefaultNotifyInterval;
        _scanDuration = scanDuration ?? DefaultScanDuration;
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

    public bool IsRemote => false;
    public string? HostName => null;

    /// <summary>Where the simulation currently sits - handy for a test that wants to start warm.</summary>
    public double CurrentCelsius
    {
        get { lock (_lock) return _currentCelsius; }
        set { lock (_lock) _currentCelsius = value; }
    }

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

    public async Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Connected) return true;

        State = ConnectionState.Scanning;
        _logService.Log("Simulated device: scanning");

        try
        {
            await Task.Delay(_scanDuration, ct);
        }
        catch (OperationCanceledException)
        {
            State = ConnectionState.Disconnected;
            return false;
        }

        State = ConnectionState.Connecting;
        _logService.Log($"Simulated device: connecting to {SerialNumber}");

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);

        State = ConnectionState.Connected;
        _logService.Log("Simulated device: connected · firmware V1.63 / BLE V1.35");
        return true;
    }

    public async Task DisconnectAsync()
    {
        await StopLoopAsync();
        State = ConnectionState.Disconnected;
        _logService.Log("Simulated device: disconnected");
    }

    /// <summary>
    /// Pretends the connection dropped, without anyone asking for it - the state the app has to
    /// survive gracefully and which is otherwise awkward to produce on purpose.
    /// </summary>
    public async Task SimulateConnectionLossAsync()
    {
        if (State != ConnectionState.Connected) return;

        await StopLoopAsync();
        _logService.Log("Simulated device: connection lost", LogLevel.Warning);
        State = ConnectionState.Error;
        ErrorOccurred?.Invoke(this, "Connection to the device was lost");
    }

    public Task SetTargetTemperatureAsync(double celsius)
    {
        lock (_lock)
        {
            _targetCelsius = Math.Clamp(celsius, RampValidation.MinCelsius, RampValidation.MaxCelsius);
        }
        return Task.CompletedTask;
    }

    public Task SetHeaterAsync(bool on)
    {
        lock (_lock)
        {
            _heaterOn = on;
            // The device restarts its shut-off countdown whenever the heater comes on.
            _remainingAutoOffSeconds = on ? _autoOffMinutes * 60 : 0;
        }

        RaiseActivity();
        RaiseRemainingAutoOff();
        return Task.CompletedTask;
    }

    public Task SetPumpAsync(bool on)
    {
        lock (_lock) _pumpOn = on;
        RaiseActivity();
        return Task.CompletedTask;
    }

    private const string SerialNumber = "VCSIM0001";

    public Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync() =>
        Task.FromResult<VolcanoDeviceInfo?>(new VolcanoDeviceInfo(
            SerialNumber,
            FirmwareVersion: "V1.63",
            FirmwareBleVersion: "V1.35",
            HoursOfHeating: 412,
            MinutesOfHeating: 37));

    public Task<int?> ReadBrightnessAsync()
    {
        lock (_lock) return Task.FromResult<int?>(_brightness);
    }

    public Task SetBrightnessAsync(int level)
    {
        lock (_lock) _brightness = Math.Clamp(level, 0, 100);
        return Task.CompletedTask;
    }

    public Task<int?> ReadAutoOffMinutesAsync()
    {
        lock (_lock) return Task.FromResult<int?>(_autoOffMinutes);
    }

    public Task SetAutoOffMinutesAsync(int minutes)
    {
        lock (_lock)
        {
            _autoOffMinutes = Math.Clamp(minutes, 5, 360);
            if (_heaterOn) _remainingAutoOffSeconds = _autoOffMinutes * 60;
        }

        RaiseRemainingAutoOff();
        return Task.CompletedTask;
    }

    public Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync()
    {
        lock (_lock) return Task.FromResult<(bool, bool)?>((_fahrenheit, _displayOnCooling));
    }

    public Task SetFahrenheitAsync(bool enabled)
    {
        lock (_lock) _fahrenheit = enabled;
        return Task.CompletedTask;
    }

    public Task SetDisplayOnCoolingAsync(bool enabled)
    {
        lock (_lock) _displayOnCooling = enabled;
        return Task.CompletedTask;
    }

    public Task<bool?> ReadVibrationAsync()
    {
        lock (_lock) return Task.FromResult<bool?>(_vibration);
    }

    public Task SetVibrationAsync(bool enabled)
    {
        lock (_lock) _vibration = enabled;
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_notifyInterval);
        var lastTick = DateTime.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;
                var seconds = (now - lastTick).TotalSeconds;
                lastTick = now;

                var (temperature, autoOffFired) = Advance(seconds);

                CurrentTemperatureChanged?.Invoke(this, temperature);
                RaiseRemainingAutoOff();

                if (autoOffFired)
                {
                    _logService.Log("Simulated device: auto shut-off switched the heater off");
                    RaiseActivity();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnected.
        }
    }

    /// <summary>Advances the simulation by <paramref name="seconds"/> and reports the new
    /// temperature plus whether the auto shut-off just fired.</summary>
    private (double Temperature, bool AutoOffFired) Advance(double seconds)
    {
        lock (_lock)
        {
            var autoOffFired = false;

            if (_heaterOn && _remainingAutoOffSeconds > 0)
            {
                _remainingAutoOffSeconds = Math.Max(0, _remainingAutoOffSeconds - (int)Math.Round(seconds));
                if (_remainingAutoOffSeconds == 0)
                {
                    _heaterOn = false;
                    _pumpOn = false;
                    autoOffFired = true;
                }
            }

            if (_heaterOn)
            {
                var delta = _targetCelsius - _currentCelsius;
                if (delta > 0)
                {
                    // Rate-limited approach: fast, but never overshooting the target in one tick.
                    _currentCelsius += Math.Min(HeatRatePerSecond * seconds, delta);
                }
                else
                {
                    // Target lowered below where we are - the device just waits for it to cool.
                    _currentCelsius -= (_currentCelsius - _targetCelsius) * CoolCoefficientPerSecond * seconds;
                }
            }
            else
            {
                _currentCelsius -= (_currentCelsius - AmbientCelsius) * CoolCoefficientPerSecond * seconds;
            }

            return (Math.Round(_currentCelsius, 1), autoOffFired);
        }
    }

    private void RaiseActivity()
    {
        ushort activity = 0;

        lock (_lock)
        {
            if (_heaterOn) activity |= VolcanoUuids.ActivityFlags.HeatingEnabled;
            if (_pumpOn) activity |= VolcanoUuids.ActivityFlags.PumpEnabled;
            if (_autoOffMinutes > 0) activity |= VolcanoUuids.ActivityFlags.AutoShutdownEnabled;
        }

        ActivityChanged?.Invoke(this, activity);
    }

    private void RaiseRemainingAutoOff()
    {
        int seconds;
        lock (_lock) seconds = _remainingAutoOffSeconds;
        RemainingAutoOffSecondsChanged?.Invoke(this, seconds);
    }

    private async Task StopLoopAsync()
    {
        _cts?.Cancel();

        if (_loop is not null)
        {
            try { await _loop; }
            catch { /* best-effort shutdown */ }
            _loop = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopLoopAsync();
}
