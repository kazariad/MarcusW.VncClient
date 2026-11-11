using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MarcusW.VncClient.Avalonia.Tests.TestApp))]

namespace MarcusW.VncClient.Avalonia.Tests;

public class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
