using System.Runtime.CompilerServices;
using System.Text;

namespace Scrappy.Protocol;

public enum StreamMessageKind
{
    Session,
    Packet,
}

/// <summary>One decoded frame or codec-config payload.</summary>
public readonly record struct VideoPacket(
    ulong Pts,
    bool IsKeyFrame,
    bool IsConfig,
    byte[] Payload,
    long ReceivedTimestampMs);

/// <summary>Either a geometry update or a packet. Exactly one of the two is meaningful.</summary>
public readonly record struct StreamMessage(
    StreamMessageKind Kind,
    SessionGeometry Session,
    VideoPacket Packet);

/// <summary>
/// Reads scrcpy's video stream from a socket.
/// Framing is length-prefixed with no resync marker, so any read failure is terminal:
/// the caller must drop the connection and reconnect rather than scan for the next header.
/// </summary>
public sealed class VideoStreamReader
{
    /// <summary>Guard against a corrupt or hostile length field allocating unbounded memory.</summary>
    public const int MaxPacketLength = 64 * 1024 * 1024;

    private readonly byte[] _headerBuffer = new byte[ProtocolConstants.HeaderSize];

    /// <summary>
    /// Reads the 64-byte device name that precedes the first header.
    /// Returns the name with trailing NULs removed.
    /// </summary>
    public async Task<string> ReadDeviceNameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[ProtocolConstants.DeviceNameLength];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    /// <summary>
    /// Yields geometry updates and packets until the stream ends or is cancelled.
    /// The device name must already have been consumed by <see cref="ReadDeviceNameAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<StreamMessage> ReadMessagesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int headerRead = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (headerRead == 0)
            {
                yield break; // clean end of stream
            }

            if (StreamHeader.IsSession(_headerBuffer))
            {
                SessionGeometry geometry = StreamHeader.ParseSession(_headerBuffer);
                yield return new StreamMessage(StreamMessageKind.Session, geometry, default);
                continue;
            }

            PacketHeader header = StreamHeader.ParsePacket(_headerBuffer);
            if (header.Length > MaxPacketLength)
            {
                throw new InvalidDataException(
                    $"Packet length {header.Length} exceeds sanity limit {MaxPacketLength}.");
            }

            byte[] payload = new byte[header.Length];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            long now = Environment.TickCount64;
            yield return new StreamMessage(
                StreamMessageKind.Packet,
                default,
                new VideoPacket(header.Pts, header.IsKeyFrame, header.IsConfig, payload, now));
        }
    }

    /// <summary>Returns the number of header bytes read: 0 for a clean end, 12 otherwise.</summary>
    private async Task<int> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < ProtocolConstants.HeaderSize)
        {
            int read = await stream
                .ReadAsync(_headerBuffer.AsMemory(total, ProtocolConstants.HeaderSize - total), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                if (total == 0)
                {
                    return 0; // peer closed between packets: expected on stop
                }

                throw new EndOfStreamException(
                    $"Stream ended mid-header after {total} of {ProtocolConstants.HeaderSize} bytes.");
            }

            total += read;
        }

        return total;
    }
}
