using Avalonia.Controls;
using Avalonia.Threading;

namespace Scrappy.App.Updates;

/// <summary>
/// The real <see cref="IUpdateUi"/>: an Avalonia window, shown as a modal to the main window.
///
/// The notifier runs off the UI thread, so every touch of the window is marshalled here with
/// <see cref="Dispatcher.UIThread"/>. If the app is already shutting down the dialog simply never
/// appears; there is no path from here into an exception on the UI thread.
/// </summary>
public sealed class AvaloniaUpdateUi : IUpdateUi
{
    private readonly Func<Window?> _owner;

    public AvaloniaUpdateUi(Func<Window?> owner)
    {
        _owner = owner;
    }

    public Task<UpdateChoice> ShowAsync(UpdateInfo latest, AppVersion running, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<UpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var dialog = new UpdateDialogWindow(latest, running);
                dialog.Closed += (_, _) => completion.TrySetResult(dialog.Choice);

                if (_owner() is { IsVisible: true } owner)
                {
                    await dialog.ShowDialog(owner);
                }
                else
                {
                    dialog.Show();
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (cancellationToken.CanBeCanceled)
        {
            // A window that is never answered must not hold the check open: treat it as a skip.
            cancellationToken.Register(() => completion.TrySetResult(UpdateChoice.Skip));
        }

        return completion.Task;
    }

    public void ReportLaunchFailure(string releaseUrl, string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = new UpdateFailureWindow(releaseUrl, message);
                if (_owner() is { IsVisible: true } owner)
                {
                    await window.ShowDialog(owner);
                }
                else
                {
                    window.Show();
                }
            }
            catch (Exception)
            {
                // Reporting a failure is best effort; there is nothing left to fall back to.
            }
        });
    }
}
