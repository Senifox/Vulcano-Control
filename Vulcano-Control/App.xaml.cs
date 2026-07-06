using System.Windows;
using Velopack;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
    // Explicit entry point (instead of the WPF-generated one - see the Page/StartupObject
    // setup in the .csproj) so VelopackApp.Build().Run() can be the literal first line, as
    // Velopack requires: this is how it hooks into first-run/update/uninstall events raised
    // by the installer, and it exits the process immediately for those (no WPF overhead should
    // run in that case).
    [STAThread]
    private static void Main(string[] args)
    {
      VelopackApp.Build().Run();

      var app = new App();
      app.InitializeComponent();
      app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);

      var logService = new LogService();

      // The log window is the best place for the user to see what went wrong, even for
      // errors that aren't already funneled through a service's own error handling.
      DispatcherUnhandledException += (_, args) =>
        logService.Log($"Unerwarteter Fehler: {args.Exception.Message}", LogLevel.Error);
      AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        logService.Log($"Unerwarteter Fehler: {(args.ExceptionObject as Exception)?.Message ?? args.ExceptionObject}", LogLevel.Error);

      var settingsService = new SettingsService();
      var themeService = new ThemeService(settingsService);
      themeService.ApplyStartupTheme();

      var soundService = new SoundService(logService) { SoundEnabled = settingsService.Load().SoundEnabled };
      var updateService = new UpdateService(logService);

      var mainWindow = new MainWindow(themeService, logService, settingsService, soundService, updateService);
      mainWindow.Show();
    }
  }

}
