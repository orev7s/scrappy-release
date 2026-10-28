using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Scrappy.Settings;

namespace Scrappy.App.Views;

/// <summary>
/// One screen's usable area, in device-independent pixels, origin at the top-left of the virtual
/// desktop. This is deliberately a plain value type with no Avalonia in it, so the placement rules
/// can be tested at any resolution without a window server.
/// </summary>
/// <param name="Scale">The screen's DPI scale, only used to turn a position back into pixels.</param>
public readonly record struct ScreenArea(double X, double Y, double Width, double Height, double Scale = 1)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsUsable =>
        double.IsFinite(X) && double.IsFinite(Y) && Width >= 1 && Height >= 1;

    /// <summary>How much of <paramref name="frame"/> this screen actually shows, in square DIPs.</summary>
    public double Overlap(WindowFrame frame)
    {
        double width = Math.Min(Right, frame.Right) - Math.Max(X, frame.X);
        double height = Math.Min(Bottom, frame.Bottom) - Math.Max(Y, frame.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}

/// <summary>A window frame in device-independent pixels, top-left origin.</summary>
public readonly record struct WindowFrame(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CentreX => X + (Width / 2);

    public double CentreY => Y + (Height / 2);

    public bool IsUsable =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width >= 1 && Height >= 1;
}

/// <summary>
/// Where the window goes next launch.
///
/// The saved frame is an untrusted input: it can come from a build with different defaults, from a
/// hand-edited file, or - the case this exists for - from the same machine before a 4K monitor on
/// the right-hand side was unplugged. Restoring it blindly opens a window on a screen that is not
/// there any more, which looks exactly like the app failing to start. So the frame is matched
/// against the screens that exist now, pulled inside the working area it lands on, and centred on
/// the primary screen when it lands on nothing at all.
///
/// The rules are a pure function of the frame and the screen list so they can be checked at 1080p,
/// 1440p and 4K, and against a disconnected-monitor frame, without opening a window.
/// </summary>
public static class WindowGeometry
{
    /// <summary>
    /// Fits <paramref name="wanted"/> onto the screens that exist, never growing it and never
    /// leaving it smaller than the window's minimum.
    ///
    /// <paramref name="screens"/> must have the primary screen first; that is the screen a frame
    /// which is on no screen at all is centred on, and the screen list Avalonia hands us is
    /// reordered that way by <see cref="CurrentScreens"/>.
    /// </summary>
    public static WindowFrame ClampToScreens(
        WindowFrame wanted,
        IReadOnlyList<ScreenArea> screens,
        double minWidth = 0,
        double minHeight = 0)
    {
        if (!wanted.IsUsable || screens is null || screens.Count == 0)
        {
            // Nothing to reason from. Restoring what was asked for beats inventing a position.
            return wanted;
        }

        ScreenArea target = screens[0];
        bool onScreen = false;
        double best = 0;

        foreach (ScreenArea screen in screens)
        {
            if (!screen.IsUsable)
            {
                continue;
            }

            double overlap = screen.Overlap(wanted);
            if (overlap > best)
            {
                best = overlap;
                target = screen;
                onScreen = true;
            }
        }

        if (!onScreen && !target.IsUsable)
        {
            // Every entry in the list was nonsense; only a known-good primary is worth centring on.
            foreach (ScreenArea screen in screens)
            {
                if (screen.IsUsable)
                {
                    target = screen;
                    break;
                }
            }
        }

        if (!target.IsUsable)
        {
            return wanted;
        }

        double width = Fit(wanted.Width, minWidth, target.Width);
        double height = Fit(wanted.Height, minHeight, target.Height);

        if (onScreen)
        {
            // Partly off the edge is the common case (the window was dragged half past the right
            // edge). Pulling it inside keeps the title bar reachable.
            return new WindowFrame(
                Math.Clamp(wanted.X, target.X, target.Right - width),
                Math.Clamp(wanted.Y, target.Y, target.Bottom - height),
                width,
                height);
        }

        // Off every screen: the monitor it lived on is gone. Same size, centred, visible.
        return new WindowFrame(
            target.X + ((target.Width - width) / 2),
            target.Y + ((target.Height - height) / 2),
            width,
            height);
    }

    /// <summary>Where <paramref name="frame"/> would land, in DIPs, right now.</summary>
    public static WindowFrame CurrentFrame(Window window)
    {
        double scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        double width = window.Bounds.Width > 1 ? window.Bounds.Width : window.Width;
        double height = window.Bounds.Height > 1 ? window.Bounds.Height : window.Height;

        return new WindowFrame(
            window.Position.X / scale,
            window.Position.Y / scale,
            double.IsFinite(width) ? width : 0,
            double.IsFinite(height) ? height : 0);
    }

    /// <summary>The screens that exist now, primary first, in DIPs with their working area.</summary>
    public static IReadOnlyList<ScreenArea> CurrentScreens(Window window)
    {
        var areas = new List<ScreenArea>();
        var primary = new List<ScreenArea>();

        try
        {
            foreach (Screen screen in window.Screens.All)
            {
                if (TryConvert(screen, out ScreenArea area))
                {
                    areas.Add(area);
                }
            }

            if (window.Screens.Primary is { } first && TryConvert(first, out ScreenArea primaryArea))
            {
                primary.Add(primaryArea);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A platform without screen reporting. The clamp then has nothing to say, and the
            // saved frame is restored as-is - which is the old behaviour, not a crash.
            return [];
        }

        // Primary first, then whatever is left; a duplicate of the primary is harmless, so it is
        // easier to guarantee ordering than to de-duplicate by identity.
        primary.AddRange(areas.Where(area => !primary.Any(p => p.X == area.X && p.Y == area.Y && p.Width == area.Width)));

        return primary;
    }

    /// <summary>
    /// Applies a remembered frame to a window that has not been shown yet. Setting the size and
    /// position before <c>Show</c> is what makes the restore invisible: the window is never drawn
    /// at the default size first.
    ///
    /// Returns the frame the window was actually put at, so the caller can remember that instead of
    /// the maximised frame if the user closes it while maximised. Returns an unused frame when there
    /// was nothing to restore.
    /// </summary>
    public static WindowFrame Apply(Window window, WindowPlacement? saved)
    {
        if (saved is null || !saved.IsUsable)
        {
            return default;
        }

        IReadOnlyList<ScreenArea> screens = CurrentScreens(window);
        if (screens.Count == 0)
        {
            // No screen geometry to reason about. Leave the window at its XAML default rather than
            // placing it somewhere arbitrary.
            return default;
        }

        double minWidth = double.IsFinite(window.MinWidth) && window.MinWidth > 0 ? window.MinWidth : 0;
        double minHeight = double.IsFinite(window.MinHeight) && window.MinHeight > 0 ? window.MinHeight : 0;

        WindowFrame frame = ClampToScreens(
            new WindowFrame(saved.Left, saved.Top, saved.Width, saved.Height),
            screens,
            minWidth,
            minHeight);

        window.Width = frame.Width;
        window.Height = frame.Height;

        // Position is in physical pixels while the frame is in DIPs; the screen the window landed
        // on knows the factor between them.
        double scale = ScaleAt(frame, screens);
        window.Position = new PixelPoint(
            (int)Math.Round(frame.X * scale),
            (int)Math.Round(frame.Y * scale));

        if (saved.Maximised)
        {
            window.WindowState = WindowState.Maximized;
        }

        return frame;
    }

    /// <summary>
    /// The frame to remember. <paramref name="lastNormal"/> is the last frame seen while the window
    /// was not maximised, so un-maximising after the next launch goes back to a real size instead of
    /// the maximised one.
    /// </summary>
    public static WindowPlacement Capture(Window window, WindowFrame? lastNormal)
    {
        WindowFrame frame = lastNormal ?? CurrentFrame(window);
        bool maximised = window.WindowState == WindowState.Maximized;

        return new WindowPlacement
        {
            Left = frame.X,
            Top = frame.Y,
            Width = frame.Width,
            Height = frame.Height,
            Maximised = maximised,
        };
    }

    private static double Fit(double size, double minimum, double room)
    {
        double floor = Math.Min(minimum, room);
        return Math.Clamp(size, floor, room);
    }

    private static double ScaleAt(WindowFrame frame, IReadOnlyList<ScreenArea> screens)
    {
        foreach (ScreenArea screen in screens)
        {
            if (screen.Scale > 0 &&
                frame.CentreX >= screen.X && frame.CentreX <= screen.Right &&
                frame.CentreY >= screen.Y && frame.CentreY <= screen.Bottom)
            {
                return screen.Scale;
            }
        }

        return 1.0;
    }

    private static bool TryConvert(Screen screen, out ScreenArea area)
    {
        PixelRect working = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;

        area = new ScreenArea(
            working.X / scale,
            working.Y / scale,
            working.Width / scale,
            working.Height / scale,
            scale);

        return area.IsUsable;
    }
}

