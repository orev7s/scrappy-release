using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Scrappy.App.Updates;

/// <summary>
/// The one dialog this feature shows. Built in code rather than XAML so it can be created, filled
/// and rendered by the headless test project without a XAML loader or a display server.
///
/// Three answers, and only one of them is sticky: Update opens the release page, Skip update
/// forgets everything, Don't show again remembers this version and nothing more.
/// </summary>
public sealed class UpdateDialogWindow : Window
{
    public UpdateDialogWindow(UpdateInfo latest, AppVersion running)
    {
        Title = "Scrappy update";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Choice = UpdateChoice.Skip;

        var update = new Button { Content = "Update", IsDefault = true, MinWidth = 96 };
        update.Classes.Add("primary");
        update.Click += (_, _) => Answer(UpdateChoice.Update);

        var skip = new Button { Content = "Skip update", MinWidth = 96 };
        skip.Click += (_, _) => Answer(UpdateChoice.Skip);

        var never = new Button { Content = "Don't show again", MinWidth = 120 };
        never.Click += (_, _) => Answer(UpdateChoice.DontShowAgain);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { update, skip, never },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "There is an update. Do you want to update?",
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = Describe(latest, running),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (latest.NotesExcerpt.Length > 0)
        {
            body.Children.Add(Notes(latest.NotesExcerpt));
        }

        body.Children.Add(buttons);

        Content = body;

        KeyDown += (_, e) =>
        {
            // Escape is the same answer as closing the window: ask again next time.
            if (e.Key == Key.Escape)
            {
                Answer(UpdateChoice.Skip);
            }
        };
    }

    /// <summary>What the user chose. Starts as Skip, so a window closed with the X is not sticky.</summary>
    public UpdateChoice Choice { get; private set; }

    /// <summary>The version line, spelled out so nobody has to guess which end is which.</summary>
    public static string Describe(UpdateInfo latest, AppVersion running)
    {
        // Most releases are named "Scrappy 0.2.2", which already carries the number. Only add it
        // when the name does not, so the line never reads "Scrappy 0.2.2 (0.2.2)".
        string released = latest.Name.Contains(latest.Version.Text, StringComparison.Ordinal)
            ? latest.Name
            : $"{latest.Name} ({latest.Version.Display})";

        string when = latest.PublishedUtc is { } published
            ? $" was released on {published.UtcDateTime:yyyy-MM-dd}"
            : " is available";

        return $"You are running Scrappy {running.Display}. {released}{when}.";
    }

    private static Border Notes(string excerpt) => new()
    {
        Background = Brush("#111317"),
        BorderBrush = Brush("#2A2D33"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 10),
        Child = new ScrollViewer
        {
            MaxHeight = 190,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock
            {
                Text = excerpt,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brush("#C9CCD1"),
            },
        },
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private void Answer(UpdateChoice choice)
    {
        Choice = choice;
        Close();
    }
}

/// <summary>
/// The "no browser" case, as its own small window. It exists because the alternative - a silent
/// failure after the user pressed Update - is the sort of thing that makes an update prompt worse
/// than none at all.
/// </summary>
public sealed class UpdateFailureWindow : Window
{
    public UpdateFailureWindow(string releaseUrl, string message)
    {
        Title = "Scrappy update";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = "Close", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("primary");
        close.Click += (_, _) => Close();
        close.IsDefault = true;

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "The release page could not be opened.",
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"{message}. Open this address in a browser to get the new version:",
                    TextWrapping = TextWrapping.Wrap,
                },
                new SelectableTextBlock
                {
                    Text = releaseUrl,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#C9CCD1")),
                },
                close,
            },
        };
    }
}
