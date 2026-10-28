using Scrappy.App.Updates;

namespace Scrappy.Verify.Updates;

/// <summary>
/// Every check the auto-update feature needs, with no network, no display server and no waiting.
/// The order is deliberate: the version rule, then the payload, then the decision, then the file,
/// then the dialog seam - each layer is only believed once the one under it is proven.
/// </summary>
public static class UpdateChecks
{
    private const string Version = "VERSION";
    private const string Feed = "FEED";
    private const string Reject = "REJECT";
    private const string Decide = "DECISION";
    private const string State = "STATE";
    private const string Dialog = "DIALOG";
    private const string Offline = "OFFLINE";
    private const string Flow = "FLOW";

    public static async Task RunAsync(Report report)
    {
        Versions(report);
        Payload(report);
        BadPayloads(report);
        Decisions(report);
        Store(report);
        await DialogSeamAsync(report);
        await OfflineAsync(report);
        await RequiredScenarioAsync(report);
    }

    // ---------------------------------------------------------------- versions

    private static void Versions(Report r)
    {
        r.Check(Version, "0.2.1 > 0.2.0", Greater("0.2.1", "0.2.0"));
        r.Check(Version, "0.10.0 > 0.9.9 (numeric, not lexical)", Greater("0.10.0", "0.9.9"));
        r.Check(Version, "1.0.0 > 0.99.99", Greater("1.0.0", "0.99.99"));
        r.Check(Version, "0.2.1 > 0.2.0.9 (padded with zeros)", Greater("0.2.1", "0.2.0.9"));
        r.Check(Version, "0.2.1 == 0.2.1", Same("0.2.1", "0.2.1"));
        r.Check(Version, "0.2.1 == v0.2.1", Same("0.2.1", "v0.2.1"));
        r.Check(Version, "0.2.1 == 0.2.1+abc (build metadata ignored)", Same("0.2.1", "0.2.1+abc"));
        r.Check(Version, "1 == 1.0.0 (1 to 4 components)", Same("1", "1.0.0"));
        r.Check(Version, "1.2.3.4 parses", AppVersion.TryParse("1.2.3.4") is not null);
        r.Check(Version, "' 0.2.1 ' parses", AppVersion.TryParse(" 0.2.1 ") is not null);
        r.Check(Version, "0.2.2-beta.1 < 0.2.2", Greater("0.2.2", "0.2.2-beta.1"));
        r.Check(Version, "0.2.2-beta.1 is flagged as a prerelease", AppVersion.TryParse("0.2.2-beta.1")?.IsPrerelease == true);
        r.Check(Version, "v0.2.1 is not a prerelease", AppVersion.TryParse("v0.2.1")?.IsPrerelease == false);
        r.Check(Version, "v0.2.1 displays as 0.2.1", AppVersion.TryParse("v0.2.1")?.Display == "0.2.1");
        r.Check(Version, "0.2.1+abc displays as 0.2.1", AppVersion.TryParse("0.2.1+abc")?.Display == "0.2.1");

        r.Check(Version, "empty is not a version", AppVersion.TryParse("") is null);
        r.Check(Version, "whitespace is not a version", AppVersion.TryParse("   ") is null);
        r.Check(Version, "null is not a version", AppVersion.TryParse(null) is null);
        r.Check(Version, "words are not a version", AppVersion.TryParse("latest") is null);
        r.Check(Version, "a bare v is not a version", AppVersion.TryParse("v") is null);
        r.Check(Version, "0..1 is not a version", AppVersion.TryParse("0..1") is null);
        r.Check(Version, "0.2.x is not a version", AppVersion.TryParse("0.2.x") is null);
        r.Check(Version, "1.0- is not a version", AppVersion.TryParse("1.0-") is null);
        r.Check(Version, "five components are not a version", AppVersion.TryParse("1.2.3.4.5") is null);
        r.Check(Version, "-1.0 is not a version", AppVersion.TryParse("-1.0") is null);
        r.Check(Version, "a 10-digit component is not a version", AppVersion.TryParse("1.1234567890") is null);

        // The assembly is the single source: this fails if the csproj <Version> and the code drift.
        r.Check(Version, "the running version is 0.2.1", AppVersion.Current.Text == "0.2.1", AppVersion.Current.Display);
        r.Check(Version, "the running version is a release", !AppVersion.Current.IsPrerelease);
    }

