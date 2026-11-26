// Node tests for the map editor's generator and wall auto-tiler (no browser needed):
//   node --test tests/web
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const www = path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot");
require(path.join(www, "autotile.js"));
require(path.join(www, "lvlgen.js"));
const { Autotile, Lvl } = globalThis;
const N = 1024;

// a grid from a picture: '#' = wall (tile 1), anything else empty; drawn at (100, 100)
function draw(rows) {
  const g = new Uint8Array(N * N);
  rows.forEach((row, y) => [...row].forEach((ch, x) => { if (ch === "#") g[(100 + y) * N + 100 + x] = 1; }));
  return g;
}
// the role names back as a picture (walls only), using the stand-in layout's tile -> role
const byTile = new Map(Object.entries(Autotile.STAND_IN.roles).map(([r, t]) => [t, r]));
function roles(g, rows) {
  return rows.map((row, y) => [...row].map((ch, x) => (ch === "#" ? byTile.get(g[(100 + y) * N + 100 + x]) : null)));
}
function tidied(rows, layout = Autotile.STAND_IN) {
  const g = draw(rows);
  Autotile.tidy(g, layout);
  return roles(g, rows);
}

test("each neighbour pattern gets its line role", () => {
  const { B } = Autotile;
  const want = {
    0: "lone", [B.n]: "end-n", [B.e]: "end-e", [B.s]: "end-s", [B.w]: "end-w",
    [B.n | B.s]: "bar-v", [B.e | B.w]: "bar-h",
    [B.n | B.e]: "corner-ne", [B.e | B.s]: "corner-es", [B.s | B.w]: "corner-sw", [B.n | B.w]: "corner-nw",
    [B.n | B.e | B.s]: "tee-nes", [B.e | B.s | B.w]: "tee-esw", [B.n | B.s | B.w]: "tee-nsw", [B.n | B.e | B.w]: "tee-new",
    [B.n | B.e | B.s | B.w]: "cross",
  };
  for (const [m, role] of Object.entries(want)) assert.equal(Autotile.roleOf(Number(m)), role, `mask ${m}`);
  // diagonals don't change a line's role where they don't matter
  assert.equal(Autotile.roleOf(B.n | B.s | B.ne | B.sw), "bar-v");
  assert.equal(Autotile.roleOf(B.e | B.ne | B.se), "end-e");
});

test("a straight run gets end caps and middles, both ways", () => {
  assert.deepEqual(tidied(["#####"])[0], ["end-e", "bar-h", "bar-h", "bar-h", "end-w"]);
  assert.deepEqual(tidied(["#", "#", "#"]).map((r) => r[0]), ["end-s", "bar-v", "end-n"]);
  assert.deepEqual(tidied(["#"])[0], ["lone"]);
});

test("turns get the matching corner and junctions the matching connector", () => {
  const r = tidied([
    "###.",
    "#.#.",
    "####",
    "..#.",
  ]);
  assert.equal(r[0][0], "corner-es");
  assert.equal(r[0][2], "corner-sw");
  assert.equal(r[2][0], "corner-ne");
  assert.equal(r[2][2], "cross");
  assert.equal(r[2][3], "end-w");
  assert.equal(r[3][2], "end-n");
  const t = tidied(["###", ".#."]);
  assert.deepEqual(t[0], ["end-e", "tee-esw", "end-w"]);
});

test("thick shapes get outer corners, edges, fill and inside corners", () => {
  const r = tidied([
    "####",
    "####",
    "####",
    "##..",
  ]);
  assert.deepEqual(r[0], ["block-es", "edge-n", "edge-n", "block-sw"]);
  assert.equal(r[1][0], "edge-w");
  assert.equal(r[1][1], "fill");
  assert.equal(r[2][1], "inner-se");
  assert.equal(r[2][3], "block-nw");
  assert.equal(r[3][0], "block-ne");
  // a 2x2 is four outer corners
  assert.deepEqual(tidied(["##", "##"]), [["block-es", "block-sw"], ["block-ne", "block-nw"]]);
});

test("a layout without shape roles falls back to line roles", () => {
  const lines = { id: "lines", fallback: 9, roles: { "bar-h": 2, "bar-v": 3, "corner-es": 4, "corner-sw": 5, "corner-ne": 6, "corner-nw": 7, "tee-esw": 8, cross: 10 } };
  const g = draw(["###", "###"]);
  Autotile.tidy(g, lines);
  const at = (x, y) => g[(100 + y) * N + 100 + x];
  assert.equal(at(0, 0), 4); // block-es -> corner-es
  assert.equal(at(1, 0), 8); // edge-n -> tee-esw
  assert.equal(at(2, 1), 7);
  // no role and no fallback role: the layout's fallback tile
  const g2 = draw(["#"]);
  Autotile.tidy(g2, lines);
  assert.equal(g2[100 * N + 100], 9);
});

