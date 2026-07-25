using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Vulcano.App.Views;

/// <summary>
/// The window draws its own title bar (ExtendClientAreaToDecorationsHint), which is what makes the
/// app look the same under Windows, GNOME and KDE - and what let the WPF version's DWM P/Invoke go.
/// The cost is that dragging, maximising and closing are ours to wire up; that is all this file is.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximised();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimiseClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object? sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