    // ---------------------------------------------------------------- payload

    private static void Payload(Report r)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "release-latest.json");
        string? json = File.Exists(path) ? File.ReadAllText(path) : null;
        r.Check(Feed, "the real GitHub payload fixture is next to the tool", json is not null, path);

        if (json is null)
        {
            return;
        }

        UpdateInfo? info = GitHubReleaseFeed.ParseLatest(json);
        r.Check(Feed, "the real payload parses", info is not null);
        if (info is null)
        {
            return;
        }

        r.Unchanged(Feed, "tag", "v0.2.1", info.Tag);
        r.Unchanged(Feed, "version", "0.2.1", info.Version.Text);
        r.Unchanged(Feed, "name", "Scrappy 0.2.1", info.Name);
        r.Unchanged(
            Feed,
            "release page url",
            "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.1",
            info.Url);
        r.Check(Feed, "notes are the release body", info.Notes.Contains("the settings panel now scrolls", StringComparison.Ordinal));
        r.Check(Feed, "notes excerpt is capped", info.NotesExcerpt.Length <= UpdateInfo.ExcerptLength + 3, info.NotesExcerpt.Length.ToString());
        r.Check(Feed, "published date is read", info.PublishedUtc is not null && info.PublishedUtc.Value.Year == 2026, info.PublishedUtc?.ToString("u"));

        // Notes are plain text. A release body full of markup must reach the dialog as characters,
        // never as something a text control would interpret.
        UpdateInfo? markup = GitHubReleaseFeed.ParseLatest("""
            {"tag_name":"v0.3.0","name":"Scrappy 0.3.0","body":"<b>bold</b> & <script>alert(1)</script>"}
            """);
        r.Unchanged(Feed, "markup in the notes stays literal", "<b>bold</b> & <script>alert(1)</script>", markup?.Notes);

        UpdateInfo? nameless = GitHubReleaseFeed.ParseLatest("""{"tag_name":"v0.3.0"}""");
        r.Unchanged(Feed, "a release without a name falls back to the tag", "v0.3.0", nameless?.Name);
        r.Unchanged(Feed, "a missing body is an empty excerpt", "", nameless?.NotesExcerpt);

        // html_url is only trusted when it is this repository over https.
        UpdateInfo? hostile = GitHubReleaseFeed.ParseLatest("""
            {"tag_name":"v0.3.0","html_url":"https://example.invalid/releases/tag/v0.3.0"}
            """);
        r.Unchanged(
            Feed,
            "a foreign html_url is replaced with the canonical page",
            "https://github.com/orev7s/scrappy-release/releases/tag/v0.3.0",
            hostile?.Url);

        r.Unchanged(Feed, "a short note is not truncated", "Fixes.", UpdateInfo.Excerpt("Fixes."));
        string long_notes = string.Join(' ', Enumerable.Repeat("word", 400));
        string excerpt = UpdateInfo.Excerpt(long_notes);
        r.Check(Feed, "a long note is truncated", excerpt.Length <= UpdateInfo.ExcerptLength + 3, excerpt.Length.ToString());
        r.Check(Feed, "a truncated note says so", excerpt.EndsWith("...", StringComparison.Ordinal));
    }

    private static void BadPayloads(Report r)
    {
        var cases = new (string Name, string? Json)[]
        {
            ("empty body", ""),
            ("whitespace body", "   \r\n"),
            ("null body", null),
            ("html error page", "<!DOCTYPE html><html><body>404 Not Found</body></html>"),
            ("rate limit body", """{"message":"API rate limit exceeded for 92.1.2.3.","documentation_url":"https://docs.github.com/rest"}"""),
            ("not found body", """{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}"""),
            ("truncated json", """{"tag_name":"v0.2.2","name":"Scra"""),
            ("json array", "[]"),
            ("empty object", "{}"),
            ("a tag that is not a version", """{"tag_name":"nightly-2026-10-28"}"""),
            ("a tag that is only v", """{"tag_name":"v"}"""),
            ("a numeric tag_name", """{"tag_name":5}"""),
            ("a draft", """{"tag_name":"v0.9.0","draft":true}"""),
            ("a prerelease", """{"tag_name":"v0.9.0","prerelease":true}"""),
        };

        foreach ((string name, string? json) in cases)
        {
            UpdateInfo? parsed = null;
            bool threw = false;
            try
            {
                parsed = GitHubReleaseFeed.ParseLatest(json);
            }
            catch (Exception)
            {
                threw = true;
            }

            r.Check(Reject, $"{name} reads as no release, without throwing", parsed is null && !threw);
        }
    }

    // ---------------------------------------------------------------- decision

    private static void Decisions(Report r)
    {
        AppVersion running = AppVersion.TryParse("0.2.1")!;

        UpdateVerdict newer = UpdateDecision.Decide(running, Release("v0.2.2"), null);
        r.Check(Decide, "a newer release prompts", newer.ShouldPrompt);
        r.Unchanged(Decide, "a newer release says why", UpdateDecision.ReasonNewer, newer.Reason);

        UpdateInfo? newest = Release("v0.2.2");
        r.Unchanged(
            Decide,
            "the prompt carries the release page",
            "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.2",
            newest.Url);

        r.Check(Decide, "the same version is silent", !UpdateDecision.Decide(running, Release("v0.2.1"), null).ShouldPrompt);
        r.Check(Decide, "an older release is silent", !UpdateDecision.Decide(running, Release("v0.2.0"), null).ShouldPrompt);
        r.Check(Decide, "no release at all is silent", !UpdateDecision.Decide(running, null, null).ShouldPrompt);
        r.Unchanged(Decide, "no release says why", UpdateDecision.ReasonFeedUnavailable, UpdateDecision.Decide(running, null, null).Reason);
        r.Check(
            Decide,
            "a prerelease never prompts a stable build",
            !UpdateDecision.Decide(running, Release("v0.3.0-beta.1"), null).ShouldPrompt);

        // The required scenario, as a rule: 0.2.1 suppressed, 0.2.2 shipped.
        AppVersion dismissed021 = AppVersion.TryParse("0.2.1")!;
        r.Check(Decide, "dismissed 0.2.1 + feed 0.2.2 -> prompt", UpdateDecision.Decide(running, Release("v0.2.2"), dismissed021).ShouldPrompt);
        r.Check(
            Decide,
            "dismissed 0.2.2 + feed 0.2.2 -> silent",
            !UpdateDecision.Decide(running, Release("v0.2.2"), AppVersion.TryParse("0.2.2")).ShouldPrompt);
        r.Check(
            Decide,
            "dismissed 0.2.10 + feed 0.2.9 -> silent (numeric)",
            !UpdateDecision.Decide(AppVersion.TryParse("0.2.8")!, Release("v0.2.9"), AppVersion.TryParse("0.2.10")).ShouldPrompt);
        r.Check(
            Decide,
            "dismissed 0.2.9 + feed 0.2.10 -> prompt (numeric)",
            UpdateDecision.Decide(AppVersion.TryParse("0.2.8")!, Release("v0.2.10"), AppVersion.TryParse("0.2.9")).ShouldPrompt);

        // running 0.2.0 so the feed is genuinely newer than the build; the dismissal is what silences it.
        UpdateVerdict olderThanDismissal = UpdateDecision.Decide(
            AppVersion.TryParse("0.2.0")!,
            Release("v0.2.1"),
            AppVersion.TryParse("0.2.2"));
        r.Check(Decide, "dismissed 0.2.2 + feed 0.2.1 -> silent", !olderThanDismissal.ShouldPrompt);
        r.Unchanged(Decide, "a dismissed release says why", UpdateDecision.ReasonDismissed, olderThanDismissal.Reason);

        r.Unchanged(
            Decide,
            "an up-to-date build says why",
            UpdateDecision.ReasonUpToDate,
            UpdateDecision.Decide(running, Release("v0.2.1"), null).Reason);
        r.Unchanged(
            Decide,
            "a prerelease says why",
            UpdateDecision.ReasonPrerelease,
            UpdateDecision.Decide(running, Release("v0.2.3-beta.2"), null).Reason);
    }

    // ---------------------------------------------------------------- state file

    private static void Store(Report r)
    {
        string missing = Path.Combine(FreshDirectory(), "update-state.json");
        r.Check(State, "a missing file means nothing is suppressed", new UpdateStateStore(missing).LoadDismissed() is null);

        string corrupt = Path.Combine(FreshDirectory(), "update-state.json");
        File.WriteAllText(corrupt, "{ this is not json");
        AppVersion? fromCorrupt = null;
        bool threw = false;
        try
        {
            fromCorrupt = new UpdateStateStore(corrupt).LoadDismissed();
        }
        catch (Exception)
        {
            threw = true;
        }

        r.Check(State, "a corrupt file is empty, not fatal", fromCorrupt is null && !threw);

        string empty = Path.Combine(FreshDirectory(), "update-state.json");
        File.WriteAllText(empty, "");
        r.Check(State, "an empty file means nothing is suppressed", new UpdateStateStore(empty).LoadDismissed() is null);

        string blank = Path.Combine(FreshDirectory(), "update-state.json");
        File.WriteAllText(blank, """{"version":1,"dismissedVersion":null}""");
        r.Check(State, "a null dismissedVersion means nothing is suppressed", new UpdateStateStore(blank).LoadDismissed() is null);

        string junk = Path.Combine(FreshDirectory(), "update-state.json");
        File.WriteAllText(junk, """{"version":1,"dismissedVersion":"later"}""");
        r.Check(State, "a dismissedVersion that is not a version is ignored", new UpdateStateStore(junk).LoadDismissed() is null);

        string roundTripPath = Path.Combine(FreshDirectory(), "update-state.json");
        var store = new UpdateStateStore(roundTripPath);
        AppVersion target = AppVersion.TryParse("v0.2.1")!;
        r.Check(State, "dismissing an existing file's version succeeds", store.Dismiss(target));
        r.Unchanged(State, "the dismissal round-trips", "0.2.1", new UpdateStateStore(roundTripPath).LoadDismissed()?.Text);
        r.Check(State, "the file is written where it says", File.Exists(roundTripPath));

        string json = File.ReadAllText(roundTripPath);
        r.Check(State, "the file is valid json with a version", json.Contains("\"version\": 1", StringComparison.Ordinal) || json.Contains("\"version\":1", StringComparison.Ordinal), json);
        r.Check(State, "the file records the version as text", json.Contains("0.2.1", StringComparison.Ordinal));

        // Atomic overwrite: the second dismissal replaces the first, and leaves no temporary behind.
        r.Check(State, "a second dismissal overwrites the first", store.Dismiss(AppVersion.TryParse("0.2.2")!));
        r.Unchanged(State, "the newest dismissal wins", "0.2.2", store.LoadDismissed()?.Text);
        r.Check(State, "no temporary file is left behind", !File.Exists(roundTripPath + ".tmp"));

        string nested = Path.Combine(FreshDirectory(), "one", "two", "update-state.json");
        r.Check(State, "a missing directory is created", new UpdateStateStore(nested).Dismiss(AppVersion.TryParse("0.4.0")!));
        r.Unchanged(State, "the nested file round-trips", "0.4.0", new UpdateStateStore(nested).LoadDismissed()?.Text);
    }

    // ---------------------------------------------------------------- dialog seam

    private static async Task DialogSeamAsync(Report r)
    {
        const string expected = "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.2";

        // Update: the launcher gets the exact release page, and nothing is remembered.
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var ui = new FakeUi(UpdateChoice.Update);
            var launcher = new FakeLauncher();
            var notifier = new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), ui, launcher, AppVersion.TryParse("0.2.1")!);

            UpdateOutcome outcome = await notifier.CheckAsync();
            r.Check(Dialog, "Update is offered the dialog", ui.Shows == 1);
            r.Unchanged(Dialog, "the dialog is about the new version", "0.2.2", ui.ShownAbout?.Version.Text);
            r.Unchanged(Dialog, "the dialog knows what is running", "0.2.1", ui.ShownRunning?.Text);
            r.Unchanged(Dialog, "Update opens the release page", expected, launcher.Opened.FirstOrDefault());
            r.Check(Dialog, "Update opens exactly one page", launcher.Opened.Count == 1, launcher.Opened.Count.ToString());
            r.Check(Dialog, "Update reports a launch", outcome.Launched && outcome.Error is null);
            r.Check(Dialog, "Update remembers nothing", !File.Exists(path));
        }

        // Skip update: nothing persisted, so the next launch asks again.
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var ui = new FakeUi(UpdateChoice.Skip);
            var launcher = new FakeLauncher();
            var notifier = new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), ui, launcher, AppVersion.TryParse("0.2.1")!);

            await notifier.CheckAsync();
            r.Check(Dialog, "Skip update persists nothing", !File.Exists(path));
            r.Check(Dialog, "Skip update opens no browser", launcher.Opened.Count == 0);
            r.Check(Dialog, "Skip update leaves nothing suppressed", new UpdateStateStore(path).LoadDismissed() is null);
        }

        // Don't show again: this version, and only this version.
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var ui = new FakeUi(UpdateChoice.DontShowAgain);
            var launcher = new FakeLauncher();
            var notifier = new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), ui, launcher, AppVersion.TryParse("0.2.1")!);

            await notifier.CheckAsync();
            r.Check(Dialog, "Don't show again writes the version", File.Exists(path));
            r.Unchanged(Dialog, "Don't show again suppresses that version", "0.2.2", new UpdateStateStore(path).LoadDismissed()?.Text);
            r.Check(Dialog, "Don't show again opens no browser", launcher.Opened.Count == 0);
        }

        // No browser registered: the URL is still offered, and nothing throws.
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var ui = new FakeUi(UpdateChoice.Update);
            var launcher = new FakeLauncher(works: false);
            var notifier = new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), ui, launcher, AppVersion.TryParse("0.2.1")!);

            UpdateOutcome outcome = await notifier.CheckAsync();
            r.Check(Dialog, "a browser that cannot start does not throw", !outcome.Launched && outcome.Error is not null);
            r.Unchanged(Dialog, "the failure names the release page", expected, ui.LaunchFailures.FirstOrDefault()?.Split('|')[0]);
            r.Check(Dialog, "the failure is reported once", ui.LaunchFailures.Count == 1, ui.LaunchFailures.Count.ToString());
        }

        // A dialog that itself fails must not take the caller down.
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var notifier = new UpdateNotifier(
                new StubFeed(Release("v0.2.2")),
                new UpdateStateStore(path),
                new ThrowingUi(),
                new FakeLauncher(),
                AppVersion.TryParse("0.2.1")!);

            UpdateOutcome outcome = await notifier.CheckAsync();
            r.Check(Dialog, "a dialog that throws is caught", outcome.Prompted && !outcome.Launched && outcome.Error is not null);
            r.Check(Dialog, "a dialog that throws persists nothing", !File.Exists(path));
        }
    }

    // ---------------------------------------------------------------- offline

    private static async Task OfflineAsync(Report r)
    {
        {
            string path = Path.Combine(FreshDirectory(), "update-state.json");
            var ui = new FakeUi(UpdateChoice.Update);
            var feed = new ThrowingFeed();
            var notifier = new UpdateNotifier(feed, new UpdateStateStore(path), ui, new FakeLauncher(), AppVersion.TryParse("0.2.1")!);

            UpdateOutcome outcome = await notifier.CheckAsync();
            r.Check(Offline, "a throwing feed is quiet", outcome.IsQuiet);
            r.Check(Offline, "a throwing feed shows no dialog", ui.Shows == 0);
            r.Unchanged(Offline, "a throwing feed says why", UpdateDecision.ReasonFeedUnavailable, outcome.Reason);
            r.Check(Offline, "a throwing feed writes no file", !File.Exists(path));
            r.Check(Offline, "the feed was actually asked", feed.Calls == 1);
        }

        {
            var ui = new FakeUi(UpdateChoice.Update);
            var notifier = new UpdateNotifier(new StubFeed(null), new UpdateStateStore(Path.Combine(FreshDirectory(), "update-state.json")), ui, new FakeLauncher(), AppVersion.TryParse("0.2.1")!);

            UpdateOutcome outcome = await notifier.CheckAsync();
            r.Check(Offline, "a null release is quiet", outcome.IsQuiet && ui.Shows == 0);
        }

        {
            // A request that hangs: the budget ends it, and the check still returns instead of
            // leaving a task (and a socket) alive for the rest of the session.
            var ui = new FakeUi(UpdateChoice.Update);
            var notifier = new UpdateNotifier(new HangingFeed(), new UpdateStateStore(Path.Combine(FreshDirectory(), "update-state.json")), ui, new FakeLauncher(), AppVersion.TryParse("0.2.1")!);

            using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            DateTime started = DateTime.UtcNow;
            UpdateOutcome outcome = await notifier.CheckAsync(budget.Token);
            TimeSpan elapsed = DateTime.UtcNow - started;

            r.Check(Offline, "a hanging feed gives up", outcome.IsQuiet && ui.Shows == 0);
            r.Check(Offline, "a hanging feed gives up quickly", elapsed < TimeSpan.FromSeconds(5), $"{elapsed.TotalMilliseconds:F0} ms");
        }
    }

    // ---------------------------------------------------------------- the required scenario

    private static async Task RequiredScenarioAsync(Report r)
    {
        // "Don't show again" for 0.2.1, then 0.2.2 ships. This is the behaviour the feature exists
        // for, so it is exercised through the real file, the real rule and the real notifier.
        string path = Path.Combine(FreshDirectory(), "update-state.json");
        AppVersion running = AppVersion.TryParse("0.2.1")!;

        var first = new FakeUi(UpdateChoice.DontShowAgain);
        await new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), first, new FakeLauncher(), running).CheckAsync();
        r.Check(Flow, "dismiss v0.2.2 once", first.Shows == 1);

        var second = new FakeUi(UpdateChoice.Update);
        UpdateOutcome silent = await new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(path), second, new FakeLauncher(), running).CheckAsync();
        r.Check(Flow, "the same release does not ask again", second.Shows == 0);
        r.Unchanged(Flow, "the silence is the dismissal", UpdateDecision.ReasonDismissed, silent.Reason);

        var third = new FakeUi(UpdateChoice.Update);
        var thirdLauncher = new FakeLauncher();
        UpdateOutcome prompted = await new UpdateNotifier(new StubFeed(Release("v0.2.3")), new UpdateStateStore(path), third, thirdLauncher, running).CheckAsync();
        r.Check(Flow, "a newer release asks again", third.Shows == 1 && prompted.Prompted);
        r.Unchanged(
            Flow,
            "and points at the new release page",
            "https://github.com/orev7s/scrappy-release/releases/tag/v0.2.3",
            thirdLauncher.Opened.FirstOrDefault());

        // Skip is not sticky: two launches in a row both prompt.
        string skipPath = Path.Combine(FreshDirectory(), "update-state.json");
        var skipFirst = new FakeUi(UpdateChoice.Skip);
        await new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(skipPath), skipFirst, new FakeLauncher(), running).CheckAsync();
        var skipSecond = new FakeUi(UpdateChoice.Skip);
        await new UpdateNotifier(new StubFeed(Release("v0.2.2")), new UpdateStateStore(skipPath), skipSecond, new FakeLauncher(), running).CheckAsync();
        r.Check(Flow, "Skip update asks again on the next launch", skipFirst.Shows == 1 && skipSecond.Shows == 1);
        r.Check(Flow, "Skip update wrote nothing", !File.Exists(skipPath));
    }

    // ---------------------------------------------------------------- helpers

    private static UpdateInfo Release(string tag)
    {
        AppVersion version = AppVersion.TryParse(tag)!;
        return new UpdateInfo(
            version,
            tag,
            $"Scrappy {version.Text}",
            DateTimeOffset.UnixEpoch,
            $"Release notes for {version.Text}.",
            string.Format(GitHubReleaseFeed.ReleasePageFormat, tag));
    }

    private static bool Greater(string left, string right) =>
        AppVersion.TryParse(left) is { } l && AppVersion.TryParse(right) is { } r && l.CompareTo(r) > 0;

    private static bool Same(string left, string right) =>
        AppVersion.TryParse(left) is { } l && AppVersion.TryParse(right) is { } r && l.CompareTo(r) == 0;

    private static string FreshDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "scrappy-verify-updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