test("variants spread over a run, and tidying is stable", () => {
  const layout = { id: "v", roles: { "bar-h": [11, 12, 13] } };
  const g = draw(["." + "#".repeat(40)]);
  Autotile.tidy(g, layout);
  const used = new Set();
  for (let x = 102; x < 140; x++) used.add(g[100 * N + x]);
  assert.ok(used.size >= 2, "uses more than one variant");
  assert.equal(Autotile.tidy(g, layout).length, 0, "a second tidy changes nothing");
});

test("tidy only touches its area and leaves specials alone", () => {
  const g = draw(["#####"]);
  g[100 * N + 106] = 170; // a flag
  const changes = Autotile.tidy(g, Autotile.STAND_IN, [100, 100, 101, 100]);
  assert.equal(changes.length, 2);
  assert.equal(g[100 * N + 102], 1, "outside the area: untouched");
  assert.equal(byTile.get(g[100 * N + 101]), "bar-h", "neighbours outside the area still count");
  assert.equal(g[100 * N + 106], 170);
});

test("bad layouts are refused", () => {
  assert.ok(Autotile.check({ roles: { "bar-x": 1 } }).length);
  assert.ok(Autotile.check({ roles: { lone: 200 } }).length);
  assert.deepEqual(Autotile.check(Autotile.STAND_IN), []);
  assert.throws(() => Autotile.register({ id: "bad", roles: { lone: 0 } }));
});

// ---- generator ---------------------------------------------------------------------------------

function reachableFromCentre(cells, C) {
  const mid = Math.floor((C - 1) / 2), seen = new Uint8Array(C * C), q = [mid * C + mid];
  assert.equal(cells[q[0]], 0, "the centre is open");
  seen[q[0]] = 1;
  while (q.length) {
    const i = q.pop(), x = i % C, y = (i - x) / C;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy, j = ny * C + nx;
      if (nx < 0 || ny < 0 || nx >= C || ny >= C || seen[j] || cells[j]) continue;
      seen[j] = 1; q.push(j);
    }
  }
  return seen;
}

for (const layout of Object.keys(Lvl.GEN_LAYOUTS)) {
  for (const symmetry of [1, 2, 4]) {
    test(`generate ${layout}, ${symmetry}-way: playable, closed, symmetric`, () => {
      for (const seed of [1, 7, 42, 9001]) {
        for (const size of ["small", "large"]) {
          const g = Lvl.generate(seed, { layout, symmetry, size, density: 0.3 + (seed % 3) * 0.1 });
          const { cells, C } = g;
          const seen = reachableFromCentre(cells, C);
          let open = 0;
          for (let i = 0; i < C * C; i++) if (!cells[i]) { open++; assert.ok(seen[i], `${layout} seed ${seed}: open cell ${i % C},${Math.floor(i / C)} is sealed off`); }
          assert.ok(open > C * C * 0.15, `${layout} seed ${seed}: enough room to fly (${open} of ${C * C})`);
          for (let k = 0; k < C; k++) for (const i of [k, (C - 1) * C + k, k * C, k * C + C - 1]) assert.equal(cells[i], 1, "border closed");
          const sym = g.symmetry;
          for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
            if (sym >= 2) assert.equal(cells[y * C + x], cells[y * C + C - 1 - x], `${layout} seed ${seed}: mirrored left-right`);
            if (sym === 4) assert.equal(cells[y * C + x], cells[(C - 1 - y) * C + x], `${layout} seed ${seed}: mirrored top-bottom`);
          }
          for (const s of g.specials) assert.equal(cells[s.y * C + s.x], 0, "specials sit in open space");
        }
      }
    });
  }
}

test("a seed always gives the same map; the team arena has a flag each side", () => {
  const a = Lvl.generate(5, { layout: "base", flags: true }), b = Lvl.generate(5, { layout: "base", flags: true });
  assert.deepEqual(a.grid, b.grid);
  const t = Lvl.generate(3, { layout: "teams", symmetry: 4 });
  assert.equal(t.symmetry, 2);
  const flags = t.specials.filter((s) => s.tile === 170);
  assert.equal(flags.length, 2);
  assert.ok(flags.some((f) => f.x < t.C / 2) && flags.some((f) => f.x > t.C / 2));
});

test("a generated map with a tile layout is fully auto-tiled", () => {
  for (const thickness of [1, 3]) {
    const g = Lvl.generate(11, { layout: "rings", thickness, tiles: Autotile.STAND_IN });
    let walls = 0;
    for (let i = 0; i < N * N; i++) {
      const t = g.grid[i];
      if (!Autotile.isPlainWall(t)) continue;
      walls++;
      const x = i % N, y = (i - x) / N;
      assert.equal(t, Autotile.resolve(Autotile.STAND_IN, Autotile.roleOf(Autotile.maskAt(g.grid, x, y)), x, y));
    }
    assert.ok(walls > 1000);
  }
});

