// Run: node --test tests/js/*.test.js   (no packages needed)
"use strict";
const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const WWW = path.join(__dirname, "..", "..", "src", "Dare.Arenas", "Web", "wwwroot");
const TA = require(path.join(WWW, "tileart.js"));
const layout = JSON.parse(fs.readFileSync(path.join(WWW, "tile-layout.json"), "utf8"));
const data = (() => {
  const ctx = { window: {} };
  vm.runInNewContext(fs.readFileSync(path.join(WWW, "tileart-styles.js"), "utf8"), ctx);
  return ctx.window.TileArtStyles;
})();

const n = 64;
function grid(rows, x0 = 10, y0 = 10) {
  const g = new Uint8Array(n * n);
  rows.forEach((r, y) => [...r].forEach((c, x) => { if (c !== ".") g[(y0 + y) * n + x0 + x] = c.charCodeAt(0) - 48; }));
  return g;
}

test("masks match the generator's 47 pieces", () => {
  const set = new Set();
  for (let m = 0; m < 256; m++) set.add(TA.canon(m));
  assert.deepStrictEqual(JSON.stringify([...set].sort((a, b) => a - b)), JSON.stringify(data.masks));
  for (const r of Object.values(layout.roles)) assert.ok(data.masks.includes(TA.openMask(r.open)));
});

test("usage finds the piece each slot is used as", () => {
  // slot 1: a horizontal bar's middle (twice), slot 2 its ends; slot 3 a lone block;
  // slot 4 the middle of a block of 6s; slot 7 used as several different pieces
  const g = grid([
    "2112......",
    "..........",
    "....3.....",
    "..........",
    "666...7...",
    "646...77..",
    "666.......",
    "......777.",
    ".......7..",
  ]);
  const u = TA.usage(g, n);
  assert.strictEqual(u.get(1).mask, 4 | 64);           // E W
  assert.strictEqual(u.get(1).count, 2);
  assert.strictEqual(u.get(3).mask, 0);
  assert.strictEqual(u.get(4).mask, 255);              // wall all round
  assert.ok(u.get(7).masks.size > 1 && u.get(7).masks.get(u.get(7).mask) >= 1);
  assert.ok(!TA.isWall(170) && TA.isWall(161));
});

test("groups keep touching slots together", () => {
  const g = grid(["2112..55", "........", "3......."]);
  const gs = TA.groups(g, TA.usage(g, n), n);
  assert.deepStrictEqual(gs, [[1, 2], [5], [3]]);
});

test("blank plan paints every blank wall slot, structures in one style", () => {
  const g = grid(["2112..55", "........", "3......."]);
  const blank = new Map([[1, "red"], [2, "red"], [5, "red"], [9, "red"], [170, "red"]]);
  const plan = TA.blankPlan(blank, g, "foundry", data, layout);
  assert.deepStrictEqual([...plan.keys()].sort((a, b) => a - b), [1, 2, 5, 9]);
  assert.strictEqual(plan.get(1).style, plan.get(2).style);
  assert.notStrictEqual(plan.get(1).style, plan.get(5).style);
  assert.strictEqual(plan.get(1).mask, 4 | 64);
  // 9 is not placed in the map: the layout's piece for slot 9 (grey-round end-right, open W)
  assert.strictEqual(plan.get(9).mask, TA.openMask("W"));
});

test("restyle covers every wall slot and follows the layout where the map doesn't say", () => {
  const plan = TA.restylePlan(grid(["2112"]), "steel", data, layout);
  assert.strictEqual(plan.size, 161);
  const fill = layout.packs[0].tiles.fill;
  assert.strictEqual(plan.get(fill).mask, TA.openMask("NESW"));
  assert.strictEqual(plan.get(fill).style, "steel");
});

test("theme plan puts each pack in its style", () => {
  const plan = TA.sheetPlan(layout, "station", data);
  for (const p of layout.packs) for (const t of Object.values(p.tiles)) assert.strictEqual(plan.get(t).style, data.themes.station[p.id]);
});

