"""Reads the browser game's log reports on the server (run by deploy/client-logs.sh over ssh).

  show.py [--days N]            one line per visit, newest last
  show.py [--days N] WHO        every line from the visits whose pilot, flying-as name or id
                                contains WHO (case-insensitive)
"""

import json
import os
import re
import statistics
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

FOLDER = Path(os.environ.get("CLIENTLOG_DIR", "/var/lib/dare-clientlog"))
PERF = re.compile(
    r"perf: (\d+) updates/s, (\d+) draws/s, longest gap (\d+) ms between updates, (\d+) ms "
    r"between draws \| ping (\d+|unknown)(?: ms)? \| packets (\d+)/s in, (\d+)/s out, (\d+) resent"
)


def browser(ua):
    for name, pattern in (("Edge", r"Edg/(\d+)"), ("Opera", r"OPR/(\d+)"), ("Firefox", r"Firefox/(\d+)"),
                          ("Chrome", r"Chrome/(\d+)"), ("Safari", r"Version/(\d+).*Safari")):
        m = re.search(pattern, ua)
        if m:
            return f"{name} {m.group(1)}"
    return "?"


def load(days):
    since = datetime.now(timezone.utc).date() - timedelta(days=days - 1)
    visits = {}
    for path in sorted(FOLDER.glob("*.jsonl")):
        try:
            if datetime.strptime(path.stem, "%Y-%m-%d").date() < since:
                continue
        except ValueError:
            continue
        for raw in path.open(encoding="utf-8"):
            try:
                r = json.loads(raw)
            except ValueError:
                continue
            v = visits.setdefault(r["id"], {"reports": [], "flying": ""})
            v["reports"].append(r)
            for _, _, text in r["lines"]:
                if text.startswith("flying as "):
                    v["flying"] = text[len("flying as "):]
    for v in visits.values():
        v["reports"].sort(key=lambda r: (r.get("seq") is None, r.get("seq") or 0))
    return sorted(visits.items(), key=lambda kv: kv[1]["reports"][0]["received"])


def median(values):
    return f"{statistics.median(values):.0f}" if values else "-"


def summary(visits):
    print(f"{'first report (UTC)':19}  {'pilot':16} {'platform':9} {'browser':10} {'screen':12} "
          f"{'min':>4} {'upd/s':>5} {'draw/s':>6} {'ping':>5} {'upd gap':>7} {'draw gap':>8} {'resent':>6} {'err':>3}  id")
    for vid, v in visits:
        first = v["reports"][0]
        perf = [m.groups() for r in v["reports"] for _, _, t in r["lines"] if (m := PERF.match(t))]
        errors = sum(1 for r in v["reports"] for _, level, _ in r["lines"] if level == "error")
        pings = [int(p[4]) for p in perf if p[4] != "unknown"]
        name = v["flying"] or first["pilot"]
        print(
            f"{first['received'][:19].replace('T', ' '):19}  {name[:16]:16} {first['platform'][:9]:9} "
            f"{browser(first['ua'])[:10]:10} {first['screen'][:12]:12} "
            f"{(max(r.get('seconds') or 0 for r in v['reports'])) // 60:>4} "
            f"{median([int(p[0]) for p in perf]):>5} {median([int(p[1]) for p in perf]):>6} "
            f"{median(pings):>5} {max((int(p[2]) for p in perf), default=0):>7} "
            f"{max((int(p[3]) for p in perf), default=0):>8} {sum(int(p[7]) for p in perf):>6} {errors:>3}  {vid[:8]}"
        )
    print("\nmin: minutes in the game; upd/s, draw/s, ping: medians of the 10 s figures; gaps: the longest (ms);"
          "\nresent: reliable packets sent again (loss); err: error lines. Details: client-logs.sh <name or id>")


def detail(visits, who):
    who = who.lower()
    shown = 0
    for vid, v in visits:
        first = v["reports"][0]
        if who not in vid.lower() and who not in first["pilot"].lower() and who not in v["flying"].lower():
            continue
        shown += 1
        print(f"== {vid}  pilot {first['pilot']!r} flying as {v['flying'] or '?'!r}  arena {first['arena'] or 'public'!r}")
        print(f"   {first['ua']}")
        print(f"   platform {first['platform']}, screen {first['screen']}, {first.get('cores')} cores, "
              f"network {first.get('net')}, page version {first['version']!r}")
        for r in v["reports"]:
            for ms, level, text in r["lines"]:
                mark = "!" if level == "error" else ("~" if level == "warn" else " ")
                print(f"{ms / 1000:9.1f}s {mark} {text}")
            if r.get("dropped"):
                print(f"           ({r['dropped']} lines not sent: over the per-report limit)")
            if r.get("closing"):
                print(f"           (page closed at {r['received']})")
        print()
    if not shown:
        print(f"no visit matches {who!r}")


def main(argv):
    days = 2
    if len(argv) >= 2 and argv[0] == "--days":
        days = max(1, int(argv[1]))
        argv = argv[2:]
    visits = load(days)
    if argv:
        detail(visits, argv[0])
    else:
        summary(visits)


if __name__ == "__main__":
    main(sys.argv[1:])
