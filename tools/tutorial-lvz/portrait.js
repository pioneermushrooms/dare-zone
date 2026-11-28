// The mentor's portrait for Continuum: site/tutorial-vex.svg (the browser's own picture of Vex) rasterised
// at its native 96x96, one pixel per viewBox unit, as two frames: eye glow bright, eye glow dim.
// Polygons are filled by pixel centre (even-odd, which is the same as nonzero for these simple shapes),
// polylines and lines are 1-px Bresenham lines, and each "vex-glow" shape (blurred in the browser) gets a
// 1-px red halo instead. CRT scanlines darken every third row, as in the box. No pure black: Continuum
// shows black in LVZ images as see-through.
"use strict";

const fs = require("node:fs");
const path = require("node:path");

const SIZE = 96;
const SVG = path.join(__dirname, "../../site/tutorial-vex.svg");
const BG = [0x1a, 0x0f, 0x00]; // the portrait's well, as in the browser's box
const SCANLINE = 0.72; // rgba(0,0,0,.28) every third row
/** Eye glow per frame: the shape's opacity and its halo's. The bright frame is the SVG as drawn. */
const GLOW = { bright: { fill: 0.9, halo: 0.55 }, dim: { fill: 0.4, halo: 0.2 } };

const hex = (s) => {
  const m = /^#([0-9a-f]{6})$/i.exec(s || "");
  return m ? [0, 2, 4].map((i) => parseInt(m[1].slice(i, i + 2), 16)) : null;
};
const nums = (s) => String(s).trim().split(/[\s,]+/).map(Number);
const pairs = (s) => { const n = nums(s), out = []; for (let i = 0; i + 1 < n.length; i += 2) out.push([n[i], n[i + 1]]); return out; };

/** The shapes of an SVG written as tutorial-vex.svg is: [{ kind, points, fill, stroke, opacity, glow }]. */
function parseSvg(text) {
  const shapes = [];
  for (const m of text.matchAll(/<(polygon|polyline|line)\b([^>]*)\/?>/g)) {
    const attr = {};
    for (const a of m[2].matchAll(/([\w-]+)="([^"]*)"/g)) attr[a[1]] = a[2];
    const points = m[1] === "line" ? [[+attr.x1, +attr.y1], [+attr.x2, +attr.y2]] : pairs(attr.points);
    shapes.push({
      kind: m[1] === "polygon" ? "fill" : "stroke",
      points, fill: hex(attr.fill), stroke: hex(attr.stroke),
      opacity: attr.opacity === undefined ? 1 : Number(attr.opacity),
      glow: /\bvex-glow\b/.test(attr.class || ""),
    });
  }
  return shapes;
}

function inside(points, x, y) {
  let odd = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i], [xj, yj] = points[j];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) odd = !odd;
  }
  return odd;
}

function line([x0, y0], [x1, y1], plot) {
  x0 = Math.round(x0); y0 = Math.round(y0); x1 = Math.round(x1); y1 = Math.round(y1);
  const dx = Math.abs(x1 - x0), dy = -Math.abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
  let err = dx + dy;
  for (;;) {
    plot(x0, y0);
    if (x0 === x1 && y0 === y1) return;
    const e2 = 2 * err;
    if (e2 >= dy) { err += dy; x0 += sx; }
    if (e2 <= dx) { err += dx; y0 += sy; }
  }
}

/** One frame, { width, height, rgb } (3 bytes per pixel, rows top-down), with the eye glow `glow`. */
function render(shapes, glow) {
  const px = [];
  for (let i = 0; i < SIZE * SIZE; i++) px.push([...BG]);
  const blend = (x, y, c, a) => {
    if (x < 0 || y < 0 || x >= SIZE || y >= SIZE) return;
    const p = px[y * SIZE + x];
    for (let k = 0; k < 3; k++) p[k] = p[k] * (1 - a) + c[k] * a;
  };
  for (const s of shapes) {
    if (s.kind === "stroke") {
      if (!s.stroke) continue;
      const seen = new Set();
      for (let i = 1; i < s.points.length; i++) line(s.points[i - 1], s.points[i], (x, y) => {
        if (!seen.has(y * SIZE + x)) { seen.add(y * SIZE + x); blend(x, y, s.stroke, s.opacity); }
      });
      continue;
    }
    if (!s.fill) continue;
    const mask = new Uint8Array(SIZE * SIZE);
    for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) if (inside(s.points, x + 0.5, y + 0.5)) mask[y * SIZE + x] = 1;
    const a = s.glow ? glow.fill : s.opacity;
    if (s.glow) { // the blur, as a 1-px halo round the shape
      for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) {
        if (mask[y * SIZE + x]) continue;
        let near = false;
        for (let dy = -1; dy <= 1 && !near; dy++) for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx >= 0 && ny >= 0 && nx < SIZE && ny < SIZE && mask[ny * SIZE + nx]) { near = true; break; }
        }
        if (near) blend(x, y, s.fill, glow.halo);
      }
    }
    for (let i = 0; i < mask.length; i++) if (mask[i]) blend(i % SIZE, (i / SIZE) | 0, s.fill, a);
  }
  const rgb = new Uint8Array(SIZE * SIZE * 3);
  px.forEach((p, i) => {
    const dark = ((i / SIZE) | 0) % 3 === 0 ? SCANLINE : 1;
    rgb.set(p.map((v) => Math.round(v * dark)), i * 3);
  });
  return { width: SIZE, height: SIZE, rgb };
}

/** [eye glow bright, eye glow dim], each { width, height, rgb }. */
function frames(svgText = fs.readFileSync(SVG, "utf8")) {
  const shapes = parseSvg(svgText);
  return [render(shapes, GLOW.bright), render(shapes, GLOW.dim)];
}

module.exports = { SIZE, SVG, parseSvg, frames };
