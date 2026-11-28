// node --test tests/js   (no packages needed)
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const G = require("../../src/Dare.Arenas/Web/wwwroot/gamemode-ships.js");

const BITS = { InitialGuns: 3, MaxGuns: 3, InitialBombs: 3, MaxBombs: 3, CloakStatus: 2, StealthStatus: 2, XRadarStatus: 2, AntiWarpStatus: 2 };
const BOOLS = new Set(["DoubleBarrel", "EmpBomb", "SeeMines"]);

// A spec for every key in STANDARD, like the arena's /api/me settings list gives the page.
function specsAll() {
  const specs = {};
  for (const [sec, keys] of Object.entries(G.STANDARD))
    for (const k of Object.keys(keys)) {
      if (k in BITS) specs[`${sec}:${k}`] = { type: "int", min: 0, max: BITS[k] };
      else if (BOOLS.has(k)) specs[`${sec}:${k}`] = { type: "bool" };
      else specs[`${sec}:${k}`] = { type: "int", min: 0, max: 65535 };
    }
  return specs;
}
const withRoles = (role) => ({ ...G.defaults(), roles: Object.fromEntries(G.SHIPS.map((s) => [s, role])) });

test("standard everything with standard roles gives the stock values back", () => {
  const out = G.settings(withRoles("standard"), specsAll());
  for (const sec of Object.keys(G.STANDARD))
    for (const [k, v] of Object.entries(G.STANDARD[sec])) assert.equal(out[`${sec}:${k}`], String(v), `${sec}:${k}`);
});

test("STANDARD matches SubspaceServer's conf/svs when it is on this machine", (t) => {
  const dir = path.join(os.homedir(), "SubspaceServer/src/SubspaceServer/Zone/conf/svs");
  if (!fs.existsSync(dir)) return t.skip("no SubspaceServer checkout here");
  const read = (f) => {
    const out = {}; let sec = null;
    for (let line of fs.readFileSync(path.join(dir, f), "utf8").split(/\r?\n/)) {
      line = line.split(";")[0].trim();
      const m = /^\[\s*(\w+)\s*\]$/.exec(line);
      if (m) { sec = m[1]; out[sec] ??= {}; } else if (sec && line.includes("=")) { const i = line.indexOf("="); out[sec][line.slice(0, i).trim()] = +line.slice(i + 1).trim(); }
    }
    return out;
  };
  for (const s of G.SHIPS) {
    const file = read(`ship-${s.toLowerCase()}`)[s];
    for (const [k, v] of Object.entries(file)) if (!k.startsWith("Soccer")) assert.equal(G.STANDARD[s][k], v, `${s}:${k}`);
  }
  for (const [k, v] of Object.entries(read("prizeweights").PrizeWeight)) assert.equal(G.STANDARD.PrizeWeight[k], v, `PrizeWeight:${k}`);
  for (const [k, v] of Object.entries(G.STANDARD.Prize)) assert.equal(read("misc").Prize[k], v, `Prize:${k}`);
});

test("every role x pace x items x upgrades x greens stays within spec", () => {
  const specs = specsAll();
  for (const role of G.ROLES.map((r) => r.id))
    for (const pace of G.PACES) for (const items of G.ITEMS) for (const upgrades of G.UPGRADES) for (const greens of [true, false]) {
      const out = G.settings({ ...withRoles(role), pace, items, upgrades, greens, start: { ...G.defaults().start, Repel: 3, Rocket: 2 } }, specs);
      for (const [name, v] of Object.entries(out)) {
        assert.match(v, /^\d+$/, `${role}/${pace}/${items}/${upgrades}: ${name}=${v}`);
        const s = specs[name];
        if (s.type === "bool") assert.ok(v === "0" || v === "1", `${name}=${v}`);
        if (s.max != null) assert.ok(+v >= s.min && +v <= s.max, `${role}/${pace}/${items}/${upgrades}: ${name}=${v}`);
      }
    }
});

