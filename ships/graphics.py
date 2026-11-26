"""New pictures for the game's other graphics: bullets, bombs, mines, items, explosions...

Every generator starts from the arena's current sheet (the template) and returns a PNG of
exactly the same size and frame layout, so the game reads it where the stock file was:

  recolor  every colour turned to a new hue (shapes, rows and animation kept)
  new      fresh procedural art that follows what each row and frame means to the game
  prompt   one sprite drawn by PixelLab from a description, then turned into every row the
           same way "new" does (EMP crackle, bounce halo, thor orb, coin-like tumble...)

What each row means comes from the game client (oss-client view.rs) and the stock sheets:
see SHEETS and BURSTS below. Each row keeps the template row's colour, so weapon levels and
types still read apart however the shapes change.

Black is see-through in the game, so empty pixels are transparent black and drawn pixels
never use pure black.
"""

import base64
import colorsys
import io
import math
import random
from dataclasses import dataclass

import requests
from PIL import Image, ImageDraw, ImageFilter, ImageOps

import shipgen

MAX_SIDE = 4096


# ---- what each sheet means -------------------------------------------------------------------

@dataclass(frozen=True)
class Row:
    """One row of a sprite sheet: every frame of one thing at one level.

    role   regular | emp | bounce | thor | burst (bullets' burst item, unarmed) | armed (burst
           going off) | ball | phased | trail | item | enemy | own
    level  weapon level 1..4, 0 when the row isn't a level
    anim   spin (turns in the picture's plane) | tilt (tumbles like a coin on an axis nearly
           upright, so it narrows and darkens mid-turn) | pulse (breathes) | flicker (EMP
           crackle, new sparks every frame) | fade (powerball trail: dissolves over time)
    """
    role: str
    level: int = 0
    anim: str = "spin"


def _levels(role: str, anim: str, n: int) -> tuple[Row, ...]:
    return tuple(Row(role, k + 1, anim) for k in range(n))


@dataclass(frozen=True)
class Sheet:
    cell: tuple[int, int]
    frames: int  # frames per row
    rows: tuple[Row, ...]
    body: str  # the procedural shape family ("" when New look has none: prompt and recolour only)
    continuous: bool = False  # one animation through every frame, left to right, top to bottom
    keep_colour: bool = False  # one thing in every row: a drawing keeps its own colours
    full: bool = False  # the picture fills its cell edge to edge (bricks)


# Row order as the client reads it (view.rs): bullets lvl*4 (+20 bouncing, 16 burst, last row
# armed burst); bombs lvl*10 (+40 EMP, +80 bouncing, 120 thor); mines lvl*10 (+40 EMP);
# shrapnel lvl*10 (+30 bouncing); powerball normal, phased, trail; flag enemy then yours.
SHEETS: dict[str, Sheet] = {
    "bullets": Sheet((5, 5), 4, _levels("regular", "pulse", 4) + (Row("burst", 0, "pulse"),)
                     + _levels("bounce", "pulse", 4) + (Row("armed", 0, "pulse"),), "bullet"),
    "bombs": Sheet((16, 16), 10, _levels("regular", "spin", 4) + _levels("emp", "flicker", 4)
                   + _levels("bounce", "spin", 4) + (Row("thor", 0, "spin"),), "bomb"),
    "mines": Sheet((16, 16), 10, _levels("regular", "tilt", 4) + _levels("emp", "flicker", 4), "mine"),
    "shrapnel": Sheet((8, 8), 10, _levels("regular", "spin", 3) + _levels("bounce", "spin", 3), "shrapnel"),
    "prizes": Sheet((16, 16), 10, (Row("item", 0, "tilt"),), "prize"),
    "powerb": Sheet((16, 16), 10, (Row("ball"), Row("phased"), Row("trail", 0, "fade")), "ball"),
    "flag": Sheet((16, 16), 10, (Row("enemy", 0, "tilt"), Row("own", 0, "tilt")), "flag"),
    "shield": Sheet((16, 16), 10, (Row("item", 0, "tilt"),), "shield"),
    "super": Sheet((16, 16), 10, (Row("item"),), "super"),
    "warppnt": Sheet((16, 16), 10, (Row("item"),), "warppnt"),
    # Map objects (view.rs draws over1..over5 as asteroids, a space station and a wormhole, each
    # one animation through all of its frames), the goal (yours and the enemy's halves) and
    # bricks (the enemy's half, then your team's).
    "over1": Sheet((16, 16), 15, (Row("item"),) * 2, "", continuous=True, keep_colour=True),
    "over2": Sheet((32, 32), 10, (Row("item"),) * 3, "", continuous=True, keep_colour=True),
    "over3": Sheet((16, 16), 15, (Row("item"),) * 2, "", continuous=True, keep_colour=True),
    "over4": Sheet((96, 96), 5, (Row("item"),) * 2, "", continuous=True, keep_colour=True),
    "over5": Sheet((80, 80), 4, (Row("item"),) * 6, "", continuous=True, keep_colour=True),
    "goal": Sheet((16, 16), 9, (Row("enemy", 0, "glow"), Row("own", 0, "glow")), ""),
    "wall": Sheet((16, 16), 10, (Row("enemy", 0, "glow"), Row("own", 0, "glow")), "", full=True),
}


@dataclass(frozen=True)
class Burst:
    """A timed animation played once: frames left to right, top to bottom.

    style   spark (bullet hit) | explosion | emp (electric ring) | warp (swirl) | grow (repel:
            a ring that only grows) | rocket (flash, glowing ball, smoke ring; grows) | puff
    per_row each row is its own animation (exhaust: row 1 thrusters, row 2 afterburners)
    """
    side: int
    style: str
    per_row: bool = False


