// Update notifier checks: the three things that have to be true for a dialog that appears once
// per version and never at the wrong time.
//
//   version      newer, equal, older, and what a pre-release tag ranks as
//   suppression  "don't show again" silences the version it was pressed on, and only that one
//   failure      a release API that is down, slow, or lying is silence, never an exception
//
// Exit code 0 means every check passed.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Scrappy.Update;

internal static class UpdateChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();
        int checks = 0;

        void Check(bool condition, string what)
        {
            checks++;
            if (!condition)
            {
                failures.Add(what);
            }
        }

        string directory = Path.Combine(Path.GetTempPath(), "scrappy-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            // --- version: newer, equal, older ------------------------------------------------
            // The whole feature rests on this comparison being right, so it is written out case by
            // case rather than asserted through the checker.
            (string left, string right, int expected)[] ordered =
            [
                ("0.3.0", "0.2.1", 1),
                ("0.2.1", "0.3.0", -1),
                ("0.2.1", "0.2.1", 0),
                ("1.0.0", "0.9.9", 1),
                ("0.2.2", "0.2.1", 1),
                ("0.2.1", "0.2.2", -1),

                // A "v" prefix, which every tag in this repository carries.
                ("v0.3.0", "0.3.0", 0),
                ("v0.3.0", "v0.2.1", 1),

                // Build metadata ranks nowhere: it is the same release.
                ("0.3.0+build.7", "0.3.0", 0),

                // Four fields, for a build that numbers a revision.
                ("0.2.1.4", "0.2.1", 1),
                ("0.2.1", "0.2.1.4", -1),

                // The case a string comparison gets wrong.
                ("1.0.10", "1.0.9", 1),
                ("1.0.9", "1.0.10", -1),
            ];

            foreach ((string left, string right, int expected) in ordered)
            {
                SemanticVersion? a = SemanticVersion.Parse(left);
                SemanticVersion? b = SemanticVersion.Parse(right);
                int actual = a is null || b is null ? 99 : a.CompareTo(b);
                Check(actual == expected, $"{left} vs {right}: compared as {actual}, expected {expected}");
            }

            // --- version: pre-releases -------------------------------------------------------
            (string left, string right, int expected)[] preRelease =
            [
                // A release outranks its own pre-releases.
                ("1.0.0", "1.0.0-rc.1", 1),
                ("1.0.0-rc.1", "1.0.0", -1),

                // Same numbers, different pre-release: alphabetical, field by field.
                ("1.0.0-alpha", "1.0.0-beta", -1),
                ("1.0.0-beta", "1.0.0-alpha", 1),

                // Numeric identifiers compare as numbers, so rc.10 is after rc.2.
                ("1.0.0-rc.2", "1.0.0-rc.10", -1),
                ("1.0.0-rc.10", "1.0.0-rc.2", 1),

                // A numeric identifier ranks below an alphanumeric one.
                ("1.0.0-1", "1.0.0-alpha", -1),

                // Fewer fields, all else equal, is the earlier one.
                ("1.0.0-rc.1", "1.0.0-rc.1.1", -1),

                // A pre-release of a newer version still beats an older release outright.
                ("1.1.0-rc.1", "1.0.9", 1),
            ];

            foreach ((string left, string right, int expected) in preRelease)
            {
                SemanticVersion? a = SemanticVersion.Parse(left);
                SemanticVersion? b = SemanticVersion.Parse(right);
                int actual = a is null || b is null ? 99 : a.CompareTo(b);
                Check(actual == expected, $"{left} vs {right}: compared as {actual}, expected {expected}");
            }

            Check(SemanticVersion.Parse("1.0.0-rc.1")!.IsPreRelease, "1.0.0-rc.1 was not marked as a pre-release");
            Check(!SemanticVersion.Parse("1.0.0")!.IsPreRelease, "1.0.0 was marked as a pre-release");

            // A tag that is not a version is ignored, not guessed at.
            foreach (string junk in new[] { "", "   ", "nightly", "release-2026", "1.0.0.0.0", "v", "-1.0.0", "1..0" })
            {
                Check(SemanticVersion.Parse(junk) is null, $"'{junk}' was read as a version");
            }

            Check(SemanticVersion.Parse(null) is null, "null was read as a version");

            // Normalised text: the same version from two tags must produce the same key, because
            // that key is what "don't show again" remembers.
            Check(SemanticVersion.Parse("v0.3.0")!.ToString() == SemanticVersion.Parse("0.3.0+build.9")!.ToString(),
                "v0.3.0 and 0.3.0+build.9 did not normalise to the same text");

            // --- suppression: one version, not a flag ----------------------------------------
            string statePath = Path.Combine(directory, "update.json");
            var suppression = UpdateSuppression.Open(statePath);
            var offered = SemanticVersion.Parse("0.3.0")!;

            Check(!suppression.IsSuppressed(offered), "a version was already suppressed on a fresh file");

            suppression.Suppress(offered);
            Check(suppression.IsSuppressed("0.3.0"), "0.3.0 was not suppressed after being dismissed");
            Check(suppression.IsSuppressed("v0.3.0"), "v0.3.0 was not recognised as the suppressed 0.3.0");
            Check(!suppression.IsSuppressed("0.3.1"), "a newer version was suppressed by an older dismissal");
            Check(!suppression.IsSuppressed("0.2.9"), "an older version was suppressed by a newer dismissal");
            Check(!suppression.IsSuppressed("0.4.0"), "a much newer version was suppressed");

            // It has to survive a restart, or "permanently" means nothing.
            var reopened = UpdateSuppression.Open(statePath);
            Check(reopened.IsSuppressed("0.3.0"), "the dismissal did not survive a reload");
            Check(!reopened.IsSuppressed("0.3.1"), "a newer version was suppressed after a reload");

            // A damaged file is not a crash and not a decision.
            File.WriteAllText(statePath, "{ not json");
            var damaged = UpdateSuppression.Open(statePath);
            Check(!damaged.IsSuppressed("0.3.0"), "a damaged file suppressed something");
            damaged.Suppress(offered);
            Check(UpdateSuppression.Open(statePath).IsSuppressed("0.3.0"), "a damaged file was not replaced by a real one");

            // --- suppression through the checker ---------------------------------------------
            // This is the behaviour the user asked for: the same version stays silent, a newer one
            // speaks up again.
            string checkPath = Path.Combine(directory, "checker.json");
            var feed = new StubFeed("v0.3.0", "https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0");
            var checker = new UpdateChecker(feed, UpdateSuppression.Open(checkPath), SemanticVersion.Parse("0.2.1"));

            UpdateCheck first = await checker.CheckAsync();
            Check(first.Status == UpdateStatus.Available, $"a newer release read as {first.Status}, expected Available");
            Check(first.Offer?.Latest.ToString() == "0.3.0", "the offer named the wrong version");

            // Skip: dismisses this dialog and nothing else.
            UpdateChoice skipped = checker.Choose(first.Offer!, UpdateDecision.Skip);
            Check(skipped.Decision == UpdateDecision.Skip, "Skip was not reported as Skip");
            Check(!skipped.OpensBrowser, "Skip opened a browser");
            Check(skipped.Url is null, "Skip produced a URL");

            UpdateCheck afterSkip = await checker.CheckAsync();
            Check(afterSkip.Status == UpdateStatus.Available,
                $"Skip silenced the release: the next check read as {afterSkip.Status}");

            // Don't show again: this version, only.
            UpdateChoice never = checker.Choose(first.Offer!, UpdateDecision.DontShowAgain);
            Check(never.Decision == UpdateDecision.DontShowAgain, "Don't show again was not reported as such");
            Check(!never.OpensBrowser, "Don't show again opened a browser");

            UpdateCheck afterNever = await checker.CheckAsync();
            Check(afterNever.Status == UpdateStatus.Suppressed,
                $"after 'don't show again' the check read as {afterNever.Status}, expected Suppressed");
            Check(afterNever.Offer is null, "a suppressed release still produced an offer");

            // A newer release is a different question, so it is asked.
            feed.Tag = "v0.3.1";
            UpdateCheck newer = await checker.CheckAsync();
            Check(newer.Status == UpdateStatus.Available,
                $"0.3.1 was not offered after 0.3.0 was dismissed: read as {newer.Status}");

            // ...and dismissing that one does not resurrect the first.
            checker.Choose(newer.Offer!, UpdateDecision.DontShowAgain);
            Check((await checker.CheckAsync()).Status == UpdateStatus.Suppressed, "0.3.1 was not suppressed");

            feed.Tag = "v0.4.0";
            Check((await checker.CheckAsync()).Status == UpdateStatus.Available, "0.4.0 was not offered");

            // Update: the one decision that leaves the program, and it only opens a page.
            UpdateChoice update = checker.Choose((await checker.CheckAsync()).Offer!, UpdateDecision.Update);
            Check(update.OpensBrowser, "Update did not open anything");
            Check(update.Url == feed.Url, $"Update opened '{update.Url}' instead of '{feed.Url}'");

            // --- no update is not an update ---------------------------------------------------
            var current = new UpdateChecker(
                new StubFeed("v0.2.1", "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.1"),
                UpdateSuppression.Open(Path.Combine(directory, "same.json")),
                SemanticVersion.Parse("0.2.1"));
            Check((await current.CheckAsync()).Status == UpdateStatus.UpToDate, "the current version was offered as an update");

            var ahead = new UpdateChecker(
                new StubFeed("v0.1.9", ""),
                UpdateSuppression.Open(Path.Combine(directory, "ahead.json")),
                SemanticVersion.Parse("0.2.1"));
            Check((await ahead.CheckAsync()).Status == UpdateStatus.UpToDate, "an older release was offered as an update");

            // A tag nobody can read is not an offer, however new it is.
            var unreadable = new UpdateChecker(
                new StubFeed("nightly", ""),
                UpdateSuppression.Open(Path.Combine(directory, "nightly.json")),
                SemanticVersion.Parse("0.2.1"));
            Check((await unreadable.CheckAsync()).Status == UpdateStatus.Unknown,
                "an unreadable tag was offered as an update");

            // --- failure: nothing reaches the user --------------------------------------------
            // Every one of these is a real thing that happens to a real machine: no route to the
            // host, a proxy that answers HTML, a half-written body, a rate limit, a host that
            // accepts the connection and then says nothing for a minute.
            var failureCases = new (string Name, StubReleaseApi Api, GitHubReleaseFeedOptions Options)[]
            {
                ("refused", StubReleaseApi.Refusing().Start(), Options()),
                ("500", StubReleaseApi.Serving(500, "boom").Start(), Options()),
                ("404", StubReleaseApi.Serving(404, """{"message":"Not Found"}""").Start(), Options()),
                ("html", StubReleaseApi.Serving(200, "<html><body>proxy login</body></html>").Start(), Options()),
                ("broken json", StubReleaseApi.Serving(200, """{"tag_name":"v0.3.0""").Start(), Options()),
                ("empty body", StubReleaseApi.Serving(200, "").Start(), Options()),
                ("no tag", StubReleaseApi.Serving(200, """{"name":"Scrappy"}""").Start(), Options()),
                ("hung", StubReleaseApi.Hanging().Start(), Options(TimeSpan.FromMilliseconds(400))),
            };

            foreach ((string name, StubReleaseApi api, GitHubReleaseFeedOptions options) in failureCases)
            {
                using (api)
                {
                    var silent = new UpdateChecker(
                        new GitHubReleaseFeed(options with { ApiBase = api.BaseAddress, WebBase = api.BaseAddress }),
                        UpdateSuppression.Open(Path.Combine(directory, "failure.json")),
                        SemanticVersion.Parse("0.2.1"));

                    UpdateCheck result;
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        result = await silent.CheckAsync();
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{name}: the check threw {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    clock.Stop();
                    checks++;

                    if (result.Status != UpdateStatus.Unknown || result.Offer is not null)
                    {
                        failures.Add($"{name}: produced {result.Status} instead of staying silent");
                    }

                    // Silence is not enough on its own: a check that hangs for a minute would hold
                    // a socket and a task for the whole session.
                    if (clock.Elapsed > TimeSpan.FromSeconds(3))
                    {
                        failures.Add($"{name}: took {clock.ElapsedMilliseconds} ms to give up");
                    }

                    Console.WriteLine($"  {name,-12} silent in {clock.ElapsedMilliseconds,4} ms");
                }
            }

            // A feed that throws rather than returning null - the case the interface forbids, and
            // the case that would take start-up with it.
            var throwing = new UpdateChecker(
                new ThrowingFeed(),
                UpdateSuppression.Open(Path.Combine(directory, "throwing.json")),
                SemanticVersion.Parse("0.2.1"));

            try
            {
                UpdateCheck result = await throwing.CheckAsync();
                Check(result.Status == UpdateStatus.Unknown, $"a feed that threw produced {result.Status}");
            }
            catch (Exception ex)
            {
                failures.Add($"a feed that threw escaped as {ex.GetType().Name}: {ex.Message}");
            }

            // --- the real client against a stand-in API ---------------------------------------
            // The same code path the app uses, with a body shaped exactly like GitHub's.
            using (StubReleaseApi api = StubReleaseApi.Latest(tag: "v0.3.0", name: "Scrappy 0.3.0").Start())
            {
                var live = new UpdateChecker(
                    new GitHubReleaseFeed(Options() with { ApiBase = api.BaseAddress, WebBase = api.BaseAddress }),
                    UpdateSuppression.Open(Path.Combine(directory, "live.json")),
                    SemanticVersion.Parse("0.2.1"));

                UpdateCheck result = await live.CheckAsync();
                Check(result.Status == UpdateStatus.Available, $"the stand-in API read as {result.Status}");
                Check(result.Offer?.Url == "https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0",
                    $"the offer points at '{result.Offer?.Url}'");
                Check(result.Offer?.Detail == "Scrappy 0.3.0 is available. You have 0.2.1.",
                    $"the dialog would say '{result.Offer?.Detail}'");
                Check(UpdateOffer.Question == "There is an update. Do you want to update?",
                    $"the question reads '{UpdateOffer.Question}'");
            }

            // A pre-release nobody asked for is not offered; the newest published tag is.
            using (StubReleaseApi api = StubReleaseApi.PreRelease().Start())
            {
                var stable = new UpdateChecker(
                    new GitHubReleaseFeed(Options() with { ApiBase = api.BaseAddress, WebBase = api.BaseAddress }),
                    UpdateSuppression.Open(Path.Combine(directory, "prerelease.json")),
                    SemanticVersion.Parse("0.2.1"));

                Check((await stable.CheckAsync()).Status == UpdateStatus.UpToDate,
                    "a pre-release was offered to a user who did not ask for one");

                var willing = new UpdateChecker(
                    new GitHubReleaseFeed(Options() with
                    {
                        ApiBase = api.BaseAddress,
                        WebBase = api.BaseAddress,
                        IncludePreReleases = true,
                    }),
                    UpdateSuppression.Open(Path.Combine(directory, "prerelease2.json")),
                    SemanticVersion.Parse("0.2.1"));

                Check((await willing.CheckAsync()).Status == UpdateStatus.Available,
                    "a pre-release was not offered to a user who asked for one");
            }

            // --- the running version ----------------------------------------------------------
            Check(AppVersion.Current is not null, "the running version could not be read");
            Console.WriteLine($"version          compares against {AppVersion.Current}");
            Console.WriteLine($"versions         {ordered.Length + preRelease.Length} pairs, {checks} assertions");

            if (failures.Count == 0)
            {
                Console.WriteLine("UPDATE PASS");
                Console.WriteLine();
                return 0;
            }

            foreach (string failure in failures.Take(12))
            {
                Console.WriteLine("UPDATE FAIL  " + failure);
            }

            if (failures.Count > 12)
            {
                Console.WriteLine($"UPDATE FAIL  ... and {failures.Count - 12} more");
            }

            Console.WriteLine();
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temporary directory; leaving it behind is not a test failure.
            }
        }
    }

    private static GitHubReleaseFeedOptions Options(TimeSpan? timeout = null) => new()
    {
        Owner = "orev7s",
        Repository = "scrappy-release",
        Timeout = timeout ?? TimeSpan.FromSeconds(6),
    };

    /// <summary>A feed that answers with one release, and can be changed between checks.</summary>
    private sealed class StubFeed : IReleaseFeed
    {
        public StubFeed(string tag, string url)
        {
            Tag = tag;
            Url = url;
        }

        public string Tag { get; set; }

        public string Url { get; set; }

        public Task<ReleaseInfo?> TryLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReleaseInfo?>(new ReleaseInfo
            {
                Tag = Tag,
                Name = "",
                Url = Url,
                Version = SemanticVersion.Parse(Tag),
            });
    }

    /// <summary>
    /// The interface says a feed does not throw. This one does anyway, because the checker is
    /// required to survive a feed that breaks the contract.
    /// </summary>
    private sealed class ThrowingFeed : IReleaseFeed
    {
        public Task<ReleaseInfo?> TryLatestAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the release host answered with something unusable");
    }
}

