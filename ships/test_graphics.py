"""Run: python3 -m unittest test_graphics (needs Pillow). Uses the stock sheets the arena editor ships."""

import base64
import io
import os
import unittest
from unittest import mock

from PIL import Image

import graphics
import service

GFX = os.path.join(os.path.dirname(__file__), "..", "src", "Dare.Arenas", "Web", "gfx")


def stock(name: str) -> bytes:
    with open(os.path.join(GFX, name + ".bm2"), "rb") as f:
        return f.read()


def size(png: bytes) -> tuple[int, int]:
    with Image.open(io.BytesIO(png)) as im:
        return im.size


class Generators(unittest.TestCase):
    def test_every_mode_keeps_the_sheet_size(self):
        for kind in list(graphics.SPRITES) + list(graphics.BURSTS) + ["gradient"]:
            for mode in ["recolor", "new"]:
                if mode == "new" and not (graphics.SHEETS.get(kind) and graphics.SHEETS[kind].body or kind in graphics.BURSTS):
                    continue
                with self.subTest(kind=kind, mode=mode):
                    png = graphics.generate(kind, mode, stock(kind), seed=3)
                    self.assertEqual(size(png), size(stock(kind)))

    def test_black_is_see_through_and_sprites_draw(self):
        with Image.open(io.BytesIO(graphics.generate("bombs", "new", stock("bombs"), seed=5))) as im:
            im = im.convert("RGBA")
            first = im.crop((0, 0, 16, 16))
            self.assertIsNotNone(first.getchannel("A").getbbox(), "frame 0 of level 1 draws")
            for r, g, b, a in im.getdata():
                self.assertTrue(a == 0 or (r, g, b) != (0, 0, 0))

    def test_new_keeps_each_row_its_colour(self):
        tpl = graphics.load_template(stock("bombs"))
        out = graphics.load_template(graphics.generate("bombs", "new", stock("bombs"), seed=9))
        for row in range(3):
            a = graphics._hue_of(tpl, (0, row * 16, 160, row * 16 + 16))
            b = graphics._hue_of(out, (0, row * 16, 160, row * 16 + 16))
            if a is not None and b is not None:
                self.assertLess(min(abs(a - b), 1 - abs(a - b)), 0.08)

    def test_prompt_animates_the_drawing(self):
        sprite = Image.new("RGBA", (40, 40), (0, 0, 0, 0))
        sprite.paste((220, 40, 40, 255), (10, 4, 30, 36))
        with mock.patch.object(graphics, "pixel_lab_sprite", return_value=sprite):
            png = graphics.generate("prizes", "prompt", stock("prizes"), prompt="a red gem", seed=1)
        self.assertEqual(size(png), (160, 16))

    def test_a_weapon_prompt_is_briefed_as_that_weapon(self):
        sprite = Image.new("RGBA", (40, 40), (0, 0, 0, 0))
        sprite.paste((40, 220, 40, 255), (12, 12, 28, 28))
        for kind, word in (("bombs", "bomb"), ("mines", "mine"), ("bullets", "bullet"), ("shrapnel", "shrapnel")):
            with mock.patch.object(graphics, "pixel_lab_sprite", return_value=sprite) as drawn:
                png = graphics.generate(kind, "prompt", stock(kind), prompt="green plasma", seed=2)
            description = drawn.call_args.args[0]
            self.assertTrue(description.startswith("green plasma"), description)
            self.assertIn(word, description)
            self.assertEqual(drawn.call_args.kwargs["px"], max(graphics.SPRITES[kind][:2]))
            self.assertEqual(size(png), size(stock(kind)), kind)  # the stock layout, so the game reads it
        self.assertEqual(graphics.sprite_description("gradient", "a red gem"), "a red gem")

    def test_service_refuses_clearly(self):
        with self.assertRaises(service.Refused):
            service.graphic({"kind": "bombs", "mode": "new", "template": ""})
        with self.assertRaises(service.Refused):
            service.graphic({"kind": "over2", "mode": "new", "template": base64.b64encode(stock("over2")).decode()})
        with mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": ""}), self.assertRaises(service.Refused) as r:
            service.graphic({"kind": "bombs", "mode": "prompt", "prompt": "x", "template": base64.b64encode(stock("bombs")).decode()})
        self.assertEqual(r.exception.status, 503)
        ok = service.graphic({"kind": "mines", "mode": "recolor", "template": base64.b64encode(stock("mines")).decode()})
        self.assertTrue(ok["ok"])



