using System;
using Avalonia;
using Velopack;

namespace Vulcano.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Has to be the literal first thing: the installer starts the app with its own arguments to
        // create shortcuts and finish installing, and Run() handles those and exits. Anything before
        // it - a window, a log file, a settings read - would happen during an install too.
        // Checking for updates is a separate matter and happens once the window is up.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    // No .WithInterFont(): the app embeds IBM Plex Sans/Mono instead, so Windows and Linux
    // lay out identically.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
