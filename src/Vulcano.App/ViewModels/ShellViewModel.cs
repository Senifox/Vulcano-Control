using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.App.Services;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>The seven views the window can show. Run only appears while a ramp is running.</summary>
public enum AppTab
{
    Control,
    Ramp,
    Run,
    Device,
    Network,
    Log,
    Settings
}

/// <summary>
/// Owns the window itself: which tab is showing, the connection state in the title bar, and the
/// one action next to it (Connect / Disconnect / Leave). The per-tab view models hang off this one
/// - this replaces the WPF version's single 972-line MainViewModel.
///
/// Every device event arrives on a background thread, so everything that touches observable state
/// goes through the dispatcher first.
/// </summary>
public partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly LogService _log;
    private readonly SoundService? _sound;
    private readonly INotifier? _notifier;

    [ObservableProperty]
    private AppTab _selectedTab = AppTab.Control;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    [NotifyPropertyChangedFor(nameof(ConnectionDetail))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    [NotifyPropertyChangedFor(nameof(IsConnectionLost))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private string _serialNumber = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunTabVisible))]
    [NotifyPropertyChangedFor(nameof(CompactDetailText))]
    private bool _isRampRunning;

    [ObservableProperty]
    private bool _isAlwaysOnTop;

    /// <summary>
    /// The small always-in-sight window. Same window, different content and size - not a second
    /// window, so the connection, the running ramp and always-on-top all survive the switch.
    /// </summary>
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>
    /// True when the numbers on screen come from <see cref="SimulatedVolcanoDevice"/> rather than a
    /// Volcano. Worth saying out loud in the title bar: the simulator is convincing enough that a
    /// screenshot of it is indistinguishable from the real thing, which is fine while building the
    /// interface and misleading anywhere else.
    /// </summary>
    public bool IsSimulated { get; }

    /// <summary>Exposed so the window can flash its taskbar button when a notification could not go
    /// to the operating system.</summary>
    public INotifier? Notifier => _notifier;

    // --- The notice in the corner, for when Windows would not show a notification ---

    [ObservableProperty]
    private bool _isNoticeVisible;

    [ObservableProperty]
    private string _noticeTitle = "";

    [ObservableProperty]
    private string _noticeMessage = "";

    private DispatcherTimer? _noticeTimer;

    /// <summary>
    /// Shows the notification in the window itself, built from the app's own card and text styles
    /// rather than the toolkit's notification control - which never drew anything here, and would
    /// have looked like a visitor from another application if it had.
    /// </summary>
    private void ShowNotice(NotificationRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            NoticeTitle = request.Title;
            NoticeMessage = request.Message;
            IsNoticeVisible = true;

            _noticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _noticeTimer.Tick -= OnNoticeExpired;
            _noticeTimer.Tick += OnNoticeExpired;

            // Restarted rather than left running, so a second notice gets its own eight seconds.
            _noticeTimer.Stop();
            _noticeTimer.Start();
        });

    private void OnNoticeExpired(object? sender, EventArgs e)
    {
        _noticeTimer?.Stop();
        IsNoticeVisible = false;
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer?.Stop();
        IsNoticeVisible = false;
    }

    public ShellViewModel(
        VolcanoDeviceOrchestrator device,
        SettingsService settingsService,
        ThemeManager themeManager,
        AppSettings settings,
        LogService log,
        SoundService? sound = null,
        INotifier? notifier = null,
        bool isSimulated = false,
        IUpdateSource? updateSource = null)
    {
        _device = device;
        _log = log;
        _sound = sound;
        _notifier = notifier;
        IsSimulated = isSimulated;

        // Built here rather than in the Settings tab because two things need it: the tab shows it,
        // and the window carries the chip that says a version is waiting.
        Update = new UpdateViewModel(updateSource ?? new NoUpdateSource());

        // The orchestrator is both a device and a ramp controller, and these three want it as one or
        // the other rather than as itself - which is what lets them be built in a test around a fake
        // device and a real RampSessionController, with no Bluetooth anywhere near it.
        Control = new ControlViewModel(device, device, settings, sound);
        Ramp = new RampViewModel(device, device, settingsService, settings);
        Run = new RunViewModel(device, device);
        Device = new DeviceViewModel(device, log);
        Network = new NetworkViewModel(device, settingsService, settings, log);
        Log = new LogViewModel(log);
        Settings = new SettingsViewModel(settingsService, settings, themeManager, device, sound, notifier, Update);

        // The chip in the title bar appears the moment a version finishes downloading.
        Update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.IsReady)) OnPropertyChanged(nameof(IsUpdateReady));
        };

        // The compact line is stitched together from both, so it follows either of them changing.
        // Cheap enough to refresh on any of their properties - it is one short string.
        Control.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CompactDetailText));
        Run.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CompactDetailText));

        Loc.LanguageChanged += OnLanguageChanged;

        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.ProgressChanged += OnRampProgressChanged;
        _device.Completed += OnRampEnded;
        _device.Stopped += OnRampEnded;
        _device.WarmupCompleted += OnRampWarmupCompleted;

        // Somebody has to listen to these. The error channel runs from the device through the
        // orchestrator to here and nothing was plugged in at this end, so a refused or failed write
        // vanished without a trace - a watcher clicking the heater got no message and no log line.
        // Both interfaces declare ErrorOccurred, hence the casts to say which one is meant.
        ((IVolcanoDevice)_device).ErrorOccurred += OnDeviceErrorOccurred;
        ((IRampSessionController)_device).ErrorOccurred += OnRampErrorOccurred;

        if (_notifier is not null) _notifier.FellBackToWindow += OnNotificationFellBack;
    }

    private void OnNotificationFellBack(object? sender, NotificationRequest request) => ShowNotice(request);

    private void OnDeviceErrorOccurred(object? sender, string message) =>
        _log.Log(message, LogLevel.Warning);

    private void OnRampErrorOccurred(object? sender, string message) =>
        _log.Log(message, LogLevel.Warning);

    /// <summary>
    /// Nudges every view model to re-read its computed labels. The tabs and static text follow the
    /// resource swap on their own; a sentence a view model built - "Connection lost", "the ramp is
    /// paused at 205 °C" - was assembled in the old language and has to be asked again.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshText();
        Control.RefreshText();
        Ramp.RefreshText();
        Run.RefreshText();
        Device.RefreshText();
        Network.RefreshText();
        Log.RefreshText();
        Settings.RefreshText();
        Update.RefreshText();
    }

    /// <summary>The cockpit. Owns everything about live temperature, heater, pump and target.</summary>
    public ControlViewModel Control { get; }

    /// <summary>The ramp editor and its saved profiles.</summary>
    public RampViewModel Ramp { get; }

    /// <summary>The running ramp, while one is running.</summary>
    public RunViewModel Run { get; }

    /// <summary>The device's own settings, written straight through.</summary>
    public DeviceViewModel Device { get; }

    /// <summary>Hosting and joining the LAN relay.</summary>
    public NetworkViewModel Network { get; }

    /// <summary>The log, filtered by level.</summary>
    public LogViewModel Log { get; }

    /// <summary>Application settings; every change saves itself.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Checking for and holding on to a new version.</summary>
    public UpdateViewModel Update { get; }

    /// <summary>Drives the chip in the title bar: a version is downloaded and waiting.</summary>
    public bool IsUpdateReady => Update.IsReady;

    /// <summary>
    /// The startup update check, kicked off once the window is up. Not in the constructor: it goes
    /// to the network, and nothing on the way to a visible window should wait on a server.
    /// </summary>
    public Task CheckForUpdatesAsync(bool automatic) => Update.RunStartupCheckAsync(automatic);

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    /// <summary>Scanning or connecting - the point at which both buttons should be unavailable.</summary>
    public bool IsBusy => ConnectionState is ConnectionState.Scanning or ConnectionState.Connecting;

    /// <summary>True while this instance is driving someone else's device over the LAN relay.</summary>
    public bool IsRemote => _device.IsRemote;

    public string? HostName => _device.HostName;

    /// <summary>A remote client leaves the relay; it does not disconnect a Bluetooth link it never had.</summary>
    public string DisconnectLabel => Strings.Get(IsRemote ? "Action.Leave" : "Action.Disconnect");

    public string ConnectionText => Strings.Get(ConnectionState switch
    {
        ConnectionState.Connected => "State.Connected",
        ConnectionState.Scanning => "State.Searching",
        ConnectionState.Connecting => "State.Connecting",
        ConnectionState.Error => "State.ConnectionLost",
        _ => "State.NotConnected",
    });

    /// <summary>The Run tab is only offered while there is a run to look at.</summary>
    public bool IsRunTabVisible => IsRampRunning;

    /// <summary>
    /// The band under the toolbar that says why nothing is happening. The WPF version had no such
    /// thing: it just sat there with an empty readout, which looks the same whether the device is
    /// off, out of range or simply not connected yet.
    /// </summary>
    public bool ShowConnectionBanner => !IsConnected && ConnectionState != ConnectionState.Connecting;

    public bool IsConnectionLost => ConnectionState == ConnectionState.Error;

    public string ConnectionDetail => ConnectionState switch
    {
        ConnectionState.Scanning => Strings.Get("Connection.Searching.Hint"),
        ConnectionState.Error => IsRampRunning
            ? Strings.Get("Connection.Lost.RampPaused", Formatting.Celsius(Control.CurrentTemperature))
            : Strings.Get("Connection.Lost.Hint"),
        _ => Strings.Get("Connection.NotConnected.Hint"),
    };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (await _device.ScanAndConnectAsync())
        {
            var info = await _device.ReadDeviceInfoAsync();
            if (info is { } deviceInfo)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SerialNumber = deviceInfo.SerialNumber);
            }
        }
    }

    private bool CanConnect() => !IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (IsRemote)
        {
            await _device.DisconnectFromServerAsync();
        }
        else
        {
            await _device.DisconnectAsync();
        }

        await Dispatcher.UIThread.InvokeAsync(() => SerialNumber = "");
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand]
    private void ShowTab(AppTab tab) => SelectedTab = tab;

    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    /// <summary>
    /// The one line under the big number in compact mode. With a ramp running it is about the ramp,
    /// otherwise about the target and how long the device will stay on - in both cases the thing
    /// you glanced over for.
    /// </summary>
    public string CompactDetailText => IsRampRunning
        ? Strings.Get("Compact.WithRamp", Formatting.Celsius(Run.PlanNow), Run.TimeLeftText)
        : Strings.Get("Compact.WithoutRamp", Formatting.Celsius(Control.TargetTemperature), Control.AutoShutOffText);

    // --- Device events, all arriving off the UI thread ---

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            var wasRunning = IsRampRunning;
            ConnectionState = state;
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(HostName));
            OnPropertyChanged(nameof(DisconnectLabel));

            // Losing the device mid-ramp is the other thing worth interrupting somebody for: the ramp
            // pauses itself and waits, so a run they thought was finishing is now standing still.
            if (wasRunning && state == ConnectionState.Error)
            {
                _notifier?.Notify(
                    Strings.Get("Notify.ConnectionLost"),
                    Strings.Get("Notify.ConnectionLost.Body"));
            }
        });

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsRampRunning) return;

            IsRampRunning = true;
            Control.IsRampRunning = true;
            Ramp.IsRampRunning = true;
            // Takes the restart button away: a downloaded update waits for the run to be over.
            Update.IsRampRunning = true;
            // A ramp that starts anywhere - here, or on another machine through the relay - brings
            // the Run tab up by itself.
            SelectedTab = AppTab.Run;
        });

    /// <summary>The device has arrived at the ramp's first point and the clock has started - the one
    /// moment in a run worth waiting for, and the reason the sound exists.</summary>
    private void OnRampWarmupCompleted(object? sender, EventArgs e)
    {
        _sound?.PlayHeatReached();
        _notifier?.Notify(
            Strings.Get("Notify.WarmupReached"),
            Strings.Get("Notify.WarmupReached.Body", Formatting.Celsius(Control.CurrentTemperature)));
    }

    /// <summary>Somebody stopped it, so they are already looking at the window. No notification.</summary>
    private void OnRampEnded(object? sender, EventArgs e) => HandleRampEnded();

    private void OnRampEnded(object? sender, double resetTemperatureCelsius)
    {
        // A finished ramp, as opposed to one somebody stopped: the same sound the device's own
        // shut-off makes, because from across the room it means the same thing. And half an hour
        // after the click that started it, this is the notification the feature exists for.
        _sound?.PlayShutdown();
        _notifier?.Notify(
            Strings.Get("Notify.RampFinished"),
            Strings.Get("Notify.RampFinished.Body", Formatting.Celsius(resetTemperatureCelsius)));

        HandleRampEnded();
    }

    private void HandleRampEnded() =>
        Dispatcher.UIThread.Post(() =>
        {
            IsRampRunning = false;
            Control.IsRampRunning = false;
            Ramp.IsRampRunning = false;
            Update.IsRampRunning = false;
            Run.Reset();
            if (SelectedTab == AppTab.Run) SelectedTab = AppTab.Control;
        });

    public async ValueTask DisposeAsync()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
        _device.ProgressChanged -= OnRampProgressChanged;
        _device.Completed -= OnRampEnded;
        _device.Stopped -= OnRampEnded;
        _device.WarmupCompleted -= OnRampWarmupCompleted;
        ((IVolcanoDevice)_device).ErrorOccurred -= OnDeviceErrorOccurred;
        ((IRampSessionController)_device).ErrorOccurred -= OnRampErrorOccurred;
        if (_notifier is not null) _notifier.FellBackToWindow -= OnNotificationFellBack;
        _noticeTimer?.Stop();
        Control.Dispose();
        Ramp.Dispose();
        Run.Dispose();
        Device.Dispose();
        Network.Dispose();
        Log.Dispose();

        _log.Log(Strings.Get("Log.ShuttingDown"));
        _device.Dispose();
        await _device.DisposeAsync();
    }

    /// <summary>Re-reads every computed label. Called after a language change; passing a
    /// null property name is the framework's "all of them" signal.</summary>
    public void RefreshText() => OnPropertyChanged(new PropertyChangedEventArgs(null));
}

