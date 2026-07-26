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
    private readonly List<string> _files = new();
    private readonly List<IDisposable> _disposables = new();

    private sealed record Side(
        VolcanoDeviceOrchestrator Device,
        NetworkViewModel ViewModel,
        FakeVolcanoDevice Fake);

    private Side CreateSide()
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
        var settings = new AppSettings { RelayServerPort = 0, RelayPin = "2468" };
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
        client.ViewModel.JoinPin = "2468";
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
