#!/usr/bin/env node
// Builds the training arena's generated files from the tutorial script:
//   deploy/arenas/#training/tutorial.lvz  the covers, mentor box, portrait, popups, beacon and one
//                                         image per script line (Continuum keys filled in)
//   deploy/arenas/#training/training.lvl  the map (tools/tutorial-lvz/map.js)
// Run after editing src/Dare.Arenas/tutorial/script.json or site/tutorial-vex.svg:
//   node tools/tutorial-lvz/build.js
//
// The look is the browser's mentor box (site/tutorial.css, Chris's approved retro CRT mockup): an amber
// panel on #120a00 with faint scanlines, a 3px amber border and a hard 4px shadow, Vex's portrait
// (site/tutorial-vex.svg, rasterised by portrait.js), the name and call sign, the step "n/20" and the line
// in pale amber with the keys on amber keycaps. The 8x12 bitmap font (font.js) stands in for VT323 and
// Press Start 2P (the name is drawn at twice the size).
//
// Object ids (the zone toggles them per player; all are ServerControlled, so they start off):
//   4000 + HudPart  black covers: Radar 4000, Energy 4001 (bar and number), StatBox 4002, Items 4003
//                   (left and right columns), Chat 4004; 4005 the band just above the chat lines. TopMost.
//   4100 mentor box, 4101/4102 portrait (eye glow bright/dim), 4110 skip popup, 4111 "Try the
//   tutorial" popup, 4200 the beacon (a map object), 5000 + TutorialLine.Index each line.
// Screen positions use the CLV2 reference points, which Continuum and the Dare client share. The covers are
// sized from where the Dare client draws each part (crates/oss-client/src/game/hud.rs, whose anchors were
// measured against Continuum), and tests/js/tutorial-lvz.test.js checks them against those numbers. The
// mentor box is pinned to the chat's top-left (7) with a negative y, so it sits bottom-left above the chat,
// right of the items column.
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const Lvz = require("../../src/Dare.Arenas/Web/wwwroot/lvz.js");
const Font = require("./font.js");
const Portrait = require("./portrait.js");
const TrainingMap = require("./map.js");

const ROOT = path.join(__dirname, "../..");
const SCRIPT = path.join(ROOT, "src/Dare.Arenas/tutorial/script.json");
const ARENA = path.join(ROOT, "deploy/arenas/#training");

const SC = Lvz.MODES.indexOf("ServerControlled"), TOPMOST = Lvz.LAYERS.indexOf("TopMost"), AFTER_TILES = Lvz.LAYERS.indexOf("AfterTiles");
const FILE_TIME = 1795737600; // fixed, so a rebuild of the same script gives the same .lvz
const TEXT_CHARS = 46, TEXT_W = TEXT_CHARS * Font.W, TEXT_H = 2 * Font.H;
/** The call sign beside the name, and the box's buttons: UI, like the browser's (site/tutorial.js), not
 *  dialogue. Continuum can't click, so its buttons are the commands they send. */
const CALL_SIGN = "ACE-IN-WAITING";
const BUTTONS = [["?SKIP", true], ["?LEAVE", false]], BUTTON_H = 20;

