"""Foundation ship service: the ship generator behind a small loopback HTTP API.

Only the zone's web server talks to it (it listens on 127.0.0.1); the zone checks the
pilot is signed in and rate-limits them before a request gets here. The PixelLab key is
read from this process's environment (PIXEL_LAB_API_KEY), so it never leaves the server.

    POST /generate  {"mode": "random"}                      a ship in the stock style, from a pool
                    {"mode": "prompt", "prompt": "..."}       a batch of options in the stock style
                    {"mode": "prompt", "batch": "<token>"}    the next option from that batch
                    {"mode": "prompt", "prompt": "...", "creative": true}   any subject (Maigent's path)
      -> 200 {"ok": true, "sheet": <base64 ship-rotations.png>, "art": <base64>|null, "seed": n|null,
              "batch": "<token>", "left": n}   (batch and left for prompts)
      -> 4xx/5xx {"ok": false, "message": "..."}
    POST /flip      {"sheet": <base64 ship-rotations.png>}
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
import random
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import graphics
import shipgen
import stylegen

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


# Ships in the stock style come in batches of up to 36 (stylegen). Random hands out one at a time
# from a shared pool and refills it in the background; a prompt's batch is kept under a token so
# Try again is instant and free.
POOL_LOW = 24        # refill below this; a batch shares one theme, so the pool mixes several
KEEP_PER_BATCH = 12  # of the ~36 a batch draws
BATCH_TTL = 30 * 60
MAX_BATCHES = 200
_pool: list = []
_pool_lock = threading.Lock()
_refill_lock = threading.Lock()
_style_slots = threading.BoundedSemaphore(2)  # PixelLab style jobs at once
_batches: dict[str, tuple[float, list]] = {}
_batches_lock = threading.Lock()


def _has_key() -> bool:
    return bool(os.environ.get("PIXEL_LAB_API_KEY", "").strip())


def _sheet_reply(sprites: list) -> dict:
    """The first sprite that turns cleanly, as a rotation sheet (Maigent's rotation and export)."""
    while sprites:
        sprite = sprites.pop(0)
        try:
            frames = shipgen._rotation_frames(sprite, preserve_detail=True)
        except ValueError:
            continue
        files = shipgen._render_ship_files(frames)
        return {"ok": True, "sheet": _b64(files["ship-rotations.png"]), "art": _b64(shipgen._encode(sprite, "PNG")), "seed": None}
    raise Refused(502, "PixelLab drew nothing usable. Try again.")


def _draw(description: str) -> list:
    if not _style_slots.acquire(timeout=1):
        raise Refused(429, "The ship painter is busy. Try again in a few seconds.")
    try:
        return stylegen.draw_batch(description, seed=random.SystemRandom().randrange(2**31))
    except (ValueError, RuntimeError) as exc:
        raise Refused(502, str(exc)) from exc
    finally:
        _style_slots.release()


def _refill() -> None:
    """Draws a fresh random batch into the pool (one at a time)."""
    if not _refill_lock.acquire(blocking=False):
        return
    try:
        while True:
            with _pool_lock:
                if len(_pool) >= POOL_LOW:
                    break
            brief = stylegen.random_brief(random.Random())
            sprites = _draw(brief)[:KEEP_PER_BATCH]
            with _pool_lock:
                _pool.extend(sprites)
            log.info("random pool +%d (%s)", len(sprites), brief)
    except Refused as exc:
        log.warning("random pool refill failed: %s", exc)
    finally:
        _refill_lock.release()


def _refill_soon() -> None:
    with _pool_lock:
        low = len(_pool) < POOL_LOW
    if low and _has_key() and not _refill_lock.locked():
        threading.Thread(target=_refill, daemon=True).start()


def _random_ship() -> dict:
    for _ in range(2):
        with _pool_lock:
            # Any of them, so consecutive ships come from different batches (themes).
            random.shuffle(_pool)
            sprites = _pool[:4]
            del _pool[:4]
        if sprites:
            try:
                reply = _sheet_reply(sprites)
            except Refused:
                continue
            with _pool_lock:
                _pool[:0] = sprites  # the unused ones go back
            _refill_soon()
            return reply
        # Empty: draw now (another request may already be drawing; wait for it).
        with _refill_lock:
            with _pool_lock:
                empty = not _pool
            if empty:
                brief = stylegen.random_brief(random.Random())
                sprites = _draw(brief)[:KEEP_PER_BATCH]
                with _pool_lock:
                    _pool.extend(sprites)
                log.info("random pool +%d (%s)", len(sprites), brief)
    raise Refused(502, "Ship generation failed. Try again.")


def _batch_take(token: str) -> dict:
    with _batches_lock:
        now = time.monotonic()
        for key in [k for k, (t, _) in _batches.items() if t < now]:
            del _batches[key]
        entry = _batches.get(token)
        if entry is None:
            raise Refused(410, "Those options have expired. Draw it again.")
        sprites = entry[1]
    reply = _sheet_reply(sprites)
    with _batches_lock:
        if not sprites:
            _batches.pop(token, None)
    reply.update(batch=token, left=len(sprites))
    return reply


def generate(body: dict) -> dict:
    """One ship, as the files the browser needs. Raises Refused with a message for the pilot."""
    global _next_paid
    mode = body.get("mode")
    if mode == "random":
        seed = body.get("seed")
        if seed is not None and not (isinstance(seed, int) and 0 <= seed < 2**31):
            raise Refused(400, "Bad seed.")
        if not _has_key() or seed is not None:
            # Without PixelLab (or asked for a reproducible one): Maigent's own procedural ship.
            files, seed = shipgen.generate_ship("", seed=seed)
            return {"ok": True, "sheet": _b64(files["ship-rotations.png"]), "art": None, "seed": seed}
        return _random_ship()
    if mode != "prompt":
        raise Refused(400, "Choose Random or Prompt.")

    batch = body.get("batch")
    if isinstance(batch, str) and batch:
        return _batch_take(batch)

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
        # A spacecraft: drawn in the stock ships' style, a batch of options at once.
        sprites = _draw(prompt + " Seen from directly above, nose pointing up.")
        token = secrets.token_urlsafe(18)
        with _batches_lock:
            if len(_batches) >= MAX_BATCHES:
                _batches.pop(next(iter(_batches)))
            _batches[token] = (time.monotonic() + BATCH_TTL, sprites)
        return _batch_take(token)

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


def flip(body: dict) -> dict:
    sheet = body.get("sheet")
    try:
        raw = base64.b64decode(sheet, validate=True) if isinstance(sheet, str) else b""
    except binascii.Error:
        raw = b""
    if not raw:
        raise Refused(400, "Send the sheet to flip.")
    try:
        files = shipgen.flip_ship_heading({"ship-rotations.png": raw})
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
        routes = {"/generate": generate, "/flip": flip, "/graphic": graphic}
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
    _refill_soon()  # have random ships ready before the first pilot asks
    log.info("listening on %s:%d (prompted ships %s)", args.host, args.port,
             "on" if os.environ.get("PIXEL_LAB_API_KEY", "").strip() else "off: no PIXEL_LAB_API_KEY")
    server.serve_forever()


if __name__ == "__main__":
    main()
