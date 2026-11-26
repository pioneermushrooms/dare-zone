"""Wall tile art for Continuum maps: packs of 16 px tiles that join edge to edge.

A wall cell's picture depends on which of its eight neighbours are wall too (its mask). Every
pixel is drawn from two numbers only:

  d     how far the pixel is from the nearest place the wall stops: 0 on the outline, 1 next
        to it, ... An outline runs along each closed side (no wall that way) and, where two
        open sides meet at a missing diagonal, round the inner corner (Chebyshev distance).
  face  which way that outline faces (N, E, S or W), for light from the top left.

plus the pixel's position in the 16 x 16 cell (patterns repeat every tile). Because d and face
at the edge of one tile carry on in the next tile exactly as they would on one big picture,
neighbouring pieces join seamlessly: test_tilegen checks d against a distance transform of a
whole map.

There are 256 masks but only 47 differ (a diagonal only matters when both sides next to it are
open). The site gets those 47 pieces per style (tileart-styles.js) and builds tilesets in the
browser from the standard tile layout (tile-layout.json), or paints the slots a map uses with
the piece its walls call for.

Run: python3 tilegen.py            (writes tilesets/*.bmp, *.png and ../src/.../tileart-styles.js)
Black is see-through in the game, so drawn pixels never use pure black.
"""

from __future__ import annotations

import base64
import io
import json
import os
import random
import struct
from dataclasses import dataclass, field
from typing import Callable

from PIL import Image

TS = 16
COLS, ROWS = 19, 10
N, NE, E, SE, S, SW, W, NW = 1, 2, 4, 8, 16, 32, 64, 128
SIDES = {"N": N, "E": E, "S": S, "W": W}
FAR = 99  # d for a pixel no outline is near

HERE = os.path.dirname(os.path.abspath(__file__))
WWWROOT = os.path.join(HERE, "..", "src", "Dare.Arenas", "Web", "wwwroot")
LAYOUT = os.path.join(WWWROOT, "tile-layout.json")


# ---- masks ----------------------------------------------------------------------------------

def canon(mask: int) -> int:
    """The mask with diagonals that can't matter dropped (a diagonal counts only when both
    sides next to it are open)."""
    m = mask & (N | E | S | W)
    for diag, a, b in ((NE, N, E), (SE, S, E), (SW, S, W), (NW, N, W)):
        if mask & diag and mask & a and mask & b:
            m |= diag
    return m


MASKS: list[int] = sorted({canon(m) for m in range(256)})  # the 47 pieces, atlas order
assert len(MASKS) == 47


def open_mask(open_sides: str) -> int:
    """A layout role's open set ("NES"...) as a mask. Roles say nothing about diagonals, so a
    piece for a role has its inner corners filled (box-style: a 3x3 block reads as one plate)."""
    m = 0
    for ch in open_sides:
        m |= SIDES[ch]
    return canon(m | NE | SE | SW | NW)


def mask_at(grid: dict[tuple[int, int], bool] | Callable[[int, int], bool], x: int, y: int) -> int:
    on = grid if callable(grid) else (lambda a, b: grid.get((a, b), False))
    m = 0
    for bit, dx, dy in ((N, 0, -1), (NE, 1, -1), (E, 1, 0), (SE, 1, 1), (S, 0, 1), (SW, -1, 1), (W, -1, 0), (NW, -1, -1)):
        if on(x + dx, y + dy):
            m |= bit
    return canon(m)


# ---- distance to the outline -----------------------------------------------------------------

def outline_candidates(mask: int, x: int, y: int) -> list[tuple[int, str]]:
    """Every outline near pixel (x, y) as (distance, facing). A pixel on the mitre of a corner
    is listed once per side it could belong to."""
    cands = []
    if not mask & N: cands.append((y, "N"))
    if not mask & S: cands.append((TS - 1 - y, "S"))
    if not mask & W: cands.append((x, "W"))
    if not mask & E: cands.append((TS - 1 - x, "E"))
    # inner corners: both sides open, diagonal closed. The outline turns round the corner pixel;
    # which way it faces follows the neighbouring tiles' straight outlines (a 45 degree mitre).
    rx, ry = TS - 1 - x, TS - 1 - y
    for diag, a, b, u, v, fu, fv in ((NW, N, W, y, x, "N", "W"), (NE, N, E, y, rx, "N", "E"),
                                     (SW, S, W, ry, x, "S", "W"), (SE, S, E, ry, rx, "S", "E")):
        if mask & a and mask & b and not mask & diag:
            if u >= v: cands.append((u, fu))
            if v >= u: cands.append((v, fv))
    return cands


def field_at(mask: int, x: int, y: int) -> tuple[int, str]:
    """(d, face) for pixel (x, y) of a piece; on a mitre the first listed side wins."""
    best = (FAR, "N")
    for c in outline_candidates(mask, x, y):
        if c[0] < best[0]:
            best = c
    return best


