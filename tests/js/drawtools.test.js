// Node tests for the map editor's drawing tools (shapes, text, clips, auto-tiled drawing):
//   node --test tests/js/*.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const www = path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot");
require(path.join(www, "autotile.js"));
require(path.join(www, "drawtools.js"));
const { Autotile, DrawTools } = globalThis;
const S = DrawTools.Shapes;
const N = 1024;
const xy = (i) => [i % N, Math.floor(i / N)];
const sorted = (set) => [...set].sort((a, b) => a - b).map(xy);
const at = (x, y) => y * N + x;
// the cells as a picture over a box
function picture(set, x0, y0, w, h) {
  const rows = [];
  for (let y = y0; y < y0 + h; y++) { let r = ""; for (let x = x0; x < x0 + w; x++) r += set.has(at(x, y)) ? "#" : "."; rows.push(r); }
  return rows;
}
// every cell has an edge neighbour in the set (a one-tile wall that stays joined)
function edgeJoined(set) {
  for (const i of set) {
    const [x, y] = xy(i);
    if (set.size > 1 && ![at(x + 1, y), at(x - 1, y), at(x, y + 1), at(x, y - 1)].some((j) => set.has(j))) return false;
  }
  return true;
}
function connected(set) {
  const [first] = set, seen = new Set([first]), q = [first];
  while (q.length) {
    const [x, y] = xy(q.pop());
    for (const j of [at(x + 1, y), at(x - 1, y), at(x, y + 1), at(x, y - 1)]) if (set.has(j) && !seen.has(j)) { seen.add(j); q.push(j); }
  }
  return seen.size === set.size;
}
const byTile = new Map(Object.entries(Autotile.STAND_IN.roles).map(([r, t]) => [t, r]));

test("lines join edge to edge at any angle, so a one-tile wall is one wall", () => {
  for (const [x1, y1] of [[20, 0], [0, 20], [20, 20], [20, 7], [7, 20], [-13, 5], [3, -17]]) {
    const l = S.line(100, 100, 100 + x1, 100 + y1);
    assert.ok(l.has(at(100, 100)) && l.has(at(100 + x1, 100 + y1)), "both ends");
    assert.ok(connected(l), `connected to ${x1},${y1}`);
  }
  assert.equal(S.line(10, 10, 20, 10).size, 11);
  assert.equal(S.line(10, 10, 20, 10, 3).size, 13 * 3, "thickness widens it (a square brush, so the ends grow too)");
});

test("a rectangle's rim is the thickness deep, inward; filled is solid", () => {
  assert.deepEqual(picture(S.rect(10, 10, 15, 14), 10, 10, 6, 5), ["######", "#....#", "#....#", "#....#", "######"]);
  assert.deepEqual(picture(S.rect(15, 14, 10, 10, 2), 10, 10, 6, 5), ["######", "######", "##..##", "######", "######"]);
  assert.equal(S.rect(10, 10, 15, 14, 1, true).size, 30);
});

test("ellipses and rounded boxes close up, one tile wide, edge-joined", () => {
  for (const [w, h] of [[10, 10], [30, 12], [7, 21], [3, 3]]) {
    const e = S.ellipse(100, 100, 100 + w, 100 + h);
    assert.ok(edgeJoined(e) && connected(e), `ellipse ${w}x${h}`);
    const r = S.roundRect(100, 100, 100 + w, 100 + h, 3);
    assert.ok(edgeJoined(r) && connected(r), `rounded ${w}x${h}`);
  }
  const c = S.ellipse(0, 0, 20, 20, 1, true);
  assert.ok(c.has(at(10, 10)) && !c.has(at(0, 0)), "a filled circle: middle in, corner out");
  assert.ok(!S.roundRect(0, 0, 20, 20, 5).has(at(0, 0)), "rounded corners are cut");
  assert.ok(S.roundRect(0, 0, 20, 20, 0).has(at(0, 0)), "radius 0 is a plain box");
});

test("polygons, curves and paths", () => {
  const tri = S.polygon([[100, 100], [130, 100], [100, 130]], 1, true);
  assert.ok(tri.has(at(105, 105)) && !tri.has(at(125, 125)));
  const rim = S.polygon([[100, 100], [130, 100], [100, 130]]);
  assert.ok(connected(rim) && !rim.has(at(105, 105)));
  const cv = S.curve([100, 100], [140, 100], [120, 60]);
  assert.ok(connected(cv) && cv.has(at(100, 100)) && cv.has(at(140, 100)));
  assert.ok(![...cv].some((i) => xy(i)[1] > 100), "bends toward the control point");
  assert.ok(connected(S.path([[1, 1], [9, 4], [3, 12]], 2)));
});

test("mirror copies about the middle of the map", () => {
  const one = new Set([at(10, 20)]);
  assert.deepEqual(sorted(S.mirror(one, "h")), [[10, 20], [1013, 20]]);
  assert.deepEqual(sorted(S.mirror(one, "v")), [[10, 20], [10, 1003]]);
  assert.equal(S.mirror(one, "hv").size, 4);
  assert.equal(S.mirror(one, "none"), one);
});

