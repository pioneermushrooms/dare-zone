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

    def test_front_moves_the_chosen_frame_to_frame_0(self):
        f = frames(service.generate({"mode": "random", "seed": 11})["sheet"])
        sheet = shipgen._render_ship_files(f)["ship-rotations.png"]
        for front in (20, 7):
            moved = frames(service.front({"sheet": base64.b64encode(sheet).decode(), "frame": front})["sheet"])
            for i in range(40):
                self.assertEqual(moved[i].tobytes(), f[(i + front) % 40].tobytes(), (front, i))

    def test_refusals(self):
        with self.assertRaises(service.Refused):
            service.generate({"mode": "nope"})
        with self.assertRaises(service.Refused):
            service.generate({"mode": "prompt", "prompt": "x" * 501})
        with self.assertRaises(service.Refused):
            service.generate({"mode": "prompt", "creative": True, "prompt": " "})
        with self.assertRaises(service.Refused):
            service.front({"sheet": "not base64!", "frame": 3})
        with self.assertRaises(service.Refused):
            service.front({"sheet": base64.b64encode(b"x").decode(), "frame": 40})


if __name__ == "__main__":
    unittest.main()


class StockStyleTries(unittest.TestCase):
    """Random and prompted ships come from foundation_ships.designer; PixelLab is mocked here."""

    def setUp(self):
        from unittest import mock
        import os
        from foundation_ships import designer
        self.calls = []

        def fake_batch(description, *, seed=None, refs=None, timeout=270):
            self.calls.append(description)
            # Nose-up arrows: three near-twin blues and two clearly different ones per batch.
            sprites = []
            for colour in [(40, 120, 200), (42, 122, 198), (44, 118, 202), (220, 60, 40), (60, 200, 60)]:
                im = Image.new("RGBA", (36, 36))
                for y in range(6, 30):
                    half = (y - 6) // 2
                    for x in range(18 - half, 18 + half + 1):
                        im.putpixel((x, y), (*colour, 255))
                sprites.append(im)
            return sprites

        patches = [
            mock.patch("foundation_ships.stylegen.draw_batch", side_effect=fake_batch),
            mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": "test"}),
            mock.patch.object(designer.RandomPool, "refill_soon", lambda self: None),  # no threads
            mock.patch.object(service, "_pool", designer.RandomPool(low=24, keep_per_batch=4)),
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        service._tries.clear()

    @staticmethod
    def colour(reply):
        f = frames(reply["sheet"])[0]
        return max((px for px in f.getdata() if px[3] == 255), key=lambda p: p[0] + p[1] + p[2])

    def test_random_is_nose_up_and_try_again_differs(self):
        first = service.generate({"mode": "random"})
        self.assertEqual(len(self.calls), 1)
        f = frames(first["sheet"])
        self.assertTrue(all(fr.getchannel("A").getbbox() for fr in f))
        # Nose-up: the arrow's tip is in the top of frame 0.
        box = f[0].getchannel("A").getbbox()
        top = sum(1 for y in range(box[1], box[1] + 4) for x in range(36) if f[0].getpixel((x, y))[3])
        bottom = sum(1 for y in range(box[3] - 4, box[3]) for x in range(36) if f[0].getpixel((x, y))[3])
        self.assertLess(top, bottom)
        # The pool kept the batch's 4 most different (dropping a near-twin); Try again picks
        # the ones least like what was shown: three different colours in three tries.
        again = service.generate({"mode": "random", "batch": first["batch"]})
        third = service.generate({"mode": "random", "batch": first["batch"]})
        hues = {max(range(3), key=lambda k: self.colour(r)[k]) for r in (first, again, third)}
        self.assertEqual(len(hues), 3)

    def test_prompt_try_again_skips_near_twins_then_draws_a_fresh_take(self):
        reply = service.generate({"mode": "prompt", "prompt": "a bronze gunship"})
        self.assertEqual(len(self.calls), 1)
        self.assertIn("bronze gunship", self.calls[0])
        seen = [self.colour(reply)]
        for _ in range(2):
            reply = service.generate({"mode": "prompt", "batch": reply["batch"]})
            seen.append(self.colour(reply))
        self.assertEqual(len(self.calls), 1, "three different ships came from the first batch")
        hues = {max(range(3), key=lambda k: c[k]) for c in seen}
        self.assertEqual(len(hues), 3)
        # Only near-twins of a shown ship are left: the next try draws another take.
        service.generate({"mode": "prompt", "batch": reply["batch"]})
        self.assertEqual(len(self.calls), 2)
        self.assertIn("Another take", self.calls[1])

    def test_a_run_of_tries_has_a_cap_on_paid_batches(self):
        from foundation_ships import designer
        tries = designer.Tries(prompt="a bronze gunship", max_batches=2)
        with self.assertRaises(RuntimeError):
            for _ in range(20):
                tries.next()
        self.assertEqual(len(self.calls), 2)

    def test_an_unknown_token_without_a_prompt_has_expired(self):
        with self.assertRaises(service.Refused) as gone:
            service.generate({"mode": "prompt", "batch": "nope"})
        self.assertEqual(gone.exception.status, 410)


class StyleRequest(unittest.TestCase):
    """What is sent to PixelLab's style endpoint (requests mocked)."""

    def test_one_or_two_stock_refs_and_an_original_brief(self):
        from unittest import mock
        import os
        from foundation_ships import stylegen
        sent = []
        art = Image.new("RGBA", (36, 36))
        for y in range(8, 28):
            for x in range(10, 26):
                art.putpixel((x, y), (200, 100, 50, 255))
        buf = io.BytesIO()
        art.save(buf, "PNG")
        done = {"status": "completed", "last_response": {"images": [{"base64": base64.b64encode(buf.getvalue()).decode()}]}}

        def post(url, json, headers, timeout):
            sent.append(json)
            return mock.Mock(ok=True, status_code=202, json=lambda: {"background_job_id": "j"})

        with mock.patch.dict(os.environ, {"PIXEL_LAB_API_KEY": "k"}), \
                mock.patch("requests.post", side_effect=post), \
                mock.patch("requests.get", return_value=mock.Mock(ok=True, json=lambda: done)), \
                mock.patch("time.sleep"):
            for seed in range(6):
                self.assertEqual(len(stylegen.draw_batch("a bronze gunship", seed=seed)), 1)
        counts = {len(body["style_images"]) for body in sent}
        self.assertTrue(counts <= {1, 2} and counts, counts)
        for body in sent:
            self.assertTrue(body["description"].startswith(stylegen.ORIGINAL))
            self.assertIn("a bronze gunship", body["description"])
            self.assertIn("Do not copy", body["style_description"])
            self.assertEqual({(r["width"], r["height"]) for r in body["style_images"]}, {(36, 36)})
