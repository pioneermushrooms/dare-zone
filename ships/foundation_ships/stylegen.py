"""Ships drawn in the style of the game's own: PixelLab's style endpoint, with stock ships as
the reference.

style-refs.png holds the eight stock ships' nose-up frames (36x36 each, from the game's
ships.bm2). With 36-pixel references PixelLab answers with about 36 ships at the game's native
size, matching their colours, shading and detail, all nose-up. One call is a batch.

The references are there for the rendering (pixel scale, palette depth, shading, outline), not
the shapes: with all eight shown, results came back as near-copies of the stock ships. So only
one or two are shown at a time, chosen at random, and the brief asks for an original layout.
A batch also tends to hold many near-twins; most_distinct() picks ships that actually differ.
"""

import base64
import io
import os
import random
import time
from pathlib import Path

import requests
from PIL import Image

STYLE_URL = "https://api.pixellab.ai/v2/generate-with-style-v2"
JOB_URL = "https://api.pixellab.ai/v2/background-jobs/{job_id}"
REFS = Path(__file__).with_name("style-refs.png")
CELL = 36
STYLE = (
    "Match only how the reference sprites are rendered: tiny 36-pixel top-down space shooter "
    "art, rich multi-colour shading, beveled hull panels, a glinting cockpit, glowing engine "
    "exhausts, a crisp dark outline. Do not copy the reference ships' shapes, wings or layout: "
    "invent a new silhouette. Seen from directly above, nose pointing straight up."
)
ORIGINAL = "An original design, not a copy of the reference ships. "
# A random brief draws one from each list, so batches differ in silhouette as well as paint.
HULLS = [
    "agile interceptor", "heavy gunship", "long-range bomber", "stealthy recon scout",
    "armored assault fighter", "twin-hull catamaran fighter", "delta-wing strike craft",
    "forward-swept-wing duelist", "broad flying-wing raider", "compact turret fighter",
    "sleek racing fighter", "mining tug refitted for combat", "insect-like hunter",
    "manta-ray shaped glider", "trident-shaped lancer", "ring-hulled oddity",
    "asymmetric pirate hot-rod", "boxy armored hauler", "needle-nosed sniper craft",
    "crescent-winged corsair", "organic bio-ship", "salvaged junk fighter",
]
WINGS = [
    "swept-back wings", "forward-swept wings", "short stubby wings", "wide delta wings",
    "split twin booms", "folded gull wings", "wingtip cannons", "side-mounted engine pods",
    "no wings at all, just a thick hull", "four small fins in an X", "a ring around the hull",
    "long outrigger pontoons", "one wing longer than the other", "curved claw-like prongs",
]
ENGINES = [
    "a single large thruster", "twin thrusters", "three thrusters", "four small thrusters",
    "a wide slot-shaped exhaust", "engines on long struts",
]
COLOURS = [
    "gunmetal grey with red trim", "olive green with yellow stripes", "deep crimson with silver",
    "royal blue with orange accents", "white and cyan", "desert tan with brown camo",
    "purple with gold trim", "black with neon green highlights", "orange and charcoal",
    "teal and copper", "bronze with blue glass", "steel blue with white stripes",
    "rusty red-brown with patched panels", "pearl white with pink accents",
    "jade green with ivory", "hazard yellow and black chevrons", "midnight navy with silver",
]


def random_brief(rng: random.Random) -> str:
    return (
        f"A {rng.choice(HULLS)} spaceship with {rng.choice(WINGS)} and {rng.choice(ENGINES)}, "
        f"painted {rng.choice(COLOURS)}."
    )


def _key() -> str:
    key = os.environ.get("PIXEL_LAB_API_KEY", "").strip()
    if not key:
        raise RuntimeError("Generated ships are not set up here yet (no PixelLab key).")
    return key


