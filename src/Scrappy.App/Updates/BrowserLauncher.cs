using System.ComponentModel;
using System.Diagnostics;

namespace Scrappy.App.Updates;

/// <summary>
/// Opens the release page in the user's browser via the shell. This is the entire "install" step:
/// the app hands the page over and stops. No download, no replacement, no elevated anything.
/// </summary>
public sealed class BrowserLauncher : ILauncher
{
    public bool TryOpen(string url, out string? error)
    {
        try
        {
            // UseShellExecute is what makes this the default browser rather than a child process.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException
                                      or PlatformNotSupportedException or ArgumentException)
        {
            // A machine with no browser registered is unusual, not impossible. Say so, do not crash.
            error = ex.Message;
            return false;
        }
    }
}
