// Random arenas for the map editor: Lvl.generate(seed, options) gives a 1024x1024 tile grid.
// How a good one is made:
//  - drawn on a coarse grid (one cell = 3x3 tiles) so passages are wide enough to fly and fight in;
//  - a layout lays out rock and open space: caves, a base of rooms and corridors, rings, spokes, a
//    maze, an asteroid field, or a team arena with a fortress and flag room each side;
//  - mirrored (2-way or 4-way) so every team and spawn sees the same map, or left as it is (1-way);
//  - a solid border, stray single blocks smoothed away, and no sealed pockets: every open cell is
//    reachable from the centre (big pockets get a tunnel, small ones are filled), so no spawn is
//    shut in;
//  - then turned into tiles (walls that hug the open space, one or three tiles thick) and, when a
//    tile layout is given, auto-tiled so lines, corners and block edges join up (autotile.js).
// Works in the browser (window.Lvl.generate) and in Node (for tests).
"use strict";

(() => {
  const N = 1024;
  const root = typeof window !== "undefined" ? window : globalThis;

  // a small fast PRNG (mulberry32), so a seed always gives the same map
  function rng(seed) {
    let a = seed >>> 0;
    return () => {
      a = (a + 0x6d2b79f5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  const TILE_BUDGET = 440000; // tiles (4 bytes each): keeps a map with its tileset under 2 MB
  const SIZES = { small: 100, medium: 140, large: 200, huge: 300 }; // cells across (x3 tiles: 300 to 900 of 1024)
  // layouts drawn with smooth edges (rounded, diagonal); the others keep crisp right angles
  const SMOOTH = new Set(["caves", "rings", "spokes", "asteroids", "teams"]);
  const LAYOUTS = {
    caves: "Caves",
    base: "Base: rooms and corridors",
    rings: "Rings",
    spokes: "Spokes",
    maze: "Maze",
    asteroids: "Asteroid field",
    teams: "Team arena with flag rooms",
  };

  /** A C x C cell map (1 = rock) with drawing helpers. */
  function Cells(C, fill) {
    const a = new Uint8Array(C * C).fill(fill);
    const inside = (x, y) => x >= 0 && y >= 0 && x < C && y < C;
    const set = (x, y, v) => { if (inside(x, y)) a[y * C + x] = v; };
    return {
      a, C, inside, set,
      get: (x, y) => (inside(x, y) ? a[y * C + x] : 1),
      rect(x0, y0, w, h, v) { for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) set(x, y, v); },
      disc(cx, cy, r, v) {
        for (let y = Math.floor(cy - r); y <= Math.ceil(cy + r); y++)
          for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++)
            if (Math.hypot(x - cx, y - cy) <= r) set(x, y, v);
      },
      ring(cx, cy, r, w, v) {
        for (let y = Math.floor(cy - r - w); y <= Math.ceil(cy + r + w); y++)
          for (let x = Math.floor(cx - r - w); x <= Math.ceil(cx + r + w); x++) {
            const d = Math.hypot(x - cx, y - cy);
            if (d >= r - w / 2 && d <= r + w / 2) set(x, y, v);
          }
      },
      // a thick straight line from (x0, y0) to (x1, y1)
      line(x0, y0, x1, y1, w, v) {
        const n = Math.ceil(Math.hypot(x1 - x0, y1 - y0) * 2) + 1;
        for (let i = 0; i <= n; i++) {
          const x = x0 + (x1 - x0) * i / n, y = y0 + (y1 - y0) * i / n;
          this.disc(x, y, w / 2, v);
        }
      },
    };
  }

  // ---- layouts: each draws the whole map (rock = 1); the canonical part is then mirrored ----------

  // caves: smooth random hills (noise blurred twice), cut at the height where `density` of the map
  // is rock, then tidied by a majority rule: blobs and tunnels whose amount follows the slider
  function caves(c, rand, o) {
    const { C } = c;
    let f = new Float32Array(C * C);
    for (let i = 0; i < C * C; i++) f[i] = rand();
    // mirror the noise first, so the caves come out symmetric
    if (o.symmetry >= 2) for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) { const [sx, sy] = canon(C, o.symmetry, x, y); f[y * C + x] = f[sy * C + sx]; }
    const r = Math.max(2, Math.round(C / 45));
    for (let pass = 0; pass < 2; pass++) for (const horiz of [true, false]) {
      const g = new Float32Array(C * C);
      for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
        let sum = 0, n = 0;
        for (let k = -r; k <= r; k++) {
          const xx = horiz ? x + k : x, yy = horiz ? y : y + k;
          if (xx < 0 || yy < 0 || xx >= C || yy >= C) continue;
          sum += f[yy * C + xx]; n++;
        }
        g[y * C + x] = sum / n;
      }
      f = g;
    }
    const sorted = Float32Array.from(f).sort();
    const cut = sorted[Math.min(C * C - 1, Math.floor((1 - o.density) * C * C))];
    for (let i = 0; i < C * C; i++) c.a[i] = f[i] >= cut ? 1 : 0;
    for (let round = 0; round < 2; round++) {
      const next = c.a.slice();
      for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
        let n = 0;
        for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) if (dx || dy) n += c.get(x + dx, y + dy);
        if (n >= 6) next[y * C + x] = 1; else if (n <= 2) next[y * C + x] = 0;
      }
      c.a.set(next);
    }
  }

  // rooms joined by two-wide corridors, each room to the nearest one already joined; big rooms get
  // pillars to fight around
  function base(c, rand, o, specials) {
    const { C } = c;
    const rooms = [];
    const want = Math.round((C * C) / 260 * (1.5 - 1.8 * (o.density - 0.2)));
    for (let tries = 0; rooms.length < want && tries < want * 30; tries++) {
      const w = 4 + Math.floor(rand() * 8), h = 4 + Math.floor(rand() * 8);
      const x = 3 + Math.floor(rand() * (C - w - 6)), y = 3 + Math.floor(rand() * (C - h - 6));
      if (rooms.some((r) => x < r.x + r.w + 2 && r.x < x + w + 2 && y < r.y + r.h + 2 && r.y < y + h + 2)) continue;
      rooms.push({ x, y, w, h, cx: x + (w >> 1), cy: y + (h >> 1) });
    }
    const mid = C >> 1, hall = Math.max(4, Math.round(C * 0.07));
    rooms.push({ x: mid - hall, y: mid - hall, w: hall * 2, h: hall * 2, cx: mid, cy: mid, hall: true });
    for (const r of rooms) c.rect(r.x, r.y, r.w, r.h, 0);
    // corridors: join each room to the nearest joined room, centre hall first
    const joined = [rooms[rooms.length - 1]];
    const rest = rooms.slice(0, -1);
    while (rest.length) {
      let best = null;
      for (const r of rest) for (const j of joined) {
        const d = Math.abs(r.cx - j.cx) + Math.abs(r.cy - j.cy);
        if (!best || d < best.d) best = { r, j, d };
      }
      const { r, j } = best;
      const w = rand() < 0.3 ? 3 : 2;
      if (rand() < 0.5) { c.rect(Math.min(r.cx, j.cx), r.cy, Math.abs(r.cx - j.cx) + w, w, 0); c.rect(j.cx, Math.min(r.cy, j.cy), w, Math.abs(r.cy - j.cy) + w, 0); }
      else { c.rect(r.cx, Math.min(r.cy, j.cy), w, Math.abs(r.cy - j.cy) + w, 0); c.rect(Math.min(r.cx, j.cx), j.cy, Math.abs(r.cx - j.cx) + w, w, 0); }
      joined.push(r); rest.splice(rest.indexOf(r), 1);
    }
    for (const r of rooms) {
      if (!r.hall && r.w >= 8 && r.h >= 8) { c.rect(r.x + 2, r.y + 2, 1, 1, 1); c.rect(r.x + r.w - 3, r.y + 2, 1, 1, 1); c.rect(r.x + 2, r.y + r.h - 3, 1, 1, 1); c.rect(r.x + r.w - 3, r.y + r.h - 3, 1, 1, 1); }
    }
    if (o.flags) {
      // a flag in the room farthest from the centre (mirrored into each part of the map)
      const far = rooms.filter((r) => !r.hall).sort((a, b) => Math.hypot(b.cx - mid, b.cy - mid) - Math.hypot(a.cx - mid, a.cy - mid));
      if (far[0]) specials.push({ x: far[0].cx, y: far[0].cy, tile: 170 });
    }
  }

  // concentric walls with gaps, and some cover between them
  function rings(c, rand, o) {
    const { C } = c, mid = (C - 1) / 2;
    c.rect(0, 0, C, C, 0);
    const t = (o.density - 0.2) / 0.4; // 0 open .. 1 tight: rings closer together and thicker
    const step = Math.round(12 - 5 * t) + Math.floor(rand() * 3);
    for (let r = step + 3; r < C / 2 - 4; r += step) {
      const w = (rand() < 0.4 ? 2.4 : 1.6) + t * 1.2;
      c.ring(mid, mid, r, w, 1);
      const gaps = 2 + Math.floor(rand() * 3), turn = rand() * Math.PI * 2;
      for (let g = 0; g < gaps; g++) {
        const a = turn + (g / gaps) * Math.PI * 2 + (rand() - 0.5) * 0.4;
        c.disc(mid + Math.cos(a) * r, mid + Math.sin(a) * r, 2.2 + rand(), 0);
      }
    }
    scatter(c, rand, o.density * 0.8, 1, 2 + t);
  }

  // a hub with walls radiating out, broken by gaps
  function spokes(c, rand, o) {
    const { C } = c, mid = (C - 1) / 2;
    c.rect(0, 0, C, C, 0);
    const hub = Math.max(5, Math.round(C * 0.09));
    c.ring(mid, mid, hub, 2, 1);
    const t = (o.density - 0.2) / 0.4; // 0 open .. 1 tight: more and thicker spokes
    const k = [6, 8, 10, 12][Math.min(3, Math.floor(rand() * 2 + t * 2.5))], turn = rand() * Math.PI;
    for (let i = 0; i < k; i++) {
      const a = turn + (i / k) * Math.PI * 2;
      const r0 = hub + 3, r1 = C / 2 - 3;
      c.line(mid + Math.cos(a) * r0, mid + Math.sin(a) * r0, mid + Math.cos(a) * r1, mid + Math.sin(a) * r1, 2.4 + t * 1.6, 1);
      const gap = r0 + 4 + rand() * (r1 - r0 - 8);
      c.disc(mid + Math.cos(a) * gap, mid + Math.sin(a) * gap, 2.5, 0);
    }
    // doors in the hub between the spokes
    for (let i = 0; i < k; i += 2) {
      const a = turn + ((i + 0.5) / k) * Math.PI * 2;
      c.disc(mid + Math.cos(a) * hub, mid + Math.sin(a) * hub, 2.2, 0);
    }
    if (rand() < 0.6) c.ring(mid, mid, C * 0.36, 1.6, 1);
    scatter(c, rand, o.density * 0.9, 1, 1.5 + t * 1.5);
  }

  // a maze of two-cell passages; lower density knocks more walls out (loops, so no dead-end camping)
  function maze(c, rand, o) {
    const { C } = c, U = o.density < 0.4 ? 4 : 3; // passages 3 cells wide when open, 2 when tight; walls 1 cell
    const M = Math.floor((C - 1) / U);
    const seen = new Uint8Array(M * M);
    const stack = [[M >> 1, M >> 1]];
    seen[(M >> 1) * M + (M >> 1)] = 1;
    const open = (mx, my) => c.rect(1 + mx * U, 1 + my * U, U - 1, U - 1, 0);
    open(M >> 1, M >> 1);
    while (stack.length) {
      const [mx, my] = stack[stack.length - 1];
      const nbrs = [[1, 0], [-1, 0], [0, 1], [0, -1]].map(([dx, dy]) => [mx + dx, my + dy, dx, dy])
        .filter(([x, y]) => x >= 0 && y >= 0 && x < M && y < M && !seen[y * M + x]);
      if (!nbrs.length) { stack.pop(); continue; }
      const [nx, ny, dx, dy] = nbrs[Math.floor(rand() * nbrs.length)];
      seen[ny * M + nx] = 1;
      open(nx, ny);
      // knock out the wall between
      c.rect(1 + mx * U + (dx > 0 ? U - 1 : dx < 0 ? -1 : 0), 1 + my * U + (dy > 0 ? U - 1 : dy < 0 ? -1 : 0),
        dx ? 1 : U - 1, dy ? 1 : U - 1, 0);
      stack.push([nx, ny]);
    }
    // braid: knock out extra walls
    const extra = Math.round(M * M * (0.55 - o.density) * 0.6);
    for (let i = 0; i < extra; i++) {
      const mx = Math.floor(rand() * (M - 1)), my = Math.floor(rand() * (M - 1));
      if (rand() < 0.5) c.rect(1 + mx * U + U - 1, 1 + my * U, 1, U - 1, 0);
      else c.rect(1 + mx * U, 1 + my * U + U - 1, U - 1, 1, 0);
    }
  }

  // open space with rock blobs, and asteroids drifting between them
  function asteroids(c, rand, o, specials) {
    const { C } = c;
    c.rect(0, 0, C, C, 0);
    // a few big rocks, more middling ones, and a scatter of small ones
    const t = (o.density - 0.2) / 0.4;
    scatter(c, rand, 0.1 + t * 0.6, 4, 7);
    scatter(c, rand, 0.2 + t * 0.9, 2, 3.5);
    scatter(c, rand, 0.2 + t * 0.6, 0.6, 1.4);
    const n = Math.round(C * C / 90 * o.density);
    for (let i = 0; i < n; i++) {
      const x = 3 + Math.floor(rand() * (C - 6)), y = 3 + Math.floor(rand() * (C - 6));
      specials.push({ x, y, tile: [216, 218, 217][Math.floor(rand() * 3)] });
    }
  }

  // left vs right: a fortress each side with a flag room, a field of cover between
  function teams(c, rand, o, specials) {
    const { C } = c, mid = (C - 1) / 2;
    caves(c, rand, Object.assign({}, o, { density: o.density * 0.8 })); // the field: a little more open than caves
    const fw = Math.round(C * 0.22), fh = Math.round(C * 0.4);
    const fx = 3, fy = Math.round(mid - fh / 2);
    // the fortress: open inside, a wall ring two cells thick, gates toward the field
    c.rect(fx, fy, fw, fh, 1);
    c.rect(fx + 2, fy + 2, fw - 4, fh - 4, 0);
    const gate = Math.max(3, Math.round(fh * 0.12));
    c.rect(fx + fw - 2, Math.round(mid - gate / 2), 2, gate, 0); // front gate
    c.rect(fx + fw - 2, fy + 3, 2, gate, 0);
    c.rect(fx + fw - 2, fy + fh - 3 - gate, 2, gate, 0);
    // the flag room at the back: walls with one opening, the flag inside
    const rw = Math.max(5, Math.round(fw * 0.45)), rh = Math.max(6, Math.round(fh * 0.3));
    const rx = fx + 3, ry = Math.round(mid - rh / 2);
    c.rect(rx, ry, rw, rh, 1);
    c.rect(rx + 1, ry + 1, rw - 2, rh - 2, 0);
    c.rect(rx + rw - 1, Math.round(mid - 1), 1, 3, 0);
    specials.push({ x: rx + Math.floor(rw / 2) - 1, y: Math.round(mid), tile: 170 });
    // a lane from the gate to the centre so the bases are never far from the fight
    c.rect(fx + fw, Math.round(mid - 2), Math.round(mid - fx - fw) + 1, 4, 0);
  }

  // rock blobs over open space: amount 0..1, radius up to r
  function scatter(c, rand, amount, rMin, rMax) {
    const { C } = c;
    const n = Math.round((C * C) / (rMax * rMax * 9) * amount);
    for (let i = 0; i < n; i++) {
      const x = 2 + rand() * (C - 4), y = 2 + rand() * (C - 4), r = rMin + rand() * (rMax - rMin);
      c.disc(x, y, r, 1);
      if (r > 1.5 && rand() < 0.6) c.disc(x + (rand() - 0.5) * r * 1.6, y + (rand() - 0.5) * r * 1.6, r * 0.7, 1); // lumpy
    }
  }

  // ---- shared steps ------------------------------------------------------------------------------

  // the canonical part (left half for 2-way, top-left quarter for 4-way) copied over the rest
  const canon = (C, sym, x, y) => {
    const half = Math.ceil(C / 2);
    const sx = sym >= 2 ? (x < half ? x : C - 1 - x) : x;
    const sy = sym === 4 ? (y < half ? y : C - 1 - y) : y;
    return [sx, sy];
  };
  function mirror(c, sym) {
    if (sym < 2) return;
    const { C, a } = c;
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) { const [sx, sy] = canon(C, sym, x, y); a[y * C + x] = a[sy * C + sx]; }
  }
  // all the mirror images of (x, y), itself included
  function images(C, sym, x, y) {
    const out = [[x, y]];
    if (sym >= 2) out.push([C - 1 - x, y]);
    if (sym === 4) out.push([x, C - 1 - y], [C - 1 - x, C - 1 - y]);
    return out;
  }
  // open where any mirror image is open (joins up again after tunnels; keeps everything reachable)
  function unionOpen(c, sym) {
    if (sym < 2) return;
    const { C, a } = c, next = a.slice();
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++)
      if (images(C, sym, x, y).some(([ix, iy]) => !a[iy * C + ix])) next[y * C + x] = 0;
    a.set(next);
  }

  // open cells reachable from (sx, sy)
  function reach(c, sx, sy) {
    const { C, a } = c, seen = new Uint8Array(C * C);
    if (a[sy * C + sx]) return seen;
    const q = [sy * C + sx];
    seen[q[0]] = 1;
    while (q.length) {
      const i = q.pop(), x = i % C, y = (i - x) / C;
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const nx = x + dx, ny = y + dy, j = ny * C + nx;
        if (nx < 0 || ny < 0 || nx >= C || ny >= C || seen[j] || a[j]) continue;
        seen[j] = 1; q.push(j);
      }
    }
    return seen;
  }

  // every open cell joined to the centre: small pockets filled, big ones get a two-wide tunnel to
  // the nearest reachable space
  function connect(c, mid) {
    const { C, a } = c;
    for (let pass = 0; pass < 64; pass++) {
      const seen = reach(c, mid, mid);
      // the first open cell not reached, and its whole pocket
      let start = -1;
      for (let i = 0; i < C * C; i++) if (!a[i] && !seen[i]) { start = i; break; }
      if (start < 0) return;
      const pocket = [], inP = new Uint8Array(C * C), q = [start];
      inP[start] = 1;
      while (q.length) {
        const i = q.pop(); pocket.push(i);
        const x = i % C, y = (i - x) / C;
        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
          const nx = x + dx, ny = y + dy, j = ny * C + nx;
          if (nx < 0 || ny < 0 || nx >= C || ny >= C || inP[j] || a[j]) continue;
          inP[j] = 1; q.push(j);
        }
      }
      if (pocket.length < 12) { for (const i of pocket) a[i] = 1; continue; }
      // breadth-first from the pocket through rock until reaching the reachable space
      const from = new Int32Array(C * C).fill(-1), bq = [];
      for (const i of pocket) { from[i] = i; bq.push(i); }
      let hit = -1;
      for (let h = 0; h < bq.length && hit < 0; h++) {
        const i = bq[h], x = i % C, y = (i - x) / C;
        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
          const nx = x + dx, ny = y + dy, j = ny * C + nx;
          if (nx < 2 || ny < 2 || nx >= C - 3 || ny >= C - 3 || from[j] >= 0) continue;
          from[j] = i;
          if (seen[j]) { hit = j; break; }
          bq.push(j);
        }
      }
      if (hit < 0) { for (const i of pocket) a[i] = 1; continue; }
      for (let i = from[hit]; !inP[i]; i = from[i]) {
        const x = i % C, y = (i - x) / C;
        c.rect(x, y, 2, 2, 0);
      }
    }
  }

  // a lone block in open space goes, a lone gap in rock is filled (from a copy, so it stays symmetric)
  function smooth(c) {
    const { C, a } = c, next = a.slice();
    for (let y = 1; y < C - 1; y++) for (let x = 1; x < C - 1; x++) {
      const i = y * C + x, n = a[i - 1] + a[i + 1] + a[i - C] + a[i + C];
      if (a[i] && n === 0) next[i] = 0;
      else if (!a[i] && n === 4) next[i] = 1;
    }
    a.set(next);
  }

  // the open cell of the canonical part farthest from rock and at least a quarter of the way out
  // from the centre: where a flag can be fought over
  function flagSpots(c, sym) {
    const { C, a } = c, d = new Int32Array(C * C).fill(-1), q = [];
    for (let i = 0; i < C * C; i++) if (a[i]) { d[i] = 0; q.push(i); }
    for (let h = 0; h < q.length; h++) {
      const i = q[h], x = i % C, y = (i - x) / C;
      for (const [nx, ny] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
        if (nx < 0 || ny < 0 || nx >= C || ny >= C || d[ny * C + nx] >= 0) continue;
        d[ny * C + nx] = d[i] + 1; q.push(ny * C + nx);
      }
    }
    const mid = (C - 1) / 2;
    let best = null;
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      const [sx, sy] = canon(C, sym, x, y);
      if (sx !== x || sy !== y || a[y * C + x]) continue;
      if (Math.hypot(x - mid, y - mid) < C * 0.22) continue;
      const score = d[y * C + x] * 1000 + Math.hypot(x - mid, y - mid); // roomiest, then farthest out
      if (!best || score > best.score) best = { x, y, score };
    }
    return best ? [{ x: best.x, y: best.y, tile: 170 }] : [];
  }

  /**
   * options:
   *  layout: one of LAYOUTS (default caves); size: small|medium|large|huge;
   *  density 0.2-0.6 (how much rock); symmetry 1|2|4 (team layout is always 2);
   *  thickness: 0 solid rock (default: the rock is wall all through, drawn with the pack's plain
   *    interior), or walls 1 (lines) to 3 tiles thick round hollow rock;
   *  flags: put flags in, in the open spot of each part of the map farthest from any wall (the
   *    base layout uses its farthest rooms, the team layout always has its flag rooms);
   *  tile: the wall tile (1-161) when there's no tile layout; tiles: an Autotile layout to tidy with.
   * Returns { grid, cells, C, seed, layout, specials }.
   */
  function generate(seed, options = {}) {
    const rand = rng(seed);
    const layout = LAYOUTS[options.layout] ? options.layout : "caves";
    const C = SIZES[options.size] ?? SIZES.medium;
    const density = Math.min(0.6, Math.max(0.2, options.density ?? 0.45));
    const symmetry = layout === "teams" ? 2 : [1, 2, 4].includes(options.symmetry) ? options.symmetry : 4;
    const thickness = Math.min(3, Math.max(0, Math.round(options.thickness ?? 0)));
    const tile = Math.min(161, Math.max(1, options.tile ?? 1));
    const o = { density, symmetry, flags: !!options.flags };
    const mid = Math.floor((C - 1) / 2);

    const c = Cells(C, 1);
    const specials = [];
    ({ caves, base, rings, spokes, maze, asteroids, teams })[layout](c, rand, o, specials);
    mirror(c, symmetry);

    // a solid border two cells thick, and an open centre (spawns and the fight start there)
    const open = layout === "maze" ? 3 : Math.max(4, Math.round(C * 0.06));
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      if (x < 2 || y < 2 || x >= C - 2 || y >= C - 2) c.a[y * C + x] = 1;
      else if (Math.hypot(x - (C - 1) / 2, y - (C - 1) / 2) <= open) c.a[y * C + x] = 0;
    }
    smooth(c);
    connect(c, mid);
    unionOpen(c, symmetry);

    // flags for layouts without their own: the canonical part's open cell farthest from rock,
    // well away from the centre (where everyone starts), mirrored into each part
    if (o.flags && layout !== "base" && layout !== "teams") specials.push(...flagSpots(c, symmetry));

    // specials: those in the canonical part, mirrored, on open cells only
    const placed = [], taken = new Set();
    for (const s of specials) {
      const [sx, sy] = canon(C, symmetry, s.x, s.y);
      if (sx !== s.x || sy !== s.y) continue;
      for (const [x, y] of images(C, symmetry, s.x, s.y)) {
        if (c.get(x, y) || taken.has(y * C + x)) continue;
        taken.add(y * C + x);
        placed.push({ x, y, tile: s.tile });
      }
    }

    // to tiles: each cell 3x3, centred on the map. Smooth layouts take each tile's rock from the
    // cells round it (bilinear, a saddle counts as rock), so edges run diagonally and round instead
    // of in three-tile stairs; open cells that meet edge to edge stay joined, so nothing is cut off.
    const S = 3, T = C * S, origin = Math.floor(N / 2 - T / 2);
    const R = new Uint8Array(T * T);
    const cellAt = (x, y) => (x < 0 || y < 0 || x >= C || y >= C ? 1 : c.a[y * C + x]);
    const smoothEdges = SMOOTH.has(layout);
    for (let ty = 0; ty < T; ty++) for (let tx = 0; tx < T; tx++) {
      if (!smoothEdges) { R[ty * T + tx] = cellAt(Math.floor(tx / S), Math.floor(ty / S)); continue; }
      const u = (tx + 0.5) / S - 0.5, v = (ty + 0.5) / S - 0.5, x0 = Math.floor(u), y0 = Math.floor(v), fx = u - x0, fy = v - y0;
      const val = cellAt(x0, y0) * (1 - fx) * (1 - fy) + cellAt(x0 + 1, y0) * fx * (1 - fy) + cellAt(x0, y0 + 1) * (1 - fx) * fy + cellAt(x0 + 1, y0 + 1) * fx * fy;
      R[ty * T + tx] = val >= 0.5 ? 1 : 0;
    }
    // each rock tile's distance (in tiles, chessboard) to the nearest open tile; outside the map counts as rock
    const D = new Int32Array(T * T), BIG = 1 << 20;
    const dAt = (x, y) => (x < 0 || y < 0 || x >= T || y >= T ? BIG : D[y * T + x]);
    for (let ty = 0; ty < T; ty++) for (let tx = 0; tx < T; tx++) {
      const i = ty * T + tx;
      D[i] = R[i] ? Math.min(BIG, dAt(tx - 1, ty) + 1, dAt(tx, ty - 1) + 1, dAt(tx - 1, ty - 1) + 1, dAt(tx + 1, ty - 1) + 1) : 0;
    }
    for (let ty = T - 1; ty >= 0; ty--) for (let tx = T - 1; tx >= 0; tx--) {
      const i = ty * T + tx;
      if (R[i]) D[i] = Math.min(D[i], dAt(tx + 1, ty) + 1, dAt(tx, ty + 1) + 1, dAt(tx + 1, ty + 1) + 1, dAt(tx - 1, ty + 1) + 1);
    }
    // walls: rock within `depth` tiles of open space. Solid rock goes all the way in, unless that
    // would pass the tile budget (a .lvl must stay under 2 MB): then as deep as the budget allows
    let depth = thickness;
    if (thickness === 0) {
      const hist = new Map();
      let count = 0;
      for (let i = 0; i < T * T; i++) if (R[i]) { count++; hist.set(D[i], (hist.get(D[i]) || 0) + 1); }
      depth = BIG;
      if (count > TILE_BUDGET) {
        let sum = 0; depth = 0;
        for (const d of [...hist.keys()].sort((a, b) => a - b)) { if (sum + hist.get(d) > TILE_BUDGET) break; sum += hist.get(d); depth = d; }
        depth = Math.max(3, depth);
      }
    }
    const grid = new Uint8Array(N * N);
    const k = Math.min(depth, 3);
    for (let ty = 0; ty < T; ty++) for (let tx = 0; tx < T; tx++) {
      const i = ty * T + tx;
      if (!R[i]) continue;
      const border = tx < k || ty < k || tx >= T - k || ty >= T - k; // the outer border is a wall too
      if (D[i] <= depth || border) grid[(origin + ty) * N + origin + tx] = tile;
    }
    for (const s of placed) grid[(origin + s.y * S + 1) * N + origin + s.x * S + 1] = s.tile;
    if (options.tiles && root.Autotile) root.Autotile.tidy(grid, options.tiles);
    return { grid, cells: c.a, C, seed, layout, symmetry, specials: placed, solidDepth: thickness === 0 ? depth : null };
  }

  root.Lvl = Object.assign(root.Lvl || {}, { generate, GEN_LAYOUTS: LAYOUTS, GEN_SIZES: SIZES });
})();
