using System.Net.Http.Headers;
using System.Text.Json;

namespace Scrappy.App.Updates;

/// <summary>
/// Where the newest release comes from. An interface, so the decision and the dialog can be tested
/// with no network and no clock: the real implementation is the only thing that talks to GitHub.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>The newest public release, or <c>null</c> if it cannot be read for any reason.</summary>
    Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>/repos/orev7s/scrappy-release/releases/latest</c>.
///
/// Every failure mode is <c>null</c>, not an exception: offline, DNS, 404, 403 rate limit, a
/// captive-portal HTML page, truncated JSON, a tag that is not a version, a draft or a prerelease.
/// The update check is a courtesy; it is never allowed to be the reason a launch goes wrong.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    public const string Repository = "orev7s/scrappy-release";

    public const string LatestReleaseApi = "https://api.github.com/repos/orev7s/scrappy-release/releases/latest";

    /// <summary>The page a user downloads from. The app opens this; it never fetches the zip.</summary>
    public const string ReleasePageFormat = "https://github.com/orev7s/scrappy-release/releases/tag/{0}";

    /// <summary>Short on purpose: a slow answer is the same as no answer for a startup check.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubReleaseFeed(HttpClient? client = null, TimeSpan? timeout = null)
    {
        Timeout = timeout ?? DefaultTimeout;
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = Timeout };
        _client.Timeout = Timeout;
    }

    public TimeSpan Timeout { get; }

    public async Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);

            // GitHub rejects a request without a User-Agent outright, and the JSON media type keeps
            // the shape stable if the default ever changes.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Scrappy", AppVersion.Current.Text));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Timeout);

            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return ParseLatest(body);
        }
        catch (Exception)
        {
            // Deliberately silent. The caller logs one line; the user sees nothing.
            return null;
        }
    }

    /// <summary>
    /// Turns a GitHub release payload into <see cref="UpdateInfo"/>, or <c>null</c> when the payload
    /// is not a usable release. Pure and total: no throwing, no network, no dependency on time.
    /// </summary>
    public static UpdateInfo? ParseLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // An error page, a proxy block page or a rate-limit notice is not JSON at all. Checking the
        // first character saves a parse and a stack of exception handling on the common failure.
        if (json.TrimStart()[0] != '{')
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (ReadBool(root, "draft") || ReadBool(root, "prerelease"))
            {
                // A prerelease must never be offered to somebody running a stable build.
                return null;
            }

            string? tag = ReadString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            AppVersion? version = AppVersion.TryParse(tag);
            if (version is null)
            {
                return null;
            }

            string name = ReadString(root, "name") is { Length: > 0 } declared ? declared : tag;
            string notes = ReadString(root, "body") ?? "";

            DateTimeOffset? published = null;
            if (DateTimeOffset.TryParse(
                    ReadString(root, "published_at"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed) &&
                parsed > DateTimeOffset.UnixEpoch)
            {
                published = parsed;
            }

            return new UpdateInfo(version, tag, name, published, notes, ReleasePage(root, tag));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The page to open. GitHub's own <c>html_url</c> wins when it points at this repository over
    /// https; otherwise the tag is escaped into the canonical address rather than trusted.
    /// </summary>
    private static string ReleasePage(JsonElement root, string tag)
    {
        string? html = ReadString(root, "html_url");
        if (html is not null &&
            Uri.TryCreate(html, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/releases/tag/", StringComparison.Ordinal))
        {
            return html;
        }

        return string.Format(ReleasePageFormat, Uri.EscapeDataString(tag));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
