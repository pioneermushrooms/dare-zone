#!/usr/bin/env node
// Builds the training arena's generated files from the tutorial script:
//   deploy/arenas/#training/tutorial.lvz  the covers, mentor box, portrait, popups, beacon and one
//                                         image per script line (Continuum keys filled in)
//   deploy/arenas/#training/training.lvl  the map (tools/tutorial-lvz/map.js)
//   site/tutorial-mentor.png              the two portrait frames side by side, for the browser
// Run after editing src/Dare.Arenas/tutorial/script.json:  node tools/tutorial-lvz/build.js
//
// Object ids (the zone toggles them per player; all are ServerControlled, so they start off):
//   4000 + HudPart  black covers: Radar 4000, Energy 4001, StatBox 4002, Items 4003 (left and
//                   right columns), Chat 4004; 4005 the band just above the chat lines. TopMost.
//   4100 mentor box, 4101/4102 portrait (mouth closed/open), 4110 skip popup, 4111 "Try the
//   tutorial" popup, 4200 the beacon (a map object), 5000 + TutorialLine.Index each line.
// Screen positions use the CLV2 reference points (lvz.js REFS): the mentor box is pinned to the
// chat's top-left (7) with a negative y, so it sits bottom-left above the chat; covers follow the
// part they hide.
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");
const Lvz = require("../../src/Dare.Arenas/Web/wwwroot/lvz.js");
const Font = require("./font.js");
const Portrait = require("./portrait.js");
const TrainingMap = require("./map.js");

const ROOT = path.join(__dirname, "../..");
const SCRIPT = path.join(ROOT, "src/Dare.Arenas/tutorial/script.json");
const ARENA = path.join(ROOT, "deploy/arenas/#training");
const PNG_OUT = path.join(ROOT, "site/tutorial-mentor.png");

const SC = Lvz.MODES.indexOf("ServerControlled"), TOPMOST = Lvz.LAYERS.indexOf("TopMost"), AFTER_TILES = Lvz.LAYERS.indexOf("AfterTiles");
const FILE_TIME = 1795737600; // fixed, so a rebuild of the same script gives the same .lvz
const TEXT_CHARS = 46, TEXT_W = TEXT_CHARS * Font.W, TEXT_H = 2 * Font.H;
const PAD = 8, P = Portrait.SIZE;
const BOX_W = PAD + P + PAD + TEXT_W + PAD, BOX_H = PAD + P + PAD;
const POP_W = TEXT_W + 24, POP_H = TEXT_H + 24;
// Continuum draws pure black as see-through, so "black" covers are a near-black that survives 16-bit colour
const COL = {
  cover: [16, 16, 18], text: [234, 238, 242], name: [250, 196, 64],
  box: [20, 26, 42], boxEdge: [112, 132, 172], boxHi: [52, 64, 96], popEdge: [250, 196, 64], beacon: [80, 232, 255],
};
const REF = { topLeft: 0, centre: 1, statBox: 3, specialsTop: 4, belowEnergy: 6, chat: 7, radar: 8, weaponsTop: 10 };
const ID = { cover: 4000, upperChat: 4005, box: 4100, portrait: [4101, 4102], skip: 4110, tryIt: 4111, beacon: 4200, line: 5000 };
const POPUP_OF = { skip_popup: ID.skip, try_tutorial: ID.tryIt }; // prompts shown in a popup; the rest in the mentor box

// Where things go. x/y are offsets from the reference point; w/h the image size.
const COVERS = [
  // id, what, xRef, x, yRef, y, w, h
  [4000, "radar", REF.radar, -8, REF.radar, -24, 200, 220], // the radar and its text above it
  [4001, "energy", REF.belowEnergy, -170, REF.belowEnergy, -38, 340, 44],
  [4002, "statbox", REF.topLeft, 0, REF.statBox, -30, 330, 44],
  [4003, "items", REF.topLeft, 0, REF.specialsTop, -8, 44, 256], // specials, left
  [4003, "weapons", REF.weaponsTop, -8, REF.weaponsTop, -8, 44, 228], // weapons and toggles, right
  [4004, "chat", REF.radar, -1908, REF.chat, 0, 1900, 64], // the chat lines, up to the radar
  [ID.upperChat, "upperchat", REF.radar, -1908, REF.chat, -48, 1900, 48], // four more lines' room above them
];
const BOX = { x: 8, y: -(BOX_H + 52) }; // above the chat lines and the upper chat band, from the chat anchor
const PORTRAIT_AT = { x: BOX.x + PAD, y: BOX.y + PAD };
const LINE_AT = { x: BOX.x + PAD + P + PAD, y: BOX.y + PAD + Font.H + 8 };
const POPUP = { x: -POP_W / 2, y: -170 }; // centred above the ship
const POPUP_LINE_AT = { x: POPUP.x + 12, y: POPUP.y + 12 };

