"""Handing out generated ships: what the game's ship service and the Discord bot both use.

- RandomPool: stock-style ships drawn ahead of time from randomized briefs, mixed across
  batches so consecutive ships differ; refilled in the background.
- Tries: one person's run of attempts (a prompt, or Random). Each next() is the ship least like
  the ones they've already been shown; once a batch has nothing different enough left, it draws
  a fresh one with a changed take on the brief.
- sheet_files(): a sprite turned into the rotation sheet and previews (Maigent's rotation and
  export, in shipgen).
"""

import logging
import random
import threading

from PIL import Image

from . import shipgen, stylegen

log = logging.getLogger(__name__)

# PixelLab style jobs running at once, across everything in this process.
_style_slots = threading.BoundedSemaphore(2)


class Busy(RuntimeError):
    """Every PixelLab slot is taken; try again in a few seconds."""


def draw(description: str, **kwargs) -> list[Image.Image]:
    if not _style_slots.acquire(timeout=1):
        raise Busy("The ship painter is busy. Try again in a few seconds.")
    try:
        return stylegen.draw_batch(description, seed=random.SystemRandom().randrange(2**31), **kwargs)
    finally:
        _style_slots.release()


def sheet_files(sprite: Image.Image) -> dict[str, bytes]:
    """The rotation sheet (ship-rotations.png), spin preview and frame 0 for one sprite.

    Raises ValueError when the sprite isn't one clean shape (shipgen's own check).
    """
    frames = shipgen._rotation_frames(sprite, preserve_detail=True)
    files = shipgen._render_ship_files(frames)
    files["ship-art-128.png"] = shipgen._encode(sprite, "PNG")
    return files


class RandomPool:
    """Stock-style ships ready to hand out, from several randomized briefs at once."""

    def __init__(self, low: int = 24, keep_per_batch: int = 12):
        self.low = low
        self.keep = keep_per_batch
        self._ships: list[Image.Image] = []
        self._lock = threading.Lock()
        self._refilling = threading.Lock()

    def __len__(self) -> int:
        with self._lock:
            return len(self._ships)

    def _draw_one_batch(self) -> None:
        brief = stylegen.random_brief(random.Random())
        ships = stylegen.spread(draw(brief), self.keep)
        with self._lock:
            self._ships.extend(ships)
        log.info("random pool +%d (%s)", len(ships), brief)

    def refill(self) -> None:
        """Draws batches until the pool is above its low mark (one refill at a time)."""
        if not self._refilling.acquire(blocking=False):
            return
        try:
            while len(self) < self.low:
                self._draw_one_batch()
        except Exception as exc:  # logged; the next take tries again
            log.warning("random pool refill failed: %s", exc)
        finally:
            self._refilling.release()

    def refill_soon(self) -> None:
        if len(self) < self.low and not self._refilling.locked():
            threading.Thread(target=self.refill, daemon=True).start()

    def take(self, seen: list[list[float]]) -> Image.Image:
        """The pooled ship least like `seen`; draws a batch first when the pool is empty."""
        for _ in range(3):
            with self._lock:
                if self._ships:
                    ship = self._ships.pop(stylegen.most_distinct(self._ships, seen))
                    break
            with self._refilling:  # wait for a refill under way, or draw one now
                if len(self) == 0:
                    self._draw_one_batch()
        else:
            raise RuntimeError("Ship generation failed. Try again.")
        self.refill_soon()
        return ship


class Tries:
    """One person's attempts at a ship: each next() differs from what they've seen.

    With a prompt, ships come from batches drawn for it; with prompt=None, from `pool`.
    """

    MAX_FROM_BATCH = 3      # then a fresh batch, so Try again doesn't circle one design
    MIN_DIFFERENCE = 0.035  # below this a candidate is a near-twin or a repaint of one shown
    AGAIN = (
        " Another take on the same brief: change the silhouette, wing layout and colour "
        "accents from the usual version."
    )

    def __init__(
        self, prompt: str | None = None, pool: RandomPool | None = None, max_batches: int = 5
    ):
        if prompt is None and pool is None:
            raise ValueError("Tries needs a prompt or a pool.")
        self.prompt = prompt
        self.pool = pool
        self.seen: list[list[float]] = []
        self._batch: list[Image.Image] = []
        self._from_batch = 0
        self._takes = 0  # fresh batches drawn for this prompt
        self.max_batches = max_batches  # each is a paid PixelLab call
        self._lock = threading.Lock()

    def _fresh_batch(self) -> None:
        if self._takes >= self.max_batches:
            raise RuntimeError("That's a lot of tries for one description. Change it and draw again.")
        brief = self.prompt + (self.AGAIN if self._takes else "")
        self._batch = draw(brief)
        self._from_batch = 0
        self._takes += 1

    def _pick(self) -> Image.Image:
        if self.prompt is None:
            return self.pool.take(self.seen)
        drew = False
        if not self._batch or self._from_batch >= self.MAX_FROM_BATCH:
            self._fresh_batch()
            drew = True
        i = stylegen.most_distinct(self._batch, self.seen)
        if not drew and self.seen and min(
            stylegen.distance(stylegen.features(self._batch[i]), s) for s in self.seen
        ) < self.MIN_DIFFERENCE:
            self._fresh_batch()  # only near-twins left: one fresh draw, then the best of it
            i = stylegen.most_distinct(self._batch, self.seen)
        self._from_batch += 1
        return self._batch.pop(i)

    def next(self) -> dict[str, bytes]:
        """The next ship's files (see sheet_files). May call PixelLab, which takes 1-3 minutes."""
        with self._lock:
            for _ in range(6):
                sprite = self._pick()
                try:
                    files = sheet_files(sprite)
                except ValueError:
                    continue  # not one clean shape; take another
                self.seen.append(stylegen.features(sprite))
                return files
        raise RuntimeError("PixelLab drew nothing usable. Try again.")

    @property
    def ready(self) -> bool:
        """Whether next() can answer without drawing (a ship different enough is waiting)."""
        if self.prompt is None:
            return len(self.pool) > 0
        if not self._batch or self._from_batch >= self.MAX_FROM_BATCH:
            return False
        i = stylegen.most_distinct(self._batch, self.seen)
        return not self.seen or min(
            stylegen.distance(stylegen.features(self._batch[i]), s) for s in self.seen
        ) >= self.MIN_DIFFERENCE
