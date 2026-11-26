"""Receives the browser game's log reports (POST /clientlog) and keeps them for the staff.

The page (the client's web/client/start.mjs) sends what the game wrote to its console every
30 s while a pilot is in the game, and once more when the page closes. Each report is one
JSON line in <dir>/<UTC date>.jsonl. No address is stored. Old days are deleted after
--keep-days, and nothing more is written once the folder holds --max-mb.

Runs behind Caddy on loopback (deploy/install-clientlog.sh). Standard library only.
"""

import argparse
import json
import os
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

MAX_BODY = 256 * 1024
MAX_LINES = 400
MAX_LINE = 2000
# Per sender address: a report every 30 s, plus the closing one, with room to spare.
RATE = 1 / 10
BURST = 6
TEXT_FIELDS = {"id": 64, "version": 32, "pilot": 32, "arena": 32, "ua": 400, "platform": 64, "screen": 32}


def clean(report):
    """The report as it is stored, or None when it is not one."""
    if not isinstance(report, dict) or not isinstance(report.get("lines"), list):
        return None
    out = {}
    for field, limit in TEXT_FIELDS.items():
        value = report.get(field)
        out[field] = value[:limit] if isinstance(value, str) else ""
    if not out["id"]:
        return None
    for field in ("seq", "seconds", "cores", "dropped"):
        value = report.get(field)
        out[field] = value if isinstance(value, int) and 0 <= value < 10**9 else None
    out["closing"] = report.get("closing") is True
    net = report.get("net")
    if isinstance(net, dict):
        out["net"] = {
            k: net[k] for k in ("type", "rtt", "downlink")
            if isinstance(net.get(k), (str, int, float)) and not isinstance(net.get(k), bool)
        }
    lines = []
    for line in report["lines"][:MAX_LINES]:
        if (
            isinstance(line, list) and len(line) == 3
            and isinstance(line[0], int) and isinstance(line[1], str) and isinstance(line[2], str)
        ):
            lines.append([line[0], line[1][:8], line[2][:MAX_LINE]])
    out["lines"] = lines
    return out


class Store:
    def __init__(self, folder, keep_days, max_bytes):
        self.folder = Path(folder)
        self.folder.mkdir(parents=True, exist_ok=True)
        self.keep_days = keep_days
        self.max_bytes = max_bytes
        self.lock = threading.Lock()
        self.day = None
        self.size = self.used()

    def used(self):
        return sum(p.stat().st_size for p in self.folder.glob("*.jsonl"))

    def prune(self):
        cutoff = time.time() - self.keep_days * 86400
        for p in self.folder.glob("*.jsonl"):
            if p.stat().st_mtime < cutoff:
                p.unlink(missing_ok=True)
        self.size = self.used()

    def add(self, record):
        line = (json.dumps(record, separators=(",", ":"), ensure_ascii=False) + "\n").encode()
        now = datetime.now(timezone.utc)
        with self.lock:
            day = now.strftime("%Y-%m-%d")
            if day != self.day:
                self.day = day
                self.prune()
            if self.size + len(line) > self.max_bytes:
                return False
            with open(self.folder / f"{day}.jsonl", "ab") as f:
                f.write(line)
            self.size += len(line)
            return True


class Limiter:
    def __init__(self):
        self.lock = threading.Lock()
        self.buckets = {}

    def allow(self, who):
        now = time.monotonic()
        with self.lock:
            if len(self.buckets) > 10000:
                self.buckets.clear()
            tokens, at = self.buckets.get(who, (BURST, now))
            tokens = min(BURST, tokens + (now - at) * RATE)
            if tokens < 1:
                self.buckets[who] = (tokens, now)
                return False
            self.buckets[who] = (tokens - 1, now)
            return True


def handler(store, limiter):
    class Handler(BaseHTTPRequestHandler):
        server_version = "clientlog"
        sys_version = ""

        def do_POST(self):
            if self.path.split("?", 1)[0] != "/clientlog":
                return self.finish_with(404)
            try:
                length = int(self.headers.get("Content-Length", ""))
            except ValueError:
                return self.finish_with(411)
            if not 0 < length <= MAX_BODY:
                return self.finish_with(413)
            body = self.rfile.read(length)
            # Caddy puts the sender in X-Forwarded-For; it is used for the rate limit only.
            who = (self.headers.get("X-Forwarded-For") or self.client_address[0]).split(",")[0].strip()
            if not limiter.allow(who):
                return self.finish_with(429)
            try:
                report = clean(json.loads(body))
            except (ValueError, UnicodeDecodeError):
                report = None
            if report is None:
                return self.finish_with(400)
            report["received"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
            self.finish_with(204 if store.add(report) else 507)

        def finish_with(self, status):
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, *args):
            pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--dir", default=os.environ.get("STATE_DIRECTORY", "/var/lib/dare-clientlog"))
    parser.add_argument("--keep-days", type=int, default=14)
    parser.add_argument("--max-mb", type=int, default=500)
    args = parser.parse_args()
    store = Store(args.dir, args.keep_days, args.max_mb * 1024 * 1024)
    server = ThreadingHTTPServer((args.bind, args.port), handler(store, Limiter()))
    server.serve_forever()


if __name__ == "__main__":
    main()
