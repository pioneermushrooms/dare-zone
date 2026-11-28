// The map editor's LVZ layer: draws an .lvz's map objects on the map at their layer (under the
// tiles for BelowAll and AfterBackground, over them otherwise) and lets the pilot place images on the
// map: pick an image (uploaded, from their Armory, or from another .lvz), click the map to place it,
// drag to move, Alt+drag or Ctrl/Cmd+D to copy, Del to delete. Save LVZ keeps the placements with the
// map (a draft in Your LVZ, remembered per arena); the map's own Publish then puts the .lvz in the arena
// along with the map (window.mapPublishHooks). Hooks: editor.js's window.mapView (view, cam, arena,
// redraw, changed) and window.mapOverlays. Needs lvz.js. Text goes in with textContent only.
"use strict";

(() => {
  const mv = window.mapView, panel = document.getElementById("lvzpanel");
  if (!mv || !panel || !window.Lvz) return;
  const API = "/arena/api", ACCT = "/account/api", HDR = { "X-Requested-With": "dare-arena" };
  // files the panel doesn't open by itself: the Graphics page's and the game mode wizard's own
  const AUTO_SKIP = new Set(["arena-graphics.lvz", "gamemode.lvz"]);
  const NEW_NAME = "map-objects.lvz";
  const MODE_TEXT = ["always", "entering the zone", "entering the arena", "on a kill", "on death", "when turned on (*objon)"];

  panel.classList.add("lvzpanel");
  panel.innerHTML = `
    <p class="kicker">LVZ images <span class="dim lzp-count"></span></p>
    <p class="hint lzp-tip">Select an LVZ image, then click the map to place it.</p>
    <div class="lzp-where" role="radiogroup" aria-label="Where new images go">
      <label class="hint"><input type="radio" name="lzpwhere" value="map" checked> <b>On the map</b>: at a spot in the arena; it scrolls with the map</label>
      <label class="hint dim" title="Coming next: a game screen to drop images onto"><input type="radio" name="lzpwhere" value="screen" disabled> On the screen: fixed on every pilot's screen (coming soon)</label>
    </div>
    <div class="lzp-pal" aria-label="Images: select one, then click the map to place it"></div>
    <div class="row"><label class="btn ghost small" title="PNG, BMP or GIF from your computer">Upload…<input type="file" class="lzp-addimg" accept=".png,.bmp,.bm2,.gif,.jpg,.jpeg" multiple hidden></label>
      <select class="lzp-from" aria-label="Add images from your Armory or another .lvz"><option value="">From your Armory…</option></select></div>
    <label class="hint"><input type="checkbox" class="lzp-edit"> Place and move LVZ (instead of drawing walls)</label>
    <div class="row"><label class="hint"><input type="checkbox" class="lzp-show" checked> Show them</label>
      <label class="hint"><input type="checkbox" class="lzp-snap" checked> Snap to tiles</label>
      <select class="lzp-layer" aria-label="Layer for new images" title="Layer for new images"></select></div>
    <div class="lzp-sel" hidden>
      <label class="lzp-f">X <input type="number" data-f="x" min="0" max="16384"></label>
      <label class="lzp-f">Y <input type="number" data-f="y" min="0" max="16384"></label>
      <label class="lzp-f">Layer <select data-f="layer"></select></label>
      <label class="lzp-f">Shown <select data-f="mode"></select></label>
      <label class="lzp-f" title="Seconds it stays up once shown; 0 = until turned off">Seconds <input type="number" data-f="time" min="0" max="409.5" step="0.1"></label>
      <label class="lzp-f" title="For *objon / *objoff">Id <input type="number" data-f="id" min="0" max="32767"></label>
      <p class="hint warnnote lzp-gm" hidden>Set by the game mode (id 2000–2999): the game mode turns it on and off, and may rebuild it.</p>
      <div class="row"><button type="button" class="ghost small lzp-dup">Copy</button><button type="button" class="ghost small danger lzp-del">Delete</button></div>
    </div>
    <details class="lzp-placed"><summary class="hint">Placed <span class="lzp-pn"></span></summary><ul class="lzp-list"></ul></details>
    <div class="row"><button type="button" class="small lzp-save" title="Keep these placements with the map; the map's Publish puts them in the arena">Save LVZ</button>
      <a class="btn ghost small lzp-full" href="/arena/me#lvz" title="Screen objects, image timing and more">LVZ tab</a></div>
    <p class="lzp-msg" role="status"></p>
    <details class="lzp-more"><summary class="hint">Kept in <b class="lzp-fname"></b></summary>
      <select class="lzp-src" aria-label="Which .lvz file the placements are kept in"></select>
      <p class="hint">An arena can have several .lvz files; the placements above are kept in this one. Switching keeps your changes.</p></details>
    <p class="hint">Drag to move, Alt+drag to copy, Del deletes, arrows nudge, Esc or another tool goes back to walls. Map images don't block ships.</p>`;
  const q = (c) => panel.querySelector("." + c), F = (k) => q("lzp-sel").querySelector(`[data-f=${k}]`);
  Lvz.LAYERS.forEach((l, i) => { const t = l.replace(/([a-z])([A-Z])/g, "$1 $2"); q("lzp-layer").append(new Option(t, i)); F("layer").append(new Option(t, i)); });
  MODE_TEXT.forEach((m, i) => F("mode").append(new Option(m, i)));
  q("lzp-layer").value = 2;

  let lvz = null, pics = [], src = null, sel = -1, img = -1, dirty = false, role = null, levelFiles = [], drag = null, space = false;
  let libFiles = [], armory = [];
  const say = (t, ok) => { const m = q("lzp-msg"); m.textContent = t || ""; m.className = "lzp-msg " + (ok ? "ok" : "bad"); };
  // pending: .lvz files saved with the map but not yet published, per arena: [{ libId, name }]
  const PKEY = () => "dare.lvzPending:" + (mv.arena || "");
  const getPending = () => { try { return JSON.parse(localStorage.getItem(PKEY()) || "[]").filter((p) => p && p.libId && p.name); } catch { return []; } };
  const setPending = (list) => { try { if (list.length) localStorage.setItem(PKEY(), JSON.stringify(list)); else localStorage.removeItem(PKEY()); } catch { /* the in-page list still works */ } memPending = list; };
  let memPending = getPending();
  const pending = () => memPending;
  const setDirty = (d) => { dirty = d; q("lzp-save").textContent = d ? "Save LVZ •" : "Save LVZ"; q("lzp-save").classList.toggle("lit", d); };
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
      if (q("lzp-edit").checked) { ctx.strokeStyle = i === sel ? "#ffffff" : Lvz.isReserved(o.id) ? "#c084fc99" : "#ffcf7a66"; ctx.lineWidth = i === sel ? 2 : 1; ctx.strokeRect(x - 0.5, y - 0.5, fw * z + 1, fh * z + 1); }
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
  const editing = (e) => lvz && view.getBoundingClientRect().width > 0 && q("lzp-edit").checked && q("lzp-show").checked && e.button === 0 && !space && !e.ctrlKey && !e.metaKey;
  view.addEventListener("pointerdown", (e) => {
    if (!editing(e)) return;
    e.stopImmediatePropagation(); e.preventDefault();
    view.setPointerCapture(e.pointerId);
    const [px, py] = mapPx(e);
    let i = hit(px, py);
    if (i >= 0 && e.altKey) { const c = Object.assign({}, lvz.objects[i], { id: nextId() }); delete c.fromClv1; lvz.objects.push(c); i = lvz.objects.length - 1; setDirty(true); }
    if (i < 0) {
      if (!lvz.images.length || !lvz.images[img]) { say(lvz.images.length ? "Select an LVZ image, then click the map to place it." : "Upload an image (or add one from your Armory) first, then click the map to place it.", false); return; }
      const p = pics[img], fw = p ? p.fw : 32, fh = p ? p.fh : 32;
      lvz.objects.push(Lvz.newObject({ map: true, image: img, x: clamp(snap(px - fw / 2)), y: clamp(snap(py - fh / 2)), layer: +q("lzp-layer").value, id: nextId() }));
      i = lvz.objects.length - 1; setDirty(true);
    }
    const o = lvz.objects[i];
    sel = i; drag = { i, sx: px, sy: py, ox: o.x, oy: o.y };
    showSel(); count(); redraw();
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
    if (!q("lzp-edit").checked || e.target.closest?.("input, select, textarea, dialog")) return;
    if (e.key === "Escape" && !document.querySelector("dialog[open]")) { e.preventDefault(); e.stopImmediatePropagation(); setPlacing(false, "Placement off: back to drawing walls."); return; }
    if (!lvz || sel < 0) return;
    const o = lvz.objects[sel]; if (!o) return;
    const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
    const stop = () => { e.preventDefault(); e.stopImmediatePropagation(); };
    if (d) { stop(); const k = e.shiftKey ? 16 : 1; o.x = clamp(o.x + d[0] * k); o.y = clamp(o.y + d[1] * k); setDirty(true); showSel(); redraw(); }
    else if (e.key === "Delete" || e.key === "Backspace") { stop(); q("lzp-del").click(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "d") { stop(); q("lzp-dup").click(); }
  }, true);

  // ---- placement mode: on while placing objects, off as soon as the pilot picks a tool or tile, or presses Esc
  const badge = document.createElement("div");
  badge.className = "lzp-badge"; badge.hidden = true; badge.setAttribute("role", "status");
  badge.append(Object.assign(document.createElement("span"), { textContent: "LVZ placement: clicks place and move objects, not walls" }));
  const done = Object.assign(document.createElement("button"), { type: "button", className: "small", textContent: "Done (Esc)", title: "Back to drawing walls" });
  badge.append(done);
  view.parentElement.append(badge);
  done.addEventListener("click", () => setPlacing(false, "Placement off: back to drawing walls."));
  function setPlacing(on, note) {
    on = !!on && q("lzp-show").checked;
    q("lzp-edit").checked = on;
    if (on) q("lzp-show").checked = true;
    badge.hidden = !on;
    document.body.classList.toggle("lvzplacing", on);
    q("lzp-edit").closest("label").classList.toggle("lzp-on", on);
    if (!on) { drag = null; if (sel >= 0) { sel = -1; showSel(); showList(); } }
    showPalette();
    if (note) say(note, true);
    redraw();
  }
  // ---- panel ------------------------------------------------------------------------------------------
  const nextId = () => Lvz.nextId(lvz);
  function count() {
    const m = lvz ? lvz.objects.filter((o) => o.map).length : 0, s = lvz ? lvz.objects.length - m : 0;
    q("lzp-count").textContent = lvz ? `${m} on the map${s ? `, ${s} on the screen` : ""}` : "";
    q("lzp-pn").textContent = lvz ? `(${lvz.objects.length})` : "";
    showList();
  }
  // every placed object: map ones select and jump there; screen ones are listed (placed in the LVZ tab for now)
  function showList() {
    const ul = q("lzp-list"); ul.replaceChildren();
    if (!lvz) return;
    const head = (t) => { const li = document.createElement("li"); li.className = "lzp-lh"; li.textContent = t; ul.append(li); };
    const map = lvz.objects.map((o, i) => [o, i]).filter(([o]) => o.map), scr = lvz.objects.map((o, i) => [o, i]).filter(([o]) => !o.map);
    const name = (o) => (lvz.images[o.image] ? lvz.images[o.image].file : `image ${o.image}`);
    if (map.length) head("On the map");
    for (const [o, i] of map) {
      const li = document.createElement("li"), b = document.createElement("button");
      b.type = "button"; b.className = "ghost small" + (i === sel ? " on" : "");
      b.textContent = `${name(o)} · ${o.x}, ${o.y}${Lvz.isReserved(o.id) ? " ⚙" : ""}`;
      b.title = `Id ${o.id}, ${Lvz.LAYERS[o.layer] || "layer " + o.layer}: select it and show it on the map`;
      b.addEventListener("click", () => {
        sel = i; const [fw, fh] = size(o), cam = mv.cam; cam.x = o.x + fw / 2; cam.y = o.y + fh / 2;
        if (!q("lzp-edit").checked) setPlacing(true); showSel(); showList(); redraw();
      });
      li.append(b); ul.append(li);
    }
    if (scr.length) head("On the screen (edit these in the LVZ tab for now)");
    for (const [o] of scr) { const li = document.createElement("li"); li.className = "hint"; li.textContent = `${name(o)} · ${o.x}, ${o.y}`; ul.append(li); }
    if (!map.length && !scr.length) { const li = document.createElement("li"); li.className = "hint"; li.textContent = "Nothing placed yet."; ul.append(li); }
  }
  function showPalette() {
    const box = q("lzp-pal"); box.replaceChildren();
    if (!lvz || !lvz.images.length) { const p = document.createElement("p"); p.className = "hint"; p.textContent = "No images yet: Upload one, or add one from your Armory."; box.append(p); return; }
    lvz.images.forEach((im, i) => {
      const b = document.createElement("button"); b.type = "button"; b.title = `${im.file}: select, then click the map`; b.setAttribute("aria-label", `Place ${im.file}`);
      if (i === img && q("lzp-edit").checked) b.className = "on";
      const c = document.createElement("canvas"); c.width = 40; c.height = 40;
      const ctx = c.getContext("2d"), p = pics[i];
      ctx.imageSmoothingEnabled = false;
      if (p && p.canvas) { const z = Math.min(40 / p.fw, 40 / p.fh, 4); Lvz.drawFrame(ctx, p, im, 0, (40 - p.fw * z) / 2, (40 - p.fh * z) / 2, z); }
      else Lvz.drawFrame(ctx, null, im, 0, 4, 4, 1, "?");
      b.append(c);
      b.addEventListener("click", () => {
        img = i;
        if (sel >= 0) { sel = -1; showSel(); showList(); }
        setPlacing(true, `Placing ${im.file}: click the map. Esc or Done when finished.`); showPalette();
      });
      box.append(b);
    });
  }
  function showSel() {
    const o = lvz && lvz.objects[sel];
    q("lzp-sel").hidden = !o || !o.map;
    if (!o || !o.map) return;
    q("lzp-gm").hidden = !Lvz.isReserved(o.id);
    F("x").value = o.x; F("y").value = o.y; F("layer").value = o.layer; F("mode").value = o.mode; F("time").value = (o.time / 10).toFixed(1); F("id").value = o.id;
  }
  q("lzp-sel").addEventListener("change", (e) => {
    const o = lvz && lvz.objects[sel], k = e.target.dataset.f; if (!o || !k) return;
    const v = +e.target.value;
    if (k === "time") o.time = Math.max(0, Math.min(4095, Math.round(v * 10)));
    else if (k === "x" || k === "y") o[k] = clamp(Math.round(v) || 0);
    else if (k === "id") o.id = Math.max(0, Math.min(32767, Math.round(v) || 0));
    else o[k] = Math.round(v) || 0;
    setDirty(true); showList(); redraw();
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
  q("lzp-show").addEventListener("change", () => { if (!q("lzp-show").checked) setPlacing(false); redraw(); });
  q("lzp-edit").addEventListener("change", () => { setPlacing(q("lzp-edit").checked); showPalette(); });

  // ---- adding images: upload, the Armory, another .lvz ---------------------------------------------------------
  function ensureLvz() { if (!lvz) { lvz = Lvz.blank(); src = { name: NEW_NAME, from: "new" }; showFile(); } }
  async function addImage(fileName, bytes, frames) {
    const name = Lvz.safeName(fileName);
    await Lvz.imageCanvas(name, bytes); // throws when it isn't a picture
    Lvz.putFile(lvz, name, bytes);
    let i = lvz.images.findIndex((im) => im.file.toLowerCase() === name.toLowerCase());
    if (i < 0) { lvz.images.push(Object.assign({ file: name, cols: 1, rows: 1, time: 100 }, frames || {})); i = lvz.images.length - 1; }
    return i;
  }
  async function afterAdd(i, what) {
    pics = await Lvz.pictures(lvz); setDirty(true);
    if (i >= 0) { img = i; setPlacing(true, `${what} Click the map to place it.`); }
    showPalette();
  }
  q("lzp-addimg").addEventListener("change", async (e) => {
    const files = [...e.target.files]; e.target.value = "";
    ensureLvz();
    let last = -1;
    for (const f of files) {
      try { last = await addImage(f.name, new Uint8Array(await f.arrayBuffer())); }
      catch { say(`${f.name} couldn't be read as an image.`, false); }
    }
    if (last >= 0) await afterAdd(last, files.length > 1 ? `Added ${files.length} images.` : `Added ${files[0].name}.`);
  });
  async function fetchBytes(url) { const r = await fetch(url, { credentials: "same-origin" }).catch(() => null); return r && r.ok ? { bytes: new Uint8Array(await r.arrayBuffer()), r } : null; }
  q("lzp-from").addEventListener("change", async (e) => {
    const v = e.target.value; e.target.value = ""; if (!v) return;
    const kind = v.slice(0, v.indexOf(":")), id = v.slice(v.indexOf(":") + 1);
    ensureLvz();
    try {
      if (kind === "arm") {
        const it = armory.find((x) => String(x.id) === id); if (!it) return;
        const f = await fetchBytes(`${ACCT}/armory/sheet?id=${encodeURIComponent(id)}`);
        if (!f) { say("Couldn't load that Armory item.", false); return; }
        const ext = /bmp/.test(f.r.headers.get("Content-Type") || "") ? ".bmp" : ".png";
        await afterAdd(await addImage(`${it.name || it.kind}${ext}`, f.bytes), `Added ${it.name || it.kind} from your Armory.`);
      } else {
        // every image of another .lvz, added to this one (the other file isn't changed)
        const f = kind === "arena" ? await fetchBytes(`${API}/lvz/arena?arena=${encodeURIComponent(mv.arena)}&name=${encodeURIComponent(id)}`) : await fetchBytes(`${API}/lvzlib/file?id=${encodeURIComponent(id)}`);
        if (!f) { say("Couldn't read that .lvz.", false); return; }
        const other = await Lvz.read(f.bytes);
        let last = -1, n = 0;
        for (const im of other.images) {
          const file = other.files.find((x) => x.name.toLowerCase() === im.file.toLowerCase()); if (!file) continue;
          try { last = await addImage(im.file, await Lvz.fileData(file), { cols: im.cols, rows: im.rows, time: im.time }); n++; } catch { /* not a picture */ }
        }
        if (last < 0) { say("That .lvz has no images to add.", false); return; }
        await afterAdd(last, `Added ${n} image${n === 1 ? "" : "s"}.`);
      }
    } catch (err) { say(err.message, false); }
  });

  // ---- which file, save, publish ------------------------------------------------------------------------------
  const srcValue = () => (!src ? "" : src.from === "lib" ? "lib:" + src.libId : src.from === "arena" ? "arena:" + src.name : "new:");
  function showFile() { q("lzp-fname").textContent = src ? src.name : NEW_NAME; if (q("lzp-src").options.length) q("lzp-src").value = srcValue(); }
  async function useLvz(next, from) {
    lvz = next; src = from; sel = -1; img = lvz.images.length ? 0 : -1; setDirty(false);
    pics = await Lvz.pictures(lvz);
    showPalette(); showSel(); count(); showFile(); redraw();
    q("lzp-full").href = "/arena/me" + (mv.arena ? "?arena=" + encodeURIComponent(mv.arena) : "") + "#lvz";
  }
  async function open(v) {
    const kind = v.slice(0, v.indexOf(":")), id = v.slice(v.indexOf(":") + 1);
    if (kind === "arena") {
      const f = await fetchBytes(`${API}/lvz/arena?arena=${encodeURIComponent(mv.arena)}&name=${encodeURIComponent(id)}`);
      if (!f) { say(`Couldn't read ${id}.`, false); return false; }
      await useLvz(await Lvz.read(f.bytes), { name: id, from: "arena" });
    } else if (kind === "lib") {
      const f = await fetchBytes(`${API}/lvzlib/file?id=${encodeURIComponent(id)}`);
      if (!f) { say("Couldn't open that .lvz from Your LVZ.", false); return false; }
      await useLvz(await Lvz.read(f.bytes), { name: decodeURIComponent(f.r.headers.get("X-Lvz-Name") || NEW_NAME), from: "lib", libId: +id });
    } else {
      const taken = new Set([...levelFiles, ...libFiles.map((x) => x.name)].map((n) => n.toLowerCase()));
      let name = NEW_NAME; for (let k = 2; taken.has(name.toLowerCase()); k++) name = `map-objects-${k}.lvz`;
      await useLvz(Lvz.blank(), { name, from: "new" });
    }
    return true;
  }
  // switching files never asks: unsaved placements are saved with the map first
  q("lzp-src").addEventListener("change", async (e) => {
    const v = e.target.value; if (!v || v === srcValue()) return;
    if (dirty && !(await save(true))) { showFile(); return; }
    if (await open(v)) say(`Now placing into ${src.name}.`, true);
  });
  async function lists() {
    const [me, lib, arm] = await Promise.all([
      mv.arena ? fetch(`${API}/me?arena=${encodeURIComponent(mv.arena)}`, { credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => null) : null,
      fetch(`${API}/lvzlib`, { credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => null),
      fetch(`${ACCT}/armory`, { credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => null),
    ]);
    role = me && me.role; levelFiles = ((me && me.levelFiles) || []).filter((n) => /\.lvz$/i.test(n));
    libFiles = (lib && lib.files) || []; armory = (arm && arm.ok && arm.items) || [];
    const group = (sel2, label, items) => { if (!items.length) return; const g = document.createElement("optgroup"); g.label = label; for (const [v, t] of items) g.append(new Option(t, v)); sel2.append(g); };
    const from = q("lzp-from"); from.replaceChildren(new Option(armory.length ? "From your Armory…" : "From another .lvz…", ""));
    group(from, "Your Armory", armory.map((x) => ["arm:" + x.id, `${x.name || x.kind} (${x.kind})`]));
    group(from, "Images of another .lvz", [
      ...levelFiles.filter((n) => !src || src.from !== "arena" || n !== src.name).map((n) => ["arena:" + n, `${n} (on ${mv.arena})`]),
      ...libFiles.filter((x) => !src || src.from !== "lib" || x.id !== src.libId).map((x) => ["lib:" + x.id, `${x.name} (Your LVZ)`])]);
    const s2 = q("lzp-src"); s2.replaceChildren();
    group(s2, mv.arena ? `On ${mv.arena}` : "This arena", levelFiles.map((n) => ["arena:" + n, n]));
    group(s2, "Your LVZ (saved, not yet published)", libFiles.map((x) => ["lib:" + x.id, x.name]));
    group(s2, "New", [["new:", "a new .lvz file"]]);
    if (src && ![...s2.options].some((o) => o.value === srcValue())) s2.append(new Option(src.name, srcValue()));
    q("lzp-save").disabled = !lib;
    showFile();
  }
  async function bytes() { const b = await Lvz.write(lvz); if (b.length > 5 * 1024 * 1024) throw new Error("It's over 5 MB, the most an arena's .lvz can be."); return b; }
  // Save LVZ: kept in Your LVZ (the same entry each time) and noted as part of the map's next Publish.
  // Nothing on the map or in the list resets.
  async function save(quiet) {
    if (!lvz) return true;
    try {
      // the same Your LVZ entry each time: this draft, else the one already saved under this file's name
      const same = (x) => x.name.toLowerCase() === src.name.toLowerCase();
      const id = src.from === "lib" ? src.libId : (pending().find(same) || libFiles.find(same) || {}).libId || (libFiles.find(same) || {}).id;
      const r = await fetch(`${API}/lvzlib/save?name=${encodeURIComponent(src.name)}${id ? "&id=" + id : ""}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: await bytes() });
      const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
      if (!j.ok) { say(j.message, false); return false; }
      const name = src.name;
      src = { name, from: "lib", libId: j.id };
      setPending([...pending().filter((p) => p.name.toLowerCase() !== name.toLowerCase() && p.libId !== j.id), { libId: j.id, name }]);
      setDirty(false);
      if (mv.changed) mv.changed();
      if (!quiet) say(role === "owner" ? `Saved with the map. Publish (at the top) puts ${name} in ${mv.arena} along with the map.` : `Saved in Your LVZ. Only the arena's owner can publish it.`, true);
      await lists();
      return true;
    } catch (err) { say(err.message, false); return false; }
  }
  q("lzp-save").addEventListener("click", () => save(false));
  // the map's Publish: every .lvz saved with the map goes to the arena (before the restart); unsaved
  // placements in the open file are saved first so they aren't left behind
  const publishHook = async (arena) => {
    if (role !== "owner") return "";
    if (dirty && !(await save(true))) return "The LVZ couldn't be saved, so it wasn't published.";
    const done = [], failed = [];
    for (const p of pending()) {
      let body = null;
      if (src && src.from === "lib" && src.libId === p.libId && lvz) body = await bytes();
      else { const f = await fetchBytes(`${API}/lvzlib/file?id=${p.libId}`); body = f && f.bytes; }
      if (!body) { failed.push(p.name); continue; }
      const r = await fetch(`${API}/upload?kind=lvz&arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(p.name)}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body });
      const j = await r.json().catch(() => ({ ok: false }));
      (j.ok ? done : failed).push(p.name);
    }
    setPending(pending().filter((p) => failed.includes(p.name)));
    if (done.length) lists();
    return [done.length ? `LVZ published: ${done.join(", ")}.` : "", failed.length ? `Not published: ${failed.join(", ")}.` : ""].filter(Boolean).join(" ");
  };
  publishHook.pending = () => role === "owner" && pending().length > 0;
  (window.mapPublishHooks = window.mapPublishHooks || []).push(publishHook);

  (async () => {
    await lists();
    const p = new URLSearchParams(location.search), pend = pending();
    let ok = false;
    if (p.get("lvzlib")) ok = await open("lib:" + p.get("lvzlib"));
    else if (p.get("lvz")) ok = await open("arena:" + p.get("lvz"));
    else if (pend.length) {
      ok = await open("lib:" + pend[pend.length - 1].libId);
      if (ok && mv.changed) { mv.changed(); say(`${pend.map((x) => x.name).join(", ")} saved with the map, not published yet.`, true); }
    }
    if (!ok) { const first = levelFiles.find((n) => !AUTO_SKIP.has(n.toLowerCase())); ok = first ? await open("arena:" + first) : false; }
    if (!ok) await open("new:");
    await lists();
    if (p.get("lvz") || p.get("lvzlib")) { setPlacing(true); panel.scrollIntoView({ block: "nearest" }); }
  })();
})();
