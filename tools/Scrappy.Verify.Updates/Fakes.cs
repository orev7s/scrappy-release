using Scrappy.App.Updates;

namespace Scrappy.Verify.Updates;

/// <summary>A feed that returns a fixed answer. Counts its calls so "no prompt" can be proven.</summary>
public sealed class StubFeed : IReleaseFeed
{
    private readonly UpdateInfo? _info;

    public StubFeed(UpdateInfo? info)
    {
        _info = info;
    }

    public int Calls { get; private set; }

    public Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_info);
    }
}

/// <summary>Offline, in its most common shape: the client throws instead of returning null.</summary>
public sealed class ThrowingFeed : IReleaseFeed
{
    public int Calls { get; private set; }

    public Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        throw new HttpRequestException("No such host is known. (api.github.com)");
    }
}

/// <summary>A request that never answers, so only the cancellation budget can end it.</summary>
public sealed class HangingFeed : IReleaseFeed
{
    public int Calls { get; private set; }

    public async Task<UpdateInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return null;
    }
}

/// <summary>Records which button was pressed, and which release it was pressed for.</summary>
public sealed class FakeUi : IUpdateUi
{
    private readonly UpdateChoice _choice;

    public FakeUi(UpdateChoice choice)
    {
        _choice = choice;
    }

    public int Shows { get; private set; }

    public UpdateInfo? ShownAbout { get; private set; }

    public AppVersion? ShownRunning { get; private set; }

    public List<string> LaunchFailures { get; } = new();

    public Task<UpdateChoice> ShowAsync(UpdateInfo latest, AppVersion running, CancellationToken cancellationToken)
    {
        Shows++;
        ShownAbout = latest;
        ShownRunning = running;
        return Task.FromResult(_choice);
    }

    public void ReportLaunchFailure(string releaseUrl, string message) =>
        LaunchFailures.Add($"{releaseUrl}|{message}");
}

/// <summary>Records the URL instead of starting a browser.</summary>
public sealed class FakeLauncher : ILauncher
{
    private readonly bool _works;

    public FakeLauncher(bool works = true)
    {
        _works = works;
    }

    public List<string> Opened { get; } = new();

    public bool TryOpen(string url, out string? error)
    {
        Opened.Add(url);
        error = _works ? null : "no browser is registered for this file type";
        return _works;
    }
}

/// <summary>A dialog that fails to open. The notifier must survive it.</summary>
public sealed class ThrowingUi : IUpdateUi
{
    public Task<UpdateChoice> ShowAsync(UpdateInfo latest, AppVersion running, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the window could not be created");

    public void ReportLaunchFailure(string releaseUrl, string message)
    {
    }
}