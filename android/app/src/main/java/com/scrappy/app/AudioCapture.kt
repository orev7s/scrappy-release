package com.scrappy.app

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.util.Log
import java.nio.ByteBuffer

/**
 * Captures what the tablet is playing and encodes it as AAC, without changing where it plays.
 *
 * This is the whole design of the audio side. Android's playback capture is a *tap* on the audio
 * mix, not a redirect: the tablet keeps sending sound to its speakers while a copy goes to the
 * encoder. That is exactly what is wanted here - the person holding the tablet still hears the
 * game, and the PC gets the same sound for the stream.
 *
 * The alternative, scrcpy's `audio_dup` route flag, exists because the adb server has to choose
 * between loop-back-with-playback and loop-back-only. An installed app using the playback capture
 * API does not choose: playback is unaffected by definition.
 *
 * What it cannot capture:
 *  - apps that opt out (`android:allowAudioPlaybackCapture="false"`, or setting their capture
 *    policy to ALLOW_CAPTURE_BY_NONE). Nothing in this app can override that, and it is the first
 *    thing to suspect if a game mirrors silently while everything else has sound.
 *  - DRM-protected audio, for the same reason.
 */
class AudioCapture(
    private val projection: MediaProjection,
    private val sampleRate: Int,
    private val channels: Int,
    private val bitRate: Int,
    private val onConfig: (ByteArray) -> Unit,
    private val onFrame: (ByteArray, Long) -> Unit,
) {
    companion object {
        private const val TAG = "Scrappy"

        /** How long a single dequeue may block. Small: the loop also has to notice a stop. */
        private const val DEQUEUE_TIMEOUT_US = 10_000L
    }

    private var record: AudioRecord? = null
    private var codec: MediaCodec? = null
    private var thread: Thread? = null

    @Volatile private var running = false
    @Volatile var capturedBytes: Long = 0
        private set

    fun start() {
        if (running) return

        val captureConfig = AudioPlaybackCaptureConfiguration.Builder(projection)
            // Media covers music and most games; Game is what a game engine reports; Unknown is
            // what a surprising number of apps still use. Assistant and notification sounds are
            // deliberately left out: they are not part of what is on screen.
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()

        val channelMask = if (channels == 1) AudioFormat.CHANNEL_IN_MONO else AudioFormat.CHANNEL_IN_STEREO
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(sampleRate)
            .setChannelMask(channelMask)
            .build()

        val minimum = AudioRecord.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        if (minimum <= 0) {
            throw IllegalStateException("this device cannot record at ${sampleRate} Hz with $channels channels")
        }

        val bufferBytes = maxOf(minimum * 2, sampleRate * channels * 2 / 5)

        val recorder = AudioRecord.Builder()
            .setAudioFormat(format)
            .setBufferSizeInBytes(bufferBytes)
            .setAudioPlaybackCaptureConfig(captureConfig)
            .build()

        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            recorder.release()
            throw IllegalStateException(
                "the system refused a playback capture record. RECORD_AUDIO permission is the usual cause.",
            )
        }

        val mime = MediaFormat.MIMETYPE_AUDIO_AAC
        val mediaCodec = MediaCodec.createEncoderByType(mime)
        val mediaFormat = MediaFormat.createAudioFormat(mime, sampleRate, channels).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, bitRate)
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16384)
        }

        mediaCodec.configure(mediaFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        mediaCodec.start()
        recorder.startRecording()

        record = recorder
        codec = mediaCodec
        running = true
        capturedBytes = 0

        thread = Thread { loop(recorder, mediaCodec) }.apply {
            isDaemon = true
            name = "scrappy-audio"
            start()
        }

        Log.i(TAG, "audio capture started: $sampleRate Hz, $channels ch, $bitRate bps")
    }

    private fun loop(recorder: AudioRecord, mediaCodec: MediaCodec) {
        val pcm = ByteArray(8192)
        val info = MediaCodec.BufferInfo()
        var ptsUs = 0L
        var configSent = false

        try {
            while (running) {
                val read = recorder.read(pcm, 0, pcm.size)
                if (read <= 0) {
                    continue
                }

                capturedBytes += read

                val inputIndex = mediaCodec.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
                if (inputIndex >= 0) {
                    val input: ByteBuffer? = mediaCodec.getInputBuffer(inputIndex)
                    if (input != null) {
                        input.clear()
                        input.put(pcm, 0, read)

                        val frames = read / (channels * 2)
                        mediaCodec.queueInputBuffer(inputIndex, 0, read, ptsUs, 0)
                        ptsUs += frames * 1_000_000L / sampleRate
                    } else {
                        mediaCodec.queueInputBuffer(inputIndex, 0, 0, ptsUs, 0)
                    }
                }

                // Drain everything that is ready. Audio is small, so an inner loop costs nothing
                // and keeps the queue from building up behind a slow network.
                while (true) {
                    val outputIndex = mediaCodec.dequeueOutputBuffer(info, 0)
                    if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        val csd = readCsd(mediaCodec)
                        if (csd.isNotEmpty() && !configSent) {
                            configSent = true
                            onConfig(csd)
                        }
                        continue
                    }

                    if (outputIndex < 0) {
                        break
                    }

                    val buffer = mediaCodec.getOutputBuffer(outputIndex)
                    if (buffer != null && info.size > 0) {
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        val payload = ByteArray(info.size)
                        buffer.get(payload)

                        if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                            if (!configSent) {
                                configSent = true
                                onConfig(payload)
                            }
                        } else {
                            onFrame(payload, info.presentationTimeUs)
                        }
                    }

                    mediaCodec.releaseOutputBuffer(outputIndex, false)
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "audio capture stopped: ${e.message}")
            SessionState.audioError(e.message ?: "audio capture stopped")
        }
    }

    private fun readCsd(mediaCodec: MediaCodec): ByteArray {
        val format = try {
            mediaCodec.outputFormat
        } catch (e: IllegalStateException) {
            return ByteArray(0)
        }

        val csd0 = format.getByteBuffer("csd-0") ?: return ByteArray(0)
        val copy = ByteArray(csd0.remaining())
        csd0.get(copy)
        return copy
    }

    fun stop() {
        running = false

        try {
            record?.stop()
        } catch (e: Exception) {
            Log.w(TAG, "audio record stop: ${e.message}")
        }

        try {
            thread?.join(400)
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        thread = null

        try {
            codec?.stop()
        } catch (e: Exception) {
            Log.w(TAG, "audio codec stop: ${e.message}")
        }

        try {
            codec?.release()
        } catch (e: Exception) {
            Log.w(TAG, "audio codec release: ${e.message}")
        }

        try {
            record?.release()
        } catch (e: Exception) {
            Log.w(TAG, "audio record release: ${e.message}")
        }

        codec = null
        record = null
    }
}
