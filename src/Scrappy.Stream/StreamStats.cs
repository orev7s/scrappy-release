using System.Diagnostics;

namespace Scrappy.Stream;

/// <summary>
/// Live counters for one session. Everything the status strip shows comes from here, and nothing
/// here is estimated: a number is either counted or measured.
/// </summary>
public sealed class StreamStats
{
    private readonly object _windowLock = new();
    private long _windowStartTicks = Stopwatch.GetTimestamp();
    private int _windowFrames;
    private long _windowBytes;

    private long _framesReceived;
    private long _framesDecoded;
    private long _framesPresented;
    private long _framesDroppedByPresenter;
    private long _framesSkippedByReceiver;
    private long _decodeErrors;
    private long _bytesReceived;
    private double _decodeMsAverage;
    private double _decodeMsPeak;
    private double _latencyMsAverage;
    private double _queueDelayMs;
    private double _queueDelayMsPeak;

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>Frames discarded because a newer one arrived before the previous could be shown.</summary>
    public long FramesDroppedByPresenter => Interlocked.Read(ref _framesDroppedByPresenter);

    /// <summary>
    /// Access units read and thrown away without decoding, because the socket already held more
    /// than the latency budget allows. Not an error: this is the receiver refusing to be late.
    /// </summary>
    public long FramesSkippedByReceiver => Interlocked.Read(ref _framesSkippedByReceiver);

    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public double DecodeMsAverage => Volatile.Read(ref _decodeMsAverage);

    public double DecodeMsPeak => Volatile.Read(ref _decodeMsPeak);

    /// <summary>Packet arrival to presentation, averaged. Measured inside this process only.</summary>
    public double LatencyMsAverage => Volatile.Read(ref _latencyMsAverage);

    /// <summary>
    /// How much video is waiting in the socket, in milliseconds of playback. This is the number
    /// the user feels as lag, and unlike frame rate it does not lie: a stream can report 60 fps
    /// while showing a picture from four seconds ago.
    /// </summary>
    public double QueueDelayMs => Volatile.Read(ref _queueDelayMs);

    public double QueueDelayMsPeak => Volatile.Read(ref _queueDelayMsPeak);

    /// <summary>Records the current backlog, expressed as milliseconds of video.</summary>
    public void ObserveQueueDelay(double milliseconds)
    {
        if (milliseconds < 0)
        {
            return;
        }

        double previous = Volatile.Read(ref _queueDelayMs);
        Volatile.Write(ref _queueDelayMs, previous == 0 ? milliseconds : previous * 0.9 + milliseconds * 0.1);

        if (milliseconds > Volatile.Read(ref _queueDelayMsPeak))
        {
            Volatile.Write(ref _queueDelayMsPeak, milliseconds);
        }
    }

    public void OnPacketSkipped() => Interlocked.Increment(ref _framesSkippedByReceiver);

    /// <summary>Frames per second over the last completed one-second window.</summary>
    public double Fps { get; private set; }

    /// <summary>Megabits per second over the last completed one-second window.</summary>
    public double MegabitsPerSecond { get; private set; }

    public int GeometryWidth { get; private set; }

    public int GeometryHeight { get; private set; }

    public string Backend { get; private set; } = "software";

    /// <summary>The codec actually being decoded, which is not necessarily the one requested.</summary>
    public string Codec { get; private set; } = "h264";

    public void SetGeometry(int width, int height)
    {
        GeometryWidth = width;
        GeometryHeight = height;
    }

    public void SetBackend(string backend) => Backend = backend;

    public void SetCodec(string codec) => Codec = codec;

    public void OnPacketReceived(int payloadBytes)
    {
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, payloadBytes);
    }

    public void OnFrameDecoded(double decodeMilliseconds)
    {
        Interlocked.Increment(ref _framesDecoded);

        // Exponential moving average, so the number responds within a second without jumping.
        double previous = Volatile.Read(ref _decodeMsAverage);
        Volatile.Write(ref _decodeMsAverage, previous == 0 ? decodeMilliseconds : previous * 0.9 + decodeMilliseconds * 0.1);

        double peak = Volatile.Read(ref _decodeMsPeak);
        if (decodeMilliseconds > peak)
        {
            Volatile.Write(ref _decodeMsPeak, decodeMilliseconds);
        }
    }

    public void OnFramePresented(double latencyMilliseconds)
    {
        Interlocked.Increment(ref _framesPresented);
        double previous = Volatile.Read(ref _latencyMsAverage);
        Volatile.Write(ref _latencyMsAverage, previous == 0 ? latencyMilliseconds : previous * 0.9 + latencyMilliseconds * 0.1);
    }

    public void OnFrameDropped() => Interlocked.Increment(ref _framesDroppedByPresenter);

    public void OnDecodeError() => Interlocked.Increment(ref _decodeErrors);

    /// <summary>Ticks the one-second window. Safe to call from any thread and as often as you like.</summary>
    public void RollWindow()
    {
        double elapsedSeconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _windowStartTicks)).TotalSeconds;
        if (elapsedSeconds < 1.0)
        {
            return;
        }

        int frames;
        long bytes;
        lock (_windowLock)
        {
            frames = _windowFrames;
            bytes = _windowBytes;
            _windowFrames = 0;
            _windowBytes = 0;
            Volatile.Write(ref _windowStartTicks, Stopwatch.GetTimestamp());
        }

        Fps = frames / elapsedSeconds;
        MegabitsPerSecond = bytes * 8.0 / 1_000_000.0 / elapsedSeconds;
    }

    public void NoteWindowFrame(int bytes)
    {
        lock (_windowLock)
        {
            _windowFrames++;
            _windowBytes += bytes;
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _framesDecoded, 0);
        Interlocked.Exchange(ref _framesPresented, 0);
        Interlocked.Exchange(ref _framesDroppedByPresenter, 0);
        Interlocked.Exchange(ref _decodeErrors, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Volatile.Write(ref _decodeMsAverage, 0);
        Volatile.Write(ref _decodeMsPeak, 0);
        Volatile.Write(ref _latencyMsAverage, 0);
        Fps = 0;
        MegabitsPerSecond = 0;
        lock (_windowLock)
        {
            _windowFrames = 0;
            _windowBytes = 0;
            Volatile.Write(ref _windowStartTicks, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>One line for logs and bug reports.</summary>
    public string Summary() =>
        $"rx {FramesReceived} dec {FramesDecoded} show {FramesPresented} drop {FramesDroppedByPresenter} " +
        $"err {DecodeErrors} {Fps:F1}fps {MegabitsPerSecond:F1}Mbps dec {DecodeMsAverage:F1}ms show {LatencyMsAverage:F1}ms {Backend}";
}
