package com.scrappy.app

/**
 * What the session is doing, visible to the activity and to the notification.
 *
 * The app previously reported state by changing a button label in the activity. When the service
 * died - and it could, because an exception in onStartCommand kills the whole process - nothing on
 * screen changed, so "started" was indistinguishable from "crashed". Every state change now lands
 * here, and the activity reads it on a timer instead of assuming.
 *
 * One rule worth stating, because breaking it hid a real bug for a whole version: a failure is
 * sticky until the next session actually starts. The server used to set "Waiting for the PC" when
 * a session ended, including the session that had just failed to start its encoder - so the tablet
 * showed a friendly "Waiting for the PC" while the PC had been told the stream was closed. The
 * reason is now kept, shown on the tablet, and served over /status so the PC can repeat it.
 */
object SessionState {

    enum class Phase { Idle, Starting, Listening, Connected, Failed }

    @Volatile var phase: Phase = Phase.Idle
        private set

    @Volatile var message: String = "Not mirroring"
        private set

    /** Last exception, full text, kept so it can be read off the screen and reported. */
    @Volatile var lastError: String? = null
        private set

    /** One line of [lastError], for the status endpoint and the PC's error message. */
    @Volatile var lastErrorLine: String = ""
        private set

    @Volatile var port: Int = 0
        private set

    @Volatile var framesSent: Long = 0
        private set

    @Volatile var framesDropped: Long = 0
        private set

    /** What the encoder is running at now, which may be below what the PC asked for. */
    @Volatile var bitrateNow: Int = 0
        private set

    @Volatile var requestedBitrate: Int = 0
        private set

    @Volatile var startedAt: Long = 0
        private set

    /** What the audio side is doing. Empty when no PC has asked for sound. */
    @Volatile var audioMessage: String = ""
        private set

    fun starting(port: Int) {
        phase = Phase.Starting
        this.port = port
        message = "Starting"
        clearError()
    }

    fun listening(port: Int) {
        phase = Phase.Listening
        this.port = port
        startedAt = System.currentTimeMillis()
        message = "Waiting for the PC on port $port"
    }

    /** A PC has connected and the encoder is being opened. Not yet a picture. */
    fun connecting(peer: String) {
        phase = Phase.Connected
        message = "PC connected ($peer), starting the encoder"
        clearError()
        framesSent = 0
        framesDropped = 0
    }

    fun mirrored(peer: String, width: Int, height: Int, bitrate: Int) {
        phase = Phase.Connected
        message = "Connected: $peer - ${width}x$height at ${bitrate / 1_000_000} Mbps"
        bitrateNow = bitrate
        requestedBitrate = bitrate
    }

    fun frameSent() {
        framesSent++
    }

    fun audio(format: String) {
        audioMessage = "Audio: $format"
    }

    fun audioStopped() {
        audioMessage = ""
    }

    fun audioError(reason: String) {
        audioMessage = "Audio stopped: $reason"
    }

    fun link(dropped: Long, bitrate: Int) {
        framesDropped = dropped
        bitrateNow = bitrate
    }

    /** Called when the link forced the encoder down. Worth saying out loud, it explains softness. */
    fun throttled(bitrate: Int) {
        bitrateNow = bitrate
    }

    fun stopped(reason: String = "Not mirroring") {
        phase = Phase.Idle
        message = reason
        bitrateNow = 0
        framesDropped = 0
        audioMessage = ""
    }

    fun failed(error: Throwable, where: String) {
        phase = Phase.Failed
        message = "$where: ${error.javaClass.simpleName}: ${error.message}"
        rememberError(error.stackTraceToString(), message)
    }

    fun failed(message: String) {
        phase = Phase.Failed
        this.message = message
        rememberError(message, message)
    }

    private fun rememberError(full: String, line: String) {
        lastError = full
        lastErrorLine = line
    }

    private fun clearError() {
        lastError = null
        lastErrorLine = ""
    }
}
