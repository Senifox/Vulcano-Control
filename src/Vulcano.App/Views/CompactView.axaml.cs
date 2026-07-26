using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vulcano.App.ViewModels;

namespace Vulcano.App.Views;

public partial class CompactView : UserControl
{
    public CompactView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Dragging works the same as in the full window. Double-click leaves compact mode, which is
    /// the shortcut the design asks for - and the thing people try first when a small window is in
    /// the way.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            if (DataContext is ShellViewModel shell) shell.IsCompact = false;
            return;
        }

        (this.GetVisualRoot() as Window)?.BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        (this.GetVisualRoot() as Window)?.Close();
}
