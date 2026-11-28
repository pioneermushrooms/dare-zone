// The tutorial arena's generated files: node --test tests/js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const ROOT = path.join(__dirname, "../..");
const Lvz = require(path.join(ROOT, "src/Dare.Arenas/Web/wwwroot/lvz.js"));
const Build = require(path.join(ROOT, "tools/tutorial-lvz/build.js"));
const TrainingMap = require(path.join(ROOT, "tools/tutorial-lvz/map.js"));
const Font = require(path.join(ROOT, "tools/tutorial-lvz/font.js"));
const Portrait = require(path.join(ROOT, "tools/tutorial-lvz/portrait.js"));

const ARENA = path.join(ROOT, "deploy/arenas/#training");
const script = JSON.parse(fs.readFileSync(path.join(ROOT, "src/Dare.Arenas/tutorial/script.json"), "utf8"));
// AllLines(): step lines in file order, then prompts; Index from 0
const lines = [...script.steps.flatMap((s) => s.lines.map((text) => ({ text }))), ...script.prompts.map((p) => ({ text: p.text, id: p.id }))]
  .map((l, index) => ({ ...l, index }));
const SC = 5; // ServerControlled
const TOPMOST = 7;

let built;
const model = async () => (built ??= await Build.buildLvz(script));
const byId = (lvz, id) => lvz.objects.filter((o) => o.id === id);
const imageOf = async (lvz, o) => {
  const im = lvz.images[o.image];
  const f = lvz.files.find((x) => x.name === im.file);
  return Lvz.decodeBmp(await Lvz.fileData(f));
};
const overlaps = (a, b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

// ---- where the HUD really is ------------------------------------------------------------------------
// The Dare client's layout, copied from crates/oss-client/src/game/hud.rs in dare-client (UI scale 1), whose
// anchors were measured against Continuum at 1888x1016 (hud.rs the_lvz_anchors_match_continuum_at_1888x1016).
// If hud.rs changes, change these and re-check the covers in build.js.
const RADAR_MARGIN = 6; // hud.rs RADAR_MARGIN
const radarDim = (w) => (Math.floor(Math.floor(w / 6) / 4) * 8) / 2; // hud.rs small_radar_dim (u32 maths)
const iconsTop = (h) => Math.floor(h * 0.57 + 1 - 100) - 20; // hud.rs icons_top, ICONS_TOP_MEASURED_SHIFT 20
const ICON_PITCH = 25, SPECIALS = 7, WEAPONS = 6, ICON = [26, 24]; // hud.rs draw_items (icons 26x24, 25 apart)
const COUNT_TAB_RIGHT = 23 + 11; // draw_items: the icondoor count, 11 wide at x 23
const BAR_HALF = 240, GAUGE_H = 28; // hud.rs BAR_HALF; the hlthbar frame, 116x28 at y 0
const DIGIT = [16, 24], ENERGY_DIGITS = 4; // draw_energy's engyfont; this arena's Warbird has 3000
const RADAR_TEXT_UP = 16, GLYPH = [8, 12]; // draw_radar: the clock line's top 16px above the radar, 8x12 font
const LONGEST_RADAR_LINE = "12:59pm  T20"; // radar_text: clock_text, two spaces, map_coordinate
// chat.rs visible_rows (no chat-lines setting); sandbox.rs: chat_top = view_h - 16 - rows * 12
const chatTopOf = (h) => h - 16 - (Math.floor(h / 100) + 1) * 12;
const chatRight = (w) => 4 + 8 * Math.max(16, Math.floor((w - radarDim(w) - 12) / 8)); // sandbox.rs draw_log, chat_width
// statbox.rs, Names view: BORDER 3, HEADER_HEIGHT 14, ROW 12, width 108; sandbox.rs draw_statbox puts reference 3
// at (x + w + 3, y + h): (114, 19 + 12 * rows)
const statboxCorner = (rows) => [3 + 108 + 3, 3 + 14 + 2 + 12 * rows];
const TUTORIAL_PILOTS = 8; // the player, the target, the 5-bot swarm and the teammate

/** The twelve anchors (hud.rs screen_refs) and each HUD part's rectangles [x0, y0, x1, y1] on a w x h screen.
 *  `over` swaps in Continuum's own numbers where they differ. */
function hud(w, h, over = {}) {
  const dim = radarDim(w), rx = w - dim - RADAR_MARGIN, ry = h - dim - RADAR_MARGIN, top = iconsTop(h), cx = Math.floor(w / 2);
  const corner = over.statbox ?? statboxCorner(TUTORIAL_PILOTS), chatTop = over.chatTop ?? chatTopOf(h);
  const line = over.radarLine ?? LONGEST_RADAR_LINE, textLeft = rx + dim - GLYPH[0] * line.length;
  const refs = [[0, 0], [cx, Math.floor(h / 2)], [w, h], corner, [26, top], [26, top + SPECIALS * ICON_PITCH], [cx, GAUGE_H],
    [0, chatTop], [rx, ry], [textLeft, ry - RADAR_TEXT_UP], [w - 26, top], [w - 26, top + WEAPONS * ICON_PITCH]];
  const parts = {
    radar: [Math.min(rx - 1, textLeft), ry - RADAR_TEXT_UP, rx + dim + 1, ry + dim + 1], // with its border and clock line
    energy: [cx - BAR_HALF, 0, cx + BAR_HALF, GAUGE_H],
    energynumber: [w - DIGIT[0] * ENERGY_DIGITS, 0, w, DIGIT[1]],
    statbox: [over.statbox ? 0 : 3, over.statbox ? 0 : 3, corner[0], corner[1]],
    items: [0, top, over.countTabRight ?? COUNT_TAB_RIGHT, top + (SPECIALS - 1) * ICON_PITCH + ICON[1]],
    weapons: [w - ICON[0], top, w, top + (WEAPONS - 1) * ICON_PITCH + ICON[1]],
    chat: [0, chatTop, chatRight(w), h],
  };
  return { w, h, refs, parts };
}
// Continuum, as measured at 1888x1016 (hud.rs the_lvz_anchors_match_continuum_at_1888x1016): the player list's
// corner at (109, 29), the chat's top at 992 and the clock line "4:53pm  A1"; its specials count tab reaches 37
// (hud.rs screen_refs: "icon (26) plus the count tab beside it").
const continuum = () => hud(1888, 1016, { statbox: [109, 29], chatTop: 992, radarLine: "4:53pm  A1", countTabRight: 37 });
const LAYOUTS = [["Dare 1024x768", () => hud(1024, 768)], ["Dare 1920x1080", () => hud(1920, 1080)], ["Continuum 1888x1016", continuum]];

const rect = async (lvz, o, layout) => {
  const im = await imageOf(lvz, o);
  const x = layout.refs[o.xRef][0] + o.x, y = layout.refs[o.yRef][1] + o.y;
  return [x, y, x + im.width / (lvz.images[o.image].cols || 1), y + im.height / (lvz.images[o.image].rows || 1)];
};
const onScreen = (r, L) => [Math.max(0, r[0]), Math.max(0, r[1]), Math.min(L.w, r[2]), Math.min(L.h, r[3])];

test("every script line has an object 5000+index and an image", async () => {
  const lvz = await model();
  assert.ok(lines.length > 20);
  for (const l of lines) {
    const objs = byId(lvz, 5000 + l.index);
    assert.equal(objs.length, 1, `line ${l.index}`);
    const o = objs[0];
    assert.equal(o.map, false);
    const im = await imageOf(lvz, o);
    assert.equal(im.width, Build.TEXT_W);
    // popup prompts are just the two rows; box lines have the step's row above them
    assert.equal(im.height, ["skip_popup", "try_tutorial"].includes(l.id) ? Build.TEXT_H : Build.LINE_H, `line ${l.index} height`);
    assert.ok(im.rgba.some((v, i) => i % 4 === 3 && v), `line ${l.index} has visible text`);
  }
  assert.equal(byId(lvz, 5000 + lines.length).length, 0);
});

test("every line wraps into two lines of the bubble at most", () => {
  for (const l of lines) {
    const rows = Build.layoutLine(l.text, script.continuumKeys);
    assert.ok(rows.length >= 1 && rows.length <= 2, `${l.text} -> ${JSON.stringify(rows)}`);
    for (const r of rows) assert.ok(r.length * Font.W <= Build.TEXT_W, r);
  }
});

test("covers 4000-4005 are TopMost and server-controlled", async () => {
  const lvz = await model();
  for (let id = 4000; id <= 4005; id++) {
    const objs = byId(lvz, id);
    assert.ok(objs.length >= 1, `cover ${id}`);
    for (const o of objs) {
      assert.equal(o.map, false);
      assert.equal(o.layer, TOPMOST, `cover ${id} layer`);
      assert.equal(o.mode, SC, `cover ${id} mode`);
      const im = await imageOf(lvz, o);
      // Continuum draws pure black as see-through: a cover must be opaque everywhere
      assert.ok(im.rgba.every((v, i) => i % 4 !== 3 || v === 255), `cover ${id} is opaque`);
      // ...and looks black: the darkest colour that still isn't black in Continuum's 16-bit (RGB565) colour
      for (let i = 0; i < im.rgba.length; i += 4) {
        const [r, g, b] = im.rgba.subarray(i, i + 3);
        assert.ok((r >> 3) + (g >> 2) + (b >> 3) > 0, `cover ${id} pixel turns black (see-through) in 16-bit colour`);
        assert.ok(r + g + b <= 4, `cover ${id} pixel ${r},${g},${b} is not black-looking`);
      }
    }
  }
});

test("the HUD model is hud.rs's: it gives the anchors measured against Continuum", () => {
  // hud.rs the_lvz_anchors_match_continuum_at_1888x1016, with the same inputs
  const L = hud(1888, 1016, { statbox: [109, 29], chatTop: 992, radarLine: "4:53pm  A1" });
  assert.equal(radarDim(1888), 312);
  assert.deepEqual(L.refs, [[0, 0], [944, 508], [1888, 1016], [109, 29], [26, 460], [26, 635], [944, 28], [0, 992],
    [1570, 698], [1802, 682], [1862, 460], [1862, 610]]);
});

test("each cover hides its whole HUD part, with at most 8px to spare, in the Dare client and Continuum", async () => {
  const lvz = await model();
  const SLACK = 8;
  for (const [name, layoutOf] of LAYOUTS) {
    const L = layoutOf();
    for (const o of lvz.objects.filter((x) => x.id >= 4000 && x.id <= 4005)) {
      const what = lvz.images[o.image].file.replace(/^cover_|\.bmp$/g, "");
      const r = onScreen(await rect(lvz, o, L), L);
      if (what === "upperchat") continue; // a band, not a part: checked below
      const part = L.parts[what];
      assert.ok(part, `cover ${o.id} (${what}) has a part`);
      const where = `${name}: cover ${o.id} (${what}) ${r} over ${part}`;
      assert.ok(r[0] <= part[0] && r[1] <= part[1] && r[2] >= part[2] && r[3] >= part[3], `${where}: part shows`);
      for (let k = 0; k < 2; k++) assert.ok(part[k] - r[k] <= SLACK, `${where}: ${part[k] - r[k]}px spare`);
      for (let k = 2; k < 4; k++) assert.ok(r[k] - part[k] <= SLACK, `${where}: ${r[k] - part[k]}px spare`);
    }
    // every part has a cover with its id: radar 4000, energy 4001, list 4002, items 4003, chat 4004
    const idOf = { radar: 4000, energy: 4001, energynumber: 4001, statbox: 4002, items: 4003, weapons: 4003, chat: 4004 };
    for (const [what, id] of Object.entries(idOf))
      assert.ok(byId(lvz, id).some((o) => lvz.images[o.image].file === `cover_${what}.bmp`), `${name}: ${what} has cover ${id}`);
    // 4005: the room for four more chat lines, straight above the chat cover and as wide
    const [band] = byId(lvz, 4005), [chat] = byId(lvz, 4004).filter((o) => lvz.images[o.image].file === "cover_chat.bmp");
    const [b, c] = [await rect(lvz, band, L), await rect(lvz, chat, L)];
    assert.deepEqual([b[0], b[2], b[3], b[3] - b[1]], [c[0], c[2], c[1], 4 * GLYPH[1]], `${name}: band above the chat`);
  }
});

test("covers don't overlap each other or the ship", async () => {
  const lvz = await model();
  for (const [name, layoutOf] of LAYOUTS) {
    const L = layoutOf();
    // a revealed part is not hidden by a cover that stays on longer: no two different covers overlap
    const all = [];
    for (let id = 4000; id <= 4005; id++) for (const o of byId(lvz, id)) all.push([id, onScreen(await rect(lvz, o, L), L)]);
    for (const [ia, a] of all) for (const [ib, b] of all) if (ia < ib) assert.ok(!overlaps(a, b), `${name}: covers ${ia} and ${ib} overlap`);
    const [cx, cy] = L.refs[1];
    for (const [id, r] of all) assert.ok(!overlaps(r, [cx - 20, cy - 20, cx + 20, cy + 20]), `${name}: cover ${id} hides the ship`);
  }
});

test("mentor objects are server-controlled and start off", async () => {
  const lvz = await model();
  const ids = [4100, 4101, 4102, 4110, 4111, 4200, ...lines.map((l) => 5000 + l.index)];
  for (const id of ids) {
    const objs = byId(lvz, id);
    assert.equal(objs.length, 1, `object ${id}`);
    assert.equal(objs[0].mode, SC, `object ${id} mode`);
    assert.equal(objs[0].time, 0, `object ${id} stays until turned off`);
  }
  assert.ok(lvz.objects.every((o) => o.mode === SC), "every object is server-controlled");
  assert.ok(lvz.objects.every((o) => !Lvz.isReserved(o.id)), "no game-mode ids");
  // the box: bottom-left, pinned to the chat anchor, above the chat lines; drawn after the covers
  const box = byId(lvz, 4100)[0];
  assert.deepEqual([box.xRef, box.yRef], [7, 7]);
  assert.ok(box.y < 0);
  const last = (id) => lvz.objects.findIndex((o) => o.id === id);
  for (let id = 4000; id <= 4005; id++) assert.ok(last(id) < last(4100), `cover ${id} before the box`);
  for (const id of [4101, 4102, ...lines.filter((l) => !l.id || l.id === "arena_pointer").map((l) => 5000 + l.index)]) {
    const o = byId(lvz, id)[0];
    assert.deepEqual([o.xRef, o.yRef, o.layer], [7, 7, TOPMOST], `object ${id}`);
    const [r, b] = [await rect(lvz, o, hud(1024, 768)), await rect(lvz, box, hud(1024, 768))];
    assert.ok(r[0] >= b[0] && r[1] >= b[1] && r[2] <= b[2] && r[3] <= b[3], `object ${id} inside the box`);
  }
  // the box covers nothing meant to be seen: not the radar, items, chat (nor the band above it) or ship
  for (const [name, layoutOf] of LAYOUTS) {
    const L = layoutOf(), b = await rect(lvz, box, L);
    for (const part of ["radar", "items", "weapons", "chat", "statbox", "energy"]) assert.ok(!overlaps(b, L.parts[part]), `${name}: box ${b} over the ${part}`);
    assert.ok(!overlaps(b, await rect(lvz, byId(lvz, 4005)[0], L)), `${name}: box over the upper chat band`);
    assert.ok(b[0] >= 0 && b[1] > L.refs[1][1] + 40, `${name}: box ${b} clear of the ship`);
  }
  // popup prompts sit inside their popups
  for (const [pid, popup] of [["skip_popup", 4110], ["try_tutorial", 4111]]) {
    const l = lines.find((x) => x.id === pid);
    const [r, p] = [await rect(lvz, byId(lvz, 5000 + l.index)[0], hud(1024, 768)), await rect(lvz, byId(lvz, popup)[0], hud(1024, 768))];
    assert.ok(r[0] >= p[0] && r[1] >= p[1] && r[2] <= p[2] && r[3] <= p[3], `${pid} inside popup ${popup}`);
  }
  // portrait frames: same spot, different pictures
  const [a, b] = [byId(lvz, 4101)[0], byId(lvz, 4102)[0]];
  assert.deepEqual([a.x, a.y], [b.x, b.y]);
  assert.notDeepEqual((await imageOf(lvz, a)).rgba, (await imageOf(lvz, b)).rgba);
  // the beacon is a map object on the beacon spot
  const beacon = byId(lvz, 4200)[0];
  assert.equal(beacon.map, true);
  const size = (await imageOf(lvz, beacon)).height; // square frames side by side
  const spot = TrainingMap.SPOTS.beacon;
  assert.equal(beacon.x + size / 2, spot.x * 16 + 8);
  assert.equal(beacon.y + size / 2, spot.y * 16 + 8);
});

test("placeholders are filled with Continuum keys", () => {
  const shoot = lines.find((l) => l.text.includes("{gun}"));
  const rows = Build.layoutLine(shoot.text, script.continuumKeys).join(" ");
  assert.match(rows, /Ctrl/);
  assert.doesNotMatch(rows, /[{}]/);
  assert.equal(Build.forContinuum("{gun} and {nope}", { gun: "Ctrl" }), "Ctrl and {nope}");
  assert.match(Build.layoutLine("Now {repel}: go", script.continuumKeys).join(" "), /Shift\+Ctrl/);
});

test("the font draws every printable ASCII character, each one different", () => {
  const seen = new Set();
  for (let c = 33; c <= 126; c++) {
    const bits = Font.glyph(String.fromCharCode(c));
    assert.equal(bits.length, Font.W * Font.H);
    assert.ok(bits.some(Boolean), `char ${c} draws something`);
    const key = bits.join("");
    assert.ok(!seen.has(key), `char ${String.fromCharCode(c)} is unique`);
    seen.add(key);
  }
  assert.ok(Font.glyph(" ").every((v) => !v));
  assert.deepEqual(Font.wrap("aaa bbb ccc", 7), ["aaa bbb", "ccc"]);
  assert.deepEqual(Font.wrap("abcdefghij", 4), ["abcd", "efgh", "ij"]);
});

test("output round-trips through lvz.js and matches the checked-in file", async () => {
  const lvz = await model();
  const back = await Lvz.read(await Lvz.write(lvz));
  assert.deepEqual(back.warnings, []);
  assert.equal(back.objects.length, lvz.objects.length);
  assert.ok(back.images.length <= 256);
  const shape = async (m) => ({
    objects: m.objects.map(({ map, id, x, y, xRef, yRef, image, layer, mode, time }) => ({ map, id, x, y, xRef, yRef, image, layer, mode, time })),
    images: m.images.map(({ file, cols, rows, time }) => ({ file, cols, rows, time })),
    files: await Promise.all(m.files.map(async (f) => [f.name, Buffer.from(await Lvz.fileData(f)).toString("base64")])),
  });
  const want = await shape(lvz);
  assert.deepEqual(await shape(back), want);
  const disk = await Lvz.read(new Uint8Array(fs.readFileSync(path.join(ARENA, "tutorial.lvz"))));
  assert.deepEqual(await shape(disk), want, "deploy/arenas/#training/tutorial.lvz is stale: run node tools/tutorial-lvz/build.js");
});

test("the portrait is the browser's SVG, two frames that differ only in the eye glow", async () => {
  const lvz = await model();
  const svg = fs.readFileSync(path.join(ROOT, "site/tutorial-vex.svg"), "utf8");
  const shapes = Portrait.parseSvg(svg);
  assert.ok(shapes.length > 20);
  assert.equal(shapes.filter((x) => x.glow).length, 2, "two eye glows");
  // every shape is one portrait.js draws: a polygon with a fill, or a line with a stroke
  assert.equal((svg.match(/<(polygon|polyline|line)\b/g) || []).length, shapes.length);
  for (const x of shapes) assert.ok(x.kind === "fill" ? x.fill : x.stroke, JSON.stringify(x));
  const [bright, dim] = await Promise.all([4101, 4102].map((id) => imageOf(lvz, byId(lvz, id)[0])));
  assert.deepEqual([bright.width, bright.height], [Portrait.SIZE, Portrait.SIZE]);
  const red = (im, i) => im.rgba[i] - im.rgba[i + 1];
  let differ = 0;
  for (let i = 0; i < bright.rgba.length; i += 4) {
    if (bright.rgba[i] === dim.rgba[i] && bright.rgba[i + 1] === dim.rgba[i + 1] && bright.rgba[i + 2] === dim.rgba[i + 2]) continue;
    differ++;
    const [x, y] = [(i / 4) % Portrait.SIZE, Math.floor(i / 4 / Portrait.SIZE)];
    assert.ok(x >= 33 && x <= 63 && y >= 38 && y <= 48, `frames differ outside the eyes at ${x},${y}`);
    assert.ok(red(bright, i) > red(dim, i), `the bright frame is redder at ${x},${y}`);
  }
  assert.ok(differ > 20, `${differ} eye pixels change`);
});

test("the box, popups, portrait and lines use only colours Continuum shows (black is see-through)", async () => {
  const lvz = await model();
  const black565 = (im, i) => (im.rgba[i] >> 3) + (im.rgba[i + 1] >> 2) + (im.rgba[i + 2] >> 3) === 0;
  for (const id of [4100, 4101, 4102, 4110]) {
    const im = await imageOf(lvz, byId(lvz, id)[0]);
    for (let y = 0; y < im.height; y++) for (let x = 0; x < im.width; x++) {
      // only the corners the hard shadow leaves empty (top-right and bottom-left) are see-through
      const corner = id !== 4101 && id !== 4102 && ((x >= im.width - 4 && y < 4) || (x < 4 && y >= im.height - 4));
      const i = (y * im.width + x) * 4;
      if (corner) assert.equal(im.rgba[i + 3], 0, `object ${id} corner ${x},${y}`);
      else assert.ok(im.rgba[i + 3] === 255 && !black565(im, i), `object ${id} pixel ${x},${y} would show through`);
    }
  }
  // the box: the amber border and its shadow, as in the browser
  const box = await imageOf(lvz, byId(lvz, 4100)[0]);
  const at = (x, y) => [...box.rgba.subarray((y * box.width + x) * 4, (y * box.width + x) * 4 + 3)];
  assert.deepEqual(at(1, 1), Build.COL.edge);
  assert.deepEqual(at(box.width - 2, box.height - 2), Build.COL.shadow);
});

test("keys sit on amber keycaps in the Continuum lines", async () => {
  const lvz = await model();
  assert.deepEqual(Build.keySpans("Press {gun} now", { gun: "Ctrl" }), [{ row: 0, from: 6, to: 10 }]);
  assert.deepEqual(Build.keySpans("{nope} and plain", { gun: "Ctrl" }), []);
  // a key keeps its place through the wrap
  const long = `${"x".repeat(40)} then {repel}!`;
  assert.deepEqual(Build.layoutLine(long, script.continuumKeys), ["x".repeat(40) + " then", "Shift+Ctrl!"]);
  assert.deepEqual(Build.keySpans(long, script.continuumKeys), [{ row: 1, from: 0, to: 10 }]);
  // in the built image: the keycap's amber behind the key, dark keycap text on it
  const l = lines.find((x) => x.text.startsWith("{repel}"));
  const im = await imageOf(lvz, byId(lvz, 5000 + l.index)[0]);
  const top = Build.LINE_H - Build.TEXT_H; // the first row, below the step's row
  const colours = new Set();
  for (let y = top; y < top + Font.H; y++) for (let x = 0; x < 10 * Font.W; x++) colours.add(im.rgba.subarray((y * im.width + x) * 4, (y * im.width + x) * 4 + 3).join());
  assert.ok(colours.has(Build.COL.keycap.join()) && colours.has(Build.COL.keycapText.join()), [...colours].join(" "));
  // ...and the step it is said in, at the right of the row above
  assert.ok(im.rgba.subarray(0, im.width * 4 * Font.H).some((v, i) => i % 4 === 3 && v), "step number drawn");
});

test("the map has the training features and matches the checked-in .lvl", () => {
  global.window ??= globalThis;
  require(path.join(ROOT, "src/Dare.Arenas/Web/wwwroot/lvl.js"));
  const grid = TrainingMap.build();
  const disk = window.Lvl.parse(new Uint8Array(fs.readFileSync(path.join(ARENA, "training.lvl")))).grid;
  assert.deepEqual(disk, grid, "training.lvl is stale: run node tools/tutorial-lvz/build.js");
  const at = (x, y) => grid[y * 1024 + x];
  const { lo, hi } = TrainingMap.BOUNDS;
  assert.ok(hi - lo >= 180 && hi - lo <= 220);
  for (let i = lo; i <= hi; i++) for (const [x, y] of [[i, lo], [i, hi], [lo, i], [hi, i]]) assert.ok(at(x, y) >= 1 && at(x, y) <= 161, `border ${x},${y}`);
  let tiles = 0;
  grid.forEach((t, i) => { if (t) { tiles++; const x = i % 1024, y = (i / 1024) | 0; assert.ok(x >= lo && x <= hi && y >= lo && y <= hi, `tile outside ${x},${y}`); } });
  assert.ok(tiles > 0);
  const S = TrainingMap.SPOTS;
  const open = (p, r) => { for (let y = p.y - r; y <= p.y + r; y++) for (let x = p.x - r; x <= p.x + r; x++) if (at(x, y) && at(x, y) !== 171) return false; return true; };
  for (const k of ["start", "beacon", "flag", "ball", "green"]) assert.ok(open(S[k], 3), `${k} is in open space`);
  assert.equal(at(S.safe.x, S.safe.y), 171);
  assert.ok(grid.some((t) => t === 172), "a goal");
  assert.ok(!grid.some((t) => t === 170), "the flag is a carried flag (Flag:SpawnX/Y), not a turf tile");
  // the ring: walls all round the beacon at its radius, with gaps to fly in
  const ringWalls = [...Array(360).keys()].filter((a) => {
    const r = (a * Math.PI) / 180;
    return at(Math.round(S.beacon.x + S.ringRadius * Math.cos(r)), Math.round(S.beacon.y + S.ringRadius * Math.sin(r)));
  }).length;
  assert.ok(ringWalls > 200 && ringWalls < 350, `ring walls ${ringWalls}`);
});

test("script coordinates and arena.conf point at the map's spots", () => {
  const S = TrainingMap.SPOTS;
  const fly = script.steps.find((s) => s.id === "fly").check;
  assert.deepEqual([fly.x, fly.y], [S.beacon.x, S.beacon.y]);
  for (const s of script.steps) for (const c of [...(s.cast || []), ...(s.leanCast || [])]) {
    for (const m of c.script.matchAll(/^point (\d+) (\d+)$/gm)) {
      const [x, y] = [+m[1], +m[2]];
      assert.ok(Math.hypot(x - S.beacon.x, y - S.beacon.y) < S.ringRadius - 2, `${s.id}: point ${x} ${y} is inside the ring`);
    }
  }
  // warps (the player's at a step, each bot's as it takes its seat) land on open tiles inside the ring, where the
  // swarm brain (no pathfinding) can reach the player
  const grid = TrainingMap.build();
  const spots = [];
  for (const s of script.steps) {
    if (s.warpPlayer !== undefined) spots.push([`${s.id}: warpPlayer`, s.warpPlayer]);
    for (const c of [...(s.cast || []), ...(s.leanCast || [])]) if (c.at !== undefined) spots.push([`${s.id}: ${c.role} at`, c.at]);
  }
  assert.ok(spots.length >= 14, `${spots.length} warp spots`);
  for (const [what, at] of spots) {
    const m = /^(\d+) (\d+)$/.exec(at);
    assert.ok(m, `${what} "${at}" is "x y"`);
    const [x, y] = [+m[1], +m[2]];
    assert.ok(Math.hypot(x - S.beacon.x, y - S.beacon.y) < S.ringRadius - 2, `${what} ${x} ${y} is inside the ring`);
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++)
      assert.equal(grid[(y + dy) * 1024 + x + dx], 0, `${what} ${x} ${y} is open`);
  }
  // the bots the pilot shoots fly Javelins, the ship arena.conf makes weak (Warbirds have 3000 energy here)
  for (const s of script.steps) for (const c of [...(s.cast || []), ...(s.leanCast || [])])
    if (c.role !== "teammate") assert.match(c.script, /^ship 2$/m, `${s.id}: ${c.role} flies a Javelin`);
  const conf = fs.readFileSync(path.join(ARENA, "arena.conf"), "utf8");
  const get = (sec, key) => {
    let cur = "";
    for (const raw of conf.split(/\r?\n/)) {
      const line = raw.replace(/;.*$/, "").trim();
      const m = /^\[\s*(.+?)\s*\]$/.exec(line);
      if (m) cur = m[1].toLowerCase();
      else if (cur === sec.toLowerCase()) { const kv = /^([^=]+?)\s*=\s*(.*)$/.exec(line); if (kv && kv[1].toLowerCase() === key.toLowerCase()) return kv[2]; }
    }
    return undefined;
  };
  assert.match(conf, /^#include conf\/svs\/svs\.conf$/m);
  assert.equal(get("General", "Map"), "training.lvl");
  assert.equal(get("General", "LevelFiles"), "tutorial.lvz");
  assert.deepEqual([+get("Spawn", "Team0-X"), +get("Spawn", "Team0-Y")], [S.start.x, S.start.y]);
  assert.deepEqual([+get("Flag", "SpawnX"), +get("Flag", "SpawnY")], [S.flag.x, S.flag.y]);
  assert.deepEqual([+get("Soccer", "SpawnX"), +get("Soccer", "SpawnY")], [S.ball.x, S.ball.y]);
  assert.equal(+get("Flag", "FlagCount"), 1);
  assert.equal(+get("Soccer", "BallCount"), 1);
  assert.ok(+get("Warbird", "InitialEnergy") >= 3000 && +get("Warbird", "MaximumEnergy") >= 3000);
  assert.ok(+get("Bullet", "BulletDamageLevel") > 0);
  assert.ok(+get("Javelin", "MaximumEnergy") < 500);
  assert.equal(+get("Prize", "EngineShutdownTime"), 32767);
  // greens appear around the map centre (Prize:MinimumVirtual tiles): that is the green field
  assert.ok(Math.hypot(S.green.x - 512, S.green.y - 512) <= 2 && +get("Prize", "MinimumVirtual") <= S.greenRadius);
  assert.ok(+get("Team", "MaxFrequency") < +get("Team", "PrivFreqStart"), "no private freqs");
  assert.equal(+get("Kill", "FixedKillReward"), 0);
});
