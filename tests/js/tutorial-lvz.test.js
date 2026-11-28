// The tutorial arena's generated files: node --test tests/js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const ROOT = path.join(__dirname, "../..");
const Lvz = require(path.join(ROOT, "src/Dare.Arenas/Web/wwwroot/lvz.js"));
const Build = require(path.join(ROOT, "tools/tutorial-lvz/build.js"));
const TrainingMap = require(path.join(ROOT, "tools/tutorial-lvz/map.js"));
const Font = require(path.join(ROOT, "tools/tutorial-lvz/font.js"));

const ARENA = path.join(ROOT, "deploy/arenas/#training");
const script = JSON.parse(fs.readFileSync(path.join(ROOT, "src/Dare.Arenas/tutorial/script.json"), "utf8"));
// AllLines(): step lines in file order, then prompts; Index from 0
const lines = [...script.steps.flatMap((s) => s.lines.map((text) => ({ text }))), ...script.prompts.map((p) => ({ text: p.text, id: p.id }))]
  .map((l, index) => ({ ...l, index }));
const SC = 5; // ServerControlled
const TOPMOST = 7;

let built;
const model = async () => (built ??= await Build.buildLvz(script));
const byId = (lvz, id) => lvz.objects.filter((o) => o.id === id);
const imageOf = async (lvz, o) => {
  const im = lvz.images[o.image];
  const f = lvz.files.find((x) => x.name === im.file);
  return Lvz.decodeBmp(await Lvz.fileData(f));
};
const overlaps = (a, b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];
const rect = async (lvz, o, w, h) => {
  const im = await imageOf(lvz, o);
  const x = Lvz.anchor(o.xRef, w, h)[0] + o.x, y = Lvz.anchor(o.yRef, w, h)[1] + o.y;
  return [x, y, x + im.width / (lvz.images[o.image].cols || 1), y + im.height / (lvz.images[o.image].rows || 1)];
};

test("every script line has an object 5000+index and an image", async () => {
  const lvz = await model();
  assert.ok(lines.length > 20);
  for (const l of lines) {
    const objs = byId(lvz, 5000 + l.index);
    assert.equal(objs.length, 1, `line ${l.index}`);
    const o = objs[0];
    assert.equal(o.map, false);
    const im = await imageOf(lvz, o);
    assert.equal(im.width, Build.TEXT_W);
    assert.equal(im.height, 2 * Font.H);
    assert.ok(im.rgba.some((v, i) => i % 4 === 3 && v), `line ${l.index} has visible text`);
  }
  assert.equal(byId(lvz, 5000 + lines.length).length, 0);
});

test("every line wraps into two lines of the bubble at most", () => {
  for (const l of lines) {
    const rows = Build.layoutLine(l.text, script.continuumKeys);
    assert.ok(rows.length >= 1 && rows.length <= 2, `${l.text} -> ${JSON.stringify(rows)}`);
    for (const r of rows) assert.ok(r.length * Font.W <= Build.TEXT_W, r);
  }
});

test("covers 4000-4005 are TopMost and server-controlled", async () => {
  const lvz = await model();
  for (let id = 4000; id <= 4005; id++) {
    const objs = byId(lvz, id);
    assert.ok(objs.length >= 1, `cover ${id}`);
    for (const o of objs) {
      assert.equal(o.map, false);
      assert.equal(o.layer, TOPMOST, `cover ${id} layer`);
      assert.equal(o.mode, SC, `cover ${id} mode`);
      const im = await imageOf(lvz, o);
      // Continuum draws pure black as see-through: a cover must be opaque everywhere
      assert.ok(im.rgba.every((v, i) => i % 4 !== 3 || v === 255), `cover ${id} is opaque`);
    }
  }
});

