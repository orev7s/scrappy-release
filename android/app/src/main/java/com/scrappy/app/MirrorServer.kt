package com.scrappy.app

import android.util.Log
import org.json.JSONObject
import java.io.InputStream
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException

/**
 * The TCP listener the PC connects to.
 *
 * Rules here, each learned from a stuck tablet:
 *
 *  - **Accepting never waits for a session.** Every connection gets its own thread, so a status
 *    probe or a reconnect is answered while a stream is running. Accepting on the streaming thread
 *    meant a reconnecting PC sat in the backlog until the old session ended - which it never did.
 *  - **A session ends when the PC goes away, not when the next frame happens to be written.** The
 *    PC sends nothing during a stream, so an encoder that is quiet (still screen, paused game) hid
 *    the disconnect completely. A watchdog read on the socket notices the FIN and tears the session
 *    down within a poll interval.
 *  - **A failure is not an invitation to listen again.** A session that died while starting its
 *    encoder used to reset the state to "Waiting for the PC" in the same millisecond, which is
 *    why the tablet looked calm while the PC was told the stream had ended. The failure now stays
 *    on screen and on /status until a session really starts.
 *
 * Frames never leave from this thread: [FrameSender] owns the socket and decides what is stale.
 */
class MirrorServer(private val port: Int, private val service: CaptureService) : FrameSender.Listener {

    companion object {
        private const val TAG = "Scrappy"

        /** How long a connection has to say what it wants. */
        private const val FIRST_LINE_TIMEOUT_MS = 800

        /** How long an old session gets to unwind before a new one starts. */
        private const val TAKEOVER_GRACE_MS = 700L

        /**
         * Kernel send buffer for a session. This is the other half of the latency budget: our own
         * queue holds three frames, and everything beyond that used to sit in here - a 4 MB
         * default is several seconds of video on a link carrying 12 Mbps. 128 KB is ~5 frames.
         */
        private const val SEND_BUFFER_BYTES = 128 * 1024
    }

    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null

    @Volatile private var running = false

    /** The socket currently streaming, if any. */
    @Volatile private var client: Socket? = null

    /** The thread running the active session's frame loop. */
    @Volatile private var sessionThread: Thread? = null

    /** The writer for the active session, for its drop counters. */
    @Volatile private var sender: FrameSender? = null

    /** The peer of the active session, shown on the tablet's own screen. */
    @Volatile private var peer: String = ""

    private val sessionLock = Any()

    val peerAddress: String
        get() = peer

    fun start() {
        running = true
        serverSocket = try {
            ServerSocket(port).apply { reuseAddress = true }
        } catch (e: Exception) {
            // A port already in use is the one start failure a user can fix, so name it.
            throw IllegalStateException("cannot listen on port $port: ${e.message}", e)
        }

        acceptThread = Thread {
            while (running) {
                val socket = try {
                    serverSocket?.accept() ?: break
                } catch (e: Exception) {
                    if (running) {
                        Log.w(TAG, "accept: ${e.message}")
                        SessionState.failed("Accept failed: ${e.message}")
                    }
                    break
                }

                Thread { handle(socket) }.apply {
                    isDaemon = true
                    name = "scrappy-conn"
                    start()
                }
            }
        }.also {
            it.isDaemon = true
            it.name = "scrappy-accept"
            it.start()
        }

        Log.i(TAG, "listening on 0.0.0.0:$port")
    }

    /**
     * Reads the first line of a connection, which tells us what the connection is for:
     *
     *   GET /status ...   an HTTP probe from the PC's "Check tablet" button
     *   {"width":...}     settings pushed by the PC before a session
     *   (nothing)         a session from a client that pushes nothing
     *
     * Read a byte at a time on purpose. A buffered reader would swallow bytes belonging to the
     * stream that follows, and the stream has no resync marker.
     */
    private fun readFirstLine(socket: Socket): String? {
        socket.soTimeout = FIRST_LINE_TIMEOUT_MS
        val line = StringBuilder()

        return try {
            val input: InputStream = socket.getInputStream()
            while (line.length < 1024) {
                val b = input.read()
                if (b < 0) {
                    return if (line.isEmpty()) null else line.toString()
                }
                if (b == '\n'.code) {
                    return line.toString().trimEnd('\r')
                }
                line.append(b.toChar())
            }
            line.toString()
        } catch (e: SocketTimeoutException) {
            if (line.isEmpty()) null else line.toString()
        } catch (e: Exception) {
            null
        } finally {
            try {
                socket.soTimeout = 0
            } catch (ignored: Exception) {
                // Socket already closed.
            }
        }
    }

    private fun handle(socket: Socket) {
        socket.tcpNoDelay = true

        val firstLine = readFirstLine(socket)

        if (firstLine != null && firstLine.trimStart().startsWith("GET ")) {
            // One connection, one answer, then close. Works while a session is running, because
            // this runs on its own thread.
            try {
                StatusServer.respond(socket, service, firstLine)
            } catch (e: Exception) {
                Log.w(TAG, "status: ${e.message}")
            } finally {
                closeQuietly(socket)
            }
            return
        }

        val settings = if (firstLine != null && firstLine.trimStart().startsWith("{")) {
            try {
                JSONObject(firstLine)
            } catch (e: Exception) {
                JSONObject()
            }
        } else {
            JSONObject()
        }

        runSession(socket, settings)
    }

