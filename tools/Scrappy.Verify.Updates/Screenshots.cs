using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Scrappy.App;
using Scrappy.App.Updates;

namespace Scrappy.Verify.Updates;

/// <summary>
/// Renders the real <see cref="UpdateDialogWindow"/> - the same class the app shows - through
/// Avalonia's headless platform with the Skia renderer, and saves the pixels.
///
/// This is not a mockup: the window, its styles from App.axaml and the real layout all come from
/// the application. The only fabricated part is the release it is showing.
/// </summary>
public static class Screenshots
{
    public static int Capture(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Headless platform, but not headless drawing: the frame comes from Skia.
            AppBuilder.Configure<ScrappyApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();

            UpdateInfo latest = new(
                AppVersion.TryParse("v0.2.2")!,
                "v0.2.2",
                "Scrappy 0.2.2",
                new DateTimeOffset(2026, 10, 29, 9, 12, 0, TimeSpan.Zero),
                """
                Sound follows the tablet again.

                The PC used to keep playing the tablet's audio after the session stopped, and the
                volume slider moved the system mixer instead of the stream. Both are fixed: the
                stream stops with the session, and the slider now scales the samples on the way out.

                Discovery also stopped missing a tablet that answered slowly, and the cable sweep
                is no longer aborted by the first Wi-Fi answer.

                Upgrade both ends together. The PC app and the tablet app share a version number.
                """,
                "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.2");

            var window = new UpdateDialogWindow(latest, AppVersion.Current);
            window.Show();

            // Two passes: the first completes layout, the second renders the settled frame.
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.WriteLine("SHOT FAIL no frame was rendered");
                return 1;
            }

            string path = Path.Combine(directory, "update-dialog.png");
            frame.Save(path);
            Console.WriteLine($"SHOT PASS {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SHOT FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
