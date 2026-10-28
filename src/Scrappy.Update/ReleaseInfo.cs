namespace Scrappy.Update;

/// <summary>
/// One published release: what the API called it and where a user goes to download it.
///
/// <see cref="Url"/> is the release page on github.com. Nothing in this project downloads it: the
/// update is the user's own act, in their own browser, from the page GitHub already renders.
/// </summary>
public sealed record ReleaseInfo
{
    /// <summary>The git tag, verbatim, e.g. <c>v0.2.1</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>The release title, e.g. <c>Scrappy 0.2.1</c>. Empty when the API gave none.</summary>
    public string Name { get; init; } = "";

    /// <summary>The release page. Empty when the tag came from the tag list, which has no page.</summary>
    public string Url { get; init; } = "";

    public bool IsPreRelease { get; init; }

    public bool IsDraft { get; init; }

    /// <summary>The tag read as a version, or null when the tag is not one.</summary>
    public SemanticVersion? Version { get; init; }

    /// <summary>The title when there is one, otherwise the tag: what a dialog can show.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Tag : Name;

    public override string ToString() => $"{Tag} -> {Url}";
}
