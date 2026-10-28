using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Scrappy.Stream;

namespace Scrappy.App.Video;

/// <summary>
/// Draws the newest decoded frame, fitted to the available area without distortion.
///
/// The copy from the decoded buffer into the bitmap happens here, on the render pass, and nowhere
/// else: the UI thread never decodes and the decode thread never touches a bitmap.
/// </summary>
public sealed class VideoView : Control
{
    private static readonly IBrush LetterboxBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0E));
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _invalidateQueued;
    private BitmapVideoPresenter? _presenter;

    public BitmapVideoPresenter? Presenter
    {
        get => _presenter;
        set
        {
            if (_presenter is not null)
            {
                _presenter.FrameWaiting -= OnFrameWaiting;
            }

            _presenter = value;

            if (_presenter is not null)
            {
                _presenter.FrameWaiting += OnFrameWaiting;
            }
        }
    }

    /// <summary>Set when the last frame was drawn, for evidence capture and for tests.</summary>
    public long FramesDrawn { get; private set; }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(LetterboxBrush, bounds);

        if (_presenter is null || !_presenter.TryTake(out VideoFrame frame))
        {
            return;
        }

        try
        {
            EnsureBitmap(frame.Width, frame.Height);
            Copy(frame);
            FramesDrawn++;
            _presenter.NoteRendered(frame, Stopwatch.GetElapsedTime(frame.ArrivalTimestamp).TotalMilliseconds);
        }
        finally
        {
            frame.Release();
        }

        if (_bitmap is null)
        {
            return;
        }

        context.DrawImage(_bitmap, Fit(bounds, frame.Width, frame.Height));
    }

    private void OnFrameWaiting()
    {
        if (Interlocked.Exchange(ref _invalidateQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _invalidateQueued, 0);
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmapWidth == width && _bitmapHeight == height)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        _bitmapWidth = width;
        _bitmapHeight = height;
    }

    private unsafe void Copy(VideoFrame frame)
    {
        if (_bitmap is null)
        {
            return;
        }

        using ILockedFramebuffer buffer = _bitmap.Lock();

        fixed (byte* source = frame.Pixels)
        {
            int rowBytes = Math.Min(frame.Stride, buffer.RowBytes);
            for (int y = 0; y < frame.Height; y++)
            {
                Buffer.MemoryCopy(
                    source + y * frame.Stride,
                    (byte*)buffer.Address + y * buffer.RowBytes,
                    buffer.RowBytes,
                    rowBytes);
            }
        }

        // Unlocking the framebuffer publishes the pixels; the render pass that called this is
        // already drawing from the updated bitmap, so no further invalidation is needed.
    }

    /// <summary>Uniform fit: the frame keeps its aspect ratio, empty space stays black.</summary>
    private static Rect Fit(Rect available, int width, int height)
    {
        if (width <= 0 || height <= 0 || available.Width <= 0 || available.Height <= 0)
        {
            return available;
        }

        double scale = Math.Min(available.Width / width, available.Height / height);
        double drawWidth = width * scale;
        double drawHeight = height * scale;

        return new Rect(
            available.X + (available.Width - drawWidth) / 2,
            available.Y + (available.Height - drawHeight) / 2,
            drawWidth,
            drawHeight);
    }
}