// The panel, in the mockup's numbers: border 3, padding 12, a 14 gap after the portrait, a 4px hard shadow.
const EDGE = 3, PAD = 12, GAP = 14, SHADOW = 4;
const FACE = Portrait.SIZE, WELL = 2, FACE_BOX = WELL + FACE + WELL; // the portrait in a 2px #5a3a00 well
const TEXT_X = EDGE + PAD + FACE_BOX + GAP; // the text column, from the box's left
const BOX_W = TEXT_X + TEXT_W + PAD + EDGE, BOX_H = EDGE + PAD + FACE_BOX + PAD + EDGE;
const INK_TOP = 2, INK_BOTTOM = 9; // a glyph's ink rows in its 8x12 cell, descenders aside (font.js)
const NAME_SCALE = 2, NAME_Y = EDGE + PAD - INK_TOP * NAME_SCALE; // the name's ink starts at the padding
const HEAD_Y = NAME_Y + INK_BOTTOM * NAME_SCALE - INK_BOTTOM; // call sign and step: their ink ends where the name's does
const LINE_TEXT_Y = 14; // in a box line's image, below the step number
const LINE_H = LINE_TEXT_Y + TEXT_H; // a box line: the step number, then two rows
const POP_W = EDGE + PAD + TEXT_W + PAD + EDGE, POP_H = EDGE + PAD + TEXT_H + PAD + EDGE;
// Continuum draws pure black as see-through, so "black" covers are the darkest colour that isn't black once
// Continuum's 16-bit (RGB565) colour has had it: the smallest green step, which looks black
const COL = {
  cover: [0, 4, 0],
  panel: [0x12, 0x0a, 0x00], scan: [32, 20, 0], // #120a00, and rgba(255,176,0,.06) over it
  edge: [0xff, 0xb0, 0x00], shadow: [0x5a, 0x3a, 0x00], well: [0x1a, 0x0f, 0x00],
  name: [0xff, 0xb0, 0x00], dim: [0xc9, 0x8a, 0x00], text: [0xff, 0xd3, 0x6b],
  keycap: [0xff, 0xb0, 0x00], keycapText: [0x1a, 0x0f, 0x00], beacon: [80, 232, 255],
};
const REF = { topLeft: 0, centre: 1, bottomRight: 2, statBox: 3, specialsTop: 4, belowEnergy: 6, chat: 7, radar: 8, weaponsTop: 10 };
const ID = { cover: 4000, upperChat: 4005, box: 4100, portrait: [4101, 4102], skip: 4110, tryIt: 4111, beacon: 4200, line: 5000 };
const POPUP_OF = { skip_popup: ID.skip, try_tutorial: ID.tryIt }; // prompts shown in a popup; the rest in the mentor box

// Where things go. x/y are offsets from the reference point; w/h the image size. Each cover's source is in
// crates/oss-client/src/game/hud.rs of the Dare client (tests/js/tutorial-lvz.test.js has the numbers):
const UPPER_CHAT_Y = -48;
const COVERS = [
  // id, what, xRef, x, yRef, y, w, h
  // the radar (screen_refs 8, radar_dim, its 1px border) and the clock line 16px above it (draw_radar);
  // it hangs 6px from the bottom-right corner, so the cover runs off screen there at any size up to 2560 wide
  [4000, "radar", REF.radar, -1, REF.radar, -16, 460, 460],
  // the energy bar, 480 across centred, under its 116x28 frame (draw_energy: BAR_HALF, hlthbar)
  [4001, "energy", REF.belowEnergy, -242, REF.topLeft, 0, 484, 30],
  // the energy number: 16x24 digits right-aligned at the top right (draw_energy, engyfont); four digits
  [4001, "energynumber", REF.bottomRight, -70, REF.topLeft, 0, 70, 26],
  // the player list: reference 3 is its bottom-right corner in any view, so the cover ends there and runs
  // off the top-left (sandbox draw_statbox, statbox.rs); with the list closed, 3 is the origin and nothing shows
  [4002, "statbox", REF.statBox, -480, REF.statBox, -480, 482, 482],
  // the specials: 7 icons 26 across at a 25px pitch from reference 4, the count tab out to 34 (Continuum's
  // to 37) (draw_items)
  [4003, "items", REF.topLeft, 0, REF.specialsTop, -2, 40, 180],
  // the weapons and toggles: 6 icons from reference 10 at the right edge (draw_items)
  [4003, "weapons", REF.weaponsTop, -4, REF.weaponsTop, -2, 34, 154],
  // the chat lines: from reference 7 down past the bottom, from the left edge up to the radar's border
  // (sandbox draw_log, chat_width: the log stops short of the radar); -2048 is the farthest an offset goes
  [4004, "chat", REF.radar, -2048, REF.chat, 0, 2047, 300],
  [ID.upperChat, "upperchat", REF.radar, -2048, REF.chat, UPPER_CHAT_Y, 2047, -UPPER_CHAT_Y], // four more lines' room above them
];
const BOX = { x: 44, y: UPPER_CHAT_Y - 4 - (BOX_H + SHADOW) }; // above the upper chat band, right of the items column
const PORTRAIT_AT = { x: BOX.x + EDGE + PAD + WELL, y: BOX.y + EDGE + PAD + WELL };
const LINE_AT = { x: BOX.x + TEXT_X, y: BOX.y + HEAD_Y };
const POPUP = { x: -POP_W / 2, y: -170 }; // centred above the ship
const POPUP_LINE_AT = { x: POPUP.x + EDGE + PAD, y: POPUP.y + EDGE + PAD };

