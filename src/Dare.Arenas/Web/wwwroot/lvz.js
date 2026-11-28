// .lvz packages: read, edit and write them, and the LVZ Toolkit's .ini project format. No DOM
// needed (zlib is the platform's CompressionStream), so node tests use it as is.
//
// A .lvz is "CONT", a section count, then sections: "CONT", unpacked size, file time, packed size,
// a NUL-ended name and zlib data. Named sections are files (images, sounds, replacement graphics).
// A section with time 0 and no name holds objects: "CLV1"/"CLV2", object count, image count, ten
// bytes per object, then image definitions (columns, rows, animation time, NUL-ended file name).
//
// Object words (little-endian): id word = map flag in bit 0, id in bits 1-15; x and y words =
// plain signed pixels for map objects; for CLV2 screen objects a reference point in bits 0-3 and
// a signed 12-bit offset in bits 4-15; image byte; layer byte; time word = display time (1/10 s)
// in bits 0-11, mode in bits 12-15.
// CLV1 screen words are not split that way: the whole word as an i16 picks one of three points
// (negative: the view's bottom-right; 15001 and up: the bottom-right of a 960-pixel box centred in
// the view; else the top-left) and its low 12 bits are the offset. Measured against Continuum by
// oss-core (oss-formats/src/lvz.rs). They are read into the CLV2 model here (top-left, B, or C
// + 480) and written back as CLV2, which places them the same.
//
// The model: { version, files: [{ name, time, data }], images: [{ file, cols, rows, time }],
//   objects: [{ map, id, x, y, xRef, yRef, image, layer, mode, time }] }
// Unchanged sections are written back with their original compressed bytes, so an .lvz read and
// written without edits comes out byte for byte the same.
"use strict";

