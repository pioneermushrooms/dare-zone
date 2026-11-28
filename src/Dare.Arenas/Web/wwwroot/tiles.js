// Tilesets and the tile layout, with no DOM: shared by the tile editor (/arena/tiles) and node tests.
//   Bmp        read any 8/24/32-bit tileset BMP into indexed pixels; write an 8-bit BMP back.
//   LvlFile    split a .lvl into tileset BMP, eLVL block and tile list; put a new tileset in.
//   TileLayout parse and check a tile layout (docs/tile-layout.md), pick a piece by open edges.
//   TileOps    copy, flip and rotate a 16x16 tile; read a tile's edges to suggest its role.
"use strict";

(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else Object.assign(root, api);
})(typeof self !== "undefined" ? self : this, () => {
  const TS = 16, COLS = 19, ROWS = 10, SHEET_W = COLS * TS, SHEET_H = ROWS * TS;
  const u16 = (b, p) => b[p] | (b[p + 1] << 8);
  const u32 = (b, p) => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) >>> 0;
  const i32 = (b, p) => u32(b, p) | 0;
  const put16 = (b, p, v) => { b[p] = v & 255; b[p + 1] = (v >>> 8) & 255; };
  const put32 = (b, p, v) => { put16(b, p, v & 0xffff); put16(b, p + 2, (v >>> 16) & 0xffff); };

  // ---- BMP ----------------------------------------------------------------------------------------
  // An image is { width, height, palette: Uint8Array(768) rgb, pixels: Uint8Array(w*h) top-down }.
  // Index 0 is what the game shows as empty (see-through) space; tilesets keep it black.
  function readBmp(bytes) {
    const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    if (b.length < 54 || b[0] !== 0x42 || b[1] !== 0x4d) throw new Error("not a BMP file");
    const off = u32(b, 10), hdr = u32(b, 14), w = i32(b, 18), hRaw = i32(b, 22), h = Math.abs(hRaw);
    const bpp = u16(b, 28), comp = u32(b, 30);
    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new Error("bad BMP size");
    const row = (y) => (hRaw > 0 ? h - 1 - y : y); // file row -> image row
    if (bpp <= 8) {
      const ncol = Math.min(256, u32(b, 46) || (1 << bpp));
      const palette = new Uint8Array(768);
      for (let i = 0; i < ncol; i++) {
        const p = 14 + hdr + i * 4;
        if (p + 2 >= b.length) break;
        palette[i * 3] = b[p + 2]; palette[i * 3 + 1] = b[p + 1]; palette[i * 3 + 2] = b[p];
      }
      const pixels = new Uint8Array(w * h);
      if (bpp === 8 && comp === 1) {
        let p = off, x = 0, y = 0;
        const set = (v) => { if (x < w && y < h) pixels[row(y) * w + x] = v; x++; };
        while (p < b.length - 1) {
          const a = b[p++], c = b[p++];
          if (a > 0) { for (let i = 0; i < a; i++) set(c); }
          else if (c === 0) { x = 0; y++; }
          else if (c === 1) break;
          else if (c === 2) { x += b[p++]; y += b[p++]; }
          else { for (let i = 0; i < c; i++) set(b[p++]); if (c & 1) p++; }
        }
      } else if (comp === 0 && (bpp === 8 || bpp === 4 || bpp === 1)) {
        const stride = ((w * bpp + 31) >>> 5) << 2;
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
          const bit = x * bpp, byte = b[off + y * stride + (bit >>> 3)];
          pixels[row(y) * w + x] = bpp === 8 ? byte : (byte >>> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1);
        }
      } else throw new Error(`unsupported BMP (${bpp}-bit, compression ${comp})`);
      return { width: w, height: h, palette, pixels };
    }
    if ((bpp === 24 || bpp === 32) && (comp === 0 || comp === 3)) {
      const step = bpp / 8, stride = (w * step + 3) & ~3, rgba = new Uint8Array(w * h * 4);
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
        const s = off + y * stride + x * step, d = (row(y) * w + x) * 4;
        rgba[d] = b[s + 2]; rgba[d + 1] = b[s + 1]; rgba[d + 2] = b[s]; rgba[d + 3] = 255;
      }
      return quantize(rgba, w, h);
    }
    throw new Error(`unsupported BMP (${bpp}-bit, compression ${comp})`);
  }

  // Uncompressed 8-bit BMP, bottom-up, 256-colour palette: what Continuum reads as a tileset.
  function writeBmp(img) {
    const { width: w, height: h, palette, pixels } = img;
    const stride = (w + 3) & ~3, off = 14 + 40 + 1024, size = off + stride * h;
    const b = new Uint8Array(size);
    b[0] = 0x42; b[1] = 0x4d; put32(b, 2, size); put32(b, 10, off);
    put32(b, 14, 40); put32(b, 18, w); put32(b, 22, h); put16(b, 26, 1); put16(b, 28, 8);
    put32(b, 34, stride * h); put32(b, 38, 2835); put32(b, 42, 2835); put32(b, 46, 256); put32(b, 50, 0);
    for (let i = 0; i < 256; i++) {
      b[54 + i * 4] = palette[i * 3 + 2]; b[55 + i * 4] = palette[i * 3 + 1]; b[56 + i * 4] = palette[i * 3];
    }
    for (let y = 0; y < h; y++) b.set(pixels.subarray(y * w, y * w + w), off + (h - 1 - y) * stride);
    return b;
  }

  // RGBA (from a PNG or a 24-bit BMP) to 256 colours. Exact when there are at most 255 colours;
  // otherwise median cut. Transparent and black pixels become index 0 (empty).
  function quantize(rgba, w, h) {
    const n = w * h, pixels = new Uint8Array(n), palette = new Uint8Array(768);
    const key = (i) => (rgba[i * 4 + 3] < 128 ? 0 : ((rgba[i * 4] << 16) | (rgba[i * 4 + 1] << 8) | rgba[i * 4 + 2]));
    const counts = new Map();
    for (let i = 0; i < n; i++) { const k = key(i); if (k) counts.set(k, (counts.get(k) || 0) + 1); }
    let colours = [...counts.keys()];
    if (colours.length > 255) colours = medianCut([...counts.entries()], 255);
    colours.forEach((c, i) => { palette[(i + 1) * 3] = c >> 16; palette[(i + 1) * 3 + 1] = (c >> 8) & 255; palette[(i + 1) * 3 + 2] = c & 255; });
    const exact = new Map(colours.map((c, i) => [c, i + 1])), cache = new Map();
    for (let i = 0; i < n; i++) {
      const k = key(i);
      if (!k) continue;
      let v = exact.get(k);
      if (v === undefined) { v = cache.get(k); if (v === undefined) { v = nearest(palette, k >> 16, (k >> 8) & 255, k & 255, 1); cache.set(k, v); } }
      pixels[i] = v;
    }
    return { width: w, height: h, palette, pixels };
  }
  function medianCut(entries, max) {
    let boxes = [entries];
    while (boxes.length < max) {
      let bi = -1, best = 0, ch = 0;
      boxes.forEach((box, i) => {
        if (box.length < 2) return;
        for (let c = 0; c < 3; c++) {
          let lo = 255, hi = 0;
          for (const [k] of box) { const v = (k >> (16 - 8 * c)) & 255; if (v < lo) lo = v; if (v > hi) hi = v; }
          if (hi - lo > best) { best = hi - lo; bi = i; ch = c; }
        }
      });
      if (bi < 0) break;
      const box = boxes[bi].sort((a, b) => ((a[0] >> (16 - 8 * ch)) & 255) - ((b[0] >> (16 - 8 * ch)) & 255));
      const mid = box.length >> 1;
      boxes.splice(bi, 1, box.slice(0, mid), box.slice(mid));
    }
    return boxes.map((box) => {
      let r = 0, g = 0, bl = 0, t = 0;
      for (const [k, c] of box) { r += (k >> 16) * c; g += ((k >> 8) & 255) * c; bl += (k & 255) * c; t += c; }
      return (Math.round(r / t) << 16) | (Math.round(g / t) << 8) | Math.round(bl / t) || 0x010101;
    });
  }
  function nearest(palette, r, g, b, from = 0) {
    let bi = from, bd = Infinity;
    for (let i = from; i < 256; i++) {
      const dr = palette[i * 3] - r, dg = palette[i * 3 + 1] - g, db = palette[i * 3 + 2] - b, d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
      if (d < bd) { bd = d; bi = i; }
    }
    return bi;
  }
  // A tileset is exactly 304x160; a bigger image is cropped, a smaller one padded with empty space.
  function fitSheet(img) {
    if (img.width === SHEET_W && img.height === SHEET_H) return img;
    const pixels = new Uint8Array(SHEET_W * SHEET_H);
    for (let y = 0; y < Math.min(SHEET_H, img.height); y++)
      for (let x = 0; x < Math.min(SHEET_W, img.width); x++) pixels[y * SHEET_W + x] = img.pixels[y * img.width + x];
    return { width: SHEET_W, height: SHEET_H, palette: img.palette, pixels };
  }
  // The stock grey-ish palette for a new, empty tileset: black, then a 6x7x6 colour cube, then greys.
  function defaultPalette() {
    const p = new Uint8Array(768); let i = 1;
    for (let r = 0; r < 6; r++) for (let g = 0; g < 7; g++) for (let b = 0; b < 6; b++, i++) {
      p[i * 3] = r * 51; p[i * 3 + 1] = Math.round(g * 42.5); p[i * 3 + 2] = b * 51;
    }
    for (let k = 1; i < 256; i++, k++) p[i * 3] = p[i * 3 + 1] = p[i * 3 + 2] = k * 64; // 253-255: greys
    return p;
  }

  // ---- .lvl ---------------------------------------------------------------------------------------
  // A .lvl is [tileset BMP [eLVL block]] then one u32 per tile. In a BMP-prefixed .lvl the BMP's
  // bfSize covers the eLVL block too, and its reserved field (offset 6) says where eLVL starts.
  function splitLvl(bytes) {
    const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    if (b.length >= 54 && b[0] === 0x42 && b[1] === 0x4d) {
      const end = Math.min(u32(b, 2), b.length), at = u32(b, 6);
      const elvl = at && at + 4 <= end && String.fromCharCode(b[at], b[at + 1], b[at + 2], b[at + 3]) === "elvl" ? b.slice(at, end) : null;
      return { bmp: b.slice(0, elvl ? at : end), elvl, tiles: b.slice(end) };
    }
    if (b.length >= 8 && String.fromCharCode(b[0], b[1], b[2], b[3]) === "elvl") {
      const end = Math.min(u32(b, 4), b.length);
      return { bmp: null, elvl: b.slice(0, end), tiles: b.slice(end) };
    }
    return { bmp: null, elvl: null, tiles: b.slice() };
  }
  // A .lvl with this tileset (BMP bytes from writeBmp), keeping eLVL data and tiles from the old one.
  function joinLvl(bmp, elvl, tiles) {
    if (!bmp) {
      const out = new Uint8Array((elvl ? elvl.length : 0) + tiles.length);
      if (elvl) out.set(elvl, 0);
      out.set(tiles, elvl ? elvl.length : 0);
      return out;
    }
    const base = bmp.length, at = elvl ? (base + 3) & ~3 : 0, head = elvl ? at + elvl.length : base;
    const out = new Uint8Array(head + tiles.length);
    out.set(bmp, 0);
    if (elvl) { out.set(elvl, at); put32(out, 6, at); } else put32(out, 6, 0);
    put32(out, 2, head);
    out.set(tiles, head);
    return out;
  }
  function tilesetOfLvl(bytes) { const s = splitLvl(bytes); return s.bmp ? fitSheet(readBmp(s.bmp)) : null; }

  // ---- tile layout ---------------------------------------------------------------------------------
  const EDGES = "NESW";
  const CALM_KEYS = ["ESW", "NES", "NSW", "NEW"]; // the four edge (T) pieces' open edges
  const INNER_KEYS = ["NE", "SE", "SW", "NW"]; // inside corners, by their open diagonal
  // "en", "Ne", "NE" -> "NE"; anything that isn't one of N/S plus one of E/W -> ""
  const normDiag = (k) => { const u = String(k).toUpperCase(); if (!/^[NESW]{2}$/.test(u)) return ""; const ns = u.match(/[NS]/g) || [], ew = u.match(/[EW]/g) || []; return ns.length === 1 && ew.length === 1 ? ns[0] + ew[0] : ""; };
  const normOpen = (s) => EDGES.split("").filter((e) => String(s || "").toUpperCase().includes(e)).join("");
  // Parse and check a layout (object or JSON text). Returns { layout, errors }; layout is null when
  // it can't be used. Warnings that don't stop use are in layout.warnings.
  function parseLayout(input) {
    const errors = [], warnings = [];
    let j = input;
    if (typeof input === "string") { try { j = JSON.parse(input); } catch (e) { return { layout: null, errors: ["not JSON: " + e.message] }; } }
    if (!j || typeof j !== "object") return { layout: null, errors: ["not a layout object"] };
    if (j.format !== "dare-tile-layout") errors.push('format must be "dare-tile-layout"');
    if (j.version !== 1) errors.push("version must be 1");
    const reserved = j.reserved && Number.isInteger(j.reserved.from) ? [j.reserved.from, j.reserved.to ?? 190] : [162, 190];
    const maxTile = COLS * ROWS;
    const roles = new Map(), byOpen = new Map();
    for (const [name, r] of Object.entries(j.roles || {})) {
      if (!r || typeof r.open !== "string" || /[^NESW]/i.test(r.open)) { errors.push(`role ${name}: open must be letters from NESW`); continue; }
      const open = normOpen(r.open);
      if (byOpen.has(open)) { errors.push(`roles ${byOpen.get(open)} and ${name} have the same open edges "${open}"`); continue; }
      roles.set(name, { name, open, label: r.label || name });
      byOpen.set(open, name);
    }
    if (!roles.size) errors.push("no roles");
    const packs = [], owner = new Map();
    for (const [i, p] of (Array.isArray(j.packs) ? j.packs : []).entries()) {
      const id = p && typeof p.id === "string" && p.id ? p.id : `pack${i + 1}`;
      if (packs.some((q) => q.id === id)) { errors.push(`two packs named ${id}`); continue; }
      const tiles = new Map();
      for (const [role, t] of Object.entries((p && p.tiles) || {})) {
        if (!roles.has(role)) { errors.push(`pack ${id}: unknown role ${role}`); continue; }
        if (!Number.isInteger(t) || t < 1 || t > maxTile) { errors.push(`pack ${id}: ${role} tile ${t} is not 1-${maxTile}`); continue; }
        if (t >= reserved[0] && t <= reserved[1]) { errors.push(`pack ${id}: ${role} uses reserved tile ${t}`); continue; }
        if (owner.has(t)) { errors.push(`tile ${t} is in ${owner.get(t)} and ${id}/${role}`); continue; }
        owner.set(t, `${id}/${role}`);
        tiles.set(role, t);
      }
      // optional plain piece for cells with wall on all eight sides (a thick wall's inside), instead of the decorated fill
      let interior = null;
      if (p && p.interior !== undefined && p.interior !== null) {
        const t = p.interior;
        if (!Number.isInteger(t) || t < 1 || t > maxTile) errors.push(`pack ${id}: interior tile ${t} is not 1-${maxTile}`);
        else if (t >= reserved[0] && t <= reserved[1]) errors.push(`pack ${id}: interior uses reserved tile ${t}`);
        else if (owner.has(t)) errors.push(`tile ${t} is in ${owner.get(t)} and ${id}/interior`);
        else { owner.set(t, `${id}/interior`); interior = t; }
      }
      // optional calm edge pieces, by the edge piece's open edges: used where the cell on the edge's
      // inner side is interior, so a thick wall's edge doesn't carry decoration meant for a thin one
      const calm = new Map();
      if (p && p.calm !== undefined && p.calm !== null) {
        if (typeof p.calm !== "object" || Array.isArray(p.calm)) errors.push(`pack ${id}: calm must be an object of open edges -> tile`);
        else for (const [k, t] of Object.entries(p.calm)) {
          const open = /^[NESW]+$/i.test(k) ? normOpen(k) : "";
          if (!CALM_KEYS.includes(open)) { errors.push(`pack ${id}: calm key "${k}" is not one of ${CALM_KEYS.join(", ")}`); continue; }
          if (calm.has(open)) { errors.push(`pack ${id}: calm "${open}" given twice`); continue; }
          if (!Number.isInteger(t) || t < 1 || t > maxTile) { errors.push(`pack ${id}: calm ${open} tile ${t} is not 1-${maxTile}`); continue; }
          if (t >= reserved[0] && t <= reserved[1]) { errors.push(`pack ${id}: calm ${open} uses reserved tile ${t}`); continue; }
          if (owner.has(t)) { errors.push(`tile ${t} is in ${owner.get(t)} and ${id}/calm-${open}`); continue; }
          owner.set(t, `${id}/calm-${open}`);
          calm.set(open, t);
        }
      }
      // optional inside-corner pieces, by the open diagonal: where two walls meet at an inside corner
      const inner = new Map();
      if (p && p.inner !== undefined && p.inner !== null) {
        if (typeof p.inner !== "object" || Array.isArray(p.inner)) errors.push(`pack ${id}: inner must be an object of open diagonal -> tile`);
        else for (const [k, t] of Object.entries(p.inner)) {
          const d = normDiag(k);
          if (!INNER_KEYS.includes(d)) { errors.push(`pack ${id}: inner key "${k}" is not one of ${INNER_KEYS.join(", ")}`); continue; }
          if (inner.has(d)) { errors.push(`pack ${id}: inner "${d}" given twice`); continue; }
          if (!Number.isInteger(t) || t < 1 || t > maxTile) { errors.push(`pack ${id}: inner ${d} tile ${t} is not 1-${maxTile}`); continue; }
          if (t >= reserved[0] && t <= reserved[1]) { errors.push(`pack ${id}: inner ${d} uses reserved tile ${t}`); continue; }
          if (owner.has(t)) { errors.push(`tile ${t} is in ${owner.get(t)} and ${id}/inner-${d}`); continue; }
          owner.set(t, `${id}/inner-${d}`);
          inner.set(d, t);
        }
      }
      const missing = [...roles.keys()].filter((r) => !tiles.has(r));
      if (missing.length && tiles.size) warnings.push(`pack ${id} has no ${missing.join(", ")}`);
      packs.push({ id, name: (p && p.name) || id, origin: p && Array.isArray(p.origin) ? p.origin.slice(0, 2) : null, tiles, interior, calm, inner });
    }
    let template = null;
    if (j.packTemplate && j.packTemplate.cells) {
      template = { size: j.packTemplate.size || [4, 4], cells: new Map() };
      for (const [role, c] of Object.entries(j.packTemplate.cells)) {
        if (!roles.has(role)) warnings.push(`packTemplate: unknown role ${role}`);
        else if (Array.isArray(c) && c.length === 2) template.cells.set(role, [c[0] | 0, c[1] | 0]);
      }
    }
    if (errors.length) return { layout: null, errors };
    const layout = {
      name: j.name || "Untitled layout", about: j.about || "", roles, byOpen, packs, template,
      other: Array.isArray(j.other) ? j.other : [], reserved, warnings,
    };
    return { layout, errors };
  }
  // Back to the file format (for saving a role map).
  function layoutToJson(layout) {
    const o = {
      format: "dare-tile-layout", version: 1, name: layout.name,
      ...(layout.about ? { about: layout.about } : {}),
      tileset: { columns: COLS, rows: ROWS, tileSize: TS },
      roles: Object.fromEntries([...layout.roles.values()].map((r) => [r.name, { open: r.open, label: r.label }])),
    };
    if (layout.template) o.packTemplate = { size: layout.template.size, cells: Object.fromEntries(layout.template.cells) };
    o.packs = layout.packs.map((p) => ({ id: p.id, name: p.name, ...(p.origin ? { origin: p.origin } : {}), tiles: Object.fromEntries(p.tiles), ...(p.interior ? { interior: p.interior } : {}), ...(p.calm && p.calm.size ? { calm: Object.fromEntries(p.calm) } : {}), ...(p.inner && p.inner.size ? { inner: Object.fromEntries(p.inner) } : {}) }));
    if (layout.other.length) o.other = layout.other;
    o.reserved = { from: layout.reserved[0], to: layout.reserved[1], label: "Doors, flag, safe zone, goal, fly-over and fly-under" };
    return JSON.stringify(o, null, 1) + "\n";
  }
  // The open edges of a wall cell from which of its neighbours are wall too.
  const openOf = (n, e, s, w) => (n ? "N" : "") + (e ? "E" : "") + (s ? "S" : "") + (w ? "W" : "");
  // The tile to draw for a cell with these open edges in this pack. If the pack lacks that piece,
  // drop open edges (fewest first; among two-edge pieces a straight bar before a corner) until one
  // exists, else single, else fill.
  function pick(layout, packId, open) {
    const pack = layout.packs.find((p) => p.id === packId) || layout.packs[0];
    if (!pack) return 0;
    const want = normOpen(open), tileFor = (o) => { const r = layout.byOpen.get(o); return r ? pack.tiles.get(r) : undefined; };
    let t = tileFor(want);
    if (t) return t;
    const subsets = [];
    for (let m = (1 << want.length) - 2; m >= 0; m--) subsets.push(want.split("").filter((_, i) => m & (1 << i)).join(""));
    const straight = (o) => (o === "NS" || o === "EW" ? 0 : 1); // a junction keeps its through line first
    subsets.sort((a, b) => b.length - a.length || straight(a) - straight(b));
    for (const s of subsets) if ((t = tileFor(s))) return t;
    return tileFor("NESW") || pack.tiles.values().next().value || 0;
  }
  // { pack, role } for a tile number, or null.
  function roleOfTile(layout, tile) {
    for (const p of layout.packs) for (const [role, t] of p.tiles) if (t === tile) return { pack: p, role: layout.roles.get(role) };
    return null;
  }
  // A pack laid out by the template at (col, row), for the tile editor's "new pack here".
  function packAt(layout, id, name, col, row) {
    const tiles = new Map();
    if (layout.template) for (const [role, [dx, dy]] of layout.template.cells) {
      const c = col + dx, r = row + dy;
      if (c < COLS && r < ROWS) tiles.set(role, r * COLS + c + 1);
    }
    return { id, name, origin: [col, row], tiles, interior: null, calm: new Map(), inner: new Map() };
  }

  // ---- tile ops --------------------------------------------------------------------------------------
  const slotXY = (tile) => [((tile - 1) % COLS) * TS, Math.floor((tile - 1) / COLS) * TS];
  function getTile(img, tile) {
    const [x0, y0] = slotXY(tile), out = new Uint8Array(TS * TS);
    for (let y = 0; y < TS; y++) out.set(img.pixels.subarray((y0 + y) * img.width + x0, (y0 + y) * img.width + x0 + TS), y * TS);
    return out;
  }
  function setTile(img, tile, px) {
    const [x0, y0] = slotXY(tile);
    for (let y = 0; y < TS; y++) img.pixels.set(px.subarray(y * TS, y * TS + TS), (y0 + y) * img.width + x0);
  }
  const mapTile = (px, f) => { const o = new Uint8Array(TS * TS); for (let y = 0; y < TS; y++) for (let x = 0; x < TS; x++) { const [sx, sy] = f(x, y); o[y * TS + x] = px[sy * TS + sx]; } return o; };
  const flipH = (px) => mapTile(px, (x, y) => [TS - 1 - x, y]);
  const flipV = (px) => mapTile(px, (x, y) => [x, TS - 1 - y]);
  const rotateCW = (px) => mapTile(px, (x, y) => [y, TS - 1 - x]);
  // What flipping or rotating a tile does to its open edges.
  const EDGE_MAP = { flipH: { N: "N", E: "W", S: "S", W: "E" }, flipV: { N: "S", E: "E", S: "N", W: "W" }, rotateCW: { N: "E", E: "S", S: "W", W: "N" } };
  const turnOpen = (open, op) => normOpen(open.split("").map((e) => EDGE_MAP[op][e]).join(""));

  // Suggest each tile's open edges by reading its pixels. A closed edge is an outline: bands that run
  // along the edge, so its outer lines are nearly one colour from end to end. Across an open edge the
  // pack's texture (rivets, stripes, grooves) carries on into the neighbour, so those lines vary.
  // Measured on tactical2's six packs: 376 of 384 edges and 89 of 96 tiles right. Returns Map(tile -> {open, sure})
  // for non-empty tiles up to maxTile; sure is 0..1, low when an edge was a close call.
  function suggestOpen(img, maxTile = 161) {
    const out = new Map(), THRESHOLD = 20;
    const lum = (i) => 0.3 * img.palette[i * 3] + 0.59 * img.palette[i * 3 + 1] + 0.11 * img.palette[i * 3 + 2];
    for (let t = 1; t <= maxTile; t++) {
      const px = getTile(img, t);
      if (px.every((v) => v === 0)) continue;
      const L = (x, y) => lum(px[y * TS + x]);
      let open = "", sure = 1;
      for (const e of EDGES) {
        let spread = 0;
        for (let k = 0; k < 3; k++) { // the outer three lines, corners left out (they belong to two edges)
          const a = [];
          for (let i = 2; i < TS - 2; i++) a.push(e === "N" ? L(i, k) : e === "S" ? L(i, TS - 1 - k) : e === "W" ? L(k, i) : L(TS - 1 - k, i));
          const m = a.reduce((s, v) => s + v, 0) / a.length;
          spread += Math.sqrt(a.reduce((s, v) => s + (v - m) * (v - m), 0) / a.length) / 3;
        }
        if (spread > THRESHOLD) open += e;
        sure = Math.min(sure, Math.abs(spread - THRESHOLD) / THRESHOLD);
      }
      out.set(t, { open, sure: Math.min(1, sure) });
    }
    return out;
  }

  const TileSheet = { TS, COLS, ROWS, SHEET_W, SHEET_H };
  return {
    TileSheet,
    Bmp: { read: readBmp, write: writeBmp, quantize, nearest, fitSheet, defaultPalette },
    LvlFile: { split: splitLvl, join: joinLvl, tileset: tilesetOfLvl },
    TileLayout: { EDGES, CALM_KEYS, INNER_KEYS, normOpen, parse: parseLayout, toJson: layoutToJson, openOf, pick, roleOfTile, packAt },
    TileOps: { slotXY, getTile, setTile, flipH, flipV, rotateCW, turnOpen, suggestOpen },
  };
});
