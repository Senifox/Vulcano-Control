using Avalonia.Headless.XUnit;
using Vulcano.App.Services;
using Vulcano.App.ViewModels;

namespace Vulcano.App.Tests;

/// <summary>
/// Updating, and the one rule that must never bend: nothing is installed out from under a running
/// ramp. A ramp is half an hour of a device heating on its own, and this app is what turns it off
/// at the end - an update that restarts the app mid-run leaves a hot device with nobody watching.
///
/// Everything here runs against a fake source, so no install, no network and no GitHub is involved.
/// </summary>
public sealed class UpdateViewModelTests
{
    /// <summary>A source that answers whatever the test says, and records what was done to it.</summary>
    private sealed class FakeUpdateSource : IUpdateSource
    {
        public bool IsSupported { get; set; } = true;

        public string? PendingVersion { get; set; }

        /// <summary>What a check reports. Up to date by default.</summary>
        public UpdateCheck Result { get; set; } = UpdateCheck.UpToDate;

        public bool DownloadSucceeds { get; set; } = true;

        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public bool AppliedOnExit { get; private set; }
        public bool Restarted { get; private set; }

        public Task<UpdateCheck> CheckAsync()
        {
            Checks++;
            return Task.FromResult(Result);
        }

        public Task<bool> DownloadAsync()
        {
            Downloads++;
            return Task.FromResult(DownloadSucceeds);
        }

        public void ApplyOnExit() => AppliedOnExit = true;

        public void ApplyAndRestart() => Restarted = true;
    }

    private static FakeUpdateSource WithUpdate(string version = "2.1.0") =>
        new() { Result = UpdateCheck.Found(version) };

    // --- The startup pass ---

    [AvaloniaFact]
    public async Task A_new_version_is_fetched_at_startup_and_only_waits()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(UpdateState.Ready, vm.State);
        Assert.Equal("2.1.0", vm.Version);
        Assert.Equal(1, source.Downloads);

        // The whole point: downloaded, and nothing else has happened to this machine.
        Assert.False(source.Restarted);
        Assert.False(source.AppliedOnExit);
    }

    [AvaloniaFact]
    public async Task Switching_the_automatic_check_off_means_nothing_is_asked()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);

        await vm.RunStartupCheckAsync(automatic: false);

        Assert.Equal(0, source.Checks);
        Assert.Equal(UpdateState.Idle, vm.State);
    }

    [AvaloniaFact]
    public async Task Being_current_says_so_and_downloads_nothing()
    {
        var source = new FakeUpdateSource();
        var vm = new UpdateViewModel(source);

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(UpdateState.UpToDate, vm.State);
        Assert.Equal(0, source.Downloads);
    }

    /// <summary>Being offline at startup is ordinary, and must read as ordinary rather than as
    /// something being wrong with the app.</summary>
    [AvaloniaFact]
    public async Task A_check_that_could_not_reach_anything_is_reported_and_survived()
    {
        var source = new FakeUpdateSource { Result = UpdateCheck.Failed("no such host") };
        var vm = new UpdateViewModel(source);

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("no such host", vm.StatusText);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_download_that_failed_is_not_reported_as_ready()
    {
        var source = WithUpdate();
        source.DownloadSucceeds = false;
        var vm = new UpdateViewModel(source);

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.False(vm.IsReady);
    }

    /// <summary>
    /// The app was closed some other way than through the exit that would have applied it. Coming up
    /// and downloading the same 57 MB again would be the easy mistake here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_version_downloaded_in_an_earlier_session_is_still_ready()
    {
        var source = new FakeUpdateSource { PendingVersion = "2.1.0" };
        var vm = new UpdateViewModel(source);

        Assert.Equal(UpdateState.Ready, vm.State);
        Assert.Equal("2.1.0", vm.Version);

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(0, source.Checks);
        Assert.Equal(0, source.Downloads);
    }

    // --- The rule ---

    [AvaloniaFact]
    public async Task A_running_ramp_takes_the_restart_away_and_gives_it_back()
    {
        var vm = new UpdateViewModel(WithUpdate());
        await vm.RunStartupCheckAsync(automatic: true);

        Assert.True(vm.RestartCommand.CanExecute(null));

        vm.IsRampRunning = true;

        Assert.False(vm.RestartCommand.CanExecute(null));
        Assert.True(vm.IsRestartBlocked);

        vm.IsRampRunning = false;

        Assert.True(vm.RestartCommand.CanExecute(null));
        Assert.False(vm.IsRestartBlocked);
    }

    /// <summary>Not just the button: the command itself refuses, so a keyboard shortcut or a stray
    /// binding cannot get around it either.</summary>
    [AvaloniaFact]
    public async Task Restarting_during_a_ramp_does_not_happen_even_if_the_command_is_executed()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);
        await vm.RunStartupCheckAsync(automatic: true);

        vm.IsRampRunning = true;
        vm.RestartCommand.Execute(null);

        Assert.False(source.Restarted);
    }

    [AvaloniaFact]
    public async Task Restarting_when_nothing_is_running_applies_it()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);
        await vm.RunStartupCheckAsync(automatic: true);

        vm.RestartCommand.Execute(null);

        Assert.True(source.Restarted);
    }

    // --- Closing the app ---

    [AvaloniaFact]
    public async Task Closing_the_app_hands_a_downloaded_version_to_the_installer()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);
        await vm.RunStartupCheckAsync(automatic: true);

        vm.ApplyOnExit();

        Assert.True(source.AppliedOnExit);
    }

    /// <summary>Nothing was downloaded, so there is nothing to hand over - and asking the installer
    /// to apply nothing is how a working install gets replaced by a broken one.</summary>
    [AvaloniaFact]
    public async Task Closing_the_app_with_nothing_downloaded_does_nothing()
    {
        var source = new FakeUpdateSource();
        var vm = new UpdateViewModel(source);
        await vm.RunStartupCheckAsync(automatic: true);

        vm.ApplyOnExit();

        Assert.False(source.AppliedOnExit);
    }

    // --- A copy that cannot update itself ---

    [AvaloniaFact]
    public async Task An_uninstalled_copy_says_so_and_offers_nothing()
    {
        var source = new FakeUpdateSource { IsSupported = false, Result = UpdateCheck.Found("2.1.0") };
        var vm = new UpdateViewModel(source);

        Assert.Equal(UpdateState.Unsupported, vm.State);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.RestartCommand.CanExecute(null));

        await vm.RunStartupCheckAsync(automatic: true);

        Assert.Equal(0, source.Checks);
    }

    // --- Asking by hand ---

    [AvaloniaFact]
    public async Task Checking_by_hand_fetches_what_it_finds()
    {
        var source = WithUpdate();
        var vm = new UpdateViewModel(source);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Ready, vm.State);
        Assert.Equal(1, source.Checks);
        Assert.Equal(1, source.Downloads);
    }

    [AvaloniaFact]
    public void A_check_already_under_way_cannot_be_started_a_second_time()
    {
        var vm = new UpdateViewModel(WithUpdate())
        {
            State = UpdateState.Checking,
        };

        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.IsBusy);
    }
}
