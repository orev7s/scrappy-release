namespace Scrappy.Update;

/// <summary>What the user pressed, and nothing about what happens next.</summary>
public enum UpdateDecision
{
    /// <summary>Open the release page. The user downloads; this program does not.</summary>
    Update,

    /// <summary>Dismiss for now. A later launch may ask again.</summary>
    Skip,

    /// <summary>Never ask about this version again. A newer version is a different question.</summary>
    DontShowAgain,
}

/// <summary>What came out of a check.</summary>
public enum UpdateStatus
{
    /// <summary>A newer release exists and has not been dismissed. There is something to show.</summary>
    Available,

    /// <summary>The feed answered and the running build is already current.</summary>
    UpToDate,

    /// <summary>A newer release exists, but this user said not to ask about it.</summary>
    Suppressed,

    /// <summary>
    /// Nothing to say: the network was unreachable, the API refused, the repository has no
    /// releases, or the newest tag is not a version. Silent by design.
    /// </summary>
    Unknown,
}

/// <summary>A newer release, and the wording the dialog shows.</summary>
public sealed record UpdateOffer
{
    /// <summary>The question, verbatim: the user asked for these words and not a paraphrase.</summary>
    public const string Question = "There is an update. Do you want to update?";

    public required SemanticVersion Current { get; init; }

    public required SemanticVersion Latest { get; init; }

    public required ReleaseInfo Release { get; init; }

    /// <summary>The release page the Update button opens.</summary>
    public string Url => Release.Url;

    /// <summary>One line naming both numbers, so the user can see what they are being offered.</summary>
    public string Detail =>
        $"{Release.DisplayName} is available. You have {Current}.";

    /// <summary>
    /// Where the download happens. Stated on the dialog because "update" usually means "this
    /// program is about to replace itself", and it is not: it opens a page and stops.
    /// </summary>
    public string Note =>
        "Update opens the release page on GitHub in your browser. Nothing is downloaded or installed for you.";
}

/// <summary>The result of one check.</summary>
public sealed record UpdateCheck(UpdateStatus Status, UpdateOffer? Offer)
{
    public bool IsAvailable => Status == UpdateStatus.Available && Offer is not null;

    public static UpdateCheck Nothing(UpdateStatus status = UpdateStatus.Unknown) => new(status, null);
}

/// <summary>What a decision turned into.</summary>
public sealed record UpdateChoice(UpdateDecision Decision, string? Url)
{
    /// <summary>True for the one decision that opens a browser.</summary>
    public bool OpensBrowser => Decision == UpdateDecision.Update && !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// Decides whether there is anything to ask.
///
/// Comparison, suppression and the outcome of a button all live here, so the only thing the
/// interface has to do is show the question and report which button was pressed. This is also
/// the whole of the feature that can be tested without a window.
/// </summary>
public sealed class UpdateChecker
{
    private readonly IReleaseFeed _feed;
    private readonly UpdateSuppression _suppression;

    public UpdateChecker(IReleaseFeed feed, UpdateSuppression suppression, SemanticVersion? current = null)
    {
        _feed = feed;
        _suppression = suppression;
        Current = current ?? AppVersion.Current;
    }

    /// <summary>The running version, which every comparison is made against.</summary>
    public SemanticVersion Current { get; }

    /// <summary>Never throws and never blocks for longer than the feed allows.</summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        ReleaseInfo? release;
        try
        {
            release = await _feed.TryLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A feed is contractually silent, but a check that threw would take start-up with it.
            return UpdateCheck.Nothing();
        }

        if (release?.Version is not { } latest)
        {
            // No release at all, or a tag nobody can read as a version.
            return UpdateCheck.Nothing();
        }

        if (latest <= Current)
        {
            return UpdateCheck.Nothing(UpdateStatus.UpToDate);
        }

        if (_suppression.IsSuppressed(latest))
        {
            return UpdateCheck.Nothing(UpdateStatus.Suppressed);
        }

        return new UpdateCheck(UpdateStatus.Available, new UpdateOffer
        {
            Current = Current,
            Latest = latest,
            Release = release,
        });
    }

    /// <summary>
    /// Applies what the user pressed. Suppression is written here, so a version is only ever
    /// remembered as dismissed by the button that says so - pressing Skip never silences anything.
    /// </summary>
    public UpdateChoice Choose(UpdateOffer offer, UpdateDecision decision)
    {
        if (decision == UpdateDecision.DontShowAgain)
        {
            _suppression.Suppress(offer.Latest);
        }

        return new UpdateChoice(decision, decision == UpdateDecision.Update ? offer.Url : null);
    }
}
