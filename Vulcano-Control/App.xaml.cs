using System.Windows;
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

      var settingsService = new SettingsService();
      var themeService = new ThemeService(settingsService);
      themeService.ApplyStartupTheme();

      var mainWindow = new MainWindow(themeService);
      mainWindow.Show();
    }
  }

}
