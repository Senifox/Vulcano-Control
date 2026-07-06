using System.Windows;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
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

      var mainWindow = new MainWindow(themeService, logService, settingsService, soundService);
      mainWindow.Show();
    }
  }

}
