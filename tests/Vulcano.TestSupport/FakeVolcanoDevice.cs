using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.TestSupport;

/// <summary>
/// A device that only records what was written to it and lets a test push notifications back.
/// This is the thing the transport interface buys us: the whole ramp state machine can be
/// exercised without a Volcano, a Bluetooth adapter, or an operating system that has either.
/// </summary>
public sealed class FakeVolcanoDevice : IVolcanoDevice
{
    private ConnectionState _state = ConnectionState.Connected;

    public List<double> WrittenTargets { get; } = new();
    public List<bool> WrittenHeaterStates { get; } = new();
    public List<bool> WrittenPumpStates { get; } = new();
    public List<int> WrittenBrightness { get; } = new();

    /// <summary>
    /// Makes the device stop answering: every read waits on this until it is completed. A real
    /// Volcano can be slow or wedged while its Bluetooth link is perfectly fine, and some things -
    /// timing the network between two machines, for one - have to keep working when it is.
    /// Use <see cref="Stall"/> and <see cref="Resume"/> rather than setting it directly.
    /// </summary>
    private TaskCompletionSource? _stall;

    /// <summary>Every read from here on hangs until <see cref="Resume"/>.</summary>
    public void Stall() => _stall ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resume()
    {
        var stall = _stall;
        _stall = null;
        stall?.TrySetResult();
    }

    private async Task<T> ReadAsync<T>(T value)
    {
        if (_stall is { } stall) await stall.Task;
        return value;
    }

    /// <summary>What the read methods hand back. Left null by default so a test has to say what the
    /// device knows before it can claim a round trip carried it.</summary>
    public VolcanoDeviceInfo? DeviceInfo { get; set; }
    public int? Brightness { get; set; }
    public int? AutoOffMinutes { get; set; }
    public (bool Fahrenheit, bool DisplayOnCooling)? DisplayFlags { get; set; }
    public bool? Vibration { get; set; }

    public ConnectionState State => _state;
    public bool IsRemote => false;
    public string? HostName => null;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

    /// <summary>Pretend the device just reported this temperature.</summary>
    public void ReportTemperature(double celsius) => CurrentTemperatureChanged?.Invoke(this, celsius);

    /// <summary>Pretend the device just reported the heater being on or off.</summary>
    public void ReportHeater(bool on) =>
        ActivityChanged?.Invoke(this, on ? VolcanoUuids.ActivityFlags.HeatingEnabled : (ushort)0);

    public void ReportConnectionState(ConnectionState state)
    {
        _state = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    public void ReportAutoOffSeconds(int seconds) =>
        RemainingAutoOffSecondsChanged?.Invoke(this, seconds);

    public void ReportError(string message) => ErrorOccurred?.Invoke(this, message);

    /// <summary>Announces the connection the way a device does. Without the event nothing above this
    /// ever learns it happened - the state property alone is not what the app listens to.</summary>
    public Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        ReportConnectionState(ConnectionState.Connected);
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        ReportConnectionState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetTargetTemperatureAsync(double celsius)
    {
        WrittenTargets.Add(celsius);
        return Task.CompletedTask;
    }

    public Task<double?> ReadTargetTemperatureAsync() =>
        ReadAsync<double?>(WrittenTargets.Count > 0 ? WrittenTargets[^1] : null);

    public Task SetHeaterAsync(bool on)
    {
        WrittenHeaterStates.Add(on);
        return Task.CompletedTask;
    }

    public Task SetPumpAsync(bool on)
    {
        WrittenPumpStates.Add(on);
        return Task.CompletedTask;
    }

    public Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync() => ReadAsync(DeviceInfo);
    public Task<int?> ReadBrightnessAsync() => ReadAsync(Brightness);

    public Task SetBrightnessAsync(int level)
    {
        WrittenBrightness.Add(level);
        return Task.CompletedTask;
    }

    public Task<int?> ReadAutoOffMinutesAsync() => ReadAsync(AutoOffMinutes);
    public Task SetAutoOffMinutesAsync(int minutes) => Task.CompletedTask;
    public Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync() => ReadAsync(DisplayFlags);
    public Task SetFahrenheitAsync(bool enabled) => Task.CompletedTask;
    public Task SetDisplayOnCoolingAsync(bool enabled) => Task.CompletedTask;
    public Task<bool?> ReadVibrationAsync() => ReadAsync(Vibration);
    public Task SetVibrationAsync(bool enabled) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
