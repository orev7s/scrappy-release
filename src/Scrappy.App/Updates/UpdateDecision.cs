namespace Scrappy.App.Updates;

/// <summary>
/// What the version comparison said. <see cref="ShouldPrompt"/> is the only thing the caller acts
/// on; <see cref="Reason"/> exists so a log line, a test failure and a review can all name the
/// branch that was taken.
/// </summary>
public sealed record UpdateVerdict(bool ShouldPrompt, string Reason);

/// <summary>
/// The rule that decides whether to show the dialog. Pure: running version, the release the feed
/// returned, and the version the user suppressed. Nothing here reads a file, a clock or the network,
/// which is why the interesting case (dismissed 0.2.1, feed says 0.2.2) is a one-line test.
/// </summary>
public static class UpdateDecision
{
    /// <summary>No release could be read. Silence.</summary>
    public const string ReasonFeedUnavailable = "feed-unavailable";

    /// <summary>The feed offered a prerelease. Silence.</summary>
    public const string ReasonPrerelease = "prerelease";

    /// <summary>The newest release is this build or older. Silence.</summary>
    public const string ReasonUpToDate = "up-to-date";

    /// <summary>The user said "Don't show again" for this version or a newer one. Silence.</summary>
    public const string ReasonDismissed = "dismissed";

    /// <summary>A release newer than both this build and the dismissed version. Show the dialog.</summary>
    public const string ReasonNewer = "newer";

    public static UpdateVerdict Decide(AppVersion running, UpdateInfo? latest, AppVersion? dismissed)
    {
        if (latest is null)
        {
            return new UpdateVerdict(false, ReasonFeedUnavailable);
        }

        if (latest.Version.IsPrerelease)
        {
            return new UpdateVerdict(false, ReasonPrerelease);
        }

        if (latest.Version.CompareTo(running) <= 0)
        {
            return new UpdateVerdict(false, ReasonUpToDate);
        }

        // The point of storing a version rather than a flag: suppressing 0.2.1 must not silence
        // 0.2.2. Comparing the two versions is the whole rule.
        if (dismissed is not null && latest.Version.CompareTo(dismissed) <= 0)
        {
            return new UpdateVerdict(false, ReasonDismissed);
        }

        return new UpdateVerdict(true, ReasonNewer);
    }
}
