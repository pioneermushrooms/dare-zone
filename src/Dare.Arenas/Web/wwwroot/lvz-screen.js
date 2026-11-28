// LVZ on the screen: a mimic game screen for placing LVZ screen objects, shared by the map editor's
// LVZ panel (lvz-layer.js) and the game mode wizard's screen designer.
//
// Geometry (no DOM; node-tested): the twelve CLV2 reference points as our client places them, ported
// from oss-client crates/oss-client/src/game/hud.rs screen_refs() (measured against Continuum at
// 1888x1016; the icon columns' y is exact only at that height, chat moves with its line count).
// A screen object sits at (anchor[xRef].x + x, anchor[yRef].y + y), its image's top-left corner.
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
    const snapL = Object.assign(document.createElement("label"), { className: "hint", title: "Shift while dragging turns it off" });
    const snapBox = Object.assign(document.createElement("input"), { type: "checkbox", checked: true });
    snapL.append(snapBox, document.createTextNode(" Snap to the game's points"));
    const note = Object.assign(document.createElement("span"), { className: "hint lzs-note" });
    const extra = document.createElement("span"); extra.className = "lzs-extra";
    bar.append(sizeSel, stat, snapL, extra, note);
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
    let z = 1, drag = null, hot = -1, animating = false;
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
      drawHud(ctx, g, refs, zz, { labels, hot });
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
        if (i === sel) { // a line to its reference point
          const a = refs[o.xRef] || refs[0], b = refs[o.yRef] || refs[0];
          ctx.strokeStyle = "#ffcf7a99"; ctx.setLineDash([4, 3]); ctx.beginPath(); ctx.moveTo(a[0] * zz, b[1] * zz); ctx.lineTo(x * zz, y * zz); ctx.stroke(); ctx.setLineDash([]);
        }
      }
      if (anim && !animating) { animating = true; setTimeout(() => { animating = false; draw(); }, 100); }
    }
    function screenPx(clientX, clientY) { const r = cv.getBoundingClientRect(); return [(clientX - r.left) / z, (clientY - r.top) / z]; }
    function hit(px, py) {
      const m = lvz(); if (!m) return -1;
      const refs = anchors(...size(), opts());
      const list = m.objects.map((o, i) => [o, i]).filter(([o]) => !o.map).sort((a, b) => b[0].layer - a[0].layer || b[1] - a[1]);
      for (const [o, i] of list) { const [x, y] = place(o, refs), [fw, fh] = boxOf(o); if (px >= x && py >= y && px < x + fw && py < y + fh) return i; }
      return -1;
    }
    const radius = (e) => (snapBox.checked && !(e && e.shiftKey) ? 24 : 0);
    function setPos(o, x, y, e) {
      const [fw, fh] = boxOf(o), refs = anchors(...size(), opts());
      const a = anchorBox(x, y, fw, fh, refs, radius(e));
      Object.assign(o, { xRef: a.xRef, yRef: a.yRef, x: a.x, y: a.y });
      hot = a.snapped ? a.xRef : -1;
      note.textContent = `${labels[a.xRef] || "Top-left"} ${a.x >= 0 ? "+" : ""}${a.x}, ${a.y >= 0 ? "+" : ""}${a.y}`;
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
    const up = () => { if (!drag) return; drag = null; hot = -1; if (hooks.changed) hooks.changed(); draw(); };
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
      if (d) { e.preventDefault(); e.stopPropagation(); const k = e.shiftKey ? 10 : 1; o.x = clampOff(o.x + d[0] * k); o.y = clampOff(o.y + d[1] * k); if (hooks.changed) hooks.changed(); draw(); }
      else if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); e.stopPropagation(); m.objects.splice(i, 1); if (hooks.select) hooks.select(-1); if (hooks.changed) hooks.changed(); draw(); }
    });
    for (const c of [sizeSel, statBox]) c.addEventListener("change", () => { layout(); });
    window.addEventListener("resize", () => { if (el.hidden) return; fillSizes(); layout(); });

    const view = {
      el, bar: extra, canvas: cv,
      show(on) { el.hidden = !on; if (on) { fillSizes(); requestAnimationFrame(layout); } },
      get shown() { return !el.hidden; },
      redraw: draw,
      size, anchors: () => anchors(...size(), opts()),
      /** Places image `image` with its centre under (clientX, clientY), as a drop does. */
      dropAt(image, clientX, clientY, e) { const [px, py] = screenPx(clientX, clientY); const i = add(image, px, py, e); draw(); return i; },
    };
    return view;
  }

  return { RADAR_MARGIN, PRESETS, STATBOX_OPEN, OFFSET_MIN, OFFSET_MAX, iconsTop, radarDim, chatRows, hud, anchors, place, anchorBox, drawHud, mount };
});
