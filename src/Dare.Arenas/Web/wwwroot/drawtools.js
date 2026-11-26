// Drawing tools for the map editor, after DCME (Drake's Continuum Map Editor): pencil, line,
// rectangle, rounded rectangle, ellipse, polygon, curve, spray, bucket fill, replace, eyedropper,
// text, eraser, and selections (rectangle, lasso, magic wand) to copy, cut, paste, move, flip,
// rotate and delete. Every tool draws with a wall thickness in tiles, can be mirrored about the
// map's centre, and is auto-tiled as it lands (autotile.js): a rectangle gets corners and
// continuous edges, a line its end caps, thick walls fill solid, and the walls it touches re-join.
//
//   Shapes  pure geometry on tile cells (a cell is its index y * 1024 + x): node-testable
//   Font    a blocky 5x7 tile font for the text tool
//   mount(ed)  the tools in the editor: editor.js hands over pointer events, keys and the overlay
// Works in the browser (window.DrawTools) and in Node (for tests).
"use strict";

(() => {
  const N = 1024;
  const root = typeof window !== "undefined" ? window : globalThis;
  const inside = (x, y) => x >= 0 && y >= 0 && x < N && y < N;
  const idx = (x, y) => y * N + x;
  const add = (set, x, y) => { if (inside(x, y)) set.add(idx(x, y)); };

  // ---- shapes: each returns a Set of cell indices --------------------------------------------------

  // a line whose cells join edge to edge (no diagonal-only steps, so a one-tile wall stays one wall)
  function linePts(x0, y0, x1, y1, out = []) {
    let dx = Math.abs(x1 - x0), dy = -Math.abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
    let err = dx + dy, x = x0, y = y0;
    out.push([x, y]);
    while (x !== x1 || y !== y1) {
      const e2 = 2 * err;
      if (e2 >= dy && e2 <= dx) {
        // a diagonal step: go along the longer axis first, then the other
        if (dx >= -dy) { err += dy; x += sx; out.push([x, y]); err += dx; y += sy; }
        else { err += dx; y += sy; out.push([x, y]); err += dy; x += sx; }
      } else if (e2 >= dy) { err += dy; x += sx; }
      else { err += dx; y += sy; }
      out.push([x, y]);
    }
    return out;
  }
  // a path through points, then widened to t tiles (a t x t square brush, centred)
  function path(points, t = 1, closed = false) {
    const set = new Set();
    const pts = [];
    for (let k = 0; k < points.length - 1; k++) linePts(points[k][0], points[k][1], points[k + 1][0], points[k + 1][1], pts);
    if (closed && points.length > 2) linePts(points[points.length - 1][0], points[points.length - 1][1], points[0][0], points[0][1], pts);
    if (points.length === 1) pts.push(points[0]);
    return brush(pts, t, set);
  }
  function brush(pts, t, set = new Set()) {
    const o = Math.floor((t - 1) / 2);
    for (const [x, y] of pts) for (let dy = 0; dy < t; dy++) for (let dx = 0; dx < t; dx++) add(set, x - o + dx, y - o + dy);
    return set;
  }
  const line = (x0, y0, x1, y1, t = 1) => path([[x0, y0], [x1, y1]], t);

  // a filled region from an inside test over a box; filled, or just its rim t tiles deep (inward,
  // so the shape keeps the size you dragged)
  function region(x0, y0, x1, y1, test, t, filled) {
    const bx0 = Math.min(x0, x1), by0 = Math.min(y0, y1), w = Math.abs(x1 - x0) + 1, h = Math.abs(y1 - y0) + 1;
    const inS = new Uint8Array(w * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) inS[y * w + x] = test(bx0 + x, by0 + y) ? 1 : 0;
    const set = new Set();
    if (filled) { for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (inS[y * w + x]) add(set, bx0 + x, by0 + y); return set; }
    // chessboard distance to the outside (two passes); the rim is distance <= t
    const BIG = 1 << 20, d = new Int32Array(w * h);
    const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h ? 0 : d[y * w + x]);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      if (!inS[y * w + x]) { d[y * w + x] = 0; continue; }
      d[y * w + x] = Math.min(BIG, at(x - 1, y) + 1, at(x, y - 1) + 1, at(x - 1, y - 1) + 1, at(x + 1, y - 1) + 1);
    }
    for (let y = h - 1; y >= 0; y--) for (let x = w - 1; x >= 0; x--) {
      if (!inS[y * w + x]) continue;
      d[y * w + x] = Math.min(d[y * w + x], at(x + 1, y) + 1, at(x, y + 1) + 1, at(x + 1, y + 1) + 1, at(x - 1, y + 1) + 1);
    }
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (inS[y * w + x] && d[y * w + x] <= t) add(set, bx0 + x, by0 + y);
    return set;
  }
  const rect = (x0, y0, x1, y1, t = 1, filled = false) => region(x0, y0, x1, y1, () => true, t, filled);
  function roundRect(x0, y0, x1, y1, r, t = 1, filled = false) {
    const ax = Math.min(x0, x1), ay = Math.min(y0, y1), bx = Math.max(x0, x1), by = Math.max(y0, y1);
    r = Math.max(0, Math.min(r, Math.floor((bx - ax + 1) / 2), Math.floor((by - ay + 1) / 2)));
    // corner circles centred r cells in from each corner (cell centres)
    const test = (x, y) => {
      const cx = Math.min(Math.max(x, ax + r), bx - r), cy = Math.min(Math.max(y, ay + r), by - r);
      return Math.hypot(x - cx, y - cy) <= r + 0.3;
    };
    if (filled || t <= 1 || bx - ax + 1 <= 2 * t || by - ay + 1 <= 2 * t) return region(x0, y0, x1, y1, test, t, filled);
    // a thick rim: the box less the same box inset t, its corners rounded r - t, so the rim keeps
    // its thickness round the corners
    const outer = region(x0, y0, x1, y1, test, 1, true);
    const inner = roundRect(ax + t, ay + t, bx - t, by - t, Math.max(0, r - t), 1, true);
    for (const i of inner) outer.delete(i);
    return outer;
  }
  function ellipse(x0, y0, x1, y1, t = 1, filled = false) {
    const ax = Math.min(x0, x1), ay = Math.min(y0, y1), bx = Math.max(x0, x1), by = Math.max(y0, y1);
    const cx = (ax + bx) / 2, cy = (ay + by) / 2, rx = (bx - ax + 1) / 2, ry = (by - ay + 1) / 2;
    const test = (x, y) => ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1;
    if (filled || t <= 1 || rx <= t || ry <= t) return region(x0, y0, x1, y1, test, t, filled);
    // a thick rim: the ellipse less one t smaller all round (even thickness, unlike square steps)
    const outer = region(x0, y0, x1, y1, test, 1, true);
    const ix = rx - t, iy = ry - t;
    for (const i of [...outer]) {
      const x = i % N, y = (i - x) / N;
      if (((x - cx) / ix) ** 2 + ((y - cy) / iy) ** 2 <= 1) outer.delete(i);
    }
    return outer;
  }
  // a polygon through points (closed): filled, or its rim t tiles deep
  function polygon(points, t = 1, filled = false) {
    if (points.length < 3) return path(points, t);
    const xs = points.map((p) => p[0]), ys = points.map((p) => p[1]);
    const edges = path(points, 1, true);
    const test = (x, y) => {
      if (edges.has(idx(x, y))) return true;
      let c = false;
      for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
        const [xi, yi] = points[i], [xj, yj] = points[j];
        if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
      }
      return c;
    };
    return region(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), test, t, filled);
  }
  // a quadratic curve from a to b, bent toward c
  function curve(a, b, c, t = 1) {
    const len = Math.hypot(c[0] - a[0], c[1] - a[1]) + Math.hypot(b[0] - c[0], b[1] - c[1]);
    const n = Math.max(2, Math.ceil(len));
    const pts = [];
    for (let k = 0; k <= n; k++) {
      const u = k / n, v = 1 - u;
      const p = [Math.round(v * v * a[0] + 2 * u * v * c[0] + u * u * b[0]), Math.round(v * v * a[1] + 2 * u * v * c[1] + u * u * b[1])];
      if (!pts.length || pts[pts.length - 1][0] !== p[0] || pts[pts.length - 1][1] !== p[1]) pts.push(p);
    }
    return path(pts, t);
  }
  // random cells within radius r of (x, y)
  function spray(x, y, r, count, rand = Math.random) {
    const set = new Set();
    for (let k = 0; k < count; k++) {
      const a = rand() * Math.PI * 2, d = Math.sqrt(rand()) * r;
      add(set, Math.round(x + Math.cos(a) * d), Math.round(y + Math.sin(a) * d));
    }
    return set;
  }
  // the cells joined edge to edge to (x, y) that match it: the same tile, or (walls) any plain wall;
  // null when it runs past limit cells (filling open space around everything, say)
  function flood(grid, x, y, limit = 300000, kind = "same") {
    if (!inside(x, y)) return new Set();
    const t0 = grid[idx(x, y)];
    const wall = (t) => t >= 1 && t <= 161;
    const match = kind === "walls" && wall(t0) ? wall : (t) => t === t0;
    const set = new Set([idx(x, y)]), q = [idx(x, y)];
    while (q.length) {
      const i = q.pop(), cx = i % N, cy = (i - cx) / N;
      for (const [nx, ny] of [[cx + 1, cy], [cx - 1, cy], [cx, cy + 1], [cx, cy - 1]]) {
        if (!inside(nx, ny)) continue;
        const j = idx(nx, ny);
        if (set.has(j) || !match(grid[j])) continue;
        set.add(j); q.push(j);
        if (set.size > limit) return null;
      }
    }
    return set;
  }
  // mirror copies about the middle of the map: "h" left-right, "v" top-bottom, "hv" all four
  function mirror(set, mode) {
    if (!mode || mode === "none") return set;
    const out = new Set(set);
    for (const i of set) {
      const x = i % N, y = (i - x) / N, mx = N - 1 - x, my = N - 1 - y;
      if (mode === "h" || mode === "hv") out.add(idx(mx, y));
      if (mode === "v" || mode === "hv") out.add(idx(x, my));
      if (mode === "hv") out.add(idx(mx, my));
    }
    return out;
  }
  const mirrorPoint = (x, y, mode) => {
    const out = [[x, y]];
    if (mode === "h" || mode === "hv") out.push([N - 1 - x, y]);
    if (mode === "v" || mode === "hv") out.push([x, N - 1 - y]);
    if (mode === "hv") out.push([N - 1 - x, N - 1 - y]);
    return out;
  };

  // ---- text: a blocky 5x7 font, each pixel `size` tiles square -------------------------------------
  const FONT = {
    A: ["01110", "10001", "10001", "11111", "10001", "10001", "10001"], B: ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    C: ["01111", "10000", "10000", "10000", "10000", "10000", "01111"], D: ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    E: ["11111", "10000", "10000", "11110", "10000", "10000", "11111"], F: ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    G: ["01111", "10000", "10000", "10111", "10001", "10001", "01111"], H: ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    I: ["11111", "00100", "00100", "00100", "00100", "00100", "11111"], J: ["00111", "00001", "00001", "00001", "10001", "10001", "01110"],
    K: ["10001", "10010", "10100", "11000", "10100", "10010", "10001"], L: ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    M: ["10001", "11011", "10101", "10101", "10001", "10001", "10001"], N: ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    O: ["01110", "10001", "10001", "10001", "10001", "10001", "01110"], P: ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    Q: ["01110", "10001", "10001", "10001", "10101", "10010", "01101"], R: ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    S: ["01111", "10000", "10000", "01110", "00001", "00001", "11110"], T: ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    U: ["10001", "10001", "10001", "10001", "10001", "10001", "01110"], V: ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    W: ["10001", "10001", "10001", "10101", "10101", "10101", "01010"], X: ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    Y: ["10001", "10001", "01010", "00100", "00100", "00100", "00100"], Z: ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
    0: ["01110", "10001", "10011", "10101", "11001", "10001", "01110"], 1: ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    2: ["01110", "10001", "00001", "00010", "00100", "01000", "11111"], 3: ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
    4: ["00010", "00110", "01010", "10010", "11111", "00010", "00010"], 5: ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
    6: ["00110", "01000", "10000", "11110", "10001", "10001", "01110"], 7: ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    8: ["01110", "10001", "10001", "01110", "10001", "10001", "01110"], 9: ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    ".": ["00000", "00000", "00000", "00000", "00000", "01100", "01100"], ",": ["00000", "00000", "00000", "00000", "01100", "00100", "01000"],
    "!": ["00100", "00100", "00100", "00100", "00100", "00000", "00100"], "?": ["01110", "10001", "00001", "00010", "00100", "00000", "00100"],
    "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"], "+": ["00000", "00100", "00100", "11111", "00100", "00100", "00000"],
    ":": ["00000", "01100", "01100", "00000", "01100", "01100", "00000"], "'": ["00100", "00100", "01000", "00000", "00000", "00000", "00000"],
    "/": ["00001", "00010", "00010", "00100", "01000", "01000", "10000"], "=": ["00000", "00000", "11111", "00000", "11111", "00000", "00000"],
    "(": ["00010", "00100", "01000", "01000", "01000", "00100", "00010"], ")": ["01000", "00100", "00010", "00010", "00010", "00100", "01000"],
    "#": ["01010", "01010", "11111", "01010", "11111", "01010", "01010"], "_": ["00000", "00000", "00000", "00000", "00000", "00000", "11111"],
    "<": ["00010", "00100", "01000", "10000", "01000", "00100", "00010"], ">": ["01000", "00100", "00010", "00001", "00010", "00100", "01000"],
    " ": ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
  };
  // text at (x, y) (top-left); lines split on \n; returns { cells, w, h } in tiles
  function text(str, x, y, size = 1) {
    const set = new Set();
    let w = 0;
    const lines = String(str).toUpperCase().split("\n");
    lines.forEach((ln, row) => {
      let cx = 0;
      for (const ch of ln) {
        const g = FONT[ch] || FONT["?"];
        g.forEach((bits, gy) => [...bits].forEach((b, gx) => {
          if (b !== "1") return;
          for (let dy = 0; dy < size; dy++) for (let dx = 0; dx < size; dx++) add(set, x + (cx + gx) * size + dx, y + (row * 9 + gy) * size + dy);
        }));
        cx += 6;
      }
      w = Math.max(w, (cx - 1) * size);
    });
    return { cells: set, w: Math.max(0, w), h: (lines.length * 9 - 2) * size };
  }

  // ---- clips: copied tiles, flipped and turned -----------------------------------------------------
  // a clip is { w, h, tiles: Uint8Array(w*h), mask: Uint8Array(w*h) } (mask: part of the selection)
  function copy(grid, cells) {
    let x0 = N, y0 = N, x1 = -1, y1 = -1;
    for (const i of cells) { const x = i % N, y = (i - x) / N; x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
    if (x1 < 0) return null;
    const w = x1 - x0 + 1, h = y1 - y0 + 1, tiles = new Uint8Array(w * h), mask = new Uint8Array(w * h);
    for (const i of cells) { const x = i % N, y = (i - x) / N, k = (y - y0) * w + x - x0; tiles[k] = grid[i]; mask[k] = 1; }
    return { w, h, tiles, mask, x: x0, y: y0 };
  }
  function transform(clip, f) {
    const { w, h } = clip, turn = f === "rot";
    const W = turn ? h : w, H = turn ? w : h;
    const tiles = new Uint8Array(W * H), mask = new Uint8Array(W * H);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      const [X, Y] = f === "h" ? [w - 1 - x, y] : f === "v" ? [x, h - 1 - y] : [h - 1 - y, x]; // rot: 90 degrees clockwise
      tiles[Y * W + X] = clip.tiles[y * w + x]; mask[Y * W + X] = clip.mask[y * w + x];
    }
    return Object.assign({}, clip, { w: W, h: H, tiles, mask });
  }
  // the cells a clip covers at (x, y), with the tile for each: Map(index -> tile)
  function stamp(clip, x, y) {
    const out = new Map();
    for (let cy = 0; cy < clip.h; cy++) for (let cx = 0; cx < clip.w; cx++) {
      const k = cy * clip.w + cx;
      if (clip.mask[k] && inside(x + cx, y + cy)) out.set(idx(x + cx, y + cy), clip.tiles[k]);
    }
    return out;
  }

  /**
   * Put tiles on the grid: `put` is Map(index -> tile) (0 erases). With a tile layout, the plain
   * walls put down take its pieces by their neighbours, and the walls around them re-join, each in
   * its own layout. Returns the changes [index, old, new] (one undo step), applied to grid.
   */
  function apply(grid, put, layout = null, retile = put.keys()) {
    const old = new Map();
    for (const [i, t] of put) if (grid[i] !== t) { old.set(i, grid[i]); grid[i] = t; }
    if (layout && root.Autotile) {
      for (const [i, from] of root.Autotile.tidyCells(grid, layout, [...retile])) if (!old.has(i)) old.set(i, from);
    }
    const changes = [];
    for (const [i, from] of old) if (grid[i] !== from) changes.push([i, from, grid[i]]);
    return changes;
  }
  const fill = (cells, t) => { const m = new Map(); for (const i of cells) m.set(i, t); return m; };

  const Shapes = { linePts, path, brush, line, rect, roundRect, ellipse, polygon, curve, spray, flood, mirror, mirrorPoint, text, copy, transform, stamp, apply, fill };

  // ---- the tools in the editor ------------------------------------------------------------------------
  const TOOLS = [
    ["pencil", "Pencil", "b", "Draw walls freehand; size is the wall thickness"],
    ["line", "Line", "l", "Drag a straight wall; Shift keeps it level, upright or at 45°"],
    ["rect", "Rectangle", "r", "Drag a box; Shift makes it square"],
    ["round", "Rounded", "u", "Drag a box with rounded corners"],
    ["ellipse", "Ellipse", "o", "Drag an ellipse; Shift makes a circle"],
    ["polygon", "Polygon", "p", "Click the corners; double-click or Enter to finish, Esc to cancel"],
    ["curve", "Curve", "c", "Drag from end to end, then move to bend it and click"],
    ["spray", "Spray", "y", "Scatter tiles around the pointer"],
    ["fill", "Fill", "g", "Fill the area you click with the current tile"],
    ["replace", "Replace", "x", "Click a tile: every tile like it (in the selection, or the map) becomes the current tile"],
    ["pick", "Pick", "i", "Click a tile to draw with it (or Alt-click with any tool)"],
    ["text", "Text", "a", "Click where the text goes"],
    ["erase", "Eraser", "e", "Rub out; size is the wall thickness"],
    ["select", "Select", "s", "Drag a box to select; drag inside it to move it"],
    ["lasso", "Lasso", "f", "Draw round what to select"],
    ["wand", "Wand", "w", "Click a wall to select it and the walls joined to it (or an open area)"],
  ];
  const SHAPES = new Set(["line", "rect", "round", "ellipse", "polygon", "curve"]);

  function mount(ed) {
    const $ = (id) => document.getElementById(id);
    const opt = {
      get thick() { return Math.max(1, Math.min(16, Number($("dtThick").value) || 1)); },
      get filled() { return $("dtFilled").checked; },
      get radius() { return Number($("dtRadius").value) || 0; },
      get mirror() { return $("dtMirror").value; },
      get auto() { return $("dtAuto").checked; },
      get text() { return $("dtText").value; },
      get size() { return Math.max(1, Number($("dtSize").value) || 1); },
    };
    let drag = null;   // { tool, x0, y0, x, y, pts }
    let preview = null; // Set of cells to show
    let poly = null;   // polygon corners while clicking them
    let bend = null;   // curve: { a, b } waiting for the bend point
    let live = null;   // pencil/eraser/spray: Map(index -> tile) and changes so far
    let sel = null;    // Set of selected cells
    let floating = null; // { clip, x, y } tiles lifted off the map (moving or pasting)
    let clipboard = null;
    let hover = [0, 0];
    let lastClick = null; // polygon: a second click on the same cell finishes it

    // the tile the tools draw with: the layout's pieces when auto-tiling a plain wall
    const wallTile = () => ed.cur();
    const autoLayout = (t) => (opt.auto && t >= 1 && t <= 161 ? ed.layout() : null);
    const thick = () => (ed.cur() > 161 ? 1 : opt.thick); // special tiles go one at a time
    const constrain = (x0, y0, x, y, e, diag) => {
      if (!e || !e.shiftKey) return [x, y];
      const dx = x - x0, dy = y - y0;
      if (!diag) { const m = Math.max(Math.abs(dx), Math.abs(dy)); return [x0 + Math.sign(dx || 1) * m, y0 + Math.sign(dy || 1) * m]; }
      const a = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), len = Math.max(Math.abs(dx), Math.abs(dy));
      return [x0 + Math.round(Math.cos(a)) * len, y0 + Math.round(Math.sin(a)) * len];
    };
    function shapeCells(tool, x0, y0, x, y, e) {
      const t = thick();
      switch (tool) {
        case "line": { const [a, b] = constrain(x0, y0, x, y, e, true); return line(x0, y0, a, b, t); }
        case "rect": { const [a, b] = constrain(x0, y0, x, y, e); return rect(x0, y0, a, b, t, opt.filled); }
        case "round": { const [a, b] = constrain(x0, y0, x, y, e); return roundRect(x0, y0, a, b, opt.radius, t, opt.filled); }
        case "ellipse": { const [a, b] = constrain(x0, y0, x, y, e); return ellipse(x0, y0, a, b, t, opt.filled); }
      }
      return new Set();
    }
    // draw cells with the current tile (or erase), mirrored, auto-tiled; one undo step
    function commitCells(cells, erase = false, mirrored = true) {
      const t = erase ? 0 : wallTile();
      if (mirrored) cells = mirror(cells, opt.mirror);
      const changes = apply(ed.grid, fill(cells, t), erase ? (opt.auto ? ed.layout() : null) : autoLayout(t));
      ed.commit(changes);
      return changes.length;
    }

    // live strokes (pencil, eraser, spray): tiles go down as you draw, tidied when you let go
    function liveAdd(cells, erase) {
      const t = erase ? 0 : wallTile();
      cells = mirror(cells, opt.mirror);
      for (const i of cells) {
        if (ed.grid[i] === t) continue;
        if (!live.old.has(i)) live.old.set(i, ed.grid[i]);
        ed.grid[i] = t;
      }
    }
    function liveEnd() {
      const l = live; live = null;
      if (!l) return;
      const layout = l.erase ? (opt.auto ? ed.layout() : null) : autoLayout(wallTile());
      if (layout) for (const [i, from] of Autotile.tidyCells(ed.grid, layout, [...l.old.keys()])) if (!l.old.has(i)) l.old.set(i, from);
      const changes = [];
      for (const [i, from] of l.old) if (ed.grid[i] !== from) changes.push([i, from, ed.grid[i]]);
      ed.commit(changes);
    }

    // ---- selection ----
    // lift the selection off the map (not yet an undo step): { clip, changes }
    function lift() {
      const clip = copy(ed.grid, sel);
      return { clip, changes: apply(ed.grid, fill(sel, 0), opt.auto ? ed.layout() : null) };
    }
    // put a clip down (not yet an undo step): its walls re-join, each in the layout its pieces come
    // from (so a flipped or turned corner points the right way again), and so do the walls around it
    function place(fl) {
      const put = stamp(fl.clip, fl.x, fl.y);
      const changes = apply(ed.grid, put, null);
      if (opt.auto) {
        const byLayout = new Map();
        for (const i of put.keys()) { const l = Autotile.owner(ed.grid[i]); if (l) { if (!byLayout.has(l)) byLayout.set(l, []); byLayout.get(l).push(i); } }
        for (const [l, cells] of byLayout) changes.push(...Autotile.tidyCells(ed.grid, l, cells));
      }
      sel = new Set(put.keys());
      return changes;
    }
    function selAct(what) {
      if (what === "paste") {
        if (!clipboard) { ed.say("Nothing copied yet.", false); return; }
        floating = { clip: clipboard, x: hover[0], y: hover[1], undo: [] };
        ed.say("Move to where it goes and click. H/V flip, ] turns, Esc cancels.", true);
        ed.setTool("select");
        ed.draw(); return;
      }
      if (floating && (what === "h" || what === "v" || what === "rot")) { floating.clip = transform(floating.clip, what); ed.draw(); return; }
      if (what === "none") { sel = null; ed.draw(); return; }
      if (!sel || !sel.size) { ed.say("Select something first (Select, Lasso or Wand).", false); return; }
      if (what === "copy" || what === "cut") {
        clipboard = copy(ed.grid, sel);
        if (what === "cut") { ed.commit(lift().changes); sel = null; }
        ed.say(`${what === "cut" ? "Cut" : "Copied"} ${clipboard.mask.reduce((a, b) => a + b, 0).toLocaleString()} tiles. Paste with Cmd/Ctrl-V.`, true);
      } else if (what === "delete") {
        commitCells(sel, true, false); sel = null;
      } else if (what === "fillsel") {
        commitCells(sel, false, false);
      } else if (what === "tidysel") {
        const changes = Autotile.tidyCells(ed.grid, ed.layout(), [...sel], false);
        ed.commit(changes);
        ed.say(changes.length ? `Tidied ${changes.length.toLocaleString()} wall tiles.` : "Those walls were already tidy.", true);
      } else if (what === "h" || what === "v" || what === "rot") {
        const { clip, changes } = lift();
        const turned = transform(clip, what);
        // turn about the selection's middle
        const x = clip.x + Math.floor((clip.w - turned.w) / 2), y = clip.y + Math.floor((clip.h - turned.h) / 2);
        ed.commit(mergeChanges(changes, place({ clip: turned, x, y })));
      }
      ed.draw();
    }
    function mergeChanges(a = [], b = []) {
      const m = new Map();
      for (const [i, from, to] of a) m.set(i, [i, from, to]);
      for (const [i, from, to] of b) { if (m.has(i)) m.get(i)[2] = to; else m.set(i, [i, from, to]); }
      return [...m.values()].filter((c) => c[1] !== c[2]);
    }

    // ---- pointer ----
    function down(x, y, e) {
      hover = [x, y];
      if (e.altKey || ed.tool() === "pick") { if (inside(x, y) && ed.grid[idx(x, y)]) ed.pick(ed.grid[idx(x, y)]); return; }
      const tool = ed.tool();
      if (floating) { const fl = floating; floating = null; ed.commit(mergeChanges(fl.undo, place(fl))); ed.draw(); return; }
      if (tool === "pencil" || tool === "erase") {
        live = { old: new Map(), erase: tool === "erase" };
        liveAdd(brush([[x, y]], thick()), live.erase);
        drag = { tool, x0: x, y0: y, x, y };
      } else if (tool === "spray") {
        live = { old: new Map(), erase: false };
        liveAdd(spray(x, y, Math.max(2, opt.thick * 2), Math.max(2, opt.thick)), false);
        drag = { tool, x0: x, y0: y, x, y };
      } else if (SHAPES.has(tool) && tool !== "polygon" && tool !== "curve") {
        drag = { tool, x0: x, y0: y, x, y };
        preview = shapeCells(tool, x, y, x, y, e);
      } else if (tool === "curve") {
        if (bend) { commitCells(curve(bend.a, bend.b, [x, y], thick())); bend = null; preview = null; }
        else { drag = { tool, x0: x, y0: y, x, y }; preview = line(x, y, x, y, thick()); }
      } else if (tool === "polygon") {
        const now = Date.now(), again = lastClick && now - lastClick.t < 400 && lastClick.x === x && lastClick.y === y;
        lastClick = { t: now, x, y };
        if (!poly) poly = [[x, y]];
        else if (again) { finishPolygon(); return; }
        else poly.push([x, y]);
        preview = polygon(poly.concat([[x, y]]), thick(), opt.filled);
      } else if (tool === "fill") {
        // open space: the area round the click; a wall: the whole wall joined to it (recoloured)
        if (!inside(x, y) || ed.grid[idx(x, y)] === wallTile()) return;
        const cells = flood(ed.grid, x, y, 300000, "walls");
        if (!cells) { ed.say("That area runs out across the map: draw a wall round it first.", false); return; }
        commitCells(cells);
      } else if (tool === "replace") {
        if (!inside(x, y)) return;
        const from = ed.grid[idx(x, y)], to = wallTile();
        if (from === to) return;
        const cells = new Set();
        if (sel && sel.size) { for (const i of sel) if (ed.grid[i] === from) cells.add(i); }
        else for (let i = 0; i < N * N; i++) if (ed.grid[i] === from) cells.add(i);
        const changes = apply(ed.grid, fill(cells, to), null);
        ed.commit(changes);
        ed.say(`Replaced ${changes.length.toLocaleString()} tiles.`, true);
      } else if (tool === "text") {
        if (!opt.text.trim()) { ed.say("Type the text under Tool options first.", false); $("dtText").focus(); return; }
        const n = commitCells(text(opt.text, x, y, opt.size).cells);
        ed.say(n ? "Text placed. Undo takes it back." : "No change.", true);
      } else if (tool === "select") {
        if (sel && sel.has(idx(x, y))) {
          // move: lift the selection and carry it (one undo step when it's put down)
          const { clip, changes } = lift();
          floating = { clip, x: clip.x, y: clip.y, dx: x - clip.x, dy: y - clip.y, moving: true, undo: changes };
          drag = { tool: "move", x0: x, y0: y, x, y };
        } else { sel = null; drag = { tool, x0: x, y0: y, x, y }; preview = rect(x, y, x, y, 1, true); }
      } else if (tool === "lasso") {
        sel = null; drag = { tool, x0: x, y0: y, x, y, pts: [[x, y]] };
      } else if (tool === "wand") {
        const cells = flood(ed.grid, x, y, 300000, "walls");
        if (!cells) { ed.say("That open area runs across the whole map.", false); return; }
        sel = e.shiftKey && sel ? new Set([...sel, ...cells]) : cells;
        ed.say(`Selected ${sel.size.toLocaleString()} tiles.`, true);
      }
      ed.draw();
    }
    function move(x, y, e) {
      hover = [x, y];
      if (floating && !drag) { floating.x = x; floating.y = y; ed.draw(); return true; }
      if (bend) { preview = curve(bend.a, bend.b, [x, y], thick()); ed.draw(); return true; }
      if (poly) { preview = polygon(poly.concat([[x, y]]), thick(), opt.filled); ed.draw(); return true; }
      if (!drag) return ed.tool() === "text" ? (preview = text(opt.text || "", x, y, opt.size).cells, ed.draw(), true) : false;
      const px = drag.x, py = drag.y;
      drag.x = x; drag.y = y;
      const tool = drag.tool;
      if (tool === "pencil" || tool === "erase") liveAdd(path([[px, py], [x, y]], thick()), tool === "erase");
      else if (tool === "spray") liveAdd(spray(x, y, Math.max(2, opt.thick * 2), Math.max(2, opt.thick)), false);
      else if (tool === "curve") preview = line(drag.x0, drag.y0, x, y, thick());
      else if (SHAPES.has(tool)) preview = shapeCells(tool, drag.x0, drag.y0, x, y, e);
      else if (tool === "select") preview = rect(drag.x0, drag.y0, x, y, 1, true);
      else if (tool === "lasso") { drag.pts.push([x, y]); preview = path(drag.pts, 1); }
      else if (tool === "move") { floating.x = x - floating.dx; floating.y = y - floating.dy; }
      ed.draw();
      return true;
    }
    function up(x, y, e) {
      const d = drag; drag = null;
      if (!d) return;
      if (d.tool === "pencil" || d.tool === "erase" || d.tool === "spray") liveEnd();
      else if (d.tool === "curve") {
        if (d.x0 === x && d.y0 === y) preview = null;
        else { bend = { a: [d.x0, d.y0], b: [x, y] }; ed.say("Now move to bend the curve, and click.", true); }
      } else if (SHAPES.has(d.tool)) { commitCells(shapeCells(d.tool, d.x0, d.y0, x, y, e)); preview = null; }
      else if (d.tool === "select") { sel = rect(d.x0, d.y0, x, y, 1, true); preview = null; ed.say(`Selected ${sel.size.toLocaleString()} tiles: Cmd/Ctrl-C copy, X cut, Delete, H/V flip, ] turn.`, true); }
      else if (d.tool === "lasso") { sel = d.pts.length > 2 ? polygon(d.pts, 1, true) : null; preview = null; }
      else if (d.tool === "move" && floating) {
        const fl = floating; floating = null;
        ed.commit(mergeChanges(fl.undo, place(fl)));
      }
      ed.draw();
    }
    function finishPolygon() {
      if (poly && poly.length >= 2) commitCells(polygon(poly, thick(), opt.filled));
      poly = null; preview = null; ed.draw();
    }
    function cancel() {
      const had = !!(poly || bend || floating || preview || sel);
      if (floating && floating.moving) { // put it back where it was
        const fl = floating; floating = null;
        ed.commit(mergeChanges(fl.undo, place({ clip: fl.clip, x: fl.clip.x, y: fl.clip.y })));
      }
      poly = bend = floating = preview = null; sel = null;
      ed.draw();
      return had;
    }

    // keys: true when handled
    function key(e) {
      const k = e.key, mod = e.metaKey || e.ctrlKey;
      if (k === "Escape") return cancel();
      if (k === "Enter" && poly) { finishPolygon(); return true; }
      if (mod && k.toLowerCase() === "c") { selAct("copy"); return true; }
      if (mod && k.toLowerCase() === "x") { selAct("cut"); return true; }
      if (mod && k.toLowerCase() === "v") { selAct("paste"); return true; }
      if (mod && k.toLowerCase() === "a") { sel = new Set(); for (let i = 0; i < N * N; i++) if (ed.grid[i]) sel.add(i); ed.draw(); return true; }
      if (mod) return false;
      if ((k === "Delete" || k === "Backspace") && sel) { selAct("delete"); return true; }
      if ((sel || floating) && (k === "h" || k === "v")) { selAct(k); return true; }
      if ((sel || floating) && k === "]") { selAct("rot"); return true; }
      if (k === "-" || k === "_") { $("dtThick").value = Math.max(1, opt.thick - 1); $("dtThick").dispatchEvent(new Event("input")); return true; }
      if (k === "=" || k === "+") { $("dtThick").value = Math.min(16, opt.thick + 1); $("dtThick").dispatchEvent(new Event("input")); return true; }
      const t = TOOLS.find((x) => x[2] === k.toLowerCase());
      if (t) { ed.setTool(t[0]); return true; }
      return false;
    }

    // overlay: preview cells, the selection, the floating clip, the brush outline
    function overlay(ctx, s, ox, oy, view) {
      const x0 = Math.floor(-ox / s), y0 = Math.floor(-oy / s), x1 = Math.ceil((view.width - ox) / s), y1 = Math.ceil((view.height - oy) / s);
      const cellsIn = (set, style) => {
        if (!set) return;
        ctx.fillStyle = style;
        for (const i of set) {
          const x = i % N, y = (i - x) / N;
          if (x < x0 || y < y0 || x > x1 || y > y1) continue;
          ctx.fillRect(ox + x * s, oy + y * s, s, s);
        }
      };
      if (preview) cellsIn(mirror(preview, opt.mirror), "rgba(255,138,31,0.55)");
      if (sel) {
        cellsIn(sel, "rgba(34,211,238,0.18)");
        // marching edge: cells of the selection with a side outside it
        ctx.strokeStyle = "#22d3ee"; ctx.lineWidth = Math.max(1, Math.min(2, s / 8));
        ctx.beginPath();
        for (const i of sel) {
          const x = i % N, y = (i - x) / N;
          if (x < x0 || y < y0 || x > x1 || y > y1) continue;
          const X = ox + x * s, Y = oy + y * s;
          if (!sel.has(i - N)) { ctx.moveTo(X, Y); ctx.lineTo(X + s, Y); }
          if (!sel.has(i + N)) { ctx.moveTo(X, Y + s); ctx.lineTo(X + s, Y + s); }
          if (x === 0 || !sel.has(i - 1)) { ctx.moveTo(X, Y); ctx.lineTo(X, Y + s); }
          if (x === N - 1 || !sel.has(i + 1)) { ctx.moveTo(X + s, Y); ctx.lineTo(X + s, Y + s); }
        }
        ctx.stroke();
      }
      if (floating) {
        ctx.globalAlpha = 0.8;
        for (const [i, t] of stamp(floating.clip, floating.x, floating.y)) {
          const x = i % N, y = (i - x) / N;
          if (x < x0 || y < y0 || x > x1 || y > y1) continue;
          if (t) ed.drawTile(ctx, t, ox + x * s, oy + y * s, s);
          else { ctx.fillStyle = "rgba(4,7,14,0.8)"; ctx.fillRect(ox + x * s, oy + y * s, s, s); }
        }
        ctx.globalAlpha = 1;
        ctx.strokeStyle = "#ff8a1f"; ctx.lineWidth = 2;
        ctx.strokeRect(ox + floating.x * s, oy + floating.y * s, floating.clip.w * s, floating.clip.h * s);
      }
      // brush outline for pencil / eraser
      const tool = ed.tool();
      if ((tool === "pencil" || tool === "erase") && !drag) {
        const t = thick(), o = Math.floor((t - 1) / 2);
        ctx.strokeStyle = tool === "erase" ? "#f87171" : "#ff8a1f"; ctx.lineWidth = 1;
        for (const [hx, hy] of mirrorPoint(hover[0], hover[1], opt.mirror))
          ctx.strokeRect(ox + (hx - o) * s + 0.5, oy + (hy - o) * s + 0.5, t * s - 1, t * s - 1);
      }
      // mirror axes
      if (opt.mirror !== "none") {
        ctx.strokeStyle = "rgba(167,139,250,0.6)"; ctx.setLineDash([6, 6]); ctx.lineWidth = 1; ctx.beginPath();
        if (opt.mirror !== "v") { ctx.moveTo(ox + N / 2 * s, oy); ctx.lineTo(ox + N / 2 * s, oy + N * s); }
        if (opt.mirror !== "h") { ctx.moveTo(ox, oy + N / 2 * s); ctx.lineTo(ox + N * s, oy + N / 2 * s); }
        ctx.stroke(); ctx.setLineDash([]);
      }
    }

    // ---- the toolbar and options ----
    const bar = $("drawtools");
    for (const [id, label, k, tip] of TOOLS) {
      const b = document.createElement("button");
      b.type = "button"; b.className = "ghost small"; b.dataset.tool = id; b.setAttribute("aria-pressed", "false");
      b.title = tip;
      b.append(label + " ", Object.assign(document.createElement("kbd"), { textContent: k.toUpperCase() }));
      b.addEventListener("click", () => ed.setTool(id));
      bar.append(b);
    }
    $("dtThick").addEventListener("input", () => { $("dtThickN").textContent = String(opt.thick); ed.draw(); });
    $("dtThickN").textContent = String(opt.thick);
    for (const [id, what] of [["dtCopy", "copy"], ["dtCut", "cut"], ["dtPaste", "paste"], ["dtDel", "delete"], ["dtFlipH", "h"], ["dtFlipV", "v"], ["dtRot", "rot"], ["dtFillSel", "fillsel"], ["dtTidySel", "tidysel"], ["dtNone", "none"]])
      $(id).addEventListener("click", () => selAct(what));
    $("dtMirror").addEventListener("change", () => ed.draw());

    return {
      handles: (t) => TOOLS.some((x) => x[0] === t),
      toolOn(t) {
        // switching tools drops a half-made polygon or curve
        if (t !== "polygon") poly = null;
        if (t !== "curve") bend = null;
        preview = null;
        const hint = TOOLS.find((x) => x[0] === t);
        $("dtHint").textContent = hint ? hint[3] : "";
        ed.draw();
      },
      down, move, up, key, overlay, cancel,
      get selection() { return sel; },
    };
  }

  root.DrawTools = { Shapes, FONT, TOOLS, mount };
})();
