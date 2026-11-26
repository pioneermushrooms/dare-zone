"""Real PixelLab check of prompted graphics: one take per kind, saved as sheets to look at.

    PIXEL_LAB_API_KEY=... python3 try_prompts.py OUT_DIR [kind=prompt ...]

With no kind=prompt pairs it draws every kind that gained a prompt (16 to 18 paid pixflux calls),
plus a Try again of the coin explosion. The key is read from the environment and never printed.
"""

import os
import sys
import time

from PIL import Image

import graphics
from foundation_ships import designer, stylegen

GFX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Dare.Arenas", "Web", "gfx")
DEFAULT = {
    "explode1": "an explosion bursting into tiny gold coins",
    "explode0": "a tiny splash of green slime",
    "explode2": "a huge blast of pink confetti and stars",
    "empburst": "a crackling ring of blue lightning",
    "warp": "a swirl of green magic sparkles",
    "repel": "a ring of purple soap bubbles",
    "rocket": "a burst of rainbow sparks",
    "exhaust": "puffs of white steam",
    "over1": "a chunk of blue ice",
    "over2": "a huge cratered moon rock",
    "over3": "a floating lump of gold ore",
    "over4": "a round space station with green solar panels",
    "over5": "a red swirling black hole",
    "goal": "a glowing soccer goal",
    "wall": "mossy stone bricks",
}


def main() -> None:
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    asks = dict(a.split("=", 1) for a in sys.argv[2:]) or DEFAULT
    for kind, prompt in asks.items():
        with open(os.path.join(GFX, kind + ".bm2"), "rb") as f:
            template = f.read()
        started = time.monotonic()
        takes = 2 if kind == "explode1" else 1
        tries = designer.Tries(
            prompt=prompt,
            draw_fn=lambda brief, kind=kind: [graphics.draw_picture(kind, brief)],
            files_fn=lambda pic, kind=kind, template=template: {"sheet": graphics.from_picture(kind, template, pic), "pic": pic},
            again_fn=graphics.again, recheck=True, max_batches=takes + 1 if takes > 1 else 1,
        )
        for n in range(takes):
            files = tries.next()
            with open(os.path.join(out, f"{kind}-{n}.png"), "wb") as f:
                f.write(files["sheet"])
            files["pic"].save(os.path.join(out, f"{kind}-{n}-drawing.png"))
        if takes > 1:
            print(f"  try again distance {stylegen.distance(*tries.seen[:2]):.3f} (twins < {designer.Tries.MIN_DIFFERENCE})")
        tries._drawing and tries._drawing.join()  # the take drawn ahead, so nothing is left running
        print(f"{kind}: {time.monotonic() - started:.0f} s, {tries._takes} paid call(s)")


if __name__ == "__main__":
    main()
