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

## Audio

Audio is Scrappy's own addition: scrcpy's audio stream format is deliberately not used, because
this app controls both ends and has different priorities. The video bytes above are unchanged and
remain the compatibility promise.

It is a second TCP connection to the same port, so the tablet reads the first line and decides
which of the three things the connection is.

1. Client opens a connection and sends one UTF-8 line, terminated by `\n`:

   ```json
   {"stream":"audio","codec":"aac","sampleRate":48000,"channels":2,"bitrate":128000}
   ```

2. Server replies with a **config packet**: the same 12-byte header as video, with the config flag
   set, whose payload is the AAC `AudioSpecificConfig`. Five bytes for AAC-LC at 48 kHz stereo, and
   the decoder needs it: it carries the profile and the sample rate index.
3. Then one packet per AAC access unit, the same 12-byte header, PTS in microseconds, key-frame
   flag clear. No device name is sent on this connection.

Fixed for now: AAC-LC, 48 kHz, stereo, 128 kbps. The encoder on the tablet is MediaCodec, so the
config packet is whatever `csd-0` says, and the PC's decoder trusts that rather than the request.

Why a separate socket: video and audio have opposite congestion policies. A late video frame is
thrown away and the picture resynchronises at the next keyframe; a late audio frame is a click.
Sharing one TCP connection would let a video stall hold up the sound, and the sound has nowhere else
to go.

Why AAC rather than raw PCM: raw 48 kHz stereo is 1.5 Mbps, which is a real share of a weak Wi-Fi
link that is already the reason the video stutters. The same audio in AAC is 128 kbps.
