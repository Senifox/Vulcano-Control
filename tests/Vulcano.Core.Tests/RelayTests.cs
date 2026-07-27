using System.Net.Sockets;
using System.Text.Json;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Vulcano.Core.Services.Relay;

namespace Vulcano.Core.Tests;

/// <summary>
/// The relay, server and client against each other over the loopback interface: real sockets, real
/// line framing, real JSON - the LAN feature as it runs between two machines, minus the second
/// machine. Nothing here is mocked except the Volcano itself, which is the one part that cannot be.
///
/// The host runs a real <see cref="RampSessionController"/>, so "a client starts the host's ramp"
/// means the ramp actually runs and writes to the device on the other side.
/// </summary>
public sealed class RelayTests
{
    private const string Pin = "2468";

    private static readonly RampPoint[] Points =
    [
        new(0, 180, CurveKind.Linear),
        new(10, 200, CurveKind.Linear),
    ];

    /// <summary>
    /// One hosting instance: a fake device, a real ramp controller, the server, and whatever clients
    /// a test joins to it. Port 0 asks the OS for a free port, so tests never collide with each
    /// other or with anything else on the machine.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"vulcano-relay-{Guid.NewGuid():N}.log");
        private readonly List<VolcanoRelayClient> _clients = new();
        private readonly List<RelayConnection> _rawClients = new();

        public FakeVolcanoDevice Device { get; } = new();
        public LogService Log { get; }
        public RampSessionController Ramp { get; }
        public VolcanoRelayServer Server { get; }

        public int ClientsChangedCount;

        public Host()
        {
            Log = new LogService(_logFile);
            Ramp = new RampSessionController(Device, Log, TimeSpan.FromMilliseconds(25));
            Server = new VolcanoRelayServer(Device, Ramp, Log);
            Server.ClientsChanged += (_, _) => Interlocked.Increment(ref ClientsChangedCount);
            Server.Start(0, Pin);
        }

        /// <summary>A client, not yet connected - so a test can subscribe to its events first, which
        /// is what the app does and the only way to see the snapshot the host sends on accept.</summary>
        public VolcanoRelayClient CreateClient(
            RelayClientRole role = RelayClientRole.Controlling,
            string pin = Pin)
        {
            var client = new VolcanoRelayClient("127.0.0.1", Server.Port, pin, role, Log);
            _clients.Add(client);
            return client;
        }

        public async Task<VolcanoRelayClient> JoinAsync(RelayClientRole role = RelayClientRole.Controlling)
        {
            var client = CreateClient(role);
            Assert.True(await client.ScanAndConnectAsync(), "the client should have been accepted");
            return client;
        }

