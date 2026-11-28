# Tile art: generated wall tiles that join

Maps get wall art from the tileset at the start of their `.lvl`. Some maps paint slots one flat
colour (their real art came from an `.lvz` we don't have), so their walls show as solid blocks.
The map editor's **Tileset** panel picks the map's tileset and the walls you draw with:

- **The menu**: *This map* (its own tiles), *Your tilesets* (the Tile editor's library, the Armory,
  kept from the Foundry: `/arena/api/tilesets`), *Uploaded* (when one couldn't be kept), and
  *Defaults*: Standard (`standard-tiles.bmp`) and the generated tilesets (Foundry, Station, Ruins and
  the single styles). Picking one shows it on the map and in the Tiles palette; nothing is kept until **Apply** puts it in the map's own tileset (published with the map). **↶** puts the
  previous one back, as often as Apply was used.
- A generated tileset is drawn for this map: on a map that follows the standard layout each pack's
  slots get that pack's style and pieces; on one that doesn't, every slot the map uses is drawn as
  the piece its walls call for. Slots 162-190 keep the map's own art.
- **Walls**: the picked tileset's wall sets as swatches, named by what they look like (Foundry's
  grey-plate pack is *Steel plate*; a single style offers one). The drawing tools and **Fit pieces**
  use the chosen one. Where they come from:
  - a tileset saved with its own wall roles (Tile editor): its packs, with its names;
  - Standard and the generated tilesets: the standard packs (only those still drawn as their roles);
  - anything else (uploads, a map's own tiles): the standard packs whose tiles read as their roles
    with the tile editor's edge reader (`readOpen`, at least 60% of a pack's tiles) or match the
    standard tiles or a generated style at the same slots. They are named by look (`namesByLook`):
    packs drawn alike are offered once; a pack matching the standard tiles takes its pack name, one
    matching a generated style that style's name (a map given Green lit shows one *Green lit*),
    anything else its colour (*Green*, *Green 2*). None: "No wall sets in this tileset", a link to
    the Tile editor, and drawing places the tile you pick.
- **Apply** warns before putting in a tileset that doesn't draw the standard packs as walls on a map
  whose walls use them.
- **Fill them** (shown when the map has flat-colour wall slots): each such slot gets the piece its
  walls call for, in the picked generated style (or Foundry). ↶ undoes it.
- **Upload tileset…**: a .bmp/.bm2 (kept as 8-bit) or a .lvl's tileset, saved to Your tilesets
  when it can be.

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
- `ships/interiors.py`: plain interior pieces for the standard packs (thick walls), in tactical2 slots 128-133, and calm edge pieces (rim kept, plain inside) for grey-plate 90-93, grey-round 109-112, green-lit 147-150, steel-blue 151, 152, 94, 113 (top, left, right, bottom), and inside corners (`inner`, keyed by the open diagonal NE SE SW NW, made from the calm edges: the two rims meet square) in decor slots tactical2 barely uses: grey-plate 20, 56, 75, 76, grey-round 95, 119, 134, 135, green-lit 136, 137, 115, 153, steel-blue 154-157; writes `ships/tilesets/tactical2-interior.bmp` and the site copy `wwwroot/standard-tiles.bmp`.
- Tests: `cd ships && python3 -m unittest test_tilegen test_interiors` (Pillow), `node --test tests/js/*.test.js`.

Adding a style: add a `Style` (and a shader if it's a new kind) to `STYLES`, run `tilegen.py`.
Replacing the standard layout: replace `tile-layout.json`; sheets and the editor read it.