// ---- text ------------------------------------------------------------------------------------------
/** Like TutorialScript.ForContinuum: {action} -> the Continuum key; unknown placeholders stay. */
const forContinuum = (text, keys) => text.replace(/\{(\w+)\}/g, (m, k) => (Object.hasOwn(keys, k) ? keys[k] : m));
/** A line as the rows of its bubble. */
const layoutLine = (text, keys) => Font.wrap(forContinuum(text, keys), TEXT_CHARS);

/** The script's lines in TutorialScript.AllLines() order: step lines, then prompts; index from 0. */
function allLines(script) {
  const out = script.steps.flatMap((s) => s.lines.map((text) => ({ text })));
  for (const p of script.prompts || []) out.push({ text: p.text, id: p.id });
  return out.map((l, index) => ({ ...l, index }));
}

// ---- pictures ---------------------------------------------------------------------------------------
function raster(width, height, fill = null) {
  const rgb = new Uint8Array(width * height * 3); // 0,0,0 = see-through in Continuum
  const r = {
    width, height, rgb,
    set(x, y, c) { if (x >= 0 && y >= 0 && x < width && y < height) rgb.set(c, (y * width + x) * 3); },
    rect(x0, y0, w, h, c) { for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) r.set(x, y, c); },
    text(s, x, y, c) { Font.draw(s, x, y, (px, py) => r.set(px, py, c)); },
    blit(src, x0, y0) { for (let y = 0; y < src.height; y++) for (let x = 0; x < src.width; x++) r.set(x0 + x, y0 + y, src.rgb.subarray((y * src.width + x) * 3, (y * src.width + x) * 3 + 3)); },
  };
  if (fill) r.rect(0, 0, width, height, fill);
  return r;
}

/** An 8-bit BMP (bottom-up, uncompressed) of a raster with at most 256 colours. */
function bmp(r) {
  const palette = [], index = new Map(), px = new Uint8Array(r.width * r.height);
  for (let i = 0; i < px.length; i++) {
    const key = (r.rgb[i * 3] << 16) | (r.rgb[i * 3 + 1] << 8) | r.rgb[i * 3 + 2];
    let k = index.get(key);
    if (k === undefined) {
      if (palette.length === 256) throw new Error("more than 256 colours in one image");
      k = palette.length; index.set(key, k); palette.push(key);
    }
    px[i] = k;
  }
  const stride = (r.width + 3) & ~3, off = 54 + 256 * 4, size = off + stride * r.height;
  const b = Buffer.alloc(size);
  b.write("BM", 0, "latin1"); b.writeUInt32LE(size, 2); b.writeUInt32LE(off, 10);
  b.writeUInt32LE(40, 14); b.writeInt32LE(r.width, 18); b.writeInt32LE(r.height, 22);
  b.writeUInt16LE(1, 26); b.writeUInt16LE(8, 28); b.writeUInt32LE(0, 30); b.writeUInt32LE(stride * r.height, 34);
  b.writeInt32LE(2835, 38); b.writeInt32LE(2835, 42); b.writeUInt32LE(256, 46); b.writeUInt32LE(0, 50);
  palette.forEach((c, i) => { b[54 + i * 4] = c & 255; b[55 + i * 4] = (c >> 8) & 255; b[56 + i * 4] = c >> 16; });
  for (let y = 0; y < r.height; y++) b.set(px.subarray(y * r.width, (y + 1) * r.width), off + (r.height - 1 - y) * stride);
  return new Uint8Array(b);
}

/** A truecolour PNG of a raster. */
function png(r) {
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type, "latin1"), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(td));
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(r.width, 0); ihdr.writeUInt32BE(r.height, 4); ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
  const raw = Buffer.alloc((r.width * 3 + 1) * r.height);
  for (let y = 0; y < r.height; y++) raw.set(r.rgb.subarray(y * r.width * 3, (y + 1) * r.width * 3), y * (r.width * 3 + 1) + 1);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}

function frame(w, h, fill, edge, hi) {
  const r = raster(w, h, fill);
  r.rect(0, 0, w, 2, edge); r.rect(0, h - 2, w, 2, edge); r.rect(0, 0, 2, h, edge); r.rect(w - 2, 0, 2, h, edge);
  r.rect(2, 2, w - 4, 1, hi);
  return r;
}

