using System.Diagnostics;
using System.Net.Sockets;
using Scrappy.Protocol;

namespace Scrappy.Stream;

public sealed record MirrorOptions
{
    public int ConnectTimeoutMs { get; init; } = 4000;

    /// <summary>
    /// How long to wait for the device name and first header. Without it a device that accepts the
    /// socket and then says nothing leaves the UI on "Connecting" forever, which is indistinguishable
    /// from a hang in this code.
    /// </summary>
    public int HandshakeTimeoutMs { get; init; } = 6000;

    /// <summary>Maps to <c>--hwdec</c>. "disabled" and "auto" are accepted; only software runs today.</summary>
    public string HardwareDecode { get; init; } = "disabled";

    /// <summary>Ask the decoder not to buffer frames it does not need. Trades throughput for delay.</summary>
    public bool LowLatency { get; init; }

    /// <summary>
    /// Socket receive buffer. 0 takes <see cref="MirrorSession.DefaultReceiveBufferBytes"/>.
    ///
    /// Left to the operating system this can autotune to several megabytes, which on a tablet
    /// pushing 12 Mbps is seconds of video the PC has already accepted and cannot take back. The
    /// buffer is a place for the network's jitter to live, not for a backlog to hide.
    /// </summary>
    public int ReceiveBufferBytes { get; init; }

    /// <summary>
    /// How much video may sit in the socket before the receiver starts skipping to the next
    /// keyframe, expressed in milliseconds of the negotiated bitrate. This is the latency the
    /// user is willing to pay when the machine cannot decode fast enough.
    /// </summary>
    public double MaxBacklogMs { get; init; } = 300;

    /// <summary>
    /// One JSON line sent before the handshake. This is how the PC decides what the tablet
    /// encodes; an app that does not read it ignores it harmlessly, since the bytes are consumed
    /// before the 64-byte name.
    /// </summary>
    public SessionRequest? Request { get; init; }

    /// <summary>
    /// Writes the undecoded elementary stream here, parameter sets included. Diagnostics only: a
    /// decode problem on real hardware cannot be argued about without the bytes that caused it.
    /// </summary>
    public string? DumpPath { get; init; }
}

