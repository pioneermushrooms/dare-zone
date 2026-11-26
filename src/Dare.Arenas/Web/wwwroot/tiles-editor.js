// Tile editor (/arena/tiles): open a tileset (a .lvl's, a .bmp/.bm2/.png, or a ready-made map's),
// edit its pixels, say which slot is which wall piece (the tile layout, docs/tile-layout.md), see
// how a pack's pieces join, and save the tileset into a .lvl, as a .bmp, and the roles as JSON.
// Everything happens in the browser; only "Save to my maps" talks to the server.
// Text goes in with textContent only.
"use strict";

(() => {
  const { TS, COLS, ROWS, SHEET_W, SHEET_H } = TileSheet;
  const API = "/arena/api";
  const $ = (id) => document.getElementById(id);
  const hdrs = (type) => Object.assign({ "X-Requested-With": "dare-arena" }, type ? { "Content-Type": type } : {});
  const ask = (text, opts) => (window.Dlg ? Dlg.confirm(text, opts) : Promise.resolve(window.confirm(text)));
  const askText = (text, opts) => (window.Dlg ? Dlg.prompt(text, opts) : Promise.resolve(window.prompt(text, (opts && opts.value) || "")));

  // ---- state -------------------------------------------------------------------------------------------
  let img = { width: SHEET_W, height: SHEET_H, palette: Bmp.defaultPalette(), pixels: new Uint8Array(SHEET_W * SHEET_H) };
  let src = { name: "untitled", elvl: null, tiles: new Uint8Array(0), fromLvl: false }; // what Save .lvl rebuilds
  let standard = null, layout = null; // the shipped layout, and the one being edited (a copy)
  let sel = 1, packId = null, colour = 1, tool = "pencil", dirty = false, zoom = 3;
  let clip = null, suggestions = null;
  const undoStack = [], redoStack = [];

  const say = (text, ok) => { const m = $("msg"); m.textContent = text || ""; m.className = "edmsg " + (ok ? "ok" : "bad"); };
  const setDirty = (d) => { dirty = d; $("dirtytag").hidden = !d; };
  window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });

  // ---- drawing ---------------------------------------------------------------------------------------
  const rgb = (i) => [img.palette[i * 3], img.palette[i * 3 + 1], img.palette[i * 3 + 2]];
  const css = (i) => `rgb(${rgb(i).join(",")})`;
  const sheetCanvas = document.createElement("canvas"); sheetCanvas.width = SHEET_W; sheetCanvas.height = SHEET_H;
  function renderSheet() {
    const ctx = sheetCanvas.getContext("2d"), d = ctx.createImageData(SHEET_W, SHEET_H);
    for (let i = 0; i < SHEET_W * SHEET_H; i++) {
      const c = img.pixels[i];
      d.data[i * 4] = img.palette[c * 3]; d.data[i * 4 + 1] = img.palette[c * 3 + 1]; d.data[i * 4 + 2] = img.palette[c * 3 + 2]; d.data[i * 4 + 3] = 255;
    }
    ctx.putImageData(d, 0, 0);
  }
  const PACK_COLOURS = ["#71e4fa", "#ffcf7a", "#6ee7a8", "#f0abfc", "#fda4af", "#a5b4fc", "#fde68a", "#93c5fd"];
  const packColour = (p) => PACK_COLOURS[layout.packs.indexOf(p) % PACK_COLOURS.length];
  function drawSheet() {
    const c = $("sheet"), k = zoom;
    c.width = SHEET_W * k; c.height = SHEET_H * k;
    const ctx = c.getContext("2d");
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(sheetCanvas, 0, 0, c.width, c.height);
    const s = TS * k;
    ctx.strokeStyle = "#ffffff18"; ctx.lineWidth = 1;
    for (let x = 0; x <= COLS; x++) { ctx.beginPath(); ctx.moveTo(x * s + 0.5, 0); ctx.lineTo(x * s + 0.5, c.height); ctx.stroke(); }
    for (let y = 0; y <= ROWS; y++) { ctx.beginPath(); ctx.moveTo(0, y * s + 0.5); ctx.lineTo(c.width, y * s + 0.5); ctx.stroke(); }
    // reserved slots
    ctx.fillStyle = "#00000088";
    for (let t = layout.reserved[0]; t <= Math.min(layout.reserved[1], COLS * ROWS); t++) { const [x, y] = TileOps.slotXY(t); ctx.fillRect(x * k, y * k, s, s); }
    if ($("showRoles").checked) {
      for (const p of layout.packs) {
        const col = packColour(p), on = p.id === packId;
        for (const [role, t] of p.tiles) {
          const [x, y] = TileOps.slotXY(t), open = layout.roles.get(role).open;
          ctx.globalAlpha = on ? 1 : 0.45;
          ctx.strokeStyle = col; ctx.lineWidth = on ? 2 : 1;
          // closed edges as a line along the edge, open edges as a gap with a notch pointing out
          const x0 = x * k + 2, y0 = y * k + 2, x1 = x0 + s - 4, y1 = y0 + s - 4, m = s / 2;
          const edge = (e, a, b, cx, cy, dx, dy) => {
            if (!open.includes(e)) { ctx.beginPath(); ctx.moveTo(...a); ctx.lineTo(...b); ctx.stroke(); }
            else { ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + dx * 5, cy + dy * 5); ctx.stroke(); }
          };
          edge("N", [x0, y0], [x1, y0], x * k + m, y0, 0, -1);
          edge("E", [x1, y0], [x1, y1], x1, y * k + m, 1, 0);
          edge("S", [x0, y1], [x1, y1], x * k + m, y1, 0, 1);
          edge("W", [x0, y0], [x0, y1], x0, y * k + m, -1, 0);
          if (suggestions) {
            const sg = suggestions.get(t);
            if (sg && sg.open !== open) { ctx.globalAlpha = 1; ctx.fillStyle = "#ff8f8f"; ctx.beginPath(); ctx.arc(x * k + s - 7, y * k + 7, 4, 0, 7); ctx.fill(); }
          }
        }
      }
      ctx.globalAlpha = 1;
    }
    const [sx, sy] = TileOps.slotXY(sel);
    ctx.strokeStyle = "#ffffff"; ctx.lineWidth = 2; ctx.strokeRect(sx * k + 1, sy * k + 1, s - 2, s - 2);
    ctx.strokeStyle = "#000"; ctx.lineWidth = 1; ctx.strokeRect(sx * k + 3.5, sy * k + 3.5, s - 7, s - 7);
  }
  // The selected tile at 20x, with 4 px of each sheet neighbour around it (dimmed) to check seams.
  const TK = 20, RIM = 4;
  function drawTile() {
    const c = $("tile"), ctx = c.getContext("2d");
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#000"; ctx.fillRect(0, 0, c.width, c.height);
    const [sx, sy] = TileOps.slotXY(sel);
    ctx.drawImage(sheetCanvas, sx - RIM, sy - RIM, TS + 2 * RIM, TS + 2 * RIM, 0, 0, c.width, c.height);
    ctx.fillStyle = "#070c15b0";
    const a = RIM * TK, b = (RIM + TS) * TK;
    ctx.fillRect(0, 0, c.width, a); ctx.fillRect(0, b, c.width, c.height - b); ctx.fillRect(0, a, a, b - a); ctx.fillRect(b, a, c.width - b, b - a);
    ctx.strokeStyle = "#ffffff10";
    for (let i = 0; i <= TS; i++) {
      ctx.beginPath(); ctx.moveTo(a + i * TK + 0.5, a); ctx.lineTo(a + i * TK + 0.5, b); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(a, a + i * TK + 0.5); ctx.lineTo(b, a + i * TK + 0.5); ctx.stroke();
    }
    ctx.strokeStyle = "#71e4fa"; ctx.strokeRect(a - 0.5, a - 0.5, b - a + 1, b - a + 1);
    const p = $("tilePrev").getContext("2d");
    p.imageSmoothingEnabled = false;
    p.drawImage(sheetCanvas, sx, sy, TS, TS, 0, 0, TS, TS);
  }
  function drawPalette() {
    const c = $("pal"), ctx = c.getContext("2d"), w = 16, h = 8;
    for (let i = 0; i < 256; i++) {
      const x = (i % 32) * 8, y = Math.floor(i / 32) * 16;
      ctx.fillStyle = css(i); ctx.fillRect(x, y, w - 8, h + 8);
      if (i === 0) { ctx.strokeStyle = "#ff8f8f"; ctx.beginPath(); ctx.moveTo(x, y + 16); ctx.lineTo(x + 8, y); ctx.stroke(); }
    }
    const x = (colour % 32) * 8, y = Math.floor(colour / 32) * 16;
    ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.strokeRect(x + 1, y + 1, 6, 14); ctx.lineWidth = 1;
    $("curCol").style.background = css(colour);
    const [r, g, b] = rgb(colour);
    $("curColName").textContent = `Colour ${colour}${colour === 0 ? " (empty)" : ""} · #${[r, g, b].map((v) => v.toString(16).padStart(2, "0")).join("")}`;
    $("colEdit").value = "#" + [r, g, b].map((v) => v.toString(16).padStart(2, "0")).join("");
  }
  function drawAll() { renderSheet(); drawSheet(); drawTile(); drawPalette(); drawPreview(); showSlot(); }

  // ---- slot panel ---------------------------------------------------------------------------------------
  const currentPack = () => layout.packs.find((p) => p.id === packId) || null;
  function showSlot() {
    const [x, y] = TileOps.slotXY(sel);
    $("slotName").textContent = `Tile ${sel} · column ${x / TS + 1}, row ${y / TS + 1}`;
    const r = TileLayout.roleOfTile(layout, sel);
    const reserved = sel >= layout.reserved[0] && sel <= layout.reserved[1];
    $("slotRole").textContent = reserved ? "Reserved: the game gives this slot its own meaning (doors, flag, goal…)"
      : r ? `${r.pack.name}: ${r.role.label}` : otherLabel(sel) || "No role";
    const packSel = $("slotPack");
    packSel.replaceChildren(new Option("no pack", ""), ...layout.packs.map((p) => new Option(p.name, p.id)));
    packSel.value = r ? r.pack.id : "";
    packSel.disabled = reserved;
    const roleSel = $("slotRoleSel");
    roleSel.replaceChildren(new Option("—", ""), ...[...layout.roles.values()].map((ro) => new Option(`${ro.label} (${ro.open || "closed"})`, ro.name)));
    roleSel.value = r ? r.role.name : "";
    roleSel.disabled = reserved || !r;
    const sg = suggestions && suggestions.get(sel);
    $("slotSuggest").textContent = sg ? `Edges look ${sg.open ? "open to " + sg.open.split("").join(" ") : "closed all round"}${sg.sure < 0.25 ? " (a close call)" : ""}${r && r.role.open !== sg.open ? ` — its role says ${r.role.open || "closed"}` : ""}.` : "";
    if (r && r.pack.id !== packId) { packId = r.pack.id; showPacks(); drawSheet(); drawPreview(); }
  }
  function otherLabel(t) {
    for (const o of layout.other) if (o.tile === t || (Array.isArray(o.tiles) && o.tiles.includes(t))) return o.label;
    return "";
  }
  $("slotPack").addEventListener("change", (e) => {
    snapshot();
    const p = layout.packs.find((q) => q.id === e.target.value);
    const r = TileLayout.roleOfTile(layout, sel);
    if (r) r.pack.tiles.delete(r.role.name);
    if (p) {
      // the piece the edges suggest, or the role this slot had, or the first free one
      const sg = suggestions && suggestions.get(sel);
      const want = (sg && layout.byOpen.get(sg.open)) || (r && r.role.name);
      const free = [...layout.roles.keys()].filter((n) => !p.tiles.has(n));
      const role = want && free.includes(want) ? want : free[0];
      if (!role) { say(`${p.name} already has all ${layout.roles.size} pieces; change one of them first.`, false); }
      else { p.tiles.set(role, sel); packId = p.id; }
    }
    layoutChanged();
  });
  $("slotRoleSel").addEventListener("change", (e) => {
    const r = TileLayout.roleOfTile(layout, sel), role = e.target.value;
    if (!r || !role) return;
    snapshot();
    const prev = r.pack.tiles.get(role);
    r.pack.tiles.delete(r.role.name);
    if (prev && prev !== sel) r.pack.tiles.set(r.role.name, prev); // swap with the slot that had it
    r.pack.tiles.set(role, sel);
    layoutChanged();
  });
  function layoutChanged() { setDirty(true); showPacks(); drawSheet(); drawPreview(); showSlot(); }

  // ---- packs -----------------------------------------------------------------------------------------
  function showPacks() {
    $("layoutName").textContent = layout.name;
    const ul = $("packs"); ul.replaceChildren();
    for (const p of layout.packs) {
      const li = document.createElement("li");
      if (p.id === packId) li.className = "on";
      const dot = document.createElement("span"); dot.className = "dot"; dot.style.background = packColour(p);
      const b = document.createElement("button"); b.type = "button"; b.className = "ghost small open";
      b.textContent = `${p.name} · ${p.tiles.size}/${layout.roles.size}`;
      b.addEventListener("click", () => { packId = p.id; const t = p.tiles.get(layout.byOpen.get("")) || p.tiles.values().next().value; if (t) sel = t; drawSheet(); drawTile(); drawPreview(); showSlot(); showPacks(); });
      const ren = document.createElement("button"); ren.type = "button"; ren.className = "ghost small"; ren.textContent = "Rename";
      ren.addEventListener("click", async () => {
        const n = await askText("Name of this pack", { value: p.name, ok: "Rename" });
        if (n && n.trim()) { snapshot(); p.name = n.trim().slice(0, 40); layoutChanged(); }
      });
      li.append(dot, b, ren); ul.append(li);
    }
    if (!layout.packs.length) { const li = document.createElement("li"); li.className = "dim"; li.textContent = "No packs: select a slot and make one."; ul.append(li); }
    const w = layout.warnings || [];
    $("layoutWarn").hidden = !w.length; $("layoutWarn").textContent = w.join(" · ");
  }
  $("packNew").addEventListener("click", async () => {
    if (!layout.template) { say("This layout has no pack template; add pieces slot by slot with Pack and Piece.", false); return; }
    const [x, y] = TileOps.slotXY(sel), col = x / TS, row = y / TS;
    const n = await askText("Name of the new pack", { value: `Pack ${layout.packs.length + 1}`, ok: "Make pack" });
    if (!n || !n.trim()) return;
    let id = n.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "pack";
    while (layout.packs.some((p) => p.id === id)) id += "-2";
    const p = TileLayout.packAt(layout, id, n.trim().slice(0, 40), col, row);
    for (const [role, t] of [...p.tiles]) if (TileLayout.roleOfTile(layout, t) || (t >= layout.reserved[0] && t <= layout.reserved[1])) p.tiles.delete(role);
    if (!p.tiles.size) { say("Every slot there already belongs to a pack (or is reserved).", false); return; }
    snapshot();
    layout.packs.push(p); packId = id;
    say(`${p.name}: ${p.tiles.size} pieces laid out like the standard pack from tile ${sel}. Check edges to see if they fit.`, true);
    layoutChanged();
  });
  $("packDel").addEventListener("click", async () => {
    const p = currentPack(); if (!p) return;
    if (!(await ask(`Remove the pack "${p.name}"? Its tiles stay; they just lose their roles.`, { ok: "Remove", danger: true }))) return;
    snapshot();
    layout.packs = layout.packs.filter((q) => q !== p); packId = layout.packs[0] ? layout.packs[0].id : null;
    layoutChanged();
  });
  $("suggest").addEventListener("click", () => {
    suggestions = TileOps.suggestOpen(img);
    let off = 0, n = 0;
    for (const p of layout.packs) for (const [role, t] of p.tiles) { n++; const sg = suggestions.get(t); if (!sg || sg.open !== layout.roles.get(role).open) off++; }
    say(n ? `${n - off} of ${n} pack pieces look like their role. ${off ? "Red dots mark the others: the art or the role may be off." : ""}` : "No packs yet. Edges are read; select a slot to see them.", !off);
    drawSheet(); showSlot();
  });
  $("suggestApply").addEventListener("click", async () => {
    const p = currentPack(); if (!p) { say("Select a pack first.", false); return; }
    suggestions = suggestions || TileOps.suggestOpen(img);
    // every slot in the pack's area (its tiles, or the template box at its origin) gets the piece its edges look like
    const slots = new Set(p.tiles.values());
    if (p.origin && layout.template) for (const t of TileLayout.packAt(layout, "x", "x", p.origin[0], p.origin[1]).tiles.values()) slots.add(t);
    const next = new Map(), clash = [];
    for (const t of [...slots].sort((a, b) => a - b)) {
      const sg = suggestions.get(t), role = sg && layout.byOpen.get(sg.open);
      if (!role) continue;
      if (next.has(role)) { clash.push(t); continue; }
      const other = TileLayout.roleOfTile(layout, t);
      if (other && other.pack !== p) continue;
      next.set(role, t);
    }
    if (!(await ask(`Set ${p.name} to ${next.size} pieces read from the art${clash.length ? ` (tiles ${clash.join(", ")} look like a piece already taken and are left out)` : ""}?`, { ok: "Set roles" }))) return;
    snapshot();
    p.tiles = next;
    layoutChanged();
  });
  $("showRoles").addEventListener("change", drawSheet);
  function useLayout(parsed, note) {
    layout = parsed;
    packId = layout.packs[0] ? layout.packs[0].id : null;
    suggestions = null;
    showPacks(); drawAll();
    if (note) say(note, true);
  }
  function cloneLayout(l) { return TileLayout.parse(TileLayout.toJson(l)).layout; }
  $("layoutStd").addEventListener("click", async () => {
    if (dirty && !(await ask("Go back to the standard layout? Role changes you made here are lost (pixels stay).", { ok: "Use standard" }))) return;
    useLayout(cloneLayout(standard), "Standard layout.");
  });
  $("layoutFile").addEventListener("change", async (e) => {
    const f = e.target.files[0]; e.target.value = "";
    if (!f) return;
    const { layout: l, errors } = TileLayout.parse(await f.text());
    if (!l) { say(`That roles file can't be used: ${errors.slice(0, 3).join("; ")}${errors.length > 3 ? "…" : ""}`, false); return; }
    useLayout(l, `Roles from ${f.name}: ${l.packs.length} packs.`);
  });

  // ---- pixel editing ---------------------------------------------------------------------------------------
  // Undo covers pixels, palette and roles together (a flip can move a role).
  const state = () => ({ pixels: img.pixels.slice(), palette: img.palette.slice(), roles: TileLayout.toJson(layout) });
  function snapshot() { undoStack.push(state()); if (undoStack.length > 200) undoStack.shift(); redoStack.length = 0; }
  function restore(from, to) {
    const s = from.pop(); if (!s) return;
    to.push(state());
    img.pixels = s.pixels; img.palette = s.palette; layout = TileLayout.parse(s.roles).layout || layout;
    suggestions = null; setDirty(true); showPacks(); drawAll();
  }
  $("undo").addEventListener("click", () => restore(undoStack, redoStack));
  $("redo").addEventListener("click", () => restore(redoStack, undoStack));
  function setTool(t) {
    tool = t;
    for (const [id, name] of [["tPencil", "pencil"], ["tFill", "fill"], ["tPick", "pick"]]) { $(id).classList.toggle("on", t === name); $(id).setAttribute("aria-pressed", String(t === name)); }
  }
  $("tPencil").addEventListener("click", () => setTool("pencil"));
  $("tFill").addEventListener("click", () => setTool("fill"));
  $("tPick").addEventListener("click", () => setTool("pick"));
  function pixelAt(e) {
    const c = $("tile"), r = c.getBoundingClientRect();
    const px = Math.floor(((e.clientX - r.left) * c.width) / r.width / TK) - RIM, py = Math.floor(((e.clientY - r.top) * c.height) / r.height / TK) - RIM;
    return px >= 0 && px < TS && py >= 0 && py < TS ? [px, py] : null;
  }
  const sheetIndex = (px, py) => { const [x, y] = TileOps.slotXY(sel); return (y + py) * SHEET_W + x + px; };
  let painting = null;
  $("tile").addEventListener("contextmenu", (e) => e.preventDefault());
  $("tile").addEventListener("pointerdown", (e) => {
    const p = pixelAt(e); if (!p) return;
    const c = e.button === 2 ? 0 : colour;
    if (tool === "pick" || e.altKey) { colour = img.pixels[sheetIndex(...p)]; drawPalette(); setTool(tool === "pick" ? "pencil" : tool); return; }
    snapshot();
    if (tool === "fill") { floodTile(p[0], p[1], c); edited(); return; }
    painting = c; $("tile").setPointerCapture(e.pointerId); plot(p, c);
  });
  $("tile").addEventListener("pointermove", (e) => {
    const p = pixelAt(e);
    if (painting !== null && p) plot(p, painting);
    $("status").textContent = p ? `Tile ${sel} · pixel ${p[0]}, ${p[1]} · colour ${img.pixels[sheetIndex(...p)]}` : "";
  });
  const stop = () => { if (painting !== null) { painting = null; edited(); } };
  $("tile").addEventListener("pointerup", stop);
  $("tile").addEventListener("pointercancel", stop);
  function plot(p, c) {
    const i = sheetIndex(...p);
    if (img.pixels[i] === c) return;
    img.pixels[i] = c;
    const ctx = sheetCanvas.getContext("2d"); ctx.fillStyle = css(c);
    const [x, y] = TileOps.slotXY(sel); ctx.fillRect(x + p[0], y + p[1], 1, 1);
    drawTile();
  }
  function floodTile(x0, y0, c) {
    const from = img.pixels[sheetIndex(x0, y0)]; if (from === c) return;
    const stack = [[x0, y0]];
    while (stack.length) {
      const [x, y] = stack.pop();
      if (x < 0 || y < 0 || x >= TS || y >= TS || img.pixels[sheetIndex(x, y)] !== from) continue;
      img.pixels[sheetIndex(x, y)] = c;
      stack.push([x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]);
    }
  }
  function edited() { suggestions = null; setDirty(true); renderSheet(); drawSheet(); drawTile(); drawPreview(); showSlot(); }

  // palette
  $("pal").addEventListener("click", (e) => {
    const c = $("pal"), r = c.getBoundingClientRect();
    const x = Math.floor(((e.clientX - r.left) * c.width) / r.width / 8), y = Math.floor(((e.clientY - r.top) * c.height) / r.height / 16);
    colour = Math.max(0, Math.min(255, y * 32 + x)); drawPalette();
  });
  $("colEdit").addEventListener("change", (e) => {
    if (colour === 0) { say("Colour 0 is empty space; it stays black.", false); drawPalette(); return; }
    const v = e.target.value, rr = parseInt(v.slice(1, 3), 16), gg = parseInt(v.slice(3, 5), 16), bb = parseInt(v.slice(5, 7), 16);
    snapshot();
    img.palette[colour * 3] = rr; img.palette[colour * 3 + 1] = gg; img.palette[colour * 3 + 2] = bb;
    let used = 0; for (const p of img.pixels) if (p === colour) used++;
    say(`Colour ${colour} changed: ${used} pixels use it.`, true);
    setDirty(true); drawAll();
  });

  // tile ops
  function turned(op) {
    snapshot();
    const px = TileOps.getTile(img, sel);
    TileOps.setTile(img, sel, TileOps[op](px));
    const r = TileLayout.roleOfTile(layout, sel);
    if ($("turnRole").checked && r) {
      // the art now looks like another piece: move this slot's role to match, if the pack doesn't have that piece elsewhere
      const to = layout.byOpen.get(TileOps.turnOpen(r.role.open, op));
      if (to && to !== r.role.name) {
        const other = r.pack.tiles.get(to);
        r.pack.tiles.set(to, sel);
        if (other) r.pack.tiles.set(r.role.name, other); else r.pack.tiles.delete(r.role.name);
        say(`Tile ${sel} is now ${r.pack.name}'s ${layout.roles.get(to).label.toLowerCase()}${other ? `; tile ${other} took its old role` : ""}.`, true);
      }
    }
    edited(); showPacks();
  }
  $("flipH").addEventListener("click", () => turned("flipH"));
  $("flipV").addEventListener("click", () => turned("flipV"));
  $("rotT").addEventListener("click", () => turned("rotateCW"));
  $("copyT").addEventListener("click", () => { clip = { px: TileOps.getTile(img, sel), from: sel }; say(`Tile ${sel} copied.`, true); });
  $("pasteT").addEventListener("click", () => {
    if (!clip) { say("Copy a tile first.", false); return; }
    snapshot(); TileOps.setTile(img, sel, clip.px); edited();
  });
  $("clearT").addEventListener("click", () => { snapshot(); TileOps.setTile(img, sel, new Uint8Array(TS * TS)); edited(); });

  // sheet selection
  $("sheet").addEventListener("click", (e) => {
    const c = $("sheet"), r = c.getBoundingClientRect();
    const x = Math.floor(((e.clientX - r.left) * c.width) / r.width / (TS * zoom)), y = Math.floor(((e.clientY - r.top) * c.height) / r.height / (TS * zoom));
    if (x < 0 || y < 0 || x >= COLS || y >= ROWS) return;
    select(y * COLS + x + 1);
  });
  $("sheet").addEventListener("mousemove", (e) => {
    const c = $("sheet"), r = c.getBoundingClientRect();
    const x = Math.floor(((e.clientX - r.left) * c.width) / r.width / (TS * zoom)), y = Math.floor(((e.clientY - r.top) * c.height) / r.height / (TS * zoom));
    if (x < 0 || y < 0 || x >= COLS || y >= ROWS) return;
    const t = y * COLS + x + 1, ro = TileLayout.roleOfTile(layout, t);
    $("status").textContent = `Tile ${t}${ro ? ` · ${ro.pack.name}: ${ro.role.name} (${ro.role.open || "closed"})` : otherLabel(t) ? " · " + otherLabel(t) : ""}`;
  });
  function select(t) { sel = Math.max(1, Math.min(COLS * ROWS, t)); drawSheet(); drawTile(); showSlot(); }
  $("zIn").addEventListener("click", () => { zoom = Math.min(6, zoom + 1); drawSheet(); });
  $("zOut").addEventListener("click", () => { zoom = Math.max(1, zoom - 1); drawSheet(); });

  // ---- preview: sample walls built from the selected pack -------------------------------------------------
  const PW = 30, PH = 20;
  let cells = new Uint8Array(PW * PH);
  const SHAPES = {
    sampler: ["..............................", ".#....####......#.............", ".#....#..#.....###....#####...", ".#....####......#.....#...#...",
      "..........................#...", "..####.......####.....#####...", "..#..........#..#.............", "..#....###...####.....#.#.#...",
      "..####.###...#..#.....#####...", ".......###...####.....#.#.#...", "......................#####...", ".#.#..........................",
      "..............######..........", "..###.........#....#....##....", "..#.#.........#.##.#....##....", "..###.........#....#..........",
      "..............######...#####..", "..........................#...", "....######.................#..", ".............................."],
    box: ["..............................", ".######...........###########.", ".######...........#.........#.", ".######...........#.#######.#.", ".######...........#.#.....#.#.",
      "..................#.#.###.#.#.", "...####...........#.#.#.#.#.#.", "...#..#...........#.#.###.#.#.", "...####...........#.#.....#.#.", "..................#.#######.#.",
      "..###.............#.........#.", "..###.............###########.", "..###.........................", "..............................", "..######....######............",
      "..#....#....#....#............", "..#....#....######............", "..######....#....#............", "............######............", ".............................."],
    lines: ["..............................", ".#..#..###########...........", ".#..#........................", ".#..#..#.#.#.#.#.#.#.........", ".#..#........................",
      ".#..#..##.##.##.##.##........", ".#..#........................", "....#..###.###.###...........", ".#..#........................", ".#...........................",
      ".#...####################.....", "..............................", "..#.....#.....#..............", "..#.....##....###............", "..#......#.....#.............",
      "..####..........#............", "..............................", "..............................", "..............................", ".............................."],
    cross: ["..............................", "....#.........#.......#.......", "....#.........#.......#.......", "..#####....#######..#####.....", "....#.........#.......#.......",
      "....#.........#.......#.......", "..............#...............", "..#.#.#.......#.......#####...", "..#####....#######....#...#...", "..#.#.#.......#.......#####...",
      "..............#.......#...#...", "...........#######....#####...", "..............................", "..#########...................", "..#...#...#...................",
      "..#########...................", "..#...#...#...................", "..#########...................", "..............................", ".............................."],
  };
  function setShape(name) {
    cells = new Uint8Array(PW * PH);
    if (name === "maze") {
      for (let y = 0; y < PH; y++) for (let x = 0; x < PW; x++) if (x % 2 === 0 && y % 2 === 0) cells[y * PW + x] = 1;
      let seed = 7; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
      for (let y = 0; y < PH; y += 2) for (let x = 0; x < PW; x += 2) { if (rnd() < 0.5) { if (x + 1 < PW) cells[y * PW + x + 1] = 1; } else if (y + 1 < PH) cells[(y + 1) * PW + x] = 1; }
    } else if (SHAPES[name]) SHAPES[name].forEach((row, y) => [...row].forEach((ch, x) => { if (ch === "#" && x < PW && y < PH) cells[y * PW + x] = 1; }));
    drawPreview();
  }
  function drawPreview() {
    const c = $("prev"), ctx = c.getContext("2d"), k = c.width / PW;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#000"; ctx.fillRect(0, 0, c.width, c.height);
    const p = currentPack();
    const wall = (x, y) => x >= 0 && y >= 0 && x < PW && y < PH && cells[y * PW + x];
    let fallbacks = 0;
    for (let y = 0; y < PH; y++) for (let x = 0; x < PW; x++) {
      if (!wall(x, y)) continue;
      const open = TileLayout.openOf(wall(x, y - 1), wall(x + 1, y), wall(x, y + 1), wall(x - 1, y));
      const t = p ? TileLayout.pick(layout, p.id, open) : 0;
      if (t) { const [sx, sy] = TileOps.slotXY(t); ctx.drawImage(sheetCanvas, sx, sy, TS, TS, x * k, y * k, k, k); }
      else { ctx.fillStyle = "#22d3ee"; ctx.fillRect(x * k, y * k, k, k); }
      if (p && !p.tiles.has(layout.byOpen.get(open))) { fallbacks++; ctx.strokeStyle = "#ff8f8f"; ctx.strokeRect(x * k + 0.5, y * k + 0.5, k - 1, k - 1); }
    }
    if ($("prevGrid").checked) {
      ctx.strokeStyle = "#ffffff14";
      for (let x = 0; x <= PW; x++) { ctx.beginPath(); ctx.moveTo(x * k + 0.5, 0); ctx.lineTo(x * k + 0.5, c.height); ctx.stroke(); }
      for (let y = 0; y <= PH; y++) { ctx.beginPath(); ctx.moveTo(0, y * k + 0.5); ctx.lineTo(c.width, y * k + 0.5); ctx.stroke(); }
    }
    $("prevNote").textContent = !p ? "Select a pack to see its pieces here." :
      fallbacks ? `${p.name} lacks some pieces: ${fallbacks} red-framed cells use a stand-in.` : `Built from ${p.name}, each cell picked by which neighbours are wall.`;
  }
  let prevPaint = null;
  $("prev").addEventListener("pointerdown", (e) => {
    const c = $("prev"), r = c.getBoundingClientRect(), k = c.width / PW;
    const x = Math.floor(((e.clientX - r.left) * c.width) / r.width / k), y = Math.floor(((e.clientY - r.top) * c.height) / r.height / k);
    if (x < 0 || y < 0 || x >= PW || y >= PH) return;
    prevPaint = cells[y * PW + x] ? 0 : 1; cells[y * PW + x] = prevPaint; c.setPointerCapture(e.pointerId); drawPreview();
  });
  $("prev").addEventListener("pointermove", (e) => {
    if (prevPaint === null) return;
    const c = $("prev"), r = c.getBoundingClientRect(), k = c.width / PW;
    const x = Math.floor(((e.clientX - r.left) * c.width) / r.width / k), y = Math.floor(((e.clientY - r.top) * c.height) / r.height / k);
    if (x < 0 || y < 0 || x >= PW || y >= PH || cells[y * PW + x] === prevPaint) return;
    cells[y * PW + x] = prevPaint; drawPreview();
  });
  $("prev").addEventListener("pointerup", () => { prevPaint = null; });
  $("shape").addEventListener("change", (e) => setShape(e.target.value));
  $("prevGrid").addEventListener("change", drawPreview);

  // ---- open ---------------------------------------------------------------------------------------------
  async function confirmDiscard() { return !dirty || ask("Discard your unsaved changes to this tileset?", { ok: "Discard", danger: true }); }
  function openBytes(name, bytes) {
    const lower = name.toLowerCase();
    let next, from;
    if (lower.endsWith(".lvl")) {
      const s = LvlFile.split(bytes);
      if (!s.bmp) throw new Error(`${name} has no tileset of its own (the game uses the default one). Start from New empty tileset or open a .bmp, then Download .lvl to put it in.`);
      next = Bmp.fitSheet(Bmp.read(s.bmp));
      from = { name: name.replace(/\.lvl$/i, ""), elvl: s.elvl, tiles: s.tiles, fromLvl: true };
    } else {
      next = Bmp.fitSheet(Bmp.read(bytes));
      from = { name: name.replace(/\.[^.]+$/, ""), elvl: null, tiles: new Uint8Array(0), fromLvl: false };
    }
    img = next; src = from;
    undoStack.length = 0; redoStack.length = 0; suggestions = null;
    setDirty(false);
    $("edname").textContent = `Tile editor · ${src.name}`;
    drawAll();
  }
  async function openImageFile(f) {
    // PNG (and anything else the browser can decode) goes through a canvas, then down to 256 colours
    const bmp = await createImageBitmap(f);
    const c = document.createElement("canvas"); c.width = bmp.width; c.height = bmp.height;
    const ctx = c.getContext("2d"); ctx.drawImage(bmp, 0, 0);
    const d = ctx.getImageData(0, 0, c.width, c.height);
    img = Bmp.fitSheet(Bmp.quantize(d.data, c.width, c.height));
    src = { name: f.name.replace(/\.[^.]+$/, ""), elvl: null, tiles: new Uint8Array(0), fromLvl: false };
    undoStack.length = 0; redoStack.length = 0; suggestions = null; setDirty(true);
    $("edname").textContent = `Tile editor · ${src.name}`;
    drawAll();
    return c.width !== SHEET_W || c.height !== SHEET_H;
  }
  $("file").addEventListener("change", async (e) => {
    const f = e.target.files[0]; e.target.value = "";
    if (!f || !(await confirmDiscard())) return;
    try {
      if (/\.png$/i.test(f.name)) {
        const resized = await openImageFile(f);
        say(`${f.name} opened and reduced to one 256-colour palette${resized ? "; it wasn't 304 × 160, so it was cropped or padded" : ""}.`, !resized);
      } else {
        openBytes(f.name, new Uint8Array(await f.arrayBuffer()));
        say(`${f.name} opened.`, true);
      }
    } catch (err) { say(err.message, false); }
  });
  $("newSheet").addEventListener("click", async () => {
    if (!(await confirmDiscard())) return;
    img = { width: SHEET_W, height: SHEET_H, palette: Bmp.defaultPalette(), pixels: new Uint8Array(SHEET_W * SHEET_H) };
    src = { name: "untitled", elvl: null, tiles: new Uint8Array(0), fromLvl: false };
    undoStack.length = 0; redoStack.length = 0; suggestions = null; setDirty(false);
    $("edname").textContent = "Tile editor · new tileset";
    drawAll();
  });
  async function loadShared() {
    const r = await fetch(API + "/maps", { credentials: "same-origin" }).catch(() => null);
    const j = r && r.ok ? await r.json().catch(() => null) : null;
    for (const n of (j && j.maps) || []) $("shared").append(new Option(n, n));
  }
  $("shared").addEventListener("change", async (e) => {
    const name = e.target.value; e.target.value = "";
    if (!name || !(await confirmDiscard())) return;
    const r = await fetch(API + "/maps/file?name=" + encodeURIComponent(name), { credentials: "same-origin" }).catch(() => null);
    if (!r || !r.ok) { say(`Couldn't open ${name}.`, false); return; }
    try { openBytes(name, new Uint8Array(await r.arrayBuffer())); say(`${name} opened.`, true); } catch (err) { say(err.message, false); }
  });

  // ---- save -----------------------------------------------------------------------------------------------
  function download(bytes, name, type) {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([bytes], { type }));
    a.download = name; document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  }
  const lvlBytes = () => LvlFile.join(Bmp.write(img), src.elvl, src.tiles);
  $("saveBmp").addEventListener("click", () => { download(Bmp.write(img), src.name + ".bmp", "image/bmp"); setDirty(false); });
  $("saveLvl").addEventListener("click", () => {
    download(lvlBytes(), src.name + ".lvl", "application/octet-stream");
    say(src.fromLvl ? `${src.name}.lvl: your tileset with the map's walls${src.elvl ? " and eLVL data" : ""} kept.` : `${src.name}.lvl has this tileset and no walls yet: open it in the map editor to draw.`, true);
    setDirty(false);
  });
  $("saveRoles").addEventListener("click", () => {
    const l = Object.assign({}, layout, { name: layout.name.includes(src.name) ? layout.name : `${src.name} roles` });
    download(new TextEncoder().encode(TileLayout.toJson(l)), src.name + ".roles.json", "application/json");
  });
  $("saveLib").addEventListener("click", async () => {
    const r = await fetch(API + "/library/save?name=" + encodeURIComponent(src.name), { method: "POST", credentials: "same-origin", headers: hdrs("application/octet-stream"), body: lvlBytes() }).catch(() => null);
    const j = r ? await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status })) : { ok: false, message: "No connection." };
    if (r && r.status === 401) j.message = "Sign in to keep maps in your collection.";
    say(j.ok ? `${j.message} Open it from Your maps in the map editor.` : j.message, j.ok);
    if (j.ok) setDirty(false);
  });

  // ---- keys -----------------------------------------------------------------------------------------------
  document.addEventListener("keydown", (e) => {
    if (e.target.closest("input, select, textarea, dialog")) return;
    const mod = e.metaKey || e.ctrlKey, k = e.key.toLowerCase();
    if (mod && k === "z") { e.preventDefault(); restore(e.shiftKey ? redoStack : undoStack, e.shiftKey ? undoStack : redoStack); return; }
    if (mod && k === "y") { e.preventDefault(); restore(redoStack, undoStack); return; }
    if (mod && k === "c") { e.preventDefault(); $("copyT").click(); return; }
    if (mod && k === "v") { e.preventDefault(); $("pasteT").click(); return; }
    if (mod) return;
    const keys = { b: () => setTool("pencil"), g: () => setTool("fill"), i: () => setTool("pick"), c: () => $("copyT").click(), v: () => $("pasteT").click(),
      h: () => turned("flipH"), j: () => turned("flipV"), r: () => turned("rotateCW"),
      arrowleft: () => select(sel - 1), arrowright: () => select(sel + 1), arrowup: () => select(sel - COLS), arrowdown: () => select(sel + COLS) };
    if (keys[k]) { e.preventDefault(); keys[k](); }
  });

  // ---- start ----------------------------------------------------------------------------------------------
  (async () => {
    const r = await fetch("/arena/tile-layout.json", { credentials: "same-origin" }).catch(() => null);
    const parsed = r && r.ok ? TileLayout.parse(await r.text()) : { layout: null, errors: ["couldn't load /arena/tile-layout.json"] };
    standard = parsed.layout || TileLayout.parse({ format: "dare-tile-layout", version: 1, roles: { single: { open: "" } }, packs: [] }).layout;
    if (!parsed.layout) say("The standard layout didn't load: " + parsed.errors.join("; "), false);
    layout = cloneLayout(standard);
    zoom = Math.max(2, Math.min(4, Math.floor(($("sheet").parentElement.parentElement.clientWidth - 30) / SHEET_W)));
    packId = layout.packs[0] ? layout.packs[0].id : null;
    setShape("sampler"); showPacks(); drawAll();
    loadShared();
    const q = new URLSearchParams(location.search).get("map");
    if (q) { $("shared").append(new Option(q, q)); $("shared").value = q; $("shared").dispatchEvent(new Event("change")); }
  })();
})();
