#!/usr/bin/env python3
"""Fetch the SubSpace Banner Emporium's banners for The Foundry, politely and resumably.

Run it on the zone server, from the zone's directory (/opt/subspace/zone), as the zone's user:

    python3 -I /root/dare-arenas-deploy-<ts>/deploy/import-banner-emporium.py

It writes data/banner-emporium.sqlite (its own file: never the zone's database). The zone brings
new rows from there into The Foundry by itself (at load, then every 10 minutes while the file keeps
changing; see ArenaOwnership.Banners.cs), so banners appear while this is still running.

Manners (Chris agreed to the import on these terms, 2026-11-27):
  * One request at a time, at least --delay seconds apart (2 s at the least, 3 by default).
  * Only pages robots.txt allows (it disallows /SSBE/k/?action*; this reads ?action=banner&id= and
    ./banners/<id>.bmp). Checked at the start of every run.
  * Backs off when the site struggles (HTTP errors, or its database refusing connections): a minute,
    then doubling up to 15 minutes, and stops after 6 failures in a row (run it again later).
  * Each banner is two requests (its page for the contributor, date and use count, then the BMP),
    so all ~154,000 take about a week at 2 s. Stop it any time (Ctrl-C, or kill): it carries on from
    where it was. Ids already done (fetched, missing or not a banner) are never asked for again.

Each banner is credited to its contributor's name as plain text, linked back to the Emporium.
Needs only the Python standard library.
"""
import argparse
import datetime as dt
import html
import re
import signal
import sqlite3
import sys
import time
import urllib.error
import urllib.request
import urllib.robotparser

BASE = "https://www.trenchwars.org/SSBE/"
AGENT = "banner-import/1.0 (polite: one request at a time, 2+ s apart)"
SCHEMA = """
CREATE TABLE IF NOT EXISTS banners (
  seq INTEGER PRIMARY KEY AUTOINCREMENT, -- order rows were (re)written: what the zone brings in next
  id INTEGER NOT NULL UNIQUE,            -- the Emporium's banner id
  status TEXT NOT NULL,                  -- ok | missing | bad | error
  author TEXT, added TEXT, players INTEGER, bmp BLOB,
  fetched TEXT NOT NULL
);
"""
DB_TROUBLE = ("max_user_connections", "Too many connections", "mysql_connect", "Can't connect to")

stop = False


def on_signal(*_):
    global stop
    stop = True
    print("stopping after this request…", flush=True)


class Polite:
    """Every request goes through here: one at a time, at least `delay` seconds apart."""

    def __init__(self, delay):
        self.delay = delay
        self.last = 0.0

    def get(self, url):
        wait = self.last + self.delay - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        self.last = time.monotonic()
        req = urllib.request.Request(url, headers={"User-Agent": AGENT})
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.status, r.read(64 * 1024)
        except urllib.error.HTTPError as e:
            return e.code, b""
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
            return 0, b""
        finally:
            self.last = time.monotonic()


def field(page, label):
    m = re.search(r'<div class="listleft">\s*' + re.escape(label) + r'\s*</div>\s*<div class="listright">(.*?)</div>', page, re.S)
    return html.unescape(re.sub(r"<[^>]+>", "", m.group(1))).strip() if m else None


def iso(added):
    """'07/10/2005 01:06PM' -> '2005-07-10T13:06' (kept as it was when it doesn't parse)."""
    try:
        return dt.datetime.strptime(added, "%m/%d/%Y %I:%M%p").strftime("%Y-%m-%dT%H:%M")
    except (TypeError, ValueError):
        return added or ""


def is_banner(b):
    return (len(b) >= 54 and b[:2] == b"BM" and int.from_bytes(b[18:22], "little", signed=True) == 12
            and abs(int.from_bytes(b[22:26], "little", signed=True)) == 8)


