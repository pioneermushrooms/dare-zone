// The map editor's LVZ layer: draws an .lvz's map objects on the map at their layer (under the
// tiles for BelowAll and AfterBackground, over them otherwise) and, with "Place and move" on, lets
// the pilot place objects from an image palette, drag them, copy them (Alt+drag or Ctrl/Cmd+D) and
// delete them right on the map. The .lvz comes from the arena (?lvz=<file>), Your LVZ (?lvzlib=<id>),
// or starts empty; it is saved to Your LVZ as a draft and published to the arena from here.
// Hooks: editor.js's window.mapView and window.mapOverlays. Needs lvz.js. Text goes in with textContent only.
"use strict";

(() => {
  const mv = window.mapView, panel = document.getElementById("lvzpanel");
  if (!mv || !panel || !window.Lvz) return;
  const API = "/arena/api", HDR = { "X-Requested-With": "dare-arena" };
  const ask = (t, o) => (window.Dlg ? Dlg.confirm(t, o) : Promise.resolve(window.confirm(t)));
  const askText = (t, o) => (window.Dlg ? Dlg.prompt(t, o) : Promise.resolve(window.prompt(t, (o && o.value) || "")));
  const MODE_TEXT = ["always", "entering the zone", "entering the arena", "on a kill", "on death", "when turned on (*objon)"];

  panel.classList.add("lvzpanel");
  panel.innerHTML = `
    <p class="kicker">LVZ objects <span class="dim lzp-count"></span></p>
    <select class="lzp-src" aria-label="Which .lvz"><option value="">Pick an .lvz…</option></select>
    <label class="hint"><input type="checkbox" class="lzp-show" checked> Show LVZ objects</label>
    <label class="hint"><input type="checkbox" class="lzp-edit"> Place and move them (instead of drawing walls)</label>
    <label class="hint"><input type="checkbox" class="lzp-snap" checked> Snap to tiles</label>
    <div class="lzp-pal" aria-label="Images: pick one, then click the map to place it"></div>
    <div class="row"><label class="btn ghost small">Add image…<input type="file" class="lzp-addimg" accept=".png,.bmp,.bm2,.gif,.jpg,.jpeg" multiple hidden></label>
      <select class="lzp-layer" aria-label="Layer for new objects"></select></div>
    <div class="lzp-sel" hidden>
      <label class="lzp-f">X <input type="number" data-f="x" min="0" max="16384"></label>
      <label class="lzp-f">Y <input type="number" data-f="y" min="0" max="16384"></label>
      <label class="lzp-f">Layer <select data-f="layer"></select></label>
      <label class="lzp-f">Shown <select data-f="mode"></select></label>
      <label class="lzp-f" title="Seconds it stays up once shown; 0 = until turned off">Seconds <input type="number" data-f="time" min="0" max="409.5" step="0.1"></label>
      <label class="lzp-f" title="For *objon / *objoff">Id <input type="number" data-f="id" min="0" max="32767"></label>
      <div class="row"><button type="button" class="ghost small lzp-dup">Copy</button><button type="button" class="ghost small danger lzp-del">Delete</button></div>
    </div>
    <div class="row"><button type="button" class="small lzp-save" title="Keep it in Your LVZ (a draft)">Save .lvz</button>
      <button type="button" class="hot small lzp-pub" title="Put it on the arena; players get it when the arena restarts">Publish .lvz</button>
      <a class="btn ghost small lzp-full" href="/arena/me#lvz" title="Images, screen objects and more in the arena editor's LVZ tab">LVZ tab</a></div>
    <p class="lzp-msg" role="status"></p>
    <p class="hint">With Place and move on: pick an image and click the map to place it; drag to move, Alt+drag to copy, Del deletes, arrows nudge. Map objects don't block ships.</p>`;
  const q = (c) => panel.querySelector("." + c), F = (k) => q("lzp-sel").querySelector(`[data-f=${k}]`);
  Lvz.LAYERS.forEach((l, i) => { const t = l.replace(/([a-z])([A-Z])/g, "$1 $2"); q("lzp-layer").append(new Option(t, i)); F("layer").append(new Option(t, i)); });
  MODE_TEXT.forEach((m, i) => F("mode").append(new Option(m, i)));
  q("lzp-layer").value = 2;

  let lvz = null, pics = [], src = null, sel = -1, img = 0, dirty = false, role = null, levelFiles = [], drag = null, space = false;
  const say = (t, ok) => { const m = q("lzp-msg"); m.textContent = t || ""; m.className = "lzp-msg " + (ok ? "ok" : "bad"); };
  const setDirty = (d) => { dirty = d; q("lzp-save").textContent = d ? "Save .lvz •" : "Save .lvz"; };
  window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });

  // ---- drawing on the map ---------------------------------------------------------------------------
  const size = (o) => { const p = pics[o.image]; return p ? [p.fw, p.fh] : [32, 32]; };
  let animating = false;
  window.mapOverlays.push((phase, ctx, s, ox, oy) => {
    if (!lvz || !q("lzp-show").checked) return;
    const z = s / 16, t = performance.now(), below = phase === "below";
    let anim = false;
    const list = lvz.objects.map((o, i) => [o, i]).filter(([o]) => o.map && (o.layer <= 1) === below).sort((a, b) => a[0].layer - b[0].layer);
    for (const [o, i] of list) {
      const x = ox + o.x * z, y = oy + o.y * z, [fw, fh] = size(o);
      if (x > ctx.canvas.width || y > ctx.canvas.height || x + fw * z < 0 || y + fh * z < 0) continue;
      const im = lvz.images[o.image];
      if (im && (im.cols || 1) * (im.rows || 1) > 1) anim = true;
      ctx.imageSmoothingEnabled = false;
      Lvz.drawFrame(ctx, pics[o.image], im || {}, im ? Lvz.frameAt(im, t) : 0, x, y, z, im ? im.file : "?");
      if (q("lzp-edit").checked) { ctx.strokeStyle = i === sel ? "#ffffff" : "#ffcf7a66"; ctx.lineWidth = i === sel ? 2 : 1; ctx.strokeRect(x - 0.5, y - 0.5, fw * z + 1, fh * z + 1); }
    }
    if (!below && anim && !animating) { animating = true; setTimeout(() => { animating = false; mv.redraw(); }, 100); }
  });
  const redraw = () => mv.redraw();

  // ---- pointer: capture phase, so wall drawing doesn't also happen ---------------------------------------
  const view = mv.view;
  function mapPx(e) {
    const cam = mv.cam, r = view.getBoundingClientRect(), sx = ((e.clientX - r.left) * view.width) / r.width, sy = ((e.clientY - r.top) * view.height) / r.height;
    return [(sx - view.width / 2) / cam.z + cam.x, (sy - view.height / 2) / cam.z + cam.y];
  }
  function hit(px, py) {
    const list = lvz.objects.map((o, i) => [o, i]).filter(([o]) => o.map).sort((a, b) => b[0].layer - a[0].layer || b[1] - a[1]);
    for (const [o, i] of list) { const [fw, fh] = size(o); if (px >= o.x && py >= o.y && px < o.x + fw && py < o.y + fh) return i; }
    return -1;
  }
  const snap = (v) => (q("lzp-snap").checked ? Math.round(v / 16) * 16 : Math.round(v));
  const clamp = (v) => Math.max(0, Math.min(16384, v));
  const editing = (e) => lvz && q("lzp-edit").checked && q("lzp-show").checked && e.button === 0 && !space && !e.ctrlKey && !e.metaKey;
  view.addEventListener("pointerdown", (e) => {
    if (!editing(e)) return;
    e.stopImmediatePropagation(); e.preventDefault();
    view.setPointerCapture(e.pointerId);
    const [px, py] = mapPx(e);
    let i = hit(px, py);
    if (i >= 0 && e.altKey) { const c = Object.assign({}, lvz.objects[i], { id: nextId() }); delete c.fromClv1; lvz.objects.push(c); i = lvz.objects.length - 1; setDirty(true); }
    if (i < 0) {
      if (!lvz.images.length) { say("Add an image first, then click the map to place it.", false); return; }
      const p = pics[img], fw = p ? p.fw : 32, fh = p ? p.fh : 32;
      lvz.objects.push(Lvz.newObject({ map: true, image: img, x: clamp(snap(px - fw / 2)), y: clamp(snap(py - fh / 2)), layer: +q("lzp-layer").value, id: nextId() }));
      i = lvz.objects.length - 1; setDirty(true);
    }
    const o = lvz.objects[i];
    sel = i; drag = { i, sx: px, sy: py, ox: o.x, oy: o.y };
    showSel(); redraw();
  }, true);
  view.addEventListener("pointermove", (e) => {
    if (!drag) return;
    e.stopImmediatePropagation();
    const [px, py] = mapPx(e), o = lvz.objects[drag.i];
    const nx = clamp(snap(drag.ox + px - drag.sx)), ny = clamp(snap(drag.oy + py - drag.sy));
    if (nx !== o.x || ny !== o.y) { o.x = nx; o.y = ny; setDirty(true); showSel(); redraw(); }
  }, true);
  const up = (e) => { if (!drag) return; e.stopImmediatePropagation(); drag = null; count(); };
  view.addEventListener("pointerup", up, true);
  view.addEventListener("pointercancel", up, true);
  window.addEventListener("keyup", (e) => { if (e.key === " ") space = false; }, true);
  window.addEventListener("keydown", (e) => {
    if (e.key === " ") space = true;
    if (!lvz || !q("lzp-edit").checked || sel < 0 || e.target.closest?.("input, select, textarea, dialog")) return;
    const o = lvz.objects[sel]; if (!o) return;
    const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
    const stop = () => { e.preventDefault(); e.stopImmediatePropagation(); };
    if (d) { stop(); const k = e.shiftKey ? 16 : 1; o.x = clamp(o.x + d[0] * k); o.y = clamp(o.y + d[1] * k); setDirty(true); showSel(); redraw(); }
    else if (e.key === "Delete" || e.key === "Backspace") { stop(); q("lzp-del").click(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "d") { stop(); q("lzp-dup").click(); }
    else if (e.key === "Escape") { stop(); sel = -1; showSel(); redraw(); }
  }, true);

  // ---- panel ------------------------------------------------------------------------------------------
  const nextId = () => lvz.objects.reduce((m, o) => Math.max(m, o.id), 0) + 1;
  function count() { q("lzp-count").textContent = lvz ? `${lvz.objects.filter((o) => o.map).length} on the map` : ""; }
  function showPalette() {
    const box = q("lzp-pal"); box.replaceChildren();
    if (!lvz) return;
    lvz.images.forEach((im, i) => {
      const b = document.createElement("button"); b.type = "button"; b.title = `${i}: ${im.file}`; b.setAttribute("aria-label", `Place ${im.file}`);
      if (i === img) b.className = "on";
      const c = document.createElement("canvas"); c.width = 40; c.height = 40;
      const ctx = c.getContext("2d"), p = pics[i];
      ctx.imageSmoothingEnabled = false;
      if (p && p.canvas) { const z = Math.min(40 / p.fw, 40 / p.fh, 4); Lvz.drawFrame(ctx, p, im, 0, (40 - p.fw * z) / 2, (40 - p.fh * z) / 2, z); }
      else Lvz.drawFrame(ctx, null, im, 0, 4, 4, 1, "?");
      b.append(c);
      b.addEventListener("click", () => { img = i; showPalette(); if (!q("lzp-edit").checked) { q("lzp-edit").checked = true; redraw(); } const o = lvz.objects[sel]; if (o && o.map) { o.image = i; setDirty(true); redraw(); } });
      box.append(b);
    });
  }
  function showSel() {
    const o = lvz && lvz.objects[sel];
    q("lzp-sel").hidden = !o || !o.map;
    if (!o || !o.map) return;
    F("x").value = o.x; F("y").value = o.y; F("layer").value = o.layer; F("mode").value = o.mode; F("time").value = (o.time / 10).toFixed(1); F("id").value = o.id;
  }
  q("lzp-sel").addEventListener("change", (e) => {
    const o = lvz && lvz.objects[sel], k = e.target.dataset.f; if (!o || !k) return;
    const v = +e.target.value;
    if (k === "time") o.time = Math.max(0, Math.min(4095, Math.round(v * 10)));
    else if (k === "x" || k === "y") o[k] = clamp(Math.round(v) || 0);
    else if (k === "id") o.id = Math.max(0, Math.min(32767, Math.round(v) || 0));
    else o[k] = Math.round(v) || 0;
    setDirty(true); redraw();
  });
  q("lzp-dup").addEventListener("click", () => {
    const o = lvz && lvz.objects[sel]; if (!o) return;
    const c = Object.assign({}, o, { id: nextId(), x: clamp(o.x + 16), y: clamp(o.y + 16) }); delete c.fromClv1;
    lvz.objects.push(c); sel = lvz.objects.length - 1; setDirty(true); showSel(); count(); redraw();
  });
  q("lzp-del").addEventListener("click", () => {
    if (!lvz || !lvz.objects[sel]) return;
    lvz.objects.splice(sel, 1); sel = -1; setDirty(true); showSel(); count(); redraw();
  });
  for (const c of ["lzp-show", "lzp-edit"]) q(c).addEventListener("change", redraw);
  q("lzp-addimg").addEventListener("change", async (e) => {
    const files = [...e.target.files]; e.target.value = "";
    if (!lvz) await useLvz(Lvz.blank(), { name: "decor.lvz", from: "new" });
    for (const f of files) {
      const name = Lvz.safeName(f.name), bytes = new Uint8Array(await f.arrayBuffer());
      try { await Lvz.imageCanvas(name, bytes); } catch { say(`${f.name} couldn't be read as an image.`, false); continue; }
      Lvz.putFile(lvz, name, bytes);
      if (!lvz.images.some((im) => im.file.toLowerCase() === name.toLowerCase())) lvz.images.push({ file: name, cols: 1, rows: 1, time: 100 });
      img = lvz.images.findIndex((im) => im.file.toLowerCase() === name.toLowerCase());
    }
    pics = await Lvz.pictures(lvz); setDirty(true); showPalette(); q("lzp-edit").checked = true; redraw();
    say("Click the map to place it.", true);
  });

  // ---- open, save, publish ------------------------------------------------------------------------------
  async function useLvz(next, from) {
    lvz = next; src = from; sel = -1; img = 0; setDirty(false);
    pics = await Lvz.pictures(lvz);
    showPalette(); showSel(); count(); redraw();
    q("lzp-src").value = from.from === "lib" ? "lib:" + from.libId : from.from === "arena" ? "arena:" + from.name : "";
    q("lzp-pub").disabled = role !== "owner";
    q("lzp-full").href = "/arena/me" + (mv.arena ? "?arena=" + encodeURIComponent(mv.arena) : "") + "#lvz";
  }
  async function fetchBytes(url) { const r = await fetch(url, { credentials: "same-origin" }).catch(() => null); return r && r.ok ? { bytes: new Uint8Array(await r.arrayBuffer()), r } : null; }
  async function open(v) {
    const kind = v.slice(0, v.indexOf(":")), id = v.slice(v.indexOf(":") + 1);
    try {
      if (kind === "arena") {
        const f = await fetchBytes(`${API}/lvz/arena?arena=${encodeURIComponent(mv.arena)}&name=${encodeURIComponent(id)}`);
        if (!f) { say(`Couldn't read ${id}.`, false); return; }
        await useLvz(await Lvz.read(f.bytes), { name: id, from: "arena" });
      } else if (kind === "lib") {
        const f = await fetchBytes(`${API}/lvzlib/file?id=${encodeURIComponent(id)}`);
        if (!f) { say("Couldn't open that .lvz from Your LVZ.", false); return; }
        await useLvz(await Lvz.read(f.bytes), { name: decodeURIComponent(f.r.headers.get("X-Lvz-Name") || "decor.lvz"), from: "lib", libId: +id });
      } else if (kind === "new") {
        const n = await askText("Name of the new .lvz", { value: "decor.lvz", ok: "Make it" }); if (!n) return;
        await useLvz(Lvz.blank(), { name: n.replace(/[^A-Za-z0-9._-]/g, "_").replace(/\.lvz$/i, "").slice(0, 28) + ".lvz", from: "new" });
        say("Add an image, then click the map to place it.", true);
        return;
      }
      const n = lvz.objects.filter((o) => o.map).length, s = lvz.objects.length - n;
      say(`${src.name}: ${n} map object${n === 1 ? "" : "s"}${s ? `, ${s} screen (edit those in the LVZ tab)` : ""}.`, true);
    } catch (err) { say(err.message, false); }
  }
  q("lzp-src").addEventListener("change", async (e) => {
    const v = e.target.value; if (!v) return;
    if (dirty && !(await ask("Discard the unsaved changes to this .lvz?", { ok: "Discard", danger: true }))) { e.target.value = src ? (src.from === "lib" ? "lib:" + src.libId : src.from === "arena" ? "arena:" + src.name : "") : ""; return; }
    open(v);
  });
  async function lists() {
    const [me, lib] = await Promise.all([
      mv.arena ? fetch(`${API}/me?arena=${encodeURIComponent(mv.arena)}`, { credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => null) : null,
      fetch(`${API}/lvzlib`, { credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => null),
    ]);
    role = me && me.role; levelFiles = (me && me.levelFiles) || [];
    const sel2 = q("lzp-src"); sel2.replaceChildren(new Option("Pick an .lvz…", ""));
    const group = (label, items) => { if (!items.length) return; const g = document.createElement("optgroup"); g.label = label; for (const [v, t] of items) g.append(new Option(t, v)); sel2.append(g); };
    group(mv.arena ? `On ${mv.arena}` : "This arena", levelFiles.filter((n) => /\.lvz$/i.test(n)).map((n) => ["arena:" + n, n]));
    group("Your LVZ (drafts)", ((lib && lib.files) || []).map((x) => ["lib:" + x.id, x.name]));
    group("New", [["new:", "a new .lvz…"]]);
    q("lzp-save").disabled = !lib; q("lzp-pub").disabled = role !== "owner";
    if (src) sel2.value = src.from === "lib" ? "lib:" + src.libId : src.from === "arena" ? "arena:" + src.name : "";
  }
  async function bytes() { const b = await Lvz.write(lvz); if (b.length > 5 * 1024 * 1024) throw new Error("It's over 5 MB, the most an arena's .lvz can be."); return b; }
  q("lzp-save").addEventListener("click", async () => {
    if (!lvz) return;
    try {
      const id = src.from === "lib" ? src.libId : null;
      const r = await fetch(`${API}/lvzlib/save?name=${encodeURIComponent(src.name)}${id ? "&id=" + id : ""}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: await bytes() });
      const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
      say(j.ok ? `${j.message} A draft: publish it when it's ready.` : j.message, j.ok);
      if (j.ok) { src = { name: j.name || src.name, from: "lib", libId: j.id }; setDirty(false); lists(); }
    } catch (err) { say(err.message, false); }
  });
  q("lzp-pub").addEventListener("click", async () => {
    if (!lvz || role !== "owner") return;
    if (!(await ask(`Publish ${src.name} to ${mv.arena}? Players get it when the arena restarts (the map's own Publish restarts it, or restart from the arena editor).`, { title: "Publish .lvz", ok: "Publish" }))) return;
    try {
      const r = await fetch(`${API}/upload?kind=lvz&arena=${encodeURIComponent(mv.arena)}&name=${encodeURIComponent(src.name)}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: await bytes() });
      const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
      say(j.ok ? `Published ${src.name}. It shows in game after the arena restarts.` : j.message, j.ok);
      if (j.ok) { if (src.from !== "lib") setDirty(false); lists(); }
    } catch (err) { say(err.message, false); }
  });

  (async () => {
    await lists();
    const p = new URLSearchParams(location.search);
    if (p.get("lvzlib")) await open("lib:" + p.get("lvzlib"));
    else if (p.get("lvz")) await open("arena:" + p.get("lvz"));
    else { const first = levelFiles.find((n) => /\.lvz$/i.test(n) && n.toLowerCase() !== "arena-graphics.lvz"); if (first) await open("arena:" + first); }
    if (p.get("lvz") || p.get("lvzlib")) { q("lzp-edit").checked = true; redraw(); panel.scrollIntoView({ block: "nearest" }); }
  })();
})();