def sheet(kind: str, mode: str = "new", seed: int = 4, sprite: Image.Image | None = None) -> Image.Image:
    with mock.patch.object(graphics, "pixel_lab_sprite", return_value=sprite):
        return graphics.load_template(graphics.generate(kind, mode, stock(kind), prompt="x", seed=seed))


def cell(img: Image.Image, kind: str, row: int, col: int) -> Image.Image:
    cw, ch, _ = graphics.SPRITES[kind]
    return img.crop((col * cw, row * ch, col * cw + cw, row * ch + ch))


def frame(img: Image.Image, side: int, i: int) -> Image.Image:
    cols = img.width // side
    return img.crop((i % cols * side, i // cols * side, i % cols * side + side, i // cols * side + side))


def width(img: Image.Image) -> int:
    box = img.getchannel("A").getbbox()
    return 0 if box is None else box[2] - box[0]


def ink(img: Image.Image) -> int:
    return sum(1 for a in img.getchannel("A").getdata() if a)


def difference(a: Image.Image, b: Image.Image) -> float:
    pa, pb = list(a.getdata()), list(b.getdata())
    return sum(abs(x - y) for p, q in zip(pa, pb) for x, y in zip(p, q)) / len(pa)


def gap(a: float, b: float) -> float:
    d = abs(a - b) % 1.0
    return min(d, 1 - d)


def gem() -> Image.Image:
    """A stand-in for a PixelLab drawing: a green gem, deliberately not symmetric."""
    from PIL import ImageDraw
    img = Image.new("RGBA", (48, 48), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.polygon([(24, 2), (44, 24), (24, 46), (4, 24)], fill=(60, 200, 90, 255))
    d.ellipse((16, 16, 32, 32), fill=(230, 255, 230, 255))
    d.rectangle((22, 4, 26, 20), fill=(30, 120, 60, 255))
    return img


class SheetMeanings(unittest.TestCase):
    """Rows and frames keep the meaning the game client gives them (oss-client view.rs)."""

    def test_the_spec_matches_the_client_and_stock_sheets(self):
        def first_frame(kind, role, level=1):
            rows = graphics.SHEETS[kind].rows
            r = next(i for i, row in enumerate(rows) if row.role == role and row.level in (level, 0))
            return r * graphics.SHEETS[kind].frames
        self.assertEqual(first_frame("bullets", "bounce"), 20)  # lvl*4 + 20 bouncing
        self.assertEqual(first_frame("bullets", "burst"), 16)
        self.assertEqual(graphics.SHEETS["bullets"].rows[-1].role, "armed")  # the last row: n - 4
        self.assertEqual(first_frame("bombs", "emp"), 40)
        self.assertEqual(first_frame("bombs", "bounce"), 80)
        self.assertEqual(first_frame("bombs", "thor"), 120)
        self.assertEqual(first_frame("mines", "emp"), 40)
        self.assertEqual(first_frame("shrapnel", "bounce"), 30)
        self.assertEqual([r.role for r in graphics.SHEETS["powerb"].rows], ["ball", "phased", "trail"])
        for kind, spec in graphics.SHEETS.items():
            with self.subTest(kind=kind):
                with Image.open(io.BytesIO(stock(kind))) as im:
                    self.assertEqual(im.height // spec.cell[1], len(spec.rows), "a spec row per stock row")

    def test_each_row_keeps_one_colour_and_rows_stay_apart(self):
        for kind in ["bombs", "mines", "bullets", "shrapnel"]:
            for mode, sprite in [("new", None), ("prompt", gem())]:
                img = sheet(kind, mode, sprite=sprite)
                spec = graphics.SHEETS[kind]
                hues = {}
                for r, row in enumerate(spec.rows):
                    if row.role == "armed":
                        continue  # the burst going off cycles colours, as stock does
                    frames = [graphics._hue_of(img, cell(img, kind, r, c).getbbox() and
                                               (c * spec.cell[0], r * spec.cell[1], c * spec.cell[0] + spec.cell[0], r * spec.cell[1] + spec.cell[1]))
                              for c in range(spec.frames)]
                    frames = [h for h in frames if h is not None]
                    with self.subTest(kind=kind, mode=mode, row=r):
                        self.assertTrue(frames)
                        for h in frames:
                            self.assertLess(gap(h, frames[0]), 0.07, "one colour family across the row")
                    hues[(row.role, row.level)] = frames[0]
                levels = [hues[("regular", lv)] for lv in range(1, 4)]
                with self.subTest(kind=kind, mode=mode, check="levels"):
                    for i in range(len(levels)):
                        for j in range(i + 1, len(levels)):
                            self.assertGreater(gap(levels[i], levels[j]), 0.04, "weapon levels read apart")

    def test_emp_rows_crackle_and_bounce_rows_have_a_halo(self):
        for mode, sprite in [("new", None), ("prompt", gem())]:
            for kind, emp_row in [("bombs", 4), ("mines", 4)]:
                img = sheet(kind, mode, sprite=sprite)
                with self.subTest(kind=kind, mode=mode):
                    regular, emp = cell(img, kind, 0, 0), cell(img, kind, emp_row, 0)
                    self.assertGreater(difference(regular, emp), 20, "EMP looks unlike the regular shot")
                    # new sparks every frame: EMP frames differ from each other more than regular ones
                    flicker = difference(cell(img, kind, emp_row, 0), cell(img, kind, emp_row, 1))
                    self.assertGreater(flicker, 8)
            img = sheet("bombs", mode, sprite=sprite)
            with self.subTest(kind="bombs bounce", mode=mode):
                regular, bounce = cell(img, "bombs", 0, 0), cell(img, "bombs", 8, 0)
                self.assertGreater(ink(bounce), ink(regular) * 1.2, "the halo reaches past the shot")
                self.assertGreater(difference(regular, bounce), 20)
                thor = cell(img, "bombs", 12, 0)
                self.assertGreater(difference(thor, cell(img, "bombs", 3, 0)), 20, "thor is its own item")
        img = sheet("shrapnel")
        rim = graphics._hue_of(img, (0, 3 * 8, 80, 3 * 8 + 1))  # top edge of the first bounce row: its halo
        self.assertLess(gap(rim, 0.3), 0.08, "stock bouncing shrapnel's green carries to the halo")

    def test_tumbling_items_narrow_mid_turn_and_super_spins_flat(self):
        for kind, row in [("prizes", 0), ("mines", 0), ("flag", 0), ("shield", 0)]:
            for mode, sprite in [("new", None), ("prompt", gem())]:
                img = sheet(kind, mode, sprite=sprite)
                with self.subTest(kind=kind, mode=mode):
                    edge_on = min(width(cell(img, kind, row, c)) for c in (2, 3))
                    self.assertLess(edge_on, width(cell(img, kind, row, 0)) * 0.7)
        img = sheet("super")
        widths = [width(cell(img, "super", 0, c)) for c in range(10)]
        self.assertGreater(min(widths), max(widths) * 0.8, "super turns in the picture's plane")

    def test_repel_and_rocket_grow_and_the_ball_trail_fades(self):
        img = sheet("repel")
        widths = [width(frame(img, 96, i)) for i in range(10)]
        self.assertEqual(widths, sorted(widths), "repel only grows")
        self.assertGreater(widths[-1], widths[0] * 2)
        img = sheet("rocket")
        widths = [width(frame(img, 24, i)) for i in range(2, 12)]  # after the flash, before the smoke thins
        self.assertEqual(widths, sorted(widths), "the rocket swells")
        img = sheet("powerb")
        trail = [ink(cell(img, "powerb", 2, c)) for c in range(10)]
        self.assertGreater(trail[0], trail[-1] * 2, "the trail burns out")
        self.assertGreater(ink(cell(img, "powerb", 0, 0)), trail[5])

    def test_explosions_play_once_out(self):
        for kind in ["explode0", "explode1", "explode2", "empburst"]:
            side = graphics.BURSTS[kind]
            img = sheet(kind)
            n = (img.width // side) * (img.height // side)
            with self.subTest(kind=kind):
                used = [ink(frame(img, side, i)) for i in range(n)]
                used = [u for u in used if u]
                self.assertGreater(max(used), used[-1] * 2, "it fades out by its last frame")


if __name__ == "__main__":
    unittest.main()


def coins() -> Image.Image:
    """A stand-in for PixelLab's "explosion bursting into tiny coins": gold discs round a core."""
    import math
    from PIL import ImageDraw
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for k in range(9):
        a = k * 2 * math.pi / 9
        x, y = 32 + 24 * math.cos(a), 30 + 20 * math.sin(a)
        d.ellipse((x - 5, y - 5, x + 5, y + 5), fill=(245, 200, 30, 255))
    d.ellipse((22, 20, 42, 40), fill=(255, 140, 30, 255))
    return img


class Prompts(unittest.TestCase):
    """Every graphic but the colour gradient can be drawn from a description."""

    def test_every_picture_kind_takes_a_prompt_in_its_stock_layout(self):
        self.assertFalse(graphics.promptable("gradient"))
        for kind in list(graphics.SHEETS) + list(graphics.BURST_SHEETS):
            with self.subTest(kind=kind), mock.patch.object(graphics, "pixel_lab_sprite", return_value=coins()) as drawn:
                png = graphics.generate(kind, "prompt", stock(kind), prompt="tiny coins", seed=1)
                self.assertEqual(size(png), size(stock(kind)))
                text, kw = drawn.call_args.args[0], drawn.call_args.kwargs
                self.assertTrue(text.startswith("tiny coins, drawn as "), text)
                effect = kind in graphics.BURST_SHEETS
                self.assertIs(kw["prompt"], graphics.EFFECT_PROMPT if effect else graphics.SPRITE_PROMPT)
                self.assertEqual(kw["px"], graphics.BURSTS[kind] if effect else max(graphics.SPRITES[kind][:2]))

    def test_a_prompted_explosion_keeps_its_colours_and_plays_once_out(self):
        with mock.patch.object(graphics, "pixel_lab_sprite", return_value=coins()):
            out = sheet("explode1", "prompt", sprite=coins())
        tpl = graphics.load_template(stock("explode1"))
        live = [i for i in range(36) if frame(tpl, 48, i).getchannel("A").getbbox()]
        for i in range(36):  # the stock's unused frames stay empty, so the animation's length holds
            self.assertEqual(frame(out, 48, i).getchannel("A").getbbox() is not None, i in live, i)
        sizes = [width(frame(out, 48, i)) for i in live]
        peak = max(range(len(sizes)), key=lambda k: ink(frame(out, 48, live[k])))
        self.assertLess(sizes[0], sizes[peak] / 2, "starts small and blooms")
        self.assertLess(ink(frame(out, 48, live[-1])), ink(frame(out, 48, live[peak])) / 3, "breaks up at the end")
        hue = graphics._hue_of(frame(out, 48, live[peak]), (0, 0, 48, 48))
        self.assertLess(gap(hue, graphics._hue_of(coins(), (0, 0, 64, 64))), 0.05, "gold coins stay gold")

    def test_asteroids_turn_once_through_every_frame_in_their_own_colours(self):
        out = sheet("over1", "prompt", sprite=coins())
        frames = [frame(out, 16, i) for i in range(30)]
        self.assertGreater(difference(frames[14], frames[15]), 0, "row 2 carries on, not a restart")
        self.assertLess(difference(frames[29], frames[0]), difference(frames[15], frames[0]), "and loops back")
        self.assertLess(gap(graphics._hue_of(out, (0, 0, 240, 32)), graphics._hue_of(coins(), (0, 0, 64, 64))), 0.05)

    def test_bricks_and_goals_keep_teams_apart(self):
        for kind, cols in (("wall", 10), ("goal", 9)):
            out = sheet(kind, "prompt", sprite=coins())
            tpl = graphics.load_template(stock(kind))
            for row in (0, 1):
                a = graphics._hue_of(tpl, (0, row * 16, cols * 16, row * 16 + 16))
                b = graphics._hue_of(out, (0, row * 16, cols * 16, row * 16 + 16))
                self.assertLess(gap(a, b), 0.08, (kind, row))


class GraphicTries(unittest.TestCase):
    """/graphic prompts run like prompted ships: Try again shows a take unlike the ones shown."""

    def setUp(self):
        self.calls = []
        looks = [(245, 200, 30), (246, 201, 31), (40, 120, 230), (200, 40, 200), (40, 210, 90), (230, 60, 40)]

        def fake(description, px=16, **kw):
            self.calls.append(description)
            colour = looks[(len(self.calls) - 1) % len(looks)]
            img = coins()
            img.paste(colour + (255,), (22, 20, 42, 40), img.crop((22, 20, 42, 40)))
            body = Image.new("RGBA", img.size, (0, 0, 0, 0))
            body.paste(img, mask=img)
            px_ = body.load()
            for y in range(64):
                for x in range(64):
                    if px_[x, y][3]:
                        px_[x, y] = colour + (255,)
            return body

        for p in (mock.patch.object(graphics, "pixel_lab_sprite", side_effect=fake),
                  mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": "test"})):
            p.start()
            self.addCleanup(p.stop)
        service._tries.clear()
        self.template = base64.b64encode(stock("explode1")).decode()

    def ask(self, **body):
        return service.graphic({"kind": "explode1", "template": self.template, "mode": "prompt", **body})

    @staticmethod
    def hue(reply):
        img = graphics.load_template(base64.b64decode(reply["sheet"]))
        return graphics._hue_of(img, (0, 0, img.width, img.height))

    def test_try_again_differs_and_the_next_take_is_drawn_ahead(self):
        first = self.ask(prompt="an explosion bursting into tiny coins")
        self.assertEqual(size(base64.b64decode(first["sheet"])), (288, 288))
        tries = service._found(first["batch"])
        tries._drawing and tries._drawing.join()
        self.assertEqual(len(self.calls), 2)
        self.assertTrue(all(c.startswith("an explosion bursting into tiny coins") for c in self.calls))
        self.assertEqual(sum("Another take" in c for c in self.calls), 1)  # drawn alongside, any order
        # take 2 came back a near-twin of take 1: one more take is drawn rather than shown
        second = self.ask(batch=first["batch"])
        self.assertGreater(gap(self.hue(first), self.hue(second)), 0.1)
        self.assertGreaterEqual(len(self.calls), 3)

    def test_a_run_has_a_cap_and_tokens_stay_with_their_graphic(self):
        first = self.ask(prompt="coins")
        with self.assertRaises(service.Refused) as other:
            service.graphic({"kind": "repel", "template": base64.b64encode(stock("repel")).decode(),
                             "mode": "prompt", "batch": first["batch"]})
        self.assertEqual(other.exception.status, 410)
        with self.assertRaises(service.Refused):
            for _ in range(20):
                self.ask(batch=first["batch"])
        service._found(first["batch"])._drawing and service._found(first["batch"])._drawing.join()
        self.assertLessEqual(len(self.calls), 5)

    def test_gradient_has_no_prompt(self):
        with self.assertRaises(service.Refused) as r:
            service.graphic({"kind": "gradient", "template": base64.b64encode(stock("gradient")).decode(),
                             "mode": "prompt", "prompt": "x"})
        self.assertEqual(r.exception.status, 400)
