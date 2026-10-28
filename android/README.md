# Scrappy Android app

A thin shell. It captures the screen, encodes it, and writes scrcpy's wire format to a plain TCP
socket. Every setting is decided on the PC and pushed at connect, so this app has one screen:
start, stop, and the address to connect to.

On Android 14+ an installed app must ask for MediaProjection consent once per session. That tap is
the reason this app exists at all; the adb path avoids it, at the cost of needing adb.

## Ports

| Port | Purpose |
|---|---|
| `27183` | everything: the session, and `GET /status` |

One port. The PC opens a connection and either asks `GET /status` or pushes a JSON line of settings
followed by the stream; the server reads the first line and knows which. An earlier version used
port + 1 for status, which meant two addresses, two firewall rules, and a check that failed while
the session worked. It also confused anyone reading the two ports off the error text.

Status is read-only: the app reports the panel's refresh rate, it never sets it.

## Build

```sh
cd android
gradle assembleDebug           # needs an Android SDK, ANDROID_HOME set, JDK 17
adb install app/build/outputs/apk/debug/app-debug.apk
```

Built and released from this repository. The project is written against SDK 35 with no
dependencies except `androidx.core`, and the code paths it uses are all public API -
MediaProjection, VirtualDisplay, MediaCodec surface input - because an ordinary app does not get
the shell UID's privileges and cannot reuse upstream's hidden-API `SurfaceEncoder`.

## Protocol notes

- Handshake order is fixed: 64-byte device name, session header, config packet, frames.
- The PC may send one JSON line of settings before the handshake. If it sends nothing within
  400 ms, defaults are used (2560x1708, h265, 12 Mbps, 60 fps).
- Frames are written as one packet per encoder output buffer, with the key-frame flag taken from
  `BUFFER_FLAG_KEY_FRAME` and the presentation timestamp from the buffer info.
