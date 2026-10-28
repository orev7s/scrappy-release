using Scrappy.App.Updates;
using Scrappy.Verify.Updates;

// Headless checks for the auto-update notifier.
//
//   all              every check (default): version rule, payload parsing, decision, state file,
//                    dialog seam, offline behaviour, and the dismiss-then-newer scenario
//   shot [dir]       render the real dialog through Avalonia headless + Skia and save a PNG
//
// No network, no tablet, no display server, no test framework. Exit code 0 means every check passed.

string command = args.Length > 0 ? args[0] : "all";
string root = FindRepositoryRoot();

return command switch
{
    "all" => await AllAsync(),
    "live" => await LiveAsync(),
    "shot" => Screenshots.Capture(args.Length > 1 ? args[1] : Path.Combine(root, "out")),
    _ => Usage(),
};

async Task<int> AllAsync()
{
    var report = new Report();
    await UpdateChecks.RunAsync(report);

    Console.WriteLine();
    if (report.FailureCount == 0)
    {
        Console.WriteLine($"UPDATES PASS {report.Count} checks");
        return 0;
    }

    foreach (string failure in report.Failures)
    {
        Console.WriteLine($"UPDATES FAIL {failure}");
    }

    Console.WriteLine($"UPDATES FAIL {report.FailureCount} of {report.Count} checks");
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        Scrappy.Verify.Updates

          all           every check below (default)
          live          one real request to the GitHub API, then the decision for this build
          shot [dir]    render the update dialog to PNG, into dir (default: out/)

        The checks use no network: version ordering and parsing, GitHub payload parsing and every
        bad payload, the prompt decision (including dismissed-then-newer), the state file, the
        dialog seam, and offline behaviour. `live` is the only command that touches the network.
        """);
    return 2;
}

// The only command that uses the network: a real request through the real HttpClient, so the
// headers, the timeout and the parser are exercised against the live API at least once.
async Task<int> LiveAsync()
{
    DateTime started = DateTime.UtcNow;
    using var feed = new GitHubReleaseFeed();
    UpdateInfo? info = await feed.FetchLatestAsync(CancellationToken.None);
    TimeSpan elapsed = DateTime.UtcNow - started;

    if (info is null)
    {
        Console.WriteLine("LIVE FAIL the API returned nothing usable (offline, or the payload changed)");
        return 1;
    }

    string published = info.PublishedUtc is { } when ? when.ToString("u") : "unknown";
    Console.WriteLine($"LIVE tag        {info.Tag}");
    Console.WriteLine($"LIVE version    {info.Version.Display}");
    Console.WriteLine($"LIVE name       {info.Name}");
    Console.WriteLine($"LIVE published  {published}");
    Console.WriteLine($"LIVE url        {info.Url}");
    Console.WriteLine($"LIVE notes      {info.NotesExcerpt.Length} chars of excerpt");

    UpdateVerdict verdict = UpdateDecision.Decide(AppVersion.Current, info, null);
    Console.WriteLine($"LIVE running    {AppVersion.Current.Display}");
    Console.WriteLine($"LIVE decision   prompt {verdict.ShouldPrompt} ({verdict.Reason})");
    Console.WriteLine($"LIVE elapsed    {elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine("LIVE PASS");
    return 0;
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Scrappy.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

