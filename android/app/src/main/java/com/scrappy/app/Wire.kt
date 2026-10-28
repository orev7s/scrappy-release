package com.scrappy.app

import java.io.OutputStream
import java.nio.ByteBuffer

/**
 * The Android half of scrcpy's video wire format. Byte-for-byte the same as the C client and the
 * C# receiver: 64-byte device name, then 12-byte headers, all big-endian.
 *
 * The only thing that differs from upstream is what sits underneath: a plain TCP socket instead of
 * an adb-forwarded one, so the far end can be an installed app.
 */
object Wire {
    const val DEVICE_NAME_LENGTH = 64
    const val HEADER_SIZE = 12

    private const val FLAG_SESSION: Int = 1 shl 31
    private const val FLAG_CONFIG: Long = 1L shl 62
    private const val FLAG_KEY_FRAME: Long = 1L shl 61

    fun writeDeviceName(out: OutputStream, name: String) {
        val field = ByteArray(DEVICE_NAME_LENGTH)
        val bytes = name.toByteArray(Charsets.UTF_8)
        System.arraycopy(bytes, 0, field, 0, minOf(bytes.size, field.size))
        out.write(field)
    }

    /** Announces the frame geometry. Sent once at the start and again if the size changes. */
    fun writeSessionHeader(out: OutputStream, width: Int, height: Int) {
        val header = ByteBuffer.allocate(HEADER_SIZE)
        header.putInt(FLAG_SESSION)
        header.putInt(width)
        header.putInt(height)
        out.write(header.array())
    }

    fun writePacketHeader(out: OutputStream, pts: Long, isConfig: Boolean, isKeyFrame: Boolean, length: Int) {
        var ptsFlags = pts and ((1L shl 61) - 1)
        if (isConfig) ptsFlags = ptsFlags or FLAG_CONFIG
        if (isKeyFrame) ptsFlags = ptsFlags or FLAG_KEY_FRAME

        val header = ByteBuffer.allocate(HEADER_SIZE)
        header.putLong(ptsFlags)
        header.putInt(length)
        out.write(header.array())
    }

    fun writeConfig(out: OutputStream, csd: ByteArray) {
        if (csd.isEmpty()) return
        writePacketHeader(out, 0, isConfig = true, isKeyFrame = false, length = csd.size)
        out.write(csd)
    }

    fun writeFrame(out: OutputStream, buffer: ByteBuffer, pts: Long, isKeyFrame: Boolean) {
        val payload = ByteArray(buffer.remaining())
        buffer.get(payload)
        writePacketHeader(out, pts, isConfig = false, isKeyFrame = isKeyFrame, length = payload.size)
        out.write(payload)
    }

    /**
     * Header and payload as one array, so a frame leaves as one write and one TCP segment.
     *
     * The two-write form was not wrong, but with TCP_NODELAY every write is pushed on its own: a
     * 12-byte header followed by a 25 KB payload is two segments where one would do, and the tiny
     * one is pure overhead on a weak Wi-Fi link.
     */
    fun encodeFrame(pts: Long, isKeyFrame: Boolean, payload: ByteBuffer): ByteArray {
        val size = payload.remaining()
        val block = ByteArray(HEADER_SIZE + size)
        encodePacketHeader(block, pts, isConfig = false, isKeyFrame = isKeyFrame, length = size)
        payload.get(block, HEADER_SIZE, size)
        return block
    }

    /** The config packet in the same one-array form: parameter sets must beat the first frame out. */
    fun encodeConfig(csd: ByteArray): ByteArray {
        if (csd.isEmpty()) return ByteArray(0)
        val block = ByteArray(HEADER_SIZE + csd.size)
        encodePacketHeader(block, 0, isConfig = true, isKeyFrame = false, length = csd.size)
        System.arraycopy(csd, 0, block, HEADER_SIZE, csd.size)
        return block
    }

    private fun encodePacketHeader(target: ByteArray, pts: Long, isConfig: Boolean, isKeyFrame: Boolean, length: Int) {
        var ptsFlags = pts and ((1L shl 61) - 1)
        if (isConfig) ptsFlags = ptsFlags or FLAG_CONFIG
        if (isKeyFrame) ptsFlags = ptsFlags or FLAG_KEY_FRAME

        val header = ByteBuffer.wrap(target, 0, HEADER_SIZE)
        header.putLong(ptsFlags)
        header.putInt(length)
    }
}
