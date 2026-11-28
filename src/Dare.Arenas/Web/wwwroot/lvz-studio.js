// LVZ editor: the arena editor's LVZ tab (/arena/me#lvz). LvzStudio.mount(root, { arena, owner, onPublished })
// builds it inside root. Open an arena's .lvz, one of Your LVZ, an .lvz or a Toolkit .ini project
// (with its images) from disk, or start empty; manage files and images (animation frames, rows,
// columns, period); list and edit objects (map or screen, id, position, layer, mode, display time);
// see screen objects on a screen-sized preview and drag them; keep it in Your LVZ (drafts); and
// publish it to the arena, which takes effect when the arena restarts. Map objects are placed on
// the map itself in the map editor (lvz-layer.js); this tab links there.
// Needs lvz.js and lvl.js; dialogs.js (window.Dlg) when present. Text goes in with textContent only.
"use strict";

window.LvzStudio = (() => {
  const API = "/arena/api";
  const HDR = { "X-Requested-With": "dare-arena" };
  const ask = (t, o) => (window.Dlg ? Dlg.confirm(t, o) : Promise.resolve(window.confirm(t)));
  const askText = (t, o) => (window.Dlg ? Dlg.prompt(t, o) : Promise.resolve(window.prompt(t, (o && o.value) || "")));
  const kb = (n) => (n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB");
  const SIZES = [[1024, 768], [1280, 1024], [1920, 1080], [800, 600]];

  const TEMPLATE = `
  <div class="lz-bar">
    <select class="lz-open" aria-label="Open an .lvz"><option value="">Open…</option></select>
    <label class="btn ghost small" title="An .lvz, or a Toolkit .ini project with its image files">File…<input type="file" class="lz-file" multiple hidden></label>
    <button type="button" class="ghost small lz-new">New</button>
    <b class="lz-name"></b> <span class="tag draft lz-dirty" hidden>Unsaved</span>
    <span class="lz-grow"></span>
    <button type="button" class="small lz-save" title="Keep it in Your LVZ (a draft; the arena doesn't change)">Save</button>
    <button type="button" class="ghost small lz-saveas">Save as new</button>
    <button type="button" class="ghost small lz-dl">.lvz</button>
    <button type="button" class="ghost small lz-dlini" title="The .ini project (BuildLevel); the files are the .lvz's own">.ini</button>
    <button type="button" class="hot small lz-publish" title="Put it on the arena; players get it when the arena restarts">Publish to arena</button>
  </div>
  <p class="lz-msg" role="status"></p>
  <div class="lz-body">
    <aside class="lz-col">
      <section class="lz-card">
        <h4>Images <span class="hint lz-imgcount"></span></h4>
        <ul class="lz-images"></ul>
        <label class="btn ghost small">Add images…<input type="file" class="lz-addimg" accept=".png,.bmp,.bm2,.gif,.jpg,.jpeg" multiple hidden></label>
        <p class="hint">PNG, BMP or GIF; pure black shows as see-through in game. An image can be a sheet of frames: set columns and rows, and the period for the whole run (1/100 s).</p>
      </section>
      <section class="lz-card">
        <h4>Files <span class="hint lz-filecount"></span></h4>
        <ul class="lz-files"></ul>
        <label class="btn ghost small" title="Any file the game should get: images, replacement graphics (.bm2), sounds (.wa2)…">Add files…<input type="file" class="lz-addfile" multiple hidden></label>
      </section>
    </aside>
    <main class="lz-main">
      <div class="lz-tabs" role="tablist">
        <button type="button" role="tab" class="ghost small on" data-tab="screen" aria-selected="true">Screen objects</button>
        <button type="button" role="tab" class="ghost small" data-tab="map" aria-selected="false">Map objects</button>
      </div>
      <section class="lz-pane" data-pane="screen">
        <div class="lz-row"><select class="lz-size" aria-label="Screen size"></select>
          <label class="lz-mini"><input type="checkbox" class="lz-anchors" checked> Reference points</label>
          <label class="lz-mini"><input type="checkbox" class="lz-anim" checked> Animate</label></div>
        <canvas class="lz-screen" aria-label="Screen preview: drag screen objects to move them"></canvas>
        <p class="hint">Reference points are where Continuum puts them on a default HUD (roughly; they move with each player's HUD settings). Drag an object to move it; its offset from its reference point changes.</p>
      </section>
      <section class="lz-pane" data-pane="map" hidden>
        <canvas class="lz-mapview" width="512" height="512" aria-label="Where the map objects are on the arena's map"></canvas>
        <p class="lz-maphint"></p>
        <a class="btn small lz-onmap" href="/arena/map">Place and drag them on the map</a>
      </section>
    </main>
    <aside class="lz-col">
      <section class="lz-card">
        <h4>Objects <span class="hint lz-objcount"></span></h4>
        <select class="lz-filter" aria-label="Which objects"><option value="all">all</option><option value="screen">screen</option><option value="map">map</option></select>
        <ul class="lz-objs"></ul>
        <div class="lz-row">
          <button type="button" class="ghost small lz-addscreen">+ Screen object</button>
          <button type="button" class="ghost small lz-addmap">+ Map object</button></div>
        <div class="lz-row">
          <button type="button" class="ghost small lz-dup">Duplicate</button>
          <button type="button" class="ghost small danger lz-del">Delete</button></div>
      </section>
      <section class="lz-card lz-form" hidden>
        <h4>Object</h4>
        <label class="lz-f">Kind <select data-f="map"><option value="false">screen</option><option value="true">map</option></select></label>
        <label class="lz-f">Image <select data-f="image"></select></label>
        <label class="lz-f" title="For *objon / *objoff and bots">Id <input type="number" data-f="id" min="0" max="32767"></label>
        <label class="lz-f lz-scr">X from <select data-f="xRef"></select></label>
        <label class="lz-f">X <input type="number" data-f="x"></label>
        <label class="lz-f lz-scr">Y from <select data-f="yRef"></select></label>
        <label class="lz-f">Y <input type="number" data-f="y"></label>
        <label class="lz-f">Layer <select data-f="layer"></select></label>
        <label class="lz-f">Shown <select data-f="mode"></select></label>
        <label class="lz-f" title="How long it stays up after it is shown; 0 = until turned off">Seconds <input type="number" data-f="time" min="0" max="409.5" step="0.1"></label>
        <p class="hint lz-mapnote" hidden>Map coordinates are pixels: 16 per tile, 0–16384 across.</p>
      </section>
    </aside>
  </div>`;
  const MODE_TEXT = ["always", "on entering the zone", "on entering the arena", "on a kill", "on death", "when turned on (*objon)"];

  function mount(root, opts = {}) {
    root.classList.add("lvzstudio");
    root.innerHTML = TEMPLATE; // fixed markup; data goes in with textContent
    const q = (c) => root.querySelector("." + c);
    let arena = opts.arena || "", owner = !!opts.owner;
    let lvz = Lvz.blank(), pics = [], src = { name: "graphics.lvz", from: "new", libId: null };
    let sel = -1, dirty = false, tab = "screen", drag = null, mapGrid = null;
    const say = (t, ok) => { const m = q("lz-msg"); m.textContent = t || ""; m.className = "lz-msg " + (ok ? "ok" : "bad"); };
    const setDirty = (d) => { dirty = d; q("lz-dirty").hidden = !d; };
    window.addEventListener("beforeunload", (e) => { if (dirty && root.isConnected) { e.preventDefault(); e.returnValue = ""; } });

    // fixed option lists
    for (const [w, h] of SIZES) q("lz-size").append(new Option(`${w} × ${h}`, `${w}x${h}`));
    const form = q("lz-form"), F = (k) => form.querySelector(`[data-f=${k}]`);
    Lvz.REFS.forEach(([p, d], i) => { F("xRef").append(new Option(`${p || "–"} ${d}`, i)); F("yRef").append(new Option(`${p || "–"} ${d}`, i)); });
    Lvz.LAYERS.forEach((l, i) => F("layer").append(new Option(l.replace(/([a-z])([A-Z])/g, "$1 $2"), i)));
    MODE_TEXT.forEach((m, i) => F("mode").append(new Option(m, i)));

    // ---- lists -------------------------------------------------------------------------------------
    const imageLabel = (i) => { const im = lvz.images[i]; return im ? `${i}: ${im.file}` : `${i}: (none)`; };
    function renderImages() {
      const ul = q("lz-images"); ul.replaceChildren();
      q("lz-imgcount").textContent = `${lvz.images.length}`;
      lvz.images.forEach((im, i) => {
        const li = document.createElement("li");
        const c = document.createElement("canvas"); c.width = 40; c.height = 40; c.className = "lz-thumb";
        const p = pics[i], ctx = c.getContext("2d");
        ctx.fillStyle = "#000"; ctx.fillRect(0, 0, 40, 40);
        if (p && p.canvas) { const z = Math.min(40 / p.fw, 40 / p.fh, 4); Lvz.drawFrame(ctx, p, im, 0, (40 - p.fw * z) / 2, (40 - p.fh * z) / 2, z); }
        else Lvz.drawFrame(ctx, null, im, 0, 4, 4, 1, "?");
        const info = document.createElement("div"); info.className = "lz-iminfo";
        const n = document.createElement("b"); n.textContent = `${i}: ${im.file}`; n.title = p && p.missing ? "This file isn't in the .lvz (it may come from another one)" : "";
        if (p && p.missing) n.classList.add("missing");
        const num = (label, key, min, max, title) => {
          const l = document.createElement("label"); l.className = "lz-mini"; l.title = title || "";
          l.append(label + " ");
          const inp = document.createElement("input"); inp.type = "number"; inp.min = min; inp.max = max; inp.value = im[key];
          inp.addEventListener("change", async () => { im[key] = Math.max(min, Math.min(max, Math.round(+inp.value || min))); inp.value = im[key]; setDirty(true); await refreshPics(); });
          l.append(inp); return l;
        };
        const row = document.createElement("div"); row.className = "lz-row";
        row.append(num("cols", "cols", 1, 64, "Frames across"), num("rows", "rows", 1, 64, "Frames down"), num("period", "time", 0, 32767, "Time for the whole animation, 1/100 s"));
        const uses = lvz.objects.filter((o) => o.image === i).length;
        const del = document.createElement("button"); del.type = "button"; del.className = "ghost small danger"; del.textContent = "Remove";
        del.setAttribute("aria-label", `Remove image ${im.file}`);
        del.addEventListener("click", async () => {
          if (uses && !(await ask(`Remove image ${i} (${im.file})? ${uses} object${uses === 1 ? "" : "s"} using it go too.`, { ok: "Remove", danger: true }))) return;
          Lvz.removeImage(lvz, i); sel = -1; setDirty(true); await refreshPics(); renderAll();
        });
        const u = document.createElement("span"); u.className = "hint"; u.textContent = `${p && p.canvas ? `${p.fw}×${p.fh}` : "?"} · ${uses} object${uses === 1 ? "" : "s"}`;
        info.append(n, row, u, del);
        li.append(c, info); ul.append(li);
      });
      if (!lvz.images.length) { const li = document.createElement("li"); li.className = "dim"; li.textContent = "No images yet: add some, then objects that show them."; ul.append(li); }
      F("image").replaceChildren(...lvz.images.map((_, i) => new Option(imageLabel(i), i)));
    }
    function renderFiles() {
      const ul = q("lz-files"); ul.replaceChildren();
      q("lz-filecount").textContent = `${lvz.files.length}`;
      for (const f of lvz.files) {
        const li = document.createElement("li");
        const n = document.createElement("span"); n.className = "lz-fname"; n.textContent = f.name;
        const size = document.createElement("span"); size.className = "hint"; size.textContent = kb(f.data ? f.data.length : f._sec ? f._sec.size : 0);
        const dl = document.createElement("button"); dl.type = "button"; dl.className = "ghost small"; dl.textContent = "↓"; dl.title = `Download ${f.name}`; dl.setAttribute("aria-label", `Download ${f.name}`);
        dl.addEventListener("click", async () => download(await Lvz.fileData(f), f.name, "application/octet-stream"));
        const rm = document.createElement("button"); rm.type = "button"; rm.className = "ghost small danger"; rm.textContent = "×"; rm.title = `Remove ${f.name}`; rm.setAttribute("aria-label", `Remove ${f.name}`);
        rm.addEventListener("click", async () => {
          const used = lvz.images.some((im) => im.file.toLowerCase() === f.name.toLowerCase());
          if (!(await ask(`Remove ${f.name} from the .lvz?${used ? " Images that use it will show nothing in game unless another .lvz has it." : ""}`, { ok: "Remove", danger: true }))) return;
          lvz.files = lvz.files.filter((x) => x !== f); setDirty(true); await refreshPics(); renderAll();
        });
        li.append(n, size, dl, rm); ul.append(li);
      }
    }
    function objLabel(o) {
      const im = lvz.images[o.image];
      const where = o.map ? `map ${o.x}, ${o.y}` : `screen ${(Lvz.REFS[o.xRef] || [""])[0]}${o.x}, ${(Lvz.REFS[o.yRef] || [""])[0]}${o.y}`;
      return `#${o.id} ${im ? im.file : "image " + o.image} · ${where}`;
    }
    function renderObjects() {
      const ul = q("lz-objs"), filter = q("lz-filter").value; ul.replaceChildren();
      const maps = lvz.objects.filter((o) => o.map).length;
      q("lz-objcount").textContent = `${lvz.objects.length} (${lvz.objects.length - maps} screen, ${maps} map)`;
      let shown = 0;
      lvz.objects.forEach((o, i) => {
        if ((filter === "map" && !o.map) || (filter === "screen" && o.map)) return;
        if (shown++ >= 400) return;
        const li = document.createElement("li");
        const b = document.createElement("button"); b.type = "button"; b.className = "ghost small open" + (i === sel ? " on" : "");
        b.textContent = objLabel(o); b.title = `${Lvz.LAYERS[o.layer] || ""} · ${MODE_TEXT[o.mode] || ""}`;
        b.addEventListener("click", () => { select(i); if (o.map !== (tab === "map")) setTab(o.map ? "map" : "screen"); });
        li.append(b); ul.append(li);
      });
      if (shown > 400) { const li = document.createElement("li"); li.className = "dim"; li.textContent = `…and ${shown - 400} more.`; ul.append(li); }
      if (!lvz.objects.length) { const li = document.createElement("li"); li.className = "dim"; li.textContent = "No objects."; ul.append(li); }
    }
    function renderForm() {
      const o = lvz.objects[sel];
      form.hidden = !o;
      if (!o) return;
      F("map").value = String(o.map); F("image").value = o.image; F("id").value = o.id;
      F("xRef").value = o.xRef; F("yRef").value = o.yRef; F("x").value = o.x; F("y").value = o.y;
      F("layer").value = o.layer; F("mode").value = o.mode; F("time").value = (o.time / 10).toFixed(1);
      for (const el of form.querySelectorAll(".lz-scr")) el.hidden = o.map;
      q("lz-mapnote").hidden = !o.map;
      F("x").min = o.map ? 0 : -2048; F("x").max = o.map ? 16384 : 2047; F("y").min = F("x").min; F("y").max = F("x").max;
    }
    form.addEventListener("change", (e) => {
      const o = lvz.objects[sel], k = e.target.dataset.f;
      if (!o || !k) return;
      const v = e.target.value;
      if (k === "map") { o.map = v === "true"; if (o.map) { o.x = 8192; o.y = 8192; } else { o.xRef = 1; o.yRef = 1; o.x = 0; o.y = 0; } }
      else if (k === "time") o.time = Math.max(0, Math.min(4095, Math.round(+v * 10)));
      else if (k === "id") o.id = Math.max(0, Math.min(32767, Math.round(+v) || 0));
      else o[k] = Math.round(+v) || 0;
      setDirty(true); renderAll();
    });
    function select(i) { sel = i; renderObjects(); renderForm(); drawScreen(); drawMap(); }
    function renderAll() {
      q("lz-name").textContent = src.name + (src.from === "arena" ? ` (on ${arena})` : src.from === "lib" ? " (Your LVZ)" : "");
      renderImages(); renderFiles(); renderObjects(); renderForm(); drawScreen(); drawMap(); updateLinks();
      q("lz-publish").disabled = !owner || !arena;
    }
    async function refreshPics() { pics = await Lvz.pictures(lvz); }

    // ---- screen preview ----------------------------------------------------------------------------
    const screen = q("lz-screen");
    const screenSize = () => q("lz-size").value.split("x").map(Number);
    function screenPos(o) { const [w, h] = screenSize(), [ax, ay] = Lvz.anchor(o.xRef, w, h), [bx, by] = Lvz.anchor(o.yRef, w, h); return [ax + o.x, by + o.y]; }
    function objSize(o) { const p = pics[o.image]; return p ? [p.fw, p.fh] : [32, 32]; }
    function drawScreen() {
      if (tab !== "screen") return;
      const [w, h] = screenSize();
      if (screen.width !== w) screen.width = w;
      if (screen.height !== h) screen.height = h;
      const ctx = screen.getContext("2d"), t = performance.now();
      ctx.imageSmoothingEnabled = false;
      ctx.fillStyle = "#03060c"; ctx.fillRect(0, 0, w, h);
      ctx.strokeStyle = "#ffffff10"; for (let x = 0; x < w; x += 64) { ctx.beginPath(); ctx.moveTo(x + 0.5, 0); ctx.lineTo(x + 0.5, h); ctx.stroke(); }
      for (let y = 0; y < h; y += 64) { ctx.beginPath(); ctx.moveTo(0, y + 0.5); ctx.lineTo(w, y + 0.5); ctx.stroke(); }
      const anim = q("lz-anim").checked;
      const order = lvz.objects.map((o, i) => [o, i]).filter(([o]) => !o.map).sort((a, b) => a[0].layer - b[0].layer);
      for (const [o, i] of order) {
        const [x, y] = screenPos(o), im = lvz.images[o.image];
        Lvz.drawFrame(ctx, pics[o.image], im || {}, anim && im ? Lvz.frameAt(im, t) : 0, x, y, 1, im ? im.file : "?");
        if (i === sel) { const [fw, fh] = objSize(o); ctx.strokeStyle = "#ffffff"; ctx.lineWidth = 2; ctx.strokeRect(x - 1, y - 1, fw + 2, fh + 2); ctx.lineWidth = 1; }
      }
      if (q("lz-anchors").checked) {
        ctx.font = "bold 13px sans-serif";
        Lvz.REFS.forEach(([p], r) => {
          const [ax, ay] = Lvz.anchor(r, w, h);
          ctx.fillStyle = "#ff5a5a"; ctx.fillRect(ax - 2, ay - 2, 4, 4);
          ctx.fillText(p || "0", Math.min(w - 12, Math.max(2, ax + 4)), Math.min(h - 4, Math.max(14, ay + 14)));
        });
        const o = lvz.objects[sel];
        if (o && !o.map) { // a line from the selected object's reference points to it
          const [x, y] = screenPos(o), [ax] = Lvz.anchor(o.xRef, w, h), [, by] = Lvz.anchor(o.yRef, w, h);
          ctx.strokeStyle = "#ffcf7a"; ctx.setLineDash([4, 4]); ctx.beginPath(); ctx.moveTo(ax, by); ctx.lineTo(x, y); ctx.stroke(); ctx.setLineDash([]);
        }
      }
    }
    const screenAt = (e) => { const r = screen.getBoundingClientRect(); return [((e.clientX - r.left) * screen.width) / r.width, ((e.clientY - r.top) * screen.height) / r.height]; };
    function hitScreen(px, py) {
      const list = lvz.objects.map((o, i) => [o, i]).filter(([o]) => !o.map).sort((a, b) => b[0].layer - a[0].layer || b[1] - a[1]);
      for (const [o, i] of list) { const [x, y] = screenPos(o), [fw, fh] = objSize(o); if (px >= x && py >= y && px < x + fw && py < y + fh) return i; }
      return -1;
    }
    screen.addEventListener("pointerdown", (e) => {
      const [px, py] = screenAt(e), i = hitScreen(px, py);
      select(i);
      if (i < 0) return;
      const o = lvz.objects[i];
      drag = { i, sx: px, sy: py, ox: o.x, oy: o.y };
      screen.setPointerCapture(e.pointerId);
    });
    screen.addEventListener("pointermove", (e) => {
      if (!drag) { const [px, py] = screenAt(e); screen.style.cursor = hitScreen(px, py) >= 0 ? "move" : "default"; return; }
      const [px, py] = screenAt(e), o = lvz.objects[drag.i];
      o.x = Math.max(-2048, Math.min(2047, Math.round(drag.ox + px - drag.sx))); o.y = Math.max(-2048, Math.min(2047, Math.round(drag.oy + py - drag.sy)));
      setDirty(true); drawScreen(); renderForm();
    });
    screen.addEventListener("pointerup", () => { if (drag) { drag = null; renderObjects(); } });
    screen.tabIndex = 0;
    screen.addEventListener("keydown", (e) => {
      const o = lvz.objects[sel]; if (!o || o.map) return;
      const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
      if (d) { e.preventDefault(); const k = e.shiftKey ? 10 : 1; o.x += d[0] * k; o.y += d[1] * k; setDirty(true); drawScreen(); renderForm(); }
      if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); q("lz-del").click(); }
    });
    for (const c of ["lz-size", "lz-anchors"]) q(c).addEventListener("change", drawScreen);
    (function tick() { if (root.isConnected && tab === "screen" && q("lz-anim").checked && root.getClientRects().length && lvz.images.some((im) => (im.cols || 1) * (im.rows || 1) > 1)) drawScreen(); setTimeout(tick, 100); })();

    // ---- map overview -------------------------------------------------------------------------------
    const mapCanvas = q("lz-mapview");
    async function loadMapGrid() {
      mapGrid = null;
      if (!arena || !window.Lvl) return;
      const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" }).catch(() => null);
      if (r && r.ok) mapGrid = Lvl.parse(new Uint8Array(await r.arrayBuffer())).grid;
      drawMap();
    }
    function drawMap() {
      if (tab !== "map") return;
      const ctx = mapCanvas.getContext("2d"), W = mapCanvas.width, k = W / Lvz.MAP_PIXELS;
      if (mapGrid && window.Lvl) Lvl.drawOverview(mapCanvas, mapGrid); else { ctx.fillStyle = "#04070e"; ctx.fillRect(0, 0, W, W); }
      lvz.objects.forEach((o, i) => {
        if (!o.map) return;
        const [fw, fh] = objSize(o);
        ctx.fillStyle = i === sel ? "#ffffff" : "#ffcf7a";
        ctx.fillRect(Math.floor(o.x * k), Math.floor(o.y * k), Math.max(2, fw * k), Math.max(2, fh * k));
      });
      const n = lvz.objects.filter((o) => o.map).length;
      q("lz-maphint").textContent = n ? `${n} map object${n === 1 ? "" : "s"}, shown in yellow on ${arena ? arena + "'s" : "the"} map. Place, drag, copy and delete them on the map itself:` : "No map objects yet. Add them on the map itself, where you can see where they go:";
    }
    function updateLinks() {
      const p = new URLSearchParams();
      if (arena) p.set("arena", arena);
      if (src.from === "lib" && src.libId) p.set("lvzlib", src.libId);
      else if (src.from === "arena") p.set("lvz", src.name);
      q("lz-onmap").href = "/arena/map?" + p.toString();
      q("lz-onmap").title = src.from === "new" || src.from === "file" ? "Save it to Your LVZ first, so the map editor can open it" : "";
    }
    function setTab(t) {
      tab = t;
      for (const b of root.querySelectorAll(".lz-tabs button")) { const on = b.dataset.tab === t; b.classList.toggle("on", on); b.setAttribute("aria-selected", String(on)); }
      for (const p of root.querySelectorAll(".lz-pane")) p.hidden = p.dataset.pane !== t;
      drawScreen(); drawMap();
    }
    for (const b of root.querySelectorAll(".lz-tabs button")) b.addEventListener("click", () => setTab(b.dataset.tab));
    q("lz-filter").addEventListener("change", renderObjects);

    // ---- objects ------------------------------------------------------------------------------------
    const nextId = () => lvz.objects.reduce((m, o) => Math.max(m, o.id), 0) + 1;
    function needImage() { if (lvz.images.length) return true; say("Add an image first: objects show one.", false); return false; }
    q("lz-addscreen").addEventListener("click", () => {
      if (!needImage()) return;
      const img = lvz.objects[sel] ? lvz.objects[sel].image : 0;
      lvz.objects.push(Lvz.newObject({ map: false, xRef: 1, yRef: 1, x: -16, y: -16, image: img, layer: 6, mode: 0, id: nextId() }));
      setDirty(true); setTab("screen"); select(lvz.objects.length - 1);
    });
    q("lz-addmap").addEventListener("click", () => {
      if (!needImage()) return;
      const img = lvz.objects[sel] ? lvz.objects[sel].image : 0;
      lvz.objects.push(Lvz.newObject({ map: true, x: 8192, y: 8192, image: img, layer: 2, id: nextId() }));
      setDirty(true); setTab("map"); select(lvz.objects.length - 1);
      say("Added at the map's centre (8192, 8192). Drag it into place in the map editor, or type its position.", true);
    });
    q("lz-dup").addEventListener("click", () => {
      const o = lvz.objects[sel]; if (!o) return;
      const c = Object.assign({}, o, { id: nextId() }); delete c.fromClv1;
      if (c.map) { c.x += 16; c.y += 16; } else { c.x += 8; c.y += 8; }
      lvz.objects.push(c); setDirty(true); select(lvz.objects.length - 1);
    });
    q("lz-del").addEventListener("click", () => {
      if (!lvz.objects[sel]) return;
      lvz.objects.splice(sel, 1); sel = Math.min(sel, lvz.objects.length - 1); setDirty(true); renderAll();
    });

    // ---- images and files ---------------------------------------------------------------------------
    q("lz-addimg").addEventListener("change", async (e) => {
      const files = [...e.target.files]; e.target.value = "";
      for (const f of files) {
        const name = Lvz.safeName(f.name), bytes = new Uint8Array(await f.arrayBuffer());
        try { await Lvz.imageCanvas(name, bytes); } catch { say(`${f.name} couldn't be read as an image.`, false); continue; }
        Lvz.putFile(lvz, name, bytes);
        if (!lvz.images.some((im) => im.file.toLowerCase() === name.toLowerCase())) lvz.images.push({ file: name, cols: 1, rows: 1, time: 100 });
      }
      if (lvz.images.length > 256) say("Objects can only use the first 256 images.", false);
      setDirty(true); await refreshPics(); renderAll();
    });
    q("lz-addfile").addEventListener("change", async (e) => {
      const files = [...e.target.files]; e.target.value = "";
      for (const f of files) Lvz.putFile(lvz, Lvz.safeName(f.name), new Uint8Array(await f.arrayBuffer()));
      setDirty(true); await refreshPics(); renderAll();
    });

    // ---- open ---------------------------------------------------------------------------------------
    async function confirmDiscard() { return !dirty || ask("Discard the unsaved changes to this .lvz?", { ok: "Discard", danger: true }); }
    async function useLvz(next, from) {
      lvz = next; src = from; sel = -1; setDirty(false);
      await refreshPics(); renderAll();
      const w = lvz.warnings || [];
      say(`Opened ${src.name}: ${lvz.files.length} files, ${lvz.images.length} images, ${lvz.objects.length} objects.${w.length ? " " + w.join(" ") : ""}`, !w.length);
    }
    async function fetchBytes(url) { const r = await fetch(url, { credentials: "same-origin" }).catch(() => null); return r && r.ok ? { bytes: new Uint8Array(await r.arrayBuffer()), r } : null; }
    async function openChoice(v) {
      const kind = v.slice(0, v.indexOf(":")), id = v.slice(v.indexOf(":") + 1);
      try {
        if (kind === "arena") {
          const f = await fetchBytes(`${API}/lvz/arena?arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(id)}`);
          if (!f) { say(`Couldn't read ${id} from ${arena}.`, false); return; }
          await useLvz(await Lvz.read(f.bytes), { name: id, from: "arena", libId: null });
        } else if (kind === "lib") {
          const f = await fetchBytes(`${API}/lvzlib/file?id=${encodeURIComponent(id)}`);
          if (!f) { say("Couldn't open that .lvz from Your LVZ.", false); return; }
          await useLvz(await Lvz.read(f.bytes), { name: decodeURIComponent(f.r.headers.get("X-Lvz-Name") || "graphics.lvz"), from: "lib", libId: +id });
        }
      } catch (err) { say(err.message, false); }
    }
    q("lz-open").addEventListener("change", async (e) => { const v = e.target.value; e.target.value = ""; if (v && (await confirmDiscard())) openChoice(v); });
    q("lz-file").addEventListener("change", async (e) => {
      const files = [...e.target.files]; e.target.value = "";
      if (!files.length || !(await confirmDiscard())) return;
      try {
        const lvzFile = files.find((f) => /\.lvz$/i.test(f.name)), ini = files.find((f) => /\.ini$/i.test(f.name));
        if (lvzFile) { await useLvz(await Lvz.read(new Uint8Array(await lvzFile.arrayBuffer())), { name: ArenaSafe(lvzFile.name), from: "file", libId: null }); setDirty(true); return; }
        if (!ini) { say("Choose an .lvz, or an .ini project together with its image files.", false); return; }
        const map = new Map();
        for (const f of files) if (f !== ini) map.set(f.name.toLowerCase(), new Uint8Array(await f.arrayBuffer()));
        const r = Lvz.fromIni(await ini.text(), map);
        await useLvz(r.lvz, { name: ArenaSafe(r.outFile || ini.name.replace(/\.ini$/i, ".lvz")), from: "file", libId: null });
        setDirty(true);
        const notes = [...r.errors.slice(0, 4)];
        if (r.missing.length) notes.push(`Not chosen with it: ${r.missing.slice(0, 6).join(", ")}${r.missing.length > 6 ? "…" : ""} (choose them too, or add them under Files).`);
        say(`${ini.name}: ${r.lvz.images.length} images, ${r.lvz.objects.length} objects.${notes.length ? " " + notes.join(" ") : ""}`, !notes.length);
      } catch (err) { say(err.message, false); }
    });
    q("lz-new").addEventListener("click", async () => {
      if (!(await confirmDiscard())) return;
      const n = await askText("Name of the new .lvz", { value: "decor.lvz", ok: "Make it" });
      if (!n) return;
      await useLvz(Lvz.blank(), { name: ArenaSafe(n), from: "new", libId: null });
      say("A new, empty .lvz: add images, then objects.", true);
    });
    function ArenaSafe(n) { // the file name an arena will store it under (as the server would make it)
      let s = String(n).replace(/^.*[\\/]/, "").replace(/\.lvz$/i, "").replace(/[^A-Za-z0-9._-]/g, "_").replace(/^[._]+|[._]+$/g, "").slice(0, 28);
      return (s || "graphics") + ".lvz";
    }

    // ---- lists of what can be opened ----------------------------------------------------------------
    let libList = [], levelFiles = opts.levelFiles || [];
    async function loadLists() {
      const r = await fetch(API + "/lvzlib", { credentials: "same-origin" }).catch(() => null);
      const j = r && r.ok ? await r.json().catch(() => null) : null;
      libList = (j && j.files) || [];
      const open = q("lz-open"); open.replaceChildren(new Option("Open…", ""));
      const group = (label, items) => { if (!items.length) return; const g = document.createElement("optgroup"); g.label = label; for (const [v, t] of items) g.append(new Option(t, v)); open.append(g); };
      group(arena ? `On ${arena}` : "This arena", levelFiles.filter((n) => /\.lvz$/i.test(n)).map((n) => ["arena:" + n, n]));
      group("Your LVZ (drafts)", libList.map((x) => ["lib:" + x.id, `${x.name} · ${kb(x.size)}`]));
      q("lz-save").disabled = !(r && r.ok);
      q("lz-saveas").disabled = !(r && r.ok);
      if (r && r.status === 401) say("Sign in to keep .lvz files in Your LVZ.", false);
    }

    // ---- save, download, publish ------------------------------------------------------------------------
    function download(bytes, name, type) {
      const a = document.createElement("a"); a.href = URL.createObjectURL(new Blob([bytes], { type })); a.download = name;
      document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    }
    async function bytes() {
      const b = await Lvz.write(lvz);
      if (b.length > 5 * 1024 * 1024) throw new Error(`It's ${kb(b.length)}; an arena's .lvz files must be 5 MB or smaller.`);
      return b;
    }
    async function save(asNew) {
      let name = src.name, id = asNew ? null : src.from === "lib" ? src.libId : null;
      if (asNew || !id) { const n = await askText("Save in Your LVZ as", { value: name, ok: "Save" }); if (!n) return; name = ArenaSafe(n); }
      try {
        const r = await fetch(`${API}/lvzlib/save?name=${encodeURIComponent(name)}${id ? "&id=" + id : ""}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: await bytes() });
        const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
        say(j.ok ? `${j.message} It's a draft: ${arena || "the arena"} doesn't change until you Publish.` : j.message, j.ok);
        if (j.ok) { src = { name: j.name || name, from: "lib", libId: j.id }; setDirty(false); await loadLists(); renderAll(); }
      } catch (err) { say(err.message, false); }
    }
    q("lz-save").addEventListener("click", () => save(false));
    q("lz-saveas").addEventListener("click", () => save(true));
    q("lz-dl").addEventListener("click", async () => { try { download(await bytes(), src.name, "application/octet-stream"); } catch (err) { say(err.message, false); } });
    q("lz-dlini").addEventListener("click", () => download(new TextEncoder().encode(Lvz.toIni(lvz, src.name)), src.name.replace(/\.lvz$/i, ".ini"), "text/plain"));
    q("lz-publish").addEventListener("click", async () => {
      if (!arena || !owner) return;
      const replaces = levelFiles.some((n) => n.toLowerCase() === src.name.toLowerCase());
      if (!(await ask(`Publish ${src.name} to ${arena}? ${replaces ? "It replaces the arena's file of that name." : "It's added to the arena's .lvz files."} Players get it when the arena restarts.`, { title: "Publish .lvz", ok: "Publish" }))) return;
      try {
        const r = await fetch(`${API}/upload?kind=lvz&arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(src.name)}`, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: await bytes() });
        const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
        say(j.ok ? `Published ${src.name} to ${arena}.` : j.message, j.ok);
        if (j.ok) { if (src.from !== "lib") { src = { name: src.name, from: "arena", libId: null }; setDirty(false); } if (opts.onPublished) opts.onPublished(src.name); }
      } catch (err) { say(err.message, false); }
    });

    renderAll();
    const ready = loadLists();
    return {
      ready,
      /** The arena editor calls this when the selected arena (or its data) changes. */
      async setArena(name, { owner: own, levelFiles: lf } = {}) {
        await ready;
        const changed = name !== arena;
        arena = name || ""; owner = !!own; levelFiles = lf || [];
        await loadLists();
        if (changed) { await loadMapGrid(); if (!dirty && src.from !== "lib") { const first = levelFiles.find((n) => /\.lvz$/i.test(n)); if (first) await openChoice("arena:" + first); else renderAll(); } }
        else renderAll();
      },
    };
  }
  return { mount };
})();