def newest_id(net, base):
    status, body = net.get(base + "?action=recent50")
    ids = [int(i) for i in re.findall(rb"action=banner&(?:amp;)?id=(\d+)", body)] if status == 200 else []
    if not ids:
        sys.exit(f"Couldn't read the newest banner id (HTTP {status}). Try again later.")
    return max(ids)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="data/banner-emporium.sqlite", help="staging file (default: %(default)s)")
    ap.add_argument("--delay", type=float, default=3.0, help="seconds between requests, at least 2 (default: %(default)s)")
    ap.add_argument("--from-id", type=int, default=1)
    ap.add_argument("--to-id", type=int, default=0, help="last id (default: the newest)")
    ap.add_argument("--max", type=int, default=0, help="stop after this many banners this run (0: no limit)")
    ap.add_argument("--base", default=BASE, help=argparse.SUPPRESS)
    a = ap.parse_args()
    if a.delay < 2.0:
        sys.exit("--delay must be at least 2 seconds.")
    signal.signal(signal.SIGINT, on_signal)
    signal.signal(signal.SIGTERM, on_signal)

    net = Polite(a.delay)
    robots = urllib.robotparser.RobotFileParser()
    status, body = net.get(a.base.split("/SSBE/")[0] + "/robots.txt")
    robots.parse(body.decode("latin-1").splitlines() if status == 200 else [])
    for probe in (a.base + "?action=banner&id=1", a.base + "banners/1.bmp", a.base + "?action=recent50"):
        if not robots.can_fetch(AGENT, probe):
            sys.exit(f"robots.txt no longer allows {probe}. Stopping; ask before going on.")

    db = sqlite3.connect(a.out, timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.executescript(SCHEMA)
    done = {r[0] for r in db.execute("SELECT id FROM banners WHERE status IN ('ok', 'missing', 'bad')")}
    last = a.to_id or newest_id(net, a.base)
    todo = [i for i in range(a.from_id, last + 1) if i not in done]
    print(f"{len(done)} done before; {len(todo)} to go (ids {a.from_id}..{last}), {a.delay:g} s apart.", flush=True)

    def save(i, status, author=None, added=None, players=None, bmp=None):
        db.execute("INSERT OR REPLACE INTO banners(id, status, author, added, players, bmp, fetched) VALUES(?, ?, ?, ?, ?, ?, ?)",
                   (i, status, author, added, players, bmp, dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")))
        db.commit()

    failures, fetched, started = 0, 0, time.monotonic()
    for i in todo:
        if stop or (a.max and fetched >= a.max):
            break
        status, body = net.get(f"{a.base}?action=banner&id={i}")
        page = body.decode("latin-1", "replace")
        # only the "Selected banner" part is about this id (the page also lists random and new ones)
        start = page.find("Selected banner")
        end = page.find("Random 10", start + 1) if start >= 0 else -1
        selected = page[start:end] if start >= 0 and end > start else page[start:] if start >= 0 else ""
        here = f"banners/{i}.bmp" in selected
        trouble = status != 200 or (not here and any(t in page for t in DB_TROUBLE))
        bmp = None
        if not trouble and here:
            status, bmp = net.get(f"{a.base}banners/{i}.bmp")
            trouble = status not in (200, 404)
        if trouble:
            failures += 1
            save(i, "error")
            if failures >= 6:
                print(f"id {i}: the site is struggling ({failures} failures in a row). Stopping; run again later.", flush=True)
                break
            pause = min(60 * 2 ** (failures - 1), 900)
            print(f"id {i}: HTTP {status} or a database error; waiting {pause} s.", flush=True)
            for _ in range(pause):
                if stop:
                    break
                time.sleep(1)
            continue
        failures = 0
        fetched += 1
        if bmp is None or status == 404:
            save(i, "missing")
        elif not is_banner(bmp):
            save(i, "bad")
        else:
            players = field(selected, "# of players")
            save(i, "ok", field(selected, "Contributor") or "", iso(field(selected, "Date added")),
                 int(players) if players and players.isdigit() else 0, bmp)
        if fetched % 100 == 0:
            rate = fetched / max(1.0, time.monotonic() - started)
            left = (len(todo) - fetched) / rate / 3600 if rate else 0
            print(f"id {i}: {fetched} this run, about {left:.0f} h to go.", flush=True)
    ok = db.execute("SELECT COUNT(*) FROM banners WHERE status = 'ok'").fetchone()[0]
    print(f"{fetched} fetched this run; {ok} banners staged in {a.out}.", flush=True)


if __name__ == "__main__":
    main()
