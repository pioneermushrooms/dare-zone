// Map editor (/arena/map?arena=<name>): opens the arena's current map, edits it in the browser,
// and Publish sends it through the upload API (owner only). A public arena that has a draft changes
// only by publishing (the live-edit lock): the editor edits the draft #name, and Publish saves the
// map there and then publishes the draft to the live arena (/drafts/publish). A draft or an arena
// without a draft is saved and recycled directly.
// Text goes in with textContent only.
"use strict";

// Sign-in that comes back here: /account/login?next=<this page>, plus a short-lived dare_next
// cookie so a ?weblogin link (opened from the game) lands here too.
function signInHref(page = "/account/login") {
  const here = location.pathname + location.search + location.hash;
  try {
    document.cookie = "dare_next=" + encodeURIComponent(here) + "; Path=/; Max-Age=600; SameSite=Lax"
      + (location.protocol === "https:" ? "; Secure" : "");
  } catch { /* no cookies */ }
  return page + "?next=" + encodeURIComponent(here);
}
function signedIn() {
  if (document.cookie.includes("dare_next=")) {
    try { document.cookie = "dare_next=; Path=/; Max-Age=0; SameSite=Lax"; } catch { /* no cookies */ }
  }
}

(() => {
  const { N, TS, SPECIAL } = Lvl;
  const API = "/arena/api";
  const $ = (id) => document.getElementById(id);

  let grid = new Uint8Array(N * N), prefix = null, mapTileset = null, ownTileset = null;
  let defaultTileset = null; // Continuum's classic tiles.bm2: what players see when a map has no tileset
  let arena = new URLSearchParams(location.search).get("arena") || "";
  let role = null, tool = "pencil", cur = 1, dirty = false;
  let liveName = null; // set when editing the draft of a live arena: Publish also publishes it
  const cam = { x: 512 * TS, y: 512 * TS, z: 0.5 };
  const undoStack = [], redoStack = [];
  let flatView = false; // "Plain colours": ignore the map's tileset
  let libId = null, libName = null; // the map from "Your maps" being edited, if any
  let roleView = null; // "Show wall roles": a drawn tileset showing each tile's auto-tiling role
  const tileset = () => roleView || (flatView ? null : (ownTileset || mapTileset || defaultTileset));

  const say = (text, ok) => { const m = $("msg"); m.textContent = text || ""; m.className = "edmsg " + (ok ? "ok" : "bad"); };
  const setDirty = (d) => { dirty = d; $("publish").disabled = !d || role !== "owner"; $("drafttag").hidden = !d; };

  // Walls placed in flat-coloured (unused) tileset slots show as solid colour, here and in game,
  // unless the arena's .lvz draws over them. Say so, so it isn't mistaken for the editor's doing.
  function noteBlankSlots() {
    const note = $("blanknote");
    const blank = ownTileset ? new Map() : Lvl.blankSlots(mapTileset);
    let tiles = 0;
    const kinds = new Set();
    if (blank.size) for (let i = 0; i < N * N; i++) if (blank.has(grid[i])) { tiles++; kinds.add(grid[i]); }
    note.hidden = tiles === 0;
    if (tiles) note.textContent = `${tiles.toLocaleString()} wall tiles (${kinds.size} kinds) sit in slots this map's own tileset fills with one flat colour, so they show as solid blocks here and in game. Maps like this usually come with a .lvz graphics file that draws over them: add it under Graphics. Plain colours shows them as ordinary walls.`;
  }

  function noteTileset() {
    $("tilesetnote").textContent = ownTileset ? "Tiles from your own tileset (only in your browser)."
      : mapTileset ? "Tiles from this map's own tileset."
      : defaultTileset ? "This map has no tileset of its own: showing the standard tiles players see by default."
      : "This map has no tileset of its own: tiles show as flat colours.";
  }
  async function loadDefaultTileset() {
    try {
      const r = await fetch("/arena/tiles.bm2");
      if (r.ok) defaultTileset = Lvl.decodeBmp(new Uint8Array(await r.arrayBuffer()));
    } catch { defaultTileset = null; }
  }
  // Generated wall art (tileart.js): paint blank slots or restyle every wall; changes the map's
  // own tileset, so it is published with the map.
  const tileArt = window.TileArt && TileArt.mount($("tileart"), {
    get: () => ({ grid, prefix, tileset: mapTileset, blank: Lvl.blankSlots(mapTileset) }),
    set: (p, ts, note) => {
      prefix = p; mapTileset = ts; ownTileset = null; flatView = false; $("flatview").checked = false;
      noteTileset(); setDirty(true); noteBlankSlots(); drawPalette(); draw(); say(note, true);
    },
  });

  // "Use the standard tiles": the standard tileset (the packs' art, with plain interiors for thick
  // walls) into this map, so the packs look here and in game as they're meant to
  $("stdtiles").addEventListener("click", async () => {
    const r = await fetch("/arena/standard-tiles.bmp", { credentials: "same-origin" }).catch(() => null);
    if (!r || !r.ok || !window.TileArt) { say("Couldn't load the standard tiles.", false); return; }
    useTileset(Lvl.decodeBmp(new Uint8Array(await r.arrayBuffer())),
      "This map now uses the standard tiles (published with it). Its other tiles show as the standard set draws them.");
  });
  // A tileset into this map's own prefix (eLVL data kept), so it is published with the map.
  function useTileset(ts, note) {
    const rgba = ts.getContext("2d").getImageData(0, 0, ts.width, ts.height).data;
    prefix = TileArt.withTileset(prefix, rgba, ts.width, ts.height); mapTileset = ts; ownTileset = null;
    flatView = false; $("flatview").checked = false;
    noteTileset(); setDirty(true); noteBlankSlots(); drawPalette(); draw();
    say(note, true);
  }

  // "Use one of your tilesets": one from Your tilesets (the tile editor's library, or kept from the Foundry) into this map.
  async function loadMyTilesets() {
    const sel = $("mytilesets");
    if (!sel) return;
    const r = await fetch(API + "/tilesets", { credentials: "same-origin" }).catch(() => null);
    const j = r && r.ok ? await r.json().catch(() => null) : null;
    const list = (j && j.tilesets) || [];
    sel.replaceChildren(new Option(list.length ? "Use one of your tilesets…" : "No saved tilesets yet", ""),
      ...list.map((t) => new Option(t.name, String(t.id))));
    sel.disabled = !list.length;
  }
  async function applyMyTileset(id) {
    if (!window.TileArt) return;
    const r = await fetch(API + "/tilesets/file?id=" + encodeURIComponent(id), { credentials: "same-origin" }).catch(() => null);
    if (!r || !r.ok) { say("Couldn't open that tileset from your tilesets.", false); return; }
    const name = decodeURIComponent(r.headers.get("X-Tileset-Name") || "your tileset");
    let ts;
    try { ts = Lvl.decodeBmp(new Uint8Array(await r.arrayBuffer())); } catch (err) { say(`That tileset could not be read: ${err.message}`, false); return; }
    if (!await Dlg.confirm(`Use ${name} as this map's tileset? It goes into the map you're editing; the arena keeps its current map until you Publish.`, { title: "Use this tileset?", ok: "Use it" })) return;
    useTileset(ts, `This map now uses ${name} (published with it). Publish to put it in the arena.`);
  }
  $("mytilesets")?.addEventListener("change", async (e) => {
    const id = e.target.value;
    e.target.value = "";
    if (id) await applyMyTileset(Number(id));
  });

  // ---- load --------------------------------------------------------------------------------------
  async function load() {
    await loadDefaultTileset();
    const me = await fetch(API + "/me" + (arena ? "?arena=" + encodeURIComponent(arena) : ""), { credentials: "same-origin" });
    if (me.status === 401) { location.href = signInHref(); return; }
    signedIn();
    const body = await me.json().catch(() => null);
    if (!me.ok || !body || !body.arena) { say((body && body.message) || "You don't own or co-mod an arena yet.", false); return; }
    arena = body.arena; role = body.role;
    $("enterarena").href = enterUrl(arena);
    $("edname").textContent = `${arena} · ${body.map || "no map"}`;
    document.title = `${arena} map · SubSpace Foundation`;
    if (role !== "owner") say("You can look and download; only the owner can publish.", false);

    const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" });
    if (r.status === 404) { say("No map file yet: draw one from scratch.", true); }
    else if (!r.ok) { const j = await r.json().catch(() => null); say((j && j.message) || "Could not open the map.", false); }
    else {
      const m = Lvl.parse(new Uint8Array(await r.arrayBuffer()));
      grid = m.grid; prefix = m.prefix; mapTileset = m.tileset;
      say(`Opened ${r.headers.get("X-Map-Name") || "the map"}: ${m.count} tiles.`, true);
    }
    noteTileset();
    noteBlankSlots();
    if (tileArt) tileArt.forget();
    centre();
    drawPalette(); drawMini(); draw();
    setDirty(false);
  }

  // ---- start from a ready-made map, or a random one (one undoable change) -------------------------
  async function replaceMap(next, nextPrefix, nextTileset, note) {
    if (dirty && !await Dlg.confirm("Replace the map you're editing? Undo brings it back.", { title: "Replace the map?", ok: "Replace" })) return false;
    const change = [];
    for (let i = 0; i < N * N; i++) if (grid[i] !== next[i]) change.push([i, grid[i], next[i]]);
    for (const [i, , t] of change) grid[i] = t;
    if (change.length) { undoStack.push(change); redoStack.length = 0; setDirty(true); }
    if (nextPrefix !== undefined) { prefix = nextPrefix; mapTileset = nextTileset; noteTileset(); drawPalette(); if (tileArt) tileArt.forget(); }
    noteBlankSlots();
    if (tileArt) tileArt.refresh();
    centre(); drawMini(); draw();
    say(note, true);
    return true;
  }

  // ---- your maps (the pilot's collection): open one as a draft, rename, delete, add a file, save ----
  // Every map the pilot adds, downloads or publishes is kept there (the same bytes only once).
  const hdrs = (type) => Object.assign({ "X-Requested-With": "dare-arena" }, type ? { "Content-Type": type } : {});
  const postJson = async (path, body) => {
    const r = await fetch(API + path, { method: "POST", credentials: "same-origin", headers: hdrs("application/json"), body: JSON.stringify(body) }).catch(() => null);
    return r ? r.json().catch(() => ({ ok: false, message: "HTTP " + r.status })) : { ok: false, message: "No connection." };
  };
  const kb = (n) => n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB";
  const ago = (iso) => {
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (!(s >= 0)) return "";
    if (s < 90) return "just now";
    if (s < 5400) return Math.round(s / 60) + " min ago";
    if (s < 129600) return Math.round(s / 3600) + " h ago";
    return new Date(iso).toLocaleDateString();
  };
  const smallBtn = (text, label, onClick, cls) => {
    const b = document.createElement("button");
    b.type = "button"; b.className = "ghost small " + (cls || ""); b.textContent = text; b.setAttribute("aria-label", label); b.title = label;
    b.addEventListener("click", onClick);
    return b;
  };
  async function loadLibrary() {
    const r = await fetch(API + "/library", { credentials: "same-origin" }).catch(() => null);
    const j = r && r.ok ? await r.json().catch(() => null) : null;
    const list = $("libmaps");
    list.replaceChildren();
    const maps = (j && j.maps) || [];
    $("libcount").textContent = j ? `${maps.length}/${j.max}` : "";
    for (const m of maps) {
      const open = document.createElement("button");
      open.type = "button"; open.className = "ghost small open";
      open.title = `Open ${m.name} here as a draft for ${arena || "your arena"}`;
      const nm = document.createElement("span"); nm.className = "libname"; nm.textContent = m.name;
      const meta = document.createElement("span"); meta.className = "libmeta"; meta.textContent = `${kb(m.size)} · ${ago(m.updated)}`;
      open.append(nm, meta);
      open.addEventListener("click", () => openLib(m.id));
      const li = document.createElement("li");
      if (m.id === libId) { li.className = "on"; open.setAttribute("aria-current", "true"); }
      li.append(open,
        smallBtn("Rename", `Rename ${m.name}`, () => renameLib(m)),
        smallBtn("Delete", `Delete ${m.name} from your maps`, () => removeLib(m), "danger"));
      list.append(li);
    }
    if (!maps.length) {
      const li = document.createElement("li"); li.className = "dim";
      li.textContent = j ? "None yet. Add a .lvl, or Save to my maps." : "Couldn't load your maps.";
      list.append(li);
    }
  }
  async function renameLib(m) {
    const name = await Dlg.prompt(`A new name for ${m.name}:`, { title: "Rename map", value: m.name, ok: "Rename" });
    if (name === null || !name.trim() || name.trim() === m.name) return;
    const j = await postJson("/library/rename", { id: m.id, name: name.trim() });
    say(j.message, j.ok);
    if (j.ok && m.id === libId) { libName = j.message.replace(/^Renamed it (.*)\.$/, "$1"); $("edname").textContent = `${arena} · draft from your maps: ${libName}`; }
    loadLibrary();
  }
  async function removeLib(m) {
    if (!await Dlg.confirm(`Delete ${m.name} from your maps? Arenas using it keep their copy.`, { title: "Delete map", ok: "Delete", danger: true })) return;
    const j = await postJson("/library/remove", { id: m.id });
    say(j.message, j.ok);
    if (j.ok && m.id === libId) { libId = null; libName = null; }
    loadLibrary();
  }
  async function openLib(id) {
    const r = await fetch(API + "/library/file?id=" + encodeURIComponent(id), { credentials: "same-origin" });
    if (!r.ok) { say("Couldn't open that map from your maps.", false); return; }
    const name = decodeURIComponent(r.headers.get("X-Map-Name") || "map");
    const m = Lvl.parse(new Uint8Array(await r.arrayBuffer()));
    const changed = await replaceMap(m.grid, m.prefix, m.tileset,
      `Draft from your maps: ${name} (${m.count} tiles). ${arena || "The arena"} keeps its current map until you Publish.`);
    if (changed === false) return;
    libId = id; libName = name;
    $("edname").textContent = `${arena} · draft from your maps: ${name}`;
    setDirty(true); // a draft until published, even if it matches
    loadLibrary();
  }
  // a copy of a downloaded map (same bytes only once); the editor keeps editing what it was editing
  async function keepCopy(bytes, name) {
    const r = await fetch(API + "/library/save?name=" + encodeURIComponent(name), { method: "POST", credentials: "same-origin", headers: hdrs("application/octet-stream"), body: bytes }).catch(() => null);
    const j = r ? await r.json().catch(() => null) : null;
    loadLibrary();
    return j;
  }
  $("libjump").addEventListener("click", () => {
    const p = $("libpanel");
    p.scrollIntoView({ block: "start", behavior: "smooth" });
    p.classList.remove("flash"); void p.offsetWidth; p.classList.add("flash");
    const first = p.querySelector("button.open");
    if (first) first.focus({ preventScroll: true });
  });
  $("libfile").addEventListener("change", async (e) => {
    const f = e.target.files[0];
    e.target.value = "";
    if (!f) return;
    if (!f.name.toLowerCase().endsWith(".lvl")) { say("Choose a .lvl file.", false); return; }
    const r = await fetch(API + "/library/save?name=" + encodeURIComponent(f.name), { method: "POST", credentials: "same-origin", headers: hdrs("application/octet-stream"), body: f });
    const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
    if (!j.ok) { say(j.message, false); return; }
    await openLib(j.id);
  });
  $("libsave").addEventListener("click", async () => {
    const bytes = Lvl.serialize(grid, prefix);
    if (!bytes.length) { say("The map is empty; draw something first.", false); return; }
    const name = libName || (arena ? arena.replace(/^#/, "") + " draft" : "draft");
    const url = API + "/library/save?name=" + encodeURIComponent(name) + (libId ? "&id=" + libId : "");
    const r = await fetch(url, { method: "POST", credentials: "same-origin", headers: hdrs("application/octet-stream"), body: bytes });
    const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
    if (j.ok) { libId = j.id; libName = name; }
    say(j.ok ? `${j.message} Not published: ${arena} still has its old map.` : j.message, j.ok);
    loadLibrary();
  });
  $("flatview").addEventListener("change", (e) => { flatView = e.target.checked; drawPalette(); draw(); });

  async function loadShared() {
    const r = await fetch(API + "/maps", { credentials: "same-origin" }).catch(() => null);
    const j = r && r.ok ? await r.json().catch(() => null) : null;
    for (const name of (j && j.maps) || []) {
      const o = document.createElement("option");
      o.value = name; o.textContent = name.replace(/\.lvl$/i, "");
      $("startfrom").append(o);
    }
  }
  $("startfrom").addEventListener("change", async (e) => {
    const name = e.target.value;
    e.target.value = "";
    if (!name) return;
    if (name === "!empty") { replaceMap(new Uint8Array(N * N), null, null, "Started from an empty map."); return; }
    const r = await fetch(API + "/maps/file?name=" + encodeURIComponent(name), { credentials: "same-origin" });
    if (!r.ok) { say("Couldn't open that map.", false); return; }
    const m = Lvl.parse(new Uint8Array(await r.arrayBuffer()));
    replaceMap(m.grid, m.prefix, m.tileset, `Started from ${name}: ${m.count} tiles. Publish to use it.`);
  });
  // ---- random maps, and tidying walls (autotile.js) -------------------------------------------------
  for (const [id, label] of Object.entries(Lvl.GEN_LAYOUTS)) $("genlayout").append(new Option(label, id));
  function listTileLayouts(pick) {
    $("tilelayout").replaceChildren(...[...Autotile.layouts.values()].map((l) => new Option(l.name || l.id, l.id)));
    if (pick) $("tilelayout").value = pick;
  }
  listTileLayouts();
  // the standard tile layout (tile-layout.json): one choice per pack, the first one picked; without
  // it the stand-in role preview layout is all there is
  (async () => {
    const r = await fetch("/arena/tile-layout.json", { credentials: "same-origin" }).catch(() => null);
    const spec = r && r.ok ? await r.json().catch(() => null) : null;
    if (!spec) return;
    const { layouts, errors } = Autotile.fromSpec(spec);
    for (const l of layouts) Autotile.register(l);
    if (errors.length) console.warn("tile layout:", errors);
    if (layouts.length) listTileLayouts(layouts[0].id);
  })();
  const tileLayout = () => Autotile.layouts.get($("tilelayout").value) || Autotile.STAND_IN;
  $("genlayout").addEventListener("change", () => {
    const teams = $("genlayout").value === "teams";
    $("gensym").disabled = teams; // left vs right, always
    $("genflags").disabled = teams; // always has flag rooms
  });
  $("randomize").addEventListener("click", async () => {
    const seed = (Math.random() * 2 ** 32) >>> 0;
    const g = Lvl.generate(seed, {
      layout: $("genlayout").value, size: $("gensize").value, symmetry: Number($("gensym").value),
      thickness: Number($("genthick").value), density: Number($("gendensity").value), flags: $("genflags").checked,
      tile: cur > 0 && cur <= 161 ? cur : 1, tiles: $("genauto").checked ? tileLayout() : null,
    });
    if (await replaceMap(g.grid, undefined, undefined, `A random ${Lvl.GEN_LAYOUTS[g.layout].toLowerCase()} map. Randomize again for another, or draw on it.`))
      $("genseed").textContent = `Seed ${seed}`;
  });
  // tidy walls over an area (x0, y0, x1, y1) or the whole map, as one undoable change
  function tidy(area) {
    const change = Autotile.tidy(grid, tileLayout(), area);
    if (change.length) { undoStack.push(change); redoStack.length = 0; setDirty(true); drawMini(); }
    draw();
    say(change.length ? `Tidied ${change.length.toLocaleString()} wall tiles. Undo puts them back.` : "Those walls were already tidy.", true);
  }
  $("tidyall").addEventListener("click", () => tidy(null));
  $("roleview").addEventListener("change", (e) => {
    roleView = e.target.checked ? Autotile.previewTileset(tileLayout()) : null;
    drawPalette(); draw();
  });
  $("tilelayout").addEventListener("change", () => {
    if (roleView) roleView = Autotile.previewTileset(tileLayout());
    drawPalette(); draw();
  });

  function centre() {
    let minX = N, minY = N, maxX = -1, maxY = -1;
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) if (grid[y * N + x]) {
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    if (maxX < 0) { cam.x = cam.y = 512 * TS; cam.z = 1; return; }
    cam.x = (minX + maxX + 1) / 2 * TS; cam.y = (minY + maxY + 1) / 2 * TS;
    const w = view.clientWidth * devicePixelRatio, h = view.clientHeight * devicePixelRatio;
    cam.z = Math.max(0.05, Math.min(4, Math.min(w / ((maxX - minX + 4) * TS), h / ((maxY - minY + 4) * TS))));
  }

  // ---- drawing -----------------------------------------------------------------------------------
  const view = $("view"), vctx = view.getContext("2d");
  function resize() { view.width = view.clientWidth * devicePixelRatio; view.height = view.clientHeight * devicePixelRatio; draw(); drawPalette(); }
  function toTile(px, py) {
    const s = TS * cam.z, sx = px * devicePixelRatio, sy = py * devicePixelRatio;
    return [Math.floor((sx - view.width / 2 + cam.x * cam.z) / s), Math.floor((sy - view.height / 2 + cam.y * cam.z) / s)];
  }
  function drawTile(ctx, t, dx, dy, s) {
    const ts = tileset();
    if (t <= 190 && ts) {
      const i = t - 1;
      ctx.drawImage(ts, (i % 19) * TS, Math.floor(i / 19) * TS, TS, TS, dx, dy, s + 0.5, s + 0.5);
    } else {
      ctx.fillStyle = Lvl.color(t);
      const g = s >= 6 ? 1 : 0;
      ctx.fillRect(dx, dy, s - g, s - g);
    }
  }
  // Other scripts draw on the map through these (lvz-layer.js: the arena's LVZ map objects).
  const overlays = window.mapOverlays = window.mapOverlays || [];
  window.mapView = { view, get cam() { return cam; }, get arena() { return arena; }, redraw: () => draw() };
  // Game labels (labels-layer.js): team spawns, bases, flag spots, areas, kept as eLVL regions in this
  // map's own prefix, so they're published, downloaded and kept in Your maps with it.
  if (window.GameLabels) GameLabels.mount($("labelspanel"), {
    get: () => ({ grid, prefix, arena }),
    // Tile art's "Put the old tiles back" restores a whole earlier prefix: after a label change that would
    // drop the labels, so it's forgotten (the tiles can still be repainted).
    set: (p, note) => { prefix = p; if (tileArt) tileArt.forget(); setDirty(true); draw(); say(note, true); },
  });

  let dragRect = null;
  function draw() {
    vctx.imageSmoothingEnabled = false;
    vctx.fillStyle = "#04070e"; vctx.fillRect(0, 0, view.width, view.height);
    const s = TS * cam.z, ox = view.width / 2 - cam.x * cam.z, oy = view.height / 2 - cam.y * cam.z;
    // chart grid every 16 tiles
    if (s * 16 >= 24) {
      vctx.strokeStyle = "#0a1624"; vctx.lineWidth = 1; vctx.beginPath();
      for (let k = 0; k <= N; k += 16) {
        const x = Math.round(ox + k * s) + 0.5, y = Math.round(oy + k * s) + 0.5;
        if (x >= 0 && x <= view.width) { vctx.moveTo(x, Math.max(0, oy)); vctx.lineTo(x, Math.min(view.height, oy + N * s)); }
        if (y >= 0 && y <= view.height) { vctx.moveTo(Math.max(0, ox), y); vctx.lineTo(Math.min(view.width, ox + N * s), y); }
      }
      vctx.stroke();
    }
    for (const f of overlays) f("below", vctx, s, ox, oy); // lvz-layer.js: LVZ objects under the tiles
    const x0 = Math.max(0, Math.floor(-ox / s)), y0 = Math.max(0, Math.floor(-oy / s));
    const x1 = Math.min(N - 1, Math.ceil((view.width - ox) / s)), y1 = Math.min(N - 1, Math.ceil((view.height - oy) / s));
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const t = grid[y * N + x]; if (t) drawTile(vctx, t, ox + x * s, oy + y * s, s);
    }
    vctx.strokeStyle = "#22d3ee66"; vctx.lineWidth = 1; vctx.strokeRect(ox, oy, N * s, N * s);
    for (const f of overlays) f("above", vctx, s, ox, oy); // ... and over them
    if (tools) tools.overlay(vctx, s, ox, oy, view);
    if (dragRect) {
      const [ax, ay, bx, by] = dragRect;
      vctx.strokeStyle = "#ff8a1f"; vctx.lineWidth = 2;
      vctx.strokeRect(ox + Math.min(ax, bx) * s, oy + Math.min(ay, by) * s, (Math.abs(bx - ax) + 1) * s, (Math.abs(by - ay) + 1) * s);
    }
  }
  const mini = $("mini"), mctx = mini.getContext("2d");
  function drawMini() { Lvl.drawOverview(mini, grid); }

  // ---- tiles palette: the tileset itself (19 x 10 slots), scaled, special slots labelled ----------
  const pal = $("palette"), pctx = pal.getContext("2d");
  let palHover = 0;
  function palTileAt(e) {
    const r = pal.getBoundingClientRect();
    const x = Math.floor((e.clientX - r.left) / r.width * 19), y = Math.floor((e.clientY - r.top) / r.height * 10);
    return x >= 0 && x < 19 && y >= 0 && y < 10 ? y * 19 + x + 1 : 0;
  }
  function drawPalette() {
    // canvas pixels follow the shown size, so tiles and labels stay crisp at any zoom
    const w = Math.max(19, Math.round(pal.clientWidth * devicePixelRatio)) || 608;
    const h = Math.round(w * 10 / 19);
    if (pal.width !== w || pal.height !== h) { pal.width = w; pal.height = h; }
    const k = w / 19; // canvas pixels per tile
    const at = (t) => { const i = t - 1; return [(i % 19) * k, Math.floor(i / 19) * k]; };
    pctx.imageSmoothingEnabled = false;
    pctx.fillStyle = "#04070e"; pctx.fillRect(0, 0, w, h);
    const ts = tileset();
    if (ts) pctx.drawImage(ts, 0, 0, 19 * TS, 10 * TS, 0, 0, w, h);
    else for (let t = 1; t <= 190; t++) {
      const [x, y] = at(t), g = Math.max(1, Math.round(k / 16));
      pctx.fillStyle = Lvl.color(t); pctx.fillRect(x + g, y + g, k - 2 * g, k - 2 * g);
    }
    // special slots: a frame around each group and its name in a dark band along the bottom
    const font = Math.max(7, Math.round(k * 0.3));
    pctx.font = `600 ${font}px ui-sans-serif, system-ui, sans-serif`;
    pctx.textAlign = "center"; pctx.textBaseline = "middle";
    for (const [first, last, , label, col] of Lvl.GROUPS) {
      const [x, y] = at(first), gw = (last - first + 1) * k;
      const band = Math.round(font * 1.35);
      pctx.fillStyle = "rgba(4, 7, 14, .78)"; pctx.fillRect(x, y + k - band, gw, band);
      pctx.fillStyle = col;
      const text = gw < pctx.measureText(label).width + 4 ? label.slice(0, Math.max(1, Math.floor(gw / (font * 0.6)))) : label;
      pctx.fillText(text, x + gw / 2, y + k - band / 2 + 0.5, gw - 2);
      const lw = Math.max(1, Math.round(k / 20));
      pctx.strokeStyle = col; pctx.lineWidth = lw;
      pctx.strokeRect(x + lw / 2, y + lw / 2, gw - lw, k - lw);
    }
    if (palHover && palHover !== cur) {
      const [x, y] = at(palHover), lw = Math.max(1, Math.round(k / 16));
      pctx.strokeStyle = "#e2f3ff"; pctx.lineWidth = lw; pctx.strokeRect(x + lw / 2, y + lw / 2, k - lw, k - lw);
    }
    if (cur >= 1 && cur <= 190) {
      const [x, y] = at(cur), lw = Math.max(2, Math.round(k / 8));
      pctx.strokeStyle = "#04070e"; pctx.lineWidth = lw + 2; pctx.strokeRect(x + lw / 2, y + lw / 2, k - lw, k - lw);
      pctx.strokeStyle = "#ff8a1f"; pctx.lineWidth = lw; pctx.strokeRect(x + lw / 2, y + lw / 2, k - lw, k - lw);
    }
    const c = $("current").getContext("2d"); c.imageSmoothingEnabled = false;
    c.fillStyle = "#04070e"; c.fillRect(0, 0, 16, 16); drawTile(c, cur, 0, 0, 16);
    const name = Lvl.tileName(cur);
    $("curName").textContent = `Tile ${cur}` + (name ? ` · ${name}` : "");
  }
  pal.addEventListener("pointermove", (e) => {
    const t = palTileAt(e);
    if (t === palHover) return;
    palHover = t;
    const name = t ? Lvl.tileName(t) : "";
    pal.title = t ? `Tile ${t}` + (name ? ` · ${name}` : "") : "";
    drawPalette();
  });
  pal.addEventListener("pointerleave", () => { palHover = 0; drawPalette(); });
  // keyboard: arrows move the selection around the palette
  pal.addEventListener("keydown", (e) => {
    const d = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -19, ArrowDown: 19 }[e.key];
    if (!d) return;
    e.preventDefault(); e.stopPropagation();
    const base = cur >= 1 && cur <= 190 ? cur : 1;
    const next = base + d;
    if (next >= 1 && next <= 190) pickTile(next);
  });
  const palBig = (big) => {
    document.querySelector(".edside").classList.toggle("bigpal", big);
    $("palzoom").setAttribute("aria-pressed", String(big));
    $("palzoom").textContent = big ? "Smaller" : "Bigger";
    drawPalette();
  };
  $("palzoom").addEventListener("click", () => {
    const big = !document.querySelector(".edside").classList.contains("bigpal");
    try { localStorage.setItem("dare.mapeditor.bigpal", big ? "1" : "0"); } catch { /* fine */ }
    palBig(big);
  });
  // bigger by default on wide screens, where it leaves the map plenty of room
  let savedBig = null;
  try { savedBig = localStorage.getItem("dare.mapeditor.bigpal"); } catch { /* fine */ }
  if (savedBig ? savedBig === "1" : innerWidth >= 1500) palBig(true);

  // ---- editing -----------------------------------------------------------------------------------
  let stroke = null, panning = null, space = false;
  function setTile(x, y, t) {
    if (x < 0 || y < 0 || x >= N || y >= N) return;
    const i = y * N + x; if (grid[i] === t) return;
    stroke.push([i, grid[i], t]); grid[i] = t;
  }
  function endStroke() {
    if (stroke && stroke.length) { undoStack.push(stroke); redoStack.length = 0; setDirty(true); drawMini(); }
    stroke = null;
  }
  view.addEventListener("contextmenu", (e) => e.preventDefault());
  view.addEventListener("pointerdown", (e) => {
    view.setPointerCapture(e.pointerId);
    if (e.button === 2 || e.button === 1 || space || tool === "pan") { panning = { x: e.clientX, y: e.clientY, cx: cam.x, cy: cam.y }; return; }
    if (e.button !== 0) return;
    const [x, y] = toTile(e.offsetX, e.offsetY);
    if (tools && tools.handles(tool)) { tools.down(x, y, e); return; } // drawtools.js
    stroke = [];
    if (tool === "tidy") dragRect = [x, y, x, y];
    draw();
  });
  view.addEventListener("pointermove", (e) => {
    const [x, y] = toTile(e.offsetX, e.offsetY);
    $("status").textContent = `x ${x} · y ${y} · tile ${x >= 0 && y >= 0 && x < N && y < N ? grid[y * N + x] : "–"} · zoom ${cam.z.toFixed(2)}`;
    if (panning) {
      cam.x = panning.cx - (e.clientX - panning.x) * devicePixelRatio / cam.z;
      cam.y = panning.cy - (e.clientY - panning.y) * devicePixelRatio / cam.z;
      draw(); return;
    }
    if (tools && tools.handles(tool)) { tools.move(x, y, e); return; }
    if (!stroke) return;
    if (tool === "tidy") { dragRect[2] = x; dragRect[3] = y; }
    draw();
  });
  const up = (e) => {
    if (panning) { panning = null; return; }
    if (tools && tools.handles(tool)) { const [x, y] = toTile(e.offsetX, e.offsetY); tools.up(x, y, e); return; }
    if (!stroke) return;
    if (tool === "tidy" && dragRect) {
      const [ax, ay, bx, by] = dragRect;
      dragRect = null; stroke = null;
      tidy([Math.min(ax, bx), Math.min(ay, by), Math.max(ax, bx), Math.max(ay, by)]);
      return;
    }
    endStroke(); draw();
  };
  view.addEventListener("pointerup", up);
  view.addEventListener("pointercancel", up);
  function zoomAt(px, py, f) {
    const before = toTile(px, py);
    cam.z = Math.min(8, Math.max(0.05, cam.z * f));
    const after = toTile(px, py);
    cam.x += (before[0] - after[0]) * TS; cam.y += (before[1] - after[1]) * TS; draw();
  }
  view.addEventListener("wheel", (e) => { e.preventDefault(); zoomAt(e.offsetX, e.offsetY, e.deltaY < 0 ? 1.15 : 1 / 1.15); }, { passive: false });
  $("zoomIn").addEventListener("click", () => zoomAt(view.clientWidth / 2, view.clientHeight / 2, 1.4));
  $("zoomOut").addEventListener("click", () => zoomAt(view.clientWidth / 2, view.clientHeight / 2, 1 / 1.4));

  function undo() {
    const s = undoStack.pop(); if (!s) return;
    for (let k = s.length - 1; k >= 0; k--) grid[s[k][0]] = s[k][1];
    redoStack.push(s); setDirty(true); drawMini(); draw();
  }
  function redo() {
    const s = redoStack.pop(); if (!s) return;
    for (const [i, , t] of s) grid[i] = t;
    undoStack.push(s); setDirty(true); drawMini(); draw();
  }
  $("undo").addEventListener("click", undo);
  $("redo").addEventListener("click", redo);

  // tells other panels (lvz-layer.js leaves LVZ placement) that the pilot went back to drawing
  const toolNote = () => window.dispatchEvent(new CustomEvent("map:tool", { detail: { tool, tile: cur } }));
  function pickTile(t) { cur = t; if (["erase", "pan", "tidy", "pick", "select", "lasso", "wand"].includes(tool)) setTool("pencil"); drawPalette(); toolNote(); }
  pal.addEventListener("click", (e) => { const t = palTileAt(e); if (t) pickTile(t); });
  mini.addEventListener("click", (e) => {
    const r = mini.getBoundingClientRect();
    cam.x = (e.clientX - r.left) / r.width * N * TS; cam.y = (e.clientY - r.top) / r.height * N * TS; draw();
  });
  for (const [t, name, col] of SPECIAL) {
    const sw = document.createElement("span"); sw.className = "swatch"; sw.style.background = col;
    const b = document.createElement("button"); b.type = "button"; b.className = "ghost small";
    b.append(sw, document.createTextNode(name));
    b.title = `Tile ${t}`;
    b.addEventListener("click", () => pickTile(t));
    $("specials").append(b);
  }
  function setTool(t) {
    tool = t;
    for (const b of document.querySelectorAll("[data-tool]")) {
      b.classList.toggle("on", b.dataset.tool === t); b.setAttribute("aria-pressed", String(b.dataset.tool === t));
    }
    view.classList.toggle("panning", t === "pan");
    if (tools) tools.toolOn(t);
    toolNote();
  }
  // the drawing tools (drawtools.js): shapes, fill, text, selections, all auto-tiled
  const tools = window.DrawTools && DrawTools.mount({
    N, get grid() { return grid; },
    cur: () => cur, tool: () => tool, layout: () => tileLayout(),
    setTool, pick: (t) => { cur = t; drawPalette(); say(`Drawing with tile ${t}.`, true); toolNote(); },
    commit(change) { if (change.length) { undoStack.push(change); redoStack.length = 0; setDirty(true); drawMini(); } draw(); },
    draw: () => draw(), drawTile, say,
  });
  setTool("pencil");
  window.addEventListener("keydown", (e) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement || document.querySelector("dialog[open]")) return;
    if (e.key === " ") { space = true; e.preventDefault(); }
    if (tools && !(e.metaKey || e.ctrlKey) && e.key === "t") { setTool("tidy"); return; }
    if (tools && !((e.metaKey || e.ctrlKey) && /^[zy]$/i.test(e.key)) && tools.key(e)) { e.preventDefault(); return; }
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "z") { e.preventDefault(); if (e.shiftKey) redo(); else undo(); }
    else if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "y") { e.preventDefault(); redo(); }
    else if (!e.metaKey && !e.ctrlKey) {
      if (e.key === "b") setTool("pencil"); else if (e.key === "r") setTool("rect"); else if (e.key === "e") setTool("erase"); else if (e.key === "t") setTool("tidy");
    }
  });
  window.addEventListener("keyup", (e) => { if (e.key === " ") space = false; });

  $("tilesetfile").addEventListener("change", async (e) => {
    const f = e.target.files[0]; if (!f) return;
    try { ownTileset = Lvl.decodeBmp(new Uint8Array(await f.arrayBuffer())); say(`Showing tiles from ${f.name} (only in your browser; not published).`, true); }
    catch (err) { say(`That tileset could not be read: ${err.message}`, false); }
    noteTileset();
    drawPalette(); draw();
  });

  // ---- save / publish ------------------------------------------------------------------------
  $("download").addEventListener("click", async () => {
    const bytes = Lvl.serialize(grid, prefix);
    const name = libName || (arena || "map").replace(/^#/, "");
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    a.download = `${name}.lvl`; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    if (!bytes.length) return;
    const j = await keepCopy(bytes, name);
    say(j && j.ok ? `Downloaded ${name}.lvl. ${/already/.test(j.message) ? j.message : "A copy is in Your maps."}`
      : `Downloaded ${name}.lvl, but it couldn't be kept in Your maps: ${(j && j.message) || "no connection"}`, !!(j && j.ok));
  });

  // Publish: true once the map is saved and the arena restarted with it. thenEnter (from Enter arena,
  // where the pilot already chose "Publish and restart"): no second confirm, and with pilots inside
  // the dialog offers only "Publish and restart now".
  async function publish(thenEnter) {
    if (role !== "owner") return false;
    const bytes = Lvl.serialize(grid, prefix);
    if (bytes.length === 0) { say("The map is empty; draw something first.", false); return false; }
    if (bytes.length > 2 * 1024 * 1024) { say("Maps must be 2 MB or smaller.", false); return false; }
    const target = liveName || arena;
    // pilots inside: ask (restart now with an optional announcement, or save and restart later);
    // nobody inside: a plain confirm, then it restarts
    let choice = { restart: true, message: null };
    const info = liveName ? null : await fetch(API + "/me?arena=" + encodeURIComponent(arena), { credentials: "same-origin" })
      .then((r) => r.json()).catch(() => null);
    const inside = info?.restart;
    if (inside?.running && inside.online > 0) {
      choice = await askPublish(inside, thenEnter);
      if (!choice) return false;
    } else if (!thenEnter && !await Dlg.confirm(`Publish this map to ${target}? Players inside are sent back in with the new map.`, { title: "Publish map", ok: "Publish" })) return false;
    $("publish").disabled = true;
    say("Publishing…", true);
    const hdr = { "X-Requested-With": "dare-arena" };
    const fileName = arena.replace(/^#/, "") + ".lvl";
    const url = `${API}/upload?kind=map&arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(fileName)}&libname=${encodeURIComponent(libName || arena.replace(/^#/, ""))}`;
    const r = await fetch(url, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, hdr), body: bytes });
    const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
    if (!j.ok) { say(j.message, false); setDirty(true); return false; }
    if (j.library) { libId = j.library; libName = libName || arena.replace(/^#/, ""); loadLibrary(); } // the server kept a copy in Your maps
    // spawn labels become arena settings, before the restart so the arena starts with them
    const spawnNote = window.GameLabels ? await GameLabels.publishSettings(arena) : "";
    // the draft of a live arena: publish it (copies the draft to the live arena and restarts it);
    // otherwise restart this arena so the new map loads
    if (!liveName && !choice.restart) {
      setDirty(false);
      say(`Saved to ${arena}; the running arena keeps the old map until you restart it. ${spawnNote ? spawnNote + " " : ""}`, true);
      $("msg").append(Object.assign(document.createElement("a"),
        { href: "/arena/me?arena=" + encodeURIComponent(arena) + "#map", textContent: "Restart it in the arena editor" }));
      $("edname").textContent = `${arena} · ${fileName}`;
      return false;
    }
    const rr = liveName
      ? await fetch(API + "/drafts/publish", { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/json" }, hdr), body: JSON.stringify({ name: liveName }) })
      : await fetch(API + "/restart", { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/json" }, hdr), body: JSON.stringify({ arena, message: choice.message }) });
    const jj = await rr.json().catch(() => ({ ok: false, message: "HTTP " + rr.status }));
    setDirty(false);
    say((jj.ok ? `Published. ${jj.message}` : `Saved to ${arena}, but: ${jj.message}`) + (spawnNote ? " " + spawnNote : ""), jj.ok);
    $("edname").textContent = `${arena} · ${fileName}`;
    return !!jj.ok;
  }
  $("publish").addEventListener("click", () => publish(false));

  // ---- enter the arena in game (as the arena editor's Enter arena): ask first if the map has changes
  // the running arena doesn't have yet
  const PUBLIC = "(public)"; // the public arenas' shared folder; in game it is arena 0
  const playName = (a) => a === PUBLIC ? "0" : a;
  const enterUrl = (a) => "/?arena=" + encodeURIComponent(playName(a)) + "&enter=1"; // the home page presses Enter
  $("enterarena").addEventListener("click", async (e) => {
    if (!arena) { e.preventDefault(); return; }
    $("enterarena").href = enterUrl(arena);
    if (!dirty) return; // nothing pending: the link goes straight in
    e.preventDefault();
    const owner = role === "owner";
    const what = libName ? `Your draft of ${libName} isn't` : "Your changes to this map aren't";
    const pick = await Dlg.choose(
      `${what} published, so ${arena} still has its old map. ` + (owner
        ? "Publish and restart it first to play the new map, or go in and play the old one."
        : "Only the owner can publish; going in now, you play the old map."),
      { title: "Publish first?", choices: [
        ...(owner ? [{ value: "publish", label: "Publish and restart, then enter" }] : []),
        { value: "enter", label: "Enter without publishing" }] });
    if (pick === "enter") { leaving = true; location.href = enterUrl(arena); return; }
    if (pick !== "publish") return;
    if (await publish(true)) { say("Published. Taking you in…", true); location.href = enterUrl(arena); }
  });

  // {restart, message} from the publish dialog, or null when cancelled
  function askPublish(inside, nowOnly) {
    const dlg = $("pubdlg");
    const names = inside.onlineNames || [];
    const more = inside.online > names.length ? ` and ${inside.online - names.length} more` : "";
    $("pubtitle").textContent = `Publish this map to ${arena}?`;
    $("pubwho").textContent = `${inside.online} pilot${inside.online === 1 ? " is" : "s are"} in the arena right now`
      + (names.length ? ` (${names.join(", ")}${more})` : "")
      + ". Restarting disconnects them from the arena for a moment; they're put straight back in with the new map.";
    $("publater").hidden = !!nowOnly;
    return new Promise((resolve) => {
      const done = (value) => { dlg.close(); resolve(value); };
      $("pubnow").onclick = () => done({ restart: true, message: $("pubmsg").value.trim() || null });
      $("publater").onclick = () => done({ restart: false, message: null });
      $("pubcancel").onclick = () => done(null);
      dlg.oncancel = () => resolve(null);
      dlg.showModal();
      $("pubnow").focus();
    });
  }

  let leaving = false; // the pilot already chose to leave the changes behind (Enter without publishing)
  window.addEventListener("beforeunload", (e) => { if (dirty && !leaving) { e.preventDefault(); e.returnValue = ""; } });
  window.addEventListener("resize", resize);
  resize();
  load().then(async () => {
    loadShared();
    await loadLibrary();
    const q = new URLSearchParams(location.search);
    const lib = q.get("lib");
    if (lib) await openLib(Number(lib));
    await loadMyTilesets();
    const ts = q.get("tileset"); // from the Foundry's Keep: put that tileset into the map being edited
    if (ts) await applyMyTileset(Number(ts));
  });
  // for checks in a browser console
  window.chartRoom = { get grid() { return grid; }, setTile: (x, y, t) => { stroke = []; setTile(x, y, t); endStroke(); draw(); }, save: () => Lvl.serialize(grid, prefix) };
})();
