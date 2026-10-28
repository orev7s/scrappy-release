namespace Scrappy.Stream;

/// <summary>
/// Where decoded frames go. Implementations take ownership of the frame and must release it.
/// A presenter must not block: it runs on the network read loop, and a stall there becomes
/// backpressure on the device encoder.
/// </summary>
public interface IVideoPresenter
{
    /// <summary>Called once the first session header has arrived, before the first frame.</summary>
    void OnSessionStarted(string deviceName, int width, int height);

    /// <summary>Takes ownership of <paramref name="frame"/> and releases it when it is done with it.</summary>
    void Present(VideoFrame frame);

    /// <summary>Called when the stream ends or fails. The reason is already human-readable.</summary>
    void OnSessionEnded(string reason);
}

/// <summary>A presenter that decodes and throws frames away. Used by the verifier and by measurements.</summary>
public sealed class CountingPresenter : IVideoPresenter
{
    private int _width;
    private int _height;

    public int Presented { get; private set; }

    public int ExpectedWidth { get; init; }

    public int ExpectedHeight { get; init; }

    /// <summary>
    /// Stands in for a machine that cannot decode in real time. Used by the backlog check, where
    /// the point is that a slow consumer must cost frames and not latency.
    /// </summary>
    public int DelayPerFrameMs { get; init; }

    /// <summary>Mean luminance of the last presented frame, used to prove pixels are not all black.</summary>
    public double LastMeanLuminance { get; private set; }

    /// <summary>Checksum of the last presented frame, used to prove frames differ.</summary>
    public ulong LastChecksum { get; private set; }

    public ulong FirstChecksum { get; private set; }

    public string DeviceName { get; private set; } = "";

    public void OnSessionStarted(string deviceName, int width, int height)
    {
        DeviceName = deviceName;
        _width = width;
        _height = height;
    }

    public void Present(VideoFrame frame)
    {
        try
        {
            if (ExpectedWidth > 0 && frame.Width != ExpectedWidth)
            {
                throw new InvalidDataException($"expected width {ExpectedWidth}, got {frame.Width}");
            }

            if (ExpectedHeight > 0 && frame.Height != ExpectedHeight)
            {
                throw new InvalidDataException($"expected height {ExpectedHeight}, got {frame.Height}");
            }

            Span<byte> pixels = frame.Pixels;

            // Sample rather than sum: a full-frame pass at 60 fps would distort what we measure.
            long luminance = 0;
            ulong checksum = 1469598103934665603UL;
            const int step = 97;
            int samples = 0;
            for (int i = 0; i + 2 < pixels.Length; i += 4 * step)
            {
                luminance += pixels[i] + pixels[i + 1] + pixels[i + 2];
                checksum = (checksum ^ pixels[i]) * 1099511628211UL;
                samples++;
            }

            if (DelayPerFrameMs > 0)
            {
                Thread.Sleep(DelayPerFrameMs);
            }

            Presented++;
            LastMeanLuminance = samples == 0 ? 0 : luminance / (double)(samples * 3);
            LastChecksum = checksum;
            if (Presented == 1)
            {
                FirstChecksum = checksum;
            }
        }
        finally
        {
            frame.Release();
        }
    }

    public void OnSessionEnded(string reason)
    {
        EndReason = reason;
    }

    public string? EndReason { get; private set; }
}
