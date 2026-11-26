"""Generate overhead ship sprites and export Continuum rotation sheets.

Vendored from Maigent (src/maigent/tools/native/ship.py at f2a2be5). Only the PixelLab
endpoint and key lookup below differ: the key comes from the environment of the ship
service on the Foundation server and never reaches a browser.

Sheet contract (what the game reads as ship1..ship8): ship-rotations.png is 360x144,
40 frames of 36x36 in 10 columns and 4 rows. Frame 0 (top left) is nose-up and each next
frame, in row order, turns 9 degrees clockwise. flip_ship_heading is the 180 degree fix.
"""

import base64
import io
import math
import os
import random
import struct
import time
import zlib

import requests
from PIL import Image, ImageChops, ImageDraw, ImageOps

API_URL = "https://api.pixellab.ai/v2/create-image-pixflux"


def _api_key() -> str:
    key = os.environ.get("PIXEL_LAB_API_KEY", "").strip()
    if not key:
        raise RuntimeError("Prompted ships are not set up on this server yet.")
    return key

CELL = 36
HEADINGS = 40
SHEET_COLUMNS = 10
SHIP_SLOTS = 8
SHIPSET_LIMIT = 2 * 1024 * 1024
PROMPT = (
    "Draw exactly one spacecraft, one view, one sprite. Never draw a grid, sprite sheet, "
    "alternate views, or more than one spacecraft. Rotation frames will be created separately. "
    "Design brief: {description}\n"
    "Show a high-detail, strictly top-down orthographic view, nose pointing up. Center "
    "the entire craft on a transparent background. Use the requested art style. If no style is "
    "specified, use grounded hard-surface sci-fi. Favor muted matte materials, "
    "dark gunmetal, restrained accent colors, and a small cockpit highlight. Keep "
    "specular highlights subtle and omit bloom, neon outlines, and exaggerated cartoon "
    "shading. Separate the hull, wings, and engines with clear midtone edges so the "
    "silhouette stays readable on black. Give the visible surfaces tactile texture: "
    "grouped brush marks on metal, scuffed paint at a few edges, recessed seams, and "
    "small contrasting hardware. Use distinct clusters of marks and broad differences "
    "between materials so texture survives at 36 pixels. Avoid fine all-over noise. "
    "Keep a clear forward silhouette. Unless the brief calls for a slim design, give "
    "the spacecraft a broad, substantial outline with a span close to its nose-to-tail "
    "length. Put the wing tips, nose and tail near the edges, with the entire ship visible "
    "and only a thin clear margin. No scenery, labels, trails or cast shadows."
)
CREATIVE_PROMPT = (
    "Draw one game sprite of exactly the subject in the brief. The subject can be "
    "a creature, vehicle, object, spacecraft, or a blend if explicitly requested. "
    "A named creature or ordinary vehicle should look like itself. Add cockpits, "
    "thrusters, armor, or other spacecraft features only if requested.\n"
    "Design brief: {description}\n"
    "Use a strict orthographic top-down view. Point the subject's head, front, or nose "
    "toward the top edge, pose it with a clearly readable forward direction, and "
    "center its entire silhouette on a transparent background. Include the requested "
    "detail and art style, with bold shapes and lighting that read at a tiny game size. "
    "If the brief leaves the style open, use natural materials, subdued colors, "
    "matte shading, and restrained highlights. Show texture appropriate to the subject "
    "as distinct clusters of visible marks, such as shell ridges, scales, brushed metal, "
    "or worn paint. Keep the marks large enough to read at 36 pixels. "
    "Keep wings, fins, wheels, and other major features visibly attached. "
    "Produce one static view with no alternate poses, grids, text, props, cast shadows, "
    "trails, or scenery."
)


HEADING_REQUIREMENT = (
    " Final orientation check: frame zero is this image. The frontmost point "
    "(nose, muzzle, head, or front bumper) must point straight up at 12 o'clock, "
    "toward the top edge. The rear, exhaust, or tail must face the bottom edge "
    "at 6 o'clock. The sprite's front should be visibly distinguishable from its rear."
)