BURST_SHEETS: dict[str, Burst] = {
    "explode0": Burst(16, "spark"),
    "explode1": Burst(48, "explosion"),
    "explode2": Burst(80, "explosion"),
    "empburst": Burst(80, "emp"),
    "warp": Burst(48, "warp"),
    "repel": Burst(96, "grow"),
    "rocket": Burst(24, "rocket"),
    "exhaust": Burst(16, "puff", per_row=True),
}

# kind -> (cell width, cell height, frames per row); kind -> square cell side
SPRITES = {k: (s.cell[0], s.cell[1], s.frames) for k, s in SHEETS.items()}
BURSTS = {k: b.side for k, b in BURST_SHEETS.items()}

TILT_DEGREES = 16  # the tumbling axis leans this far from upright


def row_spec(kind: str, row: int) -> Row:
    rows = SHEETS[kind].rows
    return rows[row] if row < len(rows) else Row("regular", 0, rows[-1].anim)


class GraphicError(ValueError):
    """A message for the arena owner."""


def load_template(data: bytes) -> Image.Image:
    try:
        with Image.open(io.BytesIO(data)) as im:
            im.load()
            img = im.convert("RGBA")
    except Exception as exc:
        raise GraphicError("The current picture can't be read.") from exc
    if not (0 < img.width <= MAX_SIDE and 0 < img.height <= MAX_SIDE):
        raise GraphicError("The current picture is too large.")
    px = img.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a == 0 or (r, g, b) == (0, 0, 0):
                px[x, y] = (0, 0, 0, 0)
    return img


def encode(img: Image.Image) -> bytes:
    px = img.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a < 40:
                px[x, y] = (0, 0, 0, 0)
            else:
                # premultiply against the black the game sees through, never pure black
                k = a / 255
                px[x, y] = (max(1, round(r * k)), max(1, round(g * k)), max(1, round(b * k)), 255)
    out = io.BytesIO()
    img.save(out, "PNG", optimize=True)
    return out.getvalue()


def _hue_of(img: Image.Image, box) -> float | None:
    """The saturation-weighted mean hue of a region, or None when it is grey or empty."""
    sx = sy = wsum = 0.0
    for r, g, b, a in img.crop(box).getdata():
        if not a:
            continue
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        w = s * v
        sx += math.cos(h * 2 * math.pi) * w
        sy += math.sin(h * 2 * math.pi) * w
        wsum += w
    if wsum < 0.5:
        return None
    return (math.atan2(sy, sx) / (2 * math.pi)) % 1.0


def _rgb(h: float, s: float, v: float, a: int = 255) -> tuple[int, int, int, int]:
    r, g, b = colorsys.hsv_to_rgb(h % 1.0, max(0, min(1, s)), max(0, min(1, v)))
    return (round(r * 255), round(g * 255), round(b * 255), a)


def recolor(template: Image.Image, rng: random.Random) -> Image.Image:
    """Every colour turned by the same amount, so rows stay as far apart as they were."""
    shift = rng.uniform(0.15, 0.85)
    sat = rng.uniform(0.85, 1.3)
    out = template.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if not a:
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            px[x, y] = _rgb(h + shift, s * sat, v, a)
    return out


# ---- image helpers ---------------------------------------------------------------------------

