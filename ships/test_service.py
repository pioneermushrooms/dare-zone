"""Run: python3 -m unittest test_service (needs Pillow and requests)."""

import base64
import io
import unittest

from PIL import Image

import service
import shipgen


def frames(sheet_b64: str) -> list[Image.Image]:
    sheet = Image.open(io.BytesIO(base64.b64decode(sheet_b64)))
    assert sheet.size == (360, 144), sheet.size
    return [sheet.crop((i % 10 * 36, i // 10 * 36, i % 10 * 36 + 36, i // 10 * 36 + 36)) for i in range(40)]


class SheetContract(unittest.TestCase):
    def test_random_sheet_is_40_headings_nose_up_clockwise(self):
        reply = service.generate({"mode": "random", "seed": 7})
        self.assertTrue(reply["ok"])
        self.assertEqual(reply["seed"], 7)
        f = frames(reply["sheet"])
        self.assertTrue(all(fr.getchannel("A").getbbox() for fr in f), "every heading draws")
        up = f[0].getchannel("A").getbbox()
        right = f[10].getchannel("A").getbbox()
        # 90 degrees clockwise (frame 10) turns the frame-0 bounding box on its side.
        self.assertAlmostEqual(up[3] - up[1], right[2] - right[0], delta=2)
        self.assertAlmostEqual(up[2] - up[0], right[3] - right[1], delta=2)
        # Clockwise: what was at the top of frame 0 is at the right of frame 10.
        top_heavy = sum(1 for y in range(18) for x in range(36) if f[0].getpixel((x, y))[3]) - \
            sum(1 for y in range(18, 36) for x in range(36) if f[0].getpixel((x, y))[3])
        right_heavy = sum(1 for x in range(18, 36) for y in range(36) if f[10].getpixel((x, y))[3]) - \
            sum(1 for x in range(18) for y in range(36) if f[10].getpixel((x, y))[3])
        self.assertEqual(top_heavy > 0, right_heavy > 0)
        self.assertNotEqual(top_heavy, 0)

    def test_flip_moves_heading_20_to_frame_0(self):
        f = frames(service.generate({"mode": "random", "seed": 11})["sheet"])
        sheet = shipgen._render_ship_files(f)["ship-rotations.png"]
        flipped = frames(service.flip({"sheet": base64.b64encode(sheet).decode()})["sheet"])
        for i in range(40):
            self.assertEqual(flipped[i].tobytes(), f[(i + 20) % 40].tobytes(), i)

    def test_refusals(self):
        with self.assertRaises(service.Refused):
            service.generate({"mode": "nope"})
        with self.assertRaises(service.Refused):
            service.generate({"mode": "prompt", "prompt": "x" * 501})
        with self.assertRaises(service.Refused):
            service.generate({"mode": "prompt", "creative": True, "prompt": " "})
        with self.assertRaises(service.Refused):
            service.flip({"sheet": "not base64!"})


if __name__ == "__main__":
    unittest.main()


class StockStyleBatches(unittest.TestCase):
    """Random and prompted ships come from stylegen batches; PixelLab is mocked here."""

    def setUp(self):
        from unittest import mock
        import os
        self.calls = []

        def fake_batch(description, *, seed=None, timeout=180):
            self.calls.append(description)
            # Nose-up arrows of distinct colours, 36x36 like the style endpoint's answer.
            sprites = []
            for n in range(5):
                im = Image.new("RGBA", (36, 36))
                for y in range(6, 30):
                    half = (y - 6) // 2
                    for x in range(18 - half, 18 + half + 1):
                        im.putpixel((x, y), (40 * n, 120, 200, 255))
                sprites.append(im)
            return sprites

        patches = [
            mock.patch("stylegen.draw_batch", side_effect=fake_batch),
            mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": "test"}),
            mock.patch.object(service, "_refill_soon", lambda: None),  # no background threads
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        service._pool.clear()
        service._batches.clear()

    def test_random_draws_a_batch_once_then_serves_from_it(self):
        first = service.generate({"mode": "random"})
        second = service.generate({"mode": "random"})
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(len(service._pool), 3)  # 5 drawn, 2 served
        for reply in (first, second):
            f = frames(reply["sheet"])
            self.assertTrue(all(fr.getchannel("A").getbbox() for fr in f))
            # Nose-up: the arrow's tip is in the top half of frame 0.
            box = f[0].getchannel("A").getbbox()
            top = sum(1 for y in range(box[1], box[1] + 4) for x in range(36) if f[0].getpixel((x, y))[3])
            bottom = sum(1 for y in range(box[3] - 4, box[3]) for x in range(36) if f[0].getpixel((x, y))[3])
            self.assertLess(top, bottom)
        self.assertNotEqual(first["sheet"], second["sheet"])

    def test_a_prompt_batch_gives_try_again_for_free_until_used_up(self):
        reply = service.generate({"mode": "prompt", "prompt": "a bronze gunship"})
        self.assertEqual((reply["left"], len(self.calls)), (4, 1))
        self.assertIn("bronze gunship", self.calls[0])
        seen = {reply["sheet"]}
        for left in (3, 2, 1, 0):
            again = service.generate({"mode": "prompt", "batch": reply["batch"]})
            self.assertEqual(again["left"], left)
            seen.add(again["sheet"])
        self.assertEqual(len(seen), 5)
        self.assertEqual(len(self.calls), 1)
        with self.assertRaises(service.Refused) as gone:
            service.generate({"mode": "prompt", "batch": reply["batch"]})
        self.assertEqual(gone.exception.status, 410)
