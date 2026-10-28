# Scrappy

Mirror an Android tablet's screen to a Windows PC at 60 fps, over Wi-Fi or a USB cable, without touching the tablet's display mode.

Most mirroring tools put the device into an external display mode first. On a 120 or 144 Hz tablet that has a side effect: the panel drops to 60 Hz and stays there for as long as you mirror. If you also play games on the tablet, that is a bad trade. Scrappy reads the image the tablet is already drawing, encodes it, and sends it over the network. It never asks the system to change the resolution or the refresh rate, so the panel keeps its rate and the tablet keeps its own audio.

The aim is a mirror that feels like a monitor cable. Low latency, steady frame rate, and nothing to configure unless you want to. The PC decides everything: resolution, frame rate, bitrate and codec. The tablet is a thin app that captures, encodes and sends.

## What you get

- 60 fps at up to 2560x1708, in H.264 or H.265
- Wi-Fi or USB cable, with no adb and no developer options
- automatic discovery, so you never type an IP address
- the full scrcpy option set in the settings, saved automatically
- a live lag figure, so a weak link is visible instead of mysterious

## Requirements

- Windows 10 or 11, 64 bit
- an Android 11 or newer tablet
- both ends on the same network, or one USB cable

## Install

1. **PC**: unzip the release anywhere and run `Scrappy.App.exe`. On the first run it downloads the FFmpeg libraries it needs into `%LOCALAPPDATA%\Scrappy`.
2. **Tablet**: install the APK, open it, and allow screen capture when Android asks.
3. On the PC, pick the tablet from the list and press **Start**.

The tablet app answers a discovery probe while it is open, so the PC usually lists it before you press anything.

## Using the cable

USB tethering runs the same stream over the USB link. It is faster than Wi-Fi and does not compete with anything else on the network.

1. Plug the cable in.
2. On the tablet: Settings, Hotspot and tethering, USB tethering on.
3. Press **Scan** on the PC. The tablet now appears twice, and the one marked **Wired** is the cable.

Tethering does not turn on by itself when you plug the cable in. Until it does, the PC sees a USB device with no address, and the scan tells you that in as many words.

## When it stutters

The status bar has four numbers that answer most questions: fps, Mbps, lag and decode time.

- **Lag** is how much video is waiting to be shown. Above a few hundred milliseconds, either the link or the PC is behind. Scrappy throws late frames away on purpose instead of letting a backlog build, so the picture may skip for a moment rather than drift a few seconds behind.
- **Mbps** far below the bitrate you set means the link cannot carry it. The tablet lowers its own bitrate to fit, and its screen shows the rate it settled on next to the rate that was requested.
- **Decode time** near or above 16 ms means the PC cannot decode that size in real time. Use a smaller resolution, or H.264, which is much cheaper to decode than H.265.

If the stream stops, the PC asks the tablet what it was doing and repeats the answer in the status line, instead of just saying the connection closed.

## Layout

```
src/            Windows app and the shared protocol, settings, discovery and stream code
android/        tablet app: capture, encoder, TCP server, discovery responder
schema/         scrcpy-options.json, generated from scrcpy's option table
tools/          schema generator, verification suite, synthetic test device
docs/           wire format notes
testdata/       a short H.264 clip used by the tests
```

## Build from source

Needs the .NET 8 SDK. The Windows package is self contained, so no runtime is needed on the target machine.

```sh
dotnet build Scrappy.sln
dotnet run --project tools/Scrappy.Verify -- all
dotnet publish src/Scrappy.App -c Release -r win-x64 --self-contained true -o dist/win-x64
```

The tablet app needs JDK 17, Android SDK 35 and Gradle 8.7.

```sh
cd android
gradle assembleDebug
```

`tools/Scrappy.Verify` runs without a tablet. It covers the handshake byte order, the decoder, discovery, the link classification, the cable sweep, session teardown and reconnection, the preset suggestion, and a congestion case where the receiver is deliberately slower than the sender.

## Licence

Apache 2.0, in `LICENSE`. Scrappy speaks scrcpy's wire format and generates its settings table from scrcpy's option list. See `NOTICE` for attribution.
