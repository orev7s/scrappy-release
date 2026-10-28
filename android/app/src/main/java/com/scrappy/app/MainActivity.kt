package com.scrappy.app

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView

/**
 * One screen: start, stop, and what the session is actually doing.
 *
 * The screen no longer assumes a start worked. It reads [SessionState] on a timer, so a crash in
 * the service shows up as a red line with the exception in it, rather than as an app that closed
 * with nothing to show for itself.
 *
 * It also prints the addresses it holds, labelled by link. That list is the honest answer to "is
 * the cable working?": a tethering address appears when USB tethering is on, and only then.
 */
class MainActivity : Activity() {

    private val port = 27183
    private val consentRequest = 1001
    private val notificationRequest = 1002

    private lateinit var headline: TextView
    private lateinit var detail: TextView
    private lateinit var toggle: Button
    private lateinit var errorView: TextView
    private lateinit var links: TextView
    private lateinit var hint: TextView

    private val poll = Handler(Looper.getMainLooper())
    private var wifiLock: WifiManager.WifiLock? = null

    private val refresh = object : Runnable {
        override fun run() {
            render()
            poll.postDelayed(this, 400)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        headline = TextView(this).apply {
            text = "Scrappy"
            textSize = 24f
        }

        detail = TextView(this).apply {
            text = "Not mirroring"
            textSize = 15f
            setPadding(0, 10, 0, 6)
        }

        errorView = TextView(this).apply {
            textSize = 12f
            setPadding(0, 8, 0, 0)
            setTextColor(0xFFFF6B6B.toInt())
        }

        links = TextView(this).apply {
            textSize = 12f
            setPadding(0, 14, 0, 0)
            setTextColor(0xFFB8BEC9.toInt())
        }

        hint = TextView(this).apply {
            textSize = 12f
            setPadding(0, 8, 0, 0)
            setTextColor(0xFFB8BEC9.toInt())
        }

        toggle = Button(this).apply {
            text = "Start"

            // The label and the action have to agree. After a failure the button says "Start", so
            // it must start: it used to call stop() and leave the user with a dead session and a
            // button that did nothing visible.
            setOnClickListener {
                when (SessionState.phase) {
                    SessionState.Phase.Idle, SessionState.Phase.Failed -> begin()
                    else -> stop()
                }
            }
        }

        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(56, 120, 56, 56)
            addView(headline)
            addView(detail)
            addView(toggle)
            addView(links)
            addView(hint)
            addView(errorView)
        }

        setContentView(layout)

        // Answer the PC's discovery probe from the moment the app is on screen, so the tablet is
        // already in its list before Start is pressed.
        DiscoveryResponder.acquire(this)
    }

    override fun onDestroy() {
        DiscoveryResponder.release()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        poll.post(refresh)
    }

    override fun onPause() {
        super.onPause()
        poll.removeCallbacks(refresh)
    }

    private fun begin() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            // Without this the foreground notification is invisible on Android 13+, which is the
            // other half of "it says started but there is no sign of it".
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), notificationRequest)
            return
        }

        requestConsent()
    }

    private fun requestConsent() {
        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        startActivityForResult(manager.createScreenCaptureIntent(), consentRequest)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == notificationRequest) {
            // Denied is not fatal; the session still runs, it is just quiet.
            requestConsent()
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)

        if (requestCode != consentRequest) {
            return
        }

        if (resultCode != RESULT_OK || data == null) {
            SessionState.failed("Screen capture was declined")
            return
        }

        acquireWifiLock()

        val intent = Intent(this, CaptureService::class.java).apply {
            action = CaptureService.ACTION_START
            putExtra(CaptureService.EXTRA_RESULT_CODE, resultCode)
            putExtra(CaptureService.EXTRA_RESULT_DATA, data)
            putExtra(CaptureService.EXTRA_PORT, port)
        }

        try {
            if (Build.VERSION.SDK_INT >= 26) startForegroundService(intent) else startService(intent)
        } catch (t: Throwable) {
            SessionState.failed(t, "Starting the service")
        }
    }

    private fun stop() {
        startService(Intent(this, CaptureService::class.java).apply { action = CaptureService.ACTION_STOP })
        releaseWifiLock()
    }

    private fun render() {
        val phase = SessionState.phase
        detail.text = SessionState.message

        when (phase) {
            SessionState.Phase.Idle -> {
                toggle.text = "Start"
                errorView.text = SessionState.lastError?.let { firstLine(it) } ?: ""
            }

            SessionState.Phase.Starting, SessionState.Phase.Listening -> {
                toggle.text = "Stop"
                errorView.text = ""
            }

            SessionState.Phase.Connected -> {
                toggle.text = "Stop"
                val frames = "  ${SessionState.framesSent} frames"

                // Dropped frames and a reduced bitrate are the app saying "this link is the
                // problem", which is worth showing rather than hiding behind a smooth picture.
                val budget = when {
                    SessionState.framesDropped > 0 && SessionState.bitrateNow > 0 ->
                        "  ${SessionState.framesDropped} dropped, ${SessionState.bitrateNow / 1_000_000} of " +
                            "${SessionState.requestedBitrate / 1_000_000} Mbps"
                    SessionState.bitrateNow > 0 -> "  ${SessionState.bitrateNow / 1_000_000} Mbps"
                    else -> ""
                }

                detail.text = SessionState.message + frames + budget
                errorView.text = ""
            }

            SessionState.Phase.Failed -> {
                toggle.text = "Start"
                errorView.text = SessionState.message + "\n" + (SessionState.lastError?.let { firstLine(it) } ?: "")
            }
        }

        links.text = "This tablet: " + NetLinks.describe(port)
        hint.text = if (NetLinks.hasWired(port)) {
            "The cable is a working path: the PC lists it as Wired and it does not share the Wi-Fi."
        } else {
            "For the cable: plug in USB, then Settings -> Hotspot & tethering -> USB tethering. " +
                "It appears here as an address on Wired."
        }
    }

    private fun firstLine(text: String): String =
        text.lineSequence().firstOrNull { it.isNotBlank() }?.trim() ?: ""

    private fun acquireWifiLock() {
        try {
            val manager = application.getSystemService(Context.WIFI_SERVICE) as WifiManager

            // LOW_LATENCY is the mode built for exactly this: it keeps the radio out of the power
            // save states that turn a smooth stream into a stuttering one every few seconds.
            var mode = WifiManager.WIFI_MODE_FULL_HIGH_PERF
            if (Build.VERSION.SDK_INT >= 29) {
                mode = WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            }

            wifiLock = manager.createWifiLock(mode, "scrappy:mirror").apply { acquire() }
        } catch (e: Exception) {
            // A Wi-Fi lock is an optimisation, not a requirement.
        }
    }

    private fun releaseWifiLock() {
        try {
            wifiLock?.release()
        } catch (e: Exception) {
            // Already released.
        }
        wifiLock = null
    }
}