test("one-tile walls are lines: no wall tile is part of a 2x2 block (outside the border)", () => {
  const g = Lvl.generate(21, { layout: "spokes", thickness: 1, symmetry: 4 });
  const { C } = g, T = C * 3, o = Math.floor(N / 2 - T / 2);
  let blocks = 0;
  for (let y = o + 3; y < o + T - 4; y++) for (let x = o + 3; x < o + T - 4; x++) {
    const w = (dx, dy) => g.grid[(y + dy) * N + x + dx] !== 0;
    if (w(0, 0) && w(1, 0) && w(0, 1) && w(1, 1)) blocks++;
  }
  // corners of rock that is diagonal to open space can make a few; lines are the rule
  assert.ok(blocks < 200, `${blocks} 2x2 wall blocks`);
});

// ---- the standard tile layout file (dare-tile-layout) ----------------------------------------------

// a pack in the spec's shape, with role names unlike ours: only "open" may be used to match
const SPEC = {
  format: "dare-tile-layout", version: 1, name: "Test standard (fixture)",
  roles: {
    solo: { open: "" }, l: { open: "E" }, h: { open: "EW" }, r: { open: "W" }, t: { open: "S" }, v: { open: "NS" }, b: { open: "N" },
    tl: { open: "ES" }, top: { open: "ESW" }, tr: { open: "SW" }, left: { open: "NES" }, all: { open: "NESW" },
    right: { open: "NSW" }, bl: { open: "NE" }, bottom: { open: "NEW" }, br: { open: "NW" },
  },
  packs: [
    { id: "full", name: "Full", tiles: { solo: 2, l: 3, h: 4, r: 5, t: 21, v: 40, b: 59, tl: 22, top: 23, tr: 24, left: 41, all: 42, right: 43, bl: 60, bottom: 61, br: 62 } },
    { id: "bars", name: "Bars only", tiles: { h: 7, v: 8, all: 9 } },
  ],
};

test("the standard layout file gives one layout per pack, matched by open edges", () => {
  const { layouts, errors } = Autotile.fromSpec(SPEC);
  assert.deepEqual(errors, []);
  assert.deepEqual(layouts.map((l) => l.id), ["full", "bars"]);
  const full = layouts[0];
  assert.equal(full.name, "Test standard: Full");
  const want = { lone: 2, "end-e": 3, "bar-h": 4, "end-w": 5, "end-s": 21, "bar-v": 40, "end-n": 59, "corner-es": 22, "block-es": 22,
    "tee-esw": 23, "edge-n": 23, "corner-sw": 24, "tee-nes": 41, "edge-w": 41, cross: 42, fill: 42, "inner-ne": 42,
    "tee-nsw": 43, "corner-ne": 60, "tee-new": 61, "edge-s": 61, "corner-nw": 62, "block-nw": 62 };
  for (const [role, t] of Object.entries(want)) assert.equal(full.roles[role], t, role);
  // a box drawn with it: corners, edges and fill where the spec's 3x3 box has them
  const g = draw(["###", "###", "###"]);
  Autotile.tidy(g, full);
  const at = (x, y) => g[(100 + y) * N + 100 + x];
  assert.deepEqual([at(0, 0), at(1, 0), at(2, 0), at(0, 1), at(1, 1), at(2, 1), at(0, 2), at(1, 2), at(2, 2)], [22, 23, 24, 41, 42, 43, 60, 61, 62]);
});

test("a pack missing pieces drops open edges like the spec says (straight bar first)", () => {
  const bars = Autotile.fromSpec(SPEC).layouts[1];
  assert.equal(bars.roles["end-e"], 9, "no end piece and no single: fill");
  assert.equal(bars.roles["tee-nes"], 8, "NES keeps its NS through line");
  assert.equal(bars.roles["tee-esw"], 7, "ESW keeps its EW through line");
  assert.equal(bars.roles.cross, 9);
  assert.equal(bars.roles["corner-ne"], 9, "no piece for a turn or its ends: fill");
  assert.deepEqual(Autotile.fromSpec({ format: "other" }).layouts, []);
});

test("the real tile-layout.json, when present, gives usable layouts", (t) => {
  const file = path.join(www, "tile-layout.json");
  if (!require("node:fs").existsSync(file)) return t.skip("tile-layout.json not on this branch yet");
  const { layouts, errors } = Autotile.fromSpec(JSON.parse(require("node:fs").readFileSync(file, "utf8")));
  assert.deepEqual(errors, []);
  assert.ok(layouts.length >= 1);
  for (const l of layouts) assert.deepEqual(Autotile.check(l), [], l.id);
});