test("new tileset keeps the map's eLVL data", () => {
  const w = 304, h = 160, rgba = new Uint8Array(w * h * 4);
  for (let i = 0; i < w * h; i++) { rgba[i * 4] = i % 256; rgba[i * 4 + 1] = 7; rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
  const elvl = new Uint8Array([0x65, 0x6c, 0x76, 0x6c, 12, 0, 0, 0, 0, 0, 0, 0]);
  for (const old of [elvl, null, TA.withTileset(elvl, rgba)]) {
    const out = TA.withTileset(old, rgba);
    const dv = new DataView(out.buffer);
    assert.strictEqual(String.fromCharCode(out[0], out[1]), "BM");
    assert.strictEqual(dv.getUint32(2, true), out.length);
    assert.strictEqual(dv.getUint16(28, true), 24);
    const at = dv.getUint32(6, true);
    if (old) { assert.ok(at > 0 && at % 4 === 0); assert.deepStrictEqual([...out.slice(at)], [...elvl]); }
    else assert.strictEqual(at, 0);
    // top-left pixel is the last row in a bottom-up BMP: B G R
    const row = w * 3, o = 54 + (h - 1) * row;
    assert.deepStrictEqual([out[o], out[o + 1], out[o + 2]], [200, 7, 0]);
  }
});

test("restyle keeps each pack's style and piece on a map that follows the layout", () => {
  const pack = layout.packs.find((p) => p.id === "green-lit");
  const g = new Uint8Array(n * n);
  const row = [pack.tiles["end-left"], pack.tiles["h-mid"], pack.tiles["h-mid"], pack.tiles["end-right"]];
  row.forEach((t, i) => { g[20 * n + 10 + i] = t; g[22 * n + 10 + i] = t; });
  g[30 * n + 30] = 1; // a slot the layout doesn't claim
  const use = TA.usage(g, n);
  assert.ok(TA.followsLayout(use, layout));
  const plan = TA.restylePlan(g, "foundry", data, layout);
  for (const t of row) assert.strictEqual(plan.get(t).style, data.themes.foundry["green-lit"]);
  assert.strictEqual(plan.get(pack.tiles["h-mid"]).mask, TA.openMask("EW"));
  if (pack.interior) assert.strictEqual(plan.get(pack.interior).mask, 255);
  for (const [open, t] of Object.entries(pack.calm || {})) assert.strictEqual(plan.get(t).mask, TA.openMask(open));
  assert.strictEqual(plan.get(1).mask, 0); // unclaimed: from how the map uses it
  // a map that places the layout's slots as other pieces doesn't follow it
  assert.ok(!TA.followsLayout(TA.usage(grid(["2112"]), n), layout));
});

test("packStyle names what each pack looks like", () => {
  assert.strictEqual(TA.packStyle("foundry", "steel-blue", data, layout), data.themes.foundry["steel-blue"]);
  assert.strictEqual(TA.packStyle("steel", "navy", data, layout), "steel");
  const slots = TA.layoutSlots({ roles: layout.roles, packs: [{ id: "x", tiles: {}, inner: { NE: 20, SW: 56 } }] });
  assert.strictEqual(slots.get(20).mask, 255 & ~2);
  assert.strictEqual(slots.get(56).mask, 255 & ~32);
});

test("the edge reader finds the standard packs in the standard tileset, and not in a blank one", () => {
  const { Bmp } = require(path.join(WWW, "tiles.js"));
  const img = Bmp.read(fs.readFileSync(path.join(WWW, "standard-tiles.bmp")));
  const w = img.width, rgba = new Uint8Array(w * img.height * 4);
  for (let i = 0; i < img.pixels.length; i++) { const c = img.pixels[i] * 3; rgba.set([img.palette[c], img.palette[c + 1], img.palette[c + 2], 255], i * 4); }
  for (const p of layout.packs) assert.ok(TA.packReads(rgba, w, layout, p) >= 0.6, p.id);
  const blank = new Uint8Array(w * img.height * 4);
  for (const p of layout.packs) assert.strictEqual(TA.packReads(blank, w, layout, p), 0);
});

test("wall sets are named by what they look like, alike ones once", () => {
  const names = (p) => TA.packStyle("steel", p.id, data, layout);
  assert.strictEqual(TA.wallSets(layout, names).length, 1);
  const f = TA.wallSets(layout, (p) => TA.packStyle("foundry", p.id, data, layout));
  assert.ok(f.length > 1 && f[0].pack === layout.packs[0].id);
  assert.deepStrictEqual(TA.wallSets(layout, null, (p) => p.id === "navy").map((x) => x.name), ["Navy"]);
  // the swatch uses the pack's own tiles, a bar's middle among them
  const ts = TA.sampleTiles(layout, layout.packs[0]).map((c) => c.t);
  assert.ok(ts.includes(layout.packs[0].tiles["h-mid"]));
  assert.ok(ts.every((t) => Object.values(layout.packs[0].tiles).includes(t)));
  assert.ok(TA.defaults(data).some((d) => d.id === "foundry") && TA.defaults(data).some((d) => d.id === "steel"));
});