// ---- text ------------------------------------------------------------------------------------------
/** Like TutorialScript.ForContinuum: {action} -> the Continuum key; unknown placeholders stay. */
const forContinuum = (text, keys) => text.replace(/\{(\w+)\}/g, (m, k) => (Object.hasOwn(keys, k) ? keys[k] : m));
// A filled-in key's characters go through the wrap shifted into the private-use area, so the rows still
// know which characters sit on a keycap.
const KEY = 0xe000;
const markKey = (s) => [...s].map((c) => String.fromCharCode(KEY + c.charCodeAt(0))).join("");
const isKey = (c) => c.charCodeAt(0) >= KEY;
const plain = (c) => (isKey(c) ? String.fromCharCode(c.charCodeAt(0) - KEY) : c);
/** A line as the rows of its bubble, its keys' characters marked (see markKey). */
const keyedRows = (text, keys) => Font.wrap(text.replace(/\{(\w+)\}/g, (m, k) => (Object.hasOwn(keys, k) ? markKey(keys[k]) : m)), TEXT_CHARS);
/** A line as the rows of its bubble. */
const layoutLine = (text, keys) => keyedRows(text, keys).map((r) => [...r].map(plain).join(""));
/** Where a line's keycaps go: [{ row, from, to }], in characters. */
function keySpans(text, keys) {
  const out = [];
  keyedRows(text, keys).forEach((r, row) => {
    const cs = [...r];
    for (let i = 0; i < cs.length; i++) {
      if (!isKey(cs[i]) || (i > 0 && isKey(cs[i - 1]))) continue;
      let j = i;
      while (j < cs.length && isKey(cs[j])) j++;
      out.push({ row, from: i, to: j });
    }
  });
  return out;
}

/** The script's lines in TutorialScript.AllLines() order: step lines (with the step and step count the
 *  zone says them with), then prompts; index from 0. */
