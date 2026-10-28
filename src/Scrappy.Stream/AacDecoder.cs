using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Scrappy.Stream;

/// <summary>Raised when audio cannot be decoded or played. Never fatal to a video session.</summary>
public sealed class AudioException(string message) : Exception(message);

/// <summary>
/// Decodes the AAC access units the tablet sends into interleaved 16-bit PCM.
///
/// AAC is used because MediaCodec has an AAC encoder on every Android device that can run the app,
/// and because 128 kbps of AAC is the same audio as 1.5 Mbps of raw PCM, which matters on the weak
/// Wi-Fi links that make the video stutter in the first place.
///
/// The decoder produces planar float (FLTP) on every build of FFmpeg worth supporting, but the
/// sample format is read from the frame rather than assumed: a build that hands back packed or
/// 16-bit audio would otherwise be interpreted as noise. The conversion to interleaved 16-bit is
/// done here rather than through libswresample, because one multiply and a clamp per sample is
/// cheaper than another native library to find, ship and version.
/// </summary>
public sealed unsafe class AacDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private byte[]? _extradata;
    private bool _disposed;

    public AacDecoder(int sampleRate = 48_000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public bool IsOpen => _context is not null;

    /// <summary>Total sample frames decoded, for the status line and the tests.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>
    /// The AudioSpecificConfig from the tablet's config packet. Two bytes for AAC-LC, and without
    /// them libavcodec has no idea what the profile or sample rate is.
    /// </summary>
    public void SetExtradata(byte[] config)
    {
        if (config.Length == 0)
        {
            return;
        }

        _extradata = config;
        Close();
    }

    /// <summary>
    /// Decodes one access unit into <paramref name="destination"/>. Returns the number of sample
    /// frames written, which is zero for an access unit that produced nothing.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> accessUnit, short[] destination)
    {
        if (accessUnit.Length == 0)
        {
            return 0;
        }

        EnsureOpen();

        fixed (byte* payload = accessUnit)
        {
            _packet->data = payload;
            _packet->size = accessUnit.Length;

            int sent = ffmpeg.avcodec_send_packet(_context, _packet);
            if (sent < 0)
            {
                throw new AudioException($"The AAC decoder rejected a packet (error {sent}).");
            }
        }

        int written = 0;

        while (true)
        {
            int received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (received < 0)
            {
                throw new AudioException($"The AAC decoder failed mid-frame (error {received}).");
            }

            written += Convert(_frame, destination.AsSpan(written));
            FramesDecoded += _frame->nb_samples;
            ffmpeg.av_frame_unref(_frame);
        }

        return written;
    }

    /// <summary>Interleaves and scales one decoded frame. Returns sample frames written.</summary>
    private int Convert(AVFrame* frame, Span<short> destination)
    {
        int samples = frame->nb_samples;
        int channels = Math.Min(frame->channels, Channels);

        if (samples <= 0 || channels <= 0)
        {
            return 0;
        }

        if (destination.Length < samples * Channels)
        {
            throw new AudioException(
                $"Audio buffer too small: {destination.Length} shorts for {samples} frames of {Channels} channels.");
        }

        var format = (AVSampleFormat)frame->format;

        switch (format)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    float* plane = (float*)frame->data[(uint)channel];
                    for (int i = 0; i < samples; i++)
                    {
                        destination[i * Channels + channel] = FromFloat(plane[i]);
                    }
                }

                break;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
            {
                float* packed = (float*)frame->data[0];
                for (int i = 0; i < samples * channels; i++)
                {
                    destination[i] = FromFloat(packed[i]);
                }

                break;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    short* plane = (short*)frame->data[(uint)channel];
                    for (int i = 0; i < samples; i++)
                    {
                        destination[i * Channels + channel] = plane[i];
                    }
                }

                break;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            {
                short* packed = (short*)frame->data[0];
                for (int i = 0; i < samples * channels; i++)
                {
                    destination[i] = packed[i];
                }

                break;
            }

            default:
                throw new AudioException($"Unsupported decoded sample format: {format}.");
        }

        return samples;
    }

    private static short FromFloat(float value)
    {
        // Clamp before scaling: AAC can decode slightly above 1.0, and a cast would wrap that into
        // a loud click of the opposite sign.
        float clamped = Math.Clamp(value, -1f, 1f);
        return (short)Math.Clamp((int)MathF.Round(clamped * 32767f), short.MinValue, short.MaxValue);
    }

    private void EnsureOpen()
    {
        if (_context is not null)
        {
            return;
        }

        AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name("aac");
        if (codec is null)
        {
            throw new AudioException("This FFmpeg build has no AAC decoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null)
        {
            throw new AudioException("Could not allocate an AAC decoder context.");
        }

        _context->sample_rate = SampleRate;
        _context->channels = Channels;
        _context->channel_layout = (ulong)(Channels == 1 ? ffmpeg.AV_CH_LAYOUT_MONO : ffmpeg.AV_CH_LAYOUT_STEREO);

        if (_extradata is { Length: > 0 })
        {
            byte* buffer = (byte*)ffmpeg.av_mallocz((ulong)_extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            if (buffer is null)
            {
                throw new AudioException("Could not allocate audio extradata.");
            }

            Marshal.Copy(_extradata, 0, (IntPtr)buffer, _extradata.Length);
            _context->extradata = buffer;
            _context->extradata_size = _extradata.Length;
        }

        int opened = ffmpeg.avcodec_open2(_context, codec, null);
        if (opened < 0)
        {
            throw new AudioException($"Could not open the AAC decoder (error {opened}).");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame is null || _packet is null)
        {
            throw new AudioException("Could not allocate audio decoder buffers.");
        }

        // The stream itself is authoritative about rate and channels; trust it once open.
        if (_context->sample_rate > 0)
        {
            SampleRate = _context->sample_rate;
        }

        if (_context->channels > 0)
        {
            Channels = _context->channels;
        }
    }

    private void Close()
    {
        if (_context is not null)
        {
            fixed (AVCodecContext** context = &_context)
            {
                ffmpeg.avcodec_free_context(context);
            }
        }

        if (_frame is not null)
        {
            fixed (AVFrame** frame = &_frame)
            {
                ffmpeg.av_frame_free(frame);
            }
        }

        if (_packet is not null)
        {
            fixed (AVPacket** packet = &_packet)
            {
                ffmpeg.av_packet_free(packet);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }
}
