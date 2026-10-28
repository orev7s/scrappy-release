using System.Diagnostics;

namespace Scrappy.App.Updates;

/// <summary>The three answers the dialog offers.</summary>
public enum UpdateChoice
{
    /// <summary>Open the release page, then close.</summary>
    Update,

    /// <summary>Close and forget: the dialog is offered again next launch.</summary>
    Skip,

    /// <summary>Close and remember this version, until a newer one appears.</summary>
    DontShowAgain,
}

/// <summary>
/// The dialog, behind a seam. The real implementation is an Avalonia window; the tests use a fake
/// that records the answer, so every branch of <see cref="UpdateNotifier"/> runs with no display.
/// </summary>
public interface IUpdateUi
{
    Task<UpdateChoice> ShowAsync(UpdateInfo latest, AppVersion running, CancellationToken cancellationToken);

    /// <summary>No browser was registered. Say so where the user already is, not in a log file.</summary>
    void ReportLaunchFailure(string releaseUrl, string message);
}

/// <summary>Opens a URL in whatever the user has registered for it. Behind a seam for the same reason.</summary>
public interface ILauncher
{
    bool TryOpen(string url, out string? error);
}

/// <summary>What one check did. Returned rather than logged so a test can assert on it.</summary>
public sealed record UpdateOutcome(
    string Reason,
    bool Prompted,
    UpdateChoice? Choice,
    bool Launched,
    string? Error)
{
    public bool IsQuiet => !Prompted;
}

/// <summary>
/// The whole feature, with no Avalonia, no socket and no file system of its own: fetch, decide,
/// ask, act. Every dependency is injected, which is what makes the startup path testable.
///
/// Nothing in here throws outward. A check that fails is a check that did not happen.
/// </summary>
public sealed class UpdateNotifier
{
    private readonly IReleaseFeed _feed;
    private readonly UpdateStateStore _store;
    private readonly IUpdateUi _ui;
    private readonly ILauncher _launcher;
    private readonly AppVersion _running;
    private readonly Action<string>? _log;

    public UpdateNotifier(
        IReleaseFeed feed,
        UpdateStateStore store,
        IUpdateUi ui,
        ILauncher launcher,
        AppVersion running,
        Action<string>? log = null)
    {
        _feed = feed;
        _store = store;
        _ui = ui;
        _launcher = launcher;
        _running = running;
        _log = log;
    }

    public async Task<UpdateOutcome> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateInfo? latest;
        try
        {
            latest = await _feed.FetchLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The feed is supposed to return null rather than throw, but a seam is a seam.
            Log($"feed threw {ex.GetType().Name}; staying quiet");
            return new UpdateOutcome(UpdateDecision.ReasonFeedUnavailable, false, null, false, null);
        }

        AppVersion? dismissed = _store.LoadDismissed();
        UpdateVerdict verdict = UpdateDecision.Decide(_running, latest, dismissed);
        Log($"running {_running.Text}; decision {verdict.Reason}");

        if (!verdict.ShouldPrompt || latest is null)
        {
            return new UpdateOutcome(verdict.Reason, false, null, false, null);
        }

        UpdateChoice choice;
        try
        {
            choice = await _ui.ShowAsync(latest, _running, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"dialog failed: {ex.GetType().Name}");
            return new UpdateOutcome(verdict.Reason, true, null, false, ex.Message);
        }

        switch (choice)
        {
            case UpdateChoice.DontShowAgain:
                // Remember the version, not the fact that the dialog was dismissed: 0.2.2 must
                // still be offered after 0.2.1 is suppressed.
                _store.Dismiss(latest.Version);
                Log($"suppressed {latest.Version.Text}");
                return new UpdateOutcome(verdict.Reason, true, choice, false, null);

            case UpdateChoice.Update:
                if (_launcher.TryOpen(latest.Url, out string? error))
                {
                    Log($"opened {latest.Url}");
                    return new UpdateOutcome(verdict.Reason, true, choice, true, null);
                }

                Log($"could not open a browser: {error}");
                _ui.ReportLaunchFailure(latest.Url, error ?? "no browser was registered");
                return new UpdateOutcome(verdict.Reason, true, choice, false, error ?? "no browser was registered");

            default:
                // Skip: nothing is written, so the same dialog is offered on the next launch.
                Log("skipped");
                return new UpdateOutcome(verdict.Reason, true, choice, false, null);
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}