/// <summary>
/// A one-endpoint stand-in for the GitHub Releases API, so the real client can be driven without
/// the network and without a rate limit.
/// </summary>
internal sealed class StubReleaseApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    private StubReleaseApi()
    {
        int port = FreePort();
        BaseAddress = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{BaseAddress}/");
    }

    public string BaseAddress { get; }

    public int Status { get; init; } = 200;

    /// <summary>Answered for <c>/releases/latest</c>.</summary>
    public string LatestBody { get; init; } = "{}";

    /// <summary>Answered for the tag list, which only a repository without releases needs.</summary>
    public string TagsBody { get; init; } = "[]";

    public TimeSpan Delay { get; init; }

    /// <summary>False for the "nothing is listening" case: a port is reserved and left closed.</summary>
    public bool Listen { get; init; } = true;

    /// <summary>A release page, shaped exactly as GitHub writes it.</summary>
    public static StubReleaseApi Latest(string tag, string name) => new()
    {
        LatestBody = $$"""
            {"tag_name":"{{tag}}","name":"{{name}}","html_url":"https://github.com/orev7s/scrappy-release/releases/tag/{{tag}}","prerelease":false,"draft":false}
            """,
    };

    /// <summary>The newest release is a pre-release, and the newest published tag is older.</summary>
    public static StubReleaseApi PreRelease() => new()
    {
        LatestBody = """
            {"tag_name":"v0.3.0-rc.1","name":"Scrappy 0.3.0 rc1","html_url":"https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0-rc.1","prerelease":true,"draft":false}
            """,
        TagsBody = """[{"name":"v0.2.1"},{"name":"v0.2.0"}]""",
    };

    /// <summary>A status code and a body that is not a release.</summary>
    public static StubReleaseApi Serving(int status, string body) => new()
    {
        Status = status,
        LatestBody = body,
        TagsBody = body,
    };

    /// <summary>
    /// Nothing listening. A closed port is what a machine with no route to the internet, or a
    /// firewall that drops the connection, actually looks like.
    /// </summary>
    public static StubReleaseApi Refusing() => new() { Listen = false };

    /// <summary>A host that accepts the connection and then takes a minute to answer.</summary>
    public static StubReleaseApi Hanging() => new()
    {
        Delay = TimeSpan.FromSeconds(60),
        LatestBody = """{"tag_name":"v0.3.0"}""",
    };

    /// <summary>Starts the listener. Separate from the constructor so the factories can configure.</summary>
    public StubReleaseApi Start()
    {
        if (!Listen)
        {
            return this;
        }

        _listener.Start();
        _ = Task.Run(ServeAsync);
        return this;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(Delay, _stop.Token).ConfigureAwait(false);
                    }

                    string path = context.Request.Url?.AbsolutePath ?? "";
                    string body = path.EndsWith("/tags", StringComparison.Ordinal) ? TagsBody : LatestBody;
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = Status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // The client gave up; that is the point of the hanging case.
                }
            });
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped; nothing to do.
        }

        _stop.Dispose();
    }
}
