namespace Scrappy.Stream;

/// <summary>
/// Where decoded audio goes.
///
/// The audio is rendered into a normal Windows playback device, which is the whole point: OBS and
/// every other capture program take audio from a device or from a process, never from a window.
/// Point OBS at the same device, or at this process with Application Audio Capture, and the stream
/// has sound. The person at the PC can then silence it locally by monitoring a different device or
/// turning the speakers down.
///
/// Implementations must not throw from <see cref="Write"/>: audio failing must never take a video
/// session with it. A sink that cannot play reports it through <see cref="LastError"/> and drops.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Prepares the device. Called once per session, after the format is known.</summary>
    void Start(int sampleRate, int channels);

    /// <summary>
    /// Queues interleaved 16-bit samples. Returns false when the chunk was dropped because the
    /// device is behind, which is the correct behaviour for a live mirror: audio that is late is
    /// worse than audio that skipped.
    /// </summary>
    bool Write(ReadOnlySpan<short> samples);

    /// <summary>0 to 100. Applied in software, so it affects playback and not the capture tap.</summary>
    int Volume { get; set; }

    /// <summary>Human-readable device name for the status line.</summary>
    string Describe { get; }

    /// <summary>Null while the sink is healthy. Set when the device refused to open or died.</summary>
    string? LastError { get; }

    /// <summary>Chunks dropped because the device could not keep up.</summary>
    long DroppedChunks { get; }

    void Stop();
}

/// <summary>
/// A sink that accepts everything and plays nothing. Used when the user turned audio off, and by
/// tests that only care about the stream, so no caller has to null-check.
/// </summary>
public sealed class NullAudioSink : IAudioSink
{
    public static readonly NullAudioSink Instance = new();

    public int Volume { get; set; } = 100;

    public string Describe => "off";

    public string? LastError => null;

    public long DroppedChunks => 0;

    public long SamplesAccepted { get; private set; }

    public void Start(int sampleRate, int channels)
    {
    }

    public bool Write(ReadOnlySpan<short> samples)
    {
        SamplesAccepted += samples.Length;
        return true;
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Records everything it is given, in memory. The verification tool uses it to prove that decoded
/// audio reaches the device layer with the right rate, channel count and level, on machines that
/// have no sound card at all.
/// </summary>
public sealed class RecordingAudioSink : IAudioSink
{
    private readonly List<short> _samples = [];

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public int Volume { get; set; } = 100;

    public string Describe => "recording";

    public string? LastError => null;

    public long DroppedChunks => 0;

    public long WriteCalls { get; private set; }

    public IReadOnlyList<short> Samples
    {
        get
        {
            lock (_samples)
            {
                return _samples.ToArray();
            }
        }
    }

    public void Start(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }

    public bool Write(ReadOnlySpan<short> samples)
    {
        lock (_samples)
        {
            foreach (short sample in samples)
            {
                _samples.Add(sample);
            }
        }

        WriteCalls++;
        return true;
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }

    /// <summary>Root mean square of what was written, 0 to 1. Silence is exactly 0.</summary>
    public double Rms()
    {
        IReadOnlyList<short> samples = Samples;
        if (samples.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (short sample in samples)
        {
            double value = sample / 32768.0;
            sum += value * value;
        }

        return Math.Sqrt(sum / samples.Count);
    }

    /// <summary>Length of the recording in seconds, from the configured rate.</summary>
    public double Seconds => SampleRate > 0 && Channels > 0
        ? Samples.Count / (double)(SampleRate * Channels)
        : 0;
}
