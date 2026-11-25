// Random arenas for the map editor: Lvl.generate(seed, options) gives a 1024x1024 tile grid.
// How a good one is made:
//  - a cave pattern grown by cellular automata in one quarter, mirrored into the other three, so
//    every team and spawn sees the same map (competitive maps are symmetric);
//  - drawn on a coarse grid (one cell = 3x3 tiles) so passages are wide enough to fly and fight in;
//  - a solid border, an open centre, and no sealed pockets (every open cell joins the centre);
//  - stray single blocks smoothed away so the walls read as shapes, not noise.
"use strict";

(() => {
  const N = 1024;

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

  const SIZES = { small: 56, medium: 80, large: 104 }; // cells across (x3 tiles)

  /**
   * options: size "small"|"medium"|"large"; density 0.38-0.52 (how much rock); symmetry 2|4;
   * tile: the wall tile (1-161). Returns { grid, cells, seed }.
   */
  function generate(seed, options = {}) {
    const rand = rng(seed);
    const C = SIZES[options.size] ?? SIZES.medium;
    const density = Math.min(0.55, Math.max(0.3, options.density ?? 0.47));
    const symmetry = options.symmetry === 2 ? 2 : 4;
    const tile = Math.min(161, Math.max(1, options.tile ?? 1));
    const half = Math.ceil(C / 2);

    // 1. noise in one quarter (4-way) or half (2-way), mirrored, so the whole map is symmetric
    //    before it grows (the rules below are symmetric too, so it stays that way)
    let cells = new Uint8Array(C * C);
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      const sx = x < half ? x : C - 1 - x;
      const sy = symmetry === 4 ? (y < half ? y : C - 1 - y) : y;
      if (sx === x && sy === y) cells[y * C + x] = rand() < density ? 1 : 0;
    }
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      const sx = x < half ? x : C - 1 - x;
      const sy = symmetry === 4 ? (y < half ? y : C - 1 - y) : y;
      cells[y * C + x] = cells[sy * C + sx];
    }

    // 2. cellular automata over the whole map (outside is rock): rock where most neighbours are
    //    rock; the first rounds also fill large open holes, so caves form rather than noise
    const at = (g, x, y) => (x < 0 || y < 0 || x >= C || y >= C ? 1 : g[y * C + x]);
    for (let round = 0; round < 5; round++) {
      const next = new Uint8Array(C * C);
      for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
        let n1 = 0, n2 = 0;
        for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
          if (!dx && !dy) continue;
          const v = at(cells, x + dx, y + dy);
          if (Math.abs(dx) <= 1 && Math.abs(dy) <= 1) n1 += v; else n2 += v;
        }
        next[y * C + x] = n1 >= 5 || (round < 3 && n1 + n2 <= 3) ? 1 : 0;
      }
      cells = next;
    }

    // 4. a solid border two cells thick, and an open centre
    const mid = (C - 1) / 2, open = Math.max(4, Math.round(C * 0.08));
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      if (x < 2 || y < 2 || x >= C - 2 || y >= C - 2) cells[y * C + x] = 1;
      else if (Math.hypot(x - mid, y - mid) <= open) cells[y * C + x] = 0;
    }

    // 5. no sealed pockets: open cells not reachable from the centre become rock
    const seen = new Uint8Array(C * C);
    const start = Math.round(mid) * C + Math.round(mid);
    const queue = [start];
    seen[start] = 1;
    while (queue.length) {
      const i = queue.pop(), x = i % C, y = (i - x) / C;
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const nx = x + dx, ny = y + dy, j = ny * C + nx;
        if (nx < 0 || ny < 0 || nx >= C || ny >= C || seen[j] || cells[j]) continue;
        seen[j] = 1;
        queue.push(j);
      }
    }
    for (let i = 0; i < C * C; i++) if (!cells[i] && !seen[i]) cells[i] = 1;

    // 6. smooth: a lone block in open space goes, a lone gap in rock is filled
    for (let y = 1; y < C - 1; y++) for (let x = 1; x < C - 1; x++) {
      const i = y * C + x;
      const n = cells[i - 1] + cells[i + 1] + cells[i - C] + cells[i + C];
      if (cells[i] && n === 0) cells[i] = 0;
      else if (!cells[i] && n === 4) cells[i] = 1;
    }

    // 7. to tiles: each cell 3x3, centred on the map; only the rock's outline is drawn (thick
    //    solid rock is a wall two tiles deep, as in hand-made maps, not a filled field)
    const grid = new Uint8Array(N * N);
    const S = 3, origin = Math.floor(N / 2 - (C * S) / 2);
    const rock = (x, y) => x < 0 || y < 0 || x >= C || y >= C ? 1 : cells[y * C + x];
    for (let y = 0; y < C; y++) for (let x = 0; x < C; x++) {
      if (!cells[y * C + x]) continue;
      const inner = rock(x - 1, y) && rock(x + 1, y) && rock(x, y - 1) && rock(x, y + 1)
        && rock(x - 1, y - 1) && rock(x + 1, y + 1) && rock(x - 1, y + 1) && rock(x + 1, y - 1);
      const border = x < 2 || y < 2 || x >= C - 2 || y >= C - 2;
      if (inner && !border) continue;
      for (let ty = 0; ty < S; ty++) for (let tx = 0; tx < S; tx++)
        grid[(origin + y * S + ty) * N + origin + x * S + tx] = tile;
    }
    return { grid, cells, seed };
  }

  window.Lvl = Object.assign(window.Lvl || {}, { generate });
})();
