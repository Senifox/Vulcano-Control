using System;
using System.Linq;
using System.Reflection;
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
                orchestrator, settingsService, _themeManager, settings, log, sounds, notifier, simulate,
                new VelopackUpdateSource(log));
            desktop.MainWindow = new MainWindow { DataContext = _shell };

            // After the window, and not awaited: an update check is a network call, and a repository
            // that is slow to answer must not be able to delay the app coming up. Whatever it finds
            // is downloaded and then waits for the app to be closed - see UpdateViewModel.
            _ = _shell.CheckForUpdatesAsync(settings.AutomaticUpdates);

            // The other half of updating in the background: saying so afterwards. The version on
            // disk changed while nobody was watching, so this is the one moment to mention it.
            _shell.ShowWhatsNewIfVersionChanged(
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "");

            desktop.ShutdownRequested += async (_, _) =>
            {
                // Synchronously, and first. This hands a downloaded update to an installer that
                // waits for this process to end, and it is the one thing here that must happen -
                // the rest is tidying up after a process that is going away anyway.
                //
                // Not inside DisposeAsync, where it was: an async handler is not awaited by the
                // shutdown, so everything past the first suspension point is a race the app can
                // lose. It did lose it - the update downloaded, the app closed, and the version on
                // disk stayed the old one. And no test can see that, because a fake device
                // disposes synchronously and the method then runs to the end regardless. So it
                // lives here, where the ordering is the whole of what this handler says.
                _shell?.Update.ApplyOnExit();

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
