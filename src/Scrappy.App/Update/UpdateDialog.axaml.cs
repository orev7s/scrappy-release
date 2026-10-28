using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Scrappy.Update;

namespace Scrappy.App.Update;

/// <summary>What the dialog binds to: the question, and the two lines under it.</summary>
public sealed record UpdateDialogModel(string Question, string Detail, string Note);

/// <summary>
/// The update question, once, modally.
///
/// The dialog asks and reports which button was pressed. It decides nothing: whether there is a
/// question at all was settled by <see cref="UpdateChecker"/> before this window exists, and what
/// the answer means is settled after it closes.
/// </summary>
public sealed partial class UpdateDialog : Window
{
    private readonly TaskCompletionSource<UpdateDecision> _answered = new();

    public UpdateDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
        Closing += (_, _) => _answered.TrySetResult(UpdateDecision.Skip);
    }

    /// <summary>
    /// Shows the question and waits. Closing the window any other way - the X, Alt+F4 - is a Skip:
    /// it dismisses for now and silences nothing, which is the answer with the least consequence.
    /// </summary>
    public static async Task<UpdateDecision> AskAsync(Window owner, UpdateOffer offer)
    {
        var dialog = new UpdateDialog
        {
            DataContext = new UpdateDialogModel(UpdateOffer.Question, offer.Detail, offer.Note),
        };

        // Evidence switches, for a capture or a scripted run with no hand on the mouse: the dialog
        // is real and the button is really pressed, only the click comes from the command line.
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await dialog.ShowDialog(owner);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"update dialog failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Whatever happened to the window, the question has an answer: nothing was chosen.
                dialog._answered.TrySetResult(UpdateDecision.Skip);
            }
        });

        return await dialog._answered.Task.ConfigureAwait(true);
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        // Let one layout pass land before anything else happens, so a capture shows a finished
        // window rather than one that is still being measured.
        DispatcherTimer.RunOnce(() =>
        {
            CaptureIfAsked();
            PressIfAsked();
        }, TimeSpan.FromMilliseconds(350));
    }

    private void CaptureIfAsked()
    {
        if (Program.Options.UpdateShot is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var size = new PixelSize(
                Math.Max(1, (int)Bounds.Width),
                Math.Max(1, (int)Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(this);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bitmap.Save(path);
            Console.WriteLine($"update dialog captured: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"update dialog capture failed: {ex.Message}");
        }
    }

    private void PressIfAsked()
    {
        switch (Program.Options.UpdateAnswer)
        {
            case "update":
                Console.WriteLine("update dialog: pressing Update");
                OnUpdate(this, new Avalonia.Interactivity.RoutedEventArgs());
                break;
            case "skip":
                Console.WriteLine("update dialog: pressing Skip update");
                OnSkip(this, new Avalonia.Interactivity.RoutedEventArgs());
                break;
            case "never":
                Console.WriteLine("update dialog: pressing Don't show again");
                OnNever(this, new Avalonia.Interactivity.RoutedEventArgs());
                break;
        }
    }

    private void OnUpdate(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Answer(UpdateDecision.Update);

    private void OnSkip(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Answer(UpdateDecision.Skip);

    private void OnNever(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Answer(UpdateDecision.DontShowAgain);

    private void Answer(UpdateDecision decision)
    {
        _answered.TrySetResult(decision);
        Close();
    }
}
