# Tile layout ("tile roles")

A Continuum tileset is the BMP at the start of a `.lvl`: 19 × 10 tiles of 16 px (304 × 160, 8-bit).
Map tile values 1–190 point into it: slot = value − 1, column = slot % 19, row = slot // 19.
Values 162–190 are doors, flag, safe zone, goal, fly-over and fly-under: never wall pieces.

The **tile layout** says which slot is which piece of a wall, so tools (map generation, the
tile editor, tileset drawing) can build walls that run in a continuous line, turn properly,
join at T-junctions and crosses, and end properly.

The layout lives in one file: [`src/Dare.Arenas/Web/wwwroot/tile-layout.json`](../src/Dare.Arenas/Web/wwwroot/tile-layout.json),
served at `/arena/tile-layout.json`. Replace that file to change the standard; no code reads
slot numbers from anywhere else.

## Roles: a piece is defined by its open edges

An edge is **open** when the wall continues into the neighbouring cell on that side
(N up, E right, S down, W left), **closed** when the wall's outline runs along it.
Every combination of open edges is one role, 16 in all:

| role | open | piece |
|---|---|---|
| `single` | – | stand-alone block |
| `end-left` / `h-mid` / `end-right` | E / EW / W | horizontal bar |
| `end-top` / `v-mid` / `end-bottom` | S / NS / N | vertical bar |
| `corner-tl` `corner-tr` `corner-bl` `corner-br` | ES / SW / NE / NW | turns |
| `edge-top` `edge-left` `edge-right` `edge-bottom` | ESW / NES / NSW / NEW | T-junctions (the edges of a box) |
| `fill` | NESW | cross, or the inside of a box |

To place a wall cell, look at its four neighbours, write down the sides that are wall too,
and use the role whose `open` is that set. Tools must match on `open`, never on role names:
a replacement layout may name its roles differently.

In "box" styled packs (all of tactical2's) the T pieces are drawn as the straight edges of a
thick block and `fill` as its inside, so a 3 × 3 block of wall reads as a framed plate, and a
one-cell line through a junction still joins cleanly.

## A pack

One tileset holds several **packs**: a full set of the 16 roles in one style. In tactical2
every pack is a 4 × 4 block (`packTemplate`):

```
single     end-left   h-mid      end-right
end-top    corner-tl  edge-top   corner-tr
v-mid      edge-left  fill       edge-right
end-bottom corner-bl  edge-bottom corner-br
```

| pack | origin (col,row) | tiles |
|---|---|---|
| `grey-plate` | 1,0 | 2–5, 21–24, 40–43, 59–62 |
| `grey-round` | 5,0 | 6–9, 25–28, 44–47, 63–66 |
| `green-lit` | 9,0 | 10–13, 29–32, 48–51, 67–70 |
| `white-lit` | 13,0 | 14–17, 33–36, 52–55, 71–74 |
| `steel-blue` | 5,4 | 82–85, 101–104, 120–123, 139–142 |
| `navy` | 9,4 | 86–89, 105–108, 124–127, 143–146 |

The rest of the sheet is listed under `other` (decor that is not a full pack) and `reserved`.

## The file

```jsonc
{
  "format": "dare-tile-layout", "version": 1,
  "name": "…",
  "tileset": { "columns": 19, "rows": 10, "tileSize": 16 },
  "roles": { "<role>": { "open": "NESW subset", "label": "…" } },   // required
  "packTemplate": { "size": [4, 4], "cells": { "<role>": [col, row] } }, // optional
  "packs": [ { "id": "…", "name": "…", "origin": [col, row],          // origin optional
               "tiles": { "<role>": tileNumber } } ],                 // required, explicit
  "other": [ { "tile": n | "tiles": [n…], "label": "…" } ],            // optional
  "reserved": { "from": 162, "to": 190, "label": "…" }                 // optional
}
```

Rules a reader can rely on (checked by `TileLayout.parse` in
[`tiles.js`](../src/Dare.Arenas/Web/wwwroot/tiles.js); tests: `node --test tests/js/*.test.js`):
- every pack lists its tiles explicitly; `packTemplate` is only a convenience for editors;
- each `open` is a subset of `NESW` and no two roles share the same set;
- tile numbers are 1–161, outside `reserved`, and no tile is in two packs;
- a pack may leave roles out (a partial pack); tools fall back as below.

**Fallbacks** when a pack lacks the exact role: drop open edges, fewest first, and take the
first role that exists; among two-edge pieces a straight bar comes before a corner, so a missing
T keeps the line running through. Else `single`, else `fill`.
`TileLayout.pick(layout, packId, openSet)` implements this.

## Role maps for one tileset

The tile editor (`/arena/tiles`) saves a **role map** for a given tileset in the same format,
so a tileset whose packs sit in other slots is described by its own file and every tool that
reads a layout reads it too.

## Replacing the standard

Write your own file in this format and put it at `src/Dare.Arenas/Web/wwwroot/tile-layout.json`
(or load it in the tile editor with "Load layout"). Role names, pack ids and positions are
all data; only the `open` sets carry meaning.
