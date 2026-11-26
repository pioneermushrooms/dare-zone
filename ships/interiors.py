"""Plain interior pieces for the standard tileset's packs (tactical2's art).

The standard packs are box-style: their fill and edge pieces each carry a stud or pyramid, so
a wall three or more cells thick tiles as a waffle of little panels. An interior piece is the
pack's own field material with no rim and no motif, so the inside of a thick wall reads as one
solid surface; the auto-tiler uses it where all eight neighbours are wall, with the pack's edge
and corner pieces as the rim.

Each piece uses only colours already in tactical2's palette (the tileset is 8-bit) and repeats
every 16 px, so it joins itself on all four sides. Its main colour is the field colour the
pack's edge pieces show on their inner side.

Run: python3 interiors.py <tactical2.lvl or tileset .bmp>  -> tilesets/tactical2-interior.bmp
(tactical2's tileset with the six pieces in INTERIOR_SLOTS), the same file as the site's
wwwroot/standard-tiles.bmp, and a preview of thick walls.
"""

from __future__ import annotations

import io
import os
import struct
import sys

from PIL import Image

import tilegen as tg

# pack id -> free (black, unused) wall slot in the standard tileset, row 6 columns 13-18
INTERIOR_SLOTS = {"grey-plate": 128, "grey-round": 129, "green-lit": 130,
                  "white-lit": 131, "steel-blue": 132, "navy": 133}

RGB = tuple[int, int, int]

# Field colours, read from the packs (all in tactical2's palette).
SPECS: dict[str, dict] = {
    # the plate's field is see-through black, so its solid inside is the rim's dark marble
    "grey-plate": {"kind": "mottle", "colours": [(51, 50, 49), (57, 58, 59), (66, 65, 65), (75, 75, 74)],
                   "weights": [0.30, 0.34, 0.26, 0.10], "seed": 3},
    "grey-round": {"kind": "mottle", "colours": [(51, 50, 49), (57, 58, 59), (66, 65, 65)],
                   "weights": [0.62, 0.26, 0.12], "seed": 4},
    "green-lit": {"kind": "flat", "base": (35, 35, 46), "dot": (43, 43, 62)},
    "white-lit": {"kind": "flat", "base": (42, 42, 44), "dot": (35, 34, 34)},
    # dotted rows as in the pack's field: every fourth row has dots every other pixel
    "steel-blue": {"kind": "rows", "base": (72, 72, 92), "dot": (82, 82, 118)},
    "navy": {"kind": "flat", "base": (39, 39, 73), "dot": (51, 51, 74)},
}


def interior(pack: str) -> Image.Image:
    s = SPECS[pack]
    im = Image.new("RGB", (tg.TS, tg.TS))
    px = im.load()
    if s["kind"] == "mottle":
        noise = tg._torus_noise(s["seed"], 1.0, smooth=1)
        flat = sorted(v for row in noise for v in row)
        cuts, acc = [], 0.0
        for w in s["weights"][:-1]:
            acc += w
            cuts.append(flat[min(len(flat) - 1, int(acc * len(flat)))])
        for y in range(tg.TS):
            for x in range(tg.TS):
                k = sum(1 for c in cuts if noise[y][x] > c)
                px[x, y] = s["colours"][k]
    else:
        for y in range(tg.TS):
            for x in range(tg.TS):
                c = s["base"]
                if s["kind"] == "flat" and (x % 8, y % 8) in ((2, 2), (6, 6)):
                    c = s["dot"]  # a faint speck so a big wall isn't dead flat
                if s["kind"] == "rows" and y % 4 == 1:
                    c = s["dot"] if x % 2 == 0 else s["base"]
                px[x, y] = c
    return im


# ---- tileset ------------------------------------------------------------------------------------

def read_tileset(path: str) -> bytes:
    """The BMP from a .lvl (or a .bmp file as is)."""
    b = open(path, "rb").read()
    if b[:2] != b"BM":
        raise ValueError(f"{path} has no tileset")
    return b[:struct.unpack_from("<I", b, 2)[0]]


