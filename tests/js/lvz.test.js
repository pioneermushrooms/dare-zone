// node --test tests/js   (no packages needed; zlib is node's CompressionStream)
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Lvz = require("../../src/Dare.Arenas/Web/wwwroot/lvz.js");

const match = new Uint8Array(fs.readFileSync(path.join(__dirname, "fixtures/match.lvz")));
const REF = "/Users/chris/dare-zone/.claude/lvztoolkit-ref"; // the LVZ Toolkit's docs and sample, when on this machine

test("reads the stock match.lvz: files, a CLV2 object section, images", async () => {
  const lvz = await Lvz.read(match);
  assert.equal(lvz.version, "CLV2");
  assert.equal(lvz.files.length, 135);
  assert.equal(lvz.objects.length, 989);
  assert.equal(lvz.images.length, 139);
  assert.deepEqual(lvz.warnings, []);
  assert.equal(lvz.files[0].name, "032.bmp");
  const data = await Lvz.fileData(lvz.files[0]);
  assert.equal(data.length, 342);
  assert.equal(data[0], 0x42);
  const nums = lvz.images.find((i) => i.file === "score_numbers.bmp");
  assert.deepEqual([nums.cols, nums.rows, nums.time], [13, 1, 1300]);
  // first object: 10 bytes 90 1f 02 f1 00 00 8a 02 00 50
  const o = lvz.objects[0];
  assert.equal(o.map, false);
  assert.equal(o.id, 0x1f90 >> 1);
  assert.equal(o.xRef, 2); assert.equal(o.x, -240); // 0xf102: ref B, offset 0xf10 = -240
  assert.equal(o.yRef, 0); assert.equal(o.y, 0);
  assert.equal(o.image, 0x8a); assert.equal(o.layer, 2);
  assert.equal(o.mode, 5); assert.equal(o.time, 0);
});

test("an .lvz read and written unchanged is byte for byte the same", async () => {
  const lvz = await Lvz.read(match);
  assert.deepEqual(await Lvz.write(lvz), match);
  // even after its files were looked at
  for (const f of lvz.files.slice(0, 5)) await Lvz.fileData(f);
  assert.deepEqual(await Lvz.write(lvz), match);
});

test("object sections re-serialise to the same bytes", async () => {
  const sec = Lvz.sections(match).find((s) => s.name === "" && s.time === 0);
  const data = new Uint8Array(await new Response(new Blob([sec.packed]).stream().pipeThrough(new DecompressionStream("deflate"))).arrayBuffer());
  const parsed = Lvz.parseObjects(data);
  assert.deepEqual(Lvz.objectBytes(parsed), data);
});

test("edits survive a write and read; untouched files keep their bytes", async () => {
  const lvz = await Lvz.read(match);
  lvz.objects[0].x = -100; lvz.objects[0].xRef = 1; lvz.objects[0].time = 55; lvz.objects[0].mode = 2;
  lvz.objects.push(Lvz.newObject({ map: true, id: 7, x: 8000, y: 9000, image: 3, layer: 0 }));
  Lvz.putFile(lvz, "new.bmp", new Uint8Array([0x42, 0x4d, 1, 2, 3]));
  Lvz.putFile(lvz, "032.BMP", new Uint8Array([9, 9])); // replaces 032.bmp (any case)
  const back = await Lvz.read(await Lvz.write(lvz));
  assert.equal(back.files.length, 136);
  assert.deepEqual(await Lvz.fileData(back.files[0]), new Uint8Array([9, 9]));
  assert.deepEqual(await Lvz.fileData(back.files.at(-1)), new Uint8Array([0x42, 0x4d, 1, 2, 3]));
  assert.deepEqual(await Lvz.fileData(back.files[1]), await Lvz.fileData((await Lvz.read(match)).files[1]));
  const o = back.objects[0];
  assert.deepEqual([o.xRef, o.x, o.time, o.mode], [1, -100, 55, 2]);
  const m = back.objects.at(-1);
  assert.deepEqual([m.map, m.id, m.x, m.y, m.image, m.layer], [true, 7, 8000, 9000, 3, 0]);
});

test("values outside the format's ranges are clamped, not wrapped", () => {
  const lvz = Lvz.blank();
  lvz.images.push({ file: "a.png", cols: 1, rows: 1, time: 100 });
  lvz.objects.push(Lvz.newObject({ map: false, x: 5000, y: -5000, time: 99999, id: 40000 }));
  const back = Lvz.parseObjects(Lvz.objectBytes(lvz));
  assert.deepEqual([back.objects[0].x, back.objects[0].y, back.objects[0].time], [2047, -2048, 4095]);
  assert.equal(back.objects[0].id, 40000 & 0x7fff);
});

