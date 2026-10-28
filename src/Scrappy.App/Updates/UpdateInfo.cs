namespace Scrappy.App.Updates;

/// <summary>
/// One release, as the notifier needs it: what it is called, how new it is, what changed, and the
/// page the user has to visit to get it. The app never downloads the release itself - the URL is
/// the whole payload.
/// </summary>
public sealed record UpdateInfo(
    AppVersion Version,
    string Tag,
    string Name,
    DateTimeOffset? PublishedUtc,
    string Notes,
    string Url)
{
    /// <summary>How much of the release notes the dialog shows before it becomes a wall of text.</summary>
    public const int ExcerptLength = 600;

    /// <summary>Release notes trimmed for the dialog. Plain text; never markup, never a link.</summary>
    public string NotesExcerpt => Excerpt(Notes, ExcerptLength);

    public static string Excerpt(string? notes, int limit = ExcerptLength)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return "";
        }

        string trimmed = notes.Trim();
        if (trimmed.Length <= limit)
        {
            return trimmed;
        }

        // Cut on a word boundary where there is one close by, so the excerpt does not end mid-word.
        int cut = trimmed.LastIndexOf(' ', Math.Min(limit, trimmed.Length - 1));
        if (cut < limit - 80)
        {
            cut = limit;
        }

        return trimmed[..cut].TrimEnd() + "...";
    }
}
