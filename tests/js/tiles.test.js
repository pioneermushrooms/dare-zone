// node --test tests/js   (no packages needed)
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { Bmp, LvlFile, TileLayout, TileOps, TileSheet } = require("../../src/Dare.Arenas/Web/wwwroot/tiles.js");

const WWW = path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot");
const layoutText = fs.readFileSync(path.join(WWW, "tile-layout.json"), "utf8");
const sheetBytes = fs.readFileSync(path.join(__dirname, "fixtures/tactical2-tileset.bmp"));

test("the shipped layout parses with no errors or warnings", () => {
  const { layout, errors } = TileLayout.parse(layoutText);
  assert.deepEqual(errors, []);
  assert.deepEqual(layout.warnings, []);
  assert.equal(layout.roles.size, 16);
  assert.equal(layout.packs.length, 6);
  for (const p of layout.packs) assert.equal(p.tiles.size, 16, p.id);
});

test("every set of open edges has exactly one role", () => {
  const { layout } = TileLayout.parse(layoutText);
  const sets = new Set([...layout.roles.values()].map((r) => r.open));
  for (let m = 0; m < 16; m++) assert.ok(sets.has("NESW".split("").filter((_, i) => m & (1 << i)).join("")), `mask ${m}`);
});

test("packs follow the template from their origin", () => {
  const { layout } = TileLayout.parse(layoutText);
  for (const p of layout.packs) {
    const q = TileLayout.packAt(layout, p.id, p.name, p.origin[0], p.origin[1]);
    assert.deepEqual([...q.tiles].sort(), [...p.tiles].sort(), p.id);
  }
  assert.equal(layout.packs[0].tiles.get("single"), 2);
  assert.equal(layout.packs[0].tiles.get("fill"), 42);
});

test("pick chooses the piece by open edges and falls back for a partial pack", () => {
  const { layout } = TileLayout.parse(layoutText);
  assert.equal(TileLayout.pick(layout, "grey-plate", TileLayout.openOf(false, true, false, true)), 4); // h-mid
  assert.equal(TileLayout.pick(layout, "grey-plate", "WE"), 4); // order doesn't matter
  assert.equal(TileLayout.pick(layout, "navy", ""), 86);
  const partial = JSON.parse(layoutText);
  partial.packs = [{ id: "p", tiles: { single: 2, "h-mid": 4, "v-mid": 40, "corner-tl": 22 } }];
  const { layout: l2 } = TileLayout.parse(partial);
  assert.ok(l2.warnings.some((w) => w.includes("pack p has no")));
  assert.equal(TileLayout.pick(l2, "p", "ESW"), 4); // no T: keep the straight line through
  assert.equal(TileLayout.pick(l2, "p", "NES"), 40);
  assert.equal(TileLayout.pick(l2, "p", "ES"), 22);
  assert.equal(TileLayout.pick(l2, "p", "N"), 2); // no end piece: single
});

test("parse refuses broken layouts", () => {
  const bad = (mut) => { const j = JSON.parse(layoutText); mut(j); return TileLayout.parse(j).errors; };
  assert.ok(bad((j) => { j.format = "x"; }).length);
  assert.ok(bad((j) => { j.roles["h-mid"].open = "EX"; }).length);
  assert.ok(bad((j) => { j.roles.extra = { open: "WE" }; }).some((e) => e.includes("same open edges")));
  assert.ok(bad((j) => { j.packs[1].tiles.single = 2; }).some((e) => e.includes("tile 2 is in")));
  assert.ok(bad((j) => { j.packs[0].tiles.single = 162; }).some((e) => e.includes("reserved")));
  assert.ok(bad((j) => { j.packs[0].tiles.nope = 150; }).some((e) => e.includes("unknown role")));
  assert.ok(TileLayout.parse("{not json").errors[0].startsWith("not JSON"));
});

test("a layout survives toJson and parse unchanged", () => {
  const { layout } = TileLayout.parse(layoutText);
  const again = TileLayout.parse(TileLayout.toJson(layout)).layout;
  assert.deepEqual(again.packs, layout.packs);
  assert.deepEqual([...again.roles], [...layout.roles]);
  assert.deepEqual([...again.template.cells], [...layout.template.cells]);
});

test("reads tactical2's 8-bit tileset", () => {
  const img = Bmp.read(sheetBytes);
  assert.equal(img.width, 304);
  assert.equal(img.height, 160);
  // top-left of slot 0 is not empty, and the empty slot 20 (col 0, row 1) is all index 0
  assert.ok(TileOps.getTile(img, 1).some((v) => v));
  assert.ok(TileOps.getTile(img, 20).every((v) => v === 0));
});

test("8-bit BMP write then read is lossless and byte-stable", () => {
  const img = Bmp.read(sheetBytes);
  const out = Bmp.write(img);
  assert.equal(out.length, 49718);
  const back = Bmp.read(out);
  assert.deepEqual(back.pixels, img.pixels);
  assert.deepEqual(back.palette, img.palette);
  assert.deepEqual(Bmp.write(back), out);
  // same pixel bytes as the original file (header fields aside)
  assert.deepEqual(out.subarray(1078), new Uint8Array(sheetBytes).subarray(1078));
});