test("CLV1 screen words are read the way Continuum places them, and saved as CLV2", async () => {
  // Trench Wars' entermsg.lvz banner (oss-core's measured case): x word 0x4CF4, y word 0x0064
  const d = new Uint8Array(12 + 10 + 7 + 12), dv = new DataView(d.buffer);
  d.set(new TextEncoder().encode("CLV1"), 0); dv.setUint32(4, 1, true); dv.setUint32(8, 1, true);
  dv.setUint16(12, 0, true); dv.setUint16(14, 0x4cf4, true); dv.setUint16(16, 0x0064, true); d[18] = 0; d[19] = 6;
  dv.setUint16(20, (2 << 12) | 90, true);
  dv.setInt16(22, 1, true); dv.setInt16(24, 1, true); dv.setInt16(26, 0, true); d.set(new TextEncoder().encode("entermsg.jpg"), 28);
  const sec = Lvz.parseObjects(d), o = sec.objects[0];
  assert.equal(sec.version, "CLV1");
  // centred 960 box: bottom-right corner = centre + 480; offset -780 -> centre - 300
  assert.deepEqual([o.xRef, o.x, o.yRef, o.y], [1, -300, 0, 100]);
  assert.equal(1888 / 2 + o.x, 644); // where Continuum drew it at 1888 wide
  assert.deepEqual([o.mode, o.time, o.layer], [2, 90, 6]);
  const again = Lvz.parseObjects(Lvz.objectBytes({ version: "CLV1", objects: sec.objects, images: sec.images }));
  assert.equal(again.version, "CLV2");
  assert.deepEqual([again.objects[0].xRef, again.objects[0].x], [1, -300]);
  // bottom-right and top-left words
  dv.setUint16(14, 0xfff6, true); dv.setUint16(16, 0x0010, true);
  const b = Lvz.parseObjects(d).objects[0];
  assert.deepEqual([b.xRef, b.x, b.yRef, b.y], [2, -10, 0, 16]);
  // a CLV1 section with only map objects stays CLV1
  const maps = { version: "CLV1", images: [], objects: [Lvz.newObject({ x: 6752, y: 3728 })] };
  assert.equal(Lvz.parseObjects(Lvz.objectBytes(maps)).version, "CLV1");
});

test("damaged files are refused with a message", async () => {
  await assert.rejects(Lvz.read(new Uint8Array([1, 2, 3])), /not an .lvz/);
  await assert.rejects(Lvz.read(match.slice(0, 200)), /section/);
  assert.throws(() => Lvz.parseObjects(new TextEncoder().encode("CLV9\0\0\0\0\0\0\0\0")), /unknown object section/);
  assert.throws(() => Lvz.parseObjects(new TextEncoder().encode("CLV2\x05\0\0\0\0\0\0\0")), /object count/);
});

test("images: removing one drops its objects and renumbers the rest", () => {
  const lvz = Lvz.blank();
  lvz.images.push({ file: "a" }, { file: "b" }, { file: "c" });
  lvz.objects.push(Lvz.newObject({ image: 0 }), Lvz.newObject({ image: 1 }), Lvz.newObject({ image: 2 }));
  Lvz.removeImage(lvz, 1);
  assert.deepEqual(lvz.images.map((i) => i.file), ["a", "c"]);
  assert.deepEqual(lvz.objects.map((o) => o.image), [0, 1]);
});

test(".ini projects: read, write and read again", () => {
  const ini = [
    "; test", "OutFile=t.lvz", "File=a.bmp", "File=gone.wa2", "", "[objectimages]", "IMAGE0=a.bmp", "IMAGE5=anim.bmp,4,2,200",
    "[mapobjects]", "8192,8192,IMAGE5,AfterTiles,ServerControlled,500,1", "0,0,IMAGE0,BelowAll",
    "[screenobjects]", "C-146,29,IMAGE0,AfterChat,EnterZone,3000", "r-23,b10,IMAGE5,TopMost,EnterArena,0,12",
  ].join("\r\n");
  const { lvz, outFile, missing, errors } = Lvz.fromIni(ini, new Map([["a.bmp", new Uint8Array([1])]]));
  assert.deepEqual(errors, []);
  assert.equal(outFile, "t.lvz");
  assert.deepEqual(missing, ["gone.wa2"]);
  assert.deepEqual(lvz.files.map((f) => f.name), ["a.bmp"]);
  assert.deepEqual(lvz.images, [{ file: "a.bmp", cols: 1, rows: 1, time: 100 }, { file: "anim.bmp", cols: 4, rows: 2, time: 200 }]);
  const [m1, m2, s1, s2] = lvz.objects;
  assert.deepEqual([m1.map, m1.x, m1.y, m1.image, m1.layer, m1.mode, m1.time, m1.id], [true, 8192, 8192, 1, 2, 5, 50, 1]);
  assert.deepEqual([m2.layer, m2.mode, m2.time, m2.id], [0, 0, 0, 0]);
  assert.deepEqual([s1.map, s1.xRef, s1.x, s1.yRef, s1.y, s1.mode, s1.time], [false, 1, -146, 0, 29, 1, 300]);
  assert.deepEqual([s2.xRef, s2.x, s2.yRef, s2.y, s2.id], [8, -23, 2, 10, 12]);
  const text = Lvz.toIni(lvz, "t.lvz");
  const again = Lvz.fromIni(text, new Map([["a.bmp", new Uint8Array([1])]]));
  assert.deepEqual(again.errors, []);
  assert.deepEqual(again.lvz.objects, lvz.objects);
  assert.deepEqual(again.lvz.images, lvz.images);
  assert.match(text, /^C-146,29,IMAGE0,AfterChat,EnterZone,3000,0\r$/m);
});