def _pixel_lab_ship(
    description: str, *, variation: bool = False, creative: bool = False
) -> Image.Image:
    template = CREATIVE_PROMPT if creative else PROMPT
    prompt = template.format(description=description)
    if variation and not creative:
        prompt += (
            " Draw a fresh variation on this brief. Vary the panel layout and secondary "
            "shapes while preserving the requested silhouette, style, and key features."
        )
    elif variation:
        prompt += (
            " Draw a fresh interpretation of the same subject and design brief. Vary "
            "secondary forms, surface details, or markings while preserving the requested "
            "overall shape, style, and key features."
        )
    response = requests.post(
        API_URL,
        headers={"Authorization": f"Bearer {_api_key()}"},
        json={
            "description": prompt + HEADING_REQUIREMENT,
            "image_size": {"width": 128, "height": 128},
            "no_background": True,
        },
        timeout=(10, 90),
    )
    if response.status_code == 402:
        raise RuntimeError("PixelLab is out of credits.")
    if response.status_code == 429:
        raise RuntimeError("PixelLab is rate limited. Try again shortly.")
    if not response.ok:
        raise RuntimeError(f"PixelLab returned HTTP {response.status_code}.")
    raw = base64.b64decode(response.json()["image"]["base64"])
    with Image.open(io.BytesIO(raw), formats=("PNG",)) as source:
        return source.convert("RGBA")


def _procedural_ship(seed: int) -> Image.Image:
    rng = random.Random(seed)
    metal, light, accent = rng.choice(
        [
            ("#4d5269", "#a7b4d6", "#ff9853"),
            ("#36555c", "#8abbad", "#80f7ff"),
            ("#674953", "#cfa290", "#ffe86f"),
            ("#555165", "#c2a0dd", "#8ef18c"),
            ("#455669", "#a0b8bd", "#ff7272"),
            ("#565841", "#b7b98e", "#8bb5ff"),
        ]
    )
    image = Image.new("RGBA", (64, 64))
    draw = ImageDraw.Draw(image)
    outline = "#191b2a"
    span = rng.randint(20, 29)
    sweep = rng.randint(24, 46)
    inner = rng.randint(5, 11)
    nose = rng.randint(3, 12)
    tail = rng.randint(51, 59)
    tip = rng.randint(0, 5)
    canopy = rng.randint(19, 31)
    for direction in (-1, 1):

        def points(offsets):
            return [(32 + direction * x, y) for x, y in offsets]

        wing = [
            (inner, 23),
            (span - 5, sweep - 9),
            (span, sweep),
            (span - 3, sweep + 7),
            (inner, tail - 3),
        ]
        draw.polygon(points(wing), fill=metal, outline=outline, width=2)
        draw.line(points([(inner, 25), (span - 6, sweep - 7)]), fill=light, width=2)
        draw.polygon(
            points(
                [
                    (span - 8, sweep - 10),
                    (span - 5, sweep - 12),
                    (span - 3, sweep + 2),
                    (span - 7, sweep + 5),
                ]
            ),
            fill=accent,
            outline=outline,
        )
        engine = [
            (inner - 2, tail - 14),
            (inner + 5, tail - 13),
            (inner + 5, tail + 2),
            (inner - 2, tail + 2),
        ]
        draw.polygon(points(engine), fill=outline)
        draw.line(points([(inner, tail), (inner + 3, tail)]), fill=accent, width=2)
    draw.polygon(
        [
            (32 - tip, nose),
            (32 + tip, nose),
            (32 + inner, 26),
            (40, 46),
            (37, tail),
            (27, tail),
            (24, 46),
            (32 - inner, 26),
        ],
        fill=light,
        outline=outline,
        width=2,
    )
    draw.polygon(
        [
            (32, canopy - 5),
            (37, canopy + 6),
            (35, canopy + 13),
            (29, canopy + 13),
            (27, canopy + 6),
        ],
        fill=outline,
    )
    draw.line([(31, canopy), (30, canopy + 7)], fill=accent, width=2)
    draw.line([(29, tail - 7), (35, tail - 7)], fill=metal, width=3)
    return image