def _refs(rng: random.Random, count: int) -> list[dict]:
    with Image.open(REFS) as sheet:
        sheet = sheet.convert("RGBA")
        kinds = rng.sample(range(sheet.width // CELL), count)
        out = []
        for k in kinds:
            buffer = io.BytesIO()
            sheet.crop((k * CELL, 0, k * CELL + CELL, CELL)).save(buffer, "PNG")
            out.append({
                "image": {"type": "base64", "base64": base64.b64encode(buffer.getvalue()).decode()},
                "width": CELL,
                "height": CELL,
            })
        return out


def draw_batch(
    description: str, *, seed: int | None = None, refs: int | None = None, timeout: float = 420
) -> list[Image.Image]:
    """One PixelLab call: up to ~36 nose-up 36x36 ships for `description`, in the stock style.

    `refs` is how many stock ships to show as the style guide (1 or 2 by default, at random).
    """
    rng = random.Random(seed)
    headers = {"Authorization": f"Bearer {_key()}"}
    body = {
        "style_images": _refs(rng, refs or rng.choice((1, 1, 2))),
        "description": ORIGINAL + description,
        "style_description": STYLE,
        "no_background": True,
    }
    response = requests.post(STYLE_URL, json=body, headers=headers, timeout=(10, 60))
    if response.status_code == 402:
        raise RuntimeError("PixelLab is out of credits.")
    if response.status_code == 429:
        raise RuntimeError("PixelLab is rate limited. Try again shortly.")
    if not response.ok:
        raise RuntimeError(f"PixelLab returned HTTP {response.status_code}.")
    job = response.json()["background_job_id"]
    deadline = time.monotonic() + timeout
    while True:
        time.sleep(3)
        status = requests.get(JOB_URL.format(job_id=job), headers=headers, timeout=(10, 30))
        if not status.ok:
            raise RuntimeError(f"PixelLab returned HTTP {status.status_code}.")
        data = status.json()
        if data.get("status") == "completed":
            break
        if data.get("status") not in (None, "processing", "pending", "queued"):
            raise RuntimeError("PixelLab couldn't draw that. Try again.")
        if time.monotonic() > deadline:
            raise RuntimeError("PixelLab took too long. Try again.")
    images = []
    for item in (data.get("last_response") or {}).get("images") or []:
        raw = base64.b64decode(item.get("base64", ""))
        with Image.open(io.BytesIO(raw), formats=("PNG",)) as image:
            image = image.convert("RGBA")
        # Keep the ones that drew something substantial.
        box = image.getchannel("A").point(lambda v: 255 if v >= 128 else 0).getbbox()
        if box and (box[2] - box[0]) * (box[3] - box[1]) >= 120:
            images.append(image)
    if not images:
        raise RuntimeError("PixelLab drew nothing usable. Try again.")
    rng.shuffle(images)
    return images


# ---- telling ships apart --------------------------------------------------------------------
GRID = 12


def features(sprite: Image.Image) -> list[float]:
    """A small signature of a sprite: colours and silhouette on a 12x12 grid.

    The silhouette counts double: a repaint of the same shape is what reads as "the same ship".
    """
    small = sprite.convert("RGBA").resize((GRID, GRID), Image.Resampling.BOX)
    out = []
    for r, g, b, a in small.getdata():
        k = a / 255
        out += [r / 255 * k, g / 255 * k, b / 255 * k, 2 * k]
    return out


def distance(a: list[float], b: list[float]) -> float:
    """How different two signatures are. Measured on a real batch: ~0.01 identical twins, ~0.03
    the same shape repainted, ~0.045 a different colour and shape, ~0.09 clearly different."""
    return sum(abs(x - y) for x, y in zip(a, b)) / len(a)


def most_distinct(candidates: list, seen: list[list[float]], key=features) -> int:
    """Index of the candidate least like everything in `seen` (any one when nothing is seen yet)."""
    if not seen:
        return random.randrange(len(candidates))
    best, best_score = 0, -1.0
    for i, candidate in enumerate(candidates):
        f = key(candidate)
        score = min(distance(f, s) for s in seen)
        if score > best_score:
            best, best_score = i, score
    return best


def spread(candidates: list[Image.Image], count: int) -> list[Image.Image]:
    """`count` of the candidates, each chosen as unlike the ones already chosen as possible."""
    pool = list(candidates)
    chosen: list[Image.Image] = []
    seen: list[list[float]] = []
    while pool and len(chosen) < count:
        pick = pool.pop(most_distinct(pool, seen))
        chosen.append(pick)
        seen.append(features(pick))
    return chosen
