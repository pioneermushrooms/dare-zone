// Stock LVZ images for the game creator, made by tools/gamemode-art/make.mjs (do not edit by hand).
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
  const ITEMS = [
    {
      "file": "digit-s-0.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "0 (small)",
      "glyph": "0",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-1.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "1 (small)",
      "glyph": "1",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-2.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "2 (small)",
      "glyph": "2",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-3.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "3 (small)",
      "glyph": "3",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-4.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "4 (small)",
      "glyph": "4",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-5.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "5 (small)",
      "glyph": "5",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-6.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "6 (small)",
      "glyph": "6",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-7.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "7 (small)",
      "glyph": "7",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-8.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "8 (small)",
      "glyph": "8",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-9.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "9 (small)",
      "glyph": "9",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-s-colon.bmp",
      "w": 16,
      "h": 24,
      "kind": "digit",
      "label": "Colon (small)",
      "glyph": ":",
      "font": "s",
      "use": "screen"
    },
    {
      "file": "digit-l-0.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "0 (large)",
      "glyph": "0",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-1.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "1 (large)",
      "glyph": "1",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-2.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "2 (large)",
      "glyph": "2",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-3.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "3 (large)",
      "glyph": "3",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-4.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "4 (large)",
      "glyph": "4",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-5.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "5 (large)",
      "glyph": "5",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-6.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "6 (large)",
      "glyph": "6",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-7.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "7 (large)",
      "glyph": "7",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-8.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "8 (large)",
      "glyph": "8",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-9.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "9 (large)",
      "glyph": "9",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "digit-l-colon.bmp",
      "w": 32,
      "h": 48,
      "kind": "digit",
      "label": "Colon (large)",
      "glyph": ":",
      "font": "l",
      "use": "screen"
    },
    {
      "file": "countdown-10.bmp",
      "w": 704,
      "h": 64,
      "frames": 11,
      "frameW": 64,
      "frameMs": 1000,
      "kind": "countdown",
      "label": "Countdown 10 to 0",
      "use": "screen"
    },
    {
      "file": "countdown-5.bmp",
      "w": 384,
      "h": 64,
      "frames": 6,
      "frameW": 64,
      "frameMs": 1000,
      "kind": "countdown",
      "label": "Countdown 5 to 0",
      "use": "screen"
    },
    {
      "file": "countdown-go.bmp",
      "w": 512,
      "h": 72,
      "frames": 4,
      "frameW": 128,
      "frameMs": 1000,
      "kind": "countdown",
      "label": "3, 2, 1, GO!",
      "use": "screen"
    },
    {
      "file": "banner-go.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "GO!",
      "use": "screen"
    },
    {
      "file": "banner-victory.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "VICTORY",
      "use": "screen"
    },
    {
      "file": "banner-defeat.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "DEFEAT",
      "use": "screen"
    },
    {
      "file": "banner-overtime.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "OVERTIME",
      "use": "screen"
    },
    {
      "file": "banner-flag-taken.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "FLAG TAKEN",
      "use": "screen"
    },
    {
      "file": "banner-goal.bmp",
      "w": 256,
      "h": 56,
      "kind": "banner",
      "label": "GOAL!",
      "use": "screen"
    },
    {
      "file": "flag-red.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Red flag",
      "team": 0,
      "use": "both"
    },
    {
      "file": "shield-red.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Red shield",
      "team": 0,
      "use": "both"
    },
    {
      "file": "flag-blue.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Blue flag",
      "team": 1,
      "use": "both"
    },
    {
      "file": "shield-blue.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Blue shield",
      "team": 1,
      "use": "both"
    },
    {
      "file": "flag-yellow.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Yellow flag",
      "team": 2,
      "use": "both"
    },
    {
      "file": "shield-yellow.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Yellow shield",
      "team": 2,
      "use": "both"
    },
    {
      "file": "flag-green.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Green flag",
      "team": 3,
      "use": "both"
    },
    {
      "file": "shield-green.bmp",
      "w": 24,
      "h": 24,
      "kind": "icon",
      "label": "Green shield",
      "team": 3,
      "use": "both"
    }
  ];
  /** The image's URL. */
  const url = (item) => BASE + item.file;
  /** The item's LVZ image definition: frames side by side, the strip's animation time in 1/100 s (0 for a still). */
  const lvzImage = (item) => ({ file: item.file, cols: item.frames || 1, rows: 1, time: item.frames ? Math.round(item.frames * item.frameMs / 10) : 0 });
  return { BASE, ITEMS, url, lvzImage };
});