test(".ini mistakes are reported with their line", () => {
  const { errors } = Lvz.fromIni("[objectimages]\nIMAGE0=a.bmp\n[mapobjects]\n1,2,IMAGE9\nC5,2,IMAGE0\n1,2,IMAGE0,Sideways\n0,0,IMAGE0,TopMost,Kill,90000\n[nope]\nx=1\n");
  assert.match(errors.join("\n"), /line 4: IMAGE9 isn't in/);
  assert.match(errors.join("\n"), /line 5: map objects take plain/);
  assert.match(errors.join("\n"), /line 6: unknown layer Sideways/);
  assert.match(errors.join("\n"), /line 7: display time 90000/);
  assert.match(errors.join("\n"), /line 9: unknown section \[nope\]/);
});

test("the LVZ Toolkit's sample project reads cleanly", { skip: !fs.existsSync(REF) && "toolkit reference not on this machine" }, () => {
  const dir = path.join(REF, "Sample"), files = new Map();
  for (const n of fs.readdirSync(dir)) files.set(n.toLowerCase(), new Uint8Array(fs.readFileSync(path.join(dir, n))));
  const { lvz, errors, missing } = Lvz.fromIni(fs.readFileSync(path.join(dir, "reflvz.ini"), "latin1"), files);
  assert.deepEqual(errors, []);
  assert.deepEqual(missing, []);
  assert.equal(lvz.files.length, 13);
  assert.equal(lvz.images.length, 13);
  assert.equal(lvz.objects.length, 23);
  assert.deepEqual(new Set(lvz.objects.map((o) => o.xRef)), new Set([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]));
  const bmp = Lvz.decodeBmp(files.get("a.bmp"));
  assert.ok(bmp.width > 0 && bmp.height > 0);
  const ex = Lvz.fromIni(fs.readFileSync(path.join(REF, "Example.ini"), "latin1"));
  assert.deepEqual(ex.missing, ["ships.bm2", "bomb3.wa2", "Commands.hlp"]);
  assert.equal(ex.lvz.objects.length, 4);
  assert.ok(ex.errors.some((e) => /100000/.test(e)), "the sample's 100000 is more than the format holds");
});

test("BMP images: palette and 24-bit, black is see-through", () => {
  const w = 2, h = 1, b = new Uint8Array(54 + 8), dv = new DataView(b.buffer);
  b[0] = 0x42; b[1] = 0x4d; dv.setUint32(10, 54, true); dv.setUint32(14, 40, true); dv.setInt32(18, w, true); dv.setInt32(22, h, true); dv.setUint16(28, 24, true);
  b.set([0, 0, 0, 10, 20, 30], 54); // black, then rgb(30,20,10)
  const im = Lvz.decodeBmp(b);
  assert.deepEqual([...im.rgba], [0, 0, 0, 0, 30, 20, 10, 255]);
  assert.throws(() => Lvz.decodeBmp(new Uint8Array(10)), /not a BMP/);
});

test("safe names for files inside an .lvz", () => {
  assert.equal(Lvz.safeName("C:\\pics\\my logo (1).png"), "my_logo_1_.png");
  assert.equal(Lvz.safeName("../../etc/passwd"), "passwd");
  assert.equal(Lvz.safeName("..."), "image.png");
});

test("ids 2000-2999 are kept for game modes: new objects never get one", () => {
  const lvz = Lvz.blank();
  assert.equal(Lvz.nextId(lvz), 1);
  lvz.objects.push(Lvz.newObject({ id: 5 }), Lvz.newObject({ id: 2500 }));
  assert.equal(Lvz.nextId(lvz), 6); // the game mode's 2500 doesn't count
  lvz.objects.push(Lvz.newObject({ id: 1999 }));
  assert.equal(Lvz.nextId(lvz), 3000);
  assert.ok(Lvz.isReserved(2000) && Lvz.isReserved(2999) && !Lvz.isReserved(1999) && !Lvz.isReserved(3000));
  lvz.objects.push(Lvz.newObject({ id: 32767 }));
  assert.equal(Lvz.nextId(lvz), 1); // full at the top: the lowest free id outside the range
});
