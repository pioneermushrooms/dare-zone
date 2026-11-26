"""New pictures for the game's other graphics: bullets, bombs, mines, items, explosions...

Every generator starts from the arena's current sheet (the template) and returns a PNG of
exactly the same size and frame layout, so the game reads it where the stock file was:

  recolor  every colour turned to a new hue (shapes and animation kept)
  new      fresh procedural art per row and frame (orbs, spinning items, bursts), each row
           keeping the template row's colour so weapon levels still read apart
  prompt   one sprite drawn by PixelLab from a description, then animated (spun or pulsed)
           and tinted per row like "new"

Black is see-through in the game, so empty pixels are transparent black and drawn pixels
never use pure black.
"""

import base64
import colorsys
import io
import math
import random

import requests
from PIL import Image, ImageDraw, ImageFilter

import shipgen

# kind -> (cell width, cell height, frames per row). Rows are levels or variants.
SPRITES = {
    "bullets": (5, 5, 4),
    "bombs": (16, 16, 10),
    "mines": (16, 16, 10),
    "shrapnel": (8, 8, 10),
    "prizes": (16, 16, 10),
    "powerb": (16, 16, 10),
    "flag": (16, 16, 10),
    "warppnt": (16, 16, 10),
}
# kind -> square cell side; frames run left to right, top to bottom.
BURSTS = {"explode0": 16, "explode1": 48, "explode2": 80, "empburst": 80, "warp": 48, "repel": 96}
MAX_SIDE = 4096


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


# ---- procedural sprites ----------------------------------------------------------------------

