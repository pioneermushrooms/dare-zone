"""Run: python3 -m unittest test_interiors (needs Pillow)."""

import io
import os
import struct
import unittest

from PIL import Image

import interiors as it
import tilegen as tg

ALL = [*it.INTERIOR_SLOTS.values(), *(t for c in it.CALM_SLOTS.values() for t in c.values())]
DONE = os.path.join(os.path.dirname(__file__), "tilesets", "tactical2-interior.bmp")


def cleared(bmp: bytes) -> bytes:
    """The committed tileset with the interior slots blacked out again: tactical2's original."""
    off, hdr = struct.unpack_from("<I", bmp, 10)[0], struct.unpack_from("<I", bmp, 14)[0]
    black = next(i for i in range(256) if bmp[14 + hdr + i * 4: 14 + hdr + i * 4 + 3] == b"\0\0\0")
    out = bytearray(bmp)
    for t in ALL:
        x0, y0 = tg._slot_xy(t)
        for y in range(16):
            for x in range(16):
                out[off + (159 - (y0 + y)) * 304 + x0 + x] = black
    return bytes(out)


class Interiors(unittest.TestCase):
    def setUp(self):
        self.done = open(DONE, "rb").read()
        self.orig = cleared(self.done)

    def test_slots_are_free_wall_slots(self):
        layout = tg.load_layout()
        taken = {t for p in layout["packs"] for t in p["tiles"].values()}
        for o in layout.get("other", []):
            taken |= set(o.get("tiles") or [o["tile"]])
        self.assertEqual(set(it.INTERIOR_SLOTS), {p["id"] for p in layout["packs"]})
        self.assertEqual(len(ALL), len(set(ALL)))
        for t in ALL:
            self.assertTrue(1 <= t <= 161 and t not in taken, t)
            self.assertTrue(it.slot_is_empty(self.orig, t))

    def test_rebuilds_the_committed_tileset_and_touches_nothing_else(self):
        new = it.with_interiors(self.orig)
        self.assertEqual(new, self.done)
        a = Image.open(io.BytesIO(self.orig)).convert("RGB")
        b = Image.open(io.BytesIO(new)).convert("RGB")
        slots = {tg._slot_xy(t) for t in ALL}
        for y in range(160):
            for x in range(304):
                if (x // 16 * 16, y // 16 * 16) not in slots:
                    self.assertEqual(a.getpixel((x, y)), b.getpixel((x, y)))

    def test_plain_solid_and_seamless_with_itself(self):
        for pack in it.INTERIOR_SLOTS:
            im = it.interior(pack)
            px = list(im.getdata())
            self.assertTrue(all(sum(p) >= 24 for p in px), pack)  # no see-through pixels
            # no motif: few colours, one of them most of the tile
            self.assertLessEqual(len(set(px)), 4, pack)
            # seamless: the difference across the wrap edge is no bigger than inside the tile
            d = lambda p, q: sum(abs(a - b) for a, b in zip(p, q))
            inner = [d(im.getpixel((x, y)), im.getpixel((x + 1, y))) for y in range(16) for x in range(15)]
            inner += [d(im.getpixel((x, y)), im.getpixel((x, y + 1))) for y in range(15) for x in range(16)]
            wrap = [d(im.getpixel((15, y)), im.getpixel((0, y))) for y in range(16)]
            wrap += [d(im.getpixel((x, 15)), im.getpixel((x, 0))) for x in range(16)]
            self.assertLessEqual(sum(wrap) / len(wrap), max(inner) , pack)
            self.assertLessEqual(sum(wrap) / len(wrap), sum(inner) / len(inner) * 2 + 1, pack)

    def test_calm_edges_keep_the_rim_and_are_interior_inside(self):
        layout = tg.load_layout()
        sheet = Image.open(io.BytesIO(self.done)).convert("RGB")
        tile = lambda t: sheet.crop(tg._slot_xy(t) + (tg._slot_xy(t)[0] + 16, tg._slot_xy(t)[1] + 16))
        roles = {r["open"]: n for n, r in layout["roles"].items()}
        packs = {p["id"]: p for p in layout["packs"]}
        for pack, calm in it.CALM_SLOTS.items():
            inside = it.interior(pack)
            for open_, t in calm.items():
                piece, edge = tile(t), tile(packs[pack]["tiles"][roles[open_]])
                closed = it.CLOSED[open_]
                depth = it.CALM_DEPTH[pack] or it.rim_depth(edge, tile(packs[pack]["tiles"][roles["NESW"]]), closed)
                self.assertTrue(4 <= depth <= 12, (pack, open_, depth))
                for y in range(16):
                    for x in range(16):
                        k = {"N": y, "S": 15 - y, "W": x, "E": 15 - x}[closed]
                        want = edge.getpixel((x, y)) if k < depth else inside.getpixel((x, y))
                        self.assertEqual(piece.getpixel((x, y)), want, (pack, open_, x, y))
                # the open side opposite the rim runs straight into an interior piece below/beside it
                opp = {"N": [(x, 15) for x in range(16)], "S": [(x, 0) for x in range(16)],
                       "W": [(15, y) for y in range(16)], "E": [(0, y) for y in range(16)]}[closed]
                for p in opp:
                    self.assertEqual(piece.getpixel(p), inside.getpixel(p))

    def test_layout_lists_the_calm_edges(self):
        layout = tg.load_layout()
        for p in layout["packs"]:
            self.assertEqual(p.get("calm"), it.CALM_SLOTS.get(p["id"]), p["id"])

    def test_site_serves_the_same_tileset(self):
        site = os.path.join(tg.WWWROOT, "standard-tiles.bmp")
        self.assertEqual(open(site, "rb").read(), self.done)

    def test_refuses_a_colour_outside_the_palette(self):
        old = it.SPECS["navy"]
        it.SPECS["navy"] = {"kind": "flat", "base": (1, 2, 250), "dot": (1, 2, 250)}
        try:
            with self.assertRaises(ValueError):
                it.with_interiors(self.orig)
        finally:
            it.SPECS["navy"] = old


if __name__ == "__main__":
    unittest.main()
