// eLVL regions in a map's prefix (the part of a .lvl before its tiles), with no DOM: shared by the map
// editor's Game labels panel (labels-layer.js), the Game mode tab and node tests.
//   Elvl.regions(prefix)            -> the REGN regions: name, tiles (Set of y*1024+x), base/noAntiwarp/
//                                      noWeapons/noFlagDrops, meta (our rDGM JSON) and the raw chunk.
//   Elvl.withRegions(prefix, regs)  -> a new prefix with those regions; every other chunk is kept.
//   Elvl.encodeTiles / decodeTiles  -> the rTIL run-length format, as SubspaceServer reads it
//                                      (MapRegion.ReadRunLengthEncodedTileData).
// Format: "elvl", u32 total size (header included), u32 0, then chunks: 4-char type, u32 size, data,
// zero padding to a multiple of 4. Spec: docs/superpowers/specs/2026-11-28-game-mode-phase3-labeller-design.md.
"use strict";

(function (root, factory) {
  const lvl = typeof module === "object" && module.exports ? require("./tiles.js").LvlFile : root.LvlFile;
  const api = factory(lvl);
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.Elvl = api;
})(typeof self !== "undefined" ? self : this, (LvlFile) => {
  const N = 1024;
  const u32 = (b, p) => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) >>> 0;
  const put32 = (b, p, v) => { b[p] = v & 255; b[p + 1] = (v >>> 8) & 255; b[p + 2] = (v >>> 16) & 255; b[p + 3] = (v >>> 24) & 255; };
  const typeAt = (b, p) => String.fromCharCode(b[p], b[p + 1], b[p + 2], b[p + 3]);
  const ascii = (s) => Uint8Array.from(String(s), (c) => c.charCodeAt(0) & 0x7f);
  const utf8 = (s) => new TextEncoder().encode(s);
  const fromUtf8 = (b) => new TextDecoder().decode(b);

  function cat(parts) {
    const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
    let at = 0;
    for (const p of parts) { out.set(p, at); at += p.length; }
    return out;
  }

  /** One chunk: type, size, data, zero padding to a multiple of 4. */
  function chunk(type, data) {
    const pad = (4 - (data.length & 3)) & 3, out = new Uint8Array(8 + data.length + pad);
    out.set(ascii(type), 0); put32(out, 4, data.length); out.set(data, 8);
    return out;
  }

  /** The chunks in b[from, to): [{ type, data, raw }] (raw = header + data + padding). */
  function chunks(b, from, to) {
    const list = [];
    let p = from;
    while (p + 8 <= to) {
      const size = u32(b, p + 4), end = p + 8 + size;
      if (end > to) break;
      const next = Math.min(to, end + ((4 - (size & 3)) & 3));
      list.push({ type: typeAt(b, p), data: b.subarray(p + 8, end), raw: b.slice(p, next) });
      p = next;
    }
    return list;
  }

  // ---- rTIL ---------------------------------------------------------------------------------------
  function emit(out, op, n) {
    while (n > 0) {
      const k = Math.min(n, 1024);
      if (k <= 32) out.push((op << 6) | (k - 1));
      else out.push((op << 6) | 0x20 | (((k - 1) >> 8) & 3), (k - 1) & 255);
      n -= k;
    }
  }

  /** Encodes a tile set. "Repeat last row" is used only right after an identical non-empty row. */
  function encodeTiles(set) {
    const rows = new Array(N);
    for (const v of set) {
      const x = v % N, y = Math.floor(v / N);
      if (x < 0 || x >= N || y < 0 || y >= N) continue;
      (rows[y] ||= []).push(x);
    }
    const runsOf = (xs) => {
      if (!xs) return null;
      xs.sort((a, b) => a - b);
      const runs = [];
      for (const x of xs) {
        const last = runs[runs.length - 1];
        if (last && x === last[0] + last[1]) last[1]++;
        else if (!last || x >= last[0] + last[1]) runs.push([x, 1]);
      }
      return runs;
    };
    const out = [];
    let prevKey = null; // the previous row's runs, when that row (directly above) had tiles
    let y = 0;
    while (y < N) {
      const runs = runsOf(rows[y]);
      if (!runs) {
        let n = 1;
        while (y + n < N && !rows[y + n]) n++;
        emit(out, 2, n);
        y += n; prevKey = null;
        continue;
      }
      const key = JSON.stringify(runs);
      if (key === prevKey) {
        let n = 1;
        while (y + n < N && rows[y + n] && JSON.stringify(runsOf(rows[y + n])) === key) n++;
        emit(out, 3, n);
        y += n;
        continue;
      }
      let x = 0;
      for (const [x0, len] of runs) {
        if (x0 > x) emit(out, 0, x0 - x);
        emit(out, 1, len);
        x = x0 + len;
      }
      if (x < N) emit(out, 0, N - x);
      prevKey = key; y++;
    }
    return Uint8Array.from(out);
  }

  /** Decodes rTIL data exactly as the server does; null where the server would refuse it. */
  function decodeTiles(src) {
    const set = new Set();
    let i = 0, cx = 0, cy = 0, lastRow = -1, lastRowData = [];
    while (i < src.length) {
      if (cx < 0 || cx > 1023 || cy < 0 || cy > 1023) return null;
      const b = src[i++], op = (b & 192) >> 6;
      let n;
      if ((b & 32) === 0) n = (b & 31) + 1;
      else { if (i >= src.length) return null; n = (((b & 3) << 8) | src[i++]) + 1; }
      switch (op) {
        case 0: cx += n; break;
        case 1:
          if (cy !== lastRow) { lastRowData = []; lastRow = cy; }
          lastRowData.push([cx, n]);
          for (let k = 0; k < n; k++) set.add(cy * N + cx + k);
          cx += n;
          break;
        case 2: if (cx !== 0) return null; cy += n; break;
        case 3:
          if (cx !== 0 || cy === 0) return null;
          for (let r = 0; r < n; r++) for (const [x0, w] of lastRowData) for (let k = 0; k < w; k++) set.add((cy + r) * N + x0 + k);
          cy += n; lastRow = cy - 1;
          break;
      }
      if (cx === N) { cx = 0; cy++; }
    }
    if (i !== src.length || cy !== N) return null;
    return set;
  }

  const rect = (x, y, w, h) => {
    const s = new Set();
    for (let yy = Math.max(0, y); yy < Math.min(N, y + h); yy++) for (let xx = Math.max(0, x); xx < Math.min(N, x + w); xx++) s.add(yy * N + xx);
    return s;
  };

  // ---- regions ------------------------------------------------------------------------------------
  const FLAG_CHUNKS = [["base", "rBSE"], ["noAntiwarp", "rNAW"], ["noWeapons", "rNWP"], ["noFlagDrops", "rNFL"]];
  const fingerprint = (r) => JSON.stringify([r.name, !!r.base, !!r.noAntiwarp, !!r.noWeapons, !!r.noFlagDrops, r.meta ?? null, r.tiles?.size ?? 0]);

  function parseRegion(c) {
    const r = { name: "", tiles: new Set(), base: false, noAntiwarp: false, noWeapons: false, noFlagDrops: false, meta: null, raw: c.raw };
    for (const s of chunks(c.data, 0, c.data.length)) {
      if (s.type === "rNAM") r.name = String.fromCharCode(...s.data);
      else if (s.type === "rTIL") r.tiles = decodeTiles(s.data) || new Set();
      else if (s.type === "rDGM") { try { r.meta = JSON.parse(fromUtf8(s.data)); } catch { r.meta = null; } }
      else for (const [k, t] of FLAG_CHUNKS) if (s.type === t) r[k] = true;
    }
    r._tiles = r.tiles; r._fp = fingerprint(r); // to tell an untouched region from an edited one
    return r;
  }

  const unchanged = (r) => r.raw && r._tiles === r.tiles && r._fp === fingerprint(r);

  /** A region's REGN chunk: our fields; other sub-chunks of a region read from a map are kept. */
  function regionChunk(r) {
    if (unchanged(r)) return r.raw;
    const name = String(r.name || "Region").replace(/[^\x20-\x7e]/g, "").slice(0, 64) || "Region";
    const parts = [chunk("rNAM", ascii(name)), chunk("rTIL", encodeTiles(r.tiles || new Set()))];
    for (const [k, t] of FLAG_CHUNKS) if (r[k]) parts.push(chunk(t, new Uint8Array()));
    if (r.meta) parts.push(chunk("rDGM", utf8(JSON.stringify(r.meta))));
    if (r.raw) {
      const kept = new Set(["rNAM", "rTIL", "rDGM", ...FLAG_CHUNKS.map(([, t]) => t)]);
      const own = r.raw.subarray(8, 8 + u32(r.raw, 4));
      for (const s of chunks(own, 0, own.length)) if (!kept.has(s.type)) parts.push(s.raw);
    }
    return chunk("REGN", cat(parts));
  }

  function split(prefix) {
    if (!prefix || !prefix.length) return { bmp: null, elvl: null };
    const s = LvlFile.split(prefix);
    return { bmp: s.bmp, elvl: s.elvl };
  }

  function regions(prefix) {
    const { elvl } = split(prefix);
    if (!elvl || elvl.length < 12) return [];
    return chunks(elvl, 12, Math.min(elvl.length, u32(elvl, 4))).filter((c) => c.type === "REGN").map(parseRegion);
  }

  /** The prefix with these regions instead of its old ones (other chunks kept, in place). */
  function withRegions(prefix, regs) {
    const { bmp, elvl } = split(prefix);
    const old = elvl && elvl.length >= 12 ? chunks(elvl, 12, Math.min(elvl.length, u32(elvl, 4))) : [];
    const oldRegions = old.filter((c) => c.type === "REGN");
    if (prefix && regs.length === oldRegions.length && regs.every((r, i) => unchanged(r) && r.raw.length === oldRegions[i].raw.length
      && r.raw.every((v, k) => v === oldRegions[i].raw[k]))) return prefix; // nothing changed
    const newRegions = regs.map(regionChunk);
    const parts = [];
    let placed = false;
    for (const c of old) {
      if (c.type !== "REGN") { parts.push(c.raw); continue; }
      if (!placed) { parts.push(...newRegions); placed = true; }
    }
    if (!placed) parts.push(...newRegions);
    const body = cat(parts), block = new Uint8Array(12 + body.length);
    block.set(ascii("elvl"), 0); put32(block, 4, block.length); block.set(body, 12);
    return LvlFile.join(bmp, block, new Uint8Array());
  }

  /** Region names the server can tell apart (it keys them case-insensitively). */
  function uniqueNames(regs) {
    const seen = new Set(), count = new Map();
    return regs.map((r) => {
      const base = String(r.name || "").trim() || "Region";
      let name = base, low = base.toLowerCase();
      if (seen.has(low)) {
        let n = (count.get(low) || 1) + 1;
        while (seen.has(`${base} ${n}`.toLowerCase())) n++;
        count.set(low, n);
        name = `${base} ${n}`;
      }
      seen.add(name.toLowerCase());
      return name === r.name ? r : { ...r, name };
    });
  }

  return { regions, withRegions, encodeTiles, decodeTiles, rect, uniqueNames, N };
});