function lineImage(text, keys) {
  const r = raster(TEXT_W, TEXT_H);
  const rows = layoutLine(text, keys);
  if (rows.length > 2) throw new Error(`line too long for the bubble (two rows of ${TEXT_CHARS}): ${text}`);
  rows.forEach((row, i) => r.text(row, 0, i * Font.H, COL.text));
  return r;
}

function beaconSheet() { // two frames: a ring, then a wider fainter ring
  const S = 48, r = raster(S * 2, S);
  for (let f = 0; f < 2; f++) for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) {
    const d = Math.hypot(x + 0.5 - S / 2, y + 0.5 - S / 2), rad = f ? 21 : 17;
    if (Math.abs(d - rad) < 1.6) r.set(f * S + x, y, f ? [40, 150, 190] : COL.beacon);
    else if (d < 3) r.set(f * S + x, y, COL.beacon);
  }
  return r;
}

/** The two portrait frames side by side (128x64), for the browser. */
function portraitSheet() {
  const [a, b] = Portrait.frames(), r = raster(P * 2, P);
  r.blit(a, 0, 0); r.blit(b, P, 0);
  return r;
}
const portraitPng = () => png(portraitSheet());

// ---- the .lvz --------------------------------------------------------------------------------------------
async function buildLvz(script) {
  const lvz = Lvz.blank();
  const keys = script.continuumKeys || {};
  const addImage = (name, r, cols = 1, time = 100) => {
    const f = Lvz.putFile(lvz, name, bmp(r));
    f.time = FILE_TIME;
    lvz.images.push({ file: name, cols, rows: 1, time });
    return lvz.images.length - 1;
  };
  const screen = (id, image, xRef, x, yRef, y) => lvz.objects.push(Lvz.newObject({ map: false, id, image, xRef, x, yRef, y, layer: TOPMOST, mode: SC, time: 0 }));

  // covers first: later objects in the same layer draw over them
  for (const [id, what, xRef, x, yRef, y, w, h] of COVERS) screen(id, addImage(`cover_${what}.bmp`, raster(w, h, COL.cover)), xRef, x, yRef, y);

  const box = frame(BOX_W, BOX_H, COL.box, COL.boxEdge, COL.boxHi);
  box.text(script.mentor, LINE_AT.x - BOX.x, PAD + 2, COL.name);
  screen(ID.box, addImage("mentor_box.bmp", box), REF.chat, BOX.x, REF.chat, BOX.y);
  Portrait.frames().forEach((p, i) => screen(ID.portrait[i], addImage(`mentor_${i ? "open" : "closed"}.bmp`, p), REF.chat, PORTRAIT_AT.x, REF.chat, PORTRAIT_AT.y));

  const popup = frame(POP_W, POP_H, COL.box, COL.popEdge, COL.boxHi);
  const popImage = addImage("popup.bmp", popup);
  for (const id of [ID.skip, ID.tryIt]) screen(id, popImage, REF.centre, POPUP.x, REF.centre, POPUP.y);

  for (const l of allLines(script)) {
    const image = addImage(`line_${String(l.index).padStart(3, "0")}.bmp`, lineImage(l.text, keys));
    const at = POPUP_OF[l.id] ? POPUP_LINE_AT : LINE_AT, ref = POPUP_OF[l.id] ? REF.centre : REF.chat;
    screen(ID.line + l.index, image, ref, at.x, ref, at.y);
  }

  const b = TrainingMap.SPOTS.beacon;
  lvz.objects.push(Lvz.newObject({ map: true, id: ID.beacon, image: addImage("beacon.bmp", beaconSheet(), 2, 80),
    x: b.x * 16 + 8 - 24, y: b.y * 16 + 8 - 24, layer: AFTER_TILES, mode: SC, time: 0 }));

  if (lvz.images.length > 256) throw new Error("more than 256 images");
  return lvz;
}

async function main() {
  const script = JSON.parse(fs.readFileSync(SCRIPT, "utf8"));
  fs.mkdirSync(ARENA, { recursive: true });
  const lvz = await buildLvz(script);
  fs.writeFileSync(path.join(ARENA, "tutorial.lvz"), await Lvz.write(lvz));
  fs.writeFileSync(path.join(ARENA, "training.lvl"), TrainingMap.lvl());
  fs.writeFileSync(PNG_OUT, portraitPng());
  console.log(`tutorial.lvz: ${lvz.objects.length} objects, ${lvz.images.length} images; training.lvl; site/tutorial-mentor.png`);
}

if (require.main === module) main().catch((e) => { console.error(e.message); process.exit(1); });

module.exports = { TEXT_W, TEXT_CHARS, ID, forContinuum, layoutLine, allLines, buildLvz, bmp, png, portraitSheet, portraitPng };
