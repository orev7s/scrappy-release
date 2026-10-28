#!/usr/bin/env python3
"""
A stand-in for the GitHub Releases API, for driving the update dialog without publishing a release.

It answers the two requests GitHubReleaseFeed makes:

    GET /repos/orev7s/scrappy-release/releases/latest
    GET /repos/orev7s/scrappy-release/tags

The tag it reports is read from a file on every request, so a run can publish a newer release
between two launches of the app. Every request is logged, which is how the driver proves the app
really asked.

    python3 stub_releases.py <port-file> <log-file> [tag-file]

Writes the port it bound to into <port-file> once it is listening.
"""

import json
import os
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

OWNER = "orev7s"
REPO = "scrappy-release"

port_file, log_file = sys.argv[1], sys.argv[2]
tag_file = sys.argv[3] if len(sys.argv) > 3 else "stub-tag.txt"


def current_tag():
    try:
        with open(tag_file, encoding="utf-8") as handle:
            return handle.read().strip()
    except OSError:
        return "v0.3.0"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):  # noqa: N802 - the name is the framework's
        with open(log_file, "a", encoding="utf-8") as log:
            log.write(f"{self.path}\n")

        tag = current_tag()
        if self.path.startswith(f"/repos/{OWNER}/{REPO}/releases/latest"):
            body = {
                "tag_name": tag,
                "name": f"Scrappy {tag.lstrip('v')}",
                "html_url": f"https://github.com/{OWNER}/{REPO}/releases/tag/{tag}",
                "prerelease": False,
                "draft": False,
            }
        elif self.path.startswith(f"/repos/{OWNER}/{REPO}/tags"):
            body = [{"name": tag}, {"name": "v0.2.1"}]
        else:
            self.send_error(404)
            return

        payload = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, fmt, *args):
        with open(log_file, "a", encoding="utf-8") as log:
            log.write(f"request {args[0] if args else ''}\n")


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
port = server.server_address[1]

with open(port_file, "w", encoding="utf-8") as handle:
    handle.write(str(port))

sys.stderr.write(f"stub releases API on 127.0.0.1:{port}, tag file {tag_file}\n")
sys.stderr.flush()

try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
    time.sleep(0.05)
