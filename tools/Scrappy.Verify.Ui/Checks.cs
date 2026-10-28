using System.Text.Json;
using Scrappy.App.ViewModels;
using Scrappy.App.Views;
using Scrappy.Discovery;
using Scrappy.Settings;

namespace Scrappy.Verify.Ui;

/// <summary>
/// The parts of the two UX features that are pure enough to prove without a window: the geometry
/// clamp, the state-file migration, and the tablet preselect rule. Everything here runs headless and
/// finishes in milliseconds, so it can gate a commit.
/// </summary>
internal static class Checks
{
    private static int _failures;

    public static int Failures => _failures;

    public static void All()
    {
        WindowClamp();
        StateMigration();
        Preselect();
    }

    /// <summary>
    /// Where the window lands, at the sizes people actually have. Every case runs the same pure
    /// function the app calls, with a fake screen list.
    /// </summary>
    private static void WindowClamp()
    {
        var hd = new ScreenArea(0, 0, 1920, 1080);            // 1080p, 100%
        var qhd = new ScreenArea(0, 0, 2560, 1440);           // 1440p, 100%
        var uhd = new ScreenArea(0, 0, 3840, 2160);           // 4K, 100%
        ScreenArea[] one1440 = [qhd];
        ScreenArea[] one4k = [uhd];
        ScreenArea[] one1080 = [hd];

        // A frame that already fits is left exactly alone: the restore must not "helpfully" move a
        // window the user placed.
        Expect("geometry 1440p frame on a 1440p screen keeps its frame",
            WindowGeometry.ClampToScreens(new WindowFrame(100, 80, 1280, 860), one1440),
            new WindowFrame(100, 80, 1280, 860));

        // The same frame on a 1080p screen: still fully visible, position kept.
        Expect("geometry 1440p frame on a 1080p screen stays visible and keeps position",
            WindowGeometry.ClampToScreens(new WindowFrame(100, 80, 1280, 860), one1080),
            new WindowFrame(100, 80, 1280, 860));

        // A 4K frame at full height, restored on a 1080p screen: taller than the screen, so it is
        // shrunk rather than left with its title bar above the top edge.
        Expect("geometry 4K-sized frame on a 1080p screen shrinks to the screen",
            WindowGeometry.ClampToScreens(new WindowFrame(0, 0, 3840, 2160), one1080),
            new WindowFrame(0, 0, 1920, 1080));

        // 4K desktop, 4K frame: untouched.
        Expect("geometry 4K frame on a 4K screen keeps its frame",
            WindowGeometry.ClampToScreens(new WindowFrame(200, 150, 2400, 1500), one4k),
            new WindowFrame(200, 150, 2400, 1500));

        // Half past the right edge: pulled back inside, size kept.
        Expect("geometry frame hanging off the right edge is pulled inside",
            WindowGeometry.ClampToScreens(new WindowFrame(1800, 200, 1200, 800), one1080),
            new WindowFrame(720, 200, 1200, 800));

        // Dragged up under the title bar: the title bar comes back into reach.
        Expect("geometry frame above the top edge is pulled down",
            WindowGeometry.ClampToScreens(new WindowFrame(-200, -50, 1200, 800), one1080),
            new WindowFrame(0, 0, 1200, 800));

        // The case this feature exists for: the frame was on a 4K monitor to the right that is now
        // unplugged. It overlaps nothing, so it is centred on the primary screen at the same size.
        Expect("geometry frame from a disconnected monitor is centred on the primary",
            WindowGeometry.ClampToScreens(new WindowFrame(4040, 300, 1600, 900), one1080),
            new WindowFrame(160, 90, 1600, 900));

        // Two screens, frame from a third that is gone: primary wins over "the biggest screen".
        ScreenArea[] twoScreens = [hd, new ScreenArea(1920, 0, 2560, 1440)];
        Expect("geometry frame from a third monitor is centred on the primary, not the secondary",
            WindowGeometry.ClampToScreens(new WindowFrame(4480, 0, 1000, 700), twoScreens),
            new WindowFrame(460, 190, 1000, 700));

        // A frame on the secondary screen is kept on the secondary screen.
        Expect("geometry frame on the secondary screen stays there",
            WindowGeometry.ClampToScreens(new WindowFrame(2200, 100, 1200, 800), twoScreens),
            new WindowFrame(2200, 100, 1200, 800));

        // Smaller than the window's minimum: grown to the minimum, not restored unusable.
        Expect("geometry frame below the minimum size grows to the minimum",
            WindowGeometry.ClampToScreens(new WindowFrame(100, 100, 300, 200), one1440, 820, 520),
            new WindowFrame(100, 100, 820, 520));

        // A screen smaller than the minimum: the screen wins, so the window cannot be wider than
        // the display it has to fit on.
        Expect("geometry minimum larger than the screen yields the screen",
            WindowGeometry.ClampToScreens(new WindowFrame(0, 0, 300, 200), [new ScreenArea(0, 0, 640, 480)], 820, 520),
            new WindowFrame(0, 0, 640, 480));

        // No screens reported: nothing to reason from, so the saved frame passes through unchanged
        // instead of being invented into the top-left corner.
        Expect("geometry with no screens reports the frame unchanged",
            WindowGeometry.ClampToScreens(new WindowFrame(100, 80, 1280, 860), []),
            new WindowFrame(100, 80, 1280, 860));

        // A broken frame (NaN from a hand-edited file) is not a position.
        WindowFrame nan = WindowGeometry.ClampToScreens(new WindowFrame(double.NaN, 0, 800, 600), one1080);
        Check("geometry NaN frame is refused", !nan.IsUsable, "usable=" + nan.IsUsable);
    }

