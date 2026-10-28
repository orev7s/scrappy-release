using System.Buffers;

namespace Scrappy.Stream;

/// <summary>
/// One decoded, converted frame: 32-bit BGRA, top-down, tightly packed per row.
///
/// The buffer is rented. Whoever receives the frame owns it and must call <see cref="Release"/>
/// exactly once when done. Ownership transfer is explicit because a copy of a 2560x1708 frame is
/// 17 MB, and paying that twice per frame at 60 fps is 2 GB/s of pointless memory traffic.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 4);

    private byte[]? _buffer;
    private int _released;

    private VideoFrame(byte[] buffer, int width, int height, ulong pts, long arrivalTimestamp)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Pts = pts;
        ArrivalTimestamp = arrivalTimestamp;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    /// <summary>Presentation timestamp from the wire, in the encoder's units. Informational.</summary>
    public ulong Pts { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> at packet arrival.</summary>
    public long ArrivalTimestamp { get; }

    public Span<byte> Pixels => _buffer.AsSpan(0, Stride * Height);

    public static VideoFrame Rent(int width, int height, ulong pts, long arrivalTimestamp)
    {
        int length = width * height * 4;
        byte[] buffer = Pool.Rent(length);
        return new VideoFrame(buffer, width, height, pts, arrivalTimestamp);
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            Pool.Return(buffer);
        }
    }

    public void Dispose() => Release();
}
