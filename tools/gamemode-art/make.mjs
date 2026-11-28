// Stock LVZ images for the game creator: timer digits, countdowns, banners and team icons.
// Run: node tools/gamemode-art/make.mjs   (needs ImageMagick 7 `magick` and macOS's system fonts)
// Writes src/Dare.Arenas/Web/wwwroot/gamemode-art/*.bmp and the index src/Dare.Arenas/Web/wwwroot/gamemode-art.js.
// Images are drawn smooth (gradients, outlines, soft glows), not pixel art, on pure black: LVZ keys pure black out
// as transparent (Continuum and our client), so nothing inside an image is pure black. Saved as 8-bit uncompressed
// BMP, which every LVZ reader takes. Spec: the "Stock images" section of the LVZ screen design (side-session notes).
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const OUT = join(ROOT, "src/Dare.Arenas/Web/wwwroot/gamemode-art");
const INDEX = join(ROOT, "src/Dare.Arenas/Web/wwwroot/gamemode-art.js");
const FONT = "/System/Library/Fonts/Supplemental/DIN Condensed Bold.ttf";
const TMP = mkdtempSync(join(tmpdir(), "gmart-"));
const magick = (...args) => execFileSync("magick", args, { stdio: ["ignore", "ignore", "inherit"] });
const OUTLINE = "#13264a"; // dark navy: reads as a rim on space, never keyed out like black

/**
 * Text with a vertical gradient, a dark rim and an optional soft glow, centred on a w x h black frame (PNG in TMP).
 * Drawn on a canvas twice the size, trimmed, shrunk only if it would not fit, then centred: nothing is clipped.
 */
function text(name, fw, fh, str, { size, top, bottom, glow = null, rim = 2 }) {
  const t = (s) => join(TMP, `${name}-${s}.png`);
  const w = fw * 2, h = fh * 2;
  magick("-size", `${w}x${h}`, "xc:black", "-font", FONT, "-pointsize", String(size), "-gravity", "center",
    "-fill", "white", "-annotate", "+0+0", str, "-alpha", "off", t("mask"));
  magick(t("mask"), "-morphology", "Dilate", `Disk:${rim}`, t("rim"));
  magick("-size", `${w}x${h}`, `gradient:${top}-${bottom}`, t("grad"));
  const layers = ["-size", `${w}x${h}`, "xc:black"];
  if (glow) layers.push("(", "-size", `${w}x${h}`, `xc:${glow}`, "(", t("rim"), "-blur", "0x4", "-evaluate", "multiply", "0.55", ")",
    "-alpha", "off", "-compose", "CopyOpacity", "-composite", ")", "-compose", "over", "-composite");
  layers.push("(", "-size", `${w}x${h}`, `xc:${OUTLINE}`, t("rim"), "-alpha", "off", "-compose", "CopyOpacity", "-composite", ")",
    "-compose", "over", "-composite",
    "(", t("grad"), t("mask"), "-alpha", "off", "-compose", "CopyOpacity", "-composite", ")", "-compose", "over", "-composite",
    "-background", "black", "-alpha", "remove",
    "-trim", "+repage", "-resize", `${fw - 4}x${fh - 4}>`, "-gravity", "center", "-extent", `${fw}x${fh}`, t("out"));
  magick(...layers);
  return t("out");
}

/** Frames side by side (LVZ animation: x frames by 1). */
const strip = (name, pngs) => { const f = join(TMP, `${name}-strip.png`); magick(...pngs, "+append", f); return f; };

/** PNG -> 8-bit uncompressed BMP in OUT, with pure black kept as the transparent colour. */
function save(png, file) {
  magick(png, "-background", "black", "-alpha", "remove", "-dither", "None", "-colors", "255",
    "-fuzz", "2%", "-fill", "black", "-opaque", "black", "-type", "Palette", // reduction can merge black into near-black
    "-compress", "None", `BMP3:${join(OUT, file)}`);
}

const items = [];
const add = (file, w, h, extra) => items.push({ file, w, h, ...extra });

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

// ---- 1. digits: 0-9 and a colon, small and large -------------------------------------------------
const GLYPHS = [..."0123456789"].map((c) => [c, c]).concat([["colon", ":"]]);
const DIGIT_SIZES = { s: { w: 16, h: 24, size: 34, rim: 1.4 }, l: { w: 32, h: 48, size: 68, rim: 2.2 } };
for (const [key, d] of Object.entries(DIGIT_SIZES)) {
  for (const [id, ch] of GLYPHS) {
    const file = `digit-${key}-${id}.bmp`;
    save(text(`d${key}${id}`, d.w, d.h, ch, { size: d.size, top: "#ffffff", bottom: "#7cc4ff", rim: d.rim }), file);
    add(file, d.w, d.h, { kind: "digit", label: `${id === "colon" ? "Colon" : id} (${key === "s" ? "small" : "large"})`, glyph: ch, font: key, use: "screen" });
  }
}