# ---- styles -----------------------------------------------------------------------------------

RGB = tuple[int, int, int]


def _mix(a: RGB, b: RGB, t: float) -> RGB:
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))  # type: ignore[return-value]


def _add(c: RGB, k: float) -> RGB:
    return tuple(max(10, min(255, int(round(v + k)))) for v in c)  # type: ignore[return-value]


def _torus_noise(seed: int, amp: float, smooth: int = 1, stretch: int = 1) -> list[list[float]]:
    """16 x 16 noise that wraps at the tile edges (so it tiles), values about -amp..amp.
    stretch > 1 smears it horizontally (brushed metal)."""
    rnd = random.Random(seed)
    g = [[rnd.uniform(-1, 1) for _ in range(TS)] for _ in range(TS)]
    for _ in range(smooth):
        g = [[(g[y][x] * 2 + g[y][(x + 1) % TS] * stretch + g[y][(x - 1) % TS] * stretch
               + g[(y + 1) % TS][x] + g[(y - 1) % TS][x]) / (4 + 2 * stretch)
              for x in range(TS)] for y in range(TS)]
    peak = max(abs(v) for row in g for v in row) or 1
    return [[v / peak * amp for v in row] for row in g]


@dataclass
class Style:
    id: str
    name: str
    kind: str  # plate | lit | stone | hazard | crystal
    base: RGB
    hi: RGB  # lit outline
    lo: RGB  # shadowed outline
    accent: RGB = (0, 0, 0)
    seed: int = 1
    noise: float = 8.0
    _n: list = field(default=None, repr=False)  # type: ignore[assignment]

    def noise_at(self, x: int, y: int) -> float:
        if self._n is None:
            self._n = _torus_noise(self.seed, self.noise, smooth=2, stretch=3 if self.kind == "plate" else 1)
        return self._n[y % TS][x % TS]

    def shade(self, d: int, face: str, x: int, y: int) -> RGB:
        return SHADERS[self.kind](self, d, face, x % TS, y % TS)


def _bevel(s: Style, d: int, face: str) -> RGB | None:
    lit = face in ("N", "W")
    if d == 0:
        return s.hi if lit else s.lo
    if d == 1:
        return _mix(s.base, s.hi if lit else s.lo, 0.5)
    return None


def _plate(s: Style, d: int, face: str, x: int, y: int) -> RGB:
    b = _bevel(s, d, face)
    if b: return b
    c = _add(s.base, s.noise_at(x, y))
    if d == 2: return _add(c, -28)  # groove inside the rim
    if d == 3: return _add(c, 14)
    # panel seams where tiles meet inside a block, rivets in the panel corners
    if d >= 4:
        if x == 0 or y == 0: return _add(c, -30)
        if x == 1 or y == 1: return _add(c, 16)
        for rx, ry in ((4, 4), (12, 4), (4, 12), (12, 12)):
            if d >= 5 and (x, y) == (rx, ry): return _add(s.hi, -10)
            if d >= 5 and (x, y) == (rx + 1, ry + 1): return _add(c, -34)
            if d >= 5 and (x, y) in ((rx + 1, ry), (rx, ry + 1)): return _add(c, 18)
    return c


def _lit(s: Style, d: int, face: str, x: int, y: int) -> RGB:
    b = _bevel(s, d, face)
    if b: return b
    c = _add(s.base, s.noise_at(x, y))
    if d == 2: return _add(c, -14)
    if d == 3:  # the light strip: LEDs every other pixel along the outline
        return s.accent if (x + y) % 2 == 0 else _mix(s.accent, s.base, 0.45)
    if d == 4: return _mix(c, s.accent, 0.22)
    if d >= 5:  # dark inner panel with a faint grid that lines up across tiles
        if x in (7, 8) or y in (7, 8): return _mix(c, s.accent, 0.12)
        return _add(c, -8)
    return c


def _stone(s: Style, d: int, face: str, x: int, y: int) -> RGB:
    lit = face in ("N", "W")
    if d == 0: return s.hi if lit else s.lo
    c = _add(s.base, s.noise_at(x, y))
    if d == 1: return _add(c, 20 if lit else -22)
    course = y // 8
    if y % 8 == 7 or (x + (8 if course % 2 else 0)) % TS == 15:
        return _mix(c, s.lo, 0.55)  # mortar
    if y % 8 == 0 or (x + (8 if course % 2 else 0)) % TS == 0:
        return _add(c, 12)
    return c


