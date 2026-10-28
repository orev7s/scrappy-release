package com.scrappy.app

/**
 * Decides what bitrate the encoder should be running at, from what the link actually did.
 *
 * The tablet used to encode at whatever the PC asked for and keep doing it, whatever the link
 * could carry. On a weak Wi-Fi that means every frame is produced faster than it can be sent, the
 * sender is permanently behind, and the picture is permanently late. Dropping frames keeps the
 * delay bounded but leaves the picture either frozen or blocky; lowering the bitrate is what
 * makes the link keep up at all.
 *
 * The control law is deliberately the boring one - multiplicative decrease, additive increase:
 *
 *  - a window with any drop costs 35% of the current rate, immediately;
 *  - four clean windows in a row buy back 1 Mbps, up to the rate the PC asked for.
 *
 * It converges in a few seconds, it never oscillates fast enough to be visible as quality
 * pumping, and it never exceeds what the user chose. Pure arithmetic, so it can be reasoned about
 * without a tablet: [Scrappy.Verify] checks the same curve.
 */
class BitrateController(
    private val requested: Int,
    private val floor: Int = MIN_BITRATE,
) {
    companion object {
        /** Below this the picture is not worth calling a mirror; the app reports failure instead. */
        const val MIN_BITRATE = 1_500_000

        /** Windows of clean throughput needed before trying to go back up. */
        const val RECOVERY_WINDOWS = 4

        const val RECOVERY_STEP = 1_000_000

        const val DECREASE_NUMERATOR = 65
        const val DECREASE_DENOMINATOR = 100
    }

    /** What the encoder is set to right now. */
    var current: Int = requested.coerceAtLeast(floor)
        private set

    /** True when the encoder is running below what was asked for. */
    val isThrottled: Boolean
        get() = current < requested

    private var cleanWindows = 0

    /**
     * Feeds one measurement window. Returns the bitrate to apply, or null when nothing changed -
     * so the caller can avoid touching the encoder on every quiet second.
     */
    fun onWindow(droppedFrames: Int, sentBitsPerSecond: Long): Int? {
        if (droppedFrames > 0 || sentBitsPerSecond <= 0) {
            cleanWindows = 0
            return decrease()
        }

        cleanWindows++
        if (cleanWindows < RECOVERY_WINDOWS) {
            return null
        }

        cleanWindows = 0
        return increase()
    }

    private fun decrease(): Int? {
        // A rate that is already at the floor stays there: dropping further would only make the
        // picture worse without making it arrive.
        if (current <= floor) {
            return null
        }

        val next = (current.toLong() * DECREASE_NUMERATOR / DECREASE_DENOMINATOR).toInt().coerceAtLeast(floor)
        if (next >= current) {
            return null
        }

        current = next
        return current
    }

    private fun increase(): Int? {
        if (current >= requested) {
            return null
        }

        current = (current + RECOVERY_STEP).coerceAtMost(requested)
        return current
    }
}
