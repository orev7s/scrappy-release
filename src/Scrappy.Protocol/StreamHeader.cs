using System.Buffers.Binary;

namespace Scrappy.Protocol;

/// <summary>
/// Constants and parsers for scrcpy's 12-byte stream header.
/// See docs/wire-format.md for the derivation from upstream source.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>NUL-padded UTF-8 device name sent once, before any header.</summary>
    public const int DeviceNameLength = 64;

    /// <summary>Every stream header is exactly 12 bytes.</summary>
    public const int HeaderSize = 12;

    /// <summary>Codec configuration payload (SPS/PPS), not a frame.</summary>
    public const ulong FlagConfig = 1UL << 62;

    /// <summary>Key frame.</summary>
    public const ulong FlagKeyFrame = 1UL << 61;

    /// <summary>Bits 0..60 carry the presentation timestamp.</summary>
    public const ulong PtsMask = FlagKeyFrame - 1;

    /// <summary>Top bit of byte 0 marks a session header rather than a packet header.</summary>
    public const byte SessionMarker = 0x80;
}

/// <summary>Mid-stream geometry. Carries no payload.</summary>
public readonly record struct SessionGeometry(int Width, int Height, bool ClientResized);

/// <summary>A decoded packet header. The payload is read separately.</summary>
public readonly record struct PacketHeader(ulong Pts, bool IsConfig, bool IsKeyFrame, int Length);

public static class StreamHeader
{
    /// <summary>True if this header describes the stream geometry rather than a packet.</summary>
    public static bool IsSession(ReadOnlySpan<byte> header)
    {
        RequireHeaderSize(header);
        return (header[0] & ProtocolConstants.SessionMarker) != 0;
    }

    public static SessionGeometry ParseSession(ReadOnlySpan<byte> header)
    {
        RequireHeaderSize(header);
        if (!IsSession(header))
        {
            throw new InvalidDataException("Not a session header.");
        }

        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        bool resized = (header[3] & 1) != 0;
        return new SessionGeometry(width, height, resized);
    }

    public static PacketHeader ParsePacket(ReadOnlySpan<byte> header)
    {
        RequireHeaderSize(header);
        if (IsSession(header))
        {
            throw new InvalidDataException("Header is a session header, not a packet header.");
        }

        ulong ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);

        if (length == 0)
        {
            // Upstream treats zero length as fatal: there is no resync path.
            throw new InvalidDataException("Invalid packet length: 0.");
        }

        bool isConfig = (ptsFlags & ProtocolConstants.FlagConfig) != 0;
        bool isKeyFrame = (ptsFlags & ProtocolConstants.FlagKeyFrame) != 0;
        ulong pts = ptsFlags & ProtocolConstants.PtsMask;
        return new PacketHeader(pts, isConfig, isKeyFrame, (int)length);
    }

    /// <summary>Builds a packet header. Used by the sender and by tests.</summary>
    public static void WritePacketHeader(Span<byte> destination, ulong pts, bool isConfig, bool isKeyFrame, int length)
    {
        RequireHeaderSize(destination);
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ulong ptsFlags = pts & ProtocolConstants.PtsMask;
        if (isConfig)
        {
            ptsFlags |= ProtocolConstants.FlagConfig;
        }

        if (isKeyFrame)
        {
            ptsFlags |= ProtocolConstants.FlagKeyFrame;
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], ptsFlags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], (uint)length);
    }

    /// <summary>Builds a session header. Width and height go big-endian at offsets 4 and 8.</summary>
    public static void WriteSessionHeader(Span<byte> destination, int width, int height, bool clientResized)
    {
        RequireHeaderSize(destination);
        destination.Clear();
        destination[0] = ProtocolConstants.SessionMarker;
        if (clientResized)
        {
            destination[3] |= 1;
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], (uint)height);
    }

    private static void RequireHeaderSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < ProtocolConstants.HeaderSize)
        {
            throw new ArgumentException(
                $"Header must be at least {ProtocolConstants.HeaderSize} bytes, got {header.Length}.",
                nameof(header));
        }
    }
}
