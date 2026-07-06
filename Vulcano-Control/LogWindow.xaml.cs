using System.ComponentModel;
using System.Windows;
using Vulcano_Control.Services;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for LogWindow.xaml
  /// </summary>
  public partial class LogWindow : Window
  {
    public LogWindow(LogService logService, ThemeService themeService)
    {
      InitializeComponent();

      if (!ThemeService.UsesNativeFluentTheme)
      {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "WindowForegroundBrush");
      }
      ThemeService.ApplyTitleBarTheme(this, themeService.CurrentTheme);

      DataContext = logService;
      // New entries are inserted at the top (see LogService), so the newest activity is
      // already visible without forcing the scroll position around while reading history.
    }

    protected override void OnClosing(CancelEventArgs e)
    {
      e.Cancel = true;
      Hide();
    }
  }
}
