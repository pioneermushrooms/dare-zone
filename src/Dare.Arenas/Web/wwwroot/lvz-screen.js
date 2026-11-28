// LVZ on the screen: a mimic game screen for placing LVZ screen objects, shared by the map editor's
// LVZ panel (lvz-layer.js) and the game mode wizard's screen designer.
//
// Geometry (no DOM; node-tested): the twelve CLV2 reference points as our client places them, ported
// from oss-client crates/oss-client/src/game/hud.rs screen_refs() (measured against Continuum at
// 1888x1016; the icon columns' y is exact only at that height, chat moves with its line count).
// A screen object sits at (anchor[xRef].x + x, anchor[yRef].y + y), its image's top-left corner.
// Placing works like a design tool: smart guides (guides(), snapBox()) line an image up with the screen's
// centre and edges, the HUD's blocks and other images' edges and centres, or space it evenly; otherwise
// it settles on whole pixels or a small grid. The reference point is picked for it afterwards
// (refsFor()): the HUD part it was lined up with, else the nearest screen edge or centre by thirds, so
// it stays put at other window sizes. The view shows that only as a hint.
//
// Browser: LvzScreen.mount(host, hooks) puts a screen view over `host` (position: relative) and handles
// drawing, click-to-place, drag, Alt+drag copy, drop from a palette (HTML drag and drop with the type
// "application/x-lvz-image", or view.dropAt(image, clientX, clientY) from code), Del, arrows.
//   hooks: lvz() -> the model (lvz.js), pics() -> Lvz.pictures(...), image() -> image index to place
//   on a click (or -1), editable(o) -> bool (default every screen object), make(fields) -> a new object
//   (default Lvz.newObject), changed(), selected() -> index, select(i), say(text, ok).
// Text goes in with textContent only.
"use strict";

