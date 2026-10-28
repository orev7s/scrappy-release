#!/usr/bin/env python3
"""Headless scrcpy stream probe.

Connects to a scrcpy server running with tunnel_forward=true, consumes the video
stream for N seconds, and reports frame counts, bitrate and configuration packets.
Audio is connected (the server accepts it as a second connection and blocks
without it) but not analysed.

This exists to answer device-level questions, not to measure throughput: the
link it runs over may be far slower than the encoder.

Usage:
    adb forward tcp:27184 localabstract:scrcpy_0000000d
    python3 probe_capture.py --port 27184 --seconds 20
"""

import argparse
import json
import socket
import threading
import time

HEADER = 12
FLAG_CONFIG = 1 << 62
FLAG_KEY = 1 << 61
PTS_MASK = FLAG_KEY - 1


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError(f"stream closed after {len(buf)}/{n} bytes")
        buf += chunk
    return bytes(buf)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=27184)
    ap.add_argument("--seconds", type=float, default=20.0)
    args = ap.parse_args()

    video = socket.create_connection((args.host, args.port), timeout=10)
    dummy = recv_exact(video, 1)  # server writes one byte so the client can detect failure

    # The server accepts audio only after video; without this connection it blocks.
    audio = socket.create_connection((args.host, args.port), timeout=10)

    audio_bytes = 0
    audio_stop = threading.Event()

    def drain_audio():
        nonlocal audio_bytes
        audio.settimeout(1.0)
        while not audio_stop.is_set():
            try:
                chunk = audio.recv(65536)
                if not chunk:
                    break
                audio_bytes += len(chunk)
            except socket.timeout:
                continue
            except OSError:
                break

    audio_thread = threading.Thread(target=drain_audio, daemon=True)
    audio_thread.start()

    name = recv_exact(video, 64).split(b"\x00")[0].decode("utf-8", "replace")

    frames = 0
    configs = 0
    keys = 0
    total_bytes = 0
    geometry = None
    last_pts = None
    pts_regressions = 0
    start = time.time()
    window_start = start
    window_frames = 0
    per_second = []

    video.settimeout(5)
    while time.time() - start < args.seconds:
        try:
            header = recv_exact(video, HEADER)
        except (socket.timeout, ConnectionError):
            break

        if header[0] & 0x80:  # session header
            w = int.from_bytes(header[4:8], "big")
            h = int.from_bytes(header[8:12], "big")
            geometry = f"{w}x{h}"
            continue

        pts_flags = int.from_bytes(header[0:8], "big")
        length = int.from_bytes(header[8:12], "big")
        payload = recv_exact(video, length)
        total_bytes += length + HEADER

        if pts_flags & FLAG_CONFIG:
            configs += 1
            continue

        if pts_flags & FLAG_KEY:
            keys += 1

        pts = pts_flags & PTS_MASK
        if last_pts is not None and pts < last_pts:
            pts_regressions += 1
        last_pts = pts

        frames += 1
        window_frames += 1

        now = time.time()
        if now - window_start >= 1.0:
            per_second.append(round(window_frames / (now - window_start), 1))
            window_frames = 0
            window_start = now

    elapsed = time.time() - start
    summary = {
        "device_name": name,
        "geometry": geometry,
        "seconds": round(elapsed, 2),
        "frames": frames,
        "fps_avg": round(frames / elapsed, 2) if elapsed else 0,
        "fps_per_second": per_second,
        "config_packets": configs,
        "key_frames": keys,
        "payload_mb": round(total_bytes / (1024 * 1024), 2),
        "mbps": round(total_bytes * 8 / elapsed / 1_000_000, 2) if elapsed else 0,
        "pts_regressions": pts_regressions,
        "audio_bytes": audio_bytes,
        "audio_kbps": round(audio_bytes * 8 / elapsed / 1000, 1) if elapsed else 0,
    }
    print(json.dumps(summary, indent=2))

    audio_stop.set()
    for s in (video, audio):
        try:
            s.close()
        except Exception:
            pass


if __name__ == "__main__":
    main()