function allLines(script) {
  const of = script.steps.length;
  const out = script.steps.flatMap((s, i) => s.lines.map((text) => ({ text, step: i + 1, of })));
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
    text(s, x, y, c, scale = 1) { Font.draw(s, x, y, (px, py) => r.set(px, py, c), scale); },
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

/** The panel: amber border, #120a00 with a faint amber scanline every third row, and the hard shadow
 *  down-right (the image is SHADOW bigger each way; its two empty corners stay see-through). */
function panel(w, h) {
  const r = raster(w + SHADOW, h + SHADOW);
  r.rect(SHADOW, SHADOW, w, h, COL.shadow);
  r.rect(0, 0, w, h, COL.edge);
  for (let y = EDGE; y < h - EDGE; y++) r.rect(EDGE, y, w - 2 * EDGE, 1, (y - EDGE) % 3 === 0 ? COL.scan : COL.panel);
  return r;
}

/** A line's rows from `y`, its keys on amber keycaps. A keycap reaches a pixel past the key's cells on each
 *  side, which is blank there: glyph ink is columns 1-5 of the 8-wide cell. */
function drawRows(r, text, keys, y) {
  const rows = keyedRows(text, keys);
  if (rows.length > 2) throw new Error(`line too long for the bubble (two rows of ${TEXT_CHARS}): ${text}`);
  for (const { row, from, to } of keySpans(text, keys)) r.rect(from * Font.W - 1, y + row * Font.H + 1, (to - from) * Font.W + 2, Font.H - 2, COL.keycap);
  rows.forEach((row, i) => [...row].forEach((c, k) => r.text(plain(c), k * Font.W, y + i * Font.H, isKey(c) ? COL.keycapText : COL.text)));
}

/** A mentor-box line: "n/of" right-aligned on the name's row (none for a prompt), then the line. */
function boxLineImage(l, keys) {
  const r = raster(TEXT_W, LINE_H);
  if (l.step) { const s = `${l.step}/${l.of}`; r.text(s, TEXT_W - s.length * Font.W, 0, COL.dim); }
  drawRows(r, l.text, keys, LINE_TEXT_Y);
  return r;
}

/** A popup's line: just the rows. */
function popupLineImage(l, keys) {
  const r = raster(TEXT_W, TEXT_H);
  drawRows(r, l.text, keys, 0);
  return r;
}

/** The mentor box: the panel, the portrait's well, the name, the call sign and the buttons. */
function mentorBox(name) {
  const r = panel(BOX_W, BOX_H);
  const at = EDGE + PAD;
  r.rect(at, at, FACE_BOX, FACE_BOX, COL.shadow);
  r.rect(at + WELL, at + WELL, FACE, FACE, COL.well);
  const shown = name.toUpperCase();
  r.text(shown, TEXT_X, NAME_Y, COL.name, NAME_SCALE);
  r.text(CALL_SIGN, TEXT_X + shown.length * Font.W * NAME_SCALE + 10, HEAD_Y, COL.dim);
  // the browser's Skip (filled) and Leave (outlined) buttons, as the commands that do the same here
  let x = TEXT_X;
  const y = at + FACE_BOX - BUTTON_H;
  for (const [label, filled] of BUTTONS) {
    const w = label.length * Font.W + 16;
    r.rect(x, y, w, BUTTON_H, COL.edge);
    if (!filled) for (let row = y + 2; row < y + BUTTON_H - 2; row++) r.rect(x + 2, row, w - 4, 1, (row - EDGE) % 3 === 0 ? COL.scan : COL.panel);
    r.text(label, x + 8, y + (BUTTON_H - Font.H) / 2, filled ? COL.keycapText : COL.name);
    x += w + 8;
  }
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
  const screen = (id, image, xRef, x, yRef, y) => {
    if ([x, y].some((v) => v < -2048 || v > 2047)) throw new Error(`object ${id}: a screen offset is -2048..2047`);
    lvz.objects.push(Lvz.newObject({ map: false, id, image, xRef, x, yRef, y, layer: TOPMOST, mode: SC, time: 0 }));
  };

  // covers first: later objects in the same layer draw over them
  for (const [id, what, xRef, x, yRef, y, w, h] of COVERS) screen(id, addImage(`cover_${what}.bmp`, raster(w, h, COL.cover)), xRef, x, yRef, y);

  screen(ID.box, addImage("mentor_box.bmp", mentorBox(script.mentor)), REF.chat, BOX.x, REF.chat, BOX.y);
  Portrait.frames().forEach((p, i) => screen(ID.portrait[i], addImage(`mentor_${i ? "dim" : "bright"}.bmp`, p), REF.chat, PORTRAIT_AT.x, REF.chat, PORTRAIT_AT.y));

  const popImage = addImage("popup.bmp", panel(POP_W, POP_H));
  for (const id of [ID.skip, ID.tryIt]) screen(id, popImage, REF.centre, POPUP.x, REF.centre, POPUP.y);

  for (const l of allLines(script)) {
    const inPopup = !!POPUP_OF[l.id];
    const image = addImage(`line_${String(l.index).padStart(3, "0")}.bmp`, inPopup ? popupLineImage(l, keys) : boxLineImage(l, keys));
    const at = inPopup ? POPUP_LINE_AT : LINE_AT, ref = inPopup ? REF.centre : REF.chat;
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
  console.log(`tutorial.lvz: ${lvz.objects.length} objects, ${lvz.images.length} images; training.lvl`);
}

if (require.main === module) main().catch((e) => { console.error(e.message); process.exit(1); });

module.exports = { TEXT_W, TEXT_H, TEXT_CHARS, LINE_H, CALL_SIGN, ID, COL, COVERS, forContinuum, layoutLine, keySpans, allLines, buildLvz, bmp };
