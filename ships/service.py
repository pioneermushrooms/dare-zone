"""Foundation ship service: the ship generator behind a small loopback HTTP API.

Only the zone's web server talks to it (it listens on 127.0.0.1); the zone checks the
pilot is signed in and rate-limits them before a request gets here. The PixelLab key is
read from this process's environment (PIXEL_LAB_API_KEY), so it never leaves the server.

    POST /generate  {"mode": "random"}                      a stock-style ship from the pool
                    {"mode": "prompt", "prompt": "..."}       a stock-style ship for the prompt
                    {"mode": "prompt", "prompt": "...", "creative": true}   any subject (Maigent's path)
                    {..., "batch": "<token>"}                 Try again: a ship unlike those shown
      -> 200 {"ok": true, "sheet": <base64 ship-rotations.png>, "art": <base64>|null, "seed": n|null,
              "batch": "<token>", "ready": <whether the next Try again is instant>}
      -> 4xx/5xx {"ok": false, "message": "..."}
    POST /front     {"sheet": <base64 ship-rotations.png>, "frame": 0-39}   that frame becomes the front
      -> 200 {"ok": true, "sheet": <base64>}
    GET  /health    -> 200 {"ok": true, "prompt": <whether a PixelLab key is set>}

Run: python3 service.py [--port 8089]
"""

import argparse
import base64
import binascii
import json
import logging
import os
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import graphics
from foundation_ships import designer, shipgen

MAX_BODY = 512 * 1024
MAX_PROMPT = 500
# PixelLab is paid: one prompted ship at a time across the zone, and a short gap between them
# (the same pacing the Discord bot uses).
PAID_GAP_SECONDS = 15.0
DEFAULT_PROMPT = (
    "A modern sci-fi interceptor with broad swept wings, a prominent cockpit, "
    "sculpted armor, and recessed twin engines."
)

log = logging.getLogger("dare-ships")
_paid = threading.Lock()
_next_paid = 0.0


