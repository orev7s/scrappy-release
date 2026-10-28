using Avalonia;

namespace Scrappy.App;

internal static class Program
{
    /// <summary>Command line, kept deliberately tiny: the app is a window, not a CLI.</summary>
    internal static AppOptions Options { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        Options = AppOptions.Parse(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ScrappyApp>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>
/// Switches that exist for development and for evidence capture, not for the product surface:
/// <c>--demo</c> starts a synthetic tablet on loopback so the app can be exercised with no device,
/// <c>--shot</c> saves what the window actually shows, and <c>--dump</c> writes the undecoded
/// elementary stream to a file so a decode problem can be diagnosed without the tablet.
/// </summary>
internal sealed record AppOptions
{
    /// <summary>
    /// Empty by default: the address comes from the last session, and the picker fills it. A
    /// hard-coded default is how the field once shipped pointing at the PC itself.
    /// </summary>
    public string? Address { get; init; }

    public bool Demo { get; init; }

    public bool AutoStart { get; init; }

    public string? ScreenshotPath { get; init; }

    public int ScreenshotDelayMs { get; init; } = 1600;

    /// <summary>Capture the settings window instead of the session window.</summary>
    public bool ShotSettings { get; init; }

    /// <summary>Pre-fills the settings search box, so a filtered view can be captured.</summary>
    public string? ShotFilter { get; init; }

    /// <summary>Replaces the empty picker with a fixed list, for screenshots with no hardware.</summary>
    public IReadOnlyList<string> DemoDevices { get; init; } = [];

    /// <summary>Writes the raw elementary stream here. Diagnostics only.</summary>
    public string? DumpPath { get; init; }

    public static AppOptions Parse(string[] args)
    {
        string? address = null;
        bool demo = false, autoStart = false;
        string? screenshot = null;
        int delay = 1600;
        bool shotSettings = false;
        string? shotFilter = null;
        var demoDevices = new List<string>();
        string? dump = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--address" when i + 1 < args.Length:
                    address = args[++i];
                    break;
                case "--demo":
                    demo = true;
                    address ??= "127.0.0.1:27183";
                    break;
                case "--autostart":
                    autoStart = true;
                    break;
                case "--shot" when i + 1 < args.Length:
                    screenshot = args[++i];
                    break;
                case "--shot-delay" when i + 1 < args.Length:
                    delay = int.Parse(args[++i]);
                    break;
                case "--shot-settings":
                    shotSettings = true;
                    break;
                case "--shot-filter" when i + 1 < args.Length:
                    shotFilter = args[++i];
                    break;
                case "--demo-device" when i + 1 < args.Length:
                    demoDevices.Add(args[++i]);
                    break;
                case "--dump" when i + 1 < args.Length:
                    dump = args[++i];
                    break;
            }
        }

        return new AppOptions
        {
            Address = address,
            Demo = demo,
            AutoStart = autoStart || screenshot is not null,
            ScreenshotPath = screenshot,
            ScreenshotDelayMs = delay,
            ShotSettings = shotSettings,
            ShotFilter = shotFilter,
            DemoDevices = demoDevices,
            DumpPath = dump,
        };
    }
}