        /// <summary>
        /// A peer that speaks the wire protocol by hand, for the things a well-behaved
        /// <see cref="VolcanoRelayClient"/> cannot express: a bad first message, an unknown method,
        /// or a ramp the host should refuse. The server has to survive all three.
        /// </summary>
        public async Task<RelayConnection> ConnectRawAsync()
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", Server.Port);
            var connection = new RelayConnection(tcp);
            _rawClients.Add(connection);
            return connection;
        }

        public async Task<RelayMessage?> SayHelloAsync(RelayConnection connection)
        {
            connection.Send(Request(RelayMethods.Hello, new HelloArgs(Pin, "raw", RelayClientRole.Controlling)));
            return await ReadResponseAsync(connection);
        }

        public static RelayMessage Request(string method, object? args) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = RelayMessageKind.Request,
            Method = method,
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, RelayJson.Options),
        };

        /// <summary>Reads past the events the host pushes unprompted to the next actual response.</summary>
        public static async Task<RelayMessage?> ReadResponseAsync(RelayConnection connection)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await connection.ReceiveAsync(cts.Token);
                if (message is null || message.Kind == RelayMessageKind.Response) return message;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) await client.DisposeAsync();
            foreach (var raw in _rawClients) await raw.DisposeAsync();
            await Server.DisposeAsync();
            Ramp.Dispose();
            try { File.Delete(_logFile); } catch { /* best-effort */ }
        }
    }

    // --- Joining ---

    [Fact]
    public async Task A_client_with_the_right_pin_is_accepted_and_shows_up_in_the_hosts_list()
    {
        await using var host = new Host();

        var client = await host.JoinAsync();

        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True(client.IsRemote);
        Assert.Equal("127.0.0.1", client.HostName);

        await Wait.ForAsync(() => host.Server.Clients.Count == 1, "the host to list the client");
        var listed = Assert.Single(host.Server.Clients);
        Assert.Equal(Environment.MachineName, listed.Name);
        Assert.Equal(RelayClientRole.Controlling, listed.Role);
    }

    [Fact]
    public async Task A_client_with_the_wrong_pin_is_refused_and_never_shows_up()
    {
        await using var host = new Host();
        var client = host.CreateClient(pin: "0000");

        string? reportedError = null;
        client.ErrorOccurred += (_, message) => reportedError = message;

        Assert.False(await client.ScanAndConnectAsync());
        Assert.Equal(ConnectionState.Error, client.State);
        Assert.False(string.IsNullOrWhiteSpace(reportedError));
        Assert.Empty(host.Server.Clients);
    }

    [Fact]
    public async Task Joining_hands_the_client_what_the_host_already_knew()
    {
        await using var host = new Host();

        // The host has been watching the device for a while before anyone joins.
        host.Device.ReportTemperature(151);
        host.Device.ReportHeater(true);
        host.Device.ReportAutoOffSeconds(240);

        var client = host.CreateClient();
        double? temperature = null;
        ushort? activity = null;
        int? autoOff = null;
        client.CurrentTemperatureChanged += (_, c) => temperature = c;
        client.ActivityChanged += (_, a) => activity = a;
        client.RemainingAutoOffSecondsChanged += (_, s) => autoOff = s;

        Assert.True(await client.ScanAndConnectAsync());

        await Wait.ForAsync(
            () => temperature is not null && activity is not null && autoOff is not null,
            "the snapshot to arrive");

        Assert.Equal(151, temperature);
        Assert.Equal(VolcanoUuids.ActivityFlags.HeatingEnabled, activity);
        Assert.Equal(240, autoOff);
    }

    // --- Forwarding ---

    [Fact]
    public async Task A_write_from_a_client_reaches_the_hosts_device()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();

        await client.SetTargetTemperatureAsync(195);
        await client.SetHeaterAsync(true);
        await client.SetPumpAsync(true);
        await client.SetBrightnessAsync(70);

        Assert.Equal(195, Assert.Single(host.Device.WrittenTargets));
        Assert.True(Assert.Single(host.Device.WrittenHeaterStates));
        Assert.True(Assert.Single(host.Device.WrittenPumpStates));
        Assert.Equal(70, Assert.Single(host.Device.WrittenBrightness));
    }

    [Fact]
    public async Task A_read_from_a_client_comes_back_with_the_hosts_value()
    {
        await using var host = new Host();
        host.Device.DeviceInfo = new VolcanoDeviceInfo("VH8H9H7G00", "V01.03.00.00", "V01.02.00.00", 4422, 17);
        host.Device.Brightness = 70;
        host.Device.AutoOffMinutes = 30;
        host.Device.Vibration = false;
        host.Device.DisplayFlags = (Fahrenheit: false, DisplayOnCooling: true);

        var client = await host.JoinAsync();
        await client.SetTargetTemperatureAsync(195);

        Assert.Equal(195, await client.ReadTargetTemperatureAsync());
        Assert.Equal(70, await client.ReadBrightnessAsync());
        Assert.Equal(30, await client.ReadAutoOffMinutesAsync());

        // False has to survive as false rather than come back as "no answer".
        Assert.False(await client.ReadVibrationAsync());

        var info = await client.ReadDeviceInfoAsync();
        Assert.Equal("VH8H9H7G00", info!.Value.SerialNumber);
        Assert.Equal(4422, info.Value.HoursOfHeating);

        // The one that does not round-trip on its own: a ValueTuple crossing JSON as a named record.
        var flags = await client.ReadDisplayFlagsAsync();
        Assert.False(flags!.Value.Fahrenheit);
        Assert.True(flags.Value.DisplayOnCooling);
    }

    [Fact]
    public async Task A_value_the_host_does_not_have_comes_back_as_nothing_rather_than_zero()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();

        Assert.Null(await client.ReadBrightnessAsync());
        Assert.Null(await client.ReadVibrationAsync());
        Assert.Null(await client.ReadDeviceInfoAsync());
        Assert.Null(await client.ReadDisplayFlagsAsync());
    }

    [Fact]
    public async Task Device_events_reach_every_connected_client()
    {
        await using var host = new Host();
        var first = await host.JoinAsync();
        var second = await host.JoinAsync();

        double? onFirst = null;
        double? onSecond = null;
        first.CurrentTemperatureChanged += (_, c) => onFirst = c;
        second.CurrentTemperatureChanged += (_, c) => onSecond = c;

        host.Device.ReportTemperature(123.5);

        await Wait.ForAsync(() => onFirst is not null && onSecond is not null, "both clients to hear it");
        Assert.Equal(123.5, onFirst);
        Assert.Equal(123.5, onSecond);
    }

    // --- Roles ---

    [Fact]
    public async Task A_watching_client_is_refused_a_write_and_the_device_is_left_alone()
    {
        await using var host = new Host();
        host.Device.Brightness = 70;
        var client = await host.JoinAsync(RelayClientRole.Watching);

        string? refusal = null;
        client.ErrorOccurred += (_, message) => refusal = message;

        await client.SetHeaterAsync(true);

        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.Empty(host.Device.WrittenHeaterStates);

        // Watching, not blindfolded: reads still work, which is the entire point of the role.
        Assert.Equal(70, await client.ReadBrightnessAsync());
    }

    [Fact]
    public async Task A_watching_client_cannot_start_the_hosts_ramp()
    {
        await using var host = new Host();
        var client = await host.JoinAsync(RelayClientRole.Watching);
        using var remote = new RemoteRampController(client);

        string? refusal = null;
        remote.ErrorOccurred += (_, message) => refusal = message;

        await remote.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(2)), heaterCurrentlyOn: false);

        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.False(host.Ramp.IsRunning);
        Assert.Empty(host.Device.WrittenTargets);
    }

    /// <summary>
    /// The guard above is only as good as the list it checks against, and that list is hand-written.
    /// Anything that changes the device or the ramp has to be in it; this fails when a new Set* or
    /// ramp method is added and nobody classifies it, which is exactly the omission the list exists
    /// to make visible.
    /// </summary>
    [Fact]
    public void Every_method_that_changes_something_is_classified_as_mutating()
    {
        var declared = typeof(RelayMethods)
            .GetFields()
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToArray();

        var shouldMutate = declared
            .Where(m => m.StartsWith("Set", StringComparison.Ordinal) || m.EndsWith("Ramp", StringComparison.Ordinal)
                        || m == RelayMethods.SkipRampSegment)
            .Where(m => m != RelayMethods.Hello)
            .ToArray();

        Assert.NotEmpty(shouldMutate);
        Assert.Empty(shouldMutate.Except(RelayMethods.MutatingMethods));

        // And nothing read-only smuggled itself onto the list.
        Assert.DoesNotContain(RelayMethods.MutatingMethods, m => m.StartsWith("Read", StringComparison.Ordinal));
    }

    // --- Leaving ---

    [Fact]
    public async Task A_client_that_leaves_disappears_from_the_hosts_list()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        await Wait.ForAsync(() => host.Server.Clients.Count == 1, "the client to be listed");

        await client.DisconnectAsync();

        await Wait.ForAsync(() => host.Server.Clients.Count == 0, "the host to drop the client");
        Assert.True(host.ClientsChangedCount >= 2, "joining and leaving should both have been announced");
    }

    [Fact]
    public async Task Revoking_a_client_drops_it_and_the_client_notices()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        await Wait.ForAsync(() => host.Server.Clients.Count == 1, "the client to be listed");

        var lost = false;
        client.ErrorOccurred += (_, _) => lost = true;

        await host.Server.RevokeAsync(host.Server.Clients[0].Id);

        await Wait.ForAsync(() => lost, "the revoked client to notice");
        Assert.Equal(ConnectionState.Error, client.State);
        await Wait.ForAsync(() => host.Server.Clients.Count == 0, "the host to forget the client");
    }

    [Fact]
    public async Task Stopping_hosting_drops_everyone()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        await Wait.ForAsync(() => host.Server.Clients.Count == 1, "the client to be listed");

        var lost = false;
        client.ErrorOccurred += (_, _) => lost = true;

        await host.Server.StopAsync();

        await Wait.ForAsync(() => lost, "the client to notice hosting ended");
        Assert.False(host.Server.IsRunning);
        Assert.Empty(host.Server.Clients);
    }

    // --- Driving the host's ramp from a client ---

    [Fact]
    public async Task A_client_can_run_the_hosts_ramp_and_stop_it_again()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        using var remote = new RemoteRampController(client);

        var warmedUp = false;
        var stopped = false;
        var progressSeen = 0;
        remote.WarmupCompleted += (_, _) => warmedUp = true;
        remote.Stopped += (_, _) => stopped = true;
        remote.ProgressChanged += (_, _) => Interlocked.Increment(ref progressSeen);

        await remote.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(2)), heaterCurrentlyOn: true);

        // The ramp is running on the host and has already written the first point to the device.
        Assert.True(host.Ramp.IsRunning);
        Assert.Equal(180, Assert.Single(host.Device.WrittenTargets));

        await Wait.ForAsync(() => progressSeen > 0, "progress to travel back to the client");
        Assert.True(remote.IsRunning);

        // Warm-up finishes on the host, from the host's own device notification.
        host.Device.ReportTemperature(180);
        await Wait.ForAsync(() => warmedUp, "warm-up to be reported to the client");

        remote.Stop();

        await Wait.ForAsync(() => !host.Ramp.IsRunning, "the host's ramp to stop");
        await Wait.ForAsync(() => stopped, "the stop to be reported back");
        Assert.False(remote.IsRunning);
    }

    [Fact]
    public async Task A_client_that_joins_mid_ramp_is_told_a_ramp_is_running()
    {
        await using var host = new Host();
        await host.Ramp.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(2)), heaterCurrentlyOn: true);

        var client = host.CreateClient();
        using var remote = new RemoteRampController(client);
        var progressSeen = 0;
        remote.ProgressChanged += (_, _) => Interlocked.Increment(ref progressSeen);

        Assert.True(await client.ScanAndConnectAsync());

        await Wait.ForAsync(() => progressSeen > 0, "the joining client to be told about the running ramp");
        Assert.True(remote.IsRunning);
    }

    /// <summary>
    /// The numbers alone are not enough for the client's Run tab: without the plan it can say "1 of 2"
    /// but not which curve the segment follows, what it is heating towards, or how long the hold is,
    /// and its strip collapses to bare numbered blocks. So the host describes the running ramp, and
    /// it has to reach a client that was not there when it started.
    /// </summary>
    [Fact]
    public async Task A_client_that_joins_mid_ramp_is_given_the_plan()
    {
        await using var host = new Host();
        await host.Ramp.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(3)), heaterCurrentlyOn: true);

        var client = host.CreateClient();
        using var remote = new RemoteRampController(client);
        Assert.True(await client.ScanAndConnectAsync());

        await Wait.ForAsync(() => remote.ActivePlan is not null, "the plan to reach the joining client");

        var plan = remote.ActivePlan!;
        Assert.Equal(TimeSpan.FromMinutes(3), plan.HoldDuration);
        Assert.Equal(180, plan.StartTemperatureCelsius);
        Assert.Equal(200, plan.EndTemperatureCelsius);
        Assert.Equal(host.Ramp.ActivePlan!.SegmentCount, plan.SegmentCount);
        Assert.Equal(CurveKind.Linear, plan.Points[0].CurveToNext);
    }

    [Fact]
    public async Task A_client_gets_the_plan_when_a_ramp_starts_while_it_is_connected()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        using var remote = new RemoteRampController(client);

        Assert.Null(remote.ActivePlan);

        await host.Ramp.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(1)), heaterCurrentlyOn: true);

        await Wait.ForAsync(() => remote.ActivePlan is not null, "the plan to be announced");
        Assert.Equal(200, remote.ActivePlan!.EndTemperatureCelsius);
    }

    [Fact]
    public async Task The_plan_is_let_go_of_when_the_ramp_ends()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        using var remote = new RemoteRampController(client);

        await host.Ramp.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(1)), heaterCurrentlyOn: true);
        await Wait.ForAsync(() => remote.ActivePlan is not null, "the plan to be announced");

        host.Ramp.Stop();

        await Wait.ForAsync(() => remote.ActivePlan is null, "the plan to be dropped when the ramp stops");
        Assert.False(remote.IsRunning);
    }

    /// <summary>Once per run, not once per tick - the plan does not change while a ramp runs and the
    /// progress events arrive every second.</summary>
    [Fact]
    public async Task The_plan_is_sent_once_per_run()
    {
        await using var host = new Host();
        var raw = await host.ConnectRawAsync();
        Assert.NotNull(await host.SayHelloAsync(raw));

        await host.Ramp.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(1)), heaterCurrentlyOn: true);

        // Counted on the wire, because the client's plan event is internal to Core - and this is the
        // question anyway: how many times does it go out, not how many times is it received.
        var plans = 0;
        var ticks = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ticks < 4)
        {
            var message = await raw.ReceiveAsync(cts.Token);
            if (message is null) break;
            if (message.Method == RelayEvents.RampPlanChanged) plans++;
            if (message.Method == RelayEvents.RampProgressChanged) ticks++;
        }

        Assert.Equal(4, ticks);
        Assert.Equal(1, plans);
    }

    // --- Timing the link ---

    [Fact]
    public async Task A_round_trip_to_the_host_is_measured()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();

        var latency = await client.MeasureLatencyAsync();

        Assert.NotNull(latency);
        Assert.True(latency >= TimeSpan.Zero, "a round trip cannot take less than no time");
        Assert.True(latency < TimeSpan.FromSeconds(1), $"loopback should be immediate, was {latency}");
    }

    /// <summary>
    /// The reason the ping is answered without the device being touched. A Volcano that has stopped
    /// answering - out of range, mid-firmware-sulk - would otherwise take the latency reading with
    /// it, and the number is at its most useful exactly then: it says whether the machines can still
    /// hear each other, which is a different question from whether the device can.
    /// </summary>
    [Fact]
    public async Task The_link_can_still_be_timed_while_the_device_has_stopped_answering()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();

        host.Device.Stall();

        // Proof the device really is wedged: this one goes to it and does not come back.
        var read = client.ReadBrightnessAsync();
        var ping = await client.MeasureLatencyAsync();

        Assert.NotNull(ping);
        Assert.False(read.IsCompleted, "the device read should still be waiting");

        host.Device.Resume();
        await read;
    }

    /// <summary>A watcher may ask how far away the host is - it is not a change to anything.</summary>
    [Fact]
    public async Task A_watching_client_may_ping()
    {
        await using var host = new Host();
        var client = await host.JoinAsync(RelayClientRole.Watching);

        Assert.NotNull(await client.MeasureLatencyAsync());
    }

    [Fact]
    public async Task Joining_starts_reporting_the_latency_by_itself()
    {
        await using var host = new Host();
        var client = host.CreateClient();

        TimeSpan? reported = null;
        client.LatencyChanged += (_, value) => reported = value;

        Assert.True(await client.ScanAndConnectAsync());

        await Wait.ForAsync(() => reported is not null, "the first measurement to be reported");
        Assert.Equal(reported, client.Latency);
    }

    /// <summary>Leaving takes the number with it: a millisecond count from a host this client is no
    /// longer talking to is worse than none.</summary>
    [Fact]
    public async Task Leaving_forgets_the_latency()
    {
        await using var host = new Host();
        var client = await host.JoinAsync();
        await Wait.ForAsync(() => client.Latency is not null, "a first measurement");

        await client.DisconnectAsync();

        Assert.Null(client.Latency);
        Assert.Null(await client.MeasureLatencyAsync());
    }

    // --- A peer that does not behave ---

    [Fact]
    public async Task The_host_refuses_a_ramp_it_would_not_run_itself()
    {
        await using var host = new Host();
        var raw = await host.ConnectRawAsync();
        Assert.NotNull(await host.SayHelloAsync(raw));

        // 300 °C is past what the device accepts. A client cannot talk the host into a ramp the host
        // would have rejected for itself, which is why the whole plan travels rather than a promise
        // that it was already checked.
        RampPoint[] tooHot = [new(0, 300, CurveKind.Linear), new(5, 320, CurveKind.Linear)];
        raw.Send(Host.Request(RelayMethods.StartRamp, new StartRampArgs(tooHot, TimeSpan.FromMinutes(1), false)));

        var response = await Host.ReadResponseAsync(raw);
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.Error));
        Assert.False(host.Ramp.IsRunning);
        Assert.Empty(host.Device.WrittenTargets);
    }

    [Fact]
    public async Task An_unknown_method_is_answered_with_an_error_rather_than_a_dropped_connection()
    {
        await using var host = new Host();
        var raw = await host.ConnectRawAsync();
        Assert.NotNull(await host.SayHelloAsync(raw));

        raw.Send(Host.Request("LaunchTheRocket", null));

        var response = await Host.ReadResponseAsync(raw);
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.Error));

        // Still connected, still working: one bad request does not cost the client its session.
        raw.Send(Host.Request(RelayMethods.ReadBrightness, null));
        var afterwards = await Host.ReadResponseAsync(raw);
        Assert.NotNull(afterwards);
        Assert.Null(afterwards!.Error);
    }

    [Fact]
    public async Task A_peer_that_does_not_say_hello_first_gets_nowhere()
    {
        await using var host = new Host();
        var raw = await host.ConnectRawAsync();

        raw.Send(Host.Request(RelayMethods.SetHeater, new SetHeaterArgs(true)));

        // The connection is dropped without a reply, and the device was never asked.
        Assert.Null(await Host.ReadResponseAsync(raw));
        Assert.Empty(host.Server.Clients);
        Assert.Empty(host.Device.WrittenHeaterStates);
    }

    [Fact]
    public async Task A_method_missing_its_arguments_is_an_error_and_not_a_crash()
    {
        await using var host = new Host();
        var raw = await host.ConnectRawAsync();
        Assert.NotNull(await host.SayHelloAsync(raw));

        raw.Send(Host.Request(RelayMethods.SetTargetTemperature, null));

        var response = await Host.ReadResponseAsync(raw);
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.Error));
        Assert.Empty(host.Device.WrittenTargets);
    }
}
