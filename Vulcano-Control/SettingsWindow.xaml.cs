using System.ComponentModel;
using System.Windows;
using Vulcano_Control.Services;
using Vulcano_Control.ViewModels;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for SettingsWindow.xaml
  /// </summary>
  public partial class SettingsWindow : Window
  {
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel, ThemeService themeService)
    {
      InitializeComponent();

      if (!ThemeService.UsesNativeFluentTheme)
      {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "WindowForegroundBrush");
      }
      ThemeService.ApplyTitleBarTheme(this, themeService.CurrentTheme);

      ViewModel = viewModel;
      DataContext = viewModel;
      viewModel.SettingsSaved += (_, _) => Hide();
      viewModel.Cancelled += (_, _) => Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
      e.Cancel = true;
      Hide();
    }
  }
}
