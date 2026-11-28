// node --test tests/js/*.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const E = require("../../src/Dare.Arenas/Web/wwwroot/elvl.js");
const { LvlFile } = require("../../src/Dare.Arenas/Web/wwwroot/tiles.js");

const u32 = (b, p) => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) >>> 0;
const ascii = (s) => Uint8Array.from(s, (c) => c.charCodeAt(0));
function chunk(type, data) {
  const pad = (4 - (data.length & 3)) & 3, out = new Uint8Array(8 + data.length + pad);
  out.set(ascii(type), 0); new DataView(out.buffer).setUint32(4, data.length, true); out.set(data, 8);
  return out;
}
const cat = (...a) => { const o = new Uint8Array(a.reduce((n, x) => n + x.length, 0)); let p = 0; for (const x of a) { o.set(x, p); p += x.length; } return o; };
function block(...chunks) {
  const body = cat(...chunks), out = new Uint8Array(12 + body.length);
  out.set(ascii("elvl"), 0); new DataView(out.buffer).setUint32(4, out.length, true); out.set(body, 12);
  return out;
}
// a tiny 8-bit BMP stub (header only matters for LvlFile)
function bmpStub() {
  const b = new Uint8Array(54 + 1024 + 4); b[0] = 0x42; b[1] = 0x4d; new DataView(b.buffer).setUint32(2, b.length, true);
  new DataView(b.buffer).setUint32(10, 54 + 1024, true); return b;
}
const setOf = (...pairs) => new Set(pairs.map(([x, y]) => y * 1024 + x));
const same = (a, b) => a.size === b.size && [...a].every((v) => b.has(v));

test("encode_decode_round_trip", () => {
  const shapes = [E.rect(10, 20, 5, 3), setOf([0, 0]), setOf([5, 5], [6, 5], [5, 6]), E.rect(0, 0, 1024, 1024), new Set()];
  for (const s of shapes) assert.ok(same(E.decodeTiles(E.encodeTiles(s)), s), `size ${s.size}`);
});

test("edge_rectangles_round_trip", () => {
  for (const s of [E.rect(1000, 1000, 24, 24), E.rect(0, 1023, 1024, 1), E.rect(1023, 0, 1, 1024)])
    assert.ok(same(E.decodeTiles(E.encodeTiles(s)), s));
});

test("decoder_rejects_short_data", () => {
  assert.equal(E.decodeTiles(Uint8Array.of(0x20 | 0x03, 0xff)), null); // 1024 empty, then data ends at row 1
  assert.equal(E.decodeTiles(Uint8Array.of(0x20)), null);              // double-byte sequence cut short
});

test("regions_round_trip_with_flags_and_meta", () => {
  const regs = [{ name: "Red base", tiles: E.rect(10, 10, 4, 4), base: true, noFlagDrops: true, meta: { v: 1, kind: "base", team: 0 } },
    { name: "Lobby", tiles: E.rect(500, 500, 10, 2), noWeapons: true, noAntiwarp: true }];
  const back = E.regions(E.withRegions(null, regs));
  assert.equal(back.length, 2);
  assert.equal(back[0].name, "Red base"); assert.ok(back[0].base && back[0].noFlagDrops && !back[0].noWeapons);
  assert.deepEqual(back[0].meta, { v: 1, kind: "base", team: 0 });
  assert.ok(same(back[0].tiles, regs[0].tiles));
  assert.ok(back[1].noWeapons && back[1].noAntiwarp && !back[1].base); assert.equal(back[1].meta, null);
});

test("untouched_prefix_is_identical", () => {
  const regn = chunk("REGN", cat(chunk("rNAM", ascii("Warp1")), chunk("rTIL", E.encodeTiles(setOf([3, 4], [9, 9]))), chunk("rAWP", Uint8Array.of(1, 2, 3))));
  const p = LvlFile.join(bmpStub(), block(chunk("ATTR", ascii("NAME=x")), regn, chunk("TSET", Uint8Array.of(7))), new Uint8Array());
  const out = E.withRegions(p, E.regions(p));
  assert.deepEqual([...out], [...p]);
});

test("adds_block_to_bare_map", () => {
  const p = E.withRegions(null, [{ name: "A", tiles: setOf([1, 1]) }]);
  assert.equal(String.fromCharCode(...p.slice(0, 4)), "elvl");
  assert.equal(u32(p, 4), p.length); // tiles start right after the block
  assert.equal(E.regions(p)[0].name, "A");
});

test("bmp_offsets_fixed", () => {
  const p = E.withRegions(LvlFile.join(bmpStub(), null, new Uint8Array()), [{ name: "A", tiles: setOf([1, 1]) }]);
  const at = u32(p, 6), end = u32(p, 2);
  assert.equal(String.fromCharCode(...p.slice(at, at + 4)), "elvl");
  assert.equal(end, p.length);
  assert.equal(at + u32(p, at + 4), end);
});

test("unique_names", () => {
  const out = E.uniqueNames([{ name: "Base" }, { name: "base" }, { name: "Base" }, { name: "" }]);
  assert.deepEqual(out.map((r) => r.name), ["Base", "base 2", "Base 3", "Region"]);
});
