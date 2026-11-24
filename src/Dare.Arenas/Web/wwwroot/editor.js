// Map editor (/arena/map?arena=<name>): opens the arena's current map, edits it in the browser,
// and Publish sends it through the upload API (owner only) and recycles the arena.
// Text goes in with textContent only.
"use strict";

(() => {
  const { N, TS, SPECIAL } = Lvl;
  const API = "/arena/api";
  const $ = (id) => document.getElementById(id);

  let grid = new Uint8Array(N * N), prefix = null, mapTileset = null, ownTileset = null;
  let arena = new URLSearchParams(location.search).get("arena") || "";
  let role = null, tool = "pencil", cur = 1, dirty = false;
  const cam = { x: 512 * TS, y: 512 * TS, z: 0.5 };
  const undoStack = [], redoStack = [];
  const tileset = () => ownTileset || mapTileset;

  const say = (text, ok) => { const m = $("msg"); m.textContent = text || ""; m.className = "edmsg " + (ok ? "ok" : "bad"); };
  const setDirty = (d) => { dirty = d; $("publish").disabled = !d || role !== "owner"; };

  // ---- load --------------------------------------------------------------------------------------
  async function load() {
    const me = await fetch(API + "/me" + (arena ? "?arena=" + encodeURIComponent(arena) : ""), { credentials: "same-origin" });
    if (me.status === 401) { location.href = "/account/login"; return; }
    const body = await me.json().catch(() => null);
    if (!me.ok || !body || !body.arena) { say((body && body.message) || "You don't own or co-mod an arena yet.", false); return; }
    arena = body.arena; role = body.role;
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
    $("tilesetnote").textContent = mapTileset ? "Tiles from this map's own tileset." : "This map has no tileset of its own: tiles show as flat colours (players see their client's tiles).";
    centre();
    drawPalette(); drawMini(); draw();
    setDirty(false);
  }

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
  function resize() { view.width = view.clientWidth * devicePixelRatio; view.height = view.clientHeight * devicePixelRatio; draw(); }
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
    const x0 = Math.max(0, Math.floor(-ox / s)), y0 = Math.max(0, Math.floor(-oy / s));
    const x1 = Math.min(N - 1, Math.ceil((view.width - ox) / s)), y1 = Math.min(N - 1, Math.ceil((view.height - oy) / s));
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const t = grid[y * N + x]; if (t) drawTile(vctx, t, ox + x * s, oy + y * s, s);
    }
    vctx.strokeStyle = "#22d3ee66"; vctx.lineWidth = 1; vctx.strokeRect(ox, oy, N * s, N * s);
    if (dragRect) {
      const [ax, ay, bx, by] = dragRect;
      vctx.strokeStyle = "#ff8a1f"; vctx.lineWidth = 2;
      vctx.strokeRect(ox + Math.min(ax, bx) * s, oy + Math.min(ay, by) * s, (Math.abs(bx - ax) + 1) * s, (Math.abs(by - ay) + 1) * s);
    }
  }
  const mini = $("mini"), mctx = mini.getContext("2d");
  function drawMini() { Lvl.drawOverview(mini, grid); }

  const pal = $("palette"), pctx = pal.getContext("2d");
  function drawPalette() {
    pctx.imageSmoothingEnabled = false;
    pctx.fillStyle = "#04070e"; pctx.fillRect(0, 0, 304, 160);
    const ts = tileset();
    if (ts) pctx.drawImage(ts, 0, 0);
    else {
      pctx.font = "9px ui-monospace, monospace"; pctx.textAlign = "center"; pctx.textBaseline = "middle";
      for (let t = 1; t <= 190; t++) {
        const i = t - 1, x = (i % 19) * TS, y = Math.floor(i / 19) * TS;
        pctx.fillStyle = Lvl.color(t); pctx.fillRect(x + 1, y + 1, TS - 2, TS - 2);
        pctx.fillStyle = "#04070e"; pctx.fillText(String(t), x + 8, y + 8.5);
      }
    }
    if (cur >= 1 && cur <= 190) {
      const i = cur - 1; pctx.strokeStyle = "#ff8a1f"; pctx.lineWidth = 2;
      pctx.strokeRect((i % 19) * TS + 1, Math.floor(i / 19) * TS + 1, TS - 2, TS - 2);
    }
    const c = $("current").getContext("2d"); c.imageSmoothingEnabled = false;
    c.fillStyle = "#04070e"; c.fillRect(0, 0, 16, 16); drawTile(c, cur, 0, 0, 16);
    const sp = SPECIAL.find((s) => s[0] === cur);
    $("curName").textContent = `Tile ${cur}` + (sp ? ` · ${sp[1]}` : "");
  }

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
    stroke = [];
    if (tool === "rect") dragRect = [x, y, x, y]; else setTile(x, y, tool === "erase" ? 0 : cur);
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
    if (!stroke) return;
    if (tool === "rect") { dragRect[2] = x; dragRect[3] = y; } else setTile(x, y, tool === "erase" ? 0 : cur);
    draw();
  });
  const up = () => {
    if (panning) { panning = null; return; }
    if (!stroke) return;
    if (tool === "rect" && dragRect) {
      const [ax, ay, bx, by] = dragRect;
      for (let y = Math.min(ay, by); y <= Math.max(ay, by); y++) for (let x = Math.min(ax, bx); x <= Math.max(ax, bx); x++) setTile(x, y, cur);
      dragRect = null;
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

  pal.addEventListener("click", (e) => {
    const r = pal.getBoundingClientRect();
    const x = Math.floor((e.clientX - r.left) / r.width * 19), y = Math.floor((e.clientY - r.top) / r.height * 10);
    const t = y * 19 + x + 1;
    if (t >= 1 && t <= 190) { cur = t; if (tool === "erase" || tool === "pan") setTool("pencil"); drawPalette(); }
  });
  mini.addEventListener("click", (e) => {
    const r = mini.getBoundingClientRect();
    cam.x = (e.clientX - r.left) / r.width * N * TS; cam.y = (e.clientY - r.top) / r.height * N * TS; draw();
  });
  for (const [t, name, col] of SPECIAL) {
    const sw = document.createElement("span"); sw.className = "swatch"; sw.style.background = col;
    const b = document.createElement("button"); b.type = "button"; b.className = "ghost small";
    b.append(sw, document.createTextNode(name));
    b.addEventListener("click", () => { cur = t; if (tool === "erase" || tool === "pan") setTool("pencil"); drawPalette(); });
    $("specials").append(b);
  }
  function setTool(t) {
    tool = t;
    for (const [id, n] of [["tPencil", "pencil"], ["tRect", "rect"], ["tErase", "erase"], ["tPan", "pan"]]) {
      $(id).classList.toggle("on", n === t); $(id).setAttribute("aria-pressed", String(n === t));
    }
    view.classList.toggle("panning", t === "pan");
  }
  $("tPencil").addEventListener("click", () => setTool("pencil"));
  $("tRect").addEventListener("click", () => setTool("rect"));
  $("tErase").addEventListener("click", () => setTool("erase"));
  $("tPan").addEventListener("click", () => setTool("pan"));
  window.addEventListener("keydown", (e) => {
    if (e.target instanceof HTMLInputElement) return;
    if (e.key === " ") { space = true; e.preventDefault(); }
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "z") { e.preventDefault(); if (e.shiftKey) redo(); else undo(); }
    else if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "y") { e.preventDefault(); redo(); }
    else if (!e.metaKey && !e.ctrlKey) {
      if (e.key === "b") setTool("pencil"); else if (e.key === "r") setTool("rect"); else if (e.key === "e") setTool("erase");
    }
  });
  window.addEventListener("keyup", (e) => { if (e.key === " ") space = false; });

  $("tilesetfile").addEventListener("change", async (e) => {
    const f = e.target.files[0]; if (!f) return;
    try { ownTileset = Lvl.decodeBmp(new Uint8Array(await f.arrayBuffer())); say(`Showing tiles from ${f.name} (only in your browser; not published).`, true); }
    catch (err) { say(`That tileset could not be read: ${err.message}`, false); }
    drawPalette(); draw();
  });

  // ---- save / publish ------------------------------------------------------------------------
  $("download").addEventListener("click", () => {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([Lvl.serialize(grid, prefix)], { type: "application/octet-stream" }));
    a.download = `${arena || "map"}.lvl`; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  });

  $("publish").addEventListener("click", async () => {
    if (role !== "owner") return;
    const bytes = Lvl.serialize(grid, prefix);
    if (bytes.length === 0) { say("The map is empty; draw something first.", false); return; }
    if (bytes.length > 2 * 1024 * 1024) { say("Maps must be 2 MB or smaller.", false); return; }
    if (!confirm(`Publish this map to ${arena}? Players inside are sent back in with the new map.`)) return;
    $("publish").disabled = true;
    say("Publishing…", true);
    const hdr = { "X-Requested-With": "dare-arena" };
    const url = `${API}/upload?kind=map&arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(arena + ".lvl")}`;
    const r = await fetch(url, { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, hdr), body: bytes });
    const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
    if (!j.ok) { say(j.message, false); setDirty(true); return; }
    const rr = await fetch(API + "/recycle", { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/json" }, hdr), body: JSON.stringify({ arena }) });
    const jj = await rr.json().catch(() => ({ ok: false, message: "HTTP " + rr.status }));
    setDirty(false);
    say(jj.ok ? `Published. ${jj.message}` : `Published, but: ${jj.message}`, jj.ok);
    $("edname").textContent = `${arena} · ${arena}.lvl`;
  });

  window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });
  window.addEventListener("resize", resize);
  resize();
  load();
  // for checks in a browser console
  window.chartRoom = { get grid() { return grid; }, setTile: (x, y, t) => { stroke = []; setTile(x, y, t); endStroke(); draw(); }, save: () => Lvl.serialize(grid, prefix) };
})();
