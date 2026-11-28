// LvzScreen's geometry: the twelve CLV2 reference points, checked against oss-client's hud.rs
// test (the_lvz_anchors_match_continuum_at_1888x1016), and anchoring with and without snap.
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const S = require("../../src/Dare.Arenas/Web/wwwroot/lvz-screen.js");

test("the anchors match Continuum at 1888x1016, as hud.rs measured them", () => {
  const refs = S.anchors(1888, 1016, { statbox: [109, 29], chatTop: 992, radarLine: "4:53pm  A1" });
  assert.equal(S.radarDim(1888), 312);
  assert.deepEqual(refs, [
    [0, 0], [944, 508], [1888, 1016], [109, 29], [26, 460], [26, 635],
    [944, 28], [0, 992], [1570, 698], [1802, 682], [1862, 460], [1862, 610],
  ]);
  // the clock off: six characters fewer moves the radar text 48 px right
  assert.deepEqual(S.anchors(1888, 1016, { radarLine: "  A1" })[9], [1850, 682]);
  // the player list closed: reference 3 is the screen origin
  assert.deepEqual(S.anchors(1888, 1016, { statbox: null })[3], [0, 0]);
  // spectating: below-energy is the spectator line
  assert.equal(S.anchors(1888, 1016, { spectating: true })[6][1], 12);
});

test("chat's top follows its line count; anchors scale with the window", () => {
  assert.equal(S.chatRows(768), 8);
  assert.equal(S.anchors(1024, 768)[7][1], 768 - 16 - 8 * 12);
  const a = S.anchors(640, 480), b = S.anchors(1920, 1080);
  assert.deepEqual(a[2], [640, 480]); assert.deepEqual(b[1], [960, 540]);
  assert.ok(b[8][0] > a[8][0], "the radar moves right on a wider screen");
});

test("place adds the offset to its reference point, x from xRef and y from yRef", () => {
  const refs = S.anchors(1024, 768);
  assert.deepEqual(S.place({ xRef: 2, yRef: 2, x: -40, y: -20 }, refs), [984, 748]);
  assert.deepEqual(S.place({ xRef: 1, yRef: 0, x: -16, y: 5 }, refs), [496, 5]);
});

test("anchorBox snaps the nearest handle onto a reference point, else anchors to the nearest point", () => {
  const refs = S.anchors(1024, 768);
  // a 32x32 box whose centre is 5 px off the screen centre snaps centred on it
  const c = S.anchorBox(512 - 16 + 5, 384 - 16 - 3, 32, 32, refs, 24);
  assert.deepEqual(c, { xRef: 1, yRef: 1, x: -16, y: -16, snapped: true });
  // near the bottom-right corner: its bottom-right handle lands on reference 2
  const br = S.anchorBox(1024 - 40 - 6, 768 - 20 - 4, 40, 20, refs, 24);
  assert.deepEqual(br, { xRef: 2, yRef: 2, x: -40, y: -20, snapped: true });
  // no snap: stays put, each axis anchored to the nearest screen edge or centre (top-left here)
  const free = S.anchorBox(100, 120, 32, 32, refs, 0);
  assert.deepEqual(free, { xRef: 0, yRef: 0, x: 100, y: 120, snapped: false });
  // near the top-right, free: x from the right edge, y from the top
  assert.deepEqual(S.anchorBox(950, 10, 40, 20, refs, 0), { xRef: 2, yRef: 0, x: -74, y: 10, snapped: false });
  // it keeps its place: re-placing gives the same pixels
  assert.deepEqual(S.place(free, refs), [100, 120]);
  // offsets are clamped to CLV2's signed 12 bits
  const far = S.anchorBox(-5000, 3000, 8, 8, [[0, 0]], 0);
  assert.deepEqual([far.x, far.y], [S.OFFSET_MIN, S.OFFSET_MAX]);
});

test("snapBox lines an image up with the screen centre, the HUD and other images, else the grid", () => {
  const W = 1024, H = 768, gl = S.guides(W, H);
  // 40x20 image whose centre is 3 px right of the screen centre: centred exactly
  let r = S.snapBox(512 - 20 + 3, 250.4, 40, 20, { xs: gl.xs, ys: gl.ys, threshold: 6 });
  assert.equal(r.x, 492); assert.equal(r.gx.line.label, "screen centre");
  assert.equal(r.gy, null); assert.equal(r.y, 250, "whole pixels when nothing is near");
  // the grid when no guide is near
  r = S.snapBox(301, 203, 10, 10, { xs: [], ys: [], threshold: 6, grid: 8 });
  assert.deepEqual([r.x, r.y], [304, 200]);
  // the radar's left edge: right edge of the image on it
  const radarLeft = gl.xs.find((g) => g.label === "radar left");
  r = S.snapBox(radarLeft.v - 30 - 4, 500, 30, 10, { xs: gl.xs, ys: [], threshold: 6 });
  assert.equal(r.x + 30, radarLeft.v); assert.equal(r.gx.line.ref, 8);
  // another image's top edge
  const others = [{ x: 100, y: 100, w: 50, h: 50 }];
  r = S.snapBox(400, 103, 10, 10, { xs: [], ys: [], others, threshold: 6 });
  assert.equal(r.y, 100); assert.ok(r.gy.line.image);
  // off: nothing but whole pixels
  r = S.snapBox(509.6, 100.2, 40, 20, { xs: gl.xs, ys: gl.ys, threshold: 0 });
  assert.deepEqual([r.x, r.y, r.gx, r.gy], [510, 100, null, null]);
});

test("snapBox spaces an image evenly: centred between two, or the same gap as its neighbours", () => {
  const row = [{ x: 100, y: 50, w: 20, h: 20 }, { x: 200, y: 50, w: 20, h: 20 }];
  // between 120 and 200 a 20-wide image is centred with 30 px either side: x 150
  let r = S.snapBox(147, 52, 20, 20, { xs: [], ys: [], others: row, threshold: 6 });
  assert.equal(r.x, 150); assert.ok(r.gx.gap);
  // after two with an 80 px gap: the same gap again
  r = S.snapBox(302, 52, 20, 20, { xs: [], ys: [], others: row, threshold: 6 });
  assert.equal(r.x, 300); assert.ok(r.gx.chain);
});

test("refsFor picks the reference point: the HUD line it was lined up with, else thirds of the screen", () => {
  const W = 1024, H = 768, refs = S.anchors(W, H);
  // free, top-right area: x from the right edge, y from the top
  assert.deepEqual(S.refsFor(950, 10, 40, 20, refs, W, H), { xRef: 2, yRef: 0, x: -74, y: 10 });
  // middle of the screen: from the centre
  assert.deepEqual(S.refsFor(492, 374, 40, 20, refs, W, H), { xRef: 1, yRef: 1, x: -20, y: -10 });
  // lined up with the radar's left edge (ref 8): x follows the radar
  const radarLeft = S.guides(W, H).xs.find((g) => g.label === "radar left");
  const a = S.refsFor(radarLeft.v - 30, 600, 30, 10, refs, W, H, { gx: { line: radarLeft } });
  assert.equal(a.xRef, 8); assert.equal(a.x, -30); assert.equal(a.yRef, 2);
  // and it stays put relative to the radar at another size
  const big = S.anchors(1920, 1080);
  assert.equal(S.place(a, big)[0] + 30, S.guides(1920, 1080).xs.find((g) => g.label === "radar left").v);
});
