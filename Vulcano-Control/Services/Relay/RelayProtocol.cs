using System.Text.Json;
using System.Text.Json.Serialization;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services.Relay;

public enum RelayMessageKind { Request, Response, Event }

/// <summary>
/// Single-line-JSON wire envelope used for every message exchanged between a
/// <see cref="VolcanoRelayClient"/>/<see cref="RemoteRampController"/> and a
/// <see cref="VolcanoRelayServer"/> - one per line over the raw TCP stream (see
/// <see cref="RelayJson"/> for the framing rules this depends on).
/// </summary>
public sealed class RelayMessage
{
    /// <summary>Correlates a Request with its Response; arbitrary but unique for Event messages.</summary>
    public required string Id { get; init; }

    public required RelayMessageKind Kind { get; init; }

    /// <summary>Method name (Request/Response) or event name (Event) - see <see cref="RelayMethods"/>/<see cref="RelayEvents"/>.</summary>
    public string? Method { get; init; }

    /// <summary>Request payload, or Event payload (events reuse this field rather than adding a
    /// third payload slot, since a Request is never itself resent as an Event).</summary>
    public JsonElement? Args { get; init; }

    /// <summary>Response only - present (possibly as JSON null) when <see cref="Error"/> is null.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>Response only - null means success.</summary>
    public string? Error { get; init; }
}

/// <summary>Wire-format settings shared by client and server - deliberately compact (no
/// WriteIndented), since messages are framed one-per-line and an indented payload would
/// contain literal newline bytes that break that framing.</summary>
public static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Request method names - the 13 forwarded IVolcanoDevice methods (everything except
/// ScanAndConnectAsync/DisconnectAsync, which mean "open/close the TCP link itself" for a client,
/// not "control BLE") plus the 2 ramp control methods, plus the handshake.</summary>
public static class RelayMethods
{
    public const string Hello = "Hello";

    public const string SetTargetTemperature = "SetTargetTemperature";
    public const string SetHeater = "SetHeater";
    public const string SetPump = "SetPump";
    public const string ReadDeviceInfo = "ReadDeviceInfo";
    public const string ReadBrightness = "ReadBrightness";
    public const string SetBrightness = "SetBrightness";
    public const string ReadAutoOffMinutes = "ReadAutoOffMinutes";
    public const string SetAutoOffMinutes = "SetAutoOffMinutes";
    public const string ReadDisplayFlags = "ReadDisplayFlags";
    public const string SetFahrenheit = "SetFahrenheit";
    public const string SetDisplayOnCooling = "SetDisplayOnCooling";
    public const string ReadVibration = "ReadVibration";
    public const string SetVibration = "SetVibration";

    public const string StartRamp = "StartRamp";
    public const string StopRamp = "StopRamp";
}

/// <summary>Event names pushed unprompted from server to client - the 5 IVolcanoDevice events
/// plus the 5 IRampSessionController events.</summary>
public static class RelayEvents
{
    public const string ConnectionStateChanged = "ConnectionStateChanged";
    public const string ErrorOccurred = "ErrorOccurred";
    public const string CurrentTemperatureChanged = "CurrentTemperatureChanged";
    public const string ActivityChanged = "ActivityChanged";
    public const string RemainingAutoOffSecondsChanged = "RemainingAutoOffSecondsChanged";

    public const string RampProgressChanged = "RampProgressChanged";
    public const string RampWarmupCompleted = "RampWarmupCompleted";
    public const string RampCompleted = "RampCompleted";
    public const string RampErrorOccurred = "RampErrorOccurred";
    public const string RampStopped = "RampStopped";
}

// --- Request argument DTOs (only for methods taking more than zero args) ---

public sealed record HelloArgs(string Pin);
public sealed record HelloResult(bool Accepted, string? Error);

public sealed record SetTargetTemperatureArgs(double Celsius);
public sealed record SetHeaterArgs(bool On);
public sealed record SetPumpArgs(bool On);
public sealed record SetBrightnessArgs(int Level);
public sealed record SetAutoOffMinutesArgs(int Minutes);
public sealed record SetFahrenheitArgs(bool Enabled);
public sealed record SetDisplayOnCoolingArgs(bool Enabled);
public sealed record SetVibrationArgs(bool Enabled);

public sealed record StartRampArgs(
    double StartTemperatureCelsius,
    double EndTemperatureCelsius,
    TimeSpan Duration,
    InterpolationMethod Method,
    TimeSpan HoldDuration,
    bool HeaterCurrentlyOn);

/// <summary>
/// Wire-only stand-in for IVolcanoDevice.ReadDisplayFlagsAsync's <c>(bool Fahrenheit, bool
/// DisplayOnCooling)</c> ValueTuple return - System.Text.Json serializes ValueTuple via its public
/// fields (Item1/Item2), which aren't included by default and don't round-trip by name, so this
/// converts to/from the tuple explicitly at the RPC boundary instead of fighting that.
/// </summary>
public sealed record RelayDisplayFlags(bool Fahrenheit, bool DisplayOnCooling);

// --- Event payload DTOs ---

public sealed record ConnectionStateChangedPayload(ConnectionState State);
public sealed record ErrorOccurredPayload(string Message);
public sealed record CurrentTemperatureChangedPayload(double Celsius);
public sealed record ActivityChangedPayload(ushort Activity);
public sealed record RemainingAutoOffSecondsChangedPayload(int Seconds);
public sealed record RampCompletedPayload(double ResetTemperatureCelsius);
// RampProgressChanged's payload is RampProgressEventArgs directly (already a clean record struct).
// RampWarmupCompleted carries no payload.