// ---- 2. countdowns: one frame a second, so one object shown for the whole time plays it -------------
const cd = (name, labels, w, h, size, colour) => {
  const frames = labels.map((l, i) => text(`${name}${i}`, w, h, l, { size, top: "#ffffff", bottom: colour(l), glow: "#2f7fd8", rim: 2.5 }));
  const file = `${name}.bmp`;
  save(strip(name, frames), file);
  return { file, w: w * labels.length, h, frames: labels.length, frameW: w, frameMs: 1000 };
};
{
  const ten = cd("countdown-10", ["10", "9", "8", "7", "6", "5", "4", "3", "2", "1", "0"], 64, 64, 84, () => "#7cc4ff");
  items.push({ ...ten, kind: "countdown", label: "Countdown 10 to 0", use: "screen" });
  const five = cd("countdown-5", ["5", "4", "3", "2", "1", "0"], 64, 64, 84, () => "#7cc4ff");
  items.push({ ...five, kind: "countdown", label: "Countdown 5 to 0", use: "screen" });
  const go = cd("countdown-go", ["3", "2", "1", "GO!"], 128, 72, 96, (l) => (l === "GO!" ? "#5cff9d" : "#7cc4ff"));
  items.push({ ...go, kind: "countdown", label: "3, 2, 1, GO!", use: "screen" });
}

// ---- 3. banners -----------------------------------------------------------------------------------
const BANNERS = [
  ["go", "GO!", "#d8ffe6", "#2fe07a", "#1f9d55"],
  ["victory", "VICTORY", "#fff6c8", "#ffb000", "#c77d00"],
  ["defeat", "DEFEAT", "#ffe0e0", "#ff3d3d", "#a81e1e"],
  ["overtime", "OVERTIME", "#ffe9cc", "#ff8a1f", "#b35400"],
  ["flag-taken", "FLAG TAKEN", "#e2fbff", "#33d6ff", "#167a99"],
  ["goal", "GOAL!", "#ffffff", "#9fd2ff", "#2f7fd8"],
];
for (const [id, words, top, bottom, glow] of BANNERS) {
  const file = `banner-${id}.bmp`;
  save(text(`b${id}`, 256, 56, words, { size: 78, top, bottom, glow, rim: 2.6 }), file);
  add(file, 256, 56, { kind: "banner", label: words, use: "screen" });
}

// ---- 4. team icons: a flag and a shield per team colour -------------------------------------------
const TEAMS = [["red", "#ff5a5a", "#a31d1d"], ["blue", "#5aa8ff", "#1d4fa3"], ["yellow", "#ffd84a", "#a37e0f"], ["green", "#58dc86", "#1d8a45"]];
for (const [i, [team, light, dark]] of TEAMS.entries()) {
  const flag = join(TMP, `flag-${team}.png`), shield = join(TMP, `shield-${team}.png`);
  magick("-size", "24x24", "xc:black", "-stroke", OUTLINE, "-strokewidth", "1.5",
    "-fill", "#c9d3e3", "-draw", "roundrectangle 4,2 6.5,22 1,1",
    "-fill", light, "-draw", "path 'M 6.5,3 C 11,1 14,5 20,3 L 20,13 C 14,15 11,11 6.5,13 Z'",
    "-stroke", "none", "-fill", dark, "-draw", "path 'M 7.5,10 C 11,8.5 14,12.5 19,11 L 19,12.4 C 14,14 11,10 7.5,11.8 Z'", flag);
  magick("-size", "24x24", "xc:black", "-stroke", OUTLINE, "-strokewidth", "1.5",
    "-fill", light, "-draw", "path 'M 12,2 L 20.5,5 L 20.5,11 C 20.5,16.5 16.5,20 12,22 C 7.5,20 3.5,16.5 3.5,11 L 3.5,5 Z'",
    "-stroke", "none", "-fill", dark, "-draw", "path 'M 12,4 L 12,20 C 15.5,18.2 18.6,15.4 18.6,11 L 18.6,6.3 Z'", shield);
  save(flag, `flag-${team}.bmp`);
  add(`flag-${team}.bmp`, 24, 24, { kind: "icon", label: `${team[0].toUpperCase()}${team.slice(1)} flag`, team: i, use: "both" });
  save(shield, `shield-${team}.bmp`);
  add(`shield-${team}.bmp`, 24, 24, { kind: "icon", label: `${team[0].toUpperCase()}${team.slice(1)} shield`, team: i, use: "both" });
}

rmSync(TMP, { recursive: true, force: true });
const made = new Set(readdirSync(OUT));
for (const it of items) if (!made.has(it.file)) throw new Error(`missing ${it.file}`);

writeFileSync(INDEX, `// Stock LVZ images for the game creator, made by tools/gamemode-art/make.mjs (do not edit by hand).
// Files are served at /arena/gamemode-art/<file>: 8-bit BMP on pure black (LVZ draws black as transparent).
// Each: file, w, h (the whole image), kind (digit, countdown, banner, icon), label, use (screen, map or both);
// animations add frames (side by side), frameW and frameMs (one frame a second); digits add glyph and font (s/l);
// icons add team (0 red, 1 blue, 2 yellow, 3 green).
"use strict";
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GameModeArt = api;
})(typeof self !== "undefined" ? self : this, () => {
  const BASE = "/arena/gamemode-art/";
  const ITEMS = ${JSON.stringify(items, null, 2).replace(/\n/g, "\n  ")};
  /** The image's URL. */
  const url = (item) => BASE + item.file;
  /** The item's LVZ image definition: frames side by side, the strip's animation time in 1/100 s (0 for a still). */
  const lvzImage = (item) => ({ file: item.file, cols: item.frames || 1, rows: 1, time: item.frames ? Math.round(item.frames * item.frameMs / 10) : 0 });
  return { BASE, ITEMS, url, lvzImage };
});
`);
console.log(`${items.length} images in ${OUT}`);
