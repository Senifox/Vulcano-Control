using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Vulcano.Core.Services;
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
            if (_shell is not null)
            {
                _shell.PropertyChanged -= OnShellPropertyChanged;
                if (_shell.Notifier is { } previous) previous.FellBackToWindow -= OnNotificationFellBack;
            }

            _shell = DataContext as ShellViewModel;
            if (_shell is not null)
            {
                _shell.PropertyChanged += OnShellPropertyChanged;
                if (_shell.Notifier is { } notifier) notifier.FellBackToWindow += OnNotificationFellBack;
                ApplyCompact(_shell.IsCompact);
            }
        };
    }

    /// <summary>
    /// Windows would not take the notification. The card in the corner is the view model's business;
    /// this is the half that only a window can do - flashing the taskbar button, which is what gets
    /// noticed when the app is behind something else. Which is the entire situation being solved.
    /// </summary>
    private void OnNotificationFellBack(object? sender, NotificationRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive) FlashTaskbarButton();
        });

    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint TimeoutMs;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Keeps flashing until the window is brought to the front, which is the behaviour of
    /// every other app that wants attention without stealing focus.</summary>
    private void FlashTaskbarButton()
    {
        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
            TimeoutMs = 0,
        };

        FlashWindowEx(ref info);
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