test("covers sit over their HUD part at 1024x768 and 1920x1080", async () => {
  const lvz = await model();
  for (const [w, h] of [[1024, 768], [1920, 1080]]) {
    const at = (ref) => Lvz.anchor(ref, w, h);
    const pt = (p) => [p[0], p[1], p[0] + 1, p[1] + 1];
    const parts = { // a point inside each part, from the reference points
      4000: [pt([at(8)[0] + 60, at(8)[1] + 60]), pt(at(9))], // radar and its text
      4001: [pt([w / 2, at(6)[1] - 10])], // energy bar, just above "below energy bar"
      4002: [pt([at(3)[0] - 20, at(3)[1] - 10])], // stat box, above its lower right
      4003: [pt([at(4)[0] - 10, at(4)[1] + 20]), pt([at(5)[0] - 10, at(5)[1] - 10]), pt([at(10)[0] + 10, at(10)[1] + 20])], // items left, weapons right
      4004: [pt([20, at(7)[1] + 10]), pt([at(8)[0] - 20, at(7)[1] + 30])], // chat lines
      4005: [pt([20, at(7)[1] - 20])], // above the chat lines
    };
    for (const [id, pts] of Object.entries(parts)) {
      const rs = await Promise.all(byId(lvz, +id).map((o) => rect(lvz, o, w, h)));
      for (const p of pts) assert.ok(rs.some((r) => overlaps(r, p)), `${w}x${h}: cover ${id} misses ${p}`);
    }
    // a revealed part is not hidden by a cover that stays on longer: no two different covers overlap
    const all = [];
    for (let id = 4000; id <= 4005; id++) for (const o of byId(lvz, id)) all.push([id, await rect(lvz, o, w, h)]);
    for (const [ia, a] of all) for (const [ib, b] of all) if (ia < ib) assert.ok(!overlaps(a, b), `${w}x${h}: covers ${ia} and ${ib} overlap`);
    // the ship in the middle of the screen stays visible
    for (const [id, r] of all) assert.ok(!overlaps(r, [w / 2 - 20, h / 2 - 20, w / 2 + 20, h / 2 + 20]), `${w}x${h}: cover ${id} hides the ship`);
  }
});

test("mentor objects are server-controlled and start off", async () => {
  const lvz = await model();
  const ids = [4100, 4101, 4102, 4110, 4111, 4200, ...lines.map((l) => 5000 + l.index)];
  for (const id of ids) {
    const objs = byId(lvz, id);
    assert.equal(objs.length, 1, `object ${id}`);
    assert.equal(objs[0].mode, SC, `object ${id} mode`);
    assert.equal(objs[0].time, 0, `object ${id} stays until turned off`);
  }
  assert.ok(lvz.objects.every((o) => o.mode === SC), "every object is server-controlled");
  assert.ok(lvz.objects.every((o) => !Lvz.isReserved(o.id)), "no game-mode ids");
  // the box: bottom-left, pinned to the chat anchor, above the chat lines; drawn after the covers
  const box = byId(lvz, 4100)[0];
  assert.deepEqual([box.xRef, box.yRef], [7, 7]);
  assert.ok(box.y < 0);
  const last = (id) => lvz.objects.findIndex((o) => o.id === id);
  for (let id = 4000; id <= 4005; id++) assert.ok(last(id) < last(4100), `cover ${id} before the box`);
  for (const id of [4101, 4102, ...lines.filter((l) => !l.id || l.id === "arena_pointer").map((l) => 5000 + l.index)]) {
    const o = byId(lvz, id)[0];
    assert.deepEqual([o.xRef, o.yRef, o.layer], [7, 7, TOPMOST], `object ${id}`);
    const [r, b] = [await rect(lvz, o, 1024, 768), await rect(lvz, box, 1024, 768)];
    assert.ok(r[0] >= b[0] && r[1] >= b[1] && r[2] <= b[2] && r[3] <= b[3], `object ${id} inside the box`);
  }
  // the box covers nothing meant to be seen: not the radar, energy or ship
  for (const [w, h] of [[1024, 768], [1920, 1080]]) {
    const b = await rect(lvz, box, w, h);
    assert.ok(b[2] < Lvz.anchor(8, w, h)[0] && b[3] <= Lvz.anchor(7, w, h)[1] && b[1] > h / 2 + 40, `${w}x${h}: box ${b}`);
  }
  // popup prompts sit inside their popups
  for (const [pid, popup] of [["skip_popup", 4110], ["try_tutorial", 4111]]) {
    const l = lines.find((x) => x.id === pid);
    const [r, p] = [await rect(lvz, byId(lvz, 5000 + l.index)[0], 1024, 768), await rect(lvz, byId(lvz, popup)[0], 1024, 768)];
    assert.ok(r[0] >= p[0] && r[1] >= p[1] && r[2] <= p[2] && r[3] <= p[3], `${pid} inside popup ${popup}`);
  }
  // portrait frames: same spot, different pictures
  const [a, b] = [byId(lvz, 4101)[0], byId(lvz, 4102)[0]];
  assert.deepEqual([a.x, a.y], [b.x, b.y]);
  assert.notDeepEqual((await imageOf(lvz, a)).rgba, (await imageOf(lvz, b)).rgba);
  // the beacon is a map object on the beacon spot
  const beacon = byId(lvz, 4200)[0];
  assert.equal(beacon.map, true);
  const size = (await imageOf(lvz, beacon)).height; // square frames side by side
  const spot = TrainingMap.SPOTS.beacon;
  assert.equal(beacon.x + size / 2, spot.x * 16 + 8);
  assert.equal(beacon.y + size / 2, spot.y * 16 + 8);
});

