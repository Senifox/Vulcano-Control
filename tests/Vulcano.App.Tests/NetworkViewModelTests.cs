using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Tests;

/// <summary>
/// Hosting and joining, as the Network tab drives them.
///
/// Two orchestrators talk to each other over the loopback interface, each built around a fake
/// Volcano - the orchestrator takes a factory for its local device, so nothing here needs Bluetooth
/// or a refactor to be reachable. Until now this whole tab had only ever been exercised by running
/// two copies of the app by hand.
/// </summary>
public sealed class NetworkViewModelTests : IDisposable
{
    private const string Pin = "2468";

    private readonly List<string> _files = new();
    private readonly List<IDisposable> _disposables = new();

    private sealed record Side(
        VolcanoDeviceOrchestrator Device,
        NetworkViewModel ViewModel,
        FakeVolcanoDevice Fake);

    private Side CreateSide(bool hostOnStart = false, string pin = Pin)
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"vulcano-net-{Guid.NewGuid():N}.json");
        var logFile = Path.Combine(Path.GetTempPath(), $"vulcano-net-{Guid.NewGuid():N}.log");
        _files.Add(settingsFile);
        _files.Add(logFile);

        var fake = new FakeVolcanoDevice();
        var log = new LogService(logFile);
        var orchestrator = new VolcanoDeviceOrchestrator(() => fake, log);
        var settingsService = new SettingsService(settingsFile, []);

        // Port zero asks the operating system for a free one, so tests never collide with each other
        // or with whatever else is listening on this machine.
        var settings = new AppSettings
        {
            RelayServerPort = 0,
            RelayPin = pin,
            HostOnStart = hostOnStart,
        };
        var vm = new NetworkViewModel(orchestrator, settingsService, settings, log);

        _disposables.Add(vm);
        return new Side(orchestrator, vm, fake);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch { /* best-effort */ }
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static async Task JoinAsync(Side client, Side host, bool watcher = false)
    {
        client.ViewModel.JoinAddress = "127.0.0.1";
        client.ViewModel.JoinPort = host.Device.HostingPort ?? 0;
        client.ViewModel.JoinPin = Pin;
        client.ViewModel.JoinAsWatcher = watcher;

        await client.ViewModel.JoinCommand.ExecuteAsync(null);
        Pump();
    }

    // --- Hosting ---

    [AvaloniaFact]
    public void Hosting_starts_and_the_address_gains_a_port()
    {
        var host = CreateSide();

        host.ViewModel.StartHostingCommand.Execute(null);

        Assert.True(host.ViewModel.IsHosting);
        Assert.True(host.Device.HostingPort > 0);
        Assert.Contains(host.Device.HostingPort!.Value.ToString(), host.ViewModel.HostAddressText);
    }

    [AvaloniaFact]
    public async Task Hosting_stops_again()
    {
        var host = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);

        await host.ViewModel.StopHostingCommand.ExecuteAsync(null);

        Assert.False(host.ViewModel.IsHosting);
        Assert.Empty(host.ViewModel.Clients);
    }

    [AvaloniaFact]
    public void A_new_pin_is_four_digits()
    {
        var host = CreateSide();

        host.ViewModel.NewPinCommand.Execute(null);

        Assert.Equal(4, host.ViewModel.Pin.Length);
        Assert.True(int.TryParse(host.ViewModel.Pin, out _));
    }

    // --- Sharing by itself ---

    /// <summary>
    /// The setting is stored as HostOnStart and used to do nothing at all: it was written to
    /// settings.json, read back into its own checkbox, and never looked at again. What it means now
    /// is "share once this machine has a device", which is the first moment sharing is of use to
    /// anybody - a server with nothing behind it gives whoever joins an empty connection.
    /// </summary>
    [AvaloniaFact]
    public async Task Connecting_starts_sharing_when_the_setting_is_on()
    {
        var side = CreateSide(hostOnStart: true);

        Assert.False(side.ViewModel.IsHosting);

        await side.Device.ScanAndConnectAsync();
        Pump();

        Assert.True(side.ViewModel.IsHosting);
        Assert.True(side.Device.HostingPort > 0);
    }

    [AvaloniaFact]
    public async Task Connecting_shares_nothing_when_the_setting_is_off()
    {
        var side = CreateSide();

        await side.Device.ScanAndConnectAsync();
        Pump();

        Assert.False(side.ViewModel.IsHosting);
    }

    /// <summary>
    /// A client is borrowing somebody else's device. Passing it on is not its to do, and the
    /// orchestrator would refuse anyway - this stops it being asked.
    /// </summary>
    [AvaloniaFact]
    public async Task Joining_someone_else_does_not_start_sharing()
    {
        var host = CreateSide();
        var client = CreateSide(hostOnStart: true);
        host.ViewModel.StartHostingCommand.Execute(null);

        await JoinAsync(client, host);

        Assert.True(client.ViewModel.IsRemote);
        Assert.False(client.ViewModel.IsHosting);
        Assert.Equal("", client.ViewModel.HostError);
    }

    /// <summary>
    /// Stopping it has to stick. The connection dropping and coming back is ordinary - a device out
    /// of range, a moment of Bluetooth - and restarting something switched off by hand each time
    /// would be the app arguing with the person using it.
    /// </summary>
    [AvaloniaFact]
    public async Task Sharing_stopped_by_hand_is_not_started_again_by_a_reconnect()
    {
        var side = CreateSide(hostOnStart: true);

        await side.Device.ScanAndConnectAsync();
        Pump();
        Assert.True(side.ViewModel.IsHosting);

        await side.ViewModel.StopHostingCommand.ExecuteAsync(null);
        Assert.False(side.ViewModel.IsHosting);

        // The device drops and comes back.
        side.Fake.ReportConnectionState(ConnectionState.Error);
        Pump();
        side.Fake.ReportConnectionState(ConnectionState.Connected);
        Pump();

        Assert.False(side.ViewModel.IsHosting);
    }

    /// <summary>An empty PIN is deliberately allowed - it is the state a home network starts in,
    /// and refusing to share until one is typed would make the setting a lie again.</summary>
    [AvaloniaFact]
    public async Task Sharing_starts_even_with_no_pin_set()
    {
        var side = CreateSide(hostOnStart: true, pin: "");

        await side.Device.ScanAndConnectAsync();
        Pump();

        Assert.True(side.ViewModel.IsHosting);
    }

    // --- Joining ---

    [AvaloniaFact]
    public async Task A_client_joins_and_the_host_lists_it()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);

        await JoinAsync(client, host);

        Assert.True(client.ViewModel.IsRemote);
        Assert.Equal("", client.ViewModel.JoinError);
        Assert.NotEqual("", client.ViewModel.RemoteBanner);

        await Wait.ForAsync(() => { Pump(); return host.ViewModel.Clients.Count == 1; }, "the host to list the client");
        Assert.True(host.ViewModel.HasClients);
    }

    [AvaloniaFact]
    public async Task A_wrong_pin_is_refused_and_says_so()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);

        client.ViewModel.JoinAddress = "127.0.0.1";
        client.ViewModel.JoinPort = host.Device.HostingPort ?? 0;
        client.ViewModel.JoinPin = "0000";
        await client.ViewModel.JoinCommand.ExecuteAsync(null);
        Pump();

        Assert.False(client.ViewModel.IsRemote);
        Assert.NotEqual("", client.ViewModel.JoinError);
        Assert.Empty(host.ViewModel.Clients);
    }

    /// <summary>The role travels with the join, and the host's list is where it becomes visible.</summary>
    [AvaloniaFact]
    public async Task A_client_that_joined_to_watch_shows_up_as_a_watcher()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);

        await JoinAsync(client, host, watcher: true);

        await Wait.ForAsync(() => { Pump(); return host.ViewModel.Clients.Count == 1; }, "the host to list the client");
        Assert.Equal(Strings.Get("Network.Role.Watching"), host.ViewModel.Clients[0].Role);
    }

    [AvaloniaFact]
    public async Task Leaving_gives_the_client_its_own_device_back()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);
        await JoinAsync(client, host);

        await client.ViewModel.LeaveCommand.ExecuteAsync(null);
        Pump();

        Assert.False(client.ViewModel.IsRemote);
        Assert.Equal("", client.ViewModel.RemoteBanner);
        await Wait.ForAsync(() => { Pump(); return host.ViewModel.Clients.Count == 0; }, "the host to drop it");
    }

    [AvaloniaFact]
    public async Task Revoking_drops_the_client()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);
        await JoinAsync(client, host);
        await Wait.ForAsync(() => { Pump(); return host.ViewModel.Clients.Count == 1; }, "the client to be listed");

        await host.ViewModel.RevokeCommand.ExecuteAsync(host.ViewModel.Clients[0]);

        await Wait.ForAsync(() => { Pump(); return host.ViewModel.Clients.Count == 0; }, "the client to be dropped");
    }

    /// <summary>One machine holds the Bluetooth connection and the others borrow it, so a client
    /// cannot also be a host.</summary>
    [AvaloniaFact]
    public async Task A_client_cannot_host()
    {
        var host = CreateSide();
        var client = CreateSide();
        host.ViewModel.StartHostingCommand.Execute(null);

        Assert.True(client.ViewModel.CanHost);

        await JoinAsync(client, host);

        Assert.False(client.ViewModel.CanHost);
    }
}
