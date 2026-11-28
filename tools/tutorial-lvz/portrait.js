// The mentor's placeholder portrait: two 64x64 pixel-art frames (mouth closed, mouth open), drawn
// here. A veteran pilot in a helmet with a headset mic and a cocky raised brow. No pure black:
// Continuum shows black in LVZ images as see-through.
"use strict";

const SIZE = 64;
const C = {
  bg: [26, 34, 54], bgDot: [34, 44, 70], outline: [14, 16, 24],
  helmet: [96, 110, 134], helmetHi: [142, 156, 180], visor: [250, 196, 64], stripe: [214, 72, 56],
  skin: [226, 174, 132], skinShade: [190, 136, 100], brow: [86, 56, 40], eye: [30, 34, 44], eyeHi: [240, 240, 240],
  mouth: [126, 58, 50], mouthIn: [70, 22, 26], tongue: [200, 92, 96], suit: [84, 100, 64], suitHi: [112, 132, 84], mic: [60, 64, 74],
};

function image() {
  const px = new Array(SIZE * SIZE).fill(null);
  const set = (x, y, c) => { if (x >= 0 && y >= 0 && x < SIZE && y < SIZE) px[y * SIZE + x] = c; };
  const rect = (x0, y0, w, h, c) => { for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) set(x, y, c); };
  const ellipse = (cx, cy, rx, ry, c, keep = () => true) => {
    for (let y = Math.floor(cy - ry); y <= Math.ceil(cy + ry); y++) for (let x = Math.floor(cx - rx); x <= Math.ceil(cx + rx); x++)
      if (((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1 && keep(x, y)) set(x, y, c);
  };
  return { px, set, rect, ellipse };
}

function frame(open) {
  const im = image();
  // shoulders and flight suit
  im.ellipse(32, 66, 28, 14, C.suit);
  im.rect(22, 54, 20, 2, C.suitHi);
  im.rect(28, 50, 8, 6, C.skinShade); // neck
  // helmet behind the face, with a stripe
  im.ellipse(32, 28, 21, 21, C.helmet);
  im.ellipse(27, 18, 8, 5, C.helmetHi);
  im.rect(30, 7, 4, 14, C.stripe);
  // face
  im.ellipse(32, 35, 13, 15, C.skin);
  im.ellipse(32, 41, 12, 9, C.skinShade, (x, y) => y > 44);
  // visor pushed up on the helmet
  im.rect(18, 20, 28, 5, C.visor);
  im.rect(18, 20, 28, 1, C.helmetHi);
  // eyes and brows (the left brow raised)
  for (const ex of [25, 36]) { im.rect(ex, 31, 3, 3, C.eye); im.set(ex, 31, C.eyeHi); }
  im.rect(24, 27, 5, 1, C.brow); im.set(23, 28, C.brow);
  im.rect(35, 29, 5, 1, C.brow);
  im.set(31, 37, C.skinShade); im.set(32, 38, C.skinShade); // nose
  // mouth: a smirk, or open mid-word
  if (open) {
    im.rect(28, 42, 8, 5, C.mouthIn);
    im.rect(29, 45, 6, 2, C.tongue);
    im.rect(28, 42, 8, 1, C.mouth);
  } else {
    im.rect(28, 44, 7, 1, C.mouth);
    im.set(35, 43, C.mouth);
  }
  // headset mic
  im.rect(14, 30, 5, 9, C.mic);
  for (let i = 0; i < 7; i++) im.set(19 + i, 40 + (i >> 1), C.mic);
  im.rect(25, 43, 2, 2, C.mic);

  // outline: shape pixels next to the background
  const px = im.px.slice();
  for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) {
    if (!im.px[y * SIZE + x]) continue;
    const edge = [[1, 0], [-1, 0], [0, 1], [0, -1]].some(([dx, dy]) => {
      const nx = x + dx, ny = y + dy;
      return nx >= 0 && ny >= 0 && nx < SIZE && ny < SIZE && !im.px[ny * SIZE + nx];
    });
    if (edge) px[y * SIZE + x] = C.outline;
  }
  // background with a faint dot grid, and a frame
  for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) {
    if (x === 0 || y === 0 || x === SIZE - 1 || y === SIZE - 1) px[y * SIZE + x] = C.helmetHi;
    else if (!px[y * SIZE + x]) px[y * SIZE + x] = x % 6 === 3 && y % 6 === 3 ? C.bgDot : C.bg;
  }
  const rgb = new Uint8Array(SIZE * SIZE * 3);
  px.forEach((c, i) => rgb.set(c, i * 3));
  return { width: SIZE, height: SIZE, rgb };
}

/** [mouth closed, mouth open], each { width, height, rgb } (rgb: 3 bytes per pixel, rows top-down). */
const frames = () => [frame(false), frame(true)];

module.exports = { SIZE, frames };
