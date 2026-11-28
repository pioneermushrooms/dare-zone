// The training arena's map: a walled 200x200 play area in the middle of the 1024x1024 grid with
// the tutorial's places. SPOTS are tile coordinates; script.json (Reach checks, cast points) and
// deploy/arenas/#training/arena.conf (spawns, flag and ball) use the same numbers, and
// tests/js/tutorial-lvz.test.js checks they agree.
//
//   start  - where the Warbird spawns (Spawn:Team0-X/Y)
//   beacon - the spot to fly to (LVZ map object 4200), the centre of the ring
//   ring   - a round wall with four gaps; the target and the swarm skit happen inside
//   green  - the green field: open space at the map centre, where Continuum puts greens
//            (Prize:MinimumVirtual tiles around the centre)
//   safe   - a block of safe-zone tiles
//   flag   - Flag:SpawnX/Y of the one carried flag
//   ball   - Soccer:SpawnX/Y of the one ball; the goal is a mouth in the east wall nearby
"use strict";

const path = require("node:path");
const fs = require("node:fs");

const N = 1024;
const WALL = 1, SAFE = 171, GOAL = 172;
const BOUNDS = { lo: 412, hi: 611 };
const SPOTS = {
  start: { x: 450, y: 562 },
  beacon: { x: 460, y: 462 }, ringRadius: 22,
  target: { x: 468, y: 456 },
  green: { x: 512, y: 512 }, greenRadius: 20,
  safe: { x: 570, y: 450 },
  flag: { x: 570, y: 522 },
  ball: { x: 560, y: 585 },
  goal: { x: 608, y: 585 },
};

function build() {
  const g = new Uint8Array(N * N);
  const set = (x, y, t) => { g[y * N + x] = t; };
  const rect = (x0, y0, x1, y1, t) => { for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) set(x, y, t); };
  const { lo, hi } = BOUNDS;

  // border, two tiles thick
  rect(lo, lo, hi, lo + 1, WALL); rect(lo, hi - 1, hi, hi, WALL);
  rect(lo, lo, lo + 1, hi, WALL); rect(hi - 1, lo, hi, hi, WALL);

  // the ring round the beacon, with a gap at each compass point
  const b = SPOTS.beacon, r = SPOTS.ringRadius;
  for (let y = b.y - r - 2; y <= b.y + r + 2; y++) for (let x = b.x - r - 2; x <= b.x + r + 2; x++) {
    const d = Math.hypot(x - b.x, y - b.y);
    if (d < r - 0.8 || d > r + 0.8) continue;
    if (Math.abs(x - b.x) <= 3 || Math.abs(y - b.y) <= 3) continue; // gaps
    set(x, y, WALL);
  }

  // safe zone, in a three-sided bay
  const s = SPOTS.safe;
  rect(s.x - 4, s.y - 4, s.x + 4, s.y + 4, SAFE);
  rect(s.x - 6, s.y - 6, s.x + 6, s.y - 6, WALL);
  rect(s.x + 6, s.y - 6, s.x + 6, s.y + 6, WALL);
  rect(s.x - 6, s.y + 6, s.x + 6, s.y + 6, WALL);

  // the flag on a small pad of corner posts
  const f = SPOTS.flag;
  for (const [dx, dy] of [[-5, -5], [4, -5], [-5, 4], [4, 4]]) rect(f.x + dx, f.y + dy, f.x + dx + 1, f.y + dy + 1, WALL);

  // the goal: a mouth in the east wall with posts either side
  const gl = SPOTS.goal;
  rect(gl.x, gl.y - 8, gl.x + 1, gl.y + 8, GOAL);
  rect(gl.x - 8, gl.y - 10, gl.x + 1, gl.y - 9, WALL);
  rect(gl.x - 8, gl.y + 9, gl.x + 1, gl.y + 10, WALL);

  // a few rocks so it doesn't read as an empty box
  rect(520, 436, 524, 439, WALL);
  rect(428, 510, 430, 517, WALL);
  rect(590, 486, 594, 488, WALL);
  rect(480, 590, 486, 592, WALL);
  rect(536, 548, 538, 551, WALL);

  // walls get the standard tile layout's pieces (as the map editor does)
  globalThis.window ??= globalThis;
  const www = path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot");
  require(path.join(www, "autotile.js"));
  const spec = JSON.parse(fs.readFileSync(path.join(www, "tile-layout.json"), "utf8"));
  const layout = window.Autotile.fromSpec(spec).layouts[0];
  if (!layout) throw new Error("tile-layout.json has no layout");
  window.Autotile.tidy(g, layout, [lo, lo, hi, hi]);
  return g;
}

/** The map as .lvl bytes (no tileset: Continuum's default). */
function lvl(grid = build()) {
  globalThis.window ??= globalThis;
  require(path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot/lvl.js"));
  return window.Lvl.serialize(grid, null);
}

module.exports = { BOUNDS, SPOTS, build, lvl };
