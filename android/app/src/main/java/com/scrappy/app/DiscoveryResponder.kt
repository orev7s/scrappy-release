package com.scrappy.app

import android.content.Context
import android.net.wifi.WifiManager
import android.provider.Settings
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress

/**
 * Answers the PC's discovery probe, so the tablet appears in its list by name instead of the user
 * copying an IP off this screen.
 *
 * The PC broadcasts `SCRAPPY_DISCOVER` to UDP 27184; this replies with one small JSON object on
 * the same socket. No address is included: the PC takes the source address of the datagram, which
 * is by definition the interface that can reach it.
 *
 * Runs whenever the app is open or the service is running. It costs one sleeping thread and no
 * wake locks.
 */
object DiscoveryResponder {

    /** Not the mirror port: a probe must never disturb a live session. */
    const val PORT = 27184

    private const val TAG = "Scrappy"
    private const val PROBE = "SCRAPPY_DISCOVER"

    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var thread: Thread? = null
    @Volatile private var running = false
    @Volatile private var multicastLock: WifiManager.MulticastLock? = null

    /** The activity and the service both want it running; the last one out stops it. */
    private var holders = 0
    private val holderLock = Any()

    fun acquire(context: Context) {
        synchronized(holderLock) {
            holders++
            if (holders > 1) return
        }
        start(context)
    }

    fun release() {
        synchronized(holderLock) {
            holders = (holders - 1).coerceAtLeast(0)
            if (holders > 0) return
        }
        stop()
    }

    private fun start(context: Context) {
        if (running) return
        running = true

        val appContext = context.applicationContext

        val lock = try {
            // Some HyperOS builds drop broadcast datagrams to a background socket without this.
            val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            wifi.createMulticastLock("scrappy:discovery").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            null
        }
        multicastLock = lock

        thread = Thread {
            try {
                val datagramSocket = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(PORT))
                }
                socket = datagramSocket
                Log.i(TAG, "discovery listening on $PORT")

                val buffer = ByteArray(256)
                while (running) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        datagramSocket.receive(packet)
                    } catch (e: Exception) {
                        if (!running) break
                        Log.w(TAG, "discovery receive: ${e.message}")
                        continue
                    }

                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8).trim()
                    if (!text.startsWith(PROBE)) continue

                    val reply = replyJson(appContext).toByteArray(Charsets.UTF_8)
                    try {
                        datagramSocket.send(DatagramPacket(reply, reply.size, packet.address, packet.port))
                    } catch (e: Exception) {
                        Log.w(TAG, "discovery reply: ${e.message}")
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "discovery stopped: ${e.message}")
            } finally {
                closeSocket()
            }
        }.also {
            it.isDaemon = true
            it.name = "scrappy-discovery"
            it.start()
        }
    }

    fun stop() {
        running = false
        closeSocket()
        thread?.interrupt()
        thread = null

        try {
            multicastLock?.release()
        } catch (e: Exception) {
            // Already released.
        }
        multicastLock = null
    }

    private fun closeSocket() {
        try {
            socket?.close()
        } catch (e: Exception) {
            // Closing a socket that is already gone is allowed to fail.
        }
        socket = null
    }

    /**
     * The same facts the HTTP status endpoint reports, in the shape the PC's parser expects. The
     * version matters: the two ends must be upgraded together, and the picker shows which is which.
     */
    private fun replyJson(context: Context): String {
        val name = deviceName(context)
        val facts = DeviceFacts.read(context)
        val port = SessionState.port.takeIf { it > 0 } ?: 27183

        val addresses = JSONArray()
        for (entry in NetLinks.read(port)) {
            addresses.put(JSONObject().put("address", entry.address).put("link", entry.link))
        }

        return JSONObject().apply {
            put("scrappy", 1)
            put("name", name)
            put("model", android.os.Build.MODEL ?: "android")
            put("port", port)
            put("version", Version.NAME)
            put("phase", phaseLabel())

            // Why the PC needs the raw message: it is the only way to tell a tablet that is
            // waiting from a tablet whose encoder refused to start. Without it the PC can only
            // report "the device closed the stream", which is true and useless.
            put("message", SessionState.message)
            put("lastError", SessionState.lastErrorLine)
            put("wiredReady", NetLinks.hasWired(port))
            put("addresses", addresses)
            put("width", facts.width)
            put("height", facts.height)
            put("refreshHz", facts.refreshHz)
        }.toString()
    }

    /** "Waiting for the PC", "Connected", … - the same words the tablet shows on its own screen. */
    private fun phaseLabel(): String = when (SessionState.phase) {
        SessionState.Phase.Idle -> "Not started"
        SessionState.Phase.Starting -> "Starting"
        SessionState.Phase.Listening -> "Waiting for the PC"
        SessionState.Phase.Connected -> "Connected"
        SessionState.Phase.Failed -> "Failed"
    }

    /** What the tablet calls itself in its own settings; falls back to the model name. */
    fun deviceName(context: Context): String {
        val configured = try {
            Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)
        } catch (e: Exception) {
            null
        }

        return configured?.takeIf { it.isNotBlank() } ?: android.os.Build.MODEL ?: "Android tablet"
    }
}
