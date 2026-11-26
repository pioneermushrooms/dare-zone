"""Run: python3 -m unittest test_tilegen (needs Pillow). Checks that wall pieces join seamlessly
and that tilesets follow the standard tile layout."""

import io
import json
import random
import unittest

from PIL import Image

import tilegen as tg


def random_map(seed: int, w: int = 24, h: int = 20, fill: float = 0.45) -> dict:
    rnd = random.Random(seed)
    return {(x, y): True for y in range(h) for x in range(w) if rnd.random() < fill}


def compose(style, grid, w, h):
    im = Image.new("RGB", (w * tg.TS, h * tg.TS))
    for (x, y) in grid:
        im.paste(tg.piece(style, tg.mask_at(grid, x, y)), (x * tg.TS, y * tg.TS))
    return im


class Masks(unittest.TestCase):
    def test_47_pieces(self):
        self.assertEqual(len(tg.MASKS), 47)
        for m in range(256):
            self.assertEqual(tg.canon(tg.canon(m)), tg.canon(m))

    def test_roles_cover_the_16_edge_sets(self):
        layout = tg.load_layout()
        sets = {tg.open_mask(r["open"]) for r in layout["roles"].values()}
        self.assertEqual(len(sets), 16)
        self.assertTrue(sets <= set(tg.MASKS))