class Refused(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def _b64(data: bytes | None) -> str | None:
    return base64.b64encode(data).decode("ascii") if data else None


# Stock-style ships come from foundation_ships.designer, the same code the Discord bot uses.
# Random hands ships out of a shared pool; each pilot's run of tries (a prompt, or Random) is
# kept under a token so Try again shows a ship unlike the ones they've seen.
TRIES_TTL = 30 * 60
MAX_TRIES = 300
_pool = designer.RandomPool()
_tries: dict[str, tuple[float, designer.Tries]] = {}
_tries_lock = threading.Lock()


def _has_key() -> bool:
    return bool(os.environ.get("PIXEL_LAB_API_KEY", "").strip())


def _keep(tries: designer.Tries) -> str:
    token = secrets.token_urlsafe(18)
    with _tries_lock:
        now = time.monotonic()
        for key in [k for k, (t, _) in _tries.items() if t < now]:
            del _tries[key]
        if len(_tries) >= MAX_TRIES:
            _tries.pop(next(iter(_tries)))
        _tries[token] = (now + TRIES_TTL, tries)
    return token


def _found(token) -> designer.Tries | None:
    if not isinstance(token, str) or not token:
        return None
    with _tries_lock:
        entry = _tries.get(token)
        if entry is None:
            return None
        _tries[token] = (time.monotonic() + TRIES_TTL, entry[1])
        return entry[1]


def _next(tries: designer.Tries, token: str) -> dict:
    try:
        files = tries.next()
    except designer.Busy as exc:
        raise Refused(429, str(exc)) from exc
    except (ValueError, RuntimeError) as exc:
        raise Refused(502, str(exc)) from exc
    return {
        "ok": True,
        "sheet": _b64(files["ship-rotations.png"]),
        "art": _b64(files.get("ship-art-128.png")),
        "seed": None,
        "batch": token,
        "ready": tries.ready,  # whether Try again is instant
    }


def generate(body: dict) -> dict:
    """One ship, as the files the browser needs. Raises Refused with a message for the pilot.

    "batch" (from an earlier answer) continues that run of tries: a ship unlike the ones shown.
    """
    global _next_paid
    mode = body.get("mode")
    if mode not in ("random", "prompt"):
        raise Refused(400, "Choose Random or Prompt.")
    batch = body.get("batch")
    tries = _found(batch)
    if tries is not None:
        return _next(tries, batch)
    if isinstance(batch, str) and batch and mode == "prompt" and not body.get("prompt"):
        raise Refused(410, "Those options have expired. Draw it again.")

    if mode == "random":
        seed = body.get("seed")
        if seed is not None and not (isinstance(seed, int) and 0 <= seed < 2**31):
            raise Refused(400, "Bad seed.")
        if not _has_key() or seed is not None:
            # Without PixelLab (or asked for a reproducible one): Maigent's procedural ship.
            files, seed = shipgen.generate_ship("", seed=seed)
            return {"ok": True, "sheet": _b64(files["ship-rotations.png"]), "art": None, "seed": seed}
        tries = designer.Tries(pool=_pool)
        return _next(tries, _keep(tries))

    prompt = body.get("prompt")
    prompt = prompt.strip() if isinstance(prompt, str) else ""
    creative = body.get("creative") is True
    if creative and not prompt:
        raise Refused(400, "Describe the subject to draw.")
    prompt = prompt or DEFAULT_PROMPT
    if len(prompt) > MAX_PROMPT:
        raise Refused(400, f"Keep the description under {MAX_PROMPT + 1} characters.")
    if any(ord(ch) < 32 and ch not in "\t\n" for ch in prompt):
        raise Refused(400, "Use plain text in the description.")
    if not _has_key():
        raise Refused(503, "Prompted ships are not set up on this server yet. Try Random.")

    if not creative:
        # A spacecraft: drawn in the stock ships' style, in batches.
        tries = designer.Tries(prompt=prompt)
        return _next(tries, _keep(tries))

    # Any subject: Maigent's PixelLab path (the stock ships are no style guide for a dragon).
    if not _paid.acquire(blocking=False):
        raise Refused(429, "Another ship is being drawn. Try again in a few seconds.")
    try:
        wait = _next_paid - time.monotonic()
        if wait > 0:
            raise Refused(429, f"The ship painter is cooling down. Try again in {int(wait) + 1} s.")
        _next_paid = time.monotonic() + PAID_GAP_SECONDS
        try:
            files, _ = shipgen.generate_ship(
                prompt, variation=body.get("variation") is True, creative=creative
            )
        except (ValueError, RuntimeError) as exc:
            # The generator's own messages are written for players ("PixelLab is out of credits.").
            raise Refused(502, str(exc)) from exc
    finally:
        _paid.release()
    return {
        "ok": True,
        "sheet": _b64(files["ship-rotations.png"]),
        "art": _b64(files.get("ship-art-128.png")),
        "seed": None,
    }


def graphic(body: dict) -> dict:
    """A new sheet for one of the game's other graphics, from the arena's current one (the template)."""
    global _next_paid
    kind, mode = body.get("kind"), body.get("mode")
    if not isinstance(kind, str) or not kind.isalnum() or len(kind) > 16:
        raise Refused(400, "Unknown graphic.")
    try:
        template = base64.b64decode(body.get("template") or "", validate=True)
    except binascii.Error:
        template = b""
    if not template or len(template) > 2 * 1024 * 1024:
        raise Refused(400, "Send the current picture.")
    seed = body.get("seed")
    seed = seed if isinstance(seed, int) and 0 <= seed < 2**31 else None
    try:
        if mode != "prompt":
            return {"ok": True, "sheet": _b64(graphics.generate(kind, mode, template, seed=seed))}
        prompt = body.get("prompt")
        prompt = prompt.strip() if isinstance(prompt, str) else ""
        if not prompt:
            raise Refused(400, "Describe what you want.")
        if len(prompt) > MAX_PROMPT or any(ord(ch) < 32 and ch not in "\t\n" for ch in prompt):
            raise Refused(400, f"Use plain text, under {MAX_PROMPT + 1} characters.")
        if not os.environ.get("PIXEL_LAB_API_KEY", "").strip():
            raise Refused(503, "Drawing from a description is not set up on this server yet. Try New look.")
        if not _paid.acquire(blocking=False):
            raise Refused(429, "Something else is being drawn. Try again in a few seconds.")
        try:
            wait = _next_paid - time.monotonic()
            if wait > 0:
                raise Refused(429, f"The painter is cooling down. Try again in {int(wait) + 1} s.")
            _next_paid = time.monotonic() + PAID_GAP_SECONDS
            return {"ok": True, "sheet": _b64(graphics.generate(kind, mode, template, prompt=prompt, seed=seed))}
        finally:
            _paid.release()
    except graphics.GraphicError as exc:
        raise Refused(400, str(exc)) from exc
    except RuntimeError as exc:
        raise Refused(502, str(exc)) from exc


def front(body: dict) -> dict:
    """The sheet with another frame as its front (frame 0), clockwise order kept."""
    sheet, frame = body.get("sheet"), body.get("frame")
    try:
        raw = base64.b64decode(sheet, validate=True) if isinstance(sheet, str) else b""
    except binascii.Error:
        raw = b""
    if not raw or not isinstance(frame, int):
        raise Refused(400, "Send the sheet and the frame (0-39) that faces forward.")
    try:
        files = shipgen.rebase_ship_heading({"ship-rotations.png": raw}, frame)
    except ValueError as exc:
        raise Refused(400, str(exc)) from exc
    except Exception as exc:  # unreadable PNG
        raise Refused(400, "That isn't a ship rotation sheet.") from exc
    return {"ok": True, "sheet": _b64(files["ship-rotations.png"])}


class Handler(BaseHTTPRequestHandler):
    server_version = "dare-ships"

    def _reply(self, status: int, payload: dict) -> None:
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._reply(200, {"ok": True, "prompt": bool(os.environ.get("PIXEL_LAB_API_KEY", "").strip())})
        else:
            self._reply(404, {"ok": False, "message": "Not found."})

    def do_POST(self) -> None:  # noqa: N802
        routes = {"/generate": generate, "/front": front, "/graphic": graphic}
        handler = routes.get(self.path)
        if handler is None:
            self._reply(404, {"ok": False, "message": "Not found."})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = -1
        if not 0 <= length <= MAX_BODY:
            self._reply(413, {"ok": False, "message": "Request too large."})
            return
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
            if not isinstance(body, dict):
                raise ValueError
        except ValueError:
            self._reply(400, {"ok": False, "message": "Bad request."})
            return
        started = time.monotonic()
        try:
            payload = handler(body)
            status = 200
        except Refused as exc:
            payload, status = {"ok": False, "message": str(exc)}, exc.status
        except Exception:
            log.exception("ship %s failed", self.path)
            payload, status = {"ok": False, "message": "Ship generation failed. Try again."}, 500
        log.info("%s %s -> %d in %.1fs", self.path, body.get("mode", ""), status, time.monotonic() - started)
        self._reply(status, payload)

    def log_message(self, *args) -> None:  # requests are logged above, without bodies
        pass


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8089)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    if _has_key():
        _pool.refill_soon()  # have random ships ready before the first pilot asks
    log.info("listening on %s:%d (prompted ships %s)", args.host, args.port,
             "on" if os.environ.get("PIXEL_LAB_API_KEY", "").strip() else "off: no PIXEL_LAB_API_KEY")
    server.serve_forever()


if __name__ == "__main__":
    main()
