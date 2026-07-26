using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vulcano.App.ViewModels;

namespace Vulcano.App.Views;

/// <summary>
/// The window draws its own title bar (ExtendClientAreaToDecorationsHint), which is what makes the
/// app look the same under Windows, GNOME and KDE - and what let the WPF version's DWM P/Invoke go.
/// The cost is that dragging, maximising and closing are ours to wire up; that is all this file is.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Compact mode is 360 x 150; the full window's size is remembered so leaving it
    /// puts everything back where it was rather than at some default.</summary>
    private const double CompactWidth = 360;
    private const double CompactHeight = 150;

    private double _fullWidth;
    private double _fullHeight;
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();

        _fullWidth = Width;
        _fullHeight = Height;

        DataContextChanged += (_, _) =>
        {
            if (_shell is not null) _shell.PropertyChanged -= OnShellPropertyChanged;

            _shell = DataContext as ShellViewModel;
            if (_shell is not null)
            {
                _shell.PropertyChanged += OnShellPropertyChanged;
                ApplyCompact(_shell.IsCompact);
            }
        };
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsCompact) && _shell is not null)
        {
            ApplyCompact(_shell.IsCompact);
        }
    }

    /// <summary>
    /// Resizes the window for the mode. The minimum has to move before the size does in each
    /// direction, or the clamp fights the assignment: 360 is below the full window's 900 minimum,
    /// and the full size is above the compact one's.
    ///
    /// Topmost is deliberately left alone - always-on-top stays whatever the user set it to, which
    /// is the point of having it while a ramp runs.
    /// </summary>
    private void ApplyCompact(bool compact)
    {
        if (compact)
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

            _fullWidth = Width;
            _fullHeight = Height;

            MinWidth = CompactWidth;
            MinHeight = CompactHeight;
            Width = CompactWidth;
            Height = CompactHeight;
            CanResize = false;
        }
        else
        {
            CanResize = true;
            Width = _fullWidth;
            Height = _fullHeight;
            MinWidth = 900;
            MinHeight = 648;
        }
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
