using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

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

    public Task<bool> ScanAndConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SetTargetTemperatureAsync(double celsius)
    {
        WrittenTargets.Add(celsius);
        return Task.CompletedTask;
    }

    public Task<double?> ReadTargetTemperatureAsync() =>
        Task.FromResult<double?>(WrittenTargets.Count > 0 ? WrittenTargets[^1] : null);

    public Task SetHeaterAsync(bool on)
    {
        WrittenHeaterStates.Add(on);
        return Task.CompletedTask;
    }

    public Task SetPumpAsync(bool on) => Task.CompletedTask;

    public Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync() => Task.FromResult<VolcanoDeviceInfo?>(null);
    public Task<int?> ReadBrightnessAsync() => Task.FromResult<int?>(null);
    public Task SetBrightnessAsync(int level) => Task.CompletedTask;
    public Task<int?> ReadAutoOffMinutesAsync() => Task.FromResult<int?>(null);
    public Task SetAutoOffMinutesAsync(int minutes) => Task.CompletedTask;
    public Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync() =>
        Task.FromResult<(bool, bool)?>(null);
    public Task SetFahrenheitAsync(bool enabled) => Task.CompletedTask;
    public Task SetDisplayOnCoolingAsync(bool enabled) => Task.CompletedTask;
    public Task<bool?> ReadVibrationAsync() => Task.FromResult<bool?>(null);
    public Task SetVibrationAsync(bool enabled) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _ = ErrorOccurred;
        _ = RemainingAutoOffSecondsChanged;
        return ValueTask.CompletedTask;
    }
}
