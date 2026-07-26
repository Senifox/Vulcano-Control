using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Vulcano.App.Services;
using Vulcano.App.ViewModels;
using Vulcano.App.Views;
using Vulcano.Bluetooth.Windows;
using Vulcano.Core.Services;

namespace Vulcano.App;

public partial class App : Application
{
    private ShellViewModel? _shell;
    private ThemeManager? _themeManager;
    private WindowsSoundPlayer? _sound;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = new LogService();
            var settingsService = new SettingsService();
            var settings = settingsService.Load();

            // Before anything is built: the log writes its first line immediately, and the views
            // read their labels out of the resources the moment they are constructed.
            Loc.Apply(settings.Language);

            _themeManager = new ThemeManager();
            _themeManager.Apply(settings.Theme);

            // Real Bluetooth by default; --simulate gets the in-memory device, which is what makes
            // it possible to work on the interface without a Volcano on the desk.
            var simulate = desktop.Args?.Contains("--simulate", StringComparer.OrdinalIgnoreCase) == true;
            log.Log(Strings.Get(simulate ? "Log.UsingSimulator" : "Log.UsingRealAdapter"));

            var orchestrator = new VolcanoDeviceOrchestrator(() => CreateDevice(simulate, log), log)
            {
                PushThresholdCelsius = settings.RampPushThresholdCelsius,
            };

            // Before the notifier: it checks for this registration and quietly writes notifications
            // to the window instead when it is missing.
            WindowsAppIdentity.Register(log);

            _sound = new WindowsSoundPlayer(log);
            var sounds = new SoundService(_sound, log) { SoundEnabled = settings.SoundEnabled };
            var notifier = new WindowsToastNotifier(log) { Enabled = settings.DesktopNotifications };

            _shell = new ShellViewModel(
                orchestrator, settingsService, _themeManager, settings, log, sounds, notifier, simulate);
            desktop.MainWindow = new MainWindow { DataContext = _shell };

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_shell is not null) await _shell.DisposeAsync();
                _sound?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The one place that decides which Bluetooth stack is in use. Everything above it takes an
    /// IVolcanoDevice and cannot tell the difference - which is what will let the BlueZ adapter slot
    /// in here with nothing else changing.
    /// </summary>
    private static IVolcanoDevice CreateDevice(bool simulate, LogService log) =>
        simulate
            ? new SimulatedVolcanoDevice(log)
            : new BluetoothVolcanoDevice(new WinRtVolcanoTransport(log), log);
}