class Seams(unittest.TestCase):
    def test_distance_matches_a_whole_map_distance_transform(self):
        """d of every pixel, read piece by piece, equals the Chebyshev distance to the nearest
        empty pixel of the whole map (less one), wherever that is within the bevel: so pieces
        join exactly as one big picture would."""
        w, h = 14, 12
        for seed in range(6):
            grid = random_map(seed, w, h)
            empty = [(px, py) for py in range(-1, h * 16 + 1) for px in range(-1, w * 16 + 1)
                     if not grid.get((px // 16 if px >= 0 else -1, py // 16 if py >= 0 else -1), False)]
            emptyset = set(empty)
            for (cx, cy) in grid:
                mask = tg.mask_at(grid, cx, cy)
                for y in range(16):
                    for x in range(16):
                        d, _ = tg.field_at(mask, x, y)
                        gx, gy = cx * 16 + x, cy * 16 + y
                        # brute force within 6 px
                        best = tg.FAR
                        for r in range(1, 7):
                            ring = any((gx + dx, gy + dy) in emptyset
                                       for dx in range(-r, r + 1) for dy in range(-r, r + 1)
                                       if max(abs(dx), abs(dy)) == r)
                            if ring:
                                best = r - 1
                                break
                        if best < 6 or d < 6:
                            self.assertEqual(d, best, (seed, cx, cy, x, y, mask))

    def test_outline_faces_carry_across_seams(self):
        grid = random_map(42)
        for (cx, cy) in grid:
            for dx, dy in ((1, 0), (0, 1)):
                if not grid.get((cx + dx, cy + dy)):
                    continue
                a, b = tg.mask_at(grid, cx, cy), tg.mask_at(grid, cx + dx, cy + dy)
                for k in range(16):
                    pa = (15, k) if dx else (k, 15)
                    pb = (0, k) if dx else (k, 0)
                    da, fa = tg.field_at(a, *pa)
                    db, fb = tg.field_at(b, *pb)
                    da, db = min(da, 8), min(db, 8)
                    self.assertLessEqual(abs(da - db), 1)
                    mitre = any(sum(1 for c in tg.outline_candidates(m, *pp) if c[0] == dd) > 1
                                for m, pp, dd in ((a, pa, da), (b, pb, db)))
                    if da == db and da < 4 and not mitre:
                        self.assertEqual(fa, fb, (cx, cy, dx, dy, k))

    def test_composed_map_matches_seamless_render(self):
        """Every style: a map built from pieces equals drawing each pixel from the whole map's
        field, so there is no visible seam anywhere."""
        grid = random_map(7, 10, 8)
        for s in tg.STYLES:
            im = compose(s, grid, 10, 8)
            px = im.load()
            for (cx, cy) in grid:
                mask = tg.mask_at(grid, cx, cy)
                for y in range(0, 16, 3):
                    for x in range(0, 16, 3):
                        d, f = tg.field_at(mask, x, y)
                        want = tg._no_black(s.shade(d, f, cx * 16 + x, cy * 16 + y))
                        self.assertEqual(px[cx * 16 + x, cy * 16 + y], want)


class Pieces(unittest.TestCase):
    def test_no_see_through_pixels(self):
        for s in tg.STYLES:
            a = tg.atlas(s)
            self.assertEqual(a.size, (16 * 47, 16))
            self.assertTrue(all(sum(p) >= 24 for p in a.getdata()), s.id)

    def test_styles_look_different(self):
        means = {s.id: tuple(sum(c) // 256 for c in zip(*tg.piece(s, 0).getdata())) for s in tg.STYLES}
        self.assertEqual(len(set(means.values())), len(tg.STYLES))


class Tilesets(unittest.TestCase):
    def test_follows_the_layout(self):
        layout = tg.load_layout()
        for theme in tg.THEMES:
            sheet = tg.tileset(theme, layout)
            self.assertEqual(sheet.size, (304, 160))
            for pack in layout["packs"]:
                style = tg.BY_ID[tg.THEMES[theme][pack["id"]]]
                for role, tile in pack["tiles"].items():
                    x, y = tg._slot_xy(tile)
                    got = sheet.crop((x, y, x + 16, y + 16))
                    want = tg.piece(style, tg.open_mask(layout["roles"][role]["open"]))
                    self.assertEqual(list(got.getdata()), list(want.getdata()), (theme, pack["id"], role))
            for tile in range(1, 162):  # every wall slot is drawn
                x, y = tg._slot_xy(tile)
                self.assertTrue(all(sum(p) >= 24 for p in sheet.crop((x, y, x + 16, y + 16)).getdata()), tile)

    def test_swapped_layout_moves_the_pieces(self):
        """A replacement layout (other slots, other role names) is followed, not the built-in one."""
        layout = tg.load_layout()
        mine = json.loads(json.dumps(layout))
        mine["roles"] = {"r" + str(i): v for i, v in enumerate(layout["roles"].values())}
        names = dict(zip(layout["roles"], mine["roles"]))
        mine["packs"] = [{"id": "grey-plate", "tiles": {names[r]: t + 100 for r, t in layout["packs"][0]["tiles"].items() if t + 100 <= 161}}]
        sheet = tg.tileset({"grey-plate": "stone"}, mine)
        for role, tile in mine["packs"][0]["tiles"].items():
            x, y = tg._slot_xy(tile)
            want = tg.piece(tg.BY_ID["stone"], tg.open_mask(mine["roles"][role]["open"]))
            self.assertEqual(list(sheet.crop((x, y, x + 16, y + 16)).getdata()), list(want.getdata()))

    def test_bmp_reads_back(self):
        sheet = tg.tileset("foundry")
        data = tg.bmp24(sheet)
        back = Image.open(io.BytesIO(data)).convert("RGB")
        self.assertEqual(list(back.getdata()), list(sheet.getdata()))
        self.assertEqual(int.from_bytes(data[2:6], "little"), len(data))

    def test_styles_js(self):
        js = tg.styles_js()
        data = json.loads(js[js.index("= ") + 2:].rstrip().rstrip(";"))
        self.assertEqual(data["masks"], tg.MASKS)
        self.assertEqual(len(data["styles"]), len(tg.STYLES))
        self.assertTrue(all(st["reserved"].startswith("data:image/png;base64,") for st in data["styles"]))
        for theme in data["themes"].values():
            self.assertTrue(set(theme.values()) <= set(tg.BY_ID))


if __name__ == "__main__":
    unittest.main()
