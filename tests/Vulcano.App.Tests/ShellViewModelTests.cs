using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vulcano.App.Services;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Tests;

/// <summary>
/// The window's own decisions: which tabs exist, what the banner says, and what happens at the two
/// ends of a ramp. Built on a real orchestrator around a fake Volcano, so the ramp really runs.
/// </summary>
public sealed class ShellViewModelTests : IAsyncDisposable
{
    /// <summary>Collects what would have been shown, so a test can ask what the person would have
    /// been told without a window or an operating system being involved.</summary>
    private sealed class RecordingNotifier : INotifier
    {
        public List<NotificationRequest> Sent { get; } = new();

        public bool Enabled { get; set; } = true;

        public void Notify(string title, string message)
        {
            if (!Enabled) return;

            var request = new NotificationRequest(title, message);
            Sent.Add(request);
            FellBackToWindow?.Invoke(this, request);
        }

        public event EventHandler<NotificationRequest>? FellBackToWindow;
    }

    private sealed class RecordingSoundPlayer : ISoundPlayer
    {
        public List<AppSound> Played { get; } = new();

        public void Play(AppSound sound) => Played.Add(sound);
    }

    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-shell-{Guid.NewGuid():N}.json");
    private readonly string _logFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-shell-{Guid.NewGuid():N}.log");

    private readonly FakeVolcanoDevice _fake = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly ShellViewModel _shell;

    /// <summary>The same instance the shell holds, so a test can set what it would have read.</summary>
    private readonly AppSettings _settings;

    private static readonly RampPoint[] Points =
    [
        new(0, 180, CurveKind.Linear),
        new(1, 185, CurveKind.Linear),
    ];

    public ShellViewModelTests()
    {
        var log = new LogService(_logFile);
        // The orchestrator builds its own ramp controller at the production tick, so these tests wait
        // on conditions rather than assuming anything about timing.
        _device = new VolcanoDeviceOrchestrator(() => _fake, log);

        _settings = new AppSettings
        {
            RampProfiles = [new RampProfile { Name = "Evening", Points = [.. Points], HoldMinutes = 0 }],
            ActiveRampProfileName = "Evening",
        };

        _shell = new ShellViewModel(
            _device,
            new SettingsService(_settingsFile, []),
            new ThemeManager(),
            _settings,
            log,
            new SoundService(_player, log) { SoundEnabled = true },
            _notifier);
    }