def _reject_ship_grid(alpha: Image.Image) -> None:
    """Reject multiple substantial silhouettes while allowing small detached details."""
    width, height = alpha.size
    remaining = bytearray(alpha.tobytes())
    largest = second_largest = 0
    for start in range(len(remaining)):
        if not remaining[start]:
            continue
        remaining[start] = 0
        pending = [start]
        area = 0
        while pending:
            index = pending.pop()
            area += 1
            y, x = divmod(index, width)
            for row in range(max(0, y - 1), min(height, y + 2)):
                for col in range(max(0, x - 1), min(width, x + 2)):
                    neighbor = row * width + col
                    if remaining[neighbor]:
                        remaining[neighbor] = 0
                        pending.append(neighbor)
        if area > largest:
            largest, second_largest = area, largest
        else:
            second_largest = max(second_largest, area)
    if second_largest >= 24 and second_largest * 4 >= largest:
        raise ValueError(
            "PixelLab drew multiple large shapes. Generate again with one connected subject."
        )


def _rotation_frames(source: Image.Image, *, preserve_detail: bool = False) -> list[Image.Image]:
    source = source.convert("RGBA")
    alpha = source.getchannel("A")
    opaque_input = alpha.getextrema() == (255, 255)
    if opaque_input:
        # Some responses flatten the background to black.
        non_black = ImageChops.lighter(*source.convert("RGB").split()[:2])
        non_black = ImageChops.lighter(non_black, source.getchannel("B"))
        alpha = non_black.point(lambda value: 255 if value else 0)
    alpha = alpha.point(lambda value: 255 if value >= 128 else 0)
    box = alpha.getbbox()
    if box is None or (opaque_input and box == (0, 0, *source.size)):
        raise ValueError("The ship needs a clean, isolated background. Try another design.")
    if preserve_detail:
        _reject_ship_grid(alpha)
    source.putalpha(alpha)
    sprite = source.crop(box)
    # Fit the whole silhouette inside the turning circle, including pixel corners.
    # A common scale and pivot keep the ship from changing size as it turns.
    cx, cy = (sprite.width - 1) / 2, (sprite.height - 1) / 2
    corner = 0.5 if preserve_detail else 0.0
    radius = max(
        math.hypot(abs(x - cx) + corner, abs(y - cy) + corner)
        for y in range(sprite.height)
        for x in range(sprite.width)
        if sprite.getpixel((x, y))[3]
    )
    # Rotate detailed art on a larger canvas before reducing it to the game grid.
    # This keeps small paint and armor features from aliasing as the ship turns.
    render_scale = 4 if preserve_detail else 1
    canvas_size = CELL * render_scale
    if preserve_detail:
        # Leave two supersampled pixels for resampling at the turning circle's edge.
        scale = (canvas_size / 2 - 2) / max(radius, 1)
    else:
        scale = min(1.0, (CELL / 2 - 2) / max(radius, 1))
    size = tuple(max(1, round(dimension * scale)) for dimension in sprite.size)
    sprite = sprite.resize(
        size, Image.Resampling.BICUBIC if preserve_detail else Image.Resampling.NEAREST
    )
    centered = Image.new("RGBA", (canvas_size, canvas_size))
    centered.paste(
        sprite, ((canvas_size - sprite.width) // 2, (canvas_size - sprite.height) // 2)
    )
    resampling = Image.Resampling.BICUBIC if preserve_detail else Image.Resampling.NEAREST
    frames = []
    for index in range(HEADINGS):
        frame = centered.rotate(-index * 360 / HEADINGS, resample=resampling)
        if preserve_detail:
            frame = frame.resize((CELL, CELL), Image.Resampling.BOX)
            alpha = frame.getchannel("A").point(lambda value: 255 if value >= 128 else 0)
            frame.putalpha(alpha)
        frames.append(frame)
    return frames


def _encode(image: Image.Image, file_format: str) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format=file_format)
    return buffer.getvalue()