def with_interiors(bmp: bytes, layout: dict | None = None) -> bytes:
    """tactical2's 8-bit tileset with the interiors drawn into INTERIOR_SLOTS, palette and every
    other byte unchanged. Refuses if a target slot isn't empty or a colour isn't in the palette."""
    layout = layout or tg.load_layout()
    packs = {p["id"] for p in layout["packs"]}
    w, h, bpp, comp = struct.unpack_from("<iiHHI", bmp, 18)[0], abs(struct.unpack_from("<i", bmp, 22)[0]), \
        struct.unpack_from("<H", bmp, 28)[0], struct.unpack_from("<I", bmp, 30)[0]
    if bpp != 8 or comp != 0 or (w, h) != (304, 160):
        raise ValueError(f"expected a plain 8-bit 304x160 tileset, got {bpp}-bit {w}x{h} compression {comp}")
    off, hdr = struct.unpack_from("<I", bmp, 10)[0], struct.unpack_from("<I", bmp, 14)[0]
    ncol = struct.unpack_from("<I", bmp, 46)[0] or 256
    pal = {}
    for i in range(ncol):
        b_, g_, r_ = bmp[14 + hdr + i * 4: 14 + hdr + i * 4 + 3]
        pal.setdefault((r_, g_, b_), i)
    out = bytearray(bmp)
    row = (w + 3) & ~3
    bottom_up = struct.unpack_from("<i", bmp, 22)[0] > 0
    for pack, tile in INTERIOR_SLOTS.items():
        if pack not in packs:
            raise ValueError(f"the layout has no pack {pack}")
        x0, y0 = tg._slot_xy(tile)
        piece = interior(pack)
        for y in range(tg.TS):
            yy = (h - 1 - (y0 + y)) if bottom_up else y0 + y
            for x in range(tg.TS):
                at = off + yy * row + x0 + x
                c = piece.getpixel((x, y))
                if c not in pal:
                    raise ValueError(f"{pack}: colour {c} is not in the tileset's palette")
                out[at] = pal[c]
    return bytes(out)


def slot_is_empty(bmp: bytes, tile: int) -> bool:
    im = Image.open(io.BytesIO(bmp)).convert("RGB")
    x, y = tg._slot_xy(tile)
    return max(sum(p) for p in im.crop((x, y, x + 16, y + 16)).getdata()) < 24


def preview(bmp: bytes, layout: dict | None = None) -> Image.Image:
    """Each pack as a 6x5 solid wall: the pack's rim and corners round interior pieces."""
    layout = layout or tg.load_layout()
    sheet = Image.open(io.BytesIO(bmp)).convert("RGB")
    tile = lambda t: sheet.crop(tg._slot_xy(t) + (tg._slot_xy(t)[0] + 16, tg._slot_xy(t)[1] + 16))
    W, H = 6, 5
    out = Image.new("RGB", (len(layout["packs"]) * (W + 1) * 16, H * 16))
    roles = {frozenset(r["open"]): name for name, r in layout["roles"].items()}
    for i, p in enumerate(layout["packs"]):
        for y in range(H):
            for x in range(W):
                open_ = {d for d, ok in (("N", y > 0), ("S", y < H - 1), ("W", x > 0), ("E", x < W - 1)) if ok}
                t = INTERIOR_SLOTS[p["id"]] if 0 < x < W - 1 and 0 < y < H - 1 else p["tiles"][roles[frozenset(open_)]]
                out.paste(tile(t), ((i * (W + 1) + x) * 16, y * 16))
    return out


def main() -> None:
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/dare-zone/.local-zone/zone/maps/tactical2.lvl")
    bmp = read_tileset(src)
    for t in INTERIOR_SLOTS.values():
        if not slot_is_empty(bmp, t):
            raise SystemExit(f"slot {t} is not empty in {src}")
    new = with_interiors(bmp)
    out = os.path.join(tg.HERE, "tilesets")
    # the site serves the same file as /arena/standard-tiles.bmp ("Use the standard tiles")
    for path in (os.path.join(out, "tactical2-interior.bmp"), os.path.join(tg.WWWROOT, "standard-tiles.bmp")):
        with open(path, "wb") as f:
            f.write(new)
    pv = preview(new)
    pv.resize((pv.width * 2, pv.height * 2), Image.NEAREST).save(os.path.join(out, "tactical2-interior-preview.png"))
    print("interiors:", INTERIOR_SLOTS)


if __name__ == "__main__":
    main()
