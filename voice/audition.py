"""Audition page: hear every take with the monster treatment and pick one per phrase.

    .venv/bin/python audition.py      then open http://localhost:28417

Save writes picks.json ({"gw": 2, ...}); existing picks are pre-selected.
"""
import html
import json
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

from voicetool import HERE, MONSTER, PICKS, TAKES, ffmpeg, load_phrases

PORT = 28417


def treated(code: str) -> list[str]:
    """Monster-treated copies of a phrase's takes (cached), by take number."""
    names = []
    for raw in sorted(TAKES.glob(f"{code}_*.wav")):
        if raw.stem.endswith("_m"):
            continue
        m = raw.with_name(raw.stem + "_m.wav")
        if not m.exists() or m.stat().st_mtime < raw.stat().st_mtime:
            ffmpeg(raw, m, MONSTER, "-ar", "22050")
        names.append(m.name)
    return names


def page() -> str:
    table = load_phrases()
    picks = json.loads(PICKS.read_text()) if PICKS.exists() else {}
    menus = {m["code"]: m["label"] for m in table["menus"]}
    rows = []
    for p in table["phrases"]:
        code, takes = p["code"], treated(p["code"])
        where = " / ".join(menus[p["code"][:i]] for i in range(1, len(p["code"])))
        cells = "".join(
            f'<label><input type="radio" name="{code}" value="{n}"{" checked" if picks.get(code) == n else ""}>'
            f'<audio controls preload="none" src="/takes/{name}"></audio></label>'
            for n, name in enumerate(takes)) or "<em>no takes — run generate.py --only " + code + "</em>"
        rows.append(f'<section><h2>V {html.escape(code.upper())} <small>{html.escape(where)} · {p["scope"]}</small>'
                    f' — {html.escape(p["text"])}</h2><div class="takes">{cells}</div></section>')
    return f"""<!doctype html><meta charset="utf-8"><title>Voice picks</title>
<style>body{{font:14px system-ui;background:#111;color:#eee;margin:20px;padding-bottom:70px}}
h2{{font-size:16px;margin:16px 0 4px}}small{{color:#999;font-weight:400}}.takes{{display:flex;flex-wrap:wrap;gap:10px}}
label{{display:flex;align-items:center;gap:4px}}audio{{height:28px;width:190px}}
#bar{{position:fixed;left:0;right:0;bottom:0;background:#222;padding:12px 20px;display:flex;gap:16px;align-items:center}}
button{{font:inherit;padding:6px 18px}}</style>
<h1>Voice picks — one take per phrase</h1>{''.join(rows)}
<div id="bar"><button id="save">Save picks</button><span id="msg"></span></div>
<script>
document.getElementById('save').onclick = async () => {{
  const picks = {{}};
  document.querySelectorAll('input[type=radio]:checked').forEach(r => picks[r.name] = +r.value);
  const res = await fetch('/picks', {{method: 'POST', body: JSON.stringify(picks)}});
  document.getElementById('msg').textContent = res.ok ? 'Saved ' + Object.keys(picks).length + ' picks.' : 'Save failed';
}};
</script>"""


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=str(HERE), **kw)

    def do_GET(self):
        if self.path in ("/", "/index.html"):
            body = page().encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path.startswith("/takes/"):
            super().do_GET()
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path != "/picks":
            return self.send_error(404)
        picks = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        codes = {p["code"] for p in load_phrases()["phrases"]}
        clean = {c: int(n) for c, n in picks.items() if c in codes}
        PICKS.write_text(json.dumps(dict(sorted(clean.items())), indent=1) + "\n")
        self.send_response(204)
        self.end_headers()


if __name__ == "__main__":
    print(f"http://localhost:{PORT}")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