(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.Lvz = api;
})(typeof self !== "undefined" ? self : this, () => {
  const LAYERS = ["BelowAll", "AfterBackground", "AfterTiles", "AfterWeapons", "AfterShips", "AfterGauges", "AfterChat", "TopMost"];
  const MODES = ["ShowAlways", "EnterZone", "EnterArena", "Kill", "Death", "ServerControlled"];
  // CLV2 screen reference points: index -> [.ini prefix, description]
  const REFS = [["", "Top-left of the screen"], ["C", "Screen centre"], ["B", "Bottom-right of the screen"], ["S", "Stats box, lower right"],
    ["G", "Top-right of specials"], ["F", "Bottom-right of specials"], ["E", "Below energy bar"], ["T", "Top-left of chat"],
    ["R", "Top-left of radar"], ["O", "Top-left of radar text"], ["W", "Top-left of weapons"], ["V", "Bottom-left of weapons"]];
  const MAP_PIXELS = 16384;
  const MAX_SECTIONS = 4096, MAX_SECTION_BYTES = 16 * 1024 * 1024;

  const enc = new TextEncoder(), dec = new TextDecoder("latin1");
  const u32 = (b, p) => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) >>> 0;
  const u16 = (b, p) => b[p] | (b[p + 1] << 8);
  const i16 = (v) => (v << 16) >> 16;
  const sign12 = (v) => ((v & 0xfff) << 20) >> 20;
  const ascii = (b, p, n) => dec.decode(b.subarray(p, p + n));
  const same = (a, b) => a && b && a.length === b.length && a.every((v, i) => v === b[i]);

  // ---- zlib --------------------------------------------------------------------------------------
  async function pump(stream, bytes) {
    const out = new Response(new Blob([bytes]).stream().pipeThrough(stream));
    return new Uint8Array(await out.arrayBuffer());
  }
  const inflate = (b) => pump(new DecompressionStream("deflate"), b);
  const deflate = (b) => pump(new CompressionStream("deflate"), b);

  // ---- reading -------------------------------------------------------------------------------------
  function sections(bytes) {
    const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    if (b.length < 8 || ascii(b, 0, 4) !== "CONT") throw new Error("not an .lvz file");
    const count = u32(b, 4);
    if (count > MAX_SECTIONS) throw new Error("bad section count");
    const out = [];
    let p = 8;
    for (let i = 0; i < count; i++) {
      if (p + 16 > b.length || ascii(b, p, 4) !== "CONT") throw new Error(`section ${i + 1} is damaged`);
      const size = u32(b, p + 4), time = u32(b, p + 8), packedSize = u32(b, p + 12);
      p += 16;
      let e = p; while (e < b.length && b[e] !== 0) e++;
      if (e >= b.length) throw new Error(`section ${i + 1} has no name end`);
      const name = ascii(b, p, e - p);
      p = e + 1;
      if (size > MAX_SECTION_BYTES || p + packedSize > b.length) throw new Error(`section ${i + 1} sizes are wrong`);
      out.push({ name, time, size, packed: b.slice(p, p + packedSize) });
      p += packedSize;
    }
    return out;
  }
  async function unpack(s) {
    try {
      const d = await inflate(s.packed);
      if (d.length !== s.size) throw new Error("size");
      return d;
    } catch (err) {
      if (s.packed.length === s.size) return s.packed.slice(); // stored, not compressed
      throw new Error(`${s.name || "the object section"} couldn't be unpacked`);
    }
  }
  function clv1Ref(word) { // CLV1 screen word -> [CLV2 ref, offset]
    const w = i16(word), off = sign12(word);
    if (w < 0) return [2, off];
    if (w >= 15001) return [1, off + 480];
    return [0, off];
  }
  function parseObjects(data) {
    if (data.length < 12) throw new Error("object section is cut short");
    const version = ascii(data, 0, 4);
    if (version !== "CLV1" && version !== "CLV2") throw new Error(`unknown object section ${JSON.stringify(version)}`);
    const nObj = u32(data, 4), nImg = u32(data, 8);
    if (12 + nObj * 10 > data.length) throw new Error("object count is wrong");
    const objects = [];
    for (let i = 0; i < nObj; i++) {
      const p = 12 + i * 10, idw = u16(data, p), xw = u16(data, p + 2), yw = u16(data, p + 4), tw = u16(data, p + 8);
      const o = { map: !!(idw & 1), id: idw >> 1, x: 0, y: 0, xRef: 0, yRef: 0, image: data[p + 6], layer: data[p + 7], time: tw & 0xfff, mode: tw >> 12 };
      if (o.map) { o.x = i16(xw); o.y = i16(yw); }
      else if (version === "CLV2") { o.xRef = xw & 15; o.x = sign12(xw >> 4); o.yRef = yw & 15; o.y = sign12(yw >> 4); }
      else { [o.xRef, o.x] = clv1Ref(xw); [o.yRef, o.y] = clv1Ref(yw); o.fromClv1 = true; }
      objects.push(o);
    }
    const images = [];
    let p = 12 + nObj * 10;
    for (let i = 0; i < nImg; i++) {
      if (p + 6 > data.length) throw new Error(`image ${i} is cut short`);
      let e = p + 6; while (e < data.length && data[e] !== 0) e++;
      if (e >= data.length) throw new Error(`image ${i} has no name end`);
      images.push({ cols: i16(u16(data, p)), rows: i16(u16(data, p + 2)), time: i16(u16(data, p + 4)), file: ascii(data, p + 6, e - p - 6) });
      p = e + 1;
    }
    return { version, objects, images };
  }

  /** An .lvz's bytes -> the model. Throws Error with a readable message on a damaged file. */
  async function read(bytes) {
    const lvz = { version: "CLV2", files: [], images: [], objects: [], warnings: [] };
    const objSecs = [];
    for (const s of sections(bytes)) {
      if (s.time === 0 && s.name === "") {
        const data = await unpack(s), sec = parseObjects(data);
        // several object sections are merged; their image numbers are moved past the earlier ones'
        const base = lvz.images.length;
        for (const o of sec.objects) { o.image += base; lvz.objects.push(o); }
        lvz.images.push(...sec.images);
        objSecs.push({ s, data, version: sec.version });
      } else lvz.files.push({ name: s.name, time: s.time, data: null, _sec: s });
    }
    if (objSecs.length === 1) { lvz.version = objSecs[0].version; lvz._obj = objSecs[0]; }
    else if (objSecs.length > 1) lvz.warnings.push(`${objSecs.length} object sections were merged into one.`);
    if (lvz.objects.some((o) => o.fromClv1)) lvz.warnings.push("Old-style (CLV1) screen positions were converted; it is saved as CLV2.");
    if (lvz.images.length > 256) lvz.warnings.push("More than 256 images: objects can only use the first 256.");
    return lvz;
  }
  /** A file's bytes (unpacked on first use). */
  async function fileData(f) {
    if (!f.data) f.data = await unpack(f._sec);
    return f.data;
  }

  // ---- writing ---------------------------------------------------------------------------------------
  function objectBytes(lvz) {
    const screen = lvz.objects.some((o) => !o.map);
    const version = lvz.version === "CLV1" && !screen ? "CLV1" : "CLV2";
    const names = lvz.images.map((im) => enc.encode(im.file));
    const size = 12 + lvz.objects.length * 10 + names.reduce((s, n) => s + 7 + n.length, 0);
    const b = new Uint8Array(size), dv = new DataView(b.buffer);
    b.set(enc.encode(version), 0); dv.setUint32(4, lvz.objects.length, true); dv.setUint32(8, lvz.images.length, true);
    const clampOff = (v) => Math.max(-2048, Math.min(2047, Math.round(v)));
    lvz.objects.forEach((o, i) => {
      const p = 12 + i * 10;
      dv.setUint16(p, ((o.id & 0x7fff) << 1) | (o.map ? 1 : 0), true);
      if (o.map) { dv.setInt16(p + 2, Math.max(-32768, Math.min(32767, Math.round(o.x))), true); dv.setInt16(p + 4, Math.max(-32768, Math.min(32767, Math.round(o.y))), true); }
      else { dv.setUint16(p + 2, ((clampOff(o.x) & 0xfff) << 4) | (o.xRef & 15), true); dv.setUint16(p + 4, ((clampOff(o.y) & 0xfff) << 4) | (o.yRef & 15), true); }
      b[p + 6] = o.image & 255; b[p + 7] = o.layer & 255;
      dv.setUint16(p + 8, (Math.max(0, Math.min(4095, Math.round(o.time))) & 0xfff) | ((o.mode & 15) << 12), true);
    });
    let p = 12 + lvz.objects.length * 10;
    lvz.images.forEach((im, i) => {
      dv.setInt16(p, im.cols || 1, true); dv.setInt16(p + 2, im.rows || 1, true); dv.setInt16(p + 4, im.time || 0, true);
      b.set(names[i], p + 6); p += 7 + names[i].length;
    });
    return b;
  }
  function header(out, name, time, size, packed) {
    const n = enc.encode(name), h = new Uint8Array(16 + n.length + 1), dv = new DataView(h.buffer);
    h.set(enc.encode("CONT"), 0); dv.setUint32(4, size, true); dv.setUint32(8, time >>> 0, true); dv.setUint32(12, packed.length, true);
    h.set(n, 16);
    out.push(h, packed);
  }
  /** The model -> .lvz bytes. Untouched sections keep their compressed bytes. */
  async function write(lvz) {
    const parts = [];
    let count = 0;
    for (const f of lvz.files) {
      if (f._sec && !f.dirty) header(parts, f.name, f._sec.time, f._sec.size, f._sec.packed);
      else {
        const data = f.data || new Uint8Array(0);
        header(parts, f.name, f.time || Math.floor(Date.now() / 1000), data.length, await deflate(data));
      }
      count++;
    }
    if (lvz.objects.length || lvz.images.length) {
      const data = objectBytes(lvz);
      if (lvz._obj && same(data, lvz._obj.data)) header(parts, "", 0, data.length, lvz._obj.s.packed);
      else header(parts, "", 0, data.length, await deflate(data));
      count++;
    }
    const total = 8 + parts.reduce((s, x) => s + x.length, 0), out = new Uint8Array(total);
    out.set(enc.encode("CONT"), 0); new DataView(out.buffer).setUint32(4, count, true);
    let p = 8; for (const x of parts) { out.set(x, p); p += x.length; }
    return out;
  }
  function blank() { return { version: "CLV2", files: [], images: [], objects: [], warnings: [] }; }
  /** Put a file in (replacing one of the same name, any case). */
  function putFile(lvz, name, data) {
    const f = lvz.files.find((x) => x.name.toLowerCase() === name.toLowerCase());
    if (f) { f.data = data; f.dirty = true; f.time = Math.floor(Date.now() / 1000); return f; }
    const n = { name, time: Math.floor(Date.now() / 1000), data, dirty: true };
    lvz.files.push(n);
    return n;
  }
  /** Remove image i: objects using it go too; later image numbers move down. */
  function removeImage(lvz, i) {
    lvz.images.splice(i, 1);
    lvz.objects = lvz.objects.filter((o) => o.image !== i);
    for (const o of lvz.objects) if (o.image > i) o.image--;
  }
  function newObject(fields) {
    return Object.assign({ map: true, id: 0, x: 8192, y: 8192, xRef: 0, yRef: 0, image: 0, layer: 2, mode: 0, time: 0 }, fields);
  }

  // ---- .ini (LVZ Toolkit's BuildLevel projects) ---------------------------------------------------------
  // Times in the .ini are 1/100 s; objects store 1/10 s, so they're divided by 10. Image animation
  // periods are written as given (stock files hold 100 for a still image, 1300 for 13 frames).
  const findIn = (list, s, def) => { const i = list.findIndex((x) => x.toLowerCase() === String(s || "").trim().toLowerCase()); return i < 0 ? def : i; };
  /**
   * Text of a project + its files (Map of lower-case name -> bytes) -> { lvz, outFile, missing, errors }.
   * Files the project names but the map lacks are listed in missing (images may come from elsewhere).
   */
  function fromIni(text, files = new Map()) {
    const lvz = blank(), errors = [], missing = [];
    let section = "", outFile = "";
    const imageIndex = new Map(); // IMAGE number -> index
    const lines = String(text).split(/\r?\n/);
    lines.forEach((raw, n) => {
      const line = raw.replace(/;.*$/, "").trim();
      if (!line) return;
      const at = `line ${n + 1}`;
      const sec = /^\[(.+)\]$/.exec(line);
      if (sec) { section = sec[1].trim().toLowerCase(); return; }
      const kv = /^([A-Za-z0-9_]+)\s*=\s*(.*)$/.exec(line);
      if (!section || section === "" ) {
        if (!kv) { errors.push(`${at}: not "Key=value"`); return; }
        const k = kv[1].toLowerCase(), v = kv[2].trim();
        if (k === "outfile") outFile = v;
        else if (k === "file") {
          const name = v.replace(/^.*[\\/]/, ""), data = files.get(name.toLowerCase());
          if (data) putFile(lvz, name, data); else missing.push(name);
        } else errors.push(`${at}: unknown key ${kv[1]}`);
        return;
      }
      if (section === "objectimages") {
        const m = /^IMAGE(\d+)\s*=\s*(.*)$/i.exec(line);
        if (!m) { errors.push(`${at}: expected IMAGE<n>=file[,columns,rows,period]`); return; }
        const [file, cols, rows, time] = m[2].split(",").map((s) => s.trim());
        imageIndex.set(+m[1], lvz.images.length);
        lvz.images.push({ file: file.replace(/^.*[\\/]/, ""), cols: +cols || 1, rows: +rows || 1, time: time === undefined || time === "" ? 100 : +time || 0 });
        return;
      }
      if (section === "mapobjects" || section === "screenobjects") {
        const f = line.split(",").map((s) => s.trim());
        if (f.length < 3) { errors.push(`${at}: expected x,y,IMAGE<n>[,layer,mode,time,id]`); return; }
        const img = /^IMAGE(\d+)$/i.exec(f[2]);
        if (!img || !imageIndex.has(+img[1])) { errors.push(`${at}: ${f[2]} isn't in [objectimages]`); return; }
        const o = newObject({ map: section === "mapobjects", image: imageIndex.get(+img[1]),
          layer: findIn(LAYERS, f[3], 2), mode: findIn(MODES, f[4], 0), time: Math.round((+f[5] || 0) / 10), id: +f[6] || 0 });
        if (f[3] && findIn(LAYERS, f[3], -1) < 0) errors.push(`${at}: unknown layer ${f[3]}`);
        if (f[4] && findIn(MODES, f[4], -1) < 0) errors.push(`${at}: unknown mode ${f[4]}`);
        if (o.time > 4095) { errors.push(`${at}: display time ${f[5]} is more than 409.5 s; cut to 409.5 s`); o.time = 4095; }
        const coord = (s) => {
          const m = /^([A-Za-z]?)\s*(-?\d+)$/.exec(s);
          if (!m) return null;
          const ref = REFS.findIndex((r) => r[0] === m[1].toUpperCase());
          return [ref, +m[2]];
        };
        const cx = coord(f[0]), cy = coord(f[1]);
        if (!cx || !cy || cx[0] < 0 || cy[0] < 0) { errors.push(`${at}: bad coordinates ${f[0]},${f[1]}`); return; }
        if (o.map) {
          if (cx[0] || cy[0]) { errors.push(`${at}: map objects take plain pixel coordinates`); return; }
          o.x = cx[1]; o.y = cy[1];
        } else { [o.xRef, o.x] = cx; [o.yRef, o.y] = cy; }
        lvz.objects.push(o);
        return;
      }
      errors.push(`${at}: unknown section [${section}]`);
    });
    return { lvz, outFile, missing, errors };
  }
  /** The model -> project text (its files are the .lvz's files, saved next to it). */
  function toIni(lvz, outFile = "out.lvz") {
    const L = ["; made by the Foundation LVZ editor", `OutFile=${outFile}`, ""];
    for (const f of lvz.files) L.push(`File=${f.name}`);
    L.push("", "[objectimages]");
    lvz.images.forEach((im, i) => L.push(`IMAGE${i}=${im.file}` + (im.cols > 1 || im.rows > 1 || im.time !== 100 ? `,${im.cols},${im.rows},${im.time}` : "")));
    const line = (o, x, y) => `${x},${y},IMAGE${o.image},${LAYERS[o.layer] || LAYERS[2]},${MODES[o.mode] || MODES[0]},${o.time * 10},${o.id}`;
    const maps = lvz.objects.filter((o) => o.map), screens = lvz.objects.filter((o) => !o.map);
    if (maps.length) { L.push("", "[mapobjects]"); for (const o of maps) L.push(line(o, o.x, o.y)); }
    if (screens.length) { L.push("", "[screenobjects]"); for (const o of screens) L.push(line(o, (REFS[o.xRef] || REFS[0])[0] + o.x, (REFS[o.yRef] || REFS[0])[0] + o.y)); }
    return L.join("\r\n") + "\r\n";
  }

  // ---- images ---------------------------------------------------------------------------------------------
  // BMP (1/4/8-bit, RLE8, 24/32-bit) -> { width, height, rgba }. Continuum draws pure black in LVZ
  // images as see-through, so black pixels get alpha 0.
  function decodeBmp(bytes) {
    const b = bytes;
    if (b.length < 54 || b[0] !== 0x42 || b[1] !== 0x4d) throw new Error("not a BMP");
    const off = u32(b, 10), hdr = u32(b, 14), w = u32(b, 18) | 0, hRaw = u32(b, 22) | 0, h = Math.abs(hRaw);
    const bpp = u16(b, 28), comp = u32(b, 30);
    if (w <= 0 || h <= 0 || w > 8192 || h > 8192) throw new Error("bad BMP size");
    const rgba = new Uint8ClampedArray(w * h * 4);
    const row = (y) => (hRaw > 0 ? h - 1 - y : y);
    const put = (x, y, r, g, bl) => { if (x >= w || y >= h) return; const i = (row(y) * w + x) * 4; rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = bl; rgba[i + 3] = r | g | bl ? 255 : 0; };
    if (bpp <= 8) {
      const n = Math.min(256, u32(b, 46) || 1 << bpp), pal = [];
      for (let i = 0; i < n; i++) { const p = 14 + hdr + i * 4; pal.push([b[p + 2], b[p + 1], b[p]]); }
      const c = (i) => pal[i] || [0, 0, 0];
      if (bpp === 8 && comp === 1) {
        let p = off, x = 0, y = 0;
        while (p < b.length - 1) {
          const a = b[p++], v = b[p++];
          if (a) { for (let i = 0; i < a; i++) put(x++, y, ...c(v)); }
          else if (v === 0) { x = 0; y++; } else if (v === 1) break;
          else if (v === 2) { x += b[p++]; y += b[p++]; }
          else { for (let i = 0; i < v; i++) put(x++, y, ...c(b[p++])); if (v & 1) p++; }
        }
      } else if (comp === 0) {
        const stride = ((w * bpp + 31) >>> 5) << 2;
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
          const bit = x * bpp, byte = b[off + y * stride + (bit >>> 3)];
          put(x, y, ...c(bpp === 8 ? byte : (byte >>> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1)));
        }
      } else throw new Error(`unsupported BMP compression ${comp}`);
    } else if (bpp === 24 || bpp === 32) {
      const step = bpp / 8, stride = (w * step + 3) & ~3;
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const s = off + y * stride + x * step; put(x, y, b[s + 2], b[s + 1], b[s]); }
    } else throw new Error(`unsupported ${bpp}-bit BMP`);
    return { width: w, height: h, rgba };
  }
  /** Image file bytes -> a canvas (browser only). BMPs are decoded here; PNG, GIF, JPEG by the browser. */
  async function imageCanvas(name, bytes) {
    const c = document.createElement("canvas");
    if (/\.(bmp|bm2)$/i.test(name) || (bytes[0] === 0x42 && bytes[1] === 0x4d)) {
      const im = decodeBmp(bytes);
      c.width = im.width; c.height = im.height;
      c.getContext("2d").putImageData(new ImageData(im.rgba, im.width, im.height), 0, 0);
      return c;
    }
    const bmp = await createImageBitmap(new Blob([bytes]));
    c.width = bmp.width; c.height = bmp.height;
    c.getContext("2d").drawImage(bmp, 0, 0);
    return c;
  }
  /**
   * Browser only: each image definition's picture, for drawing. Returns [{ canvas, fw, fh, missing }]
   * per image: the whole sheet, one frame's size, and missing when its file isn't in this .lvz.
   */
  async function pictures(lvz) {
    const byName = new Map(lvz.files.map((f) => [f.name.toLowerCase(), f]));
    const cache = new Map(), out = [];
    for (const im of lvz.images) {
      const key = im.file.toLowerCase();
      if (!cache.has(key)) {
        const f = byName.get(key);
        let canvas = null;
        if (f) { try { canvas = await imageCanvas(f.name, await fileData(f)); } catch { canvas = null; } }
        cache.set(key, canvas);
      }
      const canvas = cache.get(key), cols = Math.max(1, im.cols || 1), rows = Math.max(1, im.rows || 1);
      out.push(canvas ? { canvas, fw: Math.max(1, Math.floor(canvas.width / cols)), fh: Math.max(1, Math.floor(canvas.height / rows)), missing: false }
        : { canvas: null, fw: 32, fh: 32, missing: true });
    }
    return out;
  }
  /** Draw frame k (counted across rows) of picture p at (x, y), scaled by z; a missing one as a labelled box. */
  function drawFrame(ctx, p, im, k, x, y, z, label) {
    if (!p || !p.canvas) {
      ctx.save(); ctx.strokeStyle = "#ff8f8f"; ctx.setLineDash([3, 3]); ctx.strokeRect(x + 0.5, y + 0.5, 32 * z, 32 * z);
      if (z >= 0.5) { ctx.fillStyle = "#ff8f8f"; ctx.font = "10px sans-serif"; ctx.fillText(label || "?", x + 3, y + 12, 32 * z - 4); }
      ctx.restore(); return;
    }
    const cols = Math.max(1, im.cols || 1), n = cols * Math.max(1, im.rows || 1), f = ((k % n) + n) % n;
    ctx.drawImage(p.canvas, (f % cols) * p.fw, Math.floor(f / cols) * p.fh, p.fw, p.fh, x, y, p.fw * z, p.fh * z);
  }
  /** Which frame an animated image shows at time t (ms): the period is the image's time in 1/100 s for the whole run. */
  const frameAt = (im, t) => { const n = Math.max(1, im.cols || 1) * Math.max(1, im.rows || 1); if (n < 2 || !(im.time > 0)) return 0; return Math.floor((t / (im.time * 10)) * n) % n; };
  // Where the HUD reference points sit on a w x h screen. Approximate: taken from the LVZ Toolkit's
  // refpoints picture (Continuum at 889 x 667); the real spots move with the player's HUD settings.
  function anchor(ref, w, h) {
    switch (ref) {
      case 1: return [w / 2, h / 2];
      case 2: return [w, h];
      case 3: return [155, 30];
      case 4: return [36, Math.round(h / 2 - 68)];
      case 5: return [36, Math.round(h / 2 + 175)];
      case 6: return [w / 2, 38];
      case 7: return [0, h - 45];
      case 8: return [w - 155, h - 155];
      case 9: return [w - 130, h - 170];
      case 10: return [w - 36, Math.round(h / 2 - 68)];
      case 11: return [w - 36, Math.round(h / 2 + 142)];
      default: return [0, 0];
    }
  }
  const isImageName = (n) => /\.(bmp|bm2|png|gif|jpe?g)$/i.test(n);
  /** A safe file name for inside an .lvz: letters, digits, . _ - ; at most 64. */
  const safeName = (n) => (String(n).replace(/^.*[\\/]/, "").replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^\.+/, "").slice(-64) || "image.png");

  return { LAYERS, MODES, REFS, MAP_PIXELS, read, write, fileData, parseObjects, objectBytes, blank, putFile, removeImage, newObject,
    fromIni, toIni, decodeBmp, imageCanvas, pictures, drawFrame, frameAt, anchor, isImageName, safeName, sections };
});
