# LVZ editor

An `.lvz` is an overlay package: files (images, replacement graphics, sounds) plus objects that
show images, either on the map (decoration at pixel coordinates; doesn't block ships) or on the
player's screen (placed from one of twelve HUD reference points).

## Where

- **Map editor** (`/arena/map`, panel "LVZ images", `lvz-layer.js`): where LVZ is placed.
  - Images come from Upload (PNG/BMP/GIF), the pilot's Armory, the images of another `.lvz`, or
    **Stock images** (the game creator's, `gamemode-art.js`: digits, countdowns, banners, team
    icons). A stock image picks On the screen or On the map when it's only for one; a countdown's
    objects get a display time covering every frame, so it shows once instead of looping.
    Select one, then click to place; drag to move, Alt+drag to copy, Del deletes, arrows nudge,
    Ctrl/Cmd+D copies; Esc, Done or picking any drawing tool or tile goes back to walls.
  - **On the map**: drawn at its layer (BelowAll and AfterBackground under the tiles, the rest
    over them), animated; snaps to tiles unless turned off.
  - **On the screen**: a mimic game screen (`lvz-screen.js`) replaces the map view: the pilot's
    own window size by default, or 640×480 to 1920×1080; a faint HUD (energy bar, radar and its
    text, player list, item columns, chat) and the twelve reference points. Drag an image from the
    palette onto it (or select one and click). It snaps a corner, edge middle or centre onto the
    nearest reference point within 24 px (Shift: no snap); placed freely, each axis is measured
    from the nearest screen edge or centre, so it keeps its place at every window size. New screen
    objects are always shown (layer AfterChat); "Shown" and "Seconds" can be changed per object.
  - **Show LVZ** hides them all to work on the map underneath; nothing is removed.
  - **Save LVZ** keeps the placements with the map (see below); the list under "Placed" shows
    every object, on the map and on the screen.
- **LVZ (Advanced) tab** of the arena editor (`/arena/me#lvz`, `lvz-studio.js`): whole files.
  Open one of the arena's `.lvz` files, one of Your LVZ, an `.lvz` or a Toolkit `.ini` project
  (with its images) from disk, or start empty; images (frames as columns × rows, period in
  1/100 s), files, and every object's fields. Downloads: `.lvz` and `.ini`.

**Reference points** (`lvz-screen.js` `anchors(w, h)`, the order of `Lvz.REFS`) are our client's
own, ported from oss-client `crates/oss-client/src/game/hud.rs` `screen_refs()`, which was
measured against Continuum at 1888×1016 (tests: `tests/js/lvz-screen.test.js`). Screen corners and
centre are exact everywhere; the item columns' height is exact only at that size, chat moves with
its line count, and the player list's corner is where it is in its default view (or the screen
origin when closed). Both our browser game and the native client are oss-client, so they agree.

## Ids kept for game modes

Object ids **2000–2999** belong to game modes: the game mode wizard builds them into the arena's
own `gamemode.lvz` and the server turns them on and off. The editors never give a new object an id
in that range (`Lvz.nextId` skips it), mark objects that have one ("set by the game mode", ⚙ in
the LVZ tab's list, a purple outline on the map), and the map editor's panel doesn't open
`gamemode.lvz` (or the Graphics page's `arena-graphics.lvz`) by itself. Keep pilots' own objects
in other files: publishing replaces an `.lvz` whole.

## Drafts and publishing

Save keeps the `.lvz` in **Your LVZ** (`/arena/api/lvzlib`: list, file, save, rename, duplicate,
remove; 10 per pilot, 5 MB each). In the map editor, Save LVZ also notes it (per arena, in the
browser) as part of the map's next **Publish**, which uploads it along with the map before the
restart (`window.mapPublishHooks`). Nothing in the arena changes until **Publish**, which uploads it
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
