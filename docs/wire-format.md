# Wire format

Derived from `app/src/demuxer.c`, `app/src/server.c` and `server/src/main/java/.../Streamer.java`
at commit `a60891a` (v5.0.1). This is what Scrappy implements on both ends; it is unchanged
from upstream. Only the socket underneath it differs.

All integers are **big-endian**.

## Establishing the video stream

1. Client opens the video socket.
2. Server sends exactly **64 bytes**: the device name, UTF-8, NUL-padded.
   (`SC_DEVICE_NAME_FIELD_LENGTH`, `app/src/server.h:16`)
3. Both sides then exchange 12-byte headers, each optionally followed by a payload.

## The 12-byte header

Two header shapes share the same 12 bytes, separated by the top bit of byte 0.

### Session header

Set when `header[0] & 0x80` is non-zero.

| Offset | Size | Meaning |
|---|---|---|
| 0 | 4 | flags; bit 31 marks this as a session header |
| 4 | 4 | video width |
| 8 | 4 | video height |

`client_resized` is `header[3] & 1`. A session header has **no payload**; it updates the
receiver's notion of the video geometry mid-stream (rotation, resize).

### Packet header

Everything else.

| Offset | Size | Meaning |
|---|---|---|
| 0 | 8 | `pts_flags` |
| 8 | 4 | payload length in bytes, never zero |

Within `pts_flags`:

| Bit | Constant | Meaning |
|---|---|---|
| 63 | - | session marker (never set here) |
| 62 | `SC_PACKET_FLAG_CONFIG` | codec configuration payload (SPS/PPS), not a frame |
| 61 | `SC_PACKET_FLAG_KEY_FRAME` | key frame |
| 0-60 | `SC_PACKET_PTS_MASK` | presentation timestamp |

So `pts = pts_flags & ((1 << 61) - 1)`, and a config packet carries no meaningful pts.

## Consequences for the receiver

- A config packet must reach the decoder **before** the first frame or decoding fails.
- Frames are not guaranteed to arrive in decode order after a reconnect; a key frame is the
  only safe resync point, which is why `I-frame interval` is a setting worth exposing.
- Payload length is the only framing; there is no length prefix above the packet level, so a
  desynchronised reader cannot resynchronise by scanning. Any read error is terminal - drop
  the socket and reconnect rather than attempting to resync.
