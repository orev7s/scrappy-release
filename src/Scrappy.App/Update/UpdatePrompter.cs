using Avalonia.Controls;
using Avalonia.Threading;
using Scrappy.Update;

namespace Scrappy.App.Update;

/// <summary>
/// The window-sized part of the update check: ask the question, open the page, say nothing at all
/// when there is nothing to say.
///
/// Everything that can go wrong here is contained on purpose. A start-up check that throws takes
/// the whole app with it before the first frame is on screen, and a user who has no internet has
/// not asked to be told about it.
/// </summary>
public static class UpdatePrompter
{
    public const string RepositoryOwner = "orev7s";
    public const string RepositoryName = "scrappy-release";

    /// <summary>
    /// Long enough that the window is up and the first discovery scan is under way before this
    /// competes with anything. The check runs on its own task either way: start-up never waits.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Fires the start-up check and returns immediately. Whatever it finds, it finds later, and if
    /// it finds nothing the user is never told.
    /// </summary>
    public static void CheckOnStartup(Window window) => _ = CheckOnStartupAsync(window);

    private static async Task CheckOnStartupAsync(Window window)
    {
        try
        {
            await Task.Delay(StartupDelay).ConfigureAwait(true);
            await CheckAsync(window, say: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The last line of defence. Nothing above is allowed to throw, but if it does, the
            // correct outcome is a line in the log and an app that carries on.
            Console.WriteLine($"update check abandoned: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The manual check, from the button. The difference from start-up is only that this one
    /// answers: a user who pressed a button is owed a result, even when the result is "no".
    /// </summary>
    public static async Task CheckAsync(Window window, Action<string>? say = null)
    {
        try
        {
            UpdateChecker checker = CreateChecker();
            UpdateCheck result = await checker.CheckAsync().ConfigureAwait(true);

            if (result.Offer is not { } offer)
            {
                string message = result.Status switch
                {
                    UpdateStatus.UpToDate => $"Scrappy {checker.Current} is the latest release.",
                    UpdateStatus.Suppressed => "There is a newer release, but you asked not to be told about this one.",
                    _ => "Could not check for updates. GitHub could not be reached; try again later.",
                };

                Console.WriteLine($"update check          {result.Status}: {message}");
                say?.Invoke(message);
                CloseIfScripted(window);
                return;
            }

            Console.WriteLine($"update check          {result.Status}: {offer.Detail} -> {offer.Url}");

            UpdateDecision decision = await UpdateDialog.AskAsync(window, offer).ConfigureAwait(true);
            UpdateChoice choice = checker.Choose(offer, decision);

            Console.WriteLine($"update decision       {choice.Decision}");

            if (choice.OpensBrowser)
            {
                say?.Invoke($"Opening {choice.Url}");
                await OpenAsync(window, choice.Url!).ConfigureAwait(true);
            }

            CloseIfScripted(window);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"update check failed: {ex.GetType().Name}: {ex.Message}");
            say?.Invoke("Could not check for updates.");
            CloseIfScripted(window);
        }
    }

    /// <summary>
    /// A run driven from the command line has no hand on the mouse to close the window, so it ends
    /// as soon as the check has reported. Nothing else in the app ever closes itself.
    /// </summary>
    private static void CloseIfScripted(Window window)
    {
        if (Program.Options.UpdateAnswer is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(window.Close);
    }

    /// <summary>
    /// Opens the release page in the user's own browser. This is the entire update: the download
    /// and the install are the user's, from the page GitHub already renders.
    /// </summary>
    private static async Task OpenAsync(Window window, string url)
    {
        Console.WriteLine($"update opening        {url}");

        try
        {
            Avalonia.Controls.TopLevel? topLevel = TopLevel.GetTopLevel(window);
            if (topLevel is null)
            {
                Console.WriteLine("update opening        no top level to launch from");
                return;
            }

            bool launched = await topLevel.Launcher.LaunchUriAsync(new Uri(url));
            Console.WriteLine($"update opening        {(launched ? "browser launched" : "the platform refused the URL")}");
        }
        catch (Exception ex)
        {
            // A desktop with no browser registered is not an app failure. The URL is on screen.
            Console.WriteLine($"update opening        {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The real feed against GitHub, unless a run is being driven against a stand-in for evidence.
    /// </summary>
    private static UpdateChecker CreateChecker()
    {
        var feedOptions = new GitHubReleaseFeedOptions
        {
            Owner = RepositoryOwner,
            Repository = RepositoryName,
            ApiBase = Program.Options.UpdateApi ?? GitHubReleaseFeedOptions.DefaultApiBase,
            WebBase = Program.Options.UpdateApi is { } stub ? stub : GitHubReleaseFeedOptions.DefaultWebBase,
        };

        SemanticVersion current = Program.Options.UpdateVersion is { Length: > 0 } pretend
            ? SemanticVersion.Parse(pretend) ?? AppVersion.Current
            : AppVersion.Current;

        return new UpdateChecker(
            new GitHubReleaseFeed(feedOptions),
            UpdateSuppression.Open(Program.Options.UpdateStatePath),
            current);
    }
}
