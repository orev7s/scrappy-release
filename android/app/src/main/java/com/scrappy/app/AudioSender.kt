package com.scrappy.app

import java.io.OutputStream
import java.net.Socket

/**
 * Writes encoded audio frames to the audio connection.
 *
 * Simpler than [FrameSender] on purpose: audio has no keyframes and no reference chain, so a
 * dropped packet costs a click rather than a corrupted picture, and the only rule is that the queue
 * must not grow. A live stream that is half a second behind is broken in a way nobody forgives.
 *
 * The queue holds about half a second. Beyond that the oldest frame goes, because the newest audio
 * is the audio that matches the picture on screen.
 */
class AudioSender(private val socket: Socket) {

    companion object {
        /** 128 kbps AAC is about 370 bytes per 21 ms frame; this is roughly 0.5 s of audio. */
        private const val CAPACITY = 24
    }

    private val lock = Object()
    private val queue = ArrayDeque<ByteArray>(CAPACITY)
    private val out: OutputStream = socket.getOutputStream()

    @Volatile private var running = false
    private var writer: Thread? = null

    @Volatile var dropped: Long = 0
        private set

    fun start() {
        synchronized(lock) {
            if (running) return
            running = true
            queue.clear()
        }

        writer = Thread { writeLoop() }.apply {
            isDaemon = true
            name = "scrappy-audio-tx"
            start()
        }
    }

    fun submit(block: ByteArray) {
        if (block.isEmpty()) return

        synchronized(lock) {
            if (!running) return
            if (queue.size >= CAPACITY) {
                queue.removeFirst()
                dropped++
            }
            queue.addLast(block)
            lock.notifyAll()
        }
    }

    private fun writeLoop() {
        try {
            while (true) {
                val block = synchronized(lock) {
                    while (running && queue.isEmpty()) {
                        lock.wait(250)
                    }
                    if (!running) return
                    queue.removeFirst()
                }

                out.write(block)
                out.flush()
            }
        } catch (e: Exception) {
            // The PC hung up. The session's own watchdog notices and tears down the capture.
        } finally {
            try {
                socket.close()
            } catch (ignored: Exception) {
                // Already closed.
            }
        }
    }

    fun stop() {
        synchronized(lock) {
            running = false
            queue.clear()
            lock.notifyAll()
        }

        try {
            socket.close()
        } catch (ignored: Exception) {
            // Already closed.
        }

        try {
            writer?.join(300)
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        writer = null
    }
}
