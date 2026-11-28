// Wall auto-tiling for the map editor: once it is decided which cells are wall, each wall cell gets
// the tile for its role, from which neighbours are wall too:
//  - lines (one tile thick): lone, ends, straight bars, corners, tees and crosses, so a line holds,
//    turns properly and ends with a cap;
//  - shapes (two or more tiles thick): outer corners, edges, inner corners and fill, so a block's
//    outline is continuous.
// A tile layout maps role -> tile (or a list of variants). Layouts are data and swappable:
// Autotile.register(layout) adds one, and the editor lists them. A layout may leave roles out:
// each role falls back to a plainer one (inner corner -> fill -> cross, edge -> tee, block corner ->
// line corner, anything -> fallback tile).
// Works in the browser (window.Autotile) and in Node (for tests).
"use strict";

(() => {
  const N = 1024;
  const root = typeof window !== "undefined" ? window : globalThis;

  // neighbour bits: the four sides, then the four diagonals
  const B = { n: 1, e: 2, s: 4, w: 8, ne: 16, se: 32, sw: 64, nw: 128 };
  const SIDES = ["n", "e", "s", "w"];

  // the sides in a mask, as a canonical id ("nes", "sw", ...): always in n, e, s, w order
  const sidesOf = (m) => SIDES.filter((d) => m & B[d]).join("");

  // Every role, with a short description (shown in the editor and in the preview tileset).
  // Line roles are named by the sides they connect to: "end-n" connects north only (the bottom cap
  // of a vertical bar), "corner-es" connects east and south (the top-left corner, like ┌).
  // Shape roles are named by where the solid block is: "block-es" is a block's top-left outer
  // corner (solid to the east and south); "edge-n" is a block's top edge (open to the north);
  // "inner-ne" is fill with only the north-east diagonal open (an inside corner).
  const ROLES = {
    "lone": "a single wall tile on its own",
    "end-n": "end of a vertical bar (bar goes up)", "end-s": "end of a vertical bar (bar goes down)",
    "end-e": "end of a horizontal bar (bar goes right)", "end-w": "end of a horizontal bar (bar goes left)",
    "bar-v": "middle of a vertical bar", "bar-h": "middle of a horizontal bar",
    "corner-ne": "line turn └", "corner-es": "line turn ┌", "corner-sw": "line turn ┐", "corner-nw": "line turn ┘",
    "tee-nes": "junction ├", "tee-esw": "junction ┬", "tee-nsw": "junction ┤", "tee-new": "junction ┴",
    "cross": "junction ┼",
    "block-es": "block corner, top-left", "block-sw": "block corner, top-right",
    "block-nw": "block corner, bottom-right", "block-ne": "block corner, bottom-left",
    "edge-n": "block edge, top", "edge-s": "block edge, bottom", "edge-w": "block edge, left", "edge-e": "block edge, right",
    "calm-n": "block edge, top, solid wall behind it", "calm-s": "block edge, bottom, solid wall behind it",
    "calm-w": "block edge, left, solid wall behind it", "calm-e": "block edge, right, solid wall behind it",
    "fill": "inside of a block",
    "inner-ne": "inside corner (open to the top-right)", "inner-se": "inside corner (open to the bottom-right)",
    "inner-sw": "inside corner (open to the bottom-left)", "inner-nw": "inside corner (open to the top-left)",
    "calmin-ne": "inside corner, solid wall behind it (top-right open)", "calmin-se": "inside corner, solid wall behind it (bottom-right open)",
    "calmin-sw": "inside corner, solid wall behind it (bottom-left open)", "calmin-nw": "inside corner, solid wall behind it (top-left open)",
  };

  // the sides a role's tile connects to (open edges), for drawing previews and checking layouts
  function roleSides(role) {
    if (role === "lone") return "";
    if (role === "bar-v") return "ns";
    if (role === "bar-h") return "ew";
    if (role === "cross" || role === "fill" || role.startsWith("inner-") || role.startsWith("calmin-")) return "nesw";
    if (role.startsWith("edge-") || role.startsWith("calm-")) return "nesw".replace(role.slice(5), "");
    return role.split("-")[1];
  }

  /**
   * The role of a wall cell from its neighbour mask (B bits; diagonals count only where they
   * matter). Returns the most specific role; resolve() walks the fallbacks.
   */
  function roleOf(mask) {
    const m = mask & 15, sides = sidesOf(m);
    const has = (d) => (mask & B[d]) !== 0;
    switch (sides.length) {
      case 0: return "lone";
      case 1: return "end-" + sides;
      case 2:
        if (sides === "ns") return "bar-v";
        if (sides === "ew") return "bar-h";
        // a turn; if the diagonal between the two sides is wall too it's a block's outer corner
        { const diag = sides === "es" ? "se" : sides; // ne, se, sw, nw
          return (has(diag) ? "block-" : "corner-") + sides; }
      case 3: {
        // a tee, or a block's edge when both diagonals on the connected side are wall
        const open = SIDES.find((d) => !(m & B[d]));
        const diags = { n: ["se", "sw"], s: ["ne", "nw"], e: ["nw", "sw"], w: ["ne", "se"] }[open];
        return diags.every(has) ? "edge-" + open : "tee-" + sides;
      }
      default: {
        const missing = ["ne", "se", "sw", "nw"].filter((d) => !has(d));
        if (missing.length === 0) return "fill";
        if (missing.length === 1) return "inner-" + missing[0];
        return "cross";
      }
    }
  }

  function fallbacks(role) {
    const out = [role];
    if (role.startsWith("calmin-")) out.push("inner-" + role.slice(7), "fill", "cross");
    else if (role.startsWith("calm-")) out.push("edge-" + role.slice(5), "tee-" + "nesw".replace(role.slice(5), ""), "cross");
    else if (role.startsWith("inner-")) out.push("fill", "cross");
    else if (role === "fill") out.push("cross");
    else if (role.startsWith("edge-")) {
      const open = role.slice(5);
      out.push("tee-" + "nesw".replace(open, ""), "cross");
    } else if (role.startsWith("block-")) out.push("corner-" + role.slice(6));
    else if (role.startsWith("tee-")) out.push("cross");
    else if (role.startsWith("end-")) out.push(role[4] === "n" || role[4] === "s" ? "bar-v" : "bar-h", "lone");
    return out;
  }

  // a cheap, stable hash of a cell, to pick among a role's variants without visible repeats
  const cellHash = (x, y) => {
    let h = Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1);
    h = Math.imul(h ^ (h >>> 15), 0x85ebca6b);
    return (h ^ (h >>> 13)) >>> 0;
  };

  /** The tile for a role in a layout at (x, y), following fallbacks. */
  function resolve(layout, role, x = 0, y = 0) {
    for (const r of fallbacks(role)) {
      const t = layout.roles[r];
      if (t === undefined || t === null) continue;
      if (Array.isArray(t)) { if (t.length) return t[cellHash(x, y) % t.length]; continue; }
      return t;
    }
    return layout.fallback ?? 1;
  }

  // Plain wall tiles (what tidying retiles). Doors connect to walls but keep their own tile.
  const isPlainWall = (t) => t >= 1 && t <= 161;
  const connects = (t) => t >= 1 && t <= 169;

  /** The neighbour mask of (x, y) in a 1024x1024 tile grid. */
  function maskAt(grid, x, y, joins = connects) {
    let m = 0;
    const at = (dx, dy) => {
      const nx = x + dx, ny = y + dy;
      return nx >= 0 && ny >= 0 && nx < N && ny < N && joins(grid[ny * N + nx]);
    };
    if (at(0, -1)) m |= B.n; if (at(1, 0)) m |= B.e; if (at(0, 1)) m |= B.s; if (at(-1, 0)) m |= B.w;
    if (at(1, -1)) m |= B.ne; if (at(1, 1)) m |= B.se; if (at(-1, 1)) m |= B.sw; if (at(-1, -1)) m |= B.nw;
    return m;
  }

  // the cell one step inward from an edge (the side opposite its open side)
  const INWARD = { n: [0, 1], s: [0, -1], e: [-1, 0], w: [1, 0] };
  /**
   * The role of the wall at (x, y) in the grid: roleOf its neighbours, and an edge whose inner side
   * is solid wall (the cell behind it has wall all round) is a calm edge: no half motif cut off
   * against the plain interior.
   */
  function roleAt(grid, x, y) {
    const role = roleOf(maskAt(grid, x, y));
    if (role.startsWith("inner-")) {
      // an inside corner with solid wall behind it (the cell diagonally away from its open corner)
      const d = role.slice(6), dx = d.includes("e") ? -1 : 1, dy = d.includes("n") ? 1 : -1;
      return maskAt(grid, x + dx, y + dy) === 255 ? "calmin-" + d : role;
    }
    if (!role.startsWith("edge-")) return role;
    const side = role.slice(5), [dx, dy] = INWARD[side];
    return maskAt(grid, x + dx, y + dy) === 255 ? "calm-" + side : role;
  }

  /**
   * Retile the plain walls of grid (1024x1024) inside [x0, x1] x [y0, y1] (inclusive; default the
   * whole map) with layout. Neighbours outside the area still count. Returns the changes as
   * [index, old, new] (for undo), already applied to grid.
   */
  function tidy(grid, layout, area = null) {
    const [x0, y0, x1, y1] = area ? area.map((v, i) => Math.max(0, Math.min(N - 1, v))) : [0, 0, N - 1, N - 1];
    const changes = [];
    // decide every tile from the walls as they are, then write, so the order doesn't matter
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const i = y * N + x, t = grid[i];
      if (!isPlainWall(t)) continue;
      const next = resolve(layout, roleAt(grid, x, y), x, y);
      if (next !== t) changes.push([i, t, next]);
    }
    for (const [i, , t] of changes) grid[i] = t;
    return changes;
  }

  /**
   * Retile some cells (indices into the 1024x1024 grid) after drawing: `cells` get `layout`'s
   * pieces (plain walls only); with `ring`, the walls next to them are re-joined too, each in the
   * layout its tile already belongs to (walls in no known layout are left as they are: decor, text).
   * Returns the changes [index, old, new], applied.
   */
  function tidyCells(grid, layout, cells, ring = true) {
    const todo = new Map(); // index -> layout
    for (const i of cells) if (isPlainWall(grid[i])) todo.set(i, layout);
    if (ring) for (const i of cells) {
      const x = i % N, y = (i - x) / N;
      // two deep: a change can make a neighbour interior, which calms the edge beyond it
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        const nx = x + dx, ny = y + dy, j = ny * N + nx;
        if ((!dx && !dy) || nx < 0 || ny < 0 || nx >= N || ny >= N || todo.has(j)) continue;
        const own = owner(grid[j]);
        if (own) todo.set(j, own);
      }
    }
    const changes = [];
    for (const [i, l] of todo) {
      const x = i % N, y = (i - x) / N, t = grid[i];
      const next = resolve(l, roleAt(grid, x, y), x, y);
      if (next !== t) changes.push([i, t, next]);
    }
    for (const [i, , t] of changes) grid[i] = t;
    return changes;
  }

  // the registered layout a tile belongs to (the stand-in only when no real layout claims it)
  let owners = null;
  function owner(t) {
    if (!isPlainWall(t)) return null;
    if (!owners) {
      owners = new Map();
      const all = [...layouts.values()].sort((a, b) => (a === STAND_IN) - (b === STAND_IN));
      for (const l of all) for (const v of Object.values(l.roles).flat()) if (!owners.has(v)) owners.set(v, l);
    }
    return owners.get(t) || null;
  }

  /** Errors in a layout (unknown roles, tiles out of range), as strings; [] when it's usable. */
  function check(layout) {
    const errs = [];
    if (!layout || typeof layout !== "object" || !layout.roles) return ["a layout needs roles"];
    for (const [role, t] of Object.entries(layout.roles)) {
      if (!(role in ROLES)) errs.push(`unknown role ${role}`);
      for (const v of Array.isArray(t) ? t : [t]) if (!Number.isInteger(v) || v < 1 || v > 161) errs.push(`${role}: tile ${v} is not a wall tile (1-161)`);
    }
    return errs;
  }

  // ---- layouts -----------------------------------------------------------------------------------
  // STAND-IN until the standard tileset layout (from the tile-editor work) lands: every role on its
  // own tile, in rows of the 19-wide tileset, so the role preview tileset can show each one. Delete
  // it once a real layout is registered.
  const STAND_IN = {
    id: "standin",
    name: "Role preview (stand-in)",
    fallback: 1,
    roles: Object.fromEntries(Object.keys(ROLES).map((r, i) => [r, i + 1])),
  };

  /**
   * Layouts from a standard tile layout file (format "dare-tile-layout", docs/tile-layout.md): one
   * per pack, id "<pack id>". Roles are matched by their open edges, never by name (a layout may
   * rename them). A role the pack has no piece for drops open edges until one exists (most edges
   * kept first, a straight bar before a corner), else single, else fill: the spec's own rule.
   * Returns { layouts, errors }.
   */
  function fromSpec(spec) {
    const errors = [];
    if (!spec || spec.format !== "dare-tile-layout") return { layouts: [], errors: ['not a "dare-tile-layout" file'] };
    const norm = (o) => "NESW".split("").filter((e) => String(o || "").toUpperCase().includes(e)).join("");
    const nameByOpen = new Map();
    for (const [name, r] of Object.entries(spec.roles || {})) if (r && typeof r.open === "string") nameByOpen.set(norm(r.open), name);
    const out = [];
    for (const p of Array.isArray(spec.packs) ? spec.packs : []) {
      if (!p || !p.id || !p.tiles) continue;
      const ok = (t) => Number.isInteger(t) && t >= 1 && t <= 161;
      const tileFor = (o) => {
        const n = nameByOpen.get(o), t = n && p.tiles[n];
        if (Array.isArray(t)) return t.length && t.every(ok) ? t : undefined; // variants
        return ok(t) ? t : undefined;
      };
      const pick = (open) => {
        let t = tileFor(open);
        if (t) return t;
        const subsets = [];
        for (let m = (1 << open.length) - 2; m >= 0; m--) subsets.push(open.split("").filter((_, i) => m & (1 << i)).join(""));
        const straight = (o) => (o === "NS" || o === "EW" ? 0 : 1);
        subsets.sort((a, b) => b.length - a.length || straight(a) - straight(b));
        for (const o of subsets) if ((t = tileFor(o))) return t;
        return tileFor("NESW");
      };
      const roles = {};
      for (const role of Object.keys(ROLES)) {
        if (role.startsWith("calm-") || role.startsWith("calmin-")) continue; // only from the pack's own pieces (below)
        const t = pick(norm(roleSides(role)));
        if (t) roles[role] = t;
      }
      // a plain interior piece (optional): where every neighbour is wall, so thick walls read as
      // one solid wall rather than a grid of the pack's decorated fill pieces
      if (ok(p.interior) || (Array.isArray(p.interior) && p.interior.length && p.interior.every(ok))) {
        // inside corners keep the pack's decorated fill: they sit among the edge pieces
        for (const d of ["ne", "se", "sw", "nw"]) { roles["inner-" + d] = roles.fill; roles["calmin-" + d] = p.interior; }
        roles.fill = p.interior;
      }
      // calm edges (optional): keyed by open edges like the roles; an edge open to one side only
      if (p.calm && typeof p.calm === "object") for (const [o, t] of Object.entries(p.calm)) {
        const open = norm(o);
        if (open.length !== 3 || !(ok(t) || (Array.isArray(t) && t.length && t.every(ok)))) continue;
        roles["calm-" + "nesw".split("").find((d) => !open.includes(d.toUpperCase()))] = t;
      }
      const layout = { id: p.id, name: `${spec.name ? spec.name.replace(/\s*\(.*\)$/, "") + ": " : ""}${p.name || p.id}`, roles, fallback: [roles.lone, roles.fill, 1].find(Number.isInteger), spec: true };
      const errs = check(layout);
      if (errs.length || !Object.keys(roles).length) { errors.push(`pack ${p.id}: ${errs.join("; ") || "no usable tiles"}`); continue; }
      out.push(layout);
    }
    if (!out.length && !errors.length) errors.push("no packs");
    return { layouts: out, errors };
  }

  const layouts = new Map();
  function register(layout) {
    const errs = check(layout);
    if (errs.length) throw new Error(`tile layout ${layout && layout.id}: ${errs.join("; ")}`);
    layouts.set(layout.id, layout);
    owners = null;
    return layout;
  }
  register(STAND_IN);

  /**
   * A tileset canvas (304x160, the 19x10 grid of 16px tiles) that draws each of a layout's tiles as
   * its role: lines along the open edges, block parts as filled panels. Lets you see the auto-tiling
   * without real art. Browser only.
   */
  function previewTileset(layout) {
    const c = document.createElement("canvas");
    c.width = 304; c.height = 160;
    const ctx = c.getContext("2d");
    const drawn = new Set();
    for (const role of Object.keys(ROLES)) {
      const t = layout.roles[role];
      for (const v of Array.isArray(t) ? t : t ? [t] : []) {
        if (drawn.has(v)) continue;
        drawn.add(v);
        const i = v - 1, x = (i % 19) * 16, y = Math.floor(i / 19) * 16;
        drawRole(ctx, role, x, y);
      }
    }
    return c;
  }
  function drawRole(ctx, role, x, y) {
    const shape = role === "fill" || /^(block|edge|calm|calmin|inner)-/.test(role);
    if (shape) {
      // a panel: solid where the block is, a bright rim on the open sides
      ctx.fillStyle = "#1f4d63"; ctx.fillRect(x, y, 16, 16);
      ctx.fillStyle = "#67e8f9";
      const open = "nesw".split("").filter((d) => !roleSides(role).includes(d));
      for (const d of open) {
        if (d === "n") ctx.fillRect(x, y, 16, 2);
        if (d === "s") ctx.fillRect(x, y + 14, 16, 2);
        if (d === "w") ctx.fillRect(x, y, 2, 16);
        if (d === "e") ctx.fillRect(x + 14, y, 2, 16);
      }
      if (role.startsWith("inner-")) {
        const d = role.slice(6);
        ctx.fillRect(x + (d.includes("e") ? 13 : 0), y + (d.includes("s") ? 13 : 0), 3, 3);
      }
      return;
    }
    // a line: a bar from the centre to each connected side, with a round cap on ends
    ctx.fillStyle = "#0b2230"; ctx.fillRect(x, y, 16, 16);
    ctx.fillStyle = "#22d3ee";
    const sides = roleSides(role);
    ctx.fillRect(x + 5, y + 5, 6, 6);
    if (sides.includes("n")) ctx.fillRect(x + 5, y, 6, 6);
    if (sides.includes("s")) ctx.fillRect(x + 5, y + 10, 6, 6);
    if (sides.includes("w")) ctx.fillRect(x, y + 5, 6, 6);
    if (sides.includes("e")) ctx.fillRect(x + 10, y + 5, 6, 6);
    if (sides.length <= 1) { ctx.fillStyle = "#facc15"; ctx.fillRect(x + 6, y + 6, 4, 4); }
  }

  root.Autotile = {
    B, ROLES, STAND_IN, layouts, register, check, fromSpec, roleOf, roleSides, fallbacks, resolve,
    maskAt, roleAt, tidy, tidyCells, owner, isPlainWall, previewTileset,
  };
})();
