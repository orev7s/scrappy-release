using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Scrappy.Update;

/// <summary>Where the release check talks to, and how long it is allowed to take.</summary>
public sealed record GitHubReleaseFeedOptions
{
    public const string DefaultApiBase = "https://api.github.com";
    public const string DefaultWebBase = "https://github.com";

    public required string Owner { get; init; }

    public required string Repository { get; init; }

    /// <summary>
    /// Overridden by tests and by <c>--update-api</c>, so a check can be driven against a local
    /// stand-in instead of GitHub.
    /// </summary>
    public string ApiBase { get; init; } = DefaultApiBase;

    /// <summary>Where a release page lives. Only used to build a URL for a tag that has no page.</summary>
    public string WebBase { get; init; } = DefaultWebBase;

    /// <summary>
    /// How long the whole check may take. Start-up does not wait for this - the check runs on its
    /// own task - but a machine with no route to the internet must not hold a socket open.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Off by default: a pre-release is not something to offer a user who is mirroring a tablet.
    /// When it is off and the newest release is a pre-release, the newest published tag is used
    /// instead, which is the last real release.
    /// </summary>
    public bool IncludePreReleases { get; init; }
}

/// <summary>
/// Reads the newest release from the GitHub Releases API.
///
/// Two requests at most: <c>/releases/latest</c>, and - when there is no published release, or the
/// newest one is a pre-release the user did not ask for - the tag list, which is also a release
/// list for a repository that tags without publishing.
///
/// It never throws. Every way this can fail (no route to the host, a TLS interception, a rate
/// limit, a 404 from a repository with no releases, HTML where JSON was expected, a tag that is not
/// a version) returns null, which the caller reads as "nothing to say".
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    private static readonly HttpClient Shared = CreateClient();

    private readonly GitHubReleaseFeedOptions _options;
    private readonly HttpClient _http;

    public GitHubReleaseFeed(GitHubReleaseFeedOptions options) : this(options, Shared)
    {
    }

    public GitHubReleaseFeed(GitHubReleaseFeedOptions options, HttpClient httpClient)
    {
        _options = options;
        _http = httpClient;
    }

    public async Task<ReleaseInfo?> TryLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        try
        {
            ReleaseInfo? latest = await ReadLatestReleaseAsync(linked.Token).ConfigureAwait(false);

            if (latest is { IsDraft: false } && (_options.IncludePreReleases || !latest.IsPreRelease))
            {
                return latest;
            }

            // No published release, a draft, or a pre-release nobody asked for: the newest tag is
            // the answer a user would recognise, so read it the same careful way.
            return await ReadLatestTagAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or JsonException or InvalidOperationException or UriFormatException
                                       or FormatException or IOException)
        {
            // The whole point of the catch: an update check is a courtesy, and a courtesy that can
            // stop the app from starting is worse than no check.
            return null;
        }
    }

    private async Task<ReleaseInfo?> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage? response = await GetAsync(
            $"repos/{_options.Owner}/{_options.Repository}/releases/latest", cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string tag = Text(root, "tag_name");
        if (tag.Length == 0)
        {
            return null;
        }

        return new ReleaseInfo
        {
            Tag = tag,
            Name = Text(root, "name"),
            Url = Text(root, "html_url"),
            IsPreRelease = Flag(root, "prerelease"),
            IsDraft = Flag(root, "draft"),
            Version = SemanticVersion.Parse(tag),
        };
    }

    private async Task<ReleaseInfo?> ReadLatestTagAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage? response = await GetAsync(
            $"repos/{_options.Owner}/{_options.Repository}/tags?per_page=30", cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The list is not ordered by version - it is ordered by the repository's own history - so
        // the newest tag is whichever tag is the highest version, not whichever comes first.
        ReleaseInfo? newest = null;

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            string tag = Text(element, "name");
            SemanticVersion? version = SemanticVersion.Parse(tag);
            if (version is null)
            {
                continue;
            }

            if (!_options.IncludePreReleases && version.IsPreRelease)
            {
                continue;
            }

            if (newest?.Version is { } held && held >= version)
            {
                continue;
            }

            newest = new ReleaseInfo
            {
                Tag = tag,
                Name = "",
                Url = $"{Trim(_options.WebBase)}/{_options.Owner}/{_options.Repository}/releases/tag/{Uri.EscapeDataString(tag)}",
                IsPreRelease = version.IsPreRelease,
                Version = version,
            };
        }

        return newest;
    }

    /// <summary>Returns null for anything that is not a 200 with a body.</summary>
    private async Task<HttpResponseMessage?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Trim(_options.ApiBase)}/{path}");

        // GitHub rejects a request with no User-Agent, and an empty reply reads as "no update"
        // rather than as "the API refused us", which would be an invisible bug.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Scrappy", AppVersion.Current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // 404 is a repository with no releases; 403 with a zero rate limit left is the same
            // answer for the user. Neither is worth a retry, and neither is worth a message.
            response.Dispose();
            return null;
        }

        return response;
    }

    private static string Trim(string baseAddress) => baseAddress.TrimEnd('/');

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static HttpClient CreateClient() => new()
    {
        // A little longer than the check's own timeout: the token is what ends a slow request, and
        // this is only here so a hang can never outlive the process.
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestVersion = HttpVersion.Version11,
    };
}
