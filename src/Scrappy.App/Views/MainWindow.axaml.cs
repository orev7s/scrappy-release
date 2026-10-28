using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Scrappy.App.ViewModels;
using Scrappy.Discovery;
using Scrappy.Protocol;

namespace Scrappy.App.Views;

public partial class MainWindow : Window
{
    private SyntheticDevice? _syntheticDevice;
    private StreamWindow? _streamWindow;

    public MainWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        VideoSurface.Presenter = ViewModel.Presenter;

        if (Program.Options.DemoDevices.Count > 0)
        {
            ViewModel.SeedDemoDevices(Program.Options.DemoDevices.Select(ParseDemoDevice));
        }

        if (Program.Options.Demo)
        {
            StartSyntheticDevice();
        }

        if (Program.Options.DemoDevices.Count == 0)
        {
            // Automatic, once: the address should already be filled in by the time the window is
            // legible. The picker still takes a manual scan afterwards.
            DispatcherTimer.RunOnce(
                () => _ = ViewModel.ScanOnceAsync(),
                TimeSpan.FromMilliseconds(120));
        }

        if (Program.Options.AutoStart)
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () => await ViewModel.StartAsync());
        }

        if (Program.Options.ScreenshotPath is { } path)
        {
            Window target = this;

            if (Program.Options.ShotSettings)
            {
                var settingsViewModel = new SettingsViewModel(ViewModel.Settings, ViewModel.Profiles, ViewModel.State);
                if (Program.Options.ShotFilter is { } filter)
                {
                    settingsViewModel.Search = filter;
                }

                var settingsWindow = new SettingsWindow
                {
                    DataContext = settingsViewModel,
                };

                settingsWindow.Show(this);
                target = settingsWindow;
            }

            CaptureAfterDelay(target, path, Program.Options.ScreenshotDelayMs);
        }
    }

    /// <summary>
    /// Runs a synthetic tablet on loopback so the app can be exercised with no device attached.
    /// Same wire format, same encoder path, same everything on this side of the socket.
    /// </summary>
    private void StartSyntheticDevice()
    {
        string fixture = Path.Combine(FindRepositoryRoot(), "testdata", "pattern-960x640.h264");
        bool hasFixture = File.Exists(fixture);

        _syntheticDevice = new SyntheticDevice(new SyntheticDeviceOptions
        {
            Port = 27183,
            BindAddress = "127.0.0.1",
            Width = 960,
            Height = 640,
            Frames = 0,
            Fps = 60,
            Source = hasFixture ? SyntheticSource.Bitstream : SyntheticSource.Filler,
            BitstreamPath = hasFixture ? fixture : null,
        });

        _syntheticDevice.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                await _syntheticDevice.ServeOnceAsync();
            }
            catch (Exception)
            {
                // Demo only: the session ending is not an application error.
            }
        });
    }

    private void CaptureAfterDelay(Window window, string path, int delayMs)
    {
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var size = new PixelSize(
                    Math.Max(1, (int)window.Bounds.Width),
                    Math.Max(1, (int)window.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(window);

                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bitmap.Save(path);
                Console.WriteLine($"screenshot written: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"screenshot failed: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }, TimeSpan.FromMilliseconds(delayMs));
    }

    private async void OnStartStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.ToggleAsync();
    }

    /// <summary>
    /// Opens the capture target and moves the frame pipeline into it. Two surfaces cannot share
    /// one decoded frame, so the preview steps aside while the stream window is open.
    /// </summary>
    private void OnOpenStreamWindow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (_streamWindow is not null)
        {
            _streamWindow.Activate();
            return;
        }

        VideoSurface.Presenter = null;

        _streamWindow = new StreamWindow();
        _streamWindow.Attach(ViewModel.Presenter);
        _streamWindow.Closed += (_, _) =>
        {
            _streamWindow?.Detach();
            _streamWindow = null;
            VideoSurface.Presenter = ViewModel.Presenter;
        };

        _streamWindow.Show(this);
    }

    /// <summary>Scans again on demand. Safe to press twice: a running scan is not restarted.</summary>
    private async void OnScan(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ScanAsync();
        }
    }

    /// <summary>
    /// <c>--demo-device "Xiaomi Pad 8|192.168.42.129|25091RP04C|wired"</c>. The last field is
    /// optional; without it the address decides, the same way the real link map does.
    /// </summary>
    private static Scrappy.Discovery.DiscoveredDevice ParseDemoDevice(string spec)
    {
        string[] parts = spec.Split('|');
        string name = parts.Length > 0 ? parts[0] : "";
        string address = parts.Length > 1 ? parts[1] : "192.168.1.23";
        string model = parts.Length > 2 ? parts[2] : "";
        string transport = parts.Length > 3
            ? parts[3]
            : address.StartsWith("192.168.42.", StringComparison.Ordinal) ? "wired" : "wifi";

        return new Scrappy.Discovery.DiscoveredDevice
        {
            Address = address,
            Name = name,
            Model = model,
            Phase = "Waiting for the PC",
            Width = 3200,
            Height = 2136,
            RefreshHz = 144,
            Version = "0.2.0",
            Source = "broadcast",
            Transport = transport,
        };
    }

    private async void OnCheckTablet(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // async void: an exception here is unhandled on the UI thread and closes the app, so the
        // button is not allowed to be the thing that ends a session. The view model reports
        // failures in its own status line; this is the last line of defence.
        try
        {
            if (ViewModel is not null)
            {
                await ViewModel.CheckDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel?.Report($"Check tablet failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnApplyRecommended(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ViewModel?.ApplyRecommendation();

    private void OnApplyPreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ViewModel?.ApplyQualityPreset();

    private void OnOpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(ViewModel.Settings, ViewModel.Profiles, ViewModel.State),
        };

        window.ShowDialog(this);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel?.Dispose();
        _syntheticDevice?.DisposeAsync().AsTask().Wait(300);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            if (Directory.Exists(Path.Combine(directory, "testdata")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }
}
