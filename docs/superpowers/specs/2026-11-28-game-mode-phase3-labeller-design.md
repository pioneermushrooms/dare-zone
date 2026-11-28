# Game mode — Phase 3: the map labeller (draft for Chris's approval)

Date: 2026-11-28 · Branch `claude/game-mode-wizard-904376` · Parent: `2026-11-27-game-mode-wizard-design.md`
(approved) · Phases 1 and 2 are live. Mockup: `docs/design/game-mode-phase3-mockup.html`.

## What this phase delivers

A **Game labels** panel in the map editor. Creators mark the places a game needs: team spawn points,
where flags and balls appear, bases, single flag spots, and areas with rules. Each label has a
name, a colour by team, and a shape (a point with a radius, or a rectangle). Some labels work in
the game the moment the map is published:

| Label | What it does now | Stored as |
|---|---|---|
| **Team spawn** (team 1–4) | pilots of that team spawn there | arena settings `Spawn:Team0-X/Y/Radius` … `Team3-…`, written on Publish |
| **Flag spawn area** | carried flags appear there instead of around the map's middle | `Flag:SpawnX/Y/Radius`, on Publish |
| **Ball spawn** (ball 1–8) | each ball starts there | `Soccer:SpawnX/Y/Radius` (ball 1), `SpawnX1/Y1/Radius1` … `SpawnX7…` (balls 2–8), on Publish |
| **Area with rules** | no weapons / no antiwarp / no flag drops inside: the server already enforces these | a region in the map file |
| **Base** (team) | marks a team's base; the rules module (Phase 4) uses it for capture-the-flag and hold-the-base | a region in the map file, "base" set |
| **Flag spot** (flag 1–N) | where each flag lives in capture-the-flag (Phase 4) | a one-tile region in the map file |

Static flags (turf) and goals stay what they are today: flag and goal tiles from the palette. The
panel shows how many the map has and links to them.

## A design decision to confirm: labels live with the map, not the game mode

A game mode is reused across arenas with different maps, so it can't hold map positions. Labels
belong to the **map**:

- **Areas, bases and flag spots** are stored inside the `.lvl` file as eLVL *regions*. That's the
  standard map format: DCME and other editors use it, and SubspaceServer already reads regions,
  enforces their no-weapons / no-antiwarp / no-flag-drop properties, and fires an event when a
  pilot enters or leaves one (what Phase 4 needs).
  - Our label kind and team are kept in one extra region chunk of our own, so our editor knows a
    region is "Base, red team".
  - Other editors and the server keep chunks they don't know, and ignore them.
  - Regions already in a map (from DCME, say) are shown and kept as they are; their name and
    properties can be edited.
- **Spawn points** (team, flag area, balls) are arena settings in SubspaceServer, not map data.
  - The editor keeps them as labels in the map too (same region chunk), so the map carries them.
  - **On Publish** it writes the matching settings into the arena through the usual settings apply
    (allow-list, value checks).
  - So a map from the library brings its spawns with it, and opening the editor shows them.
- The game mode never writes those position settings, so applying a game mode can't move them.

The parent design said labels would live in a `gamemode.json`. This replaces that: maps carry
their places, game modes carry their rules. **Approve or change this.**

## How it works in the editor

- **Panel:** a "Game labels" section in the editor's sidebar, below the LVZ panel. It's its own
  script, `labels-layer.js`, built the way `lvz-layer.js` is.
  - A list of labels, with a show/hide toggle and an edit toggle.
  - An **Add** menu: Team spawn, Flag spawn area, Ball spawn, Base, Flag spot, Area with rules.
  - The selected label's form: name, team (where it applies), radius or size, and for areas the
    three rule switches.
- **Placing:**
  - Points: Add, then click the map; drag the ring to change the radius.
  - Base and Area: drag a rectangle.
  - Flag spot: click a tile.
  - Labels can be dragged to move and deleted with Delete, and moves can be undone.
  - The LVZ panel's edit mode works the same way, so the two never take the pointer at once.
- **Drawing:** team colours (red, blue, green, yellow), a dashed outline for areas, a ring for
  spawns, a small flag for flag spots, each with its name. It's drawn above the tiles and below the
  editor's tool overlays.
- **Saving:**
  - Labels go into the map's eLVL block when the map is published, downloaded or saved to the
    library. The tileset and tiles are untouched.
  - Publish then applies the spawn settings and says so, e.g. "Map published. Also set 6 spawn
    settings."
  - If the arena's owner can't change one of those settings, it's listed as skipped, as on the
    Settings page.
- **The Game mode tab:** the Flags / ball panel says where flags and balls appear, e.g. "Flags
  appear at the map's Flag spawn label (512, 400, radius 20)", with a link to the editor.
  Capture-the-flag and Hold-the-base show "your map has 2 bases, 2 flag spots" ready for Phase 4.

## Out of this phase

- Rules that use bases and flag spots: capture-and-return, hold the base, per-flag spawn. That's
  Phase 4.
- Region shapes other than rectangles. Regions of any shape from other editors are kept and shown,
  but reshaping them is later.
- Auto-warp regions (teleporters), which the server supports. They could be an easy follow-up.

## Work

- **`elvl.js`** (new, DOM-free, UMD like `tiles.js`, with node tests):
  - Reads the eLVL block's chunks.
  - Reads and writes `REGN` regions: `rNAM` name, `rTIL` tiles, `rBSE` base, `rNAW` no antiwarp,
    `rNWP` no weapons, `rNFL` no flag drops, plus our own `rDGM` chunk (kind, team, index, radius,
    point).
  - Keeps every other chunk and region byte-for-byte.
  - Rebuilds the block, and fixes the BMP's size and eLVL offset fields the way
    `TileArt.withTileset` / `LvlFile.join` already do.
  - The `rTIL` encoding is ported from SubspaceServer's own reader (`ExtendedLvl.cs`) so the server
    reads back exactly what we write. Tests check a round trip of rectangles and odd shapes against
    the decoder.
- **`labels-layer.js`** (new): the panel, placing, drawing, and the label ↔ region mapping.
- **`editor.js` / `map.html`:** one mount hook like `TileArt.mount` (get/set the map prefix), the
  sidebar section, and a Publish step that sends the spawn settings. The map-editor session
  (editor-polish) has been told.
- **Server (small, needs a zone restart when deployed):** the server reads `Soccer:SpawnX1`…`SpawnX7`
  (and `Y`/`Radius`) for balls 2–8, but documents only ball 1's keys, so owners can't set the others
  today. The plugin declares those 21 keys with a config help attribute, as it does for
  `GameMode:Attach`, so they become owner-editable with min/max checks. Nothing else is new on the
  server: it already reads regions. A local zone check confirms it sees
  our regions (its log prints "N regions" for a map) and that a no-weapons area stops firing.
- **Tests:**
  - `node --test`: region round trips, other chunks preserved, a DCME-style map with regions opens
    and saves unchanged when nothing is edited, labels → settings for spawns.
  - Browser on the local zone: place each kind, publish, check the arena settings and the server's
    region count, and fly into a no-weapons area with a bot.