    /**
     * Streams one session. A new connection replaces an existing one rather than being refused:
     * the PC is the only client, and a tablet that thinks it is still connected must not be able
     * to lock the user out of their own app.
     */
    private fun runSession(socket: Socket, settings: JSONObject) {
        val previous = sessionThread
        if (previous != null && previous !== Thread.currentThread()) {
            // Dropping the socket closes the old session's read loop; give it a moment to unwind
            // so two encoders never feed the same server.
            client?.let { closeQuietly(it) }
            sender?.stop()
            try {
                previous.join(TAKEOVER_GRACE_MS)
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }

        synchronized(sessionLock) {
            client = socket
            sessionThread = Thread.currentThread()
        }

        peer = socket.inetAddress?.hostAddress ?: "PC"

        val width = settings.optInt("width", 2560)
        val height = settings.optInt("height", 1708)
        val codec = settings.optString("codec", "h265")
        val bitRate = settings.optInt("bitrate", 12_000_000)
        val fps = settings.optInt("fps", 60)

        var tx: FrameSender? = null

        try {
            try {
                socket.sendBufferSize = SEND_BUFFER_BYTES
            } catch (e: Exception) {
                // Not fatal: it only bounds how much latency the kernel can add.
            }

            val out = socket.getOutputStream()

            // Order is the protocol. The device name comes first, then a session header, then the
            // config packet, then frames. Announcing geometry before the name makes the receiving
            // end read raw header bytes as text, and it waits for a name that already went past.
            Wire.writeDeviceName(out, android.os.Build.MODEL)
            Wire.writeSessionHeader(out, width, height)
            out.flush()

            tx = FrameSender(socket, this).also {
                sender = it
                it.start()
            }

            startPeerWatchdog(socket)

            SessionState.connecting(peer)
            service.startCapture(width, height, codec, bitRate, fps, tx)

            Log.i(TAG, "client connected, ${width}x$height $codec ${bitRate / 1_000_000} Mbps")

            // Frames are written from the encoder drain, paced by this loop.
            while (running && client === socket && !socket.isClosed) {
                service.drainOnce(10_000L)

                if (SessionState.phase == SessionState.Phase.Failed) {
                    Log.w(TAG, "encoder failed, ending the session")
                    break
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "session ended: ${e.message}")
        } finally {
            tx?.stop()
            endSession(socket)
        }
    }

    // --- FrameSender.Listener: the link's report card -------------------------------------

    override fun onWindow(droppedFrames: Int, bitsPerSecond: Long) {
        val active = sender
        SessionState.link(active?.dropped ?: 0, SessionState.bitrateNow)
        service.onLinkWindow(droppedFrames, bitsPerSecond)
    }

    override fun onSyncFrameNeeded() {
        service.requestSyncFrame()
    }

    override fun onBroken(reason: String) {
        Log.w(TAG, "socket gone: $reason")
        closeQuietly(socket = client)
    }

    /**
     * Waits for the PC to hang up. The PC never sends anything during a session, so the first
     * read either blocks forever or returns -1, and -1 is the disconnect. Without this the tablet
     * stayed "Connected" until the user pressed Stop on the tablet itself.
     */
    private fun startPeerWatchdog(socket: Socket) {
        Thread {
            try {
                val input = socket.getInputStream()
                val scratch = ByteArray(64)
                while (true) {
                    val read = input.read(scratch)
                    if (read < 0) {
                        Log.i(TAG, "peer closed the session")
                        break
                    }
                    // Unexpected bytes: the protocol has nothing to read here. Ignore them
                    // rather than treating them as a reason to drop the session.
                }
            } catch (e: Exception) {
                // A reset, a timeout, a closed socket: all mean the same thing.
            } finally {
                closeQuietly(socket)
            }
        }.apply {
            isDaemon = true
            name = "scrappy-watch"
            start()
        }
    }

    /** Clears the session, but only if it is still the current one. */
    private fun endSession(socket: Socket) {
        synchronized(sessionLock) {
            if (client !== socket) {
                // Superseded by a newer connection; its owner cleans up.
                closeQuietly(socket)
                return
            }

            client = null
            sessionThread = null
            sender = null
            closeQuietly(socket)
            service.stopCapture()

            // Back to listening: a disconnect is not the end of the service, and the PC will
            // reconnect. Leaving it dead is why a second Start hung on "Connecting".
            //
            // A failure is different. The encoder never opened, so there is nothing to go back to
            // waiting for; saying "Waiting for the PC" here is what made a broken session look
            // healthy on the tablet and dead on the PC.
            if (running && SessionState.phase != SessionState.Phase.Failed) {
                SessionState.listening(port)
            }
        }
    }

    fun stop() {
        running = false

        val active = client
        client = null
        active?.let { closeQuietly(it) }

        sender?.stop()
        sender = null

        try {
            serverSocket?.close()
        } catch (e: Exception) {
            // Closing an already-broken listener is allowed to fail.
        }
        serverSocket = null

        acceptThread?.interrupt()
        acceptThread = null
    }

    private fun closeQuietly(socket: Socket?) {
        if (socket == null) return
        try {
            socket.close()
        } catch (ignored: Exception) {
            // Already closed.
        }
    }
}
