package com.scrappy.app

import android.util.Log
import java.io.OutputStream
import java.net.Socket

/**
 * Writes access units to the socket on its own thread, on a short leash.
 *
 * This class exists because of the one thing the old design could not do: throw a frame away.
 *
 * The encoder's drain loop used to write straight to the socket. When the link is slower than the
 * encoder - a 2.4 GHz Wi-Fi, a phone hotspot, a router with a neighbour streaming 4K - that write
 * blocks. The encoder's output buffers then fill, the encoder stops, and every frame produced in
 * the meantime is still queued inside TCP. Nothing is ever dropped, so the picture ends up seconds
 * behind and never catches up: "the frames come in extremely slowly, very laggy and stuttery".
 *
 * The rule here is that a frame which cannot be sent now is stale, and staleness is the one thing
 * a mirror cannot fix later. So:
 *
 *  - at most [CAPACITY] access units wait between the encoder and the socket (about 50 ms at
 *    60 fps - enough to absorb a Wi-Fi jitter burst, not enough to become a delay);
 *  - when the queue is full it is emptied rather than grown, because the newest frame is the only
 *    one worth sending;
 *  - dropping a frame breaks the reference chain, so after a drop nothing is sent until the
 *    encoder produces a keyframe, which is requested immediately. One round trip of freeze
 *    instead of a second of garbage.
 *
 * The writer also measures what the link actually accepted per second and reports it, which is
 * what [BitrateController] uses to lower the encoder's bitrate until the link keeps up.
 */
class FrameSender(private val socket: Socket, private val listener: Listener) {

    interface Listener {
        /**
         * One measurement window closed. [droppedFrames] is how many access units were thrown
         * away in that second, [bitsPerSecond] how much actually left the socket.
         */
        fun onWindow(droppedFrames: Int, bitsPerSecond: Long)

        /** The encoder should emit a keyframe now, because the receiver's chain was broken. */
        fun onSyncFrameNeeded()

        /** The socket is gone. Called at most once. */
        fun onBroken(reason: String)
    }

    companion object {
        private const val TAG = "Scrappy"

        /**
         * Frames allowed to wait. Three is about 50 ms at 60 fps: it absorbs the jitter of a
         * Wi-Fi link without becoming a buffer the user can feel.
         */
        private const val CAPACITY = 3

        /** Measurement window for throughput and drops. */
        private const val SAMPLE_MS = 1000L

        /** Do not ask the encoder for a keyframe more often than this. */
        private const val SYNC_COOLDOWN_MS = 120L
    }

    private class Frame(val block: ByteArray, val isKey: Boolean)

    private val lock = Object()

    /** Serialises frame writes with the config packet, which must never land mid-frame. */
    private val writeLock = Any()

    private val queue = ArrayDeque<Frame>(CAPACITY)
    private val out: OutputStream = socket.getOutputStream()

    @Volatile private var running = false
    private var writer: Thread? = null

    /** A frame was dropped, so the decoder's reference chain is broken until the next keyframe. */
    private var awaitingKey = false

    private var lastSyncRequestAt = 0L
    private var droppedTotal = 0L
    private var droppedInWindow = 0
    private var sentTotal = 0L
    private var sentBytes = 0L
    private var windowStart = System.currentTimeMillis()

    /** Frames thrown away since the session began. Read by the tablet's own status line. */
    val dropped: Long
        get() = synchronized(lock) { droppedTotal }

    val sent: Long
        get() = synchronized(lock) { sentTotal }

    fun start() {
        synchronized(lock) {
            if (running) return
            running = true
            queue.clear()
            awaitingKey = false
            droppedTotal = 0
            droppedInWindow = 0
            sentTotal = 0
            sentBytes = 0
            windowStart = System.currentTimeMillis()
        }

        writer = Thread { writeLoop() }.apply {
            isDaemon = true
            name = "scrappy-tx"
            start()
        }
    }

    /**
     * Offers one access unit, already encoded as header + payload. Never blocks: the caller is the
     * encoder's drain loop, and making it wait is what caused the original backlog.
     */
    fun submit(block: ByteArray, isKey: Boolean) {
        var sync = false
        var enqueue = true

        synchronized(lock) {
            if (!running) return

            if (queue.size >= CAPACITY) {
                // The link is behind. Keep the newest video and lose the reference chain, which
                // costs one round trip to repair; queueing it costs the rest of the session.
                queue.clear()
                droppedTotal++
                droppedInWindow++

                if (!isKey) {
                    awaitingKey = true
                    sync = System.currentTimeMillis() - lastSyncRequestAt >= SYNC_COOLDOWN_MS
                    if (sync) lastSyncRequestAt = System.currentTimeMillis()
                    enqueue = false
                }
            }

            if (enqueue && awaitingKey && !isKey) {
                // Inside the broken GOP. Sending it would only add bytes the decoder cannot use;
                // the keyframe that follows is what restores the picture.
                droppedTotal++
                droppedInWindow++
                enqueue = false
            }

            if (enqueue) {
                if (isKey) awaitingKey = false
                queue.addLast(Frame(block, isKey))
                lock.notifyAll()
            }
        }

        if (sync) {
            listener.onSyncFrameNeeded()
        }
    }

    /**
     * The parameter sets, written ahead of the frames and outside the queue: they are tiny, they
     * are cancelled by nothing, and a decoder that misses them decodes nothing at all.
     */
    fun sendConfig(csd: ByteArray) {
        if (csd.isEmpty()) return
        val block = Wire.encodeConfig(csd)

        try {
            synchronized(writeLock) {
                out.write(block)
                out.flush()
            }
        } catch (e: Exception) {
            listener.onBroken(e.message ?: e.javaClass.simpleName)
        }
    }

    fun stop() {
        synchronized(lock) {
            running = false
            queue.clear()
            lock.notifyAll()
        }

        // The writer may be blocked inside write() on a stalled link; closing the socket is what
        // ends it. The caller closes the socket too, and closing twice is allowed.
        try {
            socket.close()
        } catch (ignored: Exception) {
            // Already closed.
        }

        try {
            writer?.join(400)
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        writer = null
    }

    private fun writeLoop() {
        try {
            while (true) {
                val frame = synchronized(lock) {
                    while (running && queue.isEmpty()) {
                        lock.wait(250)
                    }

                    if (!running) return
                    queue.removeFirst()
                }

                synchronized(writeLock) {
                    out.write(frame.block)
                    out.flush()
                }

                synchronized(lock) {
                    sentTotal++
                    sentBytes += frame.block.size
                }

                sample()
            }
        } catch (e: Exception) {
            if (running) {
                Log.w(TAG, "send: ${e.message}")
                listener.onBroken(e.message ?: e.javaClass.simpleName)
            }
        } finally {
            try {
                socket.close()
            } catch (ignored: Exception) {
                // The session is over either way.
            }
        }
    }

    /** Closes the one-second window and reports it. Runs on the writer thread only. */
    private fun sample() {
        val now = System.currentTimeMillis()
        val elapsed = now - windowStart
        if (elapsed < SAMPLE_MS) return

        val bytes: Long
        val dropped: Int
        synchronized(lock) {
            bytes = sentBytes
            dropped = droppedInWindow
            sentBytes = 0
            droppedInWindow = 0
        }
        windowStart = now

        listener.onWindow(dropped, bytes * 8 * 1000 / elapsed)
    }
}
