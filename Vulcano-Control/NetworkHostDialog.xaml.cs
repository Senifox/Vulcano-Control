using System.Windows;
using Vulcano_Control.Services;
using Vulcano_Control.ViewModels;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for NetworkHostDialog.xaml
  /// </summary>
  public partial class NetworkHostDialog : Window
  {
    public NetworkHostViewModel ViewModel { get; }

    public NetworkHostDialog(NetworkHostViewModel viewModel, ThemeService themeService)
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
