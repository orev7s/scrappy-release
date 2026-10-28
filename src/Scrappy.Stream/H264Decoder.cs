using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Scrappy.Protocol;

namespace Scrappy.Stream;

/// <summary>
/// Decodes the H.264 or H.265 elementary stream the device sends into BGRA frames.
///
/// The codec is detected from the parameter sets rather than trusted from settings: the tablet
/// decides what its encoder supports, and it may ignore the requested codec. Asking the H.264
/// decoder to open H.265 data produces AVERROR_INVALIDDATA, which is a true statement and a
/// useless diagnosis - it was the visible symptom of a mismatch nobody could see.
///
/// The codec-configuration packet (SPS/PPS) becomes <c>AVCodecContext.extradata</c>, exactly as
/// scrcpy's own client does it in <c>app/src/decoder.c</c>. Everything after that is one packet
/// per encoded frame, which is what the wire format promises.
///
/// Hardware decode is not wired up yet. <see cref="Backend"/> says what actually ran, so the UI
/// can never claim hardware decoding that did not happen.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private readonly string _codecHint;
    private readonly string _hwdecPreference;
    private string? _detectedCodec;
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _scaler;
    private int _scalerSourceFormat = -1;
    private int _scalerSourceWidth;
    private int _scalerSourceHeight;
    private byte[]? _extradata;
    private bool _switched;
    private bool _disposed;
    private bool _awaitingKeyFrame;

    public H264Decoder(string codecHint = "h264", string hwdecPreference = "disabled", bool lowLatency = true)
    {
        _codecHint = codecHint;
        _hwdecPreference = hwdecPreference;
        LowLatency = lowLatency;
        Backend = "software";
    }

    /// <summary>What is actually decoding: "software" today, "d3d11va" once hardware is wired up.</summary>
    public string Backend { get; private set; }

    /// <summary>The codec in use: detected from the stream when possible, otherwise the hint.</summary>
    public string Codec => _detectedCodec ?? NormaliseHint(_codecHint);

    public bool IsOpen => _context is not null;

    public bool LowLatency { get; }

    /// <summary>
    /// Reopens with the other codec, once. Used when the first packet is rejected: with no config
    /// packet there is nothing to detect from, and a hint of "h265" against an H.264 stream fails
    /// with AVERROR_INVALIDDATA rather than anything self-explanatory.
    /// </summary>
    public bool TrySwitchCodec()
    {
        string alternate = Codec == "hevc" ? "h264" : "hevc";
        if (Codec == alternate)
        {
            return false;
        }

        _detectedCodec = alternate;
        _switched = true;
        Close();
        return true;
    }

    /// <summary>True once the codec has been switched, so the switch cannot loop.</summary>
    public bool HasSwitchedCodec => _switched;

    /// <summary>
    /// Reopens the decoder after a rejected access unit so the session survives it. One bad packet
    /// used to end the stream and leave the window on "decode failed"; the honest recovery is to
    /// drop the packet, rebuild the decoder, and wait for the next keyframe.
    ///
    /// Returns false once the rejects stop looking like a one-off, so a stream that this build
    /// genuinely cannot decode fails with a message instead of looping.
    /// </summary>
    public bool RecoverOnBadData()
    {
        if (ConsecutiveRejects > MaxConsecutiveRejects)
        {
            return false;
        }

        Recoveries++;
        _awaitingKeyFrame = true;
        Close();
        return true;
    }

    /// <summary>Five rejects in a row is a systematic mismatch, not a glitch.</summary>
    public const int MaxConsecutiveRejects = 5;

    /// <summary>Number of decoder worker threads FFmpeg chose. 0 before open.</summary>
    public int ThreadCount { get; private set; }

    /// <summary>
    /// Set after a reopen: the decoder has no reference frame, so nothing decodes until the next
    /// keyframe. Frames in between are skipped here rather than fed to FFmpeg to be rejected.
    /// </summary>
    public bool AwaitingKeyFrame => _awaitingKeyFrame;

    /// <summary>Rejects in a row since the last accepted access unit.</summary>
    public int ConsecutiveRejects { get; private set; }

    /// <summary>How many times the decoder has reopened itself mid-session.</summary>
    public int Recoveries { get; private set; }

    /// <summary>Access units dropped while waiting for a keyframe.</summary>
    public int SkippedWhileWaiting { get; private set; }

    /// <summary>
    /// Tells the decoder that the frames it is about to receive no longer continue the ones it
    /// just decoded - because the receiver deliberately skipped some to stay on time.
    ///
    /// Without this the next inter frame is decoded against a reference that never arrived, and
    /// libavcodec produces a screenful of garbage plus a stream of "error while decoding MB"
    /// messages. Skipping to the next keyframe costs one frame of freeze and no corruption.
    /// </summary>
    public void SkipUntilKeyFrame() => _awaitingKeyFrame = true;

    /// <summary>
    /// Stores the parameter sets that arrive in the config packet. Opening is deferred until the
    /// first frame, so a stream that repeats its config mid-session can be handled by reopening.
    /// </summary>
    public void SetParameterSets(byte[] parameterSets)
    {
        if (parameterSets.Length == 0)
        {
            return;
        }

        if (_extradata is not null && _extradata.AsSpan().SequenceEqual(parameterSets))
        {
            return;
        }

        _extradata = parameterSets;

        string? detected = DetectCodec(parameterSets);
        if (detected is not null)
        {
            _detectedCodec = detected;
        }

        // A config packet mid-session means the encoder was restarted - a rotation, a resolution
        // change, a reconnect underneath. Keeping the old context would decode the new stream with
        // the old parameter sets, which fails as INVALIDDATA on every frame.
        if (_context is not null)
        {
            Close();
            _awaitingKeyFrame = true;
        }
    }

    /// <summary>
    /// Decodes one access unit. <paramref name="onFrame"/> is called for every frame the decoder
    /// emits and owns the frame it receives.
    /// </summary>
    public void Decode(
        ReadOnlySpan<byte> accessUnit,
        ulong pts,
        long arrivalTimestamp,
        Action<VideoFrame> onFrame,
        bool isKeyFrame = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (accessUnit.Length == 0)
        {
            return;
        }

        // Without a config packet there is nothing to detect from, so read the first access unit.
        // Guessing from the setting alone is what produced "rejected a 9904-byte access unit":
        // a real, well-formed frame offered to the wrong decoder.
        if (_detectedCodec is null)
        {
            string? sniffed = SniffCodec(accessUnit);
            if (sniffed is not null)
            {
                _detectedCodec = sniffed;
            }
        }

        if (_awaitingKeyFrame)
        {
            if (!isKeyFrame)
            {
                SkippedWhileWaiting++;
                return;
            }

            _awaitingKeyFrame = false;
        }

        if (AnnexB.FindStartCodes(accessUnit).Count == 0)
        {
            ConsecutiveRejects++;
            throw new DecodeException(
                $"The packet is not an Annex-B access unit: no start code in {accessUnit.Length} bytes. " +
                "The stream is out of step, which means the two apps disagree about framing.");
        }

        EnsureOpen();

        if (ffmpeg.av_new_packet(_packet, accessUnit.Length) < 0)
        {
            throw new InvalidOperationException("FFmpeg could not allocate a packet buffer.");
        }

        accessUnit.CopyTo(new Span<byte>(_packet->data, accessUnit.Length));
        _packet->pts = (long)pts;

        // With frame threading the decoder's input queue can be full. A rejected packet is not a
        // lost packet: pull the frames it is holding, then offer the same packet again. Ignoring
        // EAGAIN here silently drops frames.
        int sent;
        while ((sent = ffmpeg.avcodec_send_packet(_context, _packet)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            Drain(onFrame, pts, arrivalTimestamp);
        }

        ffmpeg.av_packet_unref(_packet);

        if (sent < 0)
        {
            ConsecutiveRejects++;
            throw new DecodeException(
                $"The {Codec} decoder rejected a {accessUnit.Length}-byte access unit " +
                $"(NAL {DescribeNalTypes(accessUnit)}, error {sent}). " +
                (sent == -1094995529
                    ? "That is 'invalid data': the bytes are not the codec this decoder was opened for, " +
                      "or the parameter sets do not match them."
                    : "The decoder cannot continue with this stream in place."));
        }

        ConsecutiveRejects = 0;
        Drain(onFrame, pts, arrivalTimestamp);
    }

    /// <summary>
    /// Drains the decoder at end of stream. A null packet is the flush marker: without it the
    /// frames still in the reorder buffer (one per B-frame of delay) are simply never emitted.
    /// </summary>
    public void Flush(Action<VideoFrame> onFrame)
    {
        if (_context is null)
        {
            return;
        }

        ffmpeg.avcodec_send_packet(_context, null);
        Drain(onFrame, 0, 0);
    }

    private void Drain(Action<VideoFrame> onFrame, ulong pts, long arrivalTimestamp)
    {
        while (true)
        {
            int received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (received < 0)
            {
                throw new DecodeException($"Decoder failed while producing a frame (error {received}).");
            }

            try
            {
                onFrame(Convert(_frame, pts, arrivalTimestamp));
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private VideoFrame Convert(AVFrame* source, ulong pts, long arrivalTimestamp)
    {
        int width = source->width;
        int height = source->height;
        VideoFrame frame = VideoFrame.Rent(width, height, pts, arrivalTimestamp);

        if (_scaler is null ||
            _scalerSourceFormat != source->format ||
            _scalerSourceWidth != width ||
            _scalerSourceHeight != height)
        {
            if (_scaler is not null)
            {
                ffmpeg.sws_freeContext(_scaler);
            }

            _scaler = ffmpeg.sws_getContext(
                width, height, (AVPixelFormat)source->format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);

            if (_scaler is null)
            {
                frame.Release();
                throw new DecodeException($"No scaler for pixel format {(AVPixelFormat)source->format}.");
            }

            _scalerSourceFormat = source->format;
            _scalerSourceWidth = width;
            _scalerSourceHeight = height;
        }

        fixed (byte* destination = frame.Pixels)
        {
            // FFmpeg.AutoGen's 4.4 binding takes managed arrays of plane pointers and strides.
            byte*[] sourcePlanes = [source->data[0], source->data[1], source->data[2], source->data[3]];
            int[] sourceStrides = [source->linesize[0], source->linesize[1], source->linesize[2], source->linesize[3]];
            byte*[] destinationPlanes = [destination, null, null, null];
            int[] destinationStrides = [frame.Stride, 0, 0, 0];

            ffmpeg.sws_scale(
                _scaler,
                sourcePlanes, sourceStrides, 0, height,
                destinationPlanes, destinationStrides);
        }

        return frame;
    }

    private void EnsureOpen()
    {
        if (_context is not null)
        {
            return;
        }

        string codecName = Codec;
        AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(codecName);
        if (codec is null)
        {
            throw new DecodeException($"This FFmpeg build has no '{codecName}' decoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null)
        {
            throw new DecodeException("Could not allocate a decoder context.");
        }

        // Thread count 0 lets FFmpeg pick. Frame threading hides latency but buys throughput; at a
        // 60 fps target the throughput matters more than a frame of delay, so LOW_DELAY is opt-in.
        _context->thread_count = 0;
        if (LowLatency)
        {
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        }

        _context->pkt_timebase = new AVRational { num = 1, den = 90000 };

        if (_extradata is { Length: > 0 })
        {
            byte* buffer = (byte*)ffmpeg.av_mallocz((ulong)_extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            if (buffer is null)
            {
                throw new DecodeException("Could not allocate extradata.");
            }

            Marshal.Copy(_extradata, 0, (IntPtr)buffer, _extradata.Length);
            _context->extradata = buffer;
            _context->extradata_size = _extradata.Length;
        }

        int opened = ffmpeg.avcodec_open2(_context, codec, null);
        if (opened < 0)
        {
            throw new DecodeException($"Could not open the {codecName} decoder (error {opened}).");
        }

        ThreadCount = _context->thread_count;
        Backend = DescribeBackend();

        if (_frame is null)
        {
            _frame = ffmpeg.av_frame_alloc();
        }

        if (_packet is null)
        {
            _packet = ffmpeg.av_packet_alloc();
        }

        if (_frame is null || _packet is null)
        {
            throw new DecodeException("Could not allocate decoder buffers.");
        }
    }

    /// <summary>
    /// Reads the codec from the first parameter set in the config packet. H.264 SPS/PPS are NAL
    /// types 7 and 8 in the low 5 bits of the header; H.265 VPS/SPS/PPS are types 32, 33 and 34 in
    /// the top 6 bits of a two-byte header. The two patterns cannot collide.
    /// </summary>
    private static string? DetectCodec(ReadOnlySpan<byte> parameterSets)
    {
        for (int i = 0; i + 4 < parameterSets.Length; i++)
        {
            if (parameterSets[i] != 0 || parameterSets[i + 1] != 0 || parameterSets[i + 2] != 1)
            {
                continue;
            }

            byte header = parameterSets[i + 3];

            int h264Type = header & 0x1F;
            if (h264Type is 7 or 8)
            {
                return "h264";
            }

            int hevcType = header >> 1;
            if (hevcType is 32 or 33 or 34)
            {
                return "hevc";
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads the codec from a single access unit, without a config packet.
    ///
    /// H.264 puts the NAL type in the low five bits of the header byte; HEVC puts it in the top
    /// six bits of a two-byte header. The sets do not overlap for the types that matter here -
    /// parameter sets, access-unit delimiters, and the IDR/CRA slices that start a stream.
    /// </summary>
    internal static string? SniffCodec(ReadOnlySpan<byte> accessUnit)
    {
        foreach (int offset in AnnexB.FindStartCodes(accessUnit))
        {
            int nalStart = offset + 3;
            if (nalStart >= accessUnit.Length)
            {
                break;
            }

            int header = accessUnit[nalStart];
            int hevcType = header >> 1;

            if (hevcType is 32 or 33 or 34 or 35)
            {
                return "hevc"; // VPS / SPS / PPS / AUD
            }

            int h264Type = header & 0x1F;
            if (h264Type is 7 or 8 or 9)
            {
                return "h264"; // SPS / PPS / AUD
            }

            if (hevcType is >= 16 and <= 21)
            {
                return "hevc"; // BLA / IDR / CRA
            }

            if (h264Type is 5 or 1)
            {
                return "h264"; // IDR or non-IDR slice
            }
        }

        return null;
    }

    /// <summary>NAL types in the packet, for the error message. Two names are enough to diagnose.</summary>
    private static string DescribeNalTypes(ReadOnlySpan<byte> accessUnit)
    {
        var types = new List<string>(4);
        foreach (int offset in AnnexB.FindStartCodes(accessUnit))
        {
            int nalStart = offset + 3;
            if (nalStart >= accessUnit.Length)
            {
                break;
            }

            int header = accessUnit[nalStart];
            types.Add($"h264:{(header & 0x1F).ToString(System.Globalization.CultureInfo.InvariantCulture)}/" +
                      $"hevc:{header >> 1}");

            if (types.Count == 2)
            {
                break;
            }
        }

        return types.Count == 0 ? "none found" : string.Join(", ", types);
    }

    private static string NormaliseHint(string hint) => hint.ToLowerInvariant() switch
    {
        "h265" or "hevc" => "hevc",
        "av1" => "av1",
        "vp8" => "vp8",
        "vp9" => "vp9",
        _ => "h264",
    };

    private string DescribeBackend()
    {
        // No hwaccel is configured: avcodec_open2 above ran with a plain software context. Report
        // the preference so a mismatch between the setting and reality is visible instead of implied.
        if (_hwdecPreference is "disabled" or "" or null)
        {
            return "software";
        }

        return $"software (hwdec '{_hwdecPreference}' requested but not wired up)";
    }

    private void Close()
    {
        if (_context is null)
        {
            return;
        }

        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }

        _context = null;
        _scaler = null;
        _scalerSourceFormat = -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();

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

        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }
    }
}

/// <summary>A decode failure that should end the session rather than be retried in place.</summary>
public sealed class DecodeException : Exception
{
    public DecodeException(string message) : base(message)
    {
    }
}
