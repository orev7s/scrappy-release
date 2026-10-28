using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Scrappy.Protocol;

namespace Scrappy.Stream;

/// <summary>What the PC asks the tablet for. Fixed for now: the encoder needs a target.</summary>
public sealed record AudioOptions
{
    public bool Enabled { get; init; } = true;

    public int SampleRate { get; init; } = 48_000;

    public int Channels { get; init; } = 2;

    public int Bitrate { get; init; } = 128_000;
}

/// <summary>Live counters for the audio side. Shown in the status strip, and by the verifier.</summary>
public sealed class AudioStats
{
    private long _packets;
    private long _samples;
    private long _dropped;
    private string _state = "not started";

    public long Packets => Interlocked.Read(ref _packets);

    public long Samples => Interlocked.Read(ref _samples);

    public long DroppedChunks => Interlocked.Read(ref _dropped);

    /// <summary>"48 kHz stereo" while healthy, otherwise why not.</summary>
    public string State
    {
        get => Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, value);
    }

    public string? Error { get; set; }

    public void Packet() => Interlocked.Increment(ref _packets);

    public void SamplesWritten(int count) => Interlocked.Add(ref _samples, count);

    public void Dropped() => Interlocked.Increment(ref _dropped);
}

/// <summary>
/// The audio half of a session: a second connection to the same port, carrying AAC frames to a
/// playback device on this machine.
///
/// It is a separate connection on purpose. Video and audio have different priorities when a link is
/// congested - video can drop a frame and nobody notices, audio cannot - and sharing one socket
/// would make an audio stall hold up the picture. Two sockets also mean the video path stays
/// byte-identical to scrcpy's, which is the one compatibility promise this project makes.
///
/// Nothing here can end a video session. Every failure path sets <see cref="AudioStats.Error"/> and
/// returns, because a missing sound card should not cost you the screen.
/// </summary>
public sealed class AudioSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly IAudioSink _sink;
    private readonly AudioOptions _options;
    private readonly AacDecoder _decoder;
    private readonly VideoStreamReader _reader = new();
    private readonly short[] _pcm = new short[8192 * 2];
    private int _disposed;

    private AudioSession(TcpClient client, IAudioSink sink, AudioOptions options)
    {
        _client = client;
        _stream = client.GetStream();
        _sink = sink;
        _options = options;
        _decoder = new AacDecoder(options.SampleRate, options.Channels);
    }

    public AudioStats Stats { get; } = new();

    /// <summary>Opens the audio connection and asks the tablet to start capturing.</summary>
    public static async Task<AudioSession> ConnectAsync(
        string host,
        int port,
        IAudioSink sink,
        AudioOptions options,
        int timeoutMs = 4000,
        CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            client.ReceiveBufferSize = 256 * 1024;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            var session = new AudioSession(client, sink, options);
            await session.NegotiateAsync(timeout.Token).ConfigureAwait(false);
            return session;
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The one line this connection opens with. The tablet reads the first line of every connection
    /// and decides which of the three it is: a status request, a video session, or this.
    /// </summary>
    private async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        var request = new
        {
            stream = "audio",
            codec = "aac",
            sampleRate = _options.SampleRate,
            channels = _options.Channels,
            bitrate = _options.Bitrate,
        };

        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
        await _stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads, decodes and plays until the stream ends. Never throws for audio reasons.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.Register(Close);

            await foreach (StreamMessage message in _reader
                               .ReadMessagesAsync(_stream, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (message.Kind != StreamMessageKind.Packet)
                {
                    continue;
                }

                VideoPacket packet = message.Packet;

                if (packet.IsConfig)
                {
                    // AudioSpecificConfig. The decoder cannot start without it, so a session that
                    // never receives one reports that instead of decoding noise.
                    _decoder.SetExtradata(packet.Payload);
                    continue;
                }

                if (!_decoder.IsOpen && _decoder.SampleRate <= 0)
                {
                    continue;
                }

                Stats.Packet();

                try
                {
                    int samples = _decoder.Decode(packet.Payload, _pcm);
                    if (samples <= 0)
                    {
                        continue;
                    }

                    _sink.Start(_decoder.SampleRate, _decoder.Channels);

                    if (_sink.Write(_pcm.AsSpan(0, samples * _decoder.Channels)))
                    {
                        Stats.SamplesWritten(samples);
                    }
                    else
                    {
                        Stats.Dropped();
                    }

                    Stats.State = $"{_decoder.SampleRate / 1000.0:0.#} kHz {(_decoder.Channels == 1 ? "mono" : "stereo")}";
                }
                catch (AudioException ex)
                {
                    Fail(ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Fail($"audio connection closed ({ex.Message})");
        }
        finally
        {
            _sink.Stop();
        }
    }

    private void Fail(string message)
    {
        Stats.Error = message;
        Stats.State = "unavailable";
        _sink.Stop();
    }

    private void Close()
    {
        try
        {
            _client.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();
        _decoder.Dispose();
        _sink.Stop();
        _stream.Dispose();
        _client.Dispose();
        await ValueTask.CompletedTask;
    }
}