(function (root, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.LvzScreen = api;
})(typeof self !== "undefined" ? self : this, function () {
  const RADAR_MARGIN = 6;
  const ICONS_TOP_MEASURED_SHIFT = 20;
  const SPECIALS_RIGHT = 26, ICON_PITCH = 25;
  const OFFSET_MIN = -2048, OFFSET_MAX = 2047; // CLV2 screen offsets are signed 12-bit
  const PRESETS = [[640, 480], [800, 600], [1024, 768], [1280, 720], [1600, 900], [1920, 1080]];
  const STATBOX_OPEN = [109, 29]; // the player list's corner in its default view (hud.rs test)

  const iconsTop = (h) => Math.floor(h * 0.57 + 1 - 100) - ICONS_TOP_MEASURED_SHIFT;
  const radarDim = (w) => Math.floor(Math.floor(Math.floor(w) / 6 / 4) * 8 / 2);
  const chatRows = (h) => Math.floor(Math.max(0, h) / 100) + 1;

  /**
   * The HUD's parts at a w x h view: where the mock draws them, and where the anchors come from.
   * opts: { statbox: [x, y] or null (list closed), spectating, radarLine, chatRows, chatTop }
   */
  function hud(w, h, opts = {}) {
    const top = iconsTop(h), dim = radarDim(w);
    const rx = w - dim - RADAR_MARGIN, ry = h - dim - RADAR_MARGIN;
    const rows = opts.chatRows === undefined ? chatRows(h) : opts.chatRows;
    const line = opts.radarLine === undefined ? "4:53pm  A1" : opts.radarLine;
    const statbox = opts.statbox === undefined ? STATBOX_OPEN : opts.statbox;
    return {
      w, h,
      energy: opts.spectating ? null : { x: Math.floor(w / 2) - 240, y: 4, w: 480, h: 20 },
      belowEnergy: opts.spectating ? 12 : 28,
      radar: { x: rx, y: ry, dim },
      radarText: { x: rx + dim - 8 * line.length, y: ry - 16, text: line },
      statbox: statbox && (statbox[0] || statbox[1]) ? { x: 0, y: 0, w: statbox[0] - 3, h: statbox[1], corner: statbox } : null,
      specials: { x: 0, y: top, w: SPECIALS_RIGHT, rows: 7, pitch: ICON_PITCH },
      weapons: { x: w - SPECIALS_RIGHT, y: top, w: SPECIALS_RIGHT, rows: 6, pitch: ICON_PITCH },
      chat: { y: opts.chatTop === undefined ? h - 16 - rows * 12 : opts.chatTop, rows },
    };
  }

  /** The twelve reference points [x, y], in Lvz.REFS order, as hud.rs screen_refs() puts them. */
  function anchors(w, h, opts = {}) {
    const g = hud(w, h, opts);
    const cx = Math.floor(w / 2), cy = Math.floor(h / 2);
    return [
      [0, 0],
      [cx, cy],
      [w, h],
      g.statbox ? g.statbox.corner.slice() : [0, 0],
      [SPECIALS_RIGHT, g.specials.y],
      [SPECIALS_RIGHT, g.specials.y + 7 * ICON_PITCH],
      [cx, g.belowEnergy],
      [0, g.chat.y],
      [g.radar.x, g.radar.y],
      [g.radarText.x, g.radarText.y],
      [w - SPECIALS_RIGHT, g.weapons.y],
      [w - SPECIALS_RIGHT, g.weapons.y + 6 * ICON_PITCH],
    ];
  }

  /** Where a screen object's image top-left lands. */
  function place(o, refs) {
    const ax = refs[o.xRef] || refs[0], ay = refs[o.yRef] || refs[0];
    return [ax[0] + o.x, ay[1] + o.y];
  }

  const clampOff = (v) => Math.max(OFFSET_MIN, Math.min(OFFSET_MAX, Math.round(v)));
  // the nine handle points of a w x h box at (x, y): corners, edge middles, centre
  const handles = (x, y, w, h) => [0, 0.5, 1].flatMap((fy) => [0, 0.5, 1].map((fx) => [x + w * fx, y + h * fy]));

  /**
   * Anchors a box whose top-left is at (x, y), size w x h, to the reference points.
   * With snap (a radius > 0): when one of the box's handles (corners, edge middles, centre) is within
   * the radius of a reference point, the box moves so that handle sits exactly on it, anchored there.
   * Otherwise it stays put, each axis anchored to the nearest screen edge or centre.
   * Returns { xRef, yRef, x, y, snapped } (x, y: offsets, clamped to CLV2's signed 12 bits).
   */
  function anchorBox(x, y, w, h, refs, radius = 0) {
    let best = null;
    if (radius > 0) {
      handles(x, y, w, h).forEach(([hx, hy], k) => {
        refs.forEach(([rx, ry], r) => {
          const d = Math.hypot(hx - rx, hy - ry);
          if (d <= radius && (!best || d < best.d)) best = { d, r, dx: rx - hx, dy: ry - hy, k };
        });
      });
    }
    if (best) { x += best.dx; y += best.dy; }
    if (best) return { xRef: best.r, yRef: best.r, x: clampOff(x - refs[best.r][0]), y: clampOff(y - refs[best.r][1]), snapped: true };
    // free: each axis from the steady points only (screen edge, centre, far edge), whichever is nearest
    // the box's centre on that axis, so it doesn't follow a HUD part that moves (the player list, chat)
    const steady = [0, 1, 2].filter((i) => refs[i]);
    const near = (k, c) => steady.reduce((b, i) => (Math.abs(c - refs[i][k]) < Math.abs(c - refs[b][k]) ? i : b), steady[0]);
    const xRef = near(0, x + w / 2), yRef = near(1, y + h / 2);
    return { xRef, yRef, x: clampOff(x - refs[xRef][0]), y: clampOff(y - refs[yRef][1]), snapped: false };
  }

  /**
   * The lines an image can line up with: the screen's edges and centre, and the HUD's blocks (energy bar,
   * radar, radar text, player list, item columns, chat). Each { v, label, ref? }: ref is the reference
   * point a line belongs to, when the game measures from it, so an image lined up there follows that part
   * of the HUD at other window sizes.
   */
  function guides(w, h, opts = {}) {
    const g = hud(w, h, opts), xs = [], ys = [];
    const X = (v, label, ref) => xs.push({ v, label, ref }), Y = (v, label, ref) => ys.push({ v, label, ref });
    X(0, "screen left", 0); X(Math.floor(w / 2), "screen centre", 1); X(w, "screen right", 2);
    Y(0, "screen top", 0); Y(Math.floor(h / 2), "screen middle", 1); Y(h, "screen bottom", 2);
    if (g.energy) { X(g.energy.x, "energy bar left"); X(g.energy.x + g.energy.w, "energy bar right"); Y(g.belowEnergy, "below the energy bar", 6); }
    X(g.radar.x, "radar left", 8); X(g.radar.x + g.radar.dim, "radar right"); X(g.radar.x + g.radar.dim / 2, "radar centre");
    Y(g.radar.y, "radar top", 8); Y(g.radar.y + g.radar.dim / 2, "radar middle");
    X(g.radarText.x, "radar text left", 9); Y(g.radarText.y, "radar text top", 9);
    if (g.statbox) { X(g.statbox.corner[0], "player list right", 3); Y(g.statbox.corner[1], "player list bottom", 3); }
    X(SPECIALS_RIGHT, "left item column", 4); Y(g.specials.y, "item columns top", 4); Y(g.specials.y + 7 * ICON_PITCH, "left item column bottom", 5);
    X(w - SPECIALS_RIGHT, "right item column", 10); Y(g.weapons.y + 6 * ICON_PITCH, "right item column bottom", 11);
    Y(g.chat.y, "chat top", 7);
    return { xs, ys };
  }

  const overlap = (a0, a1, b0, b1) => Math.min(a1, b1) - Math.max(a0, b0) > 0;
  // one axis: k = 0 for x (positions x, sizes w), 1 for y
  function snapAxis(k, pos, size, crossPos, crossSize, lines, others, threshold) {
    let best = null;
    const tryAt = (target, dist, info) => { if (dist <= threshold && (!best || dist < best.d)) best = Object.assign({ d: dist, target }, info); };
    const pts = [0, size / 2, size];
    for (const ln of lines) pts.forEach((p, i) => tryAt(ln.v - p, Math.abs(pos + p - ln.v), { line: ln, at: i }));
    const P = (o) => (k ? o.y : o.x), S = (o) => (k ? o.h : o.w), CP = (o) => (k ? o.x : o.y), CS = (o) => (k ? o.w : o.h);
    for (const o of others) {
      [P(o), P(o) + S(o) / 2, P(o) + S(o)].forEach((v, j) => pts.forEach((p, i) => tryAt(v - p, Math.abs(pos + p - v), { line: { v, label: j === 1 ? "image centre" : "image edge", image: true }, at: i })));
    }
    // equal spacing, among the images in the same row (column for y)
    const row = others.filter((o) => overlap(CP(o), CP(o) + CS(o), crossPos, crossPos + crossSize)).sort((a, b) => P(a) - P(b));
    const before = row.filter((o) => P(o) + S(o) <= pos + size / 2).filter((o) => P(o) + S(o) <= pos + threshold);
    const after = row.filter((o) => P(o) >= pos + size / 2 - threshold && P(o) >= pos + size - threshold);
    const L1 = before[before.length - 1], R1 = after[0];
    if (L1 && R1) { // centred between two
      const t = (P(L1) + S(L1) + P(R1) - size) / 2;
      tryAt(t, Math.abs(pos - t), { gap: { a: P(L1) + S(L1), b: P(R1), size, t } });
    }
    const L2 = before[before.length - 2];
    if (L1 && L2) { const gap = P(L1) - (P(L2) + S(L2)); if (gap > 0) { const t = P(L1) + S(L1) + gap; tryAt(t, Math.abs(pos - t), { chain: [[P(L2) + S(L2), P(L1)], [P(L1) + S(L1), t]] }); } }
    const R2 = after[1];
    if (R1 && R2) { const gap = P(R2) - (P(R1) + S(R1)); if (gap > 0) { const t = P(R1) - gap - size; tryAt(t, Math.abs(pos - t), { chain: [[t + size, P(R1)], [P(R1) + S(R1), P(R2)]] }); } }
    return best;
  }

  /**
   * Where a dragged w x h image at (x, y) settles: lined up with a guide line or another image's edge or
   * centre, or spaced evenly between images, when within `threshold` px (each axis on its own); else on
   * the grid. Whole pixels always. opts: { xs, ys } (guides()), others [{ x, y, w, h }], threshold, grid.
   * Returns { x, y, gx, gy }: gx/gy describe what it snapped to on that axis (null: the grid).
   */
  function snapBox(x, y, w, h, opts = {}) {
    const th = opts.threshold === undefined ? 6 : opts.threshold, grid = Math.max(1, opts.grid || 1), others = opts.others || [];
    const gx = th > 0 ? snapAxis(0, x, w, y, h, opts.xs || [], others, th) : null;
    const gy = th > 0 ? snapAxis(1, y, h, gx ? gx.target : x, w, opts.ys || [], others, th) : null;
    const onGrid = (v) => Math.round(v / grid) * grid;
    return { x: Math.round(gx ? gx.target : onGrid(x)), y: Math.round(gy ? gy.target : onGrid(y)), gx, gy };
  }

  /**
   * The reference points a placed image is measured from, picked so it stays put at other window sizes:
   * the one of the HUD line it was lined up with (it then follows that part of the HUD), else, per axis,
   * the nearest of the screen's start, centre or end by thirds. Returns { xRef, yRef, x, y } (offsets).
   */
  function refsFor(x, y, w, h, refs, W, H, snap = {}) {
    const third = (c, n) => (c < n / 3 ? 0 : c > (2 * n) / 3 ? 2 : 1);
    const xr = snap.gx && snap.gx.line && Number.isInteger(snap.gx.line.ref) ? snap.gx.line.ref : third(x + w / 2, W);
    const yr = snap.gy && snap.gy.line && Number.isInteger(snap.gy.line.ref) ? snap.gy.line.ref : third(y + h / 2, H);
    return { xRef: xr, yRef: yr, x: clampOff(x - refs[xr][0]), y: clampOff(y - refs[yr][1]) };
  }

  /** Draws the mock HUD (faint) and, with labels, the reference points, on ctx at scale z. */
  function drawHud(ctx, g, refs, z, opts = {}) {
    const S = (v) => v * z;
    ctx.save();
    ctx.fillStyle = "#05070c"; ctx.fillRect(0, 0, S(g.w), S(g.h));
    // a faint starfield so it reads as the game
    ctx.fillStyle = "#ffffff22";
    for (let i = 0; i < 160; i++) { const sx = (i * 7919) % g.w, sy = (i * 104729) % g.h; ctx.fillRect(S(sx), S(sy), Math.max(1, z), Math.max(1, z)); }
    ctx.strokeStyle = "#6b86a855"; ctx.fillStyle = "#6b86a822"; ctx.lineWidth = 1;
    const box = (x, y, w, h) => { ctx.fillRect(S(x), S(y), S(w), S(h)); ctx.strokeRect(S(x) + 0.5, S(y) + 0.5, S(w) - 1, S(h) - 1); };
    if (g.energy) box(g.energy.x, g.energy.y, g.energy.w, g.energy.h);
    box(g.radar.x, g.radar.y, g.radar.dim, g.radar.dim);
    if (g.statbox) box(g.statbox.x, g.statbox.y, g.statbox.w, g.statbox.h);
    for (let i = 0; i < g.specials.rows; i++) box(g.specials.x, g.specials.y + i * g.specials.pitch, g.specials.w, g.specials.pitch - 3);
    for (let i = 0; i < g.weapons.rows; i++) box(g.weapons.x, g.weapons.y + i * g.weapons.pitch, g.weapons.w, g.weapons.pitch - 3);
    ctx.font = `${Math.max(10, Math.round(11 * Math.min(1, z * 1.4)))}px monospace`;
    ctx.fillStyle = "#6b86a8aa"; ctx.textBaseline = "top";
    ctx.fillText(g.radarText.text, S(g.radarText.x), S(g.radarText.y));
    for (let i = 0; i < Math.min(g.chat.rows, 4); i++) ctx.fillText(i === 0 ? "Pilot> hello" : "", S(4), S(g.chat.y + i * 12));
    // reference points
    if (opts.refs !== false) refs.forEach(([x, y], i) => {
      const hot = opts.hot === i;
      ctx.fillStyle = hot ? "#ffcf7a" : "#7fd6ff";
      ctx.beginPath(); ctx.arc(Math.min(S(g.w) - 4, Math.max(4, S(x))), Math.min(S(g.h) - 4, Math.max(4, S(y))), hot ? 5 : 3.5, 0, Math.PI * 2); ctx.fill();
      if (opts.labels && opts.labels[i]) {
        ctx.fillStyle = hot ? "#ffcf7a" : "#7fd6ffaa";
        const tx = Math.min(S(g.w) - 4 - ctx.measureText(opts.labels[i]).width, Math.max(6, S(x) + 6)), ty = Math.min(S(g.h) - 14, Math.max(2, S(y) + 4));
        ctx.fillText(opts.labels[i], tx, ty);
      }
    });
    ctx.restore();
  }

  // ---- browser: the screen view ----------------------------------------------------------------------
  function mount(host, hooks) {
    const Lvz = (typeof window !== "undefined" && window.Lvz) || null;
    const el = document.createElement("div"); el.className = "lvzscreen"; el.hidden = true;
    const bar = document.createElement("div"); bar.className = "lzs-bar";
    const sizeSel = document.createElement("select"); sizeSel.setAttribute("aria-label", "Screen size");
    const stat = Object.assign(document.createElement("label"), { className: "hint" });
    const statBox = Object.assign(document.createElement("input"), { type: "checkbox", checked: true });
    stat.append(statBox, document.createTextNode(" Player list open"));
    const snapL = Object.assign(document.createElement("label"), { className: "hint", title: "Line images up with the screen's centre, the HUD and each other; equal spacing. Shift while dragging turns it off" });
    const guideBox = Object.assign(document.createElement("input"), { type: "checkbox", checked: true });
    snapL.append(guideBox, document.createTextNode(" Smart guides"));
    const gridSel = document.createElement("select"); gridSel.setAttribute("aria-label", "Grid"); gridSel.title = "Where images settle when no guide is near";
    for (const g of [1, 2, 4, 8, 16]) gridSel.append(new Option(g === 1 ? "Whole pixels" : `${g} px grid`, String(g)));
    const note = Object.assign(document.createElement("span"), { className: "hint lzs-note" });
    const extra = document.createElement("span"); extra.className = "lzs-extra";
    bar.append(sizeSel, stat, snapL, gridSel, extra, note);
    const cv = document.createElement("canvas"); cv.className = "lzs-canvas"; cv.tabIndex = 0;
    cv.setAttribute("aria-label", "The game screen: drop or click to place images; they keep their place at every window size");
    el.append(bar, cv); host.append(el);

    const own = () => [Math.max(320, Math.round(window.innerWidth)), Math.max(240, Math.round(window.innerHeight))];
    function fillSizes() {
      const [w, h] = own(), keep = sizeSel.value;
      sizeSel.replaceChildren(new Option(`Your window (${w} × ${h})`, "own"), ...PRESETS.map(([a, b]) => new Option(`${a} × ${b}`, `${a}x${b}`)));
      sizeSel.value = keep && [...sizeSel.options].some((o) => o.value === keep) ? keep : "own";
    }
    fillSizes();
    const size = () => (sizeSel.value === "own" ? own() : sizeSel.value.split("x").map(Number));
    const opts = () => ({ statbox: statBox.checked ? STATBOX_OPEN : null });
    let z = 1, drag = null, hot = -1, animating = false, live = null, view_hint = null; // live: the guides shown while dragging
    const labels = (Lvz ? Lvz.REFS : []).map((r) => r[1]);
    const editable = (o) => !o.map && (!hooks.editable || hooks.editable(o));
    const lvz = () => hooks.lvz && hooks.lvz();
    const pics = () => (hooks.pics && hooks.pics()) || [];
    const boxOf = (o) => { const p = pics()[o.image]; return p ? [p.fw, p.fh] : [32, 32]; };

    function layout() {
      const [w, h] = size(), r = host.getBoundingClientRect(), barH = bar.offsetHeight || 30;
      const availW = Math.max(100, r.width - 16), availH = Math.max(80, r.height - barH - 16);
      z = Math.min(availW / w, availH / h, 2);
      const dpr = window.devicePixelRatio || 1;
      cv.style.width = Math.round(w * z) + "px"; cv.style.height = Math.round(h * z) + "px";
      cv.width = Math.round(w * z * dpr); cv.height = Math.round(h * z * dpr);
      draw();
    }
    function draw() {
      if (el.hidden) return;
      const [w, h] = size(), refs = anchors(w, h, opts()), g = hud(w, h, opts());
      const dpr = window.devicePixelRatio || 1, ctx = cv.getContext("2d"), zz = z * dpr;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      drawHud(ctx, g, refs, zz, { refs: false });
      const m = lvz(); if (!m) return;
      const t = performance.now(); let anim = false;
      const show = !hooks.visible || hooks.visible();
      const list = m.objects.map((o, i) => [o, i]).filter(([o]) => !o.map).sort((a, b) => a[0].layer - b[0].layer);
      const sel = hooks.selected ? hooks.selected() : -1;
      ctx.imageSmoothingEnabled = false;
      for (const [o, i] of list) {
        const [x, y] = place(o, refs), [fw, fh] = boxOf(o), im = m.images[o.image];
        if (show && Lvz) {
          if (im && (im.cols || 1) * (im.rows || 1) > 1) anim = true;
          Lvz.drawFrame(ctx, pics()[o.image], im || {}, im ? Lvz.frameAt(im, t) : 0, x * zz, y * zz, zz, im ? im.file : "?");
        }
        ctx.strokeStyle = i === sel ? "#ffffff" : !editable(o) ? "#c084fc99" : "#ffcf7a66"; ctx.lineWidth = i === sel ? 2 : 1;
        ctx.strokeRect(x * zz - 0.5, y * zz - 0.5, fw * zz + 1, fh * zz + 1);
        if (i === sel && !drag) { // a small pin where the game measures it from (a hint, not a handle)
          const a = refs[o.xRef] || refs[0], b = refs[o.yRef] || refs[0];
          ctx.fillStyle = "#ffcf7a"; ctx.beginPath(); ctx.arc(Math.min(w * zz - 3, Math.max(3, a[0] * zz)), Math.min(h * zz - 3, Math.max(3, b[1] * zz)), 3, 0, Math.PI * 2); ctx.fill();
        }
      }
      if (live) drawGuides(ctx, live, w, h, zz);
      if (anim && !animating) { animating = true; setTimeout(() => { animating = false; draw(); }, 100); }
    }
    function drawGuides(ctx, L, w, h, zz) {
      ctx.save(); ctx.strokeStyle = "#ff4fd8"; ctx.fillStyle = "#ff4fd8"; ctx.lineWidth = 1;
      const vline = (v) => { ctx.beginPath(); ctx.moveTo(Math.round(v * zz) + 0.5, 0); ctx.lineTo(Math.round(v * zz) + 0.5, h * zz); ctx.stroke(); };
      const hline = (v) => { ctx.beginPath(); ctx.moveTo(0, Math.round(v * zz) + 0.5); ctx.lineTo(w * zz, Math.round(v * zz) + 0.5); ctx.stroke(); };
      const seg = (k, a, b, at) => { // a spacing marker: |<-->| along axis k at cross position `at`
        ctx.beginPath();
        if (!k) { ctx.moveTo(a * zz, at * zz); ctx.lineTo(b * zz, at * zz); ctx.moveTo(a * zz, at * zz - 4); ctx.lineTo(a * zz, at * zz + 4); ctx.moveTo(b * zz, at * zz - 4); ctx.lineTo(b * zz, at * zz + 4); }
        else { ctx.moveTo(at * zz, a * zz); ctx.lineTo(at * zz, b * zz); ctx.moveTo(at * zz - 4, a * zz); ctx.lineTo(at * zz + 4, a * zz); ctx.moveTo(at * zz - 4, b * zz); ctx.lineTo(at * zz + 4, b * zz); }
        ctx.stroke();
      };
      [[L.gx, 0], [L.gy, 1]].forEach(([g, k]) => {
        if (!g) return;
        const cross = k ? L.x + L.w / 2 : L.y + L.h / 2;
        if (g.line) (k ? hline : vline)(g.line.v);
        if (g.gap) { seg(k, g.gap.a, g.gap.t, cross); seg(k, g.gap.t + g.gap.size, g.gap.b, cross); }
        if (g.chain) for (const [a, b] of g.chain) seg(k, a, b, cross);
      });
      ctx.restore();
    }
    function screenPx(clientX, clientY) { const r = cv.getBoundingClientRect(); return [(clientX - r.left) / z, (clientY - r.top) / z]; }
    function hit(px, py) {
      const m = lvz(); if (!m) return -1;
      const refs = anchors(...size(), opts());
      const list = m.objects.map((o, i) => [o, i]).filter(([o]) => !o.map).sort((a, b) => b[0].layer - a[0].layer || b[1] - a[1]);
      for (const [o, i] of list) { const [x, y] = place(o, refs), [fw, fh] = boxOf(o); if (px >= x && py >= y && px < x + fw && py < y + fh) return i; }
      return -1;
    }
    // guides within 6 screen pixels (in game pixels at this zoom); Shift or "Smart guides" off: the grid only
    const threshold = (e) => (guideBox.checked && !(e && e.shiftKey) ? Math.max(2, 6 / z) : 0);
    const AXIS = ["left", "centre", "right"], AYIS = ["top", "middle", "bottom"];
    function hint(o) {
      const n = (i, k) => (i <= 2 ? (k ? AYIS : AXIS)[i] : (labels[i] || "").toLowerCase());
      return `Stays put from the ${n(o.xRef, 0)}${o.yRef !== o.xRef || o.xRef > 2 ? ` / ${n(o.yRef, 1)}` : ""} at any window size`;
    }
    function setPos(o, x, y, e, self) {
      const m = lvz(), [fw, fh] = boxOf(o), [W, H] = size(), refs = anchors(W, H, opts()), gl = guides(W, H, opts());
      const others = m ? m.objects.filter((p) => !p.map && p !== o && p !== self).map((p) => { const [px, py] = place(p, refs), [pw, ph] = boxOf(p); return { x: px, y: py, w: pw, h: ph }; }) : [];
      const sn = snapBox(x, y, fw, fh, { xs: gl.xs, ys: gl.ys, others, threshold: threshold(e), grid: +gridSel.value });
      const a = refsFor(sn.x, sn.y, fw, fh, refs, W, H, sn);
      Object.assign(o, { xRef: a.xRef, yRef: a.yRef, x: a.x, y: a.y });
      live = { gx: sn.gx, gy: sn.gy, x: sn.x, y: sn.y, w: fw, h: fh };
      const what = [sn.gx, sn.gy].filter(Boolean).map((g) => (g.line ? g.line.label : "equal spacing"));
      note.textContent = `${sn.x}, ${sn.y}${what.length ? ` · lined up with ${[...new Set(what)].join(" and ")}` : ""}`;
    }
    function add(image, px, py, e) {
      const m = lvz(); if (!m || !m.images[image]) return -1;
      const p = pics()[image], fw = p ? p.fw : 32, fh = p ? p.fh : 32;
      const o = hooks.make ? hooks.make({ map: false, image, layer: 6 }) : Lvz.newObject({ map: false, image, layer: 6, id: Lvz.nextId(m) });
      setPos(o, px - fw / 2, py - fh / 2, e);
      m.objects.push(o);
      const i = m.objects.length - 1;
      if (hooks.select) hooks.select(i);
      if (hooks.changed) hooks.changed();
      return i;
    }
    cv.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      cv.focus(); e.preventDefault();
      const m = lvz(); if (!m) return;
      const [px, py] = screenPx(e.clientX, e.clientY);
      let i = hit(px, py);
      if (i >= 0 && !editable(m.objects[i])) { if (hooks.select) hooks.select(i); draw(); return; }
      if (i >= 0 && e.altKey) { const c = Object.assign({}, m.objects[i], { id: hooks.make ? hooks.make({}).id : Lvz.nextId(m) }); delete c.fromClv1; m.objects.push(c); i = m.objects.length - 1; if (hooks.changed) hooks.changed(); }
      if (i < 0) {
        const img = hooks.image ? hooks.image() : -1;
        if (img < 0 || !m.images[img]) { if (hooks.say) hooks.say("Select an image, then click the screen (or drag it here).", false); return; }
        i = add(img, px, py, e);
      }
      if (i < 0) return;
      try { cv.setPointerCapture(e.pointerId); } catch { /* a pointer the browser doesn't track */ }
      const refs = anchors(...size(), opts()), [x, y] = place(m.objects[i], refs);
      drag = { i, sx: px, sy: py, ox: x, oy: y };
      if (hooks.select) hooks.select(i);
      draw();
    });
    cv.addEventListener("pointermove", (e) => {
      if (!drag) return;
      const m = lvz(), o = m && m.objects[drag.i]; if (!o) return;
      const [px, py] = screenPx(e.clientX, e.clientY);
      setPos(o, drag.ox + px - drag.sx, drag.oy + py - drag.sy, e);
      if (hooks.changed) hooks.changed(true);
      draw();
    });
    const up = () => {
      if (!drag) return;
      drag = null; hot = -1; live = null;
      const m = lvz(), o = m && m.objects[hooks.selected ? hooks.selected() : -1];
      if (o && !o.map) note.textContent = hint(o);
      if (hooks.changed) hooks.changed(); draw();
    };
    cv.addEventListener("pointerup", up); cv.addEventListener("pointercancel", up);
    cv.addEventListener("dragover", (e) => { if ([...e.dataTransfer.types].includes("application/x-lvz-image")) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; } });
    cv.addEventListener("drop", (e) => {
      const v = e.dataTransfer.getData("application/x-lvz-image"); if (v === "") return;
      e.preventDefault(); view.dropAt(+v, e.clientX, e.clientY, e);
    });
    cv.addEventListener("keydown", (e) => {
      const m = lvz(), i = hooks.selected ? hooks.selected() : -1, o = m && m.objects[i];
      if (!o || o.map || !editable(o)) return;
      const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
      if (d) {
        e.preventDefault(); e.stopPropagation();
        const k = e.shiftKey ? 10 : 1, [W, H] = size(), refs = anchors(W, H, opts()), [x, y] = place(o, refs), [fw, fh] = boxOf(o);
        const keepX = o.xRef > 2 ? { gx: { line: { ref: o.xRef } } } : {}, keepY = o.yRef > 2 ? { gy: { line: { ref: o.yRef } } } : {};
        Object.assign(o, refsFor(x + d[0] * k, y + d[1] * k, fw, fh, refs, W, H, Object.assign({}, keepX, keepY)));
        note.textContent = hint(o);
        if (hooks.changed) hooks.changed(); draw();
      }
      else if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); e.stopPropagation(); m.objects.splice(i, 1); if (hooks.select) hooks.select(-1); if (hooks.changed) hooks.changed(); draw(); }
    });
    for (const c of [sizeSel, statBox]) c.addEventListener("change", () => { layout(); });
    // the hint for the selected image, kept current as the selection changes
    view_hint = () => { const m = lvz(), o = m && m.objects[hooks.selected ? hooks.selected() : -1]; note.textContent = o && !o.map ? hint(o) : "Drag an image here; it lines up with the centre, the HUD and other images."; };
    window.addEventListener("resize", () => { if (el.hidden) return; fillSizes(); layout(); });

    const view = {
      el, bar: extra, canvas: cv,
      show(on) { el.hidden = !on; if (on) { fillSizes(); requestAnimationFrame(layout); } },
      get shown() { return !el.hidden; },
      redraw: () => { if (!drag && view_hint) view_hint(); draw(); },
      size, anchors: () => anchors(...size(), opts()),
      /** "Stays put from the right / top at any window size": which reference point, said plainly. */
      hint,
      /** Where o's top-left is at the size shown, and moving it there (the reference point re-picked). */
      position(o) { return place(o, anchors(...size(), opts())); },
      moveTo(o, x, y) {
        const [W, H] = size(), refs = anchors(W, H, opts()), [fw, fh] = boxOf(o);
        const keep = Object.assign(o.xRef > 2 ? { gx: { line: { ref: o.xRef } } } : {}, o.yRef > 2 ? { gy: { line: { ref: o.yRef } } } : {});
        Object.assign(o, refsFor(Math.round(x), Math.round(y), fw, fh, refs, W, H, keep)); draw();
      },
      /** Places image `image` with its centre under (clientX, clientY), as a drop does. */
      dropAt(image, clientX, clientY, e) {
        const [px, py] = screenPx(clientX, clientY); const i = add(image, px, py, e); live = null;
        const m = lvz(); if (i >= 0 && m) note.textContent = hint(m.objects[i]);
        draw(); return i;
      },
    };
    return view;
  }

  return { RADAR_MARGIN, PRESETS, STATBOX_OPEN, OFFSET_MIN, OFFSET_MAX, iconsTop, radarDim, chatRows, hud, anchors, place, anchorBox, guides, snapBox, refsFor, drawHud, mount };
});
