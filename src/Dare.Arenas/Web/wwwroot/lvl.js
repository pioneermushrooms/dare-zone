// .lvl maps: read, write and draw. Shared by the hub thumbnail and the map editor.
// A .lvl is an optional prefix (a BMP tileset, possibly with eLVL data, or a bare eLVL block) kept
// verbatim, then one u32 per tile: x (12 bits) | y (12 bits) << 12 | tile (8 bits) << 24.
"use strict";

window.Lvl = (() => {
  const N = 1024, TS = 16;

  // Flat "chart" colours (no game art): walls cyan, special tiles their own colour.
  const SPECIAL = [
    [162, "Door (vertical)", "#ff8a1f"], [166, "Door (horizontal)", "#ff8a1f"],
    [170, "Flag", "#facc15"], [171, "Safe zone", "#228046"], [172, "Goal", "#a78bfa"],
    [173, "Fly-over", "#46648c"], [176, "Fly-under", "#3a5373"],
    [216, "Small asteroid", "#788ca0"], [217, "Large asteroid", "#6b7d90"],
    [218, "Medium asteroid", "#728699"], [219, "Space station", "#67e8f9"],
    [220, "Wormhole", "#ec4899"],
  ];
  function color(t) {
    const s = SPECIAL.find((x) => x[0] === t);
    if (s) return s[2];
    if (t >= 162 && t <= 169) return "#ff8a1f";
    if (t >= 173 && t <= 190) return "#46648c";
    if (t > 190) return "#8aa1b6";
    return "#22d3ee";
  }

  // 8-bit (plain or RLE8) and 24-bit BMP, as used by map tilesets. Returns a canvas or throws.
  function decodeBmp(buf) {
    const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    if (buf.length < 54 || dv.getUint16(0, true) !== 0x4d42) throw new Error("not a BMP");
    const off = dv.getUint32(10, true), hdr = dv.getUint32(14, true);
    const w = dv.getInt32(18, true), hRaw = dv.getInt32(22, true), h = Math.abs(hRaw);
    const bpp = dv.getUint16(28, true), comp = dv.getUint32(30, true);
    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new Error("bad BMP size");
    const ncol = Math.min(256, dv.getUint32(46, true) || (bpp <= 8 ? 1 << bpp : 0));
    const pal = [];
    for (let i = 0; i < ncol; i++) { const p = 14 + hdr + i * 4; pal.push([buf[p + 2], buf[p + 1], buf[p]]); }
    const out = new ImageData(w, h);
    const put = (x, y, c) => {
      if (!c || x < 0 || x >= w || y < 0 || y >= h) return;
      const yy = hRaw > 0 ? h - 1 - y : y, i = (yy * w + x) * 4;
      out.data[i] = c[0]; out.data[i + 1] = c[1]; out.data[i + 2] = c[2]; out.data[i + 3] = 255;
    };
    if (bpp === 8 && comp === 1) {
      let p = off, x = 0, y = 0;
      while (p < buf.length - 1) {
        const a = buf[p++], b = buf[p++];
        if (a > 0) { for (let i = 0; i < a; i++) put(x++, y, pal[b]); }
        else if (b === 0) { x = 0; y++; }
        else if (b === 1) break;
        else if (b === 2) { x += buf[p++]; y += buf[p++]; }
        else { for (let i = 0; i < b; i++) put(x++, y, pal[buf[p++]]); if (b & 1) p++; }
      }
    } else if (bpp === 8) {
      const row = (w + 3) & ~3;
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) put(x, y, pal[buf[off + y * row + x]]);
    } else if (bpp === 24) {
      const row = (w * 3 + 3) & ~3;
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const i = off + y * row + x * 3; put(x, y, [buf[i + 2], buf[i + 1], buf[i]]); }
    } else throw new Error(`unsupported BMP (${bpp}-bit, compression ${comp})`);
    const c = document.createElement("canvas"); c.width = w; c.height = h;
    c.getContext("2d").putImageData(out, 0, 0);
    return c;
  }

  function parse(bytes) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const grid = new Uint8Array(N * N);
    let start = 0, prefix = null, tileset = null;
    if (bytes.length >= 6 && bytes[0] === 0x42 && bytes[1] === 0x4d) {
      start = Math.min(dv.getUint32(2, true), bytes.length);
      prefix = bytes.slice(0, start);
      try { tileset = decodeBmp(prefix); } catch { tileset = null; }
    } else if (bytes.length >= 8 && String.fromCharCode(...bytes.slice(0, 4)) === "elvl") {
      start = Math.min(dv.getUint32(4, true), bytes.length);
      prefix = bytes.slice(0, start);
    }
    let count = 0;
    for (let p = start; p + 4 <= bytes.length; p += 4) {
      const v = dv.getUint32(p, true), x = v & 0xfff, y = (v >>> 12) & 0xfff, t = v >>> 24;
      if (x < N && y < N && t) { if (!grid[y * N + x]) count++; grid[y * N + x] = t; }
    }
    return { grid, prefix, tileset, count };
  }

  function serialize(grid, prefix) {
    const tiles = [];
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
      const t = grid[y * N + x];
      if (t) tiles.push((x | (y << 12) | (t << 24)) >>> 0);
    }
    const head = prefix ? prefix.length : 0;
    const out = new Uint8Array(head + tiles.length * 4);
    if (prefix) out.set(prefix, 0);
    const dv = new DataView(out.buffer);
    tiles.forEach((v, i) => dv.setUint32(head + i * 4, v, true));
    return out;
  }

  // The map onto a square canvas: the whole 1024x1024, or (fit) just the part with tiles.
  function drawOverview(canvas, grid, fit) {
    const size = canvas.width, ctx = canvas.getContext("2d");
    let x0 = 0, y0 = 0, side = N;
    if (fit) {
      let minX = N, minY = N, maxX = -1, maxY = -1;
      for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) if (grid[y * N + x]) {
        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
      }
      if (maxX >= 0) {
        side = Math.min(N, Math.max(maxX - minX, maxY - minY) + 9);
        x0 = Math.max(0, Math.min(N - side, Math.floor((minX + maxX - side) / 2)));
        y0 = Math.max(0, Math.min(N - side, Math.floor((minY + maxY - side) / 2)));
      }
    }
    const k = size / side; // canvas pixels per tile
    ctx.fillStyle = "#04070e"; ctx.fillRect(0, 0, size, size);
    for (let y = y0; y < y0 + side; y++) for (let x = x0; x < x0 + side; x++) {
      const t = grid[y * N + x]; if (!t) continue;
      ctx.fillStyle = color(t);
      ctx.fillRect(Math.floor((x - x0) * k), Math.floor((y - y0) * k), Math.max(1, Math.ceil(k)), Math.max(1, Math.ceil(k)));
    }
  }

  return { N, TS, SPECIAL, color, decodeBmp, parse, serialize, drawOverview };
})();