def _draw_sprite(style: str, size: int, hue: float, t: float, rng_seed: int) -> Image.Image:
    """One frame at 4x, phase t in [0, 1)."""
    S = size * 4
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = S / 2
    ang = t * 2 * math.pi
    pulse = 0.85 + 0.15 * math.sin(ang)
    if style == "orb":
        R = S * 0.36 * pulse
        for i in range(8, 0, -1):  # soft glow to a hot core
            r = R * i / 8
            d.ellipse((c - r, c - r, c + r, c + r), fill=_rgb(hue, 0.25 + 0.09 * i, 0.55 + 0.05 * (8 - i)))
        for k in range(3):  # sparks circling
            a = ang + k * 2 * math.pi / 3
            x, y = c + math.cos(a) * S * 0.38, c + math.sin(a) * S * 0.38
            r = S * 0.06
            d.ellipse((x - r, y - r, x + r, y + r), fill=_rgb(hue + 0.05, 0.4, 1.0))
    elif style == "crystal":
        R = S * 0.42
        pts = [(c + math.cos(ang + k * math.pi / 2) * R * (1 if k % 2 == 0 else 0.55 * pulse),
                c + math.sin(ang + k * math.pi / 2) * R * (1 if k % 2 == 0 else 0.55 * pulse)) for k in range(4)]
        d.polygon(pts, fill=_rgb(hue, 0.8, 0.75), outline=_rgb(hue, 0.35, 1.0))
        r = S * 0.1
        d.ellipse((c - r, c - r, c + r, c + r), fill=_rgb(hue, 0.2, 1.0))
    elif style == "star":
        n = 5 + rng_seed % 3
        R, r2 = S * 0.44 * pulse, S * 0.18
        pts = []
        for k in range(n * 2):
            a = ang / 2 + k * math.pi / n
            rr = R if k % 2 == 0 else r2
            pts.append((c + math.cos(a) * rr, c + math.sin(a) * rr))
        d.polygon(pts, fill=_rgb(hue, 0.75, 0.9), outline=_rgb(hue, 0.3, 1.0))
    elif style == "ring":
        R = S * 0.4 * pulse
        w = max(2, int(S * 0.1))
        d.ellipse((c - R, c - R, c + R, c + R), outline=_rgb(hue, 0.7, 1.0), width=w)
        a = ang
        x, y = c + math.cos(a) * R * 0.45, c + math.sin(a) * R * 0.45
        r = S * 0.12
        d.ellipse((x - r, y - r, x + r, y + r), fill=_rgb(hue, 0.3, 1.0))
    elif style == "item":  # a box spinning about its vertical axis, with a lit face
        w = abs(math.cos(ang)) * S * 0.34 + S * 0.06
        h = S * 0.36
        d.rectangle((c - w, c - h, c + w, c + h), fill=_rgb(hue, 0.75, 0.55), outline=_rgb(hue, 0.4, 1.0), width=max(2, S // 16))
        g = w * 0.45
        d.rectangle((c - g, c - h * 0.45, c + g, c + h * 0.45), fill=_rgb(hue, 0.3, 1.0))
    return img.filter(ImageFilter.GaussianBlur(S / 64)).resize((size, size), Image.LANCZOS)


def _fit(sprite: Image.Image, w: int, h: int) -> Image.Image:
    if sprite.size == (w, h):
        return sprite
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    s = sprite.resize((min(w, h), min(w, h)), Image.LANCZOS)
    out.alpha_composite(s, ((w - s.width) // 2, (h - s.height) // 2))
    return out


def _row_hues(template: Image.Image, cw: int, ch: int, rng: random.Random) -> list[float | None]:
    """The hue of each row of cells, None for an empty row; grey rows get a random hue."""
    rows = []
    for row in range(template.height // ch):
        box = (0, row * ch, template.width, row * ch + ch)
        if template.crop(box).getchannel("A").getbbox() is None:
            rows.append(None)
            continue
        hue = _hue_of(template, box)
        rows.append(hue if hue is not None else rng.random())
    return rows


def new_sprites(kind: str, template: Image.Image, rng: random.Random, sprite: Image.Image | None = None) -> Image.Image:
    cw, ch, per_row = SPRITES[kind]
    out = Image.new("RGBA", template.size, (0, 0, 0, 0))
    style = "item" if kind == "prizes" else rng.choice(["orb", "crystal", "star", "ring"])
    base_hue = _hue_of(sprite, (0, 0, sprite.width, sprite.height)) if sprite is not None else None
    seed = rng.randrange(1000)
    for row, hue in enumerate(_row_hues(template, cw, ch, rng)):
        if hue is None:
            continue
        cols = min(per_row, template.width // cw)
        for col in range(cols):
            t = col / cols
            if sprite is None:
                frame = _draw_sprite(style, max(cw, ch), hue, t, seed)
            else:
                frame = _animate_prompted(sprite, max(cw, ch), hue, base_hue, t, kind)
            out.alpha_composite(_fit(frame, cw, ch), (col * cw, row * ch))
    return out


def new_burst(kind: str, template: Image.Image, rng: random.Random) -> Image.Image:
    side = BURSTS[kind]
    cols, rows = template.width // side, template.height // side
    n = cols * rows
    hue = _hue_of(template, (0, 0, template.width, template.height))
    hue = rng.random() if hue is None else (hue + rng.uniform(-0.08, 0.08))
    debris = [(rng.uniform(0, 2 * math.pi), rng.uniform(0.5, 1.0), rng.uniform(0.04, 0.09)) for _ in range(14)]
    out = Image.new("RGBA", template.size, (0, 0, 0, 0))
    for f in range(n):
        if template.crop((f % cols * side, f // cols * side, f % cols * side + side, f // cols * side + side)).getchannel("A").getbbox() is None:
            continue  # keep unused frames empty
        t = f / max(1, n - 1)
        S = side * 4
        img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        c = S / 2
        fade = 1 - t
        core = S * 0.3 * math.sin(math.pi * min(1.0, t * 1.7))  # flares, then burns out
        if core > 1:
            for i in range(5, 0, -1):
                r = core * i / 5
                d.ellipse((c - r, c - r, c + r, c + r), fill=_rgb(hue + 0.08, 0.25 + 0.12 * i, 1.0 - 0.5 * t))
        ring = S * (0.12 + 0.36 * t)
        d.ellipse((c - ring, c - ring, c + ring, c + ring), outline=_rgb(hue, 0.8, fade), width=max(2, int(S * 0.05 * fade) + 1))
        for a, speed, size in debris:
            r = S * 0.45 * speed * t
            x, y = c + math.cos(a) * r, c + math.sin(a) * r
            s = S * size * fade
            if s > 0.5:
                d.ellipse((x - s, y - s, x + s, y + s), fill=_rgb(hue - 0.04, 0.6, fade))
        frame = img.filter(ImageFilter.GaussianBlur(S / 80)).resize((side, side), Image.LANCZOS)
        out.alpha_composite(frame, (f % cols * side, f // cols * side))
    return out


# ---- from a prompt -------------------------------------------------------------------------

SPRITE_PROMPT = (
    "Draw exactly one small game sprite, seen from above, centred on a transparent background: "
    "{description}. Bold readable silhouette and strong colour, so it still reads at 16 pixels. "
    "No text, no frame, no grid, no scenery, no shadow."
)


def pixel_lab_sprite(description: str) -> Image.Image:
    response = requests.post(
        shipgen.API_URL,
        headers={"Authorization": f"Bearer {shipgen._api_key()}"},
        json={"description": SPRITE_PROMPT.format(description=description),
              "image_size": {"width": 64, "height": 64}, "no_background": True},
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
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(img, ((side - img.width) // 2, (side - img.height) // 2))
    return square


def _animate_prompted(sprite: Image.Image, size: int, hue: float, base_hue: float | None, t: float, kind: str) -> Image.Image:
    img = sprite
    if base_hue is not None:  # turn the drawing toward this row's colour
        shift = hue - base_hue
        img = img.copy()
        px = img.load()
        for y in range(img.height):
            for x in range(img.width):
                r, g, b, a = px[x, y]
                if a:
                    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
                    px[x, y] = _rgb(h + shift, s, v, a)
    if kind == "bullets":  # too small to spin: pulse instead
        k = 0.85 + 0.15 * math.sin(t * 2 * math.pi)
        img = img.point(lambda v: min(255, round(v * k)))
    else:
        img = img.rotate(-t * 360, resample=Image.BICUBIC, expand=False)
    return img.resize((size, size), Image.LANCZOS)


def generate(kind: str, mode: str, template_bytes: bytes, prompt: str | None = None, seed: int | None = None) -> bytes:
    """A new sheet for `kind`, the same size as the template. Raises GraphicError or RuntimeError."""
    rng = random.Random(seed)
    template = load_template(template_bytes)
    if mode == "recolor":
        return encode(recolor(template, rng))
    if mode == "new":
        if kind in SPRITES:
            return encode(new_sprites(kind, template, rng))
        if kind in BURSTS:
            return encode(new_burst(kind, template, rng))
        raise GraphicError("That graphic can only be recoloured.")
    if mode == "prompt":
        if kind not in SPRITES:
            raise GraphicError("That graphic can't be drawn from a description.")
        sprite = pixel_lab_sprite(prompt or "")
        return encode(new_sprites(kind, template, rng, sprite=sprite))
    raise GraphicError("Choose Recolour, New look or From a description.")
