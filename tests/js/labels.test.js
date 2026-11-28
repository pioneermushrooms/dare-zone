// node --test tests/js/*.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const L = require("../../src/Dare.Arenas/Web/wwwroot/labels-layer.js");
const E = require("../../src/Dare.Arenas/Web/wwwroot/elvl.js");

const all = [
  { kind: "spawn", name: "Red spawn", team: 0, x: 100, y: 500, r: 6 },
  { kind: "spawn", name: "Blue spawn", team: 1, x: 900, y: 500, r: 6 },
  { kind: "flagspawn", name: "Flag spawn area", x: 512, y: 400, r: 20 },
  { kind: "ballspawn", name: "Ball 1", index: 0, x: 512, y: 512, r: 3 },
  { kind: "ballspawn", name: "Ball 2", index: 1, x: 520, y: 512, r: 0 },
  { kind: "base", name: "Red base", team: 0, x: 80, y: 450, w: 40, h: 60, noFlagDrops: true },
  { kind: "flagspot", name: "Flag 1", index: 0, x: 95, y: 480 },
  { kind: "area", name: "Lobby", x: 480, y: 100, w: 64, h: 20, noWeapons: true, noAntiwarp: true },
];

test("labels_regions_round_trip", () => {
  const back = L.fromRegions(E.regions(E.withRegions(null, L.toRegions(all))));
  assert.equal(back.length, all.length);
  for (const [i, want] of all.entries()) {
    const got = back[i];
    for (const [k, v] of Object.entries(want)) assert.deepEqual(got[k], v, `${want.name}.${k}`);
  }
});

test("base and area regions carry the server's properties", () => {
  const regs = E.regions(E.withRegions(null, L.toRegions(all)));
  const base = regs.find((r) => r.name === "Red base"), lobby = regs.find((r) => r.name === "Lobby");
  assert.ok(base.base && base.noFlagDrops && !base.noWeapons);
  assert.equal(base.tiles.size, 40 * 60);
  assert.ok(lobby.noWeapons && lobby.noAntiwarp && !lobby.base);
});

test("foreign_regions_kept", () => {
  const prefix = E.withRegions(null, [{ name: "Warp", tiles: E.rect(3, 3, 2, 2) }]);
  const labels = L.fromRegions(E.regions(prefix));
  assert.equal(labels[0].kind, "foreign");
  assert.equal(E.withRegions(prefix, L.toRegions(labels)), prefix); // untouched: same bytes
  labels[0].noWeapons = true;
  const r = E.regions(E.withRegions(prefix, L.toRegions(labels)))[0];
  assert.ok(r.noWeapons); assert.equal(r.tiles.size, 4); assert.equal(r.name, "Warp");
});

test("spawn_settings_keys", () => {
  const s = L.spawnSettings([...all, { kind: "spawn", name: "S", team: 2, x: 1, y: 2, r: 3 }]);
  assert.equal(s["Spawn:Team0-X"], "100"); assert.equal(s["Spawn:Team0-Y"], "500"); assert.equal(s["Spawn:Team0-Radius"], "6");
  assert.equal(s["Spawn:Team2-X"], "1");
  assert.equal(s["Flag:SpawnX"], "512"); assert.equal(s["Flag:SpawnRadius"], "20");
  assert.equal(s["Soccer:SpawnX"], "512"); assert.equal(s["Soccer:SpawnRadius"], "3");
  assert.equal(s["Soccer:SpawnX1"], "520"); assert.equal(s["Soccer:SpawnRadius1"], "0");
  assert.ok(!("Soccer:SpawnX2" in s));
});

test("ball spawns are numbered in order without gaps", () => {
  const s = L.spawnSettings([{ kind: "ballspawn", index: 5, x: 10, y: 10, r: 1 }, { kind: "ballspawn", index: 2, x: 20, y: 20, r: 1 }]);
  assert.equal(s["Soccer:SpawnX"], "20");
  assert.equal(s["Soccer:SpawnX1"], "10");
});

test("same names are made unique for the server", () => {
  const names = E.regions(E.withRegions(null, L.toRegions([{ kind: "area", name: "Zone", x: 1, y: 1, w: 2, h: 2 }, { kind: "area", name: "zone", x: 9, y: 9, w: 2, h: 2 }]))).map((r) => r.name);
  assert.deepEqual(names, ["Zone", "zone 2"]);
});
