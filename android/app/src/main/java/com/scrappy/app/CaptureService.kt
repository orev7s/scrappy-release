package com.scrappy.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import android.view.Surface
import java.io.File

/**
 * Holds the capture session: MediaProjection consent, one VideoCodec encoder fed by a
 * VirtualDisplay, and a TCP server speaking the wire format.
 *
 * Every failure is caught and published to [SessionState]. An exception escaping onStartCommand
 * kills the whole process, which on screen looks exactly like a successful start: the app just
 * disappears. That is the bug this file is written around.
 *
 * Two things about the projection are worth knowing before changing anything here:
 *
 *  - **The VirtualDisplay outlives a session.** A PC that stops and reconnects must not need a new
 *    capture: on Android 14 a MediaProjection that has already produced a VirtualDisplay will not
 *    produce a second one, so the old code's "release and re-create per session" failed on the
 *    second connect - the encoder threw, the session died, and the tablet went back to "Waiting
 *    for the PC". Here the display is created once and each session attaches its own surface to
 *    it ([VirtualDisplay.setSurface]) and asks it to resize if the PC changed the geometry
 *    ([VirtualDisplay.resize]).
 *  - **The encoder belongs to the session, the display belongs to the service.** So all of it is
 *    behind one lock: the old session's stop and the new session's start used to overlap, and a
 *    drain that ran on a released codec killed the new session before its first frame.
 */
class CaptureService : Service() {

    companion object {
        const val ACTION_START = "com.scrappy.app.START"
        const val ACTION_STOP = "com.scrappy.app.STOP"
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        const val EXTRA_PORT = "port"
        private const val CHANNEL_ID = "scrappy-session"
        private const val NOTIFICATION_ID = 1
        private const val TAG = "Scrappy"
    }

    private var projection: MediaProjection? = null
    private var projectionCallback: MediaProjection.Callback? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var encoder: Encoder? = null

    /** The geometry the display was last created or resized to; VirtualDisplay has no getter. */
    private var displayWidth = 0
    private var displayHeight = 0

    /** Guards [encoder] and [virtualDisplay]: one session's start races another's stop. */
    private val captureLock = Any()

    private var bitrates: BitrateController? = null