test("settings only includes keys with specs", () => {
  const out = G.settings(G.defaults(), { "Warbird:InitialGuns": { type: "int", min: 0, max: 3 } });
  assert.deepEqual(Object.keys(out), ["Warbird:InitialGuns"]);
});

test("tweaks are clamped to spec and win over pace", () => {
  const d = { ...G.defaults(), pace: "mayhem", tweaks: { "Warbird:MaxGuns": "9", "Warbird:MaximumSpeed": "3000", "Warbird:DoubleBarrel": "7" } };
  const out = G.settings(d, specsAll());
  assert.equal(out["Warbird:MaxGuns"], "3");
  assert.equal(out["Warbird:MaximumSpeed"], "3000");
  assert.equal(out["Warbird:DoubleBarrel"], "1");
});

test("pace scales speed and fire delay", () => {
  const out = G.settings({ ...withRoles("standard"), pace: "slow" }, specsAll());
  assert.equal(out["Warbird:MaximumSpeed"], String(Math.round(G.STANDARD.Warbird.MaximumSpeed * 0.75)));
  assert.equal(out["Warbird:BulletFireDelay"], String(Math.round(G.STANDARD.Warbird.BulletFireDelay * 1.8)));
  assert.equal(out["Warbird:MaxMines"], String(G.STANDARD.Warbird.MaxMines - 2));
});

test("start items never exceed cap", () => {
  const d = { ...G.defaults(), items: "none", start: { ...G.defaults().start, Repel: 3 } };
  const out = G.settings(d, specsAll());
  for (const s of G.SHIPS) assert.ok(+out[`${s}:InitialRepel`] <= +out[`${s}:RepelMax`], s);
  assert.equal(out["Shark:InitialRepel"], "2"); // support keeps its own start...
  assert.ok(+out["Shark:RepelMax"] >= 2);      // ...and a cap that holds it
  assert.equal(out["Warbird:InitialRepel"], "0");
});

test("greens off sets PrizeFactor 0", () => {
  assert.equal(G.settings({ ...G.defaults(), greens: false }, specsAll())["Prize:PrizeFactor"], "0");
  assert.equal(G.settings({ ...G.defaults(), items: "loads" }, specsAll())["Prize:PrizeFactor"], "3500");
});

test("upgrades off: initial equals maximum and upgrade weights are 0", () => {
  const out = G.settings({ ...withRoles("standard"), upgrades: "off" }, specsAll());
  assert.equal(out["Warbird:InitialSpeed"], out["Warbird:MaximumSpeed"]);
  assert.equal(out["Warbird:InitialEnergy"], out["Warbird:MaximumEnergy"]);
  assert.equal(out["Warbird:InitialGuns"], out["Warbird:MaxGuns"]);
  assert.equal(out["PrizeWeight:Gun"], "0");
  assert.equal(out["PrizeWeight:Repel"], String(G.STANDARD.PrizeWeight.Repel)); // items still appear
  const fast = G.settings({ ...withRoles("standard"), upgrades: "fast" }, specsAll());
  assert.equal(fast["Warbird:UpgradeSpeed"], String(Math.round(G.STANDARD.Warbird.UpgradeSpeed * 1.5)));
});

test("only deployer and anchor carry turrets; every role lets pilots attach at once", () => {
  const out = G.settings(G.defaults(), specsAll());
  assert.equal(out["Terrier:TurretLimit"], "5");
  assert.equal(out["Leviathan:TurretLimit"], "5");
  assert.equal(out["Warbird:TurretLimit"], "0");
  for (const s of G.SHIPS) assert.equal(out[`${s}:AttachBounty`], "0", s);
  assert.equal(G.settings(withRoles("standard"), specsAll())["Warbird:TurretLimit"], String(G.STANDARD.Warbird.TurretLimit));
});

test("tweakCount counts a ship's tweaks", () => {
  const d = { ...G.defaults(), tweaks: { "Javelin:BombBounceCount": "2", "Javelin:ShrapnelMax": "14", "Warbird:MaxGuns": "2" } };
  assert.equal(G.tweakCount(d, "Javelin"), 2);
  assert.equal(G.tweakCount(d, "Shark"), 0);
});
