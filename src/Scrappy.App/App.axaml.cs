using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Scrappy.App.ViewModels;
using Scrappy.App.Views;
using Scrappy.Settings;

namespace Scrappy.App;

public sealed class ScrappyApp : Application
{
    private AppStateAutosave? _autosave;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // One settings store for the whole app: the panel beside the session and the full
            // settings list are two views of the same object, so they cannot disagree.
            ScrcpySchema schema = ScrcpySchema.LoadEmbedded();
            var settings = new SettingsStore(schema);
            var profiles = new ProfileStore(ProfileStore.DefaultPath());

            // Restore before anything reads the store. Defaults are for the first run only -
            // overwriting a saved configuration on every launch is the bug this replaces.
            var stateStore = new AppStateStore(AppStateStore.DefaultPath());
            LoadedState loaded = stateStore.Load();

            if (loaded.IsFresh)
            {
                ApplyDefaults(settings);
            }
            else
            {
                settings.ApplyProfileValues(loaded.State.Options);
                settings.RawArguments = loaded.State.RawArguments;
            }

            _autosave = new AppStateAutosave(stateStore, settings);
            _autosave.Restore(loaded);

            // A development switch overrides the remembered address; everything else is restored.
            if (Program.Options.Address is { Length: > 0 } commandLineAddress)
            {
                _autosave.Address = commandLineAddress;
            }

            if (loaded.IsFresh)
            {
                // Write the file immediately, so the next launch is an upgrade rather than a
                // "fresh install" that resets to defaults again.
                _autosave.Flush();
            }

            var viewModel = new MainViewModel(settings, profiles, _autosave)
            {
                Address = _autosave.Address,
            };

            if (loaded.Problem is { } problem && !loaded.IsFresh)
            {
                viewModel.Report($"Settings could not be restored - {problem}. Starting from defaults; the file is rewritten as you change things.");
            }

            var window = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.MainWindow = window;

            // The update check, on its own task, once the window is up. It never blocks this
            // method and never throws: a network that is down produces no dialog, not a delay.
            window.Opened += (_, _) => Scrappy.App.Update.UpdatePrompter.CheckOnStartup(window);

            // The last write must not depend on the user closing the window politely: a log-off or
            // a shut-down also has to land the current state on disk.
            desktop.Exit += (_, _) => _autosave?.Dispose();
            window.Closed += (_, _) => _autosave?.Flush();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The starting point for a new install: 2560x1708, 60 fps, HEVC at 12 Mbps. Every one of these
    /// is an ordinary schema option, so the settings list shows exactly what is set. Applied once,
    /// on a machine that has no state file yet, and saved so it never has to be applied again.
    /// </summary>
    private static void ApplyDefaults(SettingsStore settings)
    {
        settings.SetValue("max_size", "2560");
        settings.SetValue("max_fps", "60");
        settings.SetValue("video_bit_rate", "12000000");
        settings.SetValue("video_codec", "h265");
    }
}