test("placeholders are filled with Continuum keys", () => {
  const shoot = lines.find((l) => l.text.includes("{gun}"));
  const rows = Build.layoutLine(shoot.text, script.continuumKeys).join(" ");
  assert.match(rows, /Ctrl/);
  assert.doesNotMatch(rows, /[{}]/);
  assert.equal(Build.forContinuum("{gun} and {nope}", { gun: "Ctrl" }), "Ctrl and {nope}");
  assert.match(Build.layoutLine("Now {repel}: go", script.continuumKeys).join(" "), /Shift\+Ctrl/);
});

test("the font draws every printable ASCII character, each one different", () => {
  const seen = new Set();
  for (let c = 33; c <= 126; c++) {
    const bits = Font.glyph(String.fromCharCode(c));
    assert.equal(bits.length, Font.W * Font.H);
    assert.ok(bits.some(Boolean), `char ${c} draws something`);
    const key = bits.join("");
    assert.ok(!seen.has(key), `char ${String.fromCharCode(c)} is unique`);
    seen.add(key);
  }
  assert.ok(Font.glyph(" ").every((v) => !v));
  assert.deepEqual(Font.wrap("aaa bbb ccc", 7), ["aaa bbb", "ccc"]);
  assert.deepEqual(Font.wrap("abcdefghij", 4), ["abcd", "efgh", "ij"]);
});

test("output round-trips through lvz.js and matches the checked-in file", async () => {
  const lvz = await model();
  const back = await Lvz.read(await Lvz.write(lvz));
  assert.deepEqual(back.warnings, []);
  assert.equal(back.objects.length, lvz.objects.length);
  assert.ok(back.images.length <= 256);
  const shape = async (m) => ({
    objects: m.objects.map(({ map, id, x, y, xRef, yRef, image, layer, mode, time }) => ({ map, id, x, y, xRef, yRef, image, layer, mode, time })),
    images: m.images.map(({ file, cols, rows, time }) => ({ file, cols, rows, time })),
    files: await Promise.all(m.files.map(async (f) => [f.name, Buffer.from(await Lvz.fileData(f)).toString("base64")])),
  });
  const want = await shape(lvz);
  assert.deepEqual(await shape(back), want);
  const disk = await Lvz.read(new Uint8Array(fs.readFileSync(path.join(ARENA, "tutorial.lvz"))));
  assert.deepEqual(await shape(disk), want, "deploy/arenas/#training/tutorial.lvz is stale: run node tools/tutorial-lvz/build.js");
});

