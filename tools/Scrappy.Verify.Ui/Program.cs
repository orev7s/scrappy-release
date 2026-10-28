using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Scrappy.App;
using Scrappy.App.ViewModels;
using Scrappy.App.Views;
using Scrappy.Discovery;
using Scrappy.Settings;

// Checks for the two UX features that no other test can reach: the pure placement/migration/
// preselect rules, and the window as it actually renders.
//
//   check        geometry clamp, v1 state migration, tablet preselect   (no window, no Skia)
//   shots <dir>  render the real MainWindow to PNG with headless Skia
//   ui <dir>     check + shots, plus the view-model wiring
//   all <dir>    everything (default; <dir> defaults to ./artifacts)
//
// Exit code 0 means every requested check passed.

namespace Scrappy.Verify.Ui;

internal static class Program
{
    private static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "all";
        string outDirectory = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "artifacts"));

        Console.WriteLine($"Scrappy.Verify.Ui: {command}");

        switch (command)
        {
            case "check":
                Checks.All();
                _failures += Checks.Failures;
                return Report();

            case "shots":
            case "ui":
            case "all":
                Checks.All();
                _failures += Checks.Failures;
                Directory.CreateDirectory(outDirectory);
                Render(outDirectory, includeViewModelChecks: command is "ui" or "all");
                return Report();

            default:
                Console.WriteLine("usage: Scrappy.Verify.Ui [check|shots|ui|all] [output-directory]");
                return 2;
        }
    }

    /// <summary>
    /// Starts the real application type - same styles, same window, same view model - on the
    /// headless platform with Skia drawing, so what is captured is the pixels the desktop build
    /// would paint, not a mock-up.
    /// </summary>
    private static void Render(string outDirectory, bool includeViewModelChecks)
    {
        AppBuilder.Configure<ScrappyApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        string temporary = Path.Combine(Path.GetTempPath(), "scrappy-verify-ui-shots-" + Guid.NewGuid().ToString("n"));

        try
        {
            // 1. A launch where the state file remembers a 1440x900 window at 120,90. This is the
            //    "second launch after the user resized the window" case.
            Shot(outDirectory, "main-restored-1440x900.png", temporary, "restored",
                new WindowPlacement { Left = 120, Top = 90, Width = 1440, Height = 900 },
                includeViewModelChecks);

            // 2. The case the clamp exists for: the frame was on a 4K monitor at x=4040 that is not
            //    plugged in any more.
            Shot(outDirectory, "main-clamped-from-disconnected-monitor.png", temporary, "clamped",
                new WindowPlacement { Left = 4040, Top = 300, Width = 1600, Height = 900 },
                includeViewModelChecks: false);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
                // Temp directory, best effort.
            }
        }
    }

    private static void Shot(
        string outDirectory,
        string fileName,
        string temporaryRoot,
        string label,
        WindowPlacement placement,
        bool includeViewModelChecks)
    {
        string directory = Path.Combine(temporaryRoot, label);
        Directory.CreateDirectory(directory);

        var store = new AppStateStore(Path.Combine(directory, "state.json"));
        var settings = new SettingsStore(ScrcpySchema.LoadEmbedded());
        settings.SetValue("max_size", "2560");
        settings.SetValue("max_fps", "60");
        settings.SetValue("video_codec", "h265");

        // The remembered tablet: the picker preselects it and marks the row, which is what the
        // second feature is.
        var file = new AppStateFile
        {
            Address = "192.168.42.129:27183",
            LastDeviceName = "Xiaomi Pad 8",
            LastDeviceAddress = "192.168.42.129:27183",
            Window = placement,
        };
        store.Save(file);

        var autosave = new AppStateAutosave(store, settings, TimeSpan.Zero);
        autosave.Restore(store.Load());

        var profiles = new ProfileStore(Path.Combine(directory, "profiles.json"));
        var viewModel = new MainViewModel(settings, profiles, autosave);

        // Two tablets answered: the remembered one is the wired entry, the other is a second tablet
        // on the Wi-Fi, so "preselected" and "last used" are visibly a choice and not the only row.
        viewModel.SeedDemoDevices(new[]
        {
            Checks.Device("Xiaomi Pad 8", "192.168.42.129", "wired"),
            Checks.Device("Pixel Tablet", "192.168.1.40", "wifi"),
        });

        var window = new MainWindow { DataContext = viewModel };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        string path = Path.Combine(outDirectory, fileName);
        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();

        if (frame is null)
        {
            Checks.Check($"render {fileName}", false, "the headless renderer produced no frame");
            window.Close();
            viewModel.Dispose();
            autosave.Dispose();
            return;
        }

        frame.Save(path);
        int width = frame.PixelSize.Width;
        int height = frame.PixelSize.Height;
        Checks.Check($"render {fileName}", width > 0 && height > 0, $"{width}x{height} px -> {path}");

        // Read the placed frame now: closing the window resets Position, and the checks below run
        // after the close.
        double placedWidth = window.Width;
        double placedHeight = window.Height;
        int placedX = window.Position.X;
        int placedY = window.Position.Y;

        Console.WriteLine($"      window frame   : {window.Width:0}x{window.Height:0} at {window.Position.X},{window.Position.Y}");
        Console.WriteLine($"      selected tablet: {viewModel.SelectedDevice?.DisplayName ?? "(none)"} lastUsed={viewModel.SelectedDevice?.LastUsed}");
        Console.WriteLine($"      session running: {viewModel.IsRunning}");

        if (includeViewModelChecks)
        {
            // Feature 2, end to end: the remembered tablet is the row that is selected, it carries
            // the badge, and nothing was started.
            Checks.Check("ui picker preselects the remembered tablet",
                viewModel.SelectedDevice?.AddressText == "192.168.42.129:27183",
                viewModel.SelectedDevice?.AddressText ?? "(none)");
            Checks.Check("ui the remembered tablet carries the last-used badge",
                viewModel.SelectedDevice?.LastUsed == true);
            Checks.Check("ui the other tablet does not carry the badge",
                viewModel.Devices.Count(c => c.LastUsed) == 1,
                $"{viewModel.Devices.Count(c => c.LastUsed)} marked");
            Checks.Check("ui the address field follows the selection",
                viewModel.Address == "192.168.42.129:27183",
                viewModel.Address);
            Checks.Check("ui no session is started by the preselect", !viewModel.IsRunning);

            // Feature 1, end to end: the frame came from the state file, not from the XAML default.
            Checks.Check("ui the window is restored at the remembered size",
                Math.Abs(placedWidth - 1440) <= 1 && Math.Abs(placedHeight - 900) <= 1,
                $"{placedWidth:0}x{placedHeight:0}");
            Checks.Check("ui the window is not the hard-coded default size",
                Math.Abs(placedWidth - 1120) > 1 || Math.Abs(placedHeight - 700) > 1,
                $"{placedWidth:0}x{placedHeight:0}");
            Checks.Check("ui the window is restored at the remembered position",
                placedX == 120 && placedY == 90,
                $"{placedX},{placedY}");
        }

        // Closing writes the frame back, which is what makes the next launch restore this one.
        window.Close();
        viewModel.Dispose();
        autosave.Dispose();

        WindowPlacement? written = new AppStateStore(Path.Combine(directory, "state.json")).Load().State.Window;
        if (includeViewModelChecks)
        {
            Checks.Check("ui closing writes the frame back to the state file",
                written is { Width: > 1, Height: > 1 },
                written is null ? "(no window section)" : $"{written.Left:0},{written.Top:0} {written.Width:0}x{written.Height:0}");
            Checks.Check("ui the written frame matches the window that was closed",
                written is not null && Math.Abs(written.Width - 1440) <= 1 && Math.Abs(written.Height - 900) <= 1,
                written is null ? "(none)" : $"{written.Width:0}x{written.Height:0}");
        }
        else
        {
            // The clamp case: the frame from the missing monitor must come back on this screen, at a
            // positive position, instead of the 4040 it was saved at.
            Checks.Check("ui the clamped window was moved out of the missing monitor",
                placedX > 0 && placedX < 4040 && placedY > 0,
                $"{placedX},{placedY}");
            Checks.Check("ui the clamped window kept the remembered size",
                Math.Abs(placedWidth - 1600) <= 1 && Math.Abs(placedHeight - 900) <= 1,
                $"{placedWidth:0}x{placedHeight:0}");
            Checks.Check("ui the clamped frame is written back inside the screen",
                written is not null && written.Left < 4040 && written.Left >= 0,
                written is null ? "(none)" : $"{written.Left:0},{written.Top:0}");
        }
    }

    private static int Report()
    {
        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("ALL PASS");
            return 0;
        }

        Console.WriteLine($"FAIL {_failures} check(s)");
        return 1;
    }
}
