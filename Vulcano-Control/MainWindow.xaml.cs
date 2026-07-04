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
using Vulcano_Control.ViewModels;

namespace Vulcano_Control
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
      InitializeComponent();
      DataContext = _viewModel;
      Closed += async (_, _) => await _viewModel.DisposeAsync();
    }
  }
}