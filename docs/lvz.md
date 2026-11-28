# LVZ editor

An `.lvz` is an overlay package: files (images, replacement graphics, sounds) plus objects that
show images, either on the map (decoration at pixel coordinates; doesn't block ships) or on the
player's screen (placed from one of twelve HUD reference points).

## Where

- **LVZ tab** of the arena editor (`/arena/me#lvz`, `lvz-studio.js`): open one of the arena's
  `.lvz` files, one of Your LVZ, an `.lvz` or a Toolkit `.ini` project (pick the `.ini` together
  with its images) from disk, or start empty. Manage images (PNG/BMP/GIF; frames as columns ×
  rows, period in 1/100 s for the whole run) and files; list and edit objects (kind, image, id,
  position and reference points, layer, when shown, seconds). Screen objects show on a
  screen-sized preview (1024 × 768 and other sizes) and can be dragged there. Downloads: `.lvz`
  and `.ini`.
- **Map editor** (`/arena/map`, panel "LVZ objects", `lvz-layer.js`): the chosen `.lvz`'s map
  objects are drawn on the map at their layer (BelowAll and AfterBackground under the tiles, the
  rest over them), animated. With "Place and move" on: pick an image, click the map to place it;
  drag to move (snaps to tiles unless turned off), Alt+drag to copy, Del deletes, arrows nudge,
  Ctrl/Cmd+D copies. `?lvz=<file>` or `?lvzlib=<id>` opens one straight away.

## Drafts and publishing

Save keeps the `.lvz` in **Your LVZ** (`/arena/api/lvzlib`: list, file, save, rename, duplicate,
remove; 10 per pilot, 5 MB each). Nothing in the arena changes until **Publish**, which uploads it
through `/arena/api/upload?kind=lvz` (owner only; the arena's usual rules: 10 files, safe names,
the live-edit lock). Players get it when the arena restarts; the LVZ tab offers the restart. An
arena's own `.lvz` files are read by its owner and co-mods through `/arena/api/lvz/arena`.

## The format (`lvz.js`)

`Lvz.read(bytes)` → `{ version, files, images, objects }`; `Lvz.write(model)` → bytes. Sections
nobody touched keep their original compressed bytes, so an `.lvz` read and written unchanged comes
out byte for byte the same (tested with the stock `match.lvz`: 135 files, 989 objects, 139 images).

- Object words: id word = map flag (bit 0) + id; map x/y = signed pixels; CLV2 screen x/y = reference
  point (bits 0–3) + signed 12-bit offset; image; layer; time word = display time in 1/10 s
  (bits 0–11) + mode (bits 12–15).
- CLV1 screen objects use a different reading of the same words (measured against Continuum in
  oss-core's `oss-formats/src/lvz.rs`): the whole word as an i16 picks the view's top-left, the
  bottom-right of a 960-pixel box centred in the view, or the view's bottom-right; the low 12 bits
  are the offset. They are read into the CLV2 model (top-left, `C` + 480, or `B`) and saved as
  CLV2, which places them the same. A CLV1 section with only map objects stays CLV1.
- Several object sections are merged into one (image numbers shifted).
- `.ini` (BuildLevel projects): `Lvz.fromIni(text, files)` and `Lvz.toIni(model)`. Object display
  times in the `.ini` are 1/100 s and are divided by 10 (more than 409.5 s is reported and cut);
  image animation periods are kept as written. Errors come back with their line numbers.
- Pure black in LVZ images is see-through in game, and in the previews.

The HUD reference points in the screen preview are approximate (from the Toolkit's
`refpoints.gif`, a default HUD at 889 × 667); real positions move with each player's HUD settings.

Tests: `node --test tests/js/*.test.js` (`lvz.test.js`), and `PilotLvzTests` for the library.
