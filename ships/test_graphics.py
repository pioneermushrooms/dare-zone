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
        for kind in list(graphics.SPRITES) + list(graphics.BURSTS) + ["rocket", "over2"]:
            for mode in ["recolor", "new"]:
                if mode == "new" and kind not in graphics.SPRITES and kind not in graphics.BURSTS:
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

    def test_service_refuses_clearly(self):
        with self.assertRaises(service.Refused):
            service.graphic({"kind": "bombs", "mode": "new", "template": ""})
        with self.assertRaises(service.Refused):
            service.graphic({"kind": "rocket", "mode": "new", "template": base64.b64encode(stock("rocket")).decode()})
        with mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": ""}), self.assertRaises(service.Refused) as r:
            service.graphic({"kind": "bombs", "mode": "prompt", "prompt": "x", "template": base64.b64encode(stock("bombs")).decode()})
        self.assertEqual(r.exception.status, 503)
        ok = service.graphic({"kind": "mines", "mode": "recolor", "template": base64.b64encode(stock("mines")).decode()})
        self.assertTrue(ok["ok"])


if __name__ == "__main__":
    unittest.main()