def _hazard(s: Style, d: int, face: str, x: int, y: int) -> RGB:
    b = _bevel(s, d, face)
    if b: return b
    c = _add(s.base, s.noise_at(x, y))
    if 2 <= d <= 4:  # warning stripes round the outline
        return s.accent if ((x + y) // 3) % 2 == 0 else _add(s.lo, 8)
    if d == 5: return _add(c, -24)
    if (x * 7 + y * 3) % 23 == 0: return _add(c, -30)  # rust pits
    return c


def _crystal(s: Style, d: int, face: str, x: int, y: int) -> RGB:
    b = _bevel(s, d, face)
    if b: return b
    c = _add(s.base, s.noise_at(x, y))
    # facets: diagonal bands that line up across tiles
    if (x + y) % 16 == 0 or (x - y) % 16 == 8: return _mix(c, s.hi, 0.6)
    if (x + y) % 16 == 1: return _add(c, -18)
    if d == 2: return _mix(c, s.accent, 0.35)
    return c


SHADERS: dict[str, Callable[[Style, int, str, int, int], RGB]] = {
    "plate": _plate, "lit": _lit, "stone": _stone, "hazard": _hazard, "crystal": _crystal,
}

STYLES: list[Style] = [
    Style("steel", "Steel plate", "plate", (118, 122, 130), (214, 218, 226), (44, 46, 54), seed=11),
    Style("bronze", "Bronze plate", "plate", (128, 98, 62), (226, 192, 128), (52, 36, 22), seed=12),
    Style("green-lit", "Green lit", "lit", (46, 54, 66), (130, 140, 156), (18, 22, 28), (96, 255, 120), seed=13, noise=5),
    Style("cyan-lit", "Cyan lit", "lit", (40, 50, 70), (124, 140, 168), (16, 20, 30), (90, 230, 255), seed=14, noise=5),
    Style("white-lit", "White lit", "lit", (58, 60, 68), (150, 154, 164), (22, 22, 28), (238, 240, 250), seed=15, noise=5),
    Style("red-lit", "Red lit", "lit", (58, 40, 46), (156, 120, 128), (26, 14, 18), (255, 80, 70), seed=16, noise=5),
    Style("stone", "Stone", "stone", (112, 102, 90), (176, 166, 150), (40, 36, 32), seed=17, noise=14),
    Style("hazard", "Hazard", "hazard", (110, 70, 44), (200, 150, 110), (36, 26, 20), (232, 186, 40), seed=18, noise=12),
    Style("ice", "Ice crystal", "crystal", (84, 140, 196), (226, 244, 255), (26, 48, 86), (170, 230, 255), seed=19, noise=10),
    Style("navy", "Navy plate", "plate", (52, 62, 104), (150, 164, 214), (18, 22, 42), seed=20),
]
BY_ID = {s.id: s for s in STYLES}

# Themes: one style per pack of the standard layout, matched by pack id where a style fits.
THEMES: dict[str, dict[str, str]] = {
    "foundry": {"grey-plate": "steel", "grey-round": "bronze", "green-lit": "green-lit",
                "white-lit": "white-lit", "steel-blue": "hazard", "navy": "navy"},
    "station": {"grey-plate": "navy", "grey-round": "steel", "green-lit": "cyan-lit",
                "white-lit": "white-lit", "steel-blue": "ice", "navy": "red-lit"},
    "ruins": {"grey-plate": "stone", "grey-round": "bronze", "green-lit": "green-lit",
              "white-lit": "hazard", "steel-blue": "steel", "navy": "red-lit"},
}


# ---- drawing ------------------------------------------------------------------------------------

def piece(style: Style, mask: int) -> Image.Image:
    mask = canon(mask)
    im = Image.new("RGB", (TS, TS))
    px = im.load()
    for y in range(TS):
        for x in range(TS):
            d, face = field_at(mask, x, y)
            px[x, y] = _no_black(style.shade(d, face, x, y))
    return im


def _no_black(c: RGB) -> RGB:
    return c if sum(c) >= 24 else (10, 10, 12)


def atlas(style: Style) -> Image.Image:
    """The 47 pieces in a row, in MASKS order."""
    im = Image.new("RGB", (TS * len(MASKS), TS))
    for i, m in enumerate(MASKS):
        im.paste(piece(style, m), (i * TS, 0))
    return im


def load_layout(path: str = LAYOUT) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _slot_xy(tile: int) -> tuple[int, int]:
    i = tile - 1
    return (i % COLS) * TS, (i // COLS) * TS


def _special(tile: int, theme: list[Style]) -> Image.Image | None:
    """Pictures for the reserved slots: doors, safe zone, goal, fly-over / fly-under."""
    s = theme[0]
    im = Image.new("RGB", (TS, TS))
    px = im.load()
    for y in range(TS):
        for x in range(TS):
            if 162 <= tile <= 169:  # doors: bars across the gap (vertical 162-165, horizontal 166-169)
                along = y if tile <= 165 else x
                across = x if tile <= 165 else y
                frame = (tile - 162) % 4
                bar = across in (6, 7, 8, 9) and (along + frame * 2) % 8 < 6
                px[x, y] = _add(s.hi, -20) if bar else (0, 0, 0)
            elif tile == 171:
                px[x, y] = (24, 72, 40) if (x + y) % 4 else (40, 120, 64)
            elif tile == 172:
                px[x, y] = (70, 50, 120) if (x // 4 + y // 4) % 2 else (100, 76, 170)
            elif 173 <= tile <= 190:  # fly-over / fly-under: faint grating
                px[x, y] = _add(s.base, -40) if x % 4 == 0 or y % 4 == 0 else (0, 0, 0)
            else:
                return None
    return im


def reserved_strip(style: Style) -> Image.Image:
    """Slots 162-190 (doors, safe zone, goal, fly-over / fly-under) in a row, for one style."""
    im = Image.new("RGB", (TS * 29, TS))
    for k, tile in enumerate(range(162, 191)):
        sp = _special(tile, [style])
        if sp:
            im.paste(sp, (k * TS, 0))
    return im


def tileset(theme: str | dict[str, str], layout: dict | None = None) -> Image.Image:
    """A full 19 x 10 tileset in the standard layout: each pack drawn in the theme's style for
    it; other wall slots get stand-alone blocks so nothing a map places there shows as empty."""
    layout = layout or load_layout()
    packs = THEMES[theme] if isinstance(theme, str) else theme
    sheet = Image.new("RGB", (COLS * TS, ROWS * TS))
    order = [BY_ID[packs[p["id"]]] if p["id"] in packs else STYLES[i % len(STYLES)]
             for i, p in enumerate(layout["packs"])]
    roles = {name: r["open"] for name, r in layout["roles"].items()}
    used: set[int] = set()
    for style, pack in zip(order, layout["packs"]):
        for role, tile in pack["tiles"].items():
            sheet.paste(piece(style, open_mask(roles[role])), _slot_xy(tile))
            used.add(tile)
    for tile in range(1, 162):
        if tile not in used:
            sheet.paste(piece(order[tile % len(order)], 0), _slot_xy(tile))
    for tile in range(162, 191):
        sp = _special(tile, order)
        if sp:
            sheet.paste(sp, _slot_xy(tile))
    return sheet


def bmp24(im: Image.Image) -> bytes:
    """A plain 24-bit bottom-up BMP (what Continuum and lvl.js read)."""
    im = im.convert("RGB")
    w, h = im.size
    row = (w * 3 + 3) & ~3
    data = bytearray()
    for y in range(h - 1, -1, -1):
        line = bytearray()
        for x in range(w):
            r, g, b = im.getpixel((x, y))
            line += bytes((b, g, r))
        data += line + bytes(row - len(line))
    off = 54
    return (b"BM" + struct.pack("<IHHI", off + len(data), 0, 0, off)
            + struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(data), 2835, 2835, 0, 0) + bytes(data))


def _png_data_url(im: Image.Image) -> str:
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()


def styles_js() -> str:
    """tileart-styles.js: every style's 47-piece atlas for the map editor."""
    data = {
        "masks": MASKS,
        "styles": [{"id": s.id, "name": s.name, "atlas": _png_data_url(atlas(s)),
                    "reserved": _png_data_url(reserved_strip(s))} for s in STYLES],
        "themes": THEMES,
    }
    return ("// Generated by ships/tilegen.py: do not edit. Each style's atlas holds 47 wall pieces of 16 px,\n"
            "// one per neighbour mask in `masks` (bits N=1 NE=2 E=4 SE=8 S=16 SW=32 W=64 NW=128); `reserved`\n"
            "// holds slots 162-190 (doors, safe zone, goal, fly-over/under) in a row.\n"
            "\"use strict\";\nwindow.TileArtStyles = " + json.dumps(data, separators=(",", ":")) + ";\n")


def main() -> None:
    out = os.path.join(HERE, "tilesets")
    os.makedirs(out, exist_ok=True)
    layout = load_layout()
    for name in THEMES:
        sheet = tileset(name, layout)
        with open(os.path.join(out, f"{name}.bmp"), "wb") as f:
            f.write(bmp24(sheet))
        sheet.resize((COLS * TS * 3, ROWS * TS * 3), Image.NEAREST).save(os.path.join(out, f"{name}-preview.png"))
    strip = Image.new("RGB", (TS * len(MASKS), TS * len(STYLES)))
    for i, s in enumerate(STYLES):
        strip.paste(atlas(s), (0, i * TS))
    strip.resize((strip.width * 2, strip.height * 2), Image.NEAREST).save(os.path.join(out, "styles-preview.png"))
    with open(os.path.join(WWWROOT, "tileart-styles.js"), "w", encoding="utf-8") as f:
        f.write(styles_js())
    print(f"wrote {len(THEMES)} tilesets to {out} and tileart-styles.js")


if __name__ == "__main__":
    main()
