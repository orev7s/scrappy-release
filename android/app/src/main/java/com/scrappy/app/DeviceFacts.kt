package com.scrappy.app

import android.content.Context
import android.hardware.display.DisplayManager
import android.media.MediaCodecList
import android.view.Display

/**
 * The panel and encoder facts the PC asks for. Read once here so the HTTP status endpoint and the
 * UDP discovery reply cannot drift apart - they are two doors to the same room.
 *
 * Read-only: the refresh rate is observed, never set. That is the whole point of the project.
 */
object DeviceFacts {

    data class Facts(
        val width: Int,
        val height: Int,
        val refreshHz: Double,
        val refreshModes: List<Double>,
        val portrait: Boolean,
        val h264Hardware: Boolean,
        val h265Hardware: Boolean,
        val av1Hardware: Boolean,
    )

    fun read(context: Context): Facts {
        val displayManager = context.getSystemService(Context.DISPLAY_SERVICE) as? DisplayManager
        val display = displayManager?.getDisplay(Display.DEFAULT_DISPLAY)

        val modes = display?.supportedModes?.map { it.refreshRate.toDouble() } ?: emptyList()
        val encoders = try {
            MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos
        } catch (e: Exception) {
            emptyArray()
        }

        fun hasHardware(mime: String): Boolean = encoders.any { info ->
            info.isEncoder &&
                info.supportedTypes.any { it.equals(mime, ignoreCase = true) } &&
                info.name.startsWith("c2.") && !info.name.startsWith("c2.android.")
        }

        // Display.getWidth() reports the current orientation, not the panel's long edge. The PC
        // sizes the encoder in landscape, so report the long edge first either way.
        val rotation = display?.rotation ?: 0
        val rawWidth = display?.width ?: 0
        val rawHeight = display?.height ?: 0
        val portrait = rotation == 1 || rotation == 3 || rawHeight > rawWidth

        return Facts(
            width = maxOf(rawWidth, rawHeight),
            height = minOf(rawWidth, rawHeight),
            refreshHz = display?.refreshRate?.toDouble() ?: 0.0,
            refreshModes = modes.distinct().sorted(),
            portrait = portrait,
            h264Hardware = hasHardware("video/avc"),
            h265Hardware = hasHardware("video/hevc"),
            av1Hardware = hasHardware("video/av01"),
        )
    }
}

/** Reported by both the status endpoint and the discovery reply, so a version mismatch is visible. */
object Version {
    const val NAME = "0.2.1"
}
