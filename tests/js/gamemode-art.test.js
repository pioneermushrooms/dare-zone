// node --test tests/js/*.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Art = require("../../src/Dare.Arenas/Web/wwwroot/gamemode-art.js");
const Lvz = require("../../src/Dare.Arenas/Web/wwwroot/lvz.js");

const DIR = path.join(__dirname, "../../src/Dare.Arenas/Web/wwwroot/gamemode-art");
const read = (it) => new Uint8Array(fs.readFileSync(path.join(DIR, it.file)));

test("the first pass: digits, countdowns, six banners and team icons", () => {
  const count = (k) => Art.ITEMS.filter((i) => i.kind === k).length;
  assert.equal(count("digit"), 22);   // 0-9 and a colon, small and large
  assert.equal(count("countdown"), 3);
  assert.equal(count("banner"), 6);
  assert.equal(count("icon"), 8);     // a flag and a shield in four team colours
  assert.equal(new Set(Art.ITEMS.map((i) => i.file)).size, Art.ITEMS.length);
});

test("every file is listed and every listed file exists", () => {
  assert.deepEqual(fs.readdirSync(DIR).sort(), Art.ITEMS.map((i) => i.file).sort());
});

test("each image is an uncompressed 8-bit BMP of the listed size that lvz.js reads", () => {
  for (const it of Art.ITEMS) {
    const b = read(it), dv = new DataView(b.buffer);
    assert.equal(dv.getUint16(28, true), 8, `${it.file} bits`);
    assert.equal(dv.getUint32(30, true), 0, `${it.file} compression`);
    const im = Lvz.decodeBmp(b);
    assert.equal(im.width, it.w, it.file); assert.equal(im.height, it.h, it.file);
  }
});

test("the background is pure black, which LVZ draws as transparent, and each image has a picture", () => {
  for (const it of Art.ITEMS) {
    const { width: w, height: h, rgba } = Lvz.decodeBmp(read(it));
    const px = (x, y) => [...rgba.subarray((y * w + x) * 4, (y * w + x) * 4 + 3)];
    for (const [x, y] of [[0, 0], [w - 1, 0], [0, h - 1], [w - 1, h - 1]]) assert.deepEqual(px(x, y), [0, 0, 0], `${it.file} corner`);
    let lit = 0;
    for (let i = 0; i < w * h; i++) if (rgba[i * 4] + rgba[i * 4 + 1] + rgba[i * 4 + 2] > 120) lit++;
    assert.ok(lit > w * h * 0.04, `${it.file} has a picture`);
  }
});

test("animations are frames side by side, a second each", () => {
  for (const it of Art.ITEMS.filter((i) => i.frames)) {
    assert.equal(it.w, it.frames * it.frameW, it.file);
    assert.equal(it.frameMs, 1000, it.file);
  }
  assert.equal(Art.ITEMS.find((i) => i.file === "countdown-10.bmp").frames, 11);
});

test("urls point at the served folder", () => {
  assert.equal(Art.url(Art.ITEMS[0]), "/arena/gamemode-art/" + Art.ITEMS[0].file);
});

test("a stock image goes into an .lvz and comes back the same", async () => {
  const it = Art.ITEMS.find((i) => i.file === "countdown-10.bmp");
  assert.deepEqual(Art.lvzImage(it), { file: "countdown-10.bmp", cols: 11, rows: 1, time: 1100 }); // 1/100 s for the strip
  assert.deepEqual(Art.lvzImage(Art.ITEMS.find((i) => i.file === "banner-go.bmp")), { file: "banner-go.bmp", cols: 1, rows: 1, time: 0 });
  const lvz = Lvz.blank();
  Lvz.putFile(lvz, it.file, read(it));
  lvz.images.push(Art.lvzImage(it));
  lvz.objects.push(Lvz.newObject({ map: false, id: 2000, xRef: 0, yRef: 0, x: 10, y: 10, image: 0, mode: 5, time: 110 }));
  const back = await Lvz.read(await Lvz.write(lvz));
  assert.deepEqual([...await Lvz.fileData(back.files[0])], [...read(it)]); // files decompress lazily
  assert.deepEqual({ ...back.images[0] }, Art.lvzImage(it));
});
