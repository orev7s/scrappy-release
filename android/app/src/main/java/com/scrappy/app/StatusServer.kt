package com.scrappy.app

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.net.Socket

/**
 * Answers `GET /status` on the same port the session uses.
 *
 * It used to be on port + 1, which meant two addresses to remember, two firewall rules, and a
 * "check tablet" that failed while the session worked - or the other way round. One port, one
 * address: [MirrorServer] reads the first line of a connection and decides which of the two it is.
 *
 * Read-only: it reports the panel's refresh rate, it never sets it.
 *
 * It also reports what the session is *saying*. That is what lets the PC answer "why did the
 * stream stop?" with the tablet's own words instead of "the device closed the stream", and it is
 * polled after an unexpected end, not during a healthy session.
 */
object StatusServer {

    fun respond(socket: Socket, context: Context, requestLine: String) {
        val body = if (requestLine.contains("/status")) statusJson(context).toString() else "{}"
        val bytes = body.toByteArray(Charsets.UTF_8)

        val header = "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: ${bytes.size}\r\n" +
            "Connection: close\r\n\r\n"

        socket.getOutputStream().apply {
            write(header.toByteArray(Charsets.US_ASCII))
            write(bytes)
            flush()
        }
    }

    private fun statusJson(context: Context): JSONObject {
        val facts = DeviceFacts.read(context)

        val modes = JSONArray()
        facts.refreshModes.forEach { modes.put(it) }

        val addresses = JSONArray()
        for (entry in NetLinks.read(SessionState.port.takeIf { it > 0 } ?: 27183)) {
            addresses.put(JSONObject().put("address", entry.address).put("link", entry.link))
        }

        return JSONObject().apply {
            // Name first: this is what the PC's picker shows, and a model number alone is not
            // enough to tell two tablets apart.
            put("name", DiscoveryResponder.deviceName(context))
            put("scrappy", 1)
            put("version", Version.NAME)
            put("phase", phaseLabel())
            put("message", SessionState.message)
            put("lastError", SessionState.lastErrorLine)
            put("model", android.os.Build.MODEL ?: "android")
            put("android", android.os.Build.VERSION.RELEASE ?: "?")
            put("width", facts.width)
            put("height", facts.height)
            put("portrait", facts.portrait)
            put("refreshHz", facts.refreshHz)
            put("refreshModes", modes)
            put("h264Hardware", facts.h264Hardware)
            put("h265Hardware", facts.h265Hardware)
            put("av1Hardware", facts.av1Hardware)
            put("port", SessionState.port.takeIf { it > 0 } ?: 27183)
            put("framesSent", SessionState.framesSent)
            put("framesDropped", SessionState.framesDropped)
            put("bitrateNow", SessionState.bitrateNow)
            put("requestedBitrate", SessionState.requestedBitrate)
            put("addresses", addresses)

            // True when the tablet has a USB-tethered address, i.e. the cable is a real path.
            put("wiredReady", NetLinks.hasWired(SessionState.port.takeIf { it > 0 } ?: 27183))
        }
    }

    private fun phaseLabel(): String = when (SessionState.phase) {
        SessionState.Phase.Idle -> "Not started"
        SessionState.Phase.Starting -> "Starting"
        SessionState.Phase.Listening -> "Waiting for the PC"
        SessionState.Phase.Connected -> "Connected"
        SessionState.Phase.Failed -> "Failed"
    }
}
