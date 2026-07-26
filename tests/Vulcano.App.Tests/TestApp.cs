using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Vulcano.App.Tests.TestApp))]

namespace Vulcano.App.Tests;

/// <summary>
/// The bare Avalonia application these tests run inside.
///
/// Deliberately not the real <see cref="Vulcano.App.App"/>: that one builds the whole shell and
/// opens a Bluetooth connection when it starts, which is neither wanted nor safe in a test run. What
/// the view models actually need from Avalonia is one thing - a dispatcher that runs the jobs they
/// post, because every device event is marshalled through Dispatcher.UIThread.Post and would
/// otherwise sit in a queue nobody pumps.
/// </summary>
public class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
