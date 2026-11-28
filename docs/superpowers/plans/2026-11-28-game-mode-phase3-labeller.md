# Game mode Phase 3 (map labeller) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Creators label team spawns, the flag spawn area, ball spawns, bases, flag spots and areas with rules in the map editor. Labels are saved in the map as eLVL regions, and Publish writes the spawn settings to the arena.

**Architecture:**
- `elvl.js` is a pure UMD module: it reads and writes eLVL regions in a map prefix. It reuses `tiles.js` `LvlFile.split/join` for the BMP and offsets.
- `labels-layer.js` is a map-editor panel modelled on `lvz-layer.js`. It maps labels ↔ regions, places and draws them, and turns spawn labels into settings.
- The plugin declares `Soccer:SpawnX1..7/Y/Radius` so they're owner-editable.

**Tech Stack:** plain browser JS + node tests; C# (xUnit) for the plugin keys.

**Spec:** `docs/superpowers/specs/2026-11-28-game-mode-phase3-labeller-design.md` (approved 2026-11-28).

## Global Constraints

- **Format** (from SubspaceServer `ExtendedLvl.cs` / `MapRegion.cs`):
  - The eLVL block is `"elvl"` u32 magic, a u32 total size (header included), and u32 reserved 0, then chunks.
  - A chunk is a 4-byte ASCII type, a u32 size, then data, padded with zeros to a multiple of 4. The padding isn't counted in size.
  - `REGN` holds sub-chunks: `rNAM` (ASCII name), `rTIL` (RLE tiles), and empty markers `rBSE`, `rNAW`, `rNWP`, `rNFL`.
  - Our own sub-chunk is `rDGM`: UTF-8 JSON `{"v":1,"kind":…, "team"?:0-3, "index"?:n, "x"?,"y"?,"r"?}`.
- **`rTIL` RLE:**
  - Each byte has op = bits 7–6 and run length n.
  - If bit 5 is 0, n = (bits 4–0) + 1.
  - If bit 5 is 1, n = ((bits 1–0) << 8 | next byte) + 1, so n can be up to 1024.
  - Ops: 0 = n empty, 1 = n present, 2 = n empty rows (only at row start), 3 = repeat the previous row n times (only at row start, not on row 0).
  - Every row ends at x = 1024, and the data must end at row 1024 exactly.