    private var server: MirrorServer? = null
    private val main = Handler(Looper.getMainLooper())

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        try {
            when (intent?.action) {
                ACTION_STOP -> {
                    stopSession("Stopped")
                    stopSelf()
                    return START_NOT_STICKY
                }

                ACTION_START -> {
                    // Foreground first: on Android 14+ getMediaProjection throws unless the service
                    // is already foreground with the mediaProjection type.
                    startAsForeground("Starting")
                    SessionState.starting(intent.getIntExtra(EXTRA_PORT, 27183))

                    val port = intent.getIntExtra(EXTRA_PORT, 27183)
                    val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                    val data = intent.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)

                    if (data == null) {
                        fail(IllegalArgumentException("no consent data reached the service"), "Start")
                        stopSelf()
                        return START_NOT_STICKY
                    }

                    DiscoveryResponder.acquire(this)
                    startSession(resultCode, data, port)
                    return START_STICKY
                }
            }
        } catch (t: Throwable) {
            fail(t, "onStartCommand")
            updateNotification("Could not start - open Scrappy for details")
            stopSelf()
        }

        return START_NOT_STICKY
    }

    private fun startSession(resultCode: Int, data: Intent, port: Int) {
        try {
            // Start can arrive twice: once after a failure, once after the user pressed Start
            // again without pressing Stop first. Everything from the previous attempt has to go
            // first, or the new ServerSocket finds the old one still holding the port.
            teardown()

            val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val fresh = manager.getMediaProjection(resultCode, data)
                ?: throw IllegalStateException("the system returned no projection")

            // Held so teardown can unregister it: stopping a projection fires onStop, and a late
            // onStop from the previous session must not tear down the new one.
            val callback = object : MediaProjection.Callback() {
                override fun onStop() {
                    if (projection === fresh) {
                        stopSession("Capture permission withdrawn")
                    }
                }
            }
            fresh.registerCallback(callback, main)

            projection = fresh
            projectionCallback = callback

            // One port for both the stream and the status request. Bind first: a port collision is
            // the failure a user can actually act on.
            server = MirrorServer(port, this).also { it.start() }

            SessionState.listening(port)
            updateNotification("Waiting for the PC on port $port")
        } catch (t: Throwable) {
            fail(t, "Starting the session")
        }
    }

    /**
     * Called by the server once a PC connects: attach a new encoder to the existing display.
     *
     * The display is the expensive, permission-bound half and is created once; the encoder is the
     * cheap half and is created per session, which is also what makes a reconnect work.
     */
    fun startCapture(width: Int, height: Int, codec: String, bitRate: Int, fps: Int, sender: FrameSender) {
        synchronized(captureLock) {
            try {
                val mediaProjection = projection ?: throw IllegalStateException("no capture permission")

                releaseEncoderLocked()

                val mediaCodec = Encoder(
                    width,
                    height,
                    codec,
                    bitRate,
                    fps,
                    { block, isKey -> sender.submit(block, isKey) },
                    { config -> if (config.isNotEmpty()) sender.sendConfig(config) },
                )
                encoder = mediaCodec
                bitrates = BitrateController(bitRate)

                attachDisplayLocked(mediaProjection, mediaCodec.inputSurface, width, height)

                mediaCodec.start()

                // The config packet has to reach the PC before the first frame or decoding fails.
                val config = mediaCodec.codecConfig
                if (config.isNotEmpty()) {
                    sender.sendConfig(config)
                }

                SessionState.mirrored(
                    server?.peerAddress ?: "PC",
                    width,
                    height,
                    bitRate,
                )
                updateNotification("Mirroring ${width}x$height")
            } catch (t: Throwable) {
                fail(t, "Starting the encoder")
            }
        }
    }

    /**
     * First session creates the display; every later one re-points it.
     *
     * `createVirtualDisplay` is only ever called on a projection that has not produced a display
     * yet - the tablet keeps the same display for as long as the user keeps mirroring, which is
     * exactly the lifetime of the consent.
     */
    private fun attachDisplayLocked(mediaProjection: MediaProjection, surface: Surface, width: Int, height: Int) {
        val density = resources.displayMetrics.densityDpi
        val existing = virtualDisplay

        if (existing == null) {
            virtualDisplay = mediaProjection.createVirtualDisplay(
                "scrappy",
                width,
                height,
                density,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                surface,
                null,
                null,
            )
            displayWidth = width
            displayHeight = height
            Log.i(TAG, "display created ${width}x$height")
            return
        }

        if (displayWidth != width || displayHeight != height) {
            existing.resize(width, height, density)
            displayWidth = width
            displayHeight = height
            Log.i(TAG, "display resized to ${width}x$height")
        }

        existing.surface = surface
    }

    /** Releases the encoder and detaches it from the display, leaving the projection alive. */
    fun stopCapture() {
        synchronized(captureLock) {
            releaseEncoderLocked()
        }
    }

    private fun releaseEncoderLocked() {
        // Detach first: a display left pointing at a released surface keeps the last frame frozen
        // on screen and the next session's surface is never picked up.
        try {
            virtualDisplay?.surface = null
        } catch (e: Exception) {
            Log.w(TAG, "detach surface: ${e.message}")
        }

        try {
            encoder?.stop()
        } catch (e: Exception) {
            Log.w(TAG, "encoder stop: ${e.message}")
        }
        encoder = null
        bitrates = null
    }

    fun drainOnce(timeoutUs: Long) {
        val codec = synchronized(captureLock) { encoder } ?: return
        try {
            codec.drain(timeoutUs)
        } catch (t: Throwable) {
            fail(t, "Encoding")
        }
    }

    /** The sender dropped frames: the link is behind, so ask the encoder for a fresh keyframe. */
    fun requestSyncFrame() {
        synchronized(captureLock) { encoder }?.requestSyncFrame()
    }

    /**
     * One second of link measurement. Any drop means the encoder is producing more than the link
     * carries and the bitrate has to come down; clean seconds buy it back up.
     */
    fun onLinkWindow(droppedFrames: Int, bitsPerSecond: Long) {
        val codec: Encoder?
        val next: Int?

        synchronized(captureLock) {
            codec = encoder
            next = bitrates?.onWindow(droppedFrames, bitsPerSecond)
            if (next != null) {
                codec?.setBitrate(next)
            }
        }

        if (next != null) {
            SessionState.throttled(next)
            Log.i(TAG, "link throttled the encoder to ${next / 1_000_000} Mbps after $droppedFrames dropped frames")
        }
    }

    private fun stopSession(reason: String) {
        teardown()
        SessionState.stopped(reason)
    }

    /**
     * Releases everything a session owns, in the order that cannot fail: the encoder first (it is
     * attached to the display), then the display, then the projection that made it, then the
     * listener. Safe to call when nothing is running.
     */
    private fun teardown() {
        synchronized(captureLock) {
            releaseEncoderLocked()

            try {
                virtualDisplay?.release()
            } catch (e: Exception) {
                Log.w(TAG, "display release: ${e.message}")
            }
            virtualDisplay = null
            displayWidth = 0
            displayHeight = 0
        }

        val active = projection
        val callback = projectionCallback
        projection = null
        projectionCallback = null

        try {
            if (active != null && callback != null) {
                // Unregister before stopping: the callback would otherwise run on the main thread
                // after the next session has already begun.
                active.unregisterCallback(callback)
            }
        } catch (e: Exception) {
            Log.w(TAG, "unregister projection callback: ${e.message}")
        }

        try {
            active?.stop()
        } catch (e: Exception) {
            Log.w(TAG, "projection stop: ${e.message}")
        }

        server?.stop()
        server = null
    }

    private fun fail(t: Throwable, where: String) {
        Log.e(TAG, "$where failed", t)
        SessionState.failed(t, where)
        writeCrashLog(t, where)
    }

    /** Kept on disk as well, because a crash may take the screen with it. */
    private fun writeCrashLog(t: Throwable, where: String) {
        try {
            File(filesDir, "last-error.txt").writeText(
                "$where\n${t.javaClass.name}: ${t.message}\n\n${t.stackTraceToString()}",
            )
        } catch (ignored: Exception) {
            // Diagnostics must never be the reason a start fails.
        }
    }

    override fun onDestroy() {
        stopSession("Service stopped")
        DiscoveryResponder.release()
        super.onDestroy()
    }

    private fun startAsForeground(text: String) {
        val notification = buildNotification(text)
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Mirroring", NotificationManager.IMPORTANCE_LOW),
            )
        }

        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, CaptureService::class.java).apply { action = ACTION_STOP },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Scrappy")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }
}