test("text in blocky tiles, at a size", () => {
  const t = S.text("HI", 0, 0);
  assert.deepEqual(picture(t.cells, 0, 0, 11, 7), [
    "#...#.#####", "#...#...#..", "#...#...#..", "#####...#..", "#...#...#..", "#...#...#..", "#...#.#####"]);
  assert.equal(t.w, 11); assert.equal(t.h, 7);
  const big = S.text("I", 0, 0, 3);
  assert.equal(big.cells.size, S.text("I", 0, 0).cells.size * 9);
  assert.equal(S.text("A\nB", 0, 0).h, 16, "a second line");
  for (const ch of Object.keys(DrawTools.FONT)) for (const row of DrawTools.FONT[ch]) assert.match(row, /^[01]{5}$/, ch);
});

test("flood fill stops at walls and refuses to run across the map", () => {
  const g = new Uint8Array(N * N);
  for (const i of S.rect(100, 100, 110, 110)) g[i] = 1;
  const inside = S.flood(g, 105, 105);
  assert.equal(inside.size, 81);
  assert.equal(S.flood(g, 5, 5, 1000), null);
  assert.equal(S.flood(g, 100, 100, 1000, "walls").size, 40, "a wall and the walls joined to it");
});

test("clips flip and turn", () => {
  const g = new Uint8Array(N * N);
  g[at(10, 10)] = 1; g[at(11, 10)] = 2; g[at(10, 11)] = 3;
  const clip = S.copy(g, new Set([at(10, 10), at(11, 10), at(10, 11)]));
  assert.deepEqual([clip.w, clip.h, clip.x, clip.y], [2, 2, 10, 10]);
  assert.deepEqual([...S.transform(clip, "h").tiles], [2, 1, 0, 3]);
  assert.deepEqual([...S.transform(clip, "v").tiles], [3, 0, 1, 2]);
  assert.deepEqual([...S.transform(clip, "rot").tiles], [3, 1, 0, 2]);
  const put = S.stamp(S.transform(clip, "h"), 50, 50);
  assert.equal(put.size, 3); assert.equal(put.get(at(50, 50)), 2); assert.ok(!put.has(at(50, 51)), "masked out");
});

test("drawn shapes are auto-tiled: corners, edges, caps; walls they touch re-join", () => {
  const L = Autotile.STAND_IN;
  const g = new Uint8Array(N * N);
  S.apply(g, S.fill(S.rect(100, 100, 104, 103), 1), L);
  const role = (x, y) => byTile.get(g[at(x, y)]);
  assert.deepEqual([role(100, 100), role(102, 100), role(104, 100), role(100, 102), role(104, 103)], ["corner-es", "bar-h", "corner-sw", "bar-v", "corner-nw"]);
  // a line drawn up to the box joins it: the box's edge becomes a junction, the line has one cap
  const ch = S.apply(g, S.fill(S.line(107, 101, 105, 101), 1), L);
  assert.equal(role(104, 101), "tee-nes");
  assert.equal(role(107, 101), "end-w");
  assert.ok(ch.some(([i]) => i === at(104, 101)), "the neighbour's change is in the same undo step");
  // erasing re-joins what's left: the box edge is a bar again
  S.apply(g, S.fill(S.line(105, 101, 107, 101), 0), L);
  assert.equal(role(104, 101), "bar-v");
  // a thick wall is solid: fill inside, edges round it
  S.apply(g, S.fill(S.rect(200, 200, 211, 211, 3), 1), L);
  assert.equal(role(201, 201), "fill");
  assert.equal(role(205, 200), "edge-n");
  assert.equal(role(202, 202), "inner-se");
});

test("re-joining leaves walls of no layout alone (decor, text drawn by hand)", () => {
  const g = new Uint8Array(N * N);
  g[at(99, 100)] = 150; // not a stand-in tile? make sure it isn't
  assert.equal(Autotile.owner(150), null);
  S.apply(g, S.fill(S.line(100, 100, 103, 100), 1), Autotile.STAND_IN);
  assert.equal(g[at(99, 100)], 150);
  assert.equal(byTile.get(g[at(100, 100)]), "bar-h", "but the new wall still joins it");
});

test("a pack's plain interior piece is used inside thick walls", () => {
  const spec = { format: "dare-tile-layout", version: 1, roles: { s: { open: "" }, all: { open: "NESW" }, tl: { open: "ES" }, top: { open: "ESW" } },
    packs: [{ id: "p", tiles: { s: 2, all: 3, tl: 4, top: 5 }, interior: 9 }] };
  const [l] = Autotile.fromSpec(spec).layouts;
  assert.equal(l.roles.fill, 9);
  assert.equal(l.roles.cross, 3, "a crossing of lines keeps the pack's piece");
  assert.equal(l.roles["inner-ne"], 3, "inside corners sit among the edges: the pack's fill");
});
