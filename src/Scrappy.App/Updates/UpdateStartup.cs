using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Scrappy.App.Updates;

/// <summary>
/// Wires the feature to the real world and gets out of the way.
///
/// The check starts only after the main window is open, on a background thread, after a short pause
/// so it never competes with the first paint or the first discovery probe. Nothing about it can
/// delay the window, and nothing it does can throw into the UI thread.
/// </summary>
public static class UpdateStartup
{
    /// <summary>Long enough that the window is up and the first scan has been sent.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1200);

    /// <summary>Upper bound for the whole check, network included. Past this, the launch wins.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    public static void Start(Window owner, Action<string>? log = null)
    {
        try
        {
            void Kick() => _ = RunAsync(owner, log);

            if (owner.IsVisible)
            {
                // Already on screen (a re-attach, or a test harness): no event to wait for.
                Dispatcher.UIThread.Post(Kick, DispatcherPriority.Background);
            }
            else
            {
                owner.Opened += (_, _) => Dispatcher.UIThread.Post(Kick, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"update: could not start the check ({ex.GetType().Name})");
        }
    }

    private static async Task RunAsync(Window owner, Action<string>? log)
    {
        try
        {
            await Task.Delay(Delay).ConfigureAwait(false);

            using var budget = new CancellationTokenSource(Budget);
            using var feed = new GitHubReleaseFeed();
            var notifier = new UpdateNotifier(
                feed,
                new UpdateStateStore(UpdateStateStore.DefaultPath()),
                new AvaloniaUpdateUi(() => owner),
                new BrowserLauncher(),
                AppVersion.Current,
                log ?? (line => Trace.WriteLine($"update: {line}")));

            await notifier.CheckAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A fire-and-forget task must never surface as an unobserved exception.
            Trace.WriteLine($"update: check abandoned ({ex.GetType().Name})");
        }
    }
}
