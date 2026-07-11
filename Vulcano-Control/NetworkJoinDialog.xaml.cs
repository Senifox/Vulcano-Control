using System.Windows;
using Vulcano_Control.Services;
using Vulcano_Control.ViewModels;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for NetworkJoinDialog.xaml
  /// </summary>
  public partial class NetworkJoinDialog : Window
  {
    public NetworkJoinViewModel ViewModel { get; }

    public NetworkJoinDialog(NetworkJoinViewModel viewModel, ThemeService themeService)
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
      viewModel.Confirmed += (_, _) => { DialogResult = true; Close(); };
      viewModel.Cancelled += (_, _) => { DialogResult = false; Close(); };
    }
  }
}