    /// <summary>
    /// A v1 state file - written by every build before this change - must load with everything it
    /// had, gain a window section on the next save, and lose nothing on the way.
    /// </summary>
    private static void StateMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scrappy-verify-ui-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");

        try
        {
            // Exactly the shape version 1 wrote: no "window" property at all.
            const string v1 = """
                {
                  "version": 1,
                  "address": "192.168.1.23:27183",
                  "lastDeviceName": "Xiaomi Pad 8",
                  "lastDeviceAddress": "192.168.1.23:27183",
                  "options": { "max_size": "2560", "max_fps": "60", "video_bit_rate": "12000000" },
                  "rawArguments": "--no-audio",
                  "audioEnabled": false,
                  "audioVolume": 55,
                  "audioDevice": "CABLE Input",
                  "savedUtc": "2026-01-02T03:04:05Z"
                }
                """;
            File.WriteAllText(path, v1);

            var store = new AppStateStore(path);
            LoadedState loaded = store.Load();

            Check("migration v1 file is not treated as fresh", !loaded.IsFresh);
            Check("migration v1 file reports no problem", loaded.Problem is null, loaded.Problem ?? "");
            Check("migration v1 address survives", loaded.State.Address == "192.168.1.23:27183", loaded.State.Address);
            Check("migration v1 device name survives", loaded.State.LastDeviceName == "Xiaomi Pad 8", loaded.State.LastDeviceName ?? "null");
            Check("migration v1 device address survives", loaded.State.LastDeviceAddress == "192.168.1.23:27183", loaded.State.LastDeviceAddress ?? "null");
            Check("migration v1 options survive", loaded.State.Options.Count == 3 && loaded.State.Options["max_size"] == "2560");
            Check("migration v1 raw arguments survive", loaded.State.RawArguments == "--no-audio", loaded.State.RawArguments);
            Check("migration v1 audio flag survives", !loaded.State.AudioEnabled);
            Check("migration v1 audio volume survives", loaded.State.AudioVolume == 55, loaded.State.AudioVolume.ToString());
            Check("migration v1 audio device survives", loaded.State.AudioDevice == "CABLE Input", loaded.State.AudioDevice);
            Check("migration v1 has no remembered window", loaded.State.Window is null);

            // The new section is optional: a build without it must still be able to read the file it
            // wrote, so the version number does not move.
            Check("migration version stays 1", loaded.State.Version == 1, loaded.State.Version.ToString());

            var settings = new SettingsStore(ScrcpySchema.LoadEmbedded());
            settings.ApplyProfileValues(loaded.State.Options);
            settings.RawArguments = loaded.State.RawArguments;

            using (var autosave = new AppStateAutosave(store, settings, TimeSpan.Zero))
            {
                autosave.Restore(loaded);

                // First run of the new build: the user moves the window, so it gets remembered.
                autosave.Window = new WindowPlacement { Left = 120, Top = 90, Width = 1440, Height = 900, Maximised = false };
                autosave.Flush();
            }

            LoadedState again = new AppStateStore(path).Load();
            Check("migration v1 fields survive a save by the new build",
                again.State.Address == "192.168.1.23:27183" &&
                again.State.LastDeviceName == "Xiaomi Pad 8" &&
                again.State.Options.Count == 3 &&
                again.State.RawArguments == "--no-audio" &&
                again.State.AudioVolume == 55 &&
                again.State.AudioDevice == "CABLE Input" &&
                !again.State.AudioEnabled);
            Check("migration window section round-trips",
                again.State.Window is { } w && w.Left == 120 && w.Top == 90 && w.Width == 1440 && w.Height == 900 && !w.Maximised);

            // The file on disk now has the section, and still says version 1.
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Check("migration saved file carries the window section",
                document.RootElement.TryGetProperty("window", out _));
            Check("migration saved file is still version 1",
                document.RootElement.GetProperty("version").GetInt32() == 1);

            // A maximised flag is remembered with the frame it was maximised from.
            var maximised = new AppStateFile
            {
                Window = new WindowPlacement { Left = 40, Top = 30, Width = 1280, Height = 800, Maximised = true },
            };
            new AppStateStore(path).Save(maximised);
            LoadedState restored = new AppStateStore(path).Load();
            Check("migration maximised flag survives",
                restored.State.Window is { Maximised: true, Width: 1280 });

            // A hand-edited, impossible frame is dropped on load instead of becoming a 1x1 window.
            File.WriteAllText(path, """{ "version": 1, "window": { "left": 0, "top": 0, "width": 0, "height": 0 } }""");
            Check("migration zero-size frame is dropped",
                new AppStateStore(path).Load().State.Window is null);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp directory, best effort.
            }
        }
    }

    /// <summary>The picker's after-a-scan selection, every branch of it.</summary>
    private static void Preselect()
    {
        DiscoveredDevice wifi = Device("Xiaomi Pad 8", "192.168.1.23", "wifi");
        DiscoveredDevice wired = Device("Xiaomi Pad 8", "192.168.42.129", "wired");
        DiscoveredDevice other = Device("Pixel Tablet", "192.168.1.40", "wifi");

        DevicePreselect.Choice remembered = DevicePreselect.Choose([other, wifi], previousKey: null, lastUsedAddress: "192.168.1.23:27183");
        Check("preselect the remembered tablet is selected", remembered.Device?.Key == wifi.Key, remembered.Reason.ToString());
        Check("preselect remembered reports its reason", remembered.Reason == DevicePreselect.Reason.LastUsed, remembered.Reason.ToString());

        // A rescan must not steal a selection the user just made.
        DevicePreselect.Choice kept = DevicePreselect.Choose([other, wifi], previousKey: other.Key, lastUsedAddress: wifi.Key);
        Check("preselect an in-session selection beats the remembered one", kept.Device?.Key == other.Key, kept.Reason.ToString());

        DevicePreselect.Choice single = DevicePreselect.Choose([other], previousKey: null, lastUsedAddress: null);
        Check("preselect a single answer is selected", single.Device?.Key == other.Key && single.Reason == DevicePreselect.Reason.OnlyDevice, single.Reason.ToString());

        DevicePreselect.Choice cable = DevicePreselect.Choose([wifi, other, wired], previousKey: null, lastUsedAddress: null);
        Check("preselect the one wired tablet wins among several", cable.Device?.Key == wired.Key, cable.Reason.ToString());

        // The remembered tablet is gone: fall back silently, do not invent an address.
        DevicePreselect.Choice absent = DevicePreselect.Choose([other], previousKey: null, lastUsedAddress: "10.0.0.9:27183");
        Check("preselect a missing remembered tablet falls back to the single answer", absent.Device?.Key == other.Key, absent.Reason.ToString());

        DevicePreselect.Choice none = DevicePreselect.Choose([wifi, other], previousKey: null, lastUsedAddress: "10.0.0.9:27183");
        Check("preselect nothing selected when two answers and no memory", none.Device is null && none.Reason == DevicePreselect.Reason.None, none.Reason.ToString());

        DevicePreselect.Choice empty = DevicePreselect.Choose([], previousKey: wired.Key, lastUsedAddress: wifi.Key);
        Check("preselect an empty scan selects nothing", empty.Device is null && empty.Reason == DevicePreselect.Reason.None, empty.Reason.ToString());
    }

    public static DiscoveredDevice Device(string name, string address, string transport) => new()
    {
        Address = address,
        Name = name,
        Model = "25091RP04C",
        Phase = "Waiting for the PC",
        Width = 3200,
        Height = 2136,
        RefreshHz = 144,
        Version = "0.2.1",
        Source = "broadcast",
        Transport = transport,
    };

    private static void Expect(string name, WindowFrame actual, WindowFrame expected) =>
        Check(
            name,
            Nearly(actual.X, expected.X) && Nearly(actual.Y, expected.Y) &&
            Nearly(actual.Width, expected.Width) && Nearly(actual.Height, expected.Height),
            $"got ({actual.X:0.#},{actual.Y:0.#},{actual.Width:0.#},{actual.Height:0.#}) " +
            $"want ({expected.X:0.#},{expected.Y:0.#},{expected.Width:0.#},{expected.Height:0.#})");

    private static bool Nearly(double a, double b) => Math.Abs(a - b) <= 0.5;

    public static void Check(string name, bool ok, string detail = "")
    {
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
}
