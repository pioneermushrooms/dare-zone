"""Ships drawn in the style of the game's own: PixelLab's style endpoint, with the stock ships
as the reference.

style-refs.png holds the eight stock ships' nose-up frames (36x36 each, from the game's
ships.bm2). With 36-pixel references PixelLab answers with 36 ships at the game's native size,
matching their colours, shading and detail, all nose-up. One call is a batch; callers hand the
ships out one at a time. Each sprite then goes through shipgen's own rotation and export.
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
    "Classic top-down space shooter ship sprite, viewed straight from above with the nose "
    "pointing up. Rich multi-colour shading, beveled metal hull panels, a glinting cockpit, "
    "glowing engine exhausts, crisp dark outline, readable silhouette on black."
)
HULLS = [
    "agile interceptor", "heavy gunship", "long-range bomber", "stealthy recon scout",
    "armored assault fighter", "twin-hull catamaran fighter", "delta-wing strike craft",
    "forward-swept-wing duelist", "broad flying-wing raider", "compact turret fighter",
    "sleek racing fighter", "chunky mining tug refitted for combat",
]
WINGS = [
    "swept-back wings", "forward-swept wings", "short stubby wings", "wide delta wings",
    "split twin booms", "folded gull wings", "wingtip cannons", "side-mounted engine pods",
]
ENGINES = ["a single large thruster", "twin thrusters", "three thrusters", "four small thrusters"]
COLOURS = [
    "gunmetal grey with red trim", "olive green with yellow stripes", "deep crimson with silver",
    "royal blue with orange accents", "white and cyan", "desert tan with brown camo",
    "purple with gold trim", "black with neon green highlights", "orange and charcoal",
    "teal and copper", "bronze with blue glass", "steel blue with white stripes",
]


def random_brief(rng: random.Random) -> str:
    return (
        f"A {rng.choice(HULLS)} spaceship with {rng.choice(WINGS)} and {rng.choice(ENGINES)}, "
        f"painted {rng.choice(COLOURS)}."
    )


def _key() -> str:
    key = os.environ.get("PIXEL_LAB_API_KEY", "").strip()
    if not key:
        raise RuntimeError("Generated ships are not set up on this server yet.")
    return key


def _refs(rng: random.Random, count: int = 4) -> list[dict]:
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


def draw_batch(description: str, *, seed: int | None = None, timeout: float = 270) -> list[Image.Image]:
    """One PixelLab call: up to 36 nose-up 36x36 ships for `description`, in the stock style."""
    rng = random.Random(seed)
    headers = {"Authorization": f"Bearer {_key()}"}
    body = {
        "style_images": _refs(rng),
        "description": description,
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
