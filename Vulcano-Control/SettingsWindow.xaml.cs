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

    public SettingsWindow(SettingsViewModel viewModel)
    {
      InitializeComponent();

      if (!ThemeService.UsesNativeFluentTheme)
      {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "WindowForegroundBrush");
      }

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
