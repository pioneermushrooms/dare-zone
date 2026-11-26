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

# PixelLab style jobs running at once, across everything in this process (pool refills, prompts
# and the next takes drawn ahead in the background).
_style_slots = threading.BoundedSemaphore(4)


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

    MAX_FROM_BATCH = 2      # then a fresh take: one batch's ships are cousins of each other
    MIN_DIFFERENCE = 0.045  # below this a candidate is a cousin, twin or repaint of one shown
    AGAIN = (
        " Another take on the same brief, keeping what it asks for but built as a {hull} "
        "with {wings}: a clearly different silhouette from the usual version."
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
        self._takes = 0  # batches drawn (or being drawn) for this prompt
        self.max_batches = max_batches  # each is a paid PixelLab call
        self._lock = threading.Lock()
        # The next take, drawn in the background while the pilot looks at this one: PixelLab
        # takes 1.5-4.5 minutes per batch, too long to wait for on Try again.
        self._upcoming: list[Image.Image] | None = None
        self._drawing: threading.Thread | None = None

    def _brief(self, take: int) -> str:
        if take == 0:
            return self.prompt
        rng = random.Random()
        return self.prompt + self.AGAIN.format(
            hull=rng.choice(stylegen.HULLS), wings=rng.choice(stylegen.WINGS)
        )

    def _draw_ahead(self) -> None:
        """Starts drawing the next take in the background (once; within the paid cap)."""
        if self._drawing is not None or self._upcoming is not None or self._takes >= self.max_batches:
            return
        brief = self._brief(self._takes)
        self._takes += 1

        def run():
            try:
                self._upcoming = draw(brief)
            except Exception as exc:  # the next Try again draws in the foreground instead
                log.warning("drawing ahead failed: %s", exc)
                self._takes -= 1

        self._drawing = threading.Thread(target=run, daemon=True)
        self._drawing.start()

    def _fresh_batch(self) -> None:
        if self._drawing is not None:
            self._drawing.join()  # a take already under way: wait for it rather than pay twice
            self._drawing = None
        if self._upcoming is not None:
            self._batch, self._upcoming = self._upcoming, None
        else:
            if self._takes >= self.max_batches:
                raise RuntimeError(
                    "That's a lot of tries for one description. Change it and draw again."
                )
            brief = self._brief(self._takes)
            self._takes += 1
            self._draw_ahead()  # the next take alongside this one (PixelLab runs them in parallel)
            self._batch = draw(brief)
        self._from_batch = 0

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
        """The next ship's files (see sheet_files). May wait on PixelLab (1.5-4.5 minutes)."""
        with self._lock:
            for _ in range(6):
                sprite = self._pick()
                try:
                    files = sheet_files(sprite)
                except ValueError:
                    continue  # not one clean shape; take another
                self.seen.append(stylegen.features(sprite))
                if self.prompt is not None:
                    self._draw_ahead()  # so Try again rarely waits on PixelLab
                return files
        raise RuntimeError("PixelLab drew nothing usable. Try again.")

    @property
    def ready(self) -> bool:
        """Whether next() can answer without drawing (a ship different enough is waiting)."""
        if self.prompt is None:
            return len(self.pool) > 0
        if self._upcoming is not None:
            return True
        if not self._batch or self._from_batch >= self.MAX_FROM_BATCH:
            return False
        i = stylegen.most_distinct(self._batch, self.seen)
        return not self.seen or min(
            stylegen.distance(stylegen.features(self._batch[i]), s) for s in self.seen
        ) >= self.MIN_DIFFERENCE