/**
 * One MediaCodec surface encoder. csd-0/csd-1 become the codec-config packet.
 *
 * The output callback hands back a finished wire block (header + payload) rather than a codec
 * buffer, because the codec buffer is recycled the moment this returns: copying here, once, is
 * cheaper than holding a codec buffer open while the network decides what to do with it.
 */
class Encoder(
    private val width: Int,
    private val height: Int,
    codecName: String,
    bitRate: Int,
    fps: Int,
    private val onOutput: (ByteArray, Boolean) -> Unit,
    private val onFormat: (ByteArray) -> Unit = {},
) {
    private val mime = when (codecName) {
        "h265" -> MediaFormat.MIMETYPE_VIDEO_HEVC
        "av1" -> MediaFormat.MIMETYPE_VIDEO_AV1
        else -> MediaFormat.MIMETYPE_VIDEO_AVC
    }

    private val codec: MediaCodec = run {
        val mediaCodec = MediaCodec.createEncoderByType(mime)
        val base = MediaFormat.createVideoFormat(mime, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitRate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)

            // Put VPS/SPS/PPS in front of every keyframe. Then a receiver that missed the config
            // packet - or opened before it arrived - can still start decoding at the next IDR
            // instead of failing with AVERROR_INVALIDDATA forever.
            try {
                setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
            } catch (e: Exception) {
                // Encoders that do not know the key ignore it; the config packet still covers us.
            }

            // Latency knobs, one line each, because each of them is a real source of delay:
            // B-frames are reordered before they can be sent; an operating rate the codec does not
            // know makes it pick a conservative power/quality mode; a non-realtime priority lets
            // the scheduler starve the encoder on a loaded device (the game is still running).
            try {
                setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            } catch (e: Exception) {
                // Some encoders refuse the key on some codecs.
            }

            try {
                setInteger(MediaFormat.KEY_OPERATING_RATE, fps)
            } catch (e: Exception) {
                // Optional.
            }

            try {
                setInteger(MediaFormat.KEY_PRIORITY, 0)
            } catch (e: Exception) {
                // Optional.
            }
        }

        try {
            base.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            mediaCodec.configure(base, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        } catch (e: Exception) {
            // Not every encoder offers constant bitrate. Variable is worse for a stream's pacing
            // but much better than refusing to start.
            base.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR)
            mediaCodec.configure(base, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        }

        mediaCodec
    }

    val inputSurface: Surface = codec.createInputSurface()

    var codecConfig: ByteArray = ByteArray(0)
        private set

    private var started = false

    fun start() {
        codec.start()
        started = true
        codecConfig = readCsd()
    }

    /**
     * Dequeues at most one access unit. Called from the session loop, which paces the encoder: the
     * loop's own rate is the frame rate cap in practice, and it also stops draining the moment the
     * socket goes away.
     */
    fun drain(timeoutUs: Long = 0) {
        if (!started) return

        val info = MediaCodec.BufferInfo()
        val index = codec.dequeueOutputBuffer(info, timeoutUs)

        if (index >= 0) {
            val buffer = codec.getOutputBuffer(index)
            if (buffer != null && info.size > 0) {
                buffer.position(info.offset)
                buffer.limit(info.offset + info.size)

                val isKey = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0

                if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    // This buffer *is* the parameter set. Send it as the config packet rather than
                    // dropping it, which is what left the PC with no SPS/PPS at all.
                    val config = ByteArray(info.size)
                    buffer.get(config)
                    codecConfig = config
                    onFormat(config)
                } else {
                    onOutput(Wire.encodeFrame(info.presentationTimeUs, isKey, buffer), isKey)
                }
            }
            codec.releaseOutputBuffer(index, false)
        } else if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
            codecConfig = readCsd()
            if (codecConfig.isNotEmpty()) {
                onFormat(codecConfig)
            }
        }
    }

    /** Asks the encoder for a keyframe. Safe to call from the network thread. */
    fun requestSyncFrame() {
        synchronized(this) {
            if (!started) return
            try {
                codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
            } catch (e: Exception) {
                Log.w("Scrappy", "sync frame: ${e.message}")
            }
        }
    }

    /** Changes the target bitrate mid-stream; the encoder applies it at the next keyframe. */
    fun setBitrate(bitsPerSecond: Int) {
        synchronized(this) {
            if (!started) return
            try {
                codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bitsPerSecond) })
            } catch (e: Exception) {
                Log.w("Scrappy", "set bitrate: ${e.message}")
            }
        }
    }

    private fun readCsd(): ByteArray {
        val format = try {
            codec.outputFormat
        } catch (e: IllegalStateException) {
            return ByteArray(0)
        }

        val csd0 = format.getByteBuffer("csd-0")?.let { toArray(it) } ?: ByteArray(0)
        val csd1 = format.getByteBuffer("csd-1")?.let { toArray(it) } ?: ByteArray(0)
        return csd0 + csd1
    }

    fun stop() {
        synchronized(this) {
            started = false
            try {
                codec.stop()
            } catch (e: IllegalStateException) {
                // Already stopped.
            }
            codec.release()
        }
    }

    private fun toArray(buffer: java.nio.ByteBuffer): ByteArray {
        val copy = ByteArray(buffer.remaining())
        buffer.get(copy)
        return copy
    }
}