test("24-bit and top-down BMPs, and RLE8, read into the same pixels", () => {
  const img = Bmp.read(sheetBytes), w = 304, h = 160;
  const stride = (w * 3 + 3) & ~3, b = new Uint8Array(54 + stride * h), dv = new DataView(b.buffer);
  b[0] = 0x42; b[1] = 0x4d; dv.setUint32(2, b.length, true); dv.setUint32(10, 54, true); dv.setUint32(14, 40, true);
  dv.setInt32(18, w, true); dv.setInt32(22, -h, true); dv.setUint16(26, 1, true); dv.setUint16(28, 24, true);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = img.pixels[y * w + x], p = 54 + y * stride + x * 3;
    b[p] = img.palette[i * 3 + 2]; b[p + 1] = img.palette[i * 3 + 1]; b[p + 2] = img.palette[i * 3];
  }
  const q = Bmp.read(b);
  for (let i = 0; i < w * h; i++) {
    const a = img.pixels[i], c = q.pixels[i];
    assert.deepEqual([...q.palette.subarray(c * 3, c * 3 + 3)], [...img.palette.subarray(a * 3, a * 3 + 3)], `pixel ${i}`);
  }
  // RLE8: a 4x2 image, row "1 1 1 2" and row "3 3 0 0"
  const r = new Uint8Array(54 + 1024 + 12); const rv = new DataView(r.buffer);
  r[0] = 0x42; r[1] = 0x4d; rv.setUint32(10, 1078, true); rv.setUint32(14, 40, true); rv.setInt32(18, 4, true); rv.setInt32(22, 2, true);
  rv.setUint16(28, 8, true); rv.setUint32(30, 1, true);
  r.set([2, 3, 2, 0, 0, 0, 3, 1, 1, 2, 0, 1], 1078); // bottom row first
  assert.deepEqual([...Bmp.read(r).pixels], [1, 1, 1, 2, 3, 3, 0, 0]);
});

test("quantize keeps up to 255 colours exactly and maps black/transparent to 0", () => {
  const rgba = new Uint8Array([0, 0, 0, 255, 10, 20, 30, 255, 10, 20, 30, 0, 200, 100, 50, 255]);
  const q = Bmp.quantize(rgba, 4, 1);
  assert.equal(q.pixels[0], 0);
  assert.equal(q.pixels[2], 0);
  assert.deepEqual([...q.palette.subarray(q.pixels[1] * 3, q.pixels[1] * 3 + 3)], [10, 20, 30]);
  assert.deepEqual([...q.palette.subarray(q.pixels[3] * 3, q.pixels[3] * 3 + 3)], [200, 100, 50]);
  // 1000 colours squeeze into 255 + empty
  const many = new Uint8Array(1000 * 4);
  for (let i = 0; i < 1000; i++) { many[i * 4] = i & 255; many[i * 4 + 1] = (i >> 2) & 255; many[i * 4 + 2] = 200; many[i * 4 + 3] = 255; }
  const m = Bmp.quantize(many, 1000, 1);
  assert.ok(m.pixels.every((v) => v >= 1));
});

test("a .lvl's tileset is replaced keeping its eLVL block and tiles", () => {
  const img = Bmp.read(sheetBytes);
  const elvl = new Uint8Array(32); elvl.set([0x65, 0x6c, 0x76, 0x6c]); new DataView(elvl.buffer).setUint32(4, 32, true); elvl[20] = 99;
  const tiles = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
  const lvl = LvlFile.join(Bmp.write(img), elvl, tiles);
  assert.equal(new DataView(lvl.buffer).getUint32(6, true), 49720); // eLVL 4-byte aligned after the bitmap
  const s = LvlFile.split(lvl);
  assert.deepEqual(s.elvl, elvl);
  assert.deepEqual(s.tiles, tiles);
  const edited = Bmp.read(s.bmp); edited.pixels[0] = 7;
  const lvl2 = LvlFile.join(Bmp.write(edited), s.elvl, s.tiles), s2 = LvlFile.split(lvl2);
  assert.equal(Bmp.read(s2.bmp).pixels[0], 7);
  assert.deepEqual(s2.elvl, elvl);
  assert.deepEqual(s2.tiles, tiles);
  // no eLVL: bfSize is the bitmap, the tiles follow
  const plain = LvlFile.join(Bmp.write(img), null, tiles);
  assert.equal(plain.length, 49718 + 8);
  assert.deepEqual(LvlFile.split(plain).tiles, tiles);
  // a map with no tileset
  assert.equal(LvlFile.split(tiles).bmp, null);
  assert.equal(LvlFile.tileset(tiles), null);
});

test("flip and rotate move pixels and open edges together", () => {
  const px = new Uint8Array(256); px[0] = 5; // top-left
  assert.equal(TileOps.flipH(px)[15], 5);
  assert.equal(TileOps.flipV(px)[15 * 16], 5);
  assert.equal(TileOps.rotateCW(px)[15], 5);
  assert.deepEqual(TileOps.rotateCW(TileOps.rotateCW(TileOps.rotateCW(TileOps.rotateCW(px)))), px);
  assert.equal(TileOps.turnOpen("ES", "rotateCW"), "SW"); // corner-tl -> corner-tr
  assert.equal(TileOps.turnOpen("ES", "flipH"), "SW");
  assert.equal(TileOps.turnOpen("ES", "flipV"), "NE");
});

test("edge reading suggests the shipped roles for most of tactical2", () => {
  const img = Bmp.read(sheetBytes), { layout } = TileLayout.parse(layoutText), s = TileOps.suggestOpen(img);
  let right = 0, n = 0;
  for (const p of layout.packs) for (const [role, t] of p.tiles) { n++; if (s.get(t).open === layout.roles.get(role).open) right++; }
  assert.ok(right >= 85, `${right}/${n}`);
  assert.ok(!s.has(20)); // empty slot
});

test("TileSheet is the Continuum tileset size", () => {
  assert.deepEqual([TileSheet.SHEET_W, TileSheet.SHEET_H, TileSheet.TS], [304, 160, 16]);
});
