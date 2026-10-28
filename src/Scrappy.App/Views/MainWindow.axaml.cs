using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Scrappy.App.ViewModels;
using Scrappy.Discovery;
using Scrappy.Settings;
using Scrappy.Protocol;

namespace Scrappy.App.Views;

public partial class MainWindow : Window
{
    private SyntheticDevice? _syntheticDevice;
    private StreamWindow? _streamWindow;

    /// <summary>
    /// The last frame the window had while it was not maximised. A maximised window's own frame is
    /// the whole screen, so remembering that instead would leave a maximised-shaped window behind
    /// when the next launch is not maximised.
    /// </summary>
    private WindowFrame? _lastNormalFrame;

    public MainWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closing += OnClosing;

        // The data context is assigned before the window is shown, which is the only moment a
        // geometry restore can happen without the user seeing the default size for a frame.
        DataContextChanged += OnDataContextChanged;
        PositionChanged += (_, _) => RememberNormalFrame();
        PropertyChanged += OnWindowPropertyChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Puts the window back where it was, as soon as there is a view model to ask - and before
    /// anything is drawn. The clamp keeps a frame from an unplugged monitor visible.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel?.State?.Window is not { } saved)
        {
            return;
        }

        WindowFrame frame = WindowGeometry.Apply(this, saved);
        if (frame.IsUsable)
        {
            _lastNormalFrame = frame;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty)
        {
            RememberNormalFrame();
        }
    }

    private void RememberNormalFrame()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        WindowFrame frame = WindowGeometry.CurrentFrame(this);
        if (frame.IsUsable)
        {
            _lastNormalFrame = frame;
        }
    }

    /// <summary>
    /// Records the frame to restore next launch. Cheap: the store debounces the write, and the last
    /// flush happens in <see cref="MainViewModel.Dispose"/> a few lines later.
    /// </summary>
    private void RememberGeometry()
    {
        if (ViewModel?.State is not { } state)
        {
            return;
        }

        WindowPlacement placement = WindowGeometry.Capture(this, _lastNormalFrame);
        if (placement.IsUsable)
        {
            state.Window = placement;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        VideoSurface.Presenter = ViewModel.Presenter;

        // Set before the first layout pass, so a capture at this size shows the real thing.
        ApplyRequestedSize(this);

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

                ApplyRequestedSize(settingsWindow);
                settingsWindow.Show(this);
                target = settingsWindow;
            }

            CaptureAfterDelay(target, path, Program.Options.ScreenshotDelayMs);
        }
    }

    /// <summary>
    /// Starts a window at the size given on the command line, so a screenshot can show the layout at
    /// the minimum size instead of only at the default one.
    /// </summary>
    private static void ApplyRequestedSize(Window window)
    {
        if (Program.Options.WindowSize is not { } requested)
        {
            return;
        }

        string[] parts = requested.Split('x');
        if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
        {
            window.Width = Math.Max(window.MinWidth, w);
            window.Height = Math.Max(window.MinHeight, h);
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
            Version = "0.2.1",
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
        // Before Dispose: Dispose flushes the store, and the frame has to be in it by then.
        RememberGeometry();
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