def _arena_pack(template: bytes, slot: int, frames: list[Image.Image]) -> bytes:
    if len(template) > SHIPSET_LIMIT or not 1 <= slot <= SHIP_SLOTS:
        raise ValueError("Use a shipset under 2 MB and a ship slot from 1 to 8.")
    try:
        with Image.open(io.BytesIO(template), formats=("BMP", "PNG")) as source:
            width, height = source.size
            if (
                width % CELL
                or height % CELL
                or width * height != CELL * CELL * HEADINGS * SHIP_SLOTS
            ):
                raise ValueError("The template must contain 320 frames of 36×36 pixels.")
            atlas = source.convert("RGB")
    except OSError as exc:
        raise ValueError("Attach a readable ships.bm2, ships.bmp, or ships.png.") from exc

    columns = width // CELL
    for heading, frame in enumerate(frames):
        index = (slot - 1) * HEADINGS + heading
        position = (index % columns * CELL, index // columns * CELL)
        atlas.paste((0, 0, 0), (*position, position[0] + CELL, position[1] + CELL))
        atlas.paste(frame, position, frame.getchannel("A"))

    bitmap = _encode(atlas, "BMP")
    compressed = zlib.compress(bitmap, level=9)
    return (
        struct.pack(
            "<4sI4sIII", b"CONT", 1, b"CONT", len(bitmap), int(time.time()), len(compressed)
        )
        + b"ships.bm2\0"
        + compressed
    )


def _render_ship_files(frames: list[Image.Image]) -> dict[str, bytes]:
    sheet = Image.new("RGBA", (CELL * SHEET_COLUMNS, CELL * (HEADINGS // SHEET_COLUMNS)))
    for index, frame in enumerate(frames):
        sheet.paste(frame, (index % SHEET_COLUMNS * CELL, index // SHEET_COLUMNS * CELL))

    previews = [
        ImageOps.scale(
            Image.alpha_composite(Image.new("RGBA", frame.size, "#151b2a"), frame),
            4,
            Image.Resampling.NEAREST,
        ).convert("RGB")
        for frame in frames
    ]
    preview = io.BytesIO()
    previews[0].save(
        preview, format="GIF", save_all=True, append_images=previews[1:], duration=65, loop=0
    )
    return {
        "ship-spin.gif": preview.getvalue(),
        "ship.png": _encode(frames[0], "PNG"),
        "ship-rotations.png": _encode(sheet, "PNG"),
    }


def flip_ship_heading(files: dict[str, bytes]) -> dict[str, bytes]:
    """Move the opposite heading to frame zero while preserving clockwise order."""
    with Image.open(io.BytesIO(files["ship-rotations.png"]), formats=("PNG",)) as sheet:
        frames = []
        for index in range(HEADINGS):
            x = index % SHEET_COLUMNS * CELL
            y = index // SHEET_COLUMNS * CELL
            frames.append(sheet.crop((x, y, x + CELL, y + CELL)).convert("RGBA"))

    half_turn = HEADINGS // 2
    flipped = _render_ship_files(frames[half_turn:] + frames[:half_turn])
    # Random (procedural) ships have no PixelLab source art to turn with them.
    if "ship-art-128.png" in files:
        with Image.open(io.BytesIO(files["ship-art-128.png"]), formats=("PNG",)) as source:
            flipped["ship-art-128.png"] = _encode(
                source.transpose(Image.Transpose.ROTATE_180), "PNG"
            )
    return flipped


def generate_ship(
    description: str = "",
    seed: int | None = None,
    template: bytes | None = None,
    slot: int = 1,
    *,
    variation: bool = False,
    creative: bool = False,
) -> tuple[dict[str, bytes], int | None]:
    if description:
        source = _pixel_lab_ship(description, variation=variation, creative=creative)
        seed = None
    else:
        seed = seed if seed is not None else random.SystemRandom().randrange(2**31)
        source = _procedural_ship(seed)

    frames = _rotation_frames(source, preserve_detail=bool(description))
    files = _render_ship_files(frames)
    if description:
        files["ship-art-128.png"] = _encode(source, "PNG")
    if template is not None:
        files["custom-ships.lvz"] = _arena_pack(template, slot, frames)
    return files, seed