def _blank(S: int) -> Image.Image:
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def _scaled(img: Image.Image, k: float) -> Image.Image:
    """`img` shrunk (or grown) by k about its centre, on a canvas of the same size."""
    S = img.width
    n = max(1, round(S * k))
    out = _blank(S)
    small = img.resize((n, n), Image.LANCZOS)
    if n > S:
        small = small.crop(((n - S) // 2, (n - S) // 2, (n - S) // 2 + S, (n - S) // 2 + S))
        n = S
    out.alpha_composite(small, ((S - n) // 2, (S - n) // 2))
    return out


def _shade(img: Image.Image, k: float, alpha: float = 1.0) -> Image.Image:
    r, g, b, a = img.split()
    r, g, b = (ch.point(lambda v: min(255, round(v * k))) for ch in (r, g, b))
    if alpha != 1.0:
        a = a.point(lambda v: round(v * alpha))
    return Image.merge("RGBA", (r, g, b, a))


def _tint(img: Image.Image, hue: float, base_hue: float | None) -> Image.Image:
    """`img` turned to `hue`: shifted when it has a colour, coloured in when it is grey."""
    out = img.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if not a:
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if base_hue is None:
                px[x, y] = _rgb(hue, max(s, 0.55), v, a)
            else:
                px[x, y] = _rgb(h + hue - base_hue, s, v, a)
    return out


def _dissolve(img: Image.Image, amount: float, seed: int) -> Image.Image:
    """Drops `amount` (0..1) of the drawing in small blocks at random, as embers burning out."""
    r = random.Random(seed)
    block = max(2, img.width // 24)
    n = -(-img.width // block)
    mask = Image.new("L", (n, n), 0)
    mask.putdata([0 if r.random() < amount else 255 for _ in range(n * n)])
    mask = mask.resize((n * block, n * block), Image.NEAREST).crop((0, 0, img.width, img.height))
    out = img.copy()
    out.putalpha(Image.composite(img.getchannel("A"), Image.new("L", img.size, 0), mask))
    return out


def _bolt(d: ImageDraw.ImageDraw, c: float, r0: float, r1: float, a: float, rng: random.Random, fill, width: int):
    """A jagged lightning stroke from radius r0 to r1 along angle a."""
    pts = []
    for i in range(6):
        r = r0 + (r1 - r0) * i / 5
        aa = a + (rng.uniform(-0.45, 0.45) if 0 < i < 5 else 0)
        pts.append((c + math.cos(aa) * r, c + math.sin(aa) * r))
    d.line(pts, fill=fill, width=width, joint="curve")


# ---- procedural bodies: the plain look of each sheet's thing, unanimated ---------------------

def _body(family: str, style: str, S: int, hue: float) -> Image.Image:
    img = _blank(S)
    d = ImageDraw.Draw(img)
    c = S / 2

    def disc(r, fill):
        d.ellipse((c - r, c - r, c + r, c + r), fill=fill)

    def glow_orb(R):
        for i in range(8, 0, -1):
            disc(R * i / 8, _rgb(hue, 0.25 + 0.09 * i, 0.55 + 0.05 * (8 - i)))

    if family == "bullet":
        glow_orb(S * 0.3)
        w = max(2, S // 8)
        w = max(2, S // 5)
        d.line((c, S * 0.08, c, S * 0.92), fill=_rgb(hue, 0.5, 1.0), width=w)
        d.line((S * 0.08, c, S * 0.92, c), fill=_rgb(hue, 0.5, 1.0), width=w)
        disc(S * 0.12, _rgb(hue, 0.1, 1.0))
    elif family in ("bomb", "shrapnel", "mine"):
        if style == "orb":
            glow_orb(S * 0.38)
            for k in range(3):  # sparks around it, so a spin shows
                a = k * 2 * math.pi / 3
                x, y = c + math.cos(a) * S * 0.38, c + math.sin(a) * S * 0.38
                d.ellipse((x - S * 0.06, y - S * 0.06, x + S * 0.06, y + S * 0.06), fill=_rgb(hue + 0.05, 0.4, 1.0))
        elif style == "crystal":
            R = S * 0.44
            pts = [(c + math.cos(k * math.pi / 2) * R * (1 if k % 2 == 0 else 0.5),
                    c + math.sin(k * math.pi / 2) * R * (1 if k % 2 == 0 else 0.5)) for k in range(4)]
            d.polygon(pts, fill=_rgb(hue, 0.8, 0.75), outline=_rgb(hue, 0.35, 1.0))
            disc(S * 0.1, _rgb(hue, 0.2, 1.0))
        elif style == "star":
            n = 3 if family == "shrapnel" else 5
            pts = []
            for k in range(n * 2):
                a = k * math.pi / n - math.pi / 2
                rr = S * 0.46 if k % 2 == 0 else S * 0.17
                pts.append((c + math.cos(a) * rr, c + math.sin(a) * rr))
            d.polygon(pts, fill=_rgb(hue, 0.75, 0.9), outline=_rgb(hue, 0.3, 1.0))
            disc(S * 0.1, _rgb(hue, 0.15, 1.0))
        else:  # ring: a rim with a hub, the mine's stock look
            R, w = S * 0.38, max(2, int(S * 0.11))
            d.rounded_rectangle((c - R, c - R, c + R, c + R), radius=R * 0.45, outline=_rgb(hue, 0.75, 1.0), width=w)
            disc(S * 0.12, _rgb(hue + 0.06, 0.45, 0.85))
            d.line((c, c, c + R * 0.8, c), fill=_rgb(hue, 0.3, 1.0), width=max(1, w // 2))
    elif family == "prize":  # a box with a lit face and a mark
        h = S * 0.38
        d.rectangle((c - h, c - h, c + h, c + h), fill=_rgb(hue, 0.75, 0.55), outline=_rgb(hue, 0.4, 1.0), width=max(2, S // 16))
        d.rectangle((c - h * 0.45, c - h * 0.45, c + h * 0.45, c + h * 0.45), fill=_rgb(hue, 0.3, 1.0))
        d.line((c - h * 0.6, c - h * 0.6, c - h * 0.1, c - h * 0.6), fill=_rgb(hue, 0.1, 1.0), width=max(2, S // 20))
    elif family == "ball":
        glow_orb(S * 0.42)
        disc(S * 0.13, _rgb(hue, 0.1, 1.0))
        x = c + S * 0.18
        d.ellipse((x - S * 0.07, c - S * 0.2, x + S * 0.07, c - S * 0.06), fill=_rgb(hue, 0.15, 1.0))
    elif family == "flag":  # a pennant on a pole
        pole = max(2, S // 14)
        d.rectangle((c - S * 0.28, S * 0.1, c - S * 0.28 + pole, S * 0.9), fill=_rgb(hue, 0.15, 0.8))
        d.polygon([(c - S * 0.26, S * 0.12), (c + S * 0.38, S * 0.3), (c - S * 0.26, S * 0.52)],
                  fill=_rgb(hue, 0.8, 0.95), outline=_rgb(hue, 0.35, 1.0))
    elif family == "shield":  # a globe with meridians
        R = S * 0.42
        disc(R, _rgb(hue, 0.7, 0.45))
        w = max(1, S // 24)
        for k in (0.35, 0.75):
            d.ellipse((c - R * k, c - R, c + R * k, c + R), outline=_rgb(hue, 0.5, 1.0), width=w)
        d.line((c - R, c, c + R, c), fill=_rgb(hue, 0.5, 1.0), width=w)
        d.ellipse((c - R, c - R, c + R, c + R), outline=_rgb(hue, 0.35, 1.0), width=w * 2)
    elif family == "super":  # an open ring with a hot spot: spinning shows clearly
        R, w = S * 0.4, max(2, int(S * 0.12))
        d.arc((c - R, c - R, c + R, c + R), 20, 300, fill=_rgb(hue, 0.85, 1.0), width=w)
        d.arc((c - R * 0.6, c - R * 0.6, c + R * 0.6, c + R * 0.6), 200, 120, fill=_rgb(hue + 0.15, 0.7, 1.0), width=w // 2 + 1)
        disc(S * 0.08, _rgb(hue, 0.1, 1.0))
    elif family == "warppnt":
        R, w = S * 0.42, max(2, S // 16)
        d.ellipse((c - R, c - R, c + R, c + R), outline=_rgb(hue, 0.35, 0.85), width=w)
        for k in range(4):
            a = k * math.pi / 2
            d.line((c + math.cos(a) * R * 0.55, c + math.sin(a) * R * 0.55, c + math.cos(a) * R, c + math.sin(a) * R),
                   fill=_rgb(hue, 0.25, 1.0), width=w)
        disc(S * 0.15, _rgb(hue, 0.85, 0.9))
    return img


STYLES = {"bomb": ["orb", "crystal", "star"], "mine": ["ring", "crystal", "orb"], "shrapnel": ["star", "crystal"]}


# ---- what a row's role does to the body ------------------------------------------------------

def _role(body: Image.Image, row: Row, hue: float, accent: float | None, frame: int, frames: int, seed: int) -> Image.Image:
    """The body as this row's thing: level size, EMP crackle, bounce halo, thor orb, burst..."""
    S = body.width
    c = S / 2
    level = max(1, row.level)
    size = 0.84 + 0.055 * (level - 1)  # higher levels a touch bigger; colour carries the rest
    if row.role in ("regular", "item", "ball", "enemy", "own"):
        return _scaled(body, size) if row.level else body
    if row.role == "emp":  # a smaller, dimmer core inside crackling arcs that change every frame
        out = _shade(_scaled(body, size * 0.62), 0.8)
        d = ImageDraw.Draw(out)
        rng = random.Random(seed * 131 + level * 17 + frame)
        w = max(1, S // 22)
        for _ in range(5 + level):
            _bolt(d, c, S * 0.12, S * rng.uniform(0.36, 0.48), rng.uniform(0, 2 * math.pi), rng, _rgb(hue, 0.3, 1.0), w)
        for _ in range(6):
            a, r = rng.uniform(0, 2 * math.pi), S * rng.uniform(0.3, 0.47)
            x, y = c + math.cos(a) * r, c + math.sin(a) * r
            d.ellipse((x - w, y - w, x + w, y + w), fill=_rgb(hue, 0.55, 1.0))
        return out
    if row.role == "bounce":  # a dark halo with a bright rim around the shot, in the row's
        out = _blank(S)       # second colour when it has one (stock shrapnel: green)
        d = ImageDraw.Draw(out)
        R = S * 0.47
        rim = hue if accent is None else accent
        d.ellipse((c - R, c - R, c + R, c + R), fill=_rgb(rim, 0.6, 0.28), outline=_rgb(rim, 0.3, 1.0), width=max(1, S // 20))
        out.alpha_composite(_scaled(body, size * 0.8))
        return out
    if row.role == "thor":  # its own item: a heavy orb with a spark circling it
        out = _blank(S)
        d = ImageDraw.Draw(out)
        R = S * 0.36
        for i in range(8, 0, -1):
            r = R * i / 8
            d.ellipse((c - r, c - r, c + r, c + r), fill=_rgb(hue, 0.2 + 0.08 * i, 0.5 + 0.06 * (8 - i)))
        a = 2 * math.pi * frame / max(1, frames)
        x, y = c + math.cos(a) * S * 0.36, c + math.sin(a) * S * 0.36
        r = S * 0.08
        d.ellipse((x - r, y - r, x + r, y + r), fill=_rgb(hue + 0.08, 0.3, 1.0))
        return out
    if row.role == "burst":  # the burst item before it goes off: small and dim
        return _shade(_scaled(body, 0.62), 0.65)
    if row.role == "armed":  # going off: a colour cycle, as the stock sheet does
        return _tint(_scaled(body, 1.0), hue + frame / max(1, frames), hue)
    if row.role == "phased":  # a ghost of the ball: dim, with every other line faded
        out = _shade(body, 0.6, 0.85)
        a = out.getchannel("A")
        ap = a.load()
        band = max(1, S // 16)
        for y in range(S):
            if (y // band) % 2:
                for x in range(S):
                    ap[x, y] = ap[x, y] // 3
        out.putalpha(a)
        return out
    return body  # trail: handled by its "fade" animation


# ---- animation within a row -------------------------------------------------------------------

def _tilt(img: Image.Image, t: float) -> Image.Image:
    """A coin-like tumble: about an axis leaning TILT_DEGREES from upright, seen slightly from
    above, so the picture narrows to an edge and darkens mid-turn, then shows its mirrored back."""
    S = img.width
    k = math.cos(t * 2 * math.pi)
    face = ImageOps.mirror(img) if k < 0 else img
    face = face.rotate(TILT_DEGREES, resample=Image.BICUBIC)
    w = max(2, round(S * max(0.12, abs(k))))
    h = round(S * 0.92)
    face = _shade(face.resize((w, h), Image.LANCZOS), (0.55 + 0.45 * abs(k)) * (1.0 if k >= 0 else 0.8))
    out = _blank(S)
    out.alpha_composite(face, ((S - w) // 2, (S - h) // 2))
    return out.rotate(-TILT_DEGREES, resample=Image.BICUBIC)


def _animate(img: Image.Image, anim: str, t: float, frame: int, seed: int) -> Image.Image:
    if anim == "spin":
        return img.rotate(-t * 360, resample=Image.BICUBIC)
    if anim == "tilt":
        return _tilt(img, t)
    if anim == "pulse":
        k = 0.88 + 0.12 * math.sin(t * 2 * math.pi)
        return _shade(_scaled(img, k), 0.85 + 0.15 * math.sin(t * 2 * math.pi))
    if anim == "glow":  # brightens and dims in place (tiles that must keep their edges)
        return _shade(img, 0.8 + 0.2 * math.sin(t * 2 * math.pi))
    if anim == "flicker":
        return img.rotate(-(frame % 2) * 12, resample=Image.BICUBIC)
    if anim == "fade":  # trail: shrinks, dims and breaks up over its frames
        img = _shade(_scaled(img, 1.0 - 0.6 * t), 1.0 - 0.45 * t, 1.0 - 0.5 * t)
        return _dissolve(img, min(0.92, 0.15 + 0.85 * t), seed + frame)
    return img


def _fit(sprite: Image.Image, w: int, h: int) -> Image.Image:
    if sprite.size == (w, h):
        return sprite
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    s = sprite.resize((min(w, h), min(w, h)), Image.LANCZOS)
    out.alpha_composite(s, ((w - s.width) // 2, (h - s.height) // 2))
    return out


HUE_BINS = 24


def _hue_bins(img: Image.Image, box) -> list[float]:
    """How much of each hue a region shows, bright saturated pixels counting most."""
    bins = [0.0] * HUE_BINS
    for r, g, b, a in img.crop(box).getdata():
        if not a:
            continue
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        bins[int(h * HUE_BINS) % HUE_BINS] += s * v ** 3
    return bins


def _peak_hue(bins: list[float], skip: float | None = None) -> tuple[float | None, float]:
    """The strongest hue (smoothed over its neighbours) and its weight, away from `skip`."""
    best, weight = None, 0.0
    for i, w in enumerate(bins):
        if skip is not None and _hue_gap((i + 0.5) / HUE_BINS, skip) < 0.12:
            continue
        if w > weight:
            best, weight = i, w
    if best is None or weight <= 0:
        return None, 0.0
    sx = sy = 0.0
    for j in (best - 1, best, best + 1):
        w, h = bins[j % HUE_BINS], (j % HUE_BINS + 0.5) / HUE_BINS
        sx += math.cos(h * 2 * math.pi) * w
        sy += math.sin(h * 2 * math.pi) * w
    return (math.atan2(sy, sx) / (2 * math.pi)) % 1.0, weight


def _hue_gap(a: float, b: float) -> float:
    d = abs(a - b) % 1.0
    return min(d, 1 - d)


def _row_colours(template: Image.Image, cw: int, ch: int, rng: random.Random) -> list[tuple[float, float | None] | None]:
    """Each row's (colour, second colour or None), None for an empty row; grey rows get a random
    colour. Hues every row shares (a mine's brown hub) are set aside first, so the colour that
    tells one level or type from another is the one kept."""
    rows = []
    for row in range(template.height // ch):
        box = (0, row * ch, template.width, row * ch + ch)
        rows.append(None if template.crop(box).getchannel("A").getbbox() is None else _hue_bins(template, box))
    live = [b for b in rows if b is not None]
    shared = [min(b[i] for b in live) for i in range(HUE_BINS)] if len(live) > 2 else [0.0] * HUE_BINS
    out = []
    for bins in rows:
        if bins is None:
            out.append(None)
            continue
        own = [max(0.0, w - s) for w, s in zip(bins, shared)]
        hue, weight = _peak_hue(own)
        if hue is None or weight < 0.2 * max(bins):
            hue, weight = _peak_hue(bins)
        if hue is None or weight < 0.05:
            out.append((rng.random(), None))
            continue
        accent, aw = _peak_hue(own, skip=hue)
        out.append((hue, accent if accent is not None and aw >= 0.25 * weight else None))
    return out


def new_sprites(kind: str, template: Image.Image, rng: random.Random, sprite: Image.Image | None = None) -> Image.Image:
    """Every row drawn as what it means (SHEETS), from shapes of our own or from `sprite`."""
    sheet = SHEETS[kind]
    cw, ch = sheet.cell
    side = max(cw, ch)
    S = side * 4
    out = Image.new("RGBA", template.size, (0, 0, 0, 0))
    if sprite is None and not sheet.body:
        raise GraphicError("That graphic can be recoloured or drawn from a description.")
    style = rng.choice(STYLES.get(sheet.body, [""]))
    seed = rng.randrange(1_000_000)
    base_hue = _hue_of(sprite, (0, 0, sprite.width, sprite.height)) if sprite is not None else None
    drawn = None
    if sprite is not None:
        drawn = sprite.resize((S, S), Image.LANCZOS)
        if not sheet.full:
            drawn = _scaled(drawn, 0.92)
    cols = min(sheet.frames, template.width // cw)
    colour_rows = _row_colours(template, cw, ch, rng)
    for r, colours in enumerate(colour_rows):
        if colours is None:
            continue
        hue, accent = colours
        row = row_spec(kind, r)
        if drawn is None:
            body = _body(sheet.body, style, S, hue)
        elif sheet.keep_colour:
            body = drawn
        else:
            body = _tint(drawn, hue, base_hue)
        if sheet.body == "bullet":
            body = _shade(body, 1.2)
        for col in range(cols):
            t = (r * cols + col) / (len(colour_rows) * cols) if sheet.continuous else col / cols
            frame = _animate(_role(body, row, hue, accent, col, cols, seed), row.anim, t, col, seed)
            frame = frame.filter(ImageFilter.GaussianBlur(S / 96)).resize((side, side), Image.LANCZOS)
            out.alpha_composite(_fit(frame, cw, ch), (col * cw, r * ch))
    return out


# ---- timed animations played once -------------------------------------------------------------

def _burst_frame(style: str, S: int, hue: float, t: float, debris, seed: int) -> Image.Image:
    """Frame at time t in [0, 1] of a one-shot animation, at 4x."""
    img = _blank(S)
    d = ImageDraw.Draw(img)
    c = S / 2
    fade = 1 - t

    def disc(r, fill):
        if r > 0.5:
            d.ellipse((c - r, c - r, c + r, c + r), fill=fill)

    def ring(r, fill, w):
        if r > 0.5 and w > 0:
            d.ellipse((c - r, c - r, c + r, c + r), outline=fill, width=max(1, int(w)))

    if style in ("explosion", "spark"):
        # a fireball of overlapping puffs: white-hot and swelling, then the row's colour, then
        # dark embers breaking up; sparks fly out the whole time
        grow = min(1.0, t / 0.35)
        heat = max(0.0, 1 - t * 1.6)
        size = 0.42 if style == "explosion" else 0.3
        for a, speed, s in debris:
            r = S * size * 0.55 * speed * grow
            x, y = c + math.cos(a) * r, c + math.sin(a) * r
            rr = S * (s * 2.6 + 0.12) * (0.4 + 0.6 * grow) * (1 if style == "explosion" else 0.6)
            d.ellipse((x - rr, y - rr, x + rr, y + rr), fill=_rgb(hue - 0.04 * t, 0.95, 0.3 + 0.6 * (1 - t)))
        for a, speed, s in debris:  # each puff's hotter middle
            r = S * size * 0.45 * speed * grow
            x, y = c + math.cos(a) * r, c + math.sin(a) * r
            rr = S * (s * 2.6 + 0.12) * (0.4 + 0.6 * grow) * (1 if style == "explosion" else 0.6) * 0.55
            d.ellipse((x - rr, y - rr, x + rr, y + rr), fill=_rgb(hue + 0.06, 0.25 + 0.65 * t, 1.0 - 0.55 * t))
        disc(S * size * 0.55 * heat * (0.4 + 0.6 * grow), _rgb(hue + 0.08, 0.1, 1.0))
        for a, speed, s in debris[::2]:
            r = S * 0.47 * speed * t + S * size * 0.4
            x, y = c + math.cos(a + 0.3) * r, c + math.sin(a + 0.3) * r
            k = S * 0.03 * (1 - t) + 1
            d.ellipse((x - k, y - k, x + k, y + k), fill=_rgb(hue + 0.05, 0.4, 1.0))
        img = _dissolve(img, 0.1 + max(0.0, (t - 0.45) / 0.55 * 0.85), seed + int(t * 1000))
    elif style == "emp":  # an electric ring rushing outward, crackling, then gone
        R = S * (0.1 + 0.38 * t)
        for i in range(4, 0, -1):
            disc(R * i / 4, _rgb(hue, 0.7, (0.12 + 0.06 * i) * fade))
        disc(S * 0.16 * fade * fade, _rgb(hue, 0.15, 1.0))
        ring(R, _rgb(hue, 0.45, 0.5 + 0.5 * fade), S * 0.025 + 1)
        rng = random.Random(seed + int(t * 1000))
        for _ in range(22):
            a = rng.uniform(0, 2 * math.pi)
            _bolt(d, c, R * rng.uniform(0.1, 0.5), R * 1.05, a, rng, _rgb(hue, 0.25, 0.4 + 0.6 * fade), max(2, int(S * 0.025)))
        if t > 0.5:
            img = _dissolve(img, (t - 0.5) / 0.5 * 0.9, seed + int(t * 1000))
    elif style == "warp":  # arms swirl in, flash, and swirl out
        R = S * 0.46 * math.sin(math.pi * t)
        for k in range(3):
            pts = []
            for i in range(12):
                u = i / 11
                a = k * 2 * math.pi / 3 + u * 2.4 + t * 4 * math.pi
                pts.append((c + math.cos(a) * R * u, c + math.sin(a) * R * u))
            d.line(pts, fill=_rgb(hue, 0.6, 1.0), width=max(1, int(S * 0.04)))
        disc(S * 0.12 * math.sin(math.pi * t), _rgb(hue, 0.1, 1.0))
    elif style == "grow":  # repel: a ring that keeps growing, glow first, then thinning out
        R = S * (0.1 + 0.37 * t)
        for i in range(6, 0, -1):
            disc(R * i / 6, _rgb(hue, 0.6, (0.15 + 0.1 * i) * (1 - 0.8 * t)))
        ring(R, _rgb(hue, 0.5, 1.0 - 0.3 * t), S * (0.06 - 0.035 * t) + 1)
    elif style in ("rocket", "puff"):  # a flash, a glowing ball that swells, then a smoke ring
        if t < 0.08:
            r = S * (0.1 + 0.6 * t)
            w = max(1, int(S * 0.03))
            d.line((c - r * 1.6, c, c + r * 1.6, c), fill=_rgb(hue, 0.15, 1.0), width=w)
            d.line((c, c - r * 1.6, c, c + r * 1.6), fill=_rgb(hue, 0.15, 1.0), width=w)
            disc(r * 0.5, _rgb(hue, 0.1, 1.0))
        elif t < 0.45:
            u = (t - 0.08) / 0.37
            R = S * (0.14 + 0.2 * u)
            for i in range(6, 0, -1):
                disc(R * i / 6, _rgb(hue, 0.2 + 0.1 * i, 1.0 - 0.06 * i))
        else:
            u = (t - 0.45) / 0.55
            R = S * (0.34 + 0.12 * u)  # starts where the ball ended, so it only grows
            ring(R, _rgb(hue, 0.35 * (1 - u), 0.7 - 0.3 * u), R * 0.45)
            img = _dissolve(img, 0.25 + 0.7 * u, seed + int(t * 1000))
    return img


def _whiten(img: Image.Image, k: float) -> Image.Image:
    """`img` blended toward white by k (0..1), outline kept: the white-hot start of a blast."""
    if k <= 0:
        return img
    white = Image.new("RGBA", img.size, (255, 255, 255, 0))
    white.putalpha(img.getchannel("A"))
    return Image.blend(img, white, min(1.0, k))


def _burst_from_drawing(style: str, img: Image.Image, t: float, seed: int) -> Image.Image:
    """Frame at time t in [0, 1] of a one-shot animation made from one drawing of the effect at
    its fullest (`img`, at 4x): each style moves it the way the stock animation moves."""
    frame = seed + int(t * 1000)
    if style in ("explosion", "spark"):  # flash and bloom out, then spread, darken and break up
        if t < 0.3:
            u = t / 0.3
            return _whiten(_scaled(img, 0.2 + 0.8 * (1 - (1 - u) ** 2)), 0.7 * (1 - u))
        v = (t - 0.3) / 0.7
        return _dissolve(_shade(_scaled(img, 1 + 0.3 * v), 1 - 0.55 * v), 0.05 + 0.9 * v ** 1.3, frame)
    if style == "emp":  # rushes outward, crackling (a small twitch every frame), then gone
        out = _scaled(img, 0.15 + 0.95 * t).rotate((-1) ** int(t * 40) * 10, resample=Image.BICUBIC)
        out = _whiten(out, 0.5 * (1 - t) ** 2)
        return _dissolve(out, (t - 0.5) / 0.5 * 0.9, frame) if t > 0.5 else out
    if style == "warp":  # swirls in from a point, flashes, and swirls out
        k = math.sin(math.pi * t)
        return _whiten(_scaled(img, 0.1 + 0.9 * k).rotate(-t * 540, resample=Image.BICUBIC), 0.5 * (1 - k))
    if style == "grow":  # repel: only grows, thinning out
        out = _shade(_scaled(img, 0.15 + 0.85 * t), 1.0, 1 - 0.6 * t)
        return _dissolve(out, (t - 0.6) / 0.4 * 0.85, frame) if t > 0.6 else out
    if style in ("rocket", "puff"):  # a flash, a swell, then smoke breaking up
        if t < 0.1:
            return _whiten(_scaled(img, 0.3 + 2 * t), 0.8)
        if t < 0.5:
            u = (t - 0.1) / 0.4
            return _whiten(_scaled(img, 0.5 + 0.5 * u), 0.3 * (1 - u))
        v = (t - 0.5) / 0.5
        return _dissolve(_shade(_scaled(img, 1 + 0.15 * v), 1 - 0.5 * v), 0.2 + 0.75 * v, frame)
    return img


def new_burst(kind: str, template: Image.Image, rng: random.Random, sprite: Image.Image | None = None) -> Image.Image:
    """The effect's frames, procedural or (with `sprite`) made from a drawing of it at its fullest."""
    spec = BURST_SHEETS[kind]
    side = spec.side
    cols, rows = template.width // side, template.height // side
    seed = rng.randrange(1_000_000)
    debris = [(rng.uniform(0, 2 * math.pi), rng.uniform(0.5, 1.0), rng.uniform(0.04, 0.09)) for _ in range(14)]
    out = Image.new("RGBA", template.size, (0, 0, 0, 0))

    def used(f):
        x, y = f % cols * side, f // cols * side
        return template.crop((x, y, x + side, y + side)).getchannel("A").getbbox() is not None

    if spec.per_row:  # each row its own animation, in that row's colour
        runs = []
        for r in range(rows):
            frames = [r * cols + i for i in range(cols)]
            hue = _peak_hue(_hue_bins(template, (0, r * side, template.width, r * side + side)))[0]
            runs.append((frames, hue))
    else:
        runs = [(list(range(cols * rows)), _peak_hue(_hue_bins(template, (0, 0, template.width, template.height)))[0])]
    shift = rng.uniform(-0.08, 0.08)
    S = side * 4
    drawn = _scaled(sprite.resize((S, S), Image.LANCZOS), 0.92) if sprite is not None else None
    base_hue = _hue_of(sprite, (0, 0, sprite.width, sprite.height)) if sprite is not None else None
    first_hue = runs[0][1]
    for frames, hue in runs:
        look = drawn
        if drawn is not None and base_hue is not None and hue is not None and first_hue is not None and hue != first_hue:
            # A drawing keeps its own colours; a later row (afterburners) moves as far round the
            # colour wheel from the first as the stock rows do, so the two still read apart.
            look = _tint(drawn, base_hue + hue - first_hue, base_hue)
        hue = rng.random() if hue is None else hue + shift
        live = [f for f in frames if used(f)]  # keep unused frames empty; play over the used ones
        for i, f in enumerate(live):
            t = i / max(1, len(live) - 1)
            if look is not None:
                frame = _burst_from_drawing(spec.style, look, t, seed)
            else:
                frame = _burst_frame(spec.style, S, hue, t, debris, seed)
            frame = frame.filter(ImageFilter.GaussianBlur(S / 80)).resize((side, side), Image.LANCZOS)
            out.alpha_composite(frame, (f % cols * side, f // cols * side))
    return out


# ---- from a prompt -------------------------------------------------------------------------

SPRITE_PROMPT = (
    "Draw exactly one small game sprite, seen from above, centred on a transparent background: "
    "{description}. Bold readable silhouette and strong colour, so it still reads at {px} pixels. "
    "No text, no frame, no grid, no scenery, no shadow."
)
# Effects are drawn once, at their fullest; the sheet's frames are made from that one picture.
EFFECT_PROMPT = (
    "Draw exactly one game visual effect, seen from directly above, centred on a transparent "
    "background, at the moment it is at its fullest: {description}. Bright, bold and readable "
    "at {px} pixels, filling most of the picture. No text, no frame, no grid, no scenery."
)
# What a picture of each sheet is, so a description like "green plasma" or "tiny coins" comes
# back as a projectile, item or effect of the right kind and weight rather than a scene or a ship.
BRIEFS = {
    "bullets": "a tiny glowing energy bullet from a space fighter's gun, a simple round or "
               "teardrop bolt with a bright core",
    "bombs": "a round bomb fired by a space fighter, a compact orb or shell with a glowing core",
    "mines": "a floating space mine, a compact armed orb or spiked device",
    "shrapnel": "a tiny sharp shard of shrapnel, a jagged glowing fragment",
    "prizes": "a floating power-up item, a compact box or token",
    "powerb": "a round glowing ball for a ball game",
    "flag": "a team flag on a short pole",
    "shield": "a round protective energy bubble",
    "super": "a glowing power-up ring",
    "warppnt": "a small glowing warp point marker",
    "over1": "a small floating asteroid or space rock",
    "over2": "a large floating asteroid or space rock",
    "over3": "a small floating asteroid or space rock",
    "over4": "a large round space station seen from directly above",
    "over5": "a round swirling wormhole",
    "goal": "a small glowing goal marker tile",
    "wall": "one square wall brick tile filling the whole square, edge to edge",
    "explode0": "a tiny burst where a bullet hits something",
    "explode1": "a round explosion bursting outward in every direction",
    "explode2": "a big round explosion of a destroyed spaceship bursting outward in every direction",
    "empburst": "an electric shockwave bursting outward in a ring",
    "warp": "a round swirling warp flash",
    "repel": "a round shockwave ring pushing outward",
    "rocket": "a round flash of a rocket booster firing",
    "exhaust": "a small round puff of engine exhaust",
}
# Try again: take 1, 2... asks for another take, so the next picture differs from those shown.
TAKES = [
    "a clearly different shape and silhouette", "a different colour scheme",
    "rounder and softer", "sharper and more angular", "busier, with many small parts",
    "bolder and simpler, with a few big parts", "more glow and light", "darker and grittier",
]


def promptable(kind: str) -> bool:
    return kind in SHEETS or kind in BURST_SHEETS


def sprite_description(kind: str, description: str) -> str:
    """The pilot's description with what the sprite is for (a bomb, a mine...), when known."""
    brief = BRIEFS.get(kind)
    return f"{description}, drawn as {brief}" if brief else description


def again(description: str, take: int, rng: random.Random | None = None) -> str:
    """Take `take` (1, 2...) of a description: the same subject, a different look."""
    rng = rng or random.Random()
    return f"{description}. Another take on it, keeping what it asks for but {rng.choice(TAKES)}"


def _side_px(kind: str) -> int:
    return max(SHEETS[kind].cell) if kind in SHEETS else BURST_SHEETS[kind].side


def draw_picture(kind: str, description: str, seed: int | None = None) -> Image.Image:
    """One PixelLab picture of `kind` for the pilot's description (one paid call)."""
    if not promptable(kind):
        raise GraphicError("That graphic can't be drawn from a description.")
    px = _side_px(kind)
    return pixel_lab_sprite(
        sprite_description(kind, description), px=px,
        prompt=EFFECT_PROMPT if kind in BURST_SHEETS else SPRITE_PROMPT,
        size=64 if px <= 48 else 128, fill=kind in SHEETS and SHEETS[kind].full, seed=seed,
    )


def from_picture(kind: str, template_bytes: bytes, picture: Image.Image, seed: int | None = None) -> bytes:
    """The sheet for `kind` made from one drawing, the template's size and frame layout."""
    rng = random.Random(seed)
    template = load_template(template_bytes)
    if kind in SHEETS:
        return encode(new_sprites(kind, template, rng, sprite=picture))
    if kind in BURST_SHEETS:
        return encode(new_burst(kind, template, rng, sprite=picture))
    raise GraphicError("That graphic can't be drawn from a description.")


def pixel_lab_sprite(
    description: str, px: int = 16, *, prompt: str = SPRITE_PROMPT, size: int = 64,
    fill: bool = False, seed: int | None = None,
) -> Image.Image:
    """One picture from PixelLab, cropped to what it drew and made square (stretched when `fill`)."""
    body = {"description": prompt.format(description=description, px=px),
            "image_size": {"width": size, "height": size}, "no_background": True}
    if seed is not None:
        body["seed"] = seed
    response = requests.post(
        shipgen.API_URL,
        headers={"Authorization": f"Bearer {shipgen._api_key()}"},
        json=body,
        timeout=(10, 90),
    )
    if response.status_code == 402:
        raise RuntimeError("PixelLab is out of credits.")
    if response.status_code == 429:
        raise RuntimeError("PixelLab is rate limited. Try again shortly.")
    if not response.ok:
        raise RuntimeError(f"PixelLab returned HTTP {response.status_code}.")
    raw = base64.b64decode(response.json()["image"]["base64"])
    with Image.open(io.BytesIO(raw), formats=("PNG",)) as source:
        img = source.convert("RGBA")
    box = img.getchannel("A").getbbox()
    if box is None:
        raise RuntimeError("The drawing came back empty. Try another description.")
    img = img.crop(box)
    side = max(img.size)
    if fill:
        return img.resize((side, side), Image.LANCZOS)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(img, ((side - img.width) // 2, (side - img.height) // 2))
    return square


def generate(kind: str, mode: str, template_bytes: bytes, prompt: str | None = None, seed: int | None = None) -> bytes:
    """A new sheet for `kind`, the same size as the template. Raises GraphicError or RuntimeError."""
    rng = random.Random(seed)
    template = load_template(template_bytes)
    if mode == "recolor":
        return encode(recolor(template, rng))
    if mode == "new":
        if kind in SHEETS:
            return encode(new_sprites(kind, template, rng))
        if kind in BURST_SHEETS:
            return encode(new_burst(kind, template, rng))
        raise GraphicError("That graphic can only be recoloured.")
    if mode == "prompt":
        if not promptable(kind):
            raise GraphicError("That graphic can't be drawn from a description.")
        return from_picture(kind, template_bytes, draw_picture(kind, prompt or ""), seed=seed)
    raise GraphicError("Choose Recolour, New look or From a description.")
