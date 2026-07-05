using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Vulcano_Control.Services;
using Vulcano_Control.ViewModels;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    private readonly MainViewModel _viewModel;

    public MainWindow(ThemeService themeService, LogService logService, SettingsService settingsService, SoundService soundService)
    {
      InitializeComponent();

      if (!ThemeService.UsesNativeFluentTheme)
      {
        // No Fluent theme available on Windows 10 - reference the hand-rolled theme
        // brushes directly so the window itself picks up the swapped dictionary.
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "WindowForegroundBrush");
      }

      var logWindow = new LogWindow(logService);
      var settingsWindow = new SettingsWindow(new SettingsViewModel(settingsService));
      Loaded += (_, _) =>
      {
        logWindow.Owner = this;
        settingsWindow.Owner = this;
      };

      _viewModel = new MainViewModel(themeService, logService, logWindow, settingsService, settingsWindow, soundService);
      DataContext = _viewModel;
      Closed += async (_, _) => await _viewModel.DisposeAsync();
    }
  }
}