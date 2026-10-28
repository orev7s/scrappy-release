#!/usr/bin/env python3
"""Generate the Scrappy settings schema from scrcpy's CLI option table.

Source of truth: app/src/cli.c -> `static const struct sc_option options[]`.
Nothing is hand-maintained here; re-run this script after any upstream update.

Usage:
    python3 tools/gen_schema.py > schema/scrcpy-options.json
"""

import json
import re
import subprocess
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLI_C = ROOT / "app" / "src" / "cli.c"

# Group rules, evaluated in order against the long option name.
# Anything unmatched lands in "advanced" so no option can be silently dropped.
GROUPS = [
    ("video", re.compile(r"^(video|max-fps|max-size|min-size|bit-rate|codec|encoder|"
                         r"i-frame|display-buffer|crop|no-downsize|camera)")),
    ("audio", re.compile(r"^audio")),
    ("recording", re.compile(r"^(record|no-playback|no-display)")),
    ("input", re.compile(r"^(keyboard|mouse|gamepad|shortcut|no-key-repeat|"
                         r"legacy-paste|raw-key|no-clipboard|hid|otg|power-off|keep-active|"
                         r"screen-off|no-control)")),
    ("display", re.compile(r"^(display|orientation|capture-orientation|angle|render|"
                           r"background|new-display|vd|start-app|list-apps|window|fullscreen|"
                           r"always-on-top|no-window|no-vd)")),
    ("transport", re.compile(r"^(tcpip|tunnel|serial|select-usb|port|address|reconnect|"
                             r"kill-adb|time-limit|no-cleanup|no-power|power-on|print-fps)")),
]


def group_for(longopt: str) -> str:
    for name, pattern in GROUPS:
        if pattern.search(longopt):
            return name
    return "advanced"


def extract_text(body: str) -> str:
    """Concatenate adjacent C string literals in a `.text =` field."""
    m = re.search(r"\.text\s*=\s*(.*?)(?=\n\s*\.[a-z_]+\s*=|\Z)", body, re.S)
    if not m:
        return ""
    raw = m.group(1)
    parts = re.findall(r'"((?:[^"\\]|\\.)*)"', raw)
    text = "".join(parts)
    return (
        text.replace("\\n", "\n")
        .replace('\\"', '"')
        .replace("\\\\", "\\")
        .strip()
    )


def field(body: str, name: str):
    m = re.search(rf"\.{name}\s*=\s*([^,\n]+)", body)
    return m.group(1).strip().strip('"') if m else None


def shortopt(body: str):
    """`.shortopt = 'm'` uses char literals, not C strings."""
    m = re.search(r"\.shortopt\s*=\s*'(.)'", body)
    return m.group(1) if m else None


def parse_enums(text: str):
    """Best-effort enum detection from help text."""
    values = re.findall(r'^\s+-\s+"([^"]+)"', text, re.M)
    if values:
        return values
    m = re.search(r"\(([a-z0-9_\-]+(?:,\s*[a-z0-9_\-]+)+(?:\s+or\s+[a-z0-9_\-]+)?)\)", text)
    if m:
        return [v.strip() for v in re.split(r",|\bor\b", m.group(1)) if v.strip()]
    m = re.search(r"Possible values are:\s*([a-z0-9_\-]+(?:\s*,\s*[a-z0-9_\-]+)+)", text)
    if m:
        return [v.strip() for v in m.group(1).split(",")]
    return None


def parse_default(text: str):
    m = re.search(r"Default is\s+([^\n\.]+)\.", text)
    return m.group(1).strip() if m else None


def source_commit():
    """The commit this table was read from, so the schema can be traced back to upstream."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    try:
        out = subprocess.check_output(
            ["git", "-C", repo, "log", "-1", "--format=%h", "--", "app/src/cli.c"],
            stderr=subprocess.DEVNULL,
        )
        return out.decode().strip() or None
    except (OSError, subprocess.CalledProcessError):
        return None


def main() -> int:
    src = CLI_C.read_text(encoding="utf-8", errors="replace")

    start = src.index("static const struct sc_option options[] = {")
    depth = 0
    i = src.index("{", start)
    end = None
    for j in range(i, len(src)):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                end = j
                break
    if end is None:
        print("could not locate option table end", file=sys.stderr)
        return 1

    block = src[i : end + 1]
    entries = re.findall(r"\{\s*(.*?)\n\s*\},", block, re.S)

    options = []
    for body in entries:
        longopt = field(body, "longopt")
        if not longopt:
            continue
        text = extract_text(body)
        argdesc = field(body, "argdesc")
        optional = "optional_arg = true" in body

        if argdesc is None:
            kind = "flag"
        elif optional:
            kind = "optional-value"
        else:
            kind = "value"

        options.append(
            {
                # One stable id, derived from the flag itself: --max-size -> max_size.
                # (longopt_id is not set for every option, which used to yield a mix of
                # "max-size" and "max_fps" in the same file.)
                "id": longopt.strip("-").replace("-", "_").lower(),
                "flag": "--" + longopt,
                "short": ("-" + shortopt(body)) if shortopt(body) else None,
                "kind": kind,
                "arg": argdesc,
                "group": group_for(longopt),
                "enum": parse_enums(text) if text else None,
                "default": parse_default(text) if text else None,
                "help": text,
                "exposed": bool(text),  # text == NULL means hidden from --help
            }
        )

    options.sort(key=lambda o: (o["group"], o["flag"]))
    out = {
        "schema_version": 1,
        "generated_from": "app/src/cli.c (static const struct sc_option options[])",
        "source_commit": source_commit(),
        "count": len(options),
        "groups": sorted({o["group"] for o in options}),
        "options": options,
    }
    json.dump(out, sys.stdout, indent=2, ensure_ascii=False)
    print(f"\n// {len(options)} options parsed", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
