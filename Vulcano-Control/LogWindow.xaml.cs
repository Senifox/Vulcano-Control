using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Vulcano_Control.Models;
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

      var view = CollectionViewSource.GetDefaultView(logService.Entries);
      view.Filter = FilterEntry;
    }

    private bool FilterEntry(object item) =>
      item is LogEntry entry && entry.Level switch
      {
        LogLevel.Debug => DebugFilterCheckBox.IsChecked == true,
        LogLevel.Info => InfoFilterCheckBox.IsChecked == true,
        LogLevel.Warning => WarningFilterCheckBox.IsChecked == true,
        LogLevel.Error => ErrorFilterCheckBox.IsChecked == true,
        _ => true
      };

    private void OnLevelFilterChanged(object sender, RoutedEventArgs e) =>
      CollectionViewSource.GetDefaultView(LogListView.ItemsSource).Refresh();

    protected override void OnClosing(CancelEventArgs e)
    {
      e.Cancel = true;
      Hide();
    }
  }
}