test("the portrait sheet for the browser is a 128x64 PNG matching the build", () => {
  const png = fs.readFileSync(path.join(ROOT, "site/tutorial-mentor.png"));
  assert.deepEqual([...png.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
  assert.equal(png.readUInt32BE(16), 128);
  assert.equal(png.readUInt32BE(20), 64);
  const pixels = (b) => { // the inflated IDAT data (zlib output may differ between builds of zlib; the pixels may not)
    const parts = [];
    for (let p = 8; p < b.length; p += 12 + b.readUInt32BE(p)) if (b.toString("latin1", p + 4, p + 8) === "IDAT") parts.push(b.subarray(p + 8, p + 8 + b.readUInt32BE(p)));
    return zlib.inflateSync(Buffer.concat(parts));
  };
  assert.deepEqual(pixels(png), pixels(Build.portraitPng()), "site/tutorial-mentor.png is stale: run node tools/tutorial-lvz/build.js");
});

test("the map has the training features and matches the checked-in .lvl", () => {
  global.window ??= globalThis;
  require(path.join(ROOT, "src/Dare.Arenas/Web/wwwroot/lvl.js"));
  const grid = TrainingMap.build();
  const disk = window.Lvl.parse(new Uint8Array(fs.readFileSync(path.join(ARENA, "training.lvl")))).grid;
  assert.deepEqual(disk, grid, "training.lvl is stale: run node tools/tutorial-lvz/build.js");
  const at = (x, y) => grid[y * 1024 + x];
  const { lo, hi } = TrainingMap.BOUNDS;
  assert.ok(hi - lo >= 180 && hi - lo <= 220);
  for (let i = lo; i <= hi; i++) for (const [x, y] of [[i, lo], [i, hi], [lo, i], [hi, i]]) assert.ok(at(x, y) >= 1 && at(x, y) <= 161, `border ${x},${y}`);
  let tiles = 0;
  grid.forEach((t, i) => { if (t) { tiles++; const x = i % 1024, y = (i / 1024) | 0; assert.ok(x >= lo && x <= hi && y >= lo && y <= hi, `tile outside ${x},${y}`); } });
  assert.ok(tiles > 0);
  const S = TrainingMap.SPOTS;
  const open = (p, r) => { for (let y = p.y - r; y <= p.y + r; y++) for (let x = p.x - r; x <= p.x + r; x++) if (at(x, y) && at(x, y) !== 171) return false; return true; };
  for (const k of ["start", "beacon", "flag", "ball", "green"]) assert.ok(open(S[k], 3), `${k} is in open space`);
  assert.equal(at(S.safe.x, S.safe.y), 171);
  assert.ok(grid.some((t) => t === 172), "a goal");
  assert.ok(!grid.some((t) => t === 170), "the flag is a carried flag (Flag:SpawnX/Y), not a turf tile");
  // the ring: walls all round the beacon at its radius, with gaps to fly in
  const ringWalls = [...Array(360).keys()].filter((a) => {
    const r = (a * Math.PI) / 180;
    return at(Math.round(S.beacon.x + S.ringRadius * Math.cos(r)), Math.round(S.beacon.y + S.ringRadius * Math.sin(r)));
  }).length;
  assert.ok(ringWalls > 200 && ringWalls < 350, `ring walls ${ringWalls}`);
});

test("script coordinates and arena.conf point at the map's spots", () => {
  const S = TrainingMap.SPOTS;
  const fly = script.steps.find((s) => s.id === "fly").check;
  assert.deepEqual([fly.x, fly.y], [S.beacon.x, S.beacon.y]);
  for (const s of script.steps) for (const c of [...(s.cast || []), ...(s.leanCast || [])]) {
    for (const m of c.script.matchAll(/^point (\d+) (\d+)$/gm)) {
      const [x, y] = [+m[1], +m[2]];
      assert.ok(Math.hypot(x - S.beacon.x, y - S.beacon.y) < S.ringRadius - 2, `${s.id}: point ${x} ${y} is inside the ring`);
    }
  }
  // the bots the pilot shoots fly Javelins, the ship arena.conf makes weak (Warbirds have 3000 energy here)
  for (const s of script.steps) for (const c of [...(s.cast || []), ...(s.leanCast || [])])
    if (c.role !== "teammate") assert.match(c.script, /^ship 2$/m, `${s.id}: ${c.role} flies a Javelin`);
  const conf = fs.readFileSync(path.join(ARENA, "arena.conf"), "utf8");
  const get = (sec, key) => {
    let cur = "";
    for (const raw of conf.split(/\r?\n/)) {
      const line = raw.replace(/;.*$/, "").trim();
      const m = /^\[\s*(.+?)\s*\]$/.exec(line);
      if (m) cur = m[1].toLowerCase();
      else if (cur === sec.toLowerCase()) { const kv = /^([^=]+?)\s*=\s*(.*)$/.exec(line); if (kv && kv[1].toLowerCase() === key.toLowerCase()) return kv[2]; }
    }
    return undefined;
  };
  assert.match(conf, /^#include conf\/svs\/svs\.conf$/m);
  assert.equal(get("General", "Map"), "training.lvl");
  assert.equal(get("General", "LevelFiles"), "tutorial.lvz");
  assert.deepEqual([+get("Spawn", "Team0-X"), +get("Spawn", "Team0-Y")], [S.start.x, S.start.y]);
  assert.deepEqual([+get("Flag", "SpawnX"), +get("Flag", "SpawnY")], [S.flag.x, S.flag.y]);
  assert.deepEqual([+get("Soccer", "SpawnX"), +get("Soccer", "SpawnY")], [S.ball.x, S.ball.y]);
  assert.equal(+get("Flag", "FlagCount"), 1);
  assert.equal(+get("Soccer", "BallCount"), 1);
  assert.ok(+get("Warbird", "InitialEnergy") >= 3000 && +get("Warbird", "MaximumEnergy") >= 3000);
  assert.ok(+get("Bullet", "BulletDamageLevel") > 0);
  assert.ok(+get("Javelin", "MaximumEnergy") < 500);
  assert.equal(+get("Prize", "EngineShutdownTime"), 32767);
  // greens appear around the map centre (Prize:MinimumVirtual tiles): that is the green field
  assert.ok(Math.hypot(S.green.x - 512, S.green.y - 512) <= 2 && +get("Prize", "MinimumVirtual") <= S.greenRadius);
  assert.ok(+get("Team", "MaxFrequency") < +get("Team", "PrivFreqStart"), "no private freqs");
  assert.equal(+get("Kill", "FixedKillReward"), 0);
});