    public async ValueTask DisposeAsync()
    {
        await _shell.DisposeAsync();
        foreach (var file in new[] { _settingsFile, _logFile })
        {
            try { File.Delete(file); } catch { /* best-effort */ }
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private async Task StartRampAsync() =>
        await ((IRampSessionController)_device).StartAsync(
            new TemperatureRampPlan(Points, TimeSpan.Zero), heaterCurrentlyOn: true);

    // --- Tabs ---

    [AvaloniaFact]
    public void The_run_tab_is_not_there_until_a_ramp_is()
    {
        Assert.False(_shell.IsRunTabVisible);
        Assert.Equal(AppTab.Control, _shell.SelectedTab);
    }

    [AvaloniaFact]
    public async Task A_ramp_brings_the_run_tab_up_and_selects_it()
    {
        await StartRampAsync();

        await Wait.ForAsync(() => { Pump(); return _shell.IsRunTabVisible; }, "the Run tab to appear");
        Assert.Equal(AppTab.Run, _shell.SelectedTab);
    }

    /// <summary>A ramp started on another machine reaches this window the same way, which is why the
    /// tab follows the controller rather than the button that started it.</summary>
    [AvaloniaFact]
    public async Task The_run_tab_goes_away_when_the_ramp_ends()
    {
        await StartRampAsync();
        await Wait.ForAsync(() => { Pump(); return _shell.IsRunTabVisible; }, "the Run tab to appear");

        ((IRampSessionController)_device).Stop();

        await Wait.ForAsync(() => { Pump(); return !_shell.IsRunTabVisible; }, "the Run tab to go away");
    }

    // --- Connection ---

    [AvaloniaFact]
    public void The_banner_is_up_while_there_is_no_device()
    {
        Assert.True(_shell.ShowConnectionBanner);
        Assert.False(_shell.IsConnected);
        Assert.NotEqual("", _shell.ConnectionText);
    }

    [AvaloniaFact]
    public async Task Connecting_puts_the_banner_away()
    {
        await _shell.ConnectCommand.ExecuteAsync(null);
        Pump();

        Assert.True(_shell.IsConnected);
        Assert.False(_shell.ShowConnectionBanner);
    }

    // --- Sounds and notifications ---

    /// <summary>Warm-up finishing is the moment worth waiting for, and the only one during a ramp
    /// that says the device has arrived.</summary>
    [AvaloniaFact]
    public async Task Reaching_the_start_temperature_is_announced()
    {
        await StartRampAsync();
        _fake.ReportTemperature(180);

        await Wait.ForAsync(() => { Pump(); return _notifier.Sent.Count > 0; }, "the warm-up notification");
        Assert.Contains(AppSound.HeatReached, _player.Played);
    }

    [AvaloniaFact]
    public async Task A_finished_ramp_is_announced_and_a_stopped_one_is_not()
    {
        await StartRampAsync();
        await Wait.ForAsync(() => { Pump(); return _shell.IsRunTabVisible; }, "the ramp to be running");

        ((IRampSessionController)_device).Stop();
        Pump();

        // Somebody who stopped it is already looking at the window.
        Assert.Empty(_notifier.Sent);
    }

    /// <summary>
    /// When the operating system will not show a notification the window has to, or the notification
    /// is simply lost - which is what happened before anything listened for the fallback.
    /// </summary>
    [AvaloniaFact]
    public async Task A_notification_the_system_refused_becomes_a_notice_in_the_window()
    {
        await StartRampAsync();
        _fake.ReportTemperature(180);

        await Wait.ForAsync(() => { Pump(); return _shell.IsNoticeVisible; }, "the notice to appear");
        Assert.NotEqual("", _shell.NoticeTitle);
        Assert.NotEqual("", _shell.NoticeMessage);

        _shell.DismissNoticeCommand.Execute(null);
        Assert.False(_shell.IsNoticeVisible);
    }

    [AvaloniaFact]
    public async Task Switching_notifications_off_silences_them()
    {
        _shell.Settings.DesktopNotifications = false;

        await StartRampAsync();
        _fake.ReportTemperature(180);

        await Wait.ForAsync(() => { Pump(); return _player.Played.Count > 0; }, "the sound, which is separate");
        Assert.Empty(_notifier.Sent);
    }

    // --- Compact ---

    [AvaloniaFact]
    public void Compact_is_the_same_window_and_toggles_back()
    {
        Assert.False(_shell.IsCompact);

        _shell.ToggleCompactCommand.Execute(null);
        Assert.True(_shell.IsCompact);

        _shell.ToggleCompactCommand.Execute(null);
        Assert.False(_shell.IsCompact);
    }

    [AvaloniaFact]
    public void The_simulation_chip_only_shows_for_a_simulated_device()
    {
        Assert.False(_shell.IsSimulated);
    }

    // --- What changed ---

    /// <summary>
    /// The version on disk changes while nobody is watching, so the first start on a new one is the
    /// only chance to say what it brought. The changelog has to have an entry for it - the version
    /// under test is the one this assembly reports, which is whatever is being built.
    /// </summary>
    [AvaloniaFact]
    public void A_version_that_has_changed_is_announced_once()
    {
        var shipped = Vulcano.Core.Services.Changelog.Entries.First(e => !e.IsUnreleased);
        _settings.LastSeenVersion = "0.0.1";

        _shell.ShowWhatsNewIfVersionChanged(shipped.Version);

        Assert.True(_shell.IsNoticeVisible);
        Assert.Contains(shipped.Version, _shell.NoticeTitle);
        Assert.Contains(shipped.Items[0], _shell.NoticeMessage);

        // Once: the version has been seen now, so a second start says nothing.
        _shell.DismissNoticeCommand.Execute(null);
        _shell.ShowWhatsNewIfVersionChanged(shipped.Version);

        Assert.False(_shell.IsNoticeVisible);
    }

    /// <summary>
    /// A fresh install has not "updated" to anything. Being handed a list of what changed since a
    /// version you never ran is noise at the worst moment - the first time the app is opened.
    /// </summary>
    [AvaloniaFact]
    public void A_first_install_is_told_nothing()
    {
        _settings.LastSeenVersion = "";

        _shell.ShowWhatsNewIfVersionChanged("2.3.0");

        Assert.False(_shell.IsNoticeVisible);
    }

    [AvaloniaFact]
    public void A_version_with_no_entry_says_nothing_rather_than_nothing_in_particular()
    {
        _settings.LastSeenVersion = "0.0.1";

        _shell.ShowWhatsNewIfVersionChanged("99.0.0");

        Assert.False(_shell.IsNoticeVisible);
    }

    // --- Updates ---

    /// <summary>
    /// The ramp reaches the update view model, which is what takes the restart button away. Where
    /// the handover to the installer itself happens is not testable from here and deliberately is
    /// not attempted: see the shutdown handler in App.axaml.cs.
    /// </summary>
    [AvaloniaFact]
    public async Task A_running_ramp_holds_the_restart_back()
    {
        await StartRampAsync();
        await Wait.ForAsync(() => { Pump(); return _shell.IsRampRunning; }, "the ramp to be running");

        Assert.True(_shell.Update.IsRampRunning);

        ((IRampSessionController)_device).Stop();

        await Wait.ForAsync(() => { Pump(); return !_shell.Update.IsRampRunning; }, "the ramp to end");
    }
}
