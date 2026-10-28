using Avalonia.Controls;
using Avalonia.Input;
using Scrappy.App.Video;

namespace Scrappy.App.Views;

/// <summary>
/// A window that contains only the mirror. It exists so a streaming tool can capture the feed
/// without capturing the control surface around it.
/// </summary>
public partial class StreamWindow : Window
{
    private BitmapVideoPresenter? _presenter;

    public StreamWindow()
    {
        InitializeComponent();

        KeyDown += OnKeyDown;
    }

    /// <summary>Takes over the frame pipeline; the main window's preview detaches first.</summary>
    public void Attach(BitmapVideoPresenter presenter)
    {
        _presenter = presenter;
        Surface.Presenter = presenter;
    }

    /// <summary>The presenter is handed back by the caller, which re-attaches it where it wants.</summary>
    public BitmapVideoPresenter? Detach()
    {
        Surface.Presenter = null;
        BitmapVideoPresenter? presenter = _presenter;
        _presenter = null;
        return presenter;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                e.Handled = true;
                break;

            case Key.Escape when WindowState == WindowState.FullScreen:
                WindowState = WindowState.Normal;
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