/// <summary>
/// One mirroring session: TCP in, decoded frames out.
///
/// The transport is plain TCP with scrcpy's wire format unchanged - only the socket underneath is
/// different, so both ends keep agreeing on framing. No display state is read or written here;
/// the process never touches the tablet's resolution or refresh rate.
/// </summary>
public sealed class MirrorSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly VideoStreamReader _reader;
    private readonly H264Decoder _decoder;
    private readonly IVideoPresenter _presenter;
    private readonly CancellationTokenSource _abort = new();
    private readonly FileStream? _dump;
    private readonly string _host;
    private readonly int _port;
    private readonly long _backlogBudgetBytes;
    private readonly double _assumedBytesPerSecond;
    private int _ended;
    private int _disposed;
    private int _transportClosed;
    private bool _skipping;

    /// <summary>One megabyte: enough for a Wi-Fi jitter burst at these rates, not a backlog.</summary>
    public const int DefaultReceiveBufferBytes = 1 << 20;

    private MirrorSession(
        TcpClient client,
        NetworkStream stream,
        string deviceName,
        IVideoPresenter presenter,
        MirrorOptions options,
        StreamStats stats,
        string host,
        int port)
    {
        _host = host;
        _port = port;
        _backlogBudgetBytes = BacklogBudget(options);
        _assumedBytesPerSecond = (options.Request?.Bitrate is > 0 and var bitrate ? bitrate : 12_000_000) / 8.0;
        _client = client;
        _stream = stream;
        _reader = new VideoStreamReader();
        _decoder = new H264Decoder(options.Request?.Codec ?? "h264", options.HardwareDecode, options.LowLatency);
        _presenter = presenter;
        DeviceName = deviceName;
        Stats = stats;

        if (!string.IsNullOrWhiteSpace(options.DumpPath))
        {
            try
            {
                _dump = new FileStream(options.DumpPath!, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A diagnostic file that cannot be written must not stop the session.
                _dump = null;
            }
        }
        Stats.SetBackend(_decoder.Backend);
        Stats.SetCodec(_decoder.Codec);
    }

    public string DeviceName { get; }

    /// <summary>Where this session connected, so a dead session can be asked what happened.</summary>
    public string Host => _host;

    public int Port => _port;

    public StreamStats Stats { get; }

    /// <summary>Why the session stopped. Null while it is running.</summary>
    public string? EndReason { get; private set; }

    public static async Task<MirrorSession> ConnectAsync(
        string host,
        int port,
        IVideoPresenter presenter,
        MirrorOptions? options = null,
        StreamStats? stats = null,
        CancellationToken cancellationToken = default)
    {
        FFmpegRuntime.EnsureLocated();
        options ??= new MirrorOptions();

        var client = new TcpClient { NoDelay = true };
        client.ReceiveBufferSize = options.ReceiveBufferBytes > 0
            ? options.ReceiveBufferBytes
            : DefaultReceiveBufferBytes;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"No answer from {host}:{port} within {options.ConnectTimeoutMs} ms.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new IOException($"Could not reach {host}:{port}: {ex.Message}", ex);
        }

        NetworkStream stream = client.GetStream();

        if (options.Request is { } request)
        {
            byte[] hello = System.Text.Encoding.UTF8.GetBytes(request.ToJson() + "\n");
            await stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var reader = new VideoStreamReader();
        string deviceName;

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(options.HandshakeTimeoutMs);

        try
        {
            deviceName = await ReadHandshakeAsync(reader, stream, handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException(
                $"The tablet accepted the connection but sent no device name within " +
                $"{options.HandshakeTimeoutMs} ms. Both apps must be the same version.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new MirrorSession(
            client, stream, deviceName, presenter, options, stats ?? new StreamStats(), host, port);
    }

    /// <summary>
    /// Bytes of video worth waiting for, from the bitrate the tablet was asked for. A quarter of a
    /// second at 12 Mbps is 375 KB; ten seconds of decode catching up is not.
    /// </summary>
    private static long BacklogBudget(MirrorOptions options)
    {
        int bitrate = options.Request?.Bitrate is > 0 and var requested ? requested : 12_000_000;
        double seconds = Math.Clamp(options.MaxBacklogMs, 100, 2000) / 1000.0;
        return Math.Clamp((long)(bitrate / 8.0 * seconds), 96 * 1024, 4 * 1024 * 1024);
    }

    /// <summary>The TCP socket underneath, for the one thing a stream reader cannot know: backlog.</summary>
    /// <summary>
    /// Bytes per second the link is carrying, for turning a backlog into milliseconds. The measured
    /// rate is used once there is one; until then the requested bitrate is the honest guess.
    /// </summary>
    private double BytesPerSecond()
    {
        double measured = Stats.MegabitsPerSecond * 1_000_000 / 8;
        return measured > 0 ? measured : _assumedBytesPerSecond;
    }

    private long BacklogBytes()
    {
        try
        {
            return _client.Available;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the 64-byte device name.
    ///
    /// A real device sends one NUL byte first when it opened the socket in forward mode
    /// (DesktopConnection.java:70), which shifts the name by one. Rather than trusting either
    /// behaviour, the window is read and corrected: a leading NUL means a dummy byte was present,
    /// so the field is shifted one byte left and topped up.
    /// </summary>
    private static async Task<string> ReadHandshakeAsync(
        VideoStreamReader reader,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] field = new byte[ProtocolConstants.DeviceNameLength];
        await stream.ReadExactlyAsync(field, cancellationToken).ConfigureAwait(false);

        // A session header starts with 0x80. Seeing one here means the device wrote headers before
        // its name, which is a protocol mismatch rather than data corruption.
        if (field[0] == 0x80)
        {
            throw new InvalidDataException(
                "The tablet sent a stream header where the device name belongs: the two apps are " +
                "speaking different versions of the handshake. Update both.");
        }

        if (field[0] == 0)
        {
            byte[] extra = new byte[1];
            await stream.ReadExactlyAsync(extra, cancellationToken).ConfigureAwait(false);
            Array.Copy(field, 1, field, 0, field.Length - 1);
            field[^1] = extra[0];
        }

        int end = Array.IndexOf(field, (byte)0);
        if (end < 0)
        {
            end = field.Length;
        }

        string name = System.Text.Encoding.UTF8.GetString(field, 0, end);

        // The name field is text. Anything else means the two ends disagree about what comes first
        // on the wire, which is a version mismatch, not a bad device.
        if (name.Any(c => char.IsControl(c)))
        {
            throw new InvalidDataException(
                "The tablet sent binary data where the device name belongs: the two apps are " +
                "speaking different versions of the handshake. Update both.");
        }

        return name;
    }

    /// <summary>
    /// Reads and decodes until the peer stops, the token is cancelled, or the stream desynchronises.
    /// Completes normally when the peer closes cleanly; the reason ends up in <see cref="EndReason"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);

        string reason = "stopped";
        int width = 0;
        int height = 0;
        bool started = false;

        try
        {
            await using IAsyncEnumerator<StreamMessage> messages =
                _reader.ReadMessagesAsync(_stream, linked.Token).GetAsyncEnumerator(linked.Token);

            while (await messages.MoveNextAsync().ConfigureAwait(false))
            {
                StreamMessage message = messages.Current;

                if (message.Kind == StreamMessageKind.Session)
                {
                    SessionGeometry geometry = message.Session;
                    if (geometry.Width <= 0 || geometry.Height <= 0)
                    {
                        continue;
                    }

                    width = geometry.Width;
                    height = geometry.Height;
                    Stats.SetGeometry(width, height);

                    if (!started)
                    {
                        started = true;
                        _presenter.OnSessionStarted(DeviceName, width, height);
                    }

                    continue;
                }

                VideoPacket packet = message.Packet;

                if (packet.IsConfig)
                {
                    WriteDump(packet.Payload);
                    _decoder.SetParameterSets(packet.Payload);
                    Stats.SetBackend(_decoder.Backend);
                    Stats.SetCodec(_decoder.Codec);
                    continue;
                }

                Stats.OnPacketReceived(packet.Payload.Length);
                Stats.NoteWindowFrame(packet.Payload.Length);
                Stats.RollWindow();

                // How much video is already sitting in the socket, in milliseconds. This is the
                // number the user experiences as lag, and it is measured before the decision to
                // skip, so a session that is falling behind shows it rather than hiding it.
                long backlog = BacklogBytes();
                Stats.ObserveQueueDelay(backlog * 1000.0 / BytesPerSecond());

                if (ShouldSkip(packet, backlog))
                {
                    Stats.OnPacketSkipped();
                    continue;
                }

                WriteDump(packet.Payload);

                long arrival = Stopwatch.GetTimestamp();
                try
                {
                    DecodeOnce(packet, arrival);
                }
                catch (DecodeException) when (!_decoder.HasSwitchedCodec && _decoder.TrySwitchCodec())
                {
                    // One retry with the other codec. A wrong guess costs one frame; refusing to
                    // guess costs the session.
                    Stats.OnDecodeError();
                    Stats.SetBackend(_decoder.Backend + $" (switched to {_decoder.Codec})");
                    DecodeOnce(packet, arrival);
                }
                catch (DecodeException) when (_decoder.RecoverOnBadData())
                {
                    // A rejected access unit is a hiccup, not a reason to end the session. Rebuild
                    // the decoder and resume at the next keyframe; the picture returns within a
                    // second instead of the user having to press Start again.
                    Stats.OnDecodeError();
                    Stats.SetBackend(_decoder.Backend + $" (reopened after {_decoder.ConsecutiveRejects} rejects)");
                }
                catch (DecodeException)
                {
                    Stats.OnDecodeError();
                    throw;
                }
            }

            // The decoder may still hold frames, especially with frame threading on. Drain before
            // calling the session over, or the tail of the stream is silently lost.
            long flushArrival = Stopwatch.GetTimestamp();
            _decoder.Flush(frame =>
            {
                Stats.OnFrameDecoded(Stopwatch.GetElapsedTime(flushArrival).TotalMilliseconds);
                _presenter.Present(frame);
            });

            reason = "the device closed the stream";
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            reason = "stopped";
        }
        catch (EndOfStreamException ex)
        {
            reason = $"the stream desynchronised ({ex.Message}); a reconnect is required";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Closing the socket to stop a session makes every error here look like a fault. It is
            // not: the user asked for this.
            reason = _abort.IsCancellationRequested ? "stopped" : ex.Message;
        }
        catch (DecodeException ex)
        {
            reason = $"decode failed: {ex.Message}";
        }
        finally
        {
            EndReason = reason;
            Stats.RollWindow();
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _presenter.OnSessionEnded(reason);
            }
        }
    }

    /// <summary>
    /// True when this packet should be thrown away instead of decoded.
    ///
    /// The case this exists for: the PC cannot decode 2560x1708 as fast as the tablet produces it.
    /// TCP keeps delivering, so the socket fills with frames that are already late by the time
    /// their turn comes; decoding them makes the backlog worse, and the picture drifts further
    /// behind every second. Skipping to the next keyframe caps the delay at one keyframe interval
    /// while the display simply holds the last good frame.
    ///
    /// A keyframe is never skipped: it is the only thing that can restore the picture, and it is
    /// also the frame the decoder is waiting for once skipping has started.
    /// </summary>
    private bool ShouldSkip(VideoPacket packet, long backlogBytes)
    {
        if (packet.IsConfig)
        {
            return false;
        }

        if (packet.IsKeyFrame)
        {
            _skipping = false;
            return false;
        }

        if (_skipping)
        {
            // Already waiting for a keyframe: do not even look at the backlog, the answer cannot
            // change until one arrives.
            return true;
        }

        if (backlogBytes <= _backlogBudgetBytes)
        {
            return false;
        }

        _skipping = true;
        _decoder.SkipUntilKeyFrame();
        return true;
    }

    private void WriteDump(byte[] payload)
    {
        if (_dump is null)
        {
            return;
        }

        try
        {
            _dump.Write(payload, 0, payload.Length);
        }
        catch (IOException)
        {
            // Diagnostics must never be the reason a session fails.
        }
    }

    private void DecodeOnce(VideoPacket packet, long arrival)
    {
        _decoder.Decode(packet.Payload, packet.Pts, arrival, frame =>
        {
            double decodeMs = Stopwatch.GetElapsedTime(arrival).TotalMilliseconds;
            Stats.OnFrameDecoded(decodeMs);
            _presenter.Present(frame);
        }, packet.IsKeyFrame);
    }

    /// <summary>
    /// Stops the session from another thread. Safe to call more than once.
    ///
    /// Cancelling the token is not enough on its own: a read that is already blocked on the socket
    /// only returns when bytes arrive or the socket closes, and a tablet showing a still picture may
    /// not send a byte for a long time. Closing the transport is what makes "Stop" mean stopped -
    /// on both ends, because the tablet sees the FIN and returns to listening.
    /// </summary>
    public void Stop()
    {
        if (!_abort.IsCancellationRequested)
        {
            _abort.Cancel();
        }

        CloseTransport();
    }

    /// <summary>Shuts the socket down, unblocking any pending read. Idempotent.</summary>
    public void CloseTransport()
    {
        if (Interlocked.Exchange(ref _transportClosed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_client.Client is { Connected: true } socket)
            {
                socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already gone. Closing below is still correct.
        }

        try
        {
            _client.Close();
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();

        _decoder.Dispose();

        if (_dump is not null)
        {
            try
            {
                await _dump.FlushAsync().ConfigureAwait(false);
                await _dump.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The dump is a diagnostic; losing its tail is not a failure.
            }
        }

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The socket was already closed by Stop.
        }

        _abort.Dispose();
    }
}