- Unknown chunks and unknown regions are kept byte for byte. A map whose labels weren't touched is written back byte-identical.
- **Label kinds:** `spawn` (team 0–3, point + radius), `flagspawn` (point + radius), `ballspawn` (index 0–7, point + radius), `base` (team, rectangle), `flagspot` (index, one tile), `area` (rectangle; `noWeapons`, `noAntiwarp`, `noFlagDrops`).
- Point kinds still get a region (`rTIL` = the point's tile) so the label travels with the map, and its `rDGM` carries x, y and r. The server ignores the unused region.
- **Settings written on Publish** (positions in tiles):
  - team N → `Spawn:TeamN-X/Y/Radius`;
  - flag spawn → `Flag:SpawnX/Y/Radius`;
  - ball 0 → `Soccer:SpawnX/Y/Radius`, ball k ≥ 1 → `Soccer:SpawnXk/SpawnYk/SpawnRadiusk`.
  - Check the exact `Spawn:` key names and the ball radius key against the server source while building, and ledger what they are.
- Text with `textContent` only. No `alert`/`confirm`/`prompt`. Commits end with the `Co-Authored-By` line.

## Review Focus

1. **A DCME map with irregular regions and an autowarp chunk is opened and published without touching labels.** The prefix bytes are identical. Pinned in Task 1 (`untouched_prefix_is_identical`).
2. **A label at the map edge, or a rectangle reaching x=1023 or y=1023.** The rows end exactly at 1024, so the server's reader accepts it. Pinned in Task 1 (`edge_rectangles_round_trip`).
3. **Two labels with the same name.** Region names must be unique to the server (its dictionary is case-insensitive), so names are made unique on save ("Red base 2"). Pinned in Task 3 (browser) and Task 1 (`unique_names`).
4. **A map with no BMP, only an eLVL block, or a plain tiles-only map with no prefix.** Labels add an eLVL block with offsets right. Pinned in Task 1 (`adds_block_to_bare_map`).
5. **Publish when the owner can't write a spawn key.** The skipped rows are shown and the map publish still succeeds. Pinned in Task 3 (browser).

---

### Task 1: `elvl.js` region read/write

**Files:** create `src/Dare.Arenas/Web/wwwroot/elvl.js` (UMD, global `Elvl`; in node it requires `./tiles.js`, in the browser it uses the global `LvlFile`) and `tests/js/elvl.test.js`.

**Interfaces:**
- Produces:
  - `Elvl.regions(prefix: Uint8Array|null) → [{ name, tiles: Set<number> (y*1024+x), base, noAntiwarp, noWeapons, noFlagDrops, meta: object|null, raw: Uint8Array }]`
  - `Elvl.withRegions(prefix, regions) → Uint8Array`: replaces every `REGN` chunk with `regions` in order, keeps other chunks, and returns a new prefix (BMP + eLVL, or a bare eLVL when there's no BMP). A region carrying an unchanged `raw` (nothing edited) is written back from `raw` exactly.
  - `Elvl.encodeTiles(Set) → Uint8Array` and `Elvl.decodeTiles(Uint8Array) → Set|null`: decode is a straight port of `ReadRunLengthEncodedTileData`, returning null where the server returns false.
  - `Elvl.rect(x, y, w, h) → Set`
  - `Elvl.uniqueNames(regions) → regions`: renames clashes, comparing case-insensitively.
- [ ] **Step 1:** Tests:
  - `encode_decode_round_trip`: a rectangle, an L shape, a single tile, the whole map, and the empty set (the empty set encodes as op 2 runs only).
  - `edge_rectangles_round_trip`
  - `decoder_rejects_short_data`
  - `regions_round_trip_with_flags_and_meta`
  - `untouched_prefix_is_identical`: build a prefix with a BMP stub, an `ATTR` chunk, and a `REGN` with `rAWP` and odd padding; `withRegions(p, regions(p))` is equal byte for byte.
  - `adds_block_to_bare_map`: a null prefix → an `elvl` block; `Lvl`-style tile offset = total size.
  - `bmp_offsets_fixed`: bfSize and the reserved field point right after rewriting.
  - `unique_names`
- [ ] **Step 2:** Run `node --test tests/js/elvl.test.js`. It fails: module missing.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `node --test tests/js/*.test.js`. All pass.
- [ ] **Step 5:** Commit "Map regions: elvl.js reads and writes eLVL regions (server's rTIL format), keeps other chunks".

### Task 2: Plugin declares ball spawn keys 2–8

**Files:** modify `src/Dare.Arenas/ArenaOwnership.GameMode.cs`.
- Add `[ConfigHelp<int>("Soccer", "SpawnX1", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 2 spawns (x, tiles).")]` and the same for `SpawnY1`.
- Add `SpawnRadius1` (0–1024) for k = 1..7, with the exact key names checked in Balls.cs.
- Put them on a method that's called, the way `GameMode:Attach` is.
- [ ] **Step 1:** Write the attributes. Build, and run `dotnet test`.
- [ ] **Step 2:** On the local zone, `/api/settings/preview` with `Soccer:SpawnX3 = 500` gives a `change` row, not `skipped`.
- [ ] **Step 3:** Commit "Owners can place balls 2–8 (Soccer:SpawnXk/Yk/Radiusk documented)".

### Task 3: `labels-layer.js` panel, editor hook, Publish settings, Game mode note

**Files:**
- Create `src/Dare.Arenas/Web/wwwroot/labels-layer.js` and `tests/js/labels.test.js`. The pure part goes on `window.GameLabels` / `module.exports`: `fromRegions`, `toRegions` and `spawnSettings`.
- Modify `editor.js`:
  - `GameLabels.mount(host, { get: () => ({ prefix, arena }), set: (p, note) => { prefix = p; setDirty(true); draw(); say(note, true); } })` next to `TileArt.mount`;
  - after a successful upload in `publish()`, `await GameLabels.publishSettings(arena)` when present, and add its message to the publish message.
- Modify `map.html` (`<section id="labelspanel">` after `#lvzpanel`; scripts `tiles.js`, `elvl.js` and `labels-layer.js` after `lvz-layer.js`) and `style.css` (`.lb*`).
- Modify `WebServer.cs`: serve `elvl.js` and `labels-layer.js`, add them to the embedded list, and make sure `tiles.js` is served on map.html's path (it already is, as `/arena/tiles.js`).
- Modify `gamemode.js`: the Flags / ball panel reads the arena's map (already fetched for map checks) with `Elvl.regions`, and says where flags and balls appear and how many bases and flag spots there are. Add `elvl.js` (and `tiles.js`, already there) to arena.html.

**Interfaces:**
- `fromRegions(regions) → labels`. Regions with our `meta` become typed labels; others become `{ kind: "foreign", region }`, shown read-only except name and the three rule switches.
- `toRegions(labels) → regions`. Kinds are mapped as in the Global Constraints, `uniqueNames` is applied, and foreign regions keep `raw` unless edited.
- `spawnSettings(labels) → { "Section:Key": "value" }`.
- Panel:
  - Show/Edit toggles; the list; Add menu with the six kinds and their "works now" / "Phase 4" hints; a form for the selected label.
  - Placing: point = click, then drag the ring for the radius; rectangle = drag; flag spot = click.
  - Move by dragging; Delete removes. Esc and Delete/Backspace are caught in a capture-phase keydown listener while Edit is on (auto-tiler's note). Pointer events are taken only while Edit is on and a label tool or a label is under the pointer.
  - Counts line: turf flag tiles, goal tiles, labels, regions from other editors.
- Every edit calls `set(Elvl.withRegions(prefix, toRegions(labels)), note)`, so undo, publish, download and library work unchanged.

- [ ] **Step 1:** `labels.test.js`:
  - `labels_regions_round_trip` for each kind;
  - `foreign_regions_kept`;
  - `spawn_settings_keys` (team 2 → `Spawn:Team2-X`…; ball 3 → `Soccer:SpawnX2`…; flag spawn → `Flag:SpawnX`…).
  - Run them: they fail. Implement the pure part, then run them: they pass.
- [ ] **Step 2:** Build the panel and the hooks.
- [ ] **Step 3: Browser on the local zone:**
  - place each kind and publish;
  - `arena.conf` has the spawn keys;
  - the zone log shows "N regions" for the map, and a bot flies into a no-weapons area;
  - reopen the editor and the labels are back;
  - download the .lvl and re-add it to the library, and the labels survive;
  - the Game mode tab shows the flag spawn and base counts;
  - at 375 px there's no horizontal scroll in the panel.
- [ ] **Step 4:** Commit "Map editor: Game labels panel (spawns, bases, flag spots, areas as map regions; spawn settings on Publish)".

### Task 4: Review, merge, push

- [ ] Run `dotnet test` and `node --test tests/js/*.test.js`.
- [ ] Final whole-branch review, then the fix pass.
- [ ] Merge `origin/main`, retest, `git push origin HEAD:main`. No deploy without Chris (the plugin change means a zone restart).
