using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Scrappy.Protocol;

/// <summary>How the synthetic device produces frames.</summary>
public enum SyntheticSource
{
    /// <summary>Arbitrary bytes with valid headers. Exercises framing, not decoding.</summary>
    Filler,

    /// <summary>A real H.264 elementary stream, split into access units. Exercises decoding.</summary>
    Bitstream,
}

public sealed record SyntheticDeviceOptions
{
    public int Port { get; init; } = 27183;

    /// <summary>0 binds an ephemeral port; the actual port is reported by <see cref="SyntheticDevice.Port"/>.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    public int Width { get; init; } = 2560;

    public int Height { get; init; } = 1708;

    /// <summary>0 means "until cancelled".</summary>
    public int Frames { get; init; } = 120;

    public int Fps { get; init; } = 60;

    public SyntheticSource Source { get; init; } = SyntheticSource.Filler;

    /// <summary>Path to an Annex-B H.264 file. Required when <see cref="Source"/> is Bitstream.</summary>
    public string? BitstreamPath { get; init; }

    /// <summary>Device name written into the 64-byte field.</summary>
    public string DeviceName { get; init; } = "Synthetic Pad 8";
}

/// <summary>
/// A tablet that is not a tablet: it listens on TCP and speaks scrcpy's video wire format.
///
/// It exists so the receiver, the decoder and the presentation path can be built and tested
/// with no hardware attached. With <see cref="SyntheticSource.Bitstream"/> it emits a real
/// H.264 stream, so "it decoded" is a real claim rather than a framing check.
/// </summary>
public sealed class SyntheticDevice : IAsyncDisposable
{
    private readonly SyntheticDeviceOptions _options;
    private readonly TcpListener _listener;
    private readonly IReadOnlyList<AccessUnit> _units;
    private readonly byte[] _parameterSets;

    public SyntheticDevice(SyntheticDeviceOptions options)
    {
        _options = options;

        if (options.Source == SyntheticSource.Bitstream)
        {
            if (string.IsNullOrWhiteSpace(options.BitstreamPath))
            {
                throw new ArgumentException("A bitstream path is required for the Bitstream source.", nameof(options));
            }

            byte[] stream = File.ReadAllBytes(options.BitstreamPath);
            _units = AnnexB.SplitAccessUnits(stream);
            _parameterSets = AnnexB.ExtractParameterSets(stream);

            if (_units.Count == 0)
            {
                throw new InvalidDataException($"{options.BitstreamPath} contained no access units.");
            }
        }
        else
        {
            _units = [];
            _parameterSets = [];
        }

        IPAddress address = options.BindAddress is "0.0.0.0" or "any"
            ? IPAddress.Any
            : IPAddress.TryParse(options.BindAddress, out IPAddress? parsed) ? parsed : IPAddress.Any;

        _listener = new TcpListener(address, options.Port);
    }

    /// <summary>The port actually bound, which matters when the requested port was 0.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int FrameCount => _options.Frames == 0 ? 0 : Math.Min(_options.Frames, Math.Max(_units.Count, 1));

    public void Start() => _listener.Start();

    /// <summary>
    /// Accepts one client and streams <see cref="SyntheticDeviceOptions.Frames"/> frames at the
    /// requested pace. Returns the number of frames written.
    /// </summary>
    public async Task<int> ServeOnceAsync(CancellationToken cancellationToken = default)
    {
        using TcpClient peer = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        peer.NoDelay = true;

        await using NetworkStream stream = peer.GetStream();
        await stream.WriteAsync(DeviceNameField(_options.DeviceName), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(SessionBlock(_options.Width, _options.Height), cancellationToken).ConfigureAwait(false);

        byte[] config = _parameterSets.Length > 0
            ? _parameterSets
            : new byte[48];

        await stream.WriteAsync(PacketBlock(0, isConfig: true, isKeyFrame: false, config), cancellationToken)
            .ConfigureAwait(false);

        int frames = _options.Frames;

        // Fps 0 means "as fast as the socket allows": used by correctness tests that care about
        // frame integrity, not pacing.
        long frameIntervalMs = _options.Fps > 0 ? Math.Max(1, 1000 / _options.Fps) : 0;
        var clock = Stopwatch.StartNew();
        ulong ptsStep = (ulong)Math.Max(1, 1_000_000 / Math.Max(1, _options.Fps));

        int written = 0;
        for (int i = 0; frames == 0 || i < frames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] payload;
            bool key;
            if (_options.Source == SyntheticSource.Bitstream)
            {
                AccessUnit unit = _units[i % _units.Count];
                payload = unit.Payload;
                key = unit.IsKeyFrame;
            }
            else
            {
                payload = new byte[1400];
                payload.AsSpan().Fill(0x5A);
                key = i % (_options.Fps > 0 ? _options.Fps : 30) == 0;
            }

            await stream
                .WriteAsync(PacketBlock((ulong)i * ptsStep, isConfig: false, isKeyFrame: key, payload), cancellationToken)
                .ConfigureAwait(false);

            written++;

            if (frameIntervalMs > 0)
            {
                long target = (long)written * frameIntervalMs;
                long delay = target - clock.ElapsedMilliseconds;
                if (delay > 1)
                {
                    await Task.Delay((int)delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Let the receiver drain before the FIN, otherwise the tail of the stream is lost and a
        // short test looks like packet loss.
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return written;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static byte[] DeviceNameField(string name)
    {
        byte[] field = new byte[ProtocolConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes(name, 0, Math.Min(name.Length, field.Length), field, 0);
        return field;
    }

    private static byte[] SessionBlock(int width, int height)
    {
        byte[] block = new byte[ProtocolConstants.HeaderSize];
        StreamHeader.WriteSessionHeader(block, width, height, clientResized: false);
        return block;
    }

    private static byte[] PacketBlock(ulong pts, bool isConfig, bool isKeyFrame, byte[] payload)
    {
        byte[] block = new byte[ProtocolConstants.HeaderSize + payload.Length];
        StreamHeader.WritePacketHeader(block, pts, isConfig, isKeyFrame, payload.Length);
        payload.CopyTo(block, ProtocolConstants.HeaderSize);
        return block;
    }
}
