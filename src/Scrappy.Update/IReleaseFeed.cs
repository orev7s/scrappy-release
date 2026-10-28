namespace Scrappy.Update;

/// <summary>
/// Where the newest release comes from.
///
/// The contract is the important part: an implementation answers with the newest release it knows
/// about, or with null, and it does not throw. A network that is down, a rate limit, a proxy that
/// rewrites the reply and a repository with no releases are all the same answer here - "nothing to
/// say" - because none of them is worth interrupting the user's start-up for.
/// </summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> TryLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>A feed that always answers "nothing": offline builds, tests, and a switch to turn it off.</summary>
public sealed class NoReleaseFeed : IReleaseFeed
{
    public Task<ReleaseInfo?> TryLatestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ReleaseInfo?>(null);
}
