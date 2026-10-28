using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

/// <summary>
/// Encodes a sine tone as AAC, so the decoder can be tested on data that came from a real encoder
/// rather than from a fixture nobody can inspect.
///
/// The tone is 440 Hz at a quarter of full scale, which makes two assertions meaningful: the
/// decoded length must match the encoded length, and the decoded level must be close to 0.25/sqrt(2).
/// Silence, noise and a truncated packet all fail one of those.
/// </summary>
internal static unsafe class AacTestSource
{
    public sealed record Encoded(byte[] AudioSpecificConfig, List<byte[]> AccessUnits, int SampleRate, int Channels)
    {
        public double Seconds => SampleRate == 0 ? 0 : AccessUnits.Count * 1024.0 / SampleRate;
    }

    public static Encoded EncodeTone(
        int sampleRate = 48_000,
        int channels = 2,
        int seconds = 2,
        double frequency = 440,
        double amplitude = 0.25,
        int bitrate = 128_000)
    {
        // The AAC encoder is chatty about quality metrics; this is a test, not a transcode.
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null)
        {
            throw new InvalidOperationException("This FFmpeg build has no AAC encoder, so the audio test cannot run.");
        }

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        if (context is null)
        {
            throw new InvalidOperationException("Could not allocate an AAC encoder context.");
        }

        try
        {
            context->bit_rate = bitrate;
            context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            context->sample_rate = sampleRate;
            context->channels = channels;
            context->channel_layout = (ulong)(channels == 1 ? ffmpeg.AV_CH_LAYOUT_MONO : ffmpeg.AV_CH_LAYOUT_STEREO);
            context->profile = ffmpeg.FF_PROFILE_AAC_LOW;

            int opened = ffmpeg.avcodec_open2(context, codec, null);
            if (opened < 0)
            {
                throw new InvalidOperationException($"Could not open the AAC encoder (error {opened}).");
            }

            int frameSize = context->frame_size > 0 ? context->frame_size : 1024;
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVPacket* packet = ffmpeg.av_packet_alloc();

            if (frame is null || packet is null)
            {
                throw new InvalidOperationException("Could not allocate encoder buffers.");
            }

            try
            {
                frame->nb_samples = frameSize;
                frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                frame->sample_rate = sampleRate;
                frame->channel_layout = context->channel_layout;

                if (ffmpeg.av_frame_get_buffer(frame, 0) < 0)
                {
                    throw new InvalidOperationException("Could not allocate an audio frame buffer.");
                }

                var units = new List<byte[]>();
                int totalFrames = (int)((long)sampleRate * seconds / frameSize);
                long sampleIndex = 0;

                for (int index = 0; index < totalFrames; index++)
                {
                    if (ffmpeg.av_frame_make_writable(frame) < 0)
                    {
                        throw new InvalidOperationException("The encoder frame is not writable.");
                    }

                    for (int channel = 0; channel < channels; channel++)
                    {
                        float* plane = (float*)frame->data[(uint)channel];
                        for (int i = 0; i < frameSize; i++)
                        {
                            double t = (sampleIndex + i) / (double)sampleRate;
                            plane[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * t));
                        }
                    }

                    sampleIndex += frameSize;
                    frame->pts = index * frameSize;

                    if (ffmpeg.avcodec_send_frame(context, frame) < 0)
                    {
                        throw new InvalidOperationException("The AAC encoder rejected a frame.");
                    }

                    Drain(context, packet, units);
                }

                // Flush, or the last ~100 ms never arrives and the length assertion fails for a
                // reason that has nothing to do with the decoder.
                ffmpeg.avcodec_send_frame(context, null);
                Drain(context, packet, units);

                var config = new byte[context->extradata_size];
                if (context->extradata_size > 0 && context->extradata is not null)
                {
                    Marshal.Copy((IntPtr)context->extradata, config, 0, context->extradata_size);
                }

                return new Encoded(config, units, sampleRate, channels);
            }
            finally
            {
                // Local copies so the addresses can be taken directly: these are already fixed
                // pointers, and "fixed" cannot be applied to them.
                AVFrame* frameLocal = frame;
                AVPacket* packetLocal = packet;
                ffmpeg.av_frame_free(&frameLocal);
                ffmpeg.av_packet_free(&packetLocal);
            }
        }
        finally
        {
            AVCodecContext* contextLocal = context;
            ffmpeg.avcodec_free_context(&contextLocal);
        }
    }

    private static void Drain(AVCodecContext* context, AVPacket* packet, List<byte[]> units)
    {
        while (true)
        {
            int received = ffmpeg.avcodec_receive_packet(context, packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (received < 0)
            {
                throw new InvalidOperationException($"The AAC encoder failed (error {received}).");
            }

            var unit = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, unit, 0, packet->size);
            units.Add(unit);
            ffmpeg.av_packet_unref(packet);
        }
    }
}
