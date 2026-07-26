using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Vulcano.App.Services;
using Vulcano.App.ViewModels;
using Vulcano.App.Views;
using Vulcano.Core.Services;

namespace Vulcano.App;

public partial class App : Application
{
    private ShellViewModel? _shell;
    private ThemeManager? _themeManager;

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

            // The Bluetooth adapter is not ported yet, so there is nothing else to choose: every
            // run talks to the simulated device. Once the WinRT transport lands this becomes a
            // factory that picks the real device and falls back to the simulator on --simulate.
            log.Log(Strings.Get("Log.NoAdapter"));

            var orchestrator = new VolcanoDeviceOrchestrator(() => new SimulatedVolcanoDevice(log), log)
            {
                PushThresholdCelsius = settings.RampPushThresholdCelsius,
            };

            _shell = new ShellViewModel(orchestrator, settingsService, _themeManager, settings, log);
            desktop.MainWindow = new MainWindow { DataContext = _shell };

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_shell is not null) await _shell.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
