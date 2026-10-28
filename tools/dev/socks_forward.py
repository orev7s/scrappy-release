#!/usr/bin/env python3
"""Local TCP forwarder through a SOCKS5 proxy.

Why this exists: this container has no TUN device and no CAP_NET_ADMIN, so
tailscaled runs in userspace mode and exposes only a SOCKS5 proxy instead of a
route. adb cannot speak SOCKS, so we give it a plain localhost port that is
transparently proxied to the device's tailnet address.

    adb connect 127.0.0.1:15555

Not a performance path. This is for diagnostics; any throughput or latency
measured through it describes the proxy, not the product.

Usage:
    python3 socks_forward.py --target 100.97.80.91:5555 --listen 127.0.0.1:15555
"""

import argparse
import asyncio
import struct
import sys


async def socks5_connect(proxy_host: str, proxy_port: int, dst_host: str, dst_port: int) -> tuple:
    reader, writer = await asyncio.open_connection(proxy_host, proxy_port)

    writer.write(b"\x05\x01\x00")  # version 5, 1 method, no auth
    await writer.drain()

    resp = await reader.readexactly(2)
    if resp[0] != 0x05 or resp[1] != 0x00:
        writer.close()
        raise ConnectionError(f"SOCKS5 method rejected: {resp.hex()}")

    parts = dst_host.split(".")
    if len(parts) == 4:
        addr = b"\x01" + bytes(int(p) for p in parts)
    else:
        encoded = dst_host.encode()
        addr = b"\x03" + bytes([len(encoded)]) + encoded

    writer.write(b"\x05\x01\x00" + addr + struct.pack(">H", dst_port))
    await writer.drain()

    reply = await reader.readexactly(4)
    if reply[1] != 0x00:
        writer.close()
        raise ConnectionError(f"SOCKS5 connect failed, code {reply[1]}")

    atyp = reply[3]
    if atyp == 0x01:
        await reader.readexactly(4)
    elif atyp == 0x03:
        length = (await reader.readexactly(1))[0]
        await reader.readexactly(length)
    elif atyp == 0x04:
        await reader.readexactly(16)
    await reader.readexactly(2)  # bound port

    return reader, writer


async def pump(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    try:
        while True:
            chunk = await reader.read(65536)
            if not chunk:
                break
            writer.write(chunk)
            await writer.drain()
    except (ConnectionResetError, BrokenPipeError, asyncio.IncompleteReadError):
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


async def handle(client_reader, client_writer, target_host, target_port, proxy_host, proxy_port):
    try:
        up_reader, up_writer = await socks5_connect(proxy_host, proxy_port, target_host, target_port)
    except Exception as exc:
        print(f"[forward] upstream failed: {exc}", file=sys.stderr, flush=True)
        client_writer.close()
        return

    print(f"[forward] tunnel established -> {target_host}:{target_port}", flush=True)
    await asyncio.gather(
        pump(client_reader, up_writer),
        pump(up_reader, client_writer),
    )


async def main_async(args):
    host, port = args.listen.rsplit(":", 1)
    target_host, target_port = args.target.rsplit(":", 1)

    server = await asyncio.start_server(
        lambda r, w: handle(r, w, target_host, int(target_port), args.proxy_host, args.proxy_port),
        host,
        int(port),
    )
    print(f"[forward] {host}:{port} -> socks5 {args.proxy_host}:{args.proxy_port} "
          f"-> {target_host}:{target_port}", flush=True)
    async with server:
        await server.serve_forever()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True, help="host:port on the tailnet")
    parser.add_argument("--listen", default="127.0.0.1:15555")
    parser.add_argument("--proxy-host", default="127.0.0.1")
    parser.add_argument("--proxy-port", type=int, default=1055)
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
