using Scrappy.Stream;

namespace Scrappy.App.Video;

/// <summary>
/// Holds the newest decoded frame until the view is ready to draw it.
///
/// Frames arrive on the network thread and are drawn on the UI thread, so there is exactly one
/// slot between them. If a second frame arrives before the first is drawn, the first is dropped
/// and counted - showing a stale frame is worse than showing nothing, and dropping here is a
/// presentation decision, never a network one.
///
/// This is the development and fallback path. The Windows target replaces it with a D3D11
/// swapchain: same interface, no CPU copy, and flip-model presentation with tearing allowed.
/// </summary>
public sealed class BitmapVideoPresenter : IVideoPresenter
{
    private readonly object _gate = new();
    private VideoFrame? _pending;
    private int _notified;
    private StreamStats? _stats;

    /// <summary>Raised on the network thread when a frame is waiting. Handlers must marshal.</summary>
    public event Action? FrameWaiting;

    public event Action<string, int, int>? SessionStarted;

    public event Action<string>? SessionEnded;

    public StreamStats? Stats
    {
        get => _stats;
        set => _stats = value;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string DeviceName { get; private set; } = "";

    /// <summary>Frames dropped because a newer one replaced them before they were drawn.</summary>
    public long DroppedUnrendered { get; private set; }

    /// <summary>Frames drawn. Counts real presentations, not arrivals.</summary>
    public long Rendered { get; private set; }

    public void OnSessionStarted(string deviceName, int width, int height)
    {
        DeviceName = deviceName;
        Width = width;
        Height = height;
        SessionStarted?.Invoke(deviceName, width, height);
    }

    public void Present(VideoFrame frame)
    {
        VideoFrame? replaced;
        lock (_gate)
        {
            replaced = _pending;
            _pending = frame;
        }

        if (replaced is not null)
        {
            replaced.Release();
            DroppedUnrendered++;
            _stats?.OnFrameDropped();
        }

        // Coalesce notifications: the UI only needs to know that *something* is waiting.
        if (Interlocked.Exchange(ref _notified, 1) == 0)
        {
            FrameWaiting?.Invoke();
        }
    }

    /// <summary>Takes the waiting frame, if any. The caller releases it.</summary>
    public bool TryTake(out VideoFrame frame)
    {
        lock (_gate)
        {
            frame = _pending!;
            _pending = null;
        }

        Volatile.Write(ref _notified, 0);

        if (frame is null)
        {
            return false;
        }

        Rendered++;
        return true;
    }

    /// <summary>Called after a frame has been drawn, so the stats can record end-to-end delay.</summary>
    public void NoteRendered(VideoFrame frame, double milliseconds)
    {
        _stats?.OnFramePresented(milliseconds);
    }

    public void OnSessionEnded(string reason) => SessionEnded?.Invoke(reason);

    /// <summary>Drops any frame that was waiting when the session ended.</summary>
    public void ClearPending()
    {
        VideoFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }

        frame?.Release();
        Volatile.Write(ref _notified, 0);
    }
}
