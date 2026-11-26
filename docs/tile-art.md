# Tile art: generated wall tiles that join

Maps get wall art from the tileset at the start of their `.lvl`. Some maps paint slots one flat
colour (their real art came from an `.lvz` we don't have), so their walls show as solid blocks.
The map editor's **Tile art** panel fixes that:

- **Paint N blank slots**: each flat-colour wall slot gets the piece its walls call for (worked out
  from how the map places that slot: which of its eight neighbours are walls), so bars run on,
  corners turn and ends close. Slots that touch each other in the map share a style.
- **Restyle all walls**: every wall slot is redrawn: first the standard layout's packs
  (`tile-layout.json`), then every slot the map uses, as the piece its walls call for. Works on maps
  that don't follow the standard layout. Doors, safe zone, goal and fly-over/under (162-190) keep
  the map's own art (or get generated art when the map has no tileset).
- **Put old tiles back** undoes either. Nothing changes in game until **Publish**.

The new tileset is written as a 24-bit BMP; the map's eLVL data (regions, attributes) is kept.

## Styles

Mixed themes (Foundry, Station, Ruins: one style per structure, and per pack of the standard
layout) or one style: steel, bronze and navy plate, green / cyan / white / red lit panels, stone,
hazard stripes, ice crystal.

## How pieces join

`ships/tilegen.py` draws every pixel from its distance to the nearest outline (closed sides, and
inner corners where a diagonal is missing), which way that outline faces, and its position in the
tile. That field carries on across tile edges exactly as on one big picture, so pieces join with
no seam; `test_tilegen.py` checks it against a distance transform of whole random maps. There are
47 different pieces (a diagonal matters only when both sides beside it are walls).

Standard-layout roles only give the four sides, so pieces for a role have their inner corners
filled (a 3×3 block reads as one plate).

## Files

- `ships/tilegen.py`: styles, pieces, sheets; `python3 tilegen.py` regenerates the outputs below.
- `ships/tilesets/*.bmp`: sample tilesets in the standard layout (foundry, station, ruins) with
  previews; `styles-preview.png` shows every style's 47 pieces.
- `src/Dare.Arenas/Web/wwwroot/tileart-styles.js`: generated; every style's pieces for the site.
- `src/Dare.Arenas/Web/wwwroot/tileart.js`: slot inference, plans, BMP writing, the panel.
- Tests: `cd ships && python3 -m unittest test_tilegen` (Pillow), `node --test tests/js/*.test.js`.

Adding a style: add a `Style` (and a shader if it's a new kind) to `STYLES`, run `tilegen.py`.
Replacing the standard layout: replace `tile-layout.json`; sheets and the editor read it.
