// Tile studio: the tileset editor, mounted in the arena editor's Tile Editor tab (/arena/me#tiles)
// and on its own page (/arena/tiles). TileStudio.mount(root, { arena }) builds it inside root.
//   Drawing: pencil and eraser with brush sizes, line, rectangle, ellipse (outline or filled),
//   fill, replace colour, colour picker, rectangle selection (move, copy, cut, paste, flip, rotate),
//   mirror drawing inside a tile, sheet and single-tile views, zoom, pixel and tile grids, undo.
//   Checking: a tile repeated as a grid, a pack built into walls, a reference image and the
//   tileset as opened shown over or under the art.
//   Roles: which slot is which wall piece (docs/tile-layout.md), by hand or read from the edges.
//   Keeping: Your tilesets (/api/tilesets), downloads, and putting the tileset into a map in Your
//   maps (the arena's current map, or one already there) as a draft to Apply or Publish later.
// Needs tiles.js; dialogs.js (window.Dlg) when present. Text goes in with textContent only.
"use strict";

window.TileStudio = (() => {
  const { TS, COLS, ROWS, SHEET_W, SHEET_H } = TileSheet;
  const API = "/arena/api";
  const HDR = { "X-Requested-With": "dare-arena" };
  const ask = (text, o) => (window.Dlg ? Dlg.confirm(text, o) : Promise.resolve(window.confirm(text)));
  const askText = (text, o) => (window.Dlg ? Dlg.prompt(text, o) : Promise.resolve(window.prompt(text, (o && o.value) || "")));
  const hex = (r, g, b) => "#" + [r, g, b].map((v) => v.toString(16).padStart(2, "0")).join("");

  const TOOLS = [
    ["pencil", "Pencil", "B"], ["eraser", "Eraser", "E"], ["line", "Line", "L"], ["rect", "Rectangle", "U"],
    ["ellipse", "Ellipse", "O"], ["fill", "Fill", "G"], ["replace", "Replace colour", "Shift+G"], ["pick", "Colour picker", "I"],
    ["select", "Select", "M"],
  ];
  const HELP = [
    ["B / E / L / U / O", "pencil, eraser, line, rectangle, ellipse"], ["G / Shift+G / I / M", "fill, replace colour, picker, select"],
    ["[ and ]", "brush size"], ["F", "filled shapes on or off"], ["X", "swap the two colours"], ["Alt+click", "pick a colour with any tool"],
    ["Right-click", "draw with the second colour"], ["Shift (line, shapes)", "45° lines, squares and circles"],
    ["Tab", "sheet view or tile view"], ["+ / −", "zoom"], ["Arrows", "next tile; move a lifted selection"],
    ["Ctrl+C / X / V", "copy, cut, paste (the selection, or the tile)"], ["Del", "clear the selection"], ["Enter / Esc", "drop / cancel a selection"],
    ["Ctrl+A", "select the tile (or the sheet)"], ["H / J / R", "flip ↔, flip ↕, rotate (selection or tile)"],
    ["Ctrl+Z / Ctrl+Shift+Z", "undo, redo"], ["Space+drag, middle-drag", "scroll the view"],
  ];

  const TEMPLATE = `
  <div class="st-bar" role="toolbar" aria-label="Drawing tools">
    <div class="st-group st-tools"></div>
    <div class="st-group">
      <label class="st-mini" title="Brush size ([ and ])">Size <input type="number" class="st-size" min="1" max="8" value="1"></label>
      <label class="st-mini" title="Filled rectangles and ellipses (F)"><input type="checkbox" class="st-filled"> Filled</label>
      <label class="st-mini" title="Mirror what you draw across the tile's middle"><input type="checkbox" class="st-mirx"> Mirror ↔</label>
      <label class="st-mini"><input type="checkbox" class="st-miry"> Mirror ↕</label>
      <label class="st-mini" title="Paste and move without colour 0, so what's under shows through"><input type="checkbox" class="st-clear0" checked> Selection: colour 0 see-through</label>
    </div>
    <div class="st-group">
      <button type="button" class="ghost small st-undo" title="Undo (Ctrl+Z)">Undo</button>
      <button type="button" class="ghost small st-redo" title="Redo (Ctrl+Shift+Z)">Redo</button>
      <button type="button" class="ghost small st-viewbtn" title="Sheet view or tile view (Tab)">Tile view</button>
      <button type="button" class="ghost small st-zout" aria-label="Zoom out">−</button><span class="st-zoom hint"></span>
      <button type="button" class="ghost small st-zin" aria-label="Zoom in">+</button>
      <label class="st-mini"><input type="checkbox" class="st-pgrid" checked> Pixel grid</label>
      <label class="st-mini"><input type="checkbox" class="st-tgrid" checked> Tile grid</label>
      <label class="st-mini"><input type="checkbox" class="st-roles" checked> Roles</label>
      <button type="button" class="ghost small st-full" title="Use the whole window">Full screen</button>
      <details class="st-help"><summary class="btn ghost small">Keys</summary><dl></dl></details>
    </div>
  </div>
  <div class="st-body">
    <aside class="st-left">
      <section class="st-card">
        <h4>Tileset <span class="tag draft st-dirty" hidden>Unsaved</span></h4>
        <p class="st-name"></p>
        <select class="st-open" aria-label="Open a tileset"><option value="">Open…</option></select>
        <div class="st-row"><label class="btn ghost small">File…<input type="file" class="st-file" accept=".lvl,.bmp,.bm2,.png" hidden></label>
          <button type="button" class="ghost small st-new">New empty</button></div>
        <div class="st-row">
          <button type="button" class="small st-save" title="Save to Your tilesets">Save</button>
          <button type="button" class="ghost small st-saveas">Save as new</button></div>
        <div class="st-row">
          <button type="button" class="ghost small st-dlbmp">.bmp</button>
          <button type="button" class="ghost small st-dllvl" title="The opened map with this tileset">.lvl</button>
          <button type="button" class="ghost small st-dlroles">Roles .json</button></div>
        <p class="hint st-libhint"></p>
        <ul class="st-lib"></ul>
        <p class="hint st-defhint" hidden>Defaults (in the standard layout; Save keeps a copy to change):</p>
        <div class="st-defaults"></div>
      </section>
      <section class="st-card">
        <h4>Use it in a map</h4>
        <select class="st-target" aria-label="Map to put this tileset into"></select>
        <button type="button" class="small st-apply">Put tileset into map</button>
        <p class="hint">The map goes to Your maps as a draft: nothing changes in game until you Apply it on the Map tab or Publish it from the map editor.</p>
      </section>
      <section class="st-card">
        <h4>Colours</h4>
        <div class="st-row st-cols">
          <button type="button" class="st-sw st-c1" title="First colour (left click)"></button>
          <button type="button" class="st-sw st-c2" title="Second colour (right click)"></button>
          <span class="hint st-colname"></span></div>
        <canvas class="px st-pal" width="256" height="128" aria-label="Palette: left click first colour, right click second, double-click to change"></canvas>
        <div class="st-row">
          <label class="btn ghost small" title="Change the first colour everywhere it is used">Change…<input type="color" class="st-coledit" hidden></label>
          <button type="button" class="ghost small st-uses" title="Flash every pixel that uses the first colour">Show uses</button>
          <label class="btn ghost small" title="Take another tileset's palette; pixels keep the nearest colour">Palette from file…<input type="file" class="st-palfile" accept=".lvl,.bmp,.bm2" hidden></label></div>
        <p class="hint">256 colours for the whole tileset. Colour 0 is empty space: see-through in game.</p>
      </section>
      <section class="st-card">
        <h4>Reference</h4>
        <div class="st-row"><label class="btn ghost small">Image…<input type="file" class="st-reffile" accept="image/*,.bmp" hidden></label>
          <select class="st-refmode" aria-label="Where the reference shows"><option value="off">off</option><option value="under">under the art</option><option value="over">over the art</option></select></div>
        <label class="st-mini">Opacity <input type="range" class="st-refop" min="5" max="95" value="40"></label>
        <label class="st-mini" title="The tileset as you opened it, faintly over the art"><input type="checkbox" class="st-onion"> Show the tileset as opened</label>
      </section>
    </aside>
    <main class="st-main">
      <div class="st-scroll"><canvas class="st-view"></canvas></div>
      <div class="st-status hint" aria-live="polite"></div>
      <div class="st-msg" role="status"></div>
    </main>
    <aside class="st-right">
      <section class="st-card">
        <h4>Slot</h4>
        <div class="st-row"><canvas class="px st-slotprev" width="16" height="16" aria-hidden="true"></canvas>
          <div><b class="st-slotname"></b><br><span class="hint st-slotrole"></span></div></div>
        <label class="st-mini">Pack <select class="st-slotpack"></select></label>
        <label class="st-mini">Piece <select class="st-slotpiece"></select></label>
        <p class="hint st-slotsg"></p>
        <label class="st-mini"><input type="checkbox" class="st-turnrole" checked> Flip and rotate turn the piece's role too</label>
      </section>
      <section class="st-card">
        <h4>Seams</h4>
        <canvas class="px st-seam" width="144" height="144" aria-label="The tile repeated, to check its edges meet"></canvas>
        <p class="hint">The selected tile repeated 3 × 3.</p>
      </section>
      <section class="st-card">
        <h4>How the pack joins</h4>
        <canvas class="px st-prev" width="336" height="224" aria-label="Sample walls built from the pack: click cells to add or remove wall"></canvas>
        <div class="st-row"><select class="st-shape" aria-label="Sample shape">
          <option value="sampler">Sampler</option><option value="box">Boxes</option><option value="lines">Lines and ends</option>
          <option value="cross">Junctions</option><option value="maze">Maze</option><option value="clear">Empty</option></select></div>
        <p class="hint st-prevnote"></p>
      </section>
      <section class="st-card">
        <h4>Packs <span class="hint st-layoutname"></span></h4>
        <ul class="st-packs"></ul>
        <div class="st-row">
          <button type="button" class="ghost small st-packnew" title="Laid out like the standard pack, from the selected slot">New pack here</button>
          <button type="button" class="ghost small st-packdel">Remove pack</button></div>
        <div class="st-row">
          <button type="button" class="ghost small st-check" title="Read each tile's edges; red dots mark pieces whose art doesn't look like their role">Check edges</button>
          <button type="button" class="ghost small st-fromedges">Roles from edges</button></div>
        <div class="st-row">
          <label class="btn ghost small">Load roles…<input type="file" class="st-rolesfile" accept=".json" hidden></label>
          <button type="button" class="ghost small st-std">Standard layout</button></div>
        <p class="hint warnnote st-layoutwarn" hidden></p>
      </section>
    </aside>
  </div>`;

  // sample walls for "How the pack joins" (# = wall)
  const PW = 21, PH = 14;
  const SHAPES = {
    sampler: [".....................", ".#...####....#.......", ".#...#..#...###......", ".#...####....#.......", ".....................",
      "..####.....####......", "..#........#..#......", "..#...###..####......", "..####.###..#..#.....", "......###..####......",
      ".....................", "..######..####.......", "..#....#..#..#.......", "..######..####......."],
    box: [".....................", ".#####......#######..", ".#####......#.....#..", ".#####......#.###.#..", "............#.#.#.#..",
      "..###.......#.###.#..", "..#.#.......#.....#..", "..###.......#######..", ".....................", "..######.............",
      "..#....#....####.....", "..#....#....####.....", "..######.............", "....................."],
    lines: [".....................", ".#..#..##########....", ".#..#...............", ".#..#..#.#.#.#.#.....", ".#..#................",
      ".#..#..##.##.##......", ".#..#................", "....#..###.###.......", ".#...................", ".#..#######.........",
      ".....................", "..#....#....#........", "..#....##...###......", "..####..#....#......."],
    cross: [".....................", "....#........#.......", "..#####....#####.....", "....#........#.......", "....#..............",
      "..#.#.#....#####.....", "..#####....#...#.....", "..#.#.#....#####.....", "...........#...#.....", "..#######..#####.....",
      "..#..#..#............", "..#######............", "..#..#..#............", "..#######............"],
  };
  const PACK_COLOURS = ["#71e4fa", "#ffcf7a", "#6ee7a8", "#f0abfc", "#fda4af", "#a5b4fc", "#fde68a", "#93c5fd"];

  function mount(root, opts = {}) {
    root.classList.add("studio");
    root.innerHTML = TEMPLATE; // fixed markup only; everything from data goes in with textContent
    const q = (c) => root.querySelector("." + c);

    // ---- state -------------------------------------------------------------------------------------
    let arena = opts.arena || "";
    let img = blankImage();
    let original = img.pixels.slice(), originalPal = img.palette.slice(); // the tileset as opened
    let src = { name: "untitled", elvl: null, tiles: new Uint8Array(0), from: "new" };
    let libId = null; // Your tilesets id of what's open, if any
    let standard = null, layout = null;
    let sel = 1, packId = null, c1 = 1, c2 = 0, tool = "pencil", dirty = false;
    let mode = "sheet", zoom = 3, tileZoom = 20;
    let selection = null; // {x, y, w, h} in sheet pixels
    let floating = null; // {x, y, w, h, px} lifted pixels
    let clip = null; // {w, h, px}
    let suggestions = null, refCanvas = null, flashUntil = 0;
    let drag = null, preview = null, spaceDown = false;
    const undoStack = [], redoStack = [];
    const cells = new Uint8Array(PW * PH);
    let signedIn = null; // null unknown, else boolean

    function blankImage() {
      const im = { width: SHEET_W, height: SHEET_H, palette: Bmp.defaultPalette(), pixels: new Uint8Array(SHEET_W * SHEET_H) };
      return im;
    }
    const say = (text, ok) => { const m = q("st-msg"); m.textContent = text || ""; m.className = "st-msg " + (ok ? "ok" : "bad"); clearTimeout(say.t); if (text) say.t = setTimeout(() => { m.textContent = ""; }, 9000); };
    const setDirty = (d) => { dirty = d; q("st-dirty").hidden = !d; };
    window.addEventListener("beforeunload", (e) => { if (dirty && root.isConnected) { e.preventDefault(); e.returnValue = ""; } });

    // ---- rendering --------------------------------------------------------------------------------
    const sheetCanvas = document.createElement("canvas"); sheetCanvas.width = SHEET_W; sheetCanvas.height = SHEET_H;
    const origCanvas = document.createElement("canvas"); origCanvas.width = SHEET_W; origCanvas.height = SHEET_H;
    function paint(canvas, pixels, palette) {
      const ctx = canvas.getContext("2d"), d = ctx.createImageData(SHEET_W, SHEET_H);
      for (let i = 0; i < pixels.length; i++) {
        const c = pixels[i];
        if (!c) continue; // colour 0 stays transparent here; views put black behind it
        d.data[i * 4] = palette[c * 3]; d.data[i * 4 + 1] = palette[c * 3 + 1]; d.data[i * 4 + 2] = palette[c * 3 + 2]; d.data[i * 4 + 3] = 255;
      }
      ctx.putImageData(d, 0, 0);
    }
    const renderSheet = () => paint(sheetCanvas, img.pixels, img.palette);
    const css = (i) => `rgb(${img.palette[i * 3]},${img.palette[i * 3 + 1]},${img.palette[i * 3 + 2]})`;
    const slotXY = (t) => TileOps.slotXY(t);
    const tileAt = (x, y) => (x < 0 || y < 0 || x >= SHEET_W || y >= SHEET_H ? 0 : Math.floor(y / TS) * COLS + Math.floor(x / TS) + 1);
    // the part of the sheet the view shows, in sheet pixels
    function viewRect() {
      if (mode === "sheet") return { x: 0, y: 0, w: SHEET_W, h: SHEET_H, z: zoom };
      const [x, y] = slotXY(sel);
      return { x: x - 8, y: y - 8, w: 32, h: 32, z: tileZoom };
    }
    function draw() {
      const v = viewRect(), c = q("st-view"), W = v.w * v.z, H = v.h * v.z;
      if (c.width !== W) c.width = W;
      if (c.height !== H) c.height = H;
      const ctx = c.getContext("2d");
      ctx.imageSmoothingEnabled = false;
      ctx.fillStyle = "#000"; ctx.fillRect(0, 0, W, H);
      const blit = (src, alpha) => {
        const sx = Math.max(0, v.x), sy = Math.max(0, v.y), ex = Math.min(SHEET_W, v.x + v.w), ey = Math.min(SHEET_H, v.y + v.h);
        if (ex <= sx || ey <= sy) return;
        ctx.globalAlpha = alpha;
        ctx.drawImage(src, sx, sy, ex - sx, ey - sy, (sx - v.x) * v.z, (sy - v.y) * v.z, (ex - sx) * v.z, (ey - sy) * v.z);
        ctx.globalAlpha = 1;
      };
      const refMode = q("st-refmode").value, refOp = q("st-refop").value / 100;
      if (refCanvas && refMode === "under") blit(refCanvas, refOp);
      blit(sheetCanvas, 1);
      if (refCanvas && refMode === "over") blit(refCanvas, refOp);
      if (q("st-onion").checked) blit(origCanvas, 0.35);
      const px = (x, y, col) => { ctx.fillStyle = col; ctx.fillRect((x - v.x) * v.z, (y - v.y) * v.z, v.z, v.z); };
      if (floating) {
        for (let y = 0; y < floating.h; y++) for (let x = 0; x < floating.w; x++) {
          const c0 = floating.px[y * floating.w + x];
          if (c0 === 0 && q("st-clear0").checked) continue;
          px(floating.x + x, floating.y + y, c0 ? css(c0) : "#000");
        }
      }
      if (preview) for (const [i, col] of preview) px(i % SHEET_W, Math.floor(i / SHEET_W), col ? css(col) : "#000");
      if (flashUntil > Date.now()) {
        ctx.fillStyle = (Math.floor(Date.now() / 150) % 2) ? "#ff2bd6" : "#00fff0";
        for (let y = Math.max(0, v.y); y < Math.min(SHEET_H, v.y + v.h); y++) for (let x = Math.max(0, v.x); x < Math.min(SHEET_W, v.x + v.w); x++)
          if (img.pixels[y * SHEET_W + x] === c1) ctx.fillRect((x - v.x) * v.z, (y - v.y) * v.z, v.z, v.z);
      }
      // grids
      if (q("st-pgrid").checked && v.z >= 6) {
        ctx.strokeStyle = "#ffffff12"; ctx.lineWidth = 1; ctx.beginPath();
        for (let x = 0; x <= v.w; x++) { ctx.moveTo(x * v.z + 0.5, 0); ctx.lineTo(x * v.z + 0.5, H); }
        for (let y = 0; y <= v.h; y++) { ctx.moveTo(0, y * v.z + 0.5); ctx.lineTo(W, y * v.z + 0.5); }
        ctx.stroke();
      }
      if (q("st-tgrid").checked) {
        ctx.strokeStyle = "#ffffff30"; ctx.lineWidth = 1; ctx.beginPath();
        for (let x = Math.ceil(v.x / TS) * TS; x <= v.x + v.w; x += TS) { ctx.moveTo((x - v.x) * v.z + 0.5, 0); ctx.lineTo((x - v.x) * v.z + 0.5, H); }
        for (let y = Math.ceil(v.y / TS) * TS; y <= v.y + v.h; y += TS) { ctx.moveTo(0, (y - v.y) * v.z + 0.5); ctx.lineTo(W, (y - v.y) * v.z + 0.5); }
        ctx.stroke();
      }
      // reserved slots and roles (sheet view)
      if (mode === "sheet") {
        ctx.fillStyle = "#00000080";
        for (let t = layout.reserved[0]; t <= Math.min(layout.reserved[1], COLS * ROWS); t++) { const [x, y] = slotXY(t); ctx.fillRect(x * v.z, y * v.z, TS * v.z, TS * v.z); }
        if (q("st-roles").checked) drawRoles(ctx, v);
      } else {
        // dim the neighbours: only the selected tile is the work
        const [tx, ty] = slotXY(sel), a = (tx - v.x) * v.z, b = (ty - v.y) * v.z, s = TS * v.z;
        ctx.fillStyle = "#070c15a0";
        ctx.fillRect(0, 0, W, b); ctx.fillRect(0, b + s, W, H - b - s); ctx.fillRect(0, b, a, s); ctx.fillRect(a + s, b, W - a - s, s);
      }
      // selected tile
      const [sx, sy] = slotXY(sel);
      ctx.strokeStyle = "#ffffff"; ctx.lineWidth = 2; ctx.strokeRect((sx - v.x) * v.z + 1, (sy - v.y) * v.z + 1, TS * v.z - 2, TS * v.z - 2);
      // selection marquee
      const box = floating || selection;
      if (box) {
        ctx.save(); ctx.setLineDash([4, 4]); ctx.lineDashOffset = (Date.now() / 60) % 8;
        ctx.strokeStyle = "#fff"; ctx.strokeRect((box.x - v.x) * v.z + 0.5, (box.y - v.y) * v.z + 0.5, box.w * v.z - 1, box.h * v.z - 1);
        ctx.strokeStyle = "#000"; ctx.lineDashOffset += 4; ctx.strokeRect((box.x - v.x) * v.z + 0.5, (box.y - v.y) * v.z + 0.5, box.w * v.z - 1, box.h * v.z - 1);
        ctx.restore();
      }
      q("st-zoom").textContent = `${v.z}×`;
      q("st-view").classList.toggle("tilemode", mode === "tile");
    }
    const packColour = (p) => PACK_COLOURS[layout.packs.indexOf(p) % PACK_COLOURS.length];
    function drawRoles(ctx, v) {
      const k = v.z, s = TS * k;
      for (const p of layout.packs) {
        const col = packColour(p), on = p.id === packId;
        for (const [role, t] of p.tiles) {
          const [x, y] = slotXY(t), open = layout.roles.get(role).open;
          ctx.globalAlpha = on ? 1 : 0.4; ctx.strokeStyle = col; ctx.lineWidth = on ? 2 : 1;
          const x0 = x * k + 2, y0 = y * k + 2, x1 = x0 + s - 4, y1 = y0 + s - 4, m = s / 2;
          const edge = (e, a, b, cx, cy, dx, dy) => {
            ctx.beginPath();
            if (!open.includes(e)) { ctx.moveTo(...a); ctx.lineTo(...b); } else { ctx.moveTo(cx, cy); ctx.lineTo(cx + dx * 5, cy + dy * 5); }
            ctx.stroke();
          };
          edge("N", [x0, y0], [x1, y0], x * k + m, y0, 0, -1);
          edge("E", [x1, y0], [x1, y1], x1, y * k + m, 1, 0);
          edge("S", [x0, y1], [x1, y1], x * k + m, y1, 0, 1);
          edge("W", [x0, y0], [x0, y1], x0, y * k + m, -1, 0);
          const sg = suggestions && suggestions.get(t);
          if (sg && sg.open !== open) { ctx.globalAlpha = 1; ctx.fillStyle = "#ff8f8f"; ctx.beginPath(); ctx.arc(x * k + s - 7, y * k + 7, 4, 0, 7); ctx.fill(); }
        }
      }
      ctx.globalAlpha = 1;
    }
    function drawPalette() {
      const c = q("st-pal"), ctx = c.getContext("2d");
      for (let i = 0; i < 256; i++) {
        const x = (i % 32) * 8, y = Math.floor(i / 32) * 16;
        ctx.fillStyle = css(i); ctx.fillRect(x, y, 8, 16);
        if (i === 0) { ctx.strokeStyle = "#ff8f8f"; ctx.beginPath(); ctx.moveTo(x, y + 16); ctx.lineTo(x + 8, y); ctx.stroke(); }
      }
      const mark = (i, col) => { ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.strokeRect((i % 32) * 8 + 1, Math.floor(i / 32) * 16 + 1, 6, 14); ctx.lineWidth = 1; };
      mark(c2, "#ffcf7a"); mark(c1, "#ffffff");
      q("st-c1").style.background = css(c1); q("st-c2").style.background = css(c2);
      let used = 0; for (const p of img.pixels) if (p === c1) used++;
      const r = img.palette[c1 * 3], g = img.palette[c1 * 3 + 1], b = img.palette[c1 * 3 + 2];
      q("st-colname").textContent = `Colour ${c1}${c1 === 0 ? " (empty)" : ""} ${hex(r, g, b)} · ${used} px`;
      q("st-coledit").value = hex(r, g, b);
    }
    function drawSeam() {
      const c = q("st-seam"), ctx = c.getContext("2d"), [x, y] = slotXY(sel), k = c.width / (3 * TS);
      ctx.imageSmoothingEnabled = false;
      ctx.fillStyle = "#000"; ctx.fillRect(0, 0, c.width, c.height);
      for (let j = 0; j < 3; j++) for (let i = 0; i < 3; i++) ctx.drawImage(sheetCanvas, x, y, TS, TS, i * TS * k, j * TS * k, TS * k, TS * k);
      const p = q("st-slotprev").getContext("2d");
      p.clearRect(0, 0, 16, 16); p.fillStyle = "#000"; p.fillRect(0, 0, 16, 16); p.drawImage(sheetCanvas, x, y, TS, TS, 0, 0, TS, TS);
    }
    function drawPreview() {
      const c = q("st-prev"), ctx = c.getContext("2d"), k = c.width / PW;
      ctx.imageSmoothingEnabled = false;
      ctx.fillStyle = "#000"; ctx.fillRect(0, 0, c.width, c.height);
      const p = layout.packs.find((x) => x.id === packId);
      const wall = (x, y) => x >= 0 && y >= 0 && x < PW && y < PH && cells[y * PW + x];
      let stand = 0;
      for (let y = 0; y < PH; y++) for (let x = 0; x < PW; x++) {
        if (!wall(x, y)) continue;
        const open = TileLayout.openOf(wall(x, y - 1), wall(x + 1, y), wall(x, y + 1), wall(x - 1, y));
        const t = p ? TileLayout.pick(layout, p.id, open) : 0;
        if (t) { const [sx, sy] = slotXY(t); ctx.drawImage(sheetCanvas, sx, sy, TS, TS, x * k, y * k, k, k); } else { ctx.fillStyle = "#22d3ee"; ctx.fillRect(x * k, y * k, k, k); }
        if (p && !p.tiles.has(layout.byOpen.get(open))) { stand++; ctx.strokeStyle = "#ff8f8f"; ctx.strokeRect(x * k + 0.5, y * k + 0.5, k - 1, k - 1); }
      }
      q("st-prevnote").textContent = !p ? "Pick a pack to see its pieces here." :
        stand ? `${p.name} lacks some pieces: ${stand} red-framed cells use a stand-in.` : `${p.name}: each cell picked by which neighbours are wall. Click cells to change the walls.`;
    }
    function drawAll() { renderSheet(); draw(); drawPalette(); drawSeam(); drawPreview(); showSlot(); }
    function drawArt() { renderSheet(); draw(); drawSeam(); drawPreview(); }

    // ---- slot, roles and packs ------------------------------------------------------------------------
    const currentPack = () => layout.packs.find((p) => p.id === packId) || null;
    const otherLabel = (t) => {
      const ip = layout.packs.find((p) => p.interior === t);
      if (ip) return `${ip.name}: interior (the plain inside of a thick wall)`;
      for (const p of layout.packs) for (const [open, t2] of p.calm || []) if (t2 === t) return `${p.name}: calm ${open} edge (next to interior)`;
      for (const p of layout.packs) for (const [d, t2] of p.inner || []) if (t2 === t) return `${p.name}: inside corner, open to the ${d}`;
      for (const o of layout.other) if (o.tile === t || (Array.isArray(o.tiles) && o.tiles.includes(t))) return o.label; return ""; };
    function showSlot() {
      const [x, y] = slotXY(sel), r = TileLayout.roleOfTile(layout, sel), reserved = sel >= layout.reserved[0] && sel <= layout.reserved[1];
      q("st-slotname").textContent = `Tile ${sel} · column ${x / TS + 1}, row ${y / TS + 1}`;
      q("st-slotrole").textContent = reserved ? "Reserved: doors, flag, goal and the like" : r ? `${r.pack.name}: ${r.role.label}` : otherLabel(sel) || "No role";
      const ps = q("st-slotpack");
      ps.replaceChildren(new Option("no pack", ""), ...layout.packs.map((p) => new Option(p.name, p.id)));
      ps.value = r ? r.pack.id : ""; ps.disabled = reserved;
      const rs = q("st-slotpiece");
      rs.replaceChildren(new Option("—", ""), ...[...layout.roles.values()].map((ro) => new Option(`${ro.label} (${ro.open || "closed"})`, ro.name)));
      rs.value = r ? r.role.name : ""; rs.disabled = reserved || !r;
      const sg = suggestions && suggestions.get(sel);
      q("st-slotsg").textContent = sg ? `Edges look ${sg.open ? "open to " + sg.open.split("").join(" ") : "closed all round"}${sg.sure < 0.25 ? " (a close call)" : ""}${r && r.role.open !== sg.open ? `; its role says ${r.role.open || "closed"}` : ""}.` : "";
      if (r && r.pack.id !== packId) { packId = r.pack.id; showPacks(); drawPreview(); }
    }
    function showPacks() {
      q("st-layoutname").textContent = layout.name;
      const ul = q("st-packs"); ul.replaceChildren();
      for (const p of layout.packs) {
        const li = document.createElement("li");
        if (p.id === packId) li.className = "on";
        const dot = document.createElement("span"); dot.className = "dot"; dot.style.background = packColour(p);
        const b = document.createElement("button"); b.type = "button"; b.className = "ghost small open"; b.textContent = `${p.name} · ${p.tiles.size}/${layout.roles.size}`;
        b.addEventListener("click", () => { packId = p.id; const t = p.tiles.get(layout.byOpen.get("")) || p.tiles.values().next().value; if (t) sel = t; showPacks(); draw(); drawSeam(); drawPreview(); showSlot(); });
        const ren = document.createElement("button"); ren.type = "button"; ren.className = "ghost small"; ren.textContent = "Rename";
        ren.addEventListener("click", async () => { const n = await askText("Name of this pack", { value: p.name, ok: "Rename" }); if (n && n.trim()) { snapshot(); p.name = n.trim().slice(0, 40); layoutChanged(); } });
        li.append(dot, b, ren); ul.append(li);
      }
      if (!layout.packs.length) { const li = document.createElement("li"); li.className = "dim"; li.textContent = "No packs: select a slot and make one."; ul.append(li); }
      const w = layout.warnings || [];
      q("st-layoutwarn").hidden = !w.length; q("st-layoutwarn").textContent = w.join(" · ");
    }
    function layoutChanged() { setDirty(true); showPacks(); draw(); drawPreview(); showSlot(); }
    q("st-slotpack").addEventListener("change", (e) => {
      snapshot();
      const p = layout.packs.find((x) => x.id === e.target.value), r = TileLayout.roleOfTile(layout, sel);
      if (r) r.pack.tiles.delete(r.role.name);
      if (p) {
        const sg = suggestions && suggestions.get(sel), want = (sg && layout.byOpen.get(sg.open)) || (r && r.role.name);
        const free = [...layout.roles.keys()].filter((n) => !p.tiles.has(n)), role = want && free.includes(want) ? want : free[0];
        if (!role) say(`${p.name} already has all ${layout.roles.size} pieces; change one of them first.`, false); else { p.tiles.set(role, sel); packId = p.id; }
      }
      layoutChanged();
    });
    q("st-slotpiece").addEventListener("change", (e) => {
      const r = TileLayout.roleOfTile(layout, sel), role = e.target.value;
      if (!r || !role) return;
      snapshot();
      const prev = r.pack.tiles.get(role);
      r.pack.tiles.delete(r.role.name);
      if (prev && prev !== sel) r.pack.tiles.set(r.role.name, prev);
      r.pack.tiles.set(role, sel);
      layoutChanged();
    });
    q("st-packnew").addEventListener("click", async () => {
      if (!layout.template) { say("This layout has no pack template; set pieces slot by slot.", false); return; }
      const [x, y] = slotXY(sel), n = await askText("Name of the new pack", { value: `Pack ${layout.packs.length + 1}`, ok: "Make pack" });
      if (!n || !n.trim()) return;
      let id = n.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "pack";
      while (layout.packs.some((p) => p.id === id)) id += "-2";
      const p = TileLayout.packAt(layout, id, n.trim().slice(0, 40), x / TS, y / TS);
      for (const [role, t] of [...p.tiles]) if (TileLayout.roleOfTile(layout, t) || (t >= layout.reserved[0] && t <= layout.reserved[1])) p.tiles.delete(role);
      if (!p.tiles.size) { say("Every slot there already belongs to a pack (or is reserved).", false); return; }
      snapshot(); layout.packs.push(p); packId = id;
      say(`${p.name}: ${p.tiles.size} pieces from tile ${sel}, laid out like the standard pack. Check edges to see if they fit.`, true);
      layoutChanged();
    });
    q("st-packdel").addEventListener("click", async () => {
      const p = currentPack(); if (!p) return;
      if (!(await ask(`Remove the pack "${p.name}"? Its tiles stay; they just lose their roles.`, { ok: "Remove", danger: true }))) return;
      snapshot(); layout.packs = layout.packs.filter((x) => x !== p); packId = layout.packs[0] ? layout.packs[0].id : null;
      layoutChanged();
    });
    q("st-check").addEventListener("click", () => {
      suggestions = TileOps.suggestOpen(img);
      let off = 0, n = 0;
      for (const p of layout.packs) for (const [role, t] of p.tiles) { n++; const sg = suggestions.get(t); if (!sg || sg.open !== layout.roles.get(role).open) off++; }
      say(n ? `${n - off} of ${n} pack pieces look like their role.${off ? " Red dots mark the others." : ""}` : "Edges read; select a slot to see them.", !off);
      if (mode !== "sheet") setMode("sheet"); else draw();
      showSlot();
    });
    q("st-fromedges").addEventListener("click", async () => {
      const p = currentPack(); if (!p) { say("Pick a pack first.", false); return; }
      suggestions = suggestions || TileOps.suggestOpen(img);
      const slots = new Set(p.tiles.values());
      if (p.origin && layout.template) for (const t of TileLayout.packAt(layout, "x", "x", p.origin[0], p.origin[1]).tiles.values()) slots.add(t);
      const next = new Map(), clash = [];
      for (const t of [...slots].sort((a, b) => a - b)) {
        const sg = suggestions.get(t), role = sg && layout.byOpen.get(sg.open);
        if (!role) continue;
        if (next.has(role)) { clash.push(t); continue; }
        const o = TileLayout.roleOfTile(layout, t);
        if (o && o.pack !== p) continue;
        next.set(role, t);
      }
      if (!(await ask(`Set ${p.name} to ${next.size} pieces read from the art${clash.length ? ` (tiles ${clash.join(", ")} look like a piece already taken and are left out)` : ""}?`, { ok: "Set roles" }))) return;
      snapshot(); p.tiles = next; layoutChanged();
    });
    q("st-roles").addEventListener("change", draw);
    const cloneLayout = (l) => TileLayout.parse(TileLayout.toJson(l)).layout;
    function useLayout(l) { layout = l; packId = layout.packs[0] ? layout.packs[0].id : null; suggestions = null; showPacks(); drawAll(); }
    q("st-std").addEventListener("click", async () => {
      if (!(await ask("Use the standard layout's roles? Role changes made here are lost (the art stays).", { ok: "Use standard" }))) return;
      snapshot(); useLayout(cloneLayout(standard)); setDirty(true);
    });
    q("st-rolesfile").addEventListener("change", async (e) => {
      const f = e.target.files[0]; e.target.value = ""; if (!f) return;
      const { layout: l, errors } = TileLayout.parse(await f.text());
      if (!l) { say(`That roles file can't be used: ${errors.slice(0, 3).join("; ")}`, false); return; }
      snapshot(); useLayout(l); setDirty(true); say(`Roles from ${f.name}: ${l.packs.length} packs.`, true);
    });

    // ---- undo -----------------------------------------------------------------------------------------
    const state = () => ({ pixels: img.pixels.slice(), palette: img.palette.slice(), roles: TileLayout.toJson(layout) });
    function snapshot() { undoStack.push(state()); if (undoStack.length > 100) undoStack.shift(); redoStack.length = 0; }
    function restore(from, to) {
      dropFloating();
      const s = from.pop(); if (!s) return;
      to.push(state());
      img.pixels = s.pixels; img.palette = s.palette; layout = TileLayout.parse(s.roles).layout || layout;
      selection = null; suggestions = null; setDirty(true); showPacks(); drawAll();
    }
    q("st-undo").addEventListener("click", () => restore(undoStack, redoStack));
    q("st-redo").addEventListener("click", () => restore(redoStack, undoStack));

    // ---- tools ------------------------------------------------------------------------------------------
    const toolBox = q("st-tools");
    for (const [id, label, key] of TOOLS) {
      const b = document.createElement("button");
      b.type = "button"; b.className = "ghost small"; b.dataset.tool = id; b.title = `${label} (${key})`;
      b.textContent = label + " "; const k = document.createElement("kbd"); k.textContent = key; b.append(k);
      b.addEventListener("click", () => setTool(id));
      toolBox.append(b);
    }
    function setTool(t) {
      if (t !== "select") dropFloating();
      tool = t;
      for (const b of toolBox.children) { const on = b.dataset.tool === t; b.classList.toggle("on", on); b.setAttribute("aria-pressed", String(on)); }
      q("st-view").dataset.tool = t;
    }
    const size = () => Math.max(1, Math.min(8, q("st-size").value | 0));
    const dl = q("st-help").querySelector("dl");
    for (const [k, d] of HELP) { const dt = document.createElement("dt"); dt.textContent = k; const dd = document.createElement("dd"); dd.textContent = d; dl.append(dt, dd); }

    function setMode(m) {
      dropFloating();
      mode = m; q("st-view").parentElement.scrollTo(0, 0);
      q("st-view").classList.toggle("tilemode", m === "tile");
      q("st-viewbtn").textContent = m === "sheet" ? "Tile view" : "Sheet view";
      draw();
    }
    q("st-viewbtn").addEventListener("click", () => setMode(mode === "sheet" ? "tile" : "sheet"));
    const zoomBy = (d) => {
      if (mode === "sheet") zoom = Math.max(1, Math.min(10, zoom + d)); else tileZoom = Math.max(6, Math.min(40, tileZoom + d * 4));
      draw();
    };
    q("st-zin").addEventListener("click", () => zoomBy(1));
    q("st-zout").addEventListener("click", () => zoomBy(-1));
    for (const c of ["st-pgrid", "st-tgrid", "st-onion", "st-clear0"]) q(c).addEventListener("change", draw);
    q("st-refmode").addEventListener("change", draw);
    q("st-refop").addEventListener("input", draw);
    q("st-full").addEventListener("click", () => {
      const on = root.classList.toggle("full");
      q("st-full").textContent = on ? "Exit full screen" : "Full screen";
      document.body.classList.toggle("studio-full", on);
    });

    // clipping: a stroke stays inside the selection when there is one, else inside the tile in tile view
    function clipRect() {
      if (selection && !floating) return selection;
      if (mode === "tile") { const [x, y] = slotXY(sel); return { x, y, w: TS, h: TS }; }
      return { x: 0, y: 0, w: SHEET_W, h: SHEET_H };
    }
    const inRect = (r, x, y) => x >= r.x && y >= r.y && x < r.x + r.w && y < r.y + r.h;
    // all pixels a stroke touches, with mirroring inside each tile
    function addPoint(set, x, y, col, clipR) {
      const s = size(), o = Math.floor((s - 1) / 2);
      for (let dy = 0; dy < s; dy++) for (let dx = 0; dx < s; dx++) {
        const px = x - o + dx, py = y - o + dy;
        const pts = [[px, py]];
        if (q("st-mirx").checked || q("st-miry").checked) {
          const tx = Math.floor(px / TS) * TS, ty = Math.floor(py / TS) * TS, mx = tx + TS - 1 - (px - tx), my = ty + TS - 1 - (py - ty);
          if (q("st-mirx").checked) pts.push([mx, py]);
          if (q("st-miry").checked) pts.push([px, my]);
          if (q("st-mirx").checked && q("st-miry").checked) pts.push([mx, my]);
        }
        for (const [a, b] of pts) if (inRect(clipR, a, b)) set.set(b * SHEET_W + a, col);
      }
    }
    function linePts(x0, y0, x1, y1, f) {
      const dx = Math.abs(x1 - x0), dy = -Math.abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
      let err = dx + dy;
      for (;;) {
        f(x0, y0);
        if (x0 === x1 && y0 === y1) break;
        const e2 = 2 * err;
        if (e2 >= dy) { err += dy; x0 += sx; }
        if (e2 <= dx) { err += dx; y0 += sy; }
      }
    }
    function shapePts(kind, x0, y0, x1, y1, filled, f) {
      const ax = Math.min(x0, x1), bx = Math.max(x0, x1), ay = Math.min(y0, y1), by = Math.max(y0, y1);
      if (kind === "rect") {
        for (let y = ay; y <= by; y++) for (let x = ax; x <= bx; x++) if (filled || x === ax || x === bx || y === ay || y === by) f(x, y, filled);
        return;
      }
      const cx = (ax + bx) / 2, cy = (ay + by) / 2, rx = (bx - ax) / 2 + 0.5, ry = (by - ay) / 2 + 0.5;
      const inside = (x, y) => ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1;
      for (let y = ay; y <= by; y++) for (let x = ax; x <= bx; x++) {
        if (!inside(x, y)) continue;
        if (filled || !inside(x - 1, y) || !inside(x + 1, y) || !inside(x, y - 1) || !inside(x, y + 1)) f(x, y, filled);
      }
    }
    function constrain(a, b) { // Shift: 45° lines, square shapes
      const dx = b[0] - a[0], dy = b[1] - a[1];
      if (tool === "line") {
        const adx = Math.abs(dx), ady = Math.abs(dy);
        if (adx > ady * 2) return [b[0], a[1]];
        if (ady > adx * 2) return [a[0], b[1]];
        const m = Math.max(adx, ady); return [a[0] + Math.sign(dx) * m, a[1] + Math.sign(dy) * m];
      }
      const m = Math.max(Math.abs(dx), Math.abs(dy)); return [a[0] + Math.sign(dx || 1) * m, a[1] + Math.sign(dy || 1) * m];
    }
    function strokeFor(start, end, col, shift) {
      const set = new Map(), clipR = clipRect(), e = shift ? constrain(start, end) : end;
      if (tool === "line") linePts(start[0], start[1], e[0], e[1], (x, y) => addPoint(set, x, y, col, clipR));
      else {
        const filled = q("st-filled").checked;
        shapePts(tool, start[0], start[1], e[0], e[1], filled, (x, y, f) => { if (f) { if (inRect(clipR, x, y)) set.set(y * SHEET_W + x, col); } else addPoint(set, x, y, col, clipR); });
      }
      return set;
    }
    function commit(set) {
      if (!set || !set.size) return;
      let changed = false;
      for (const [i, c] of set) if (img.pixels[i] !== c) { changed = true; break; }
      if (!changed) return;
      snapshot();
      for (const [i, c] of set) img.pixels[i] = c;
      edited();
    }
    function edited() { suggestions = null; setDirty(true); drawArt(); drawPalette(); }
    function floodFill(x0, y0, col) {
      const r = clipRect(), from = img.pixels[y0 * SHEET_W + x0], set = new Map();
      if (from === col) return set;
      const seen = new Uint8Array(SHEET_W * SHEET_H), stack = [[x0, y0]];
      while (stack.length) {
        const [x, y] = stack.pop(), i = y * SHEET_W + x;
        if (!inRect(r, x, y) || seen[i] || img.pixels[i] !== from) continue;
        seen[i] = 1; set.set(i, col);
        stack.push([x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]);
      }
      return set;
    }
    function replaceColour(from, col) {
      const r = clipRect(), set = new Map();
      for (let y = r.y; y < r.y + r.h; y++) for (let x = r.x; x < r.x + r.w; x++) if (img.pixels[y * SHEET_W + x] === from) set.set(y * SHEET_W + x, col);
      return set;
    }

    // ---- selection ----------------------------------------------------------------------------------------
    function lift() {
      if (floating || !selection) return;
      snapshot();
      const { x, y, w, h } = selection, px = new Uint8Array(w * h);
      for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) { const k = (y + j) * SHEET_W + x + i; px[j * w + i] = img.pixels[k]; img.pixels[k] = 0; }
      floating = { x, y, w, h, px }; selection = null;
      renderSheet();
    }
    function dropFloating() {
      if (!floating) return;
      const f = floating, see = q("st-clear0").checked;
      floating = null;
      for (let j = 0; j < f.h; j++) for (let i = 0; i < f.w; i++) {
        const x = f.x + i, y = f.y + j, c = f.px[j * f.w + i];
        if (x < 0 || y < 0 || x >= SHEET_W || y >= SHEET_H || (c === 0 && see)) continue;
        img.pixels[y * SHEET_W + x] = c;
      }
      selection = { x: Math.max(0, f.x), y: Math.max(0, f.y), w: Math.min(SHEET_W, f.x + f.w) - Math.max(0, f.x), h: Math.min(SHEET_H, f.y + f.h) - Math.max(0, f.y) };
      if (selection.w <= 0 || selection.h <= 0) selection = null;
      edited();
    }
    function cancelSelection() {
      if (floating) { floating = null; restore(undoStack, []); } // put back what was lifted or pasted
      selection = null; draw();
    }
    function region() { // what copy, flip and rotate work on: the selection, else the tile
      if (floating) return floating;
      if (selection) return selection;
      const [x, y] = slotXY(sel); return { x, y, w: TS, h: TS, tile: true };
    }
    function grab(r) { const px = new Uint8Array(r.w * r.h); for (let j = 0; j < r.h; j++) for (let i = 0; i < r.w; i++) px[j * r.w + i] = img.pixels[(r.y + j) * SHEET_W + r.x + i]; return px; }
    function copy() { const r = region(); clip = { w: r.w, h: r.h, px: r.px ? r.px.slice() : grab(r) }; say(`Copied ${r.w} × ${r.h}.`, true); }
    function clearRegion() {
      if (floating) { floating = null; edited(); return; }
      const r = region(); snapshot();
      for (let j = 0; j < r.h; j++) for (let i = 0; i < r.w; i++) img.pixels[(r.y + j) * SHEET_W + r.x + i] = 0;
      edited();
    }
    function paste() {
      if (!clip) { say("Copy something first.", false); return; }
      dropFloating();
      snapshot();
      const at = selection || (() => { const [x, y] = slotXY(sel); return { x, y }; })();
      floating = { x: at.x, y: at.y, w: clip.w, h: clip.h, px: clip.px.slice() }; selection = null;
      setTool("select"); setDirty(true); draw();
      say("Pasted: drag it into place, Enter to drop it.", true);
    }
    function transform(op) {
      const r = region();
      if (r.tile) return turnTile(op);
      if (!floating) lift();
      const f = floating, w = f.w, h = f.h, out = new Uint8Array(w * h);
      if (op === "rotateCW") {
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) out[x * h + (h - 1 - y)] = f.px[y * w + x];
        const cx = f.x + w / 2, cy = f.y + h / 2;
        f.w = h; f.h = w; f.x = Math.round(cx - f.w / 2); f.y = Math.round(cy - f.h / 2);
      } else for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) out[(op === "flipV" ? h - 1 - y : y) * w + (op === "flipH" ? w - 1 - x : x)] = f.px[y * w + x];
      f.px = out; setDirty(true); draw();
    }
    function turnTile(op) {
      snapshot();
      TileOps.setTile(img, sel, TileOps[op](TileOps.getTile(img, sel)));
      const r = TileLayout.roleOfTile(layout, sel);
      if (q("st-turnrole").checked && r) {
        const to = layout.byOpen.get(TileOps.turnOpen(r.role.open, op));
        if (to && to !== r.role.name) {
          const other = r.pack.tiles.get(to);
          r.pack.tiles.set(to, sel);
          if (other) r.pack.tiles.set(r.role.name, other); else r.pack.tiles.delete(r.role.name);
          say(`Tile ${sel} is now ${r.pack.name}'s ${layout.roles.get(to).label.toLowerCase()}${other ? `; tile ${other} took its old role` : ""}.`, true);
          showPacks();
        }
      }
      edited(); showSlot();
    }

    // ---- pointer on the view ----------------------------------------------------------------------------
    const view = q("st-view"), scroller = q("st-scroll");
    function at(e) {
      const v = viewRect(), r = view.getBoundingClientRect();
      return [v.x + Math.floor(((e.clientX - r.left) * view.width) / r.width / v.z), v.y + Math.floor(((e.clientY - r.top) * view.height) / r.height / v.z)];
    }
    const onSheet = ([x, y]) => x >= 0 && y >= 0 && x < SHEET_W && y < SHEET_H;
    view.addEventListener("contextmenu", (e) => e.preventDefault());
    view.addEventListener("pointerdown", (e) => {
      const p = at(e);
      if (e.button === 1 || spaceDown) { drag = { pan: true, sx: e.clientX, sy: e.clientY, l: scroller.scrollLeft, t: scroller.scrollTop }; view.setPointerCapture(e.pointerId); return; }
      if (!onSheet(p)) return;
      const col = e.button === 2 ? c2 : c1;
      if (tool === "pick" || e.altKey) {
        const v = img.pixels[p[1] * SHEET_W + p[0]];
        if (e.button === 2) c2 = v; else c1 = v;
        drawPalette(); return;
      }
      // in sheet view a click selects the slot too (not while selecting)
      if (tool !== "select") { const t = tileAt(...p); if (t !== sel && mode === "sheet") { sel = t; drawSeam(); showSlot(); } }
      view.setPointerCapture(e.pointerId);
      if (tool === "fill") { commit(floodFill(p[0], p[1], col)); return; }
      if (tool === "replace") { const from = img.pixels[p[1] * SHEET_W + p[0]]; const s = replaceColour(from, col); commit(s); say(`Replaced colour ${from} with ${col}: ${s.size} px.`, true); return; }
      if (tool === "select") {
        const box = floating || selection;
        if (box && inRect(box, ...p)) { lift(); drag = { move: true, from: p, ox: floating.x, oy: floating.y }; return; }
        dropFloating();
        drag = { select: true, from: p }; selection = { x: p[0], y: p[1], w: 1, h: 1 }; draw(); return;
      }
      if (tool === "pencil" || tool === "eraser") {
        const c = tool === "eraser" ? 0 : col, set = new Map();
        addPoint(set, p[0], p[1], c, clipRect());
        drag = { paint: true, last: p, col: c, set }; preview = set; draw(); return;
      }
      drag = { shape: true, from: p, col }; preview = strokeFor(p, p, col, e.shiftKey); draw();
    });
    view.addEventListener("pointermove", (e) => {
      const p = at(e);
      if (onSheet(p)) { const t = tileAt(...p), ro = TileLayout.roleOfTile(layout, t);
        q("st-status").textContent = `x ${p[0]}, y ${p[1]} · tile ${t} (${p[0] % TS}, ${p[1] % TS}) · colour ${img.pixels[p[1] * SHEET_W + p[0]]}${ro ? ` · ${ro.pack.name}: ${ro.role.name}` : ""}`; }
      if (!drag) return;
      if (drag.pan) { scroller.scrollLeft = drag.l - (e.clientX - drag.sx); scroller.scrollTop = drag.t - (e.clientY - drag.sy); return; }
      if (drag.paint) { linePts(drag.last[0], drag.last[1], p[0], p[1], (x, y) => addPoint(drag.set, x, y, drag.col, clipRect())); drag.last = p; draw(); return; }
      if (drag.shape) { preview = strokeFor(drag.from, p, drag.col, e.shiftKey); draw(); return; }
      if (drag.select) {
        const x0 = Math.max(0, Math.min(drag.from[0], p[0])), y0 = Math.max(0, Math.min(drag.from[1], p[1]));
        const x1 = Math.min(SHEET_W - 1, Math.max(drag.from[0], p[0])), y1 = Math.min(SHEET_H - 1, Math.max(drag.from[1], p[1]));
        selection = { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 }; draw(); return;
      }
      if (drag.move) { floating.x = drag.ox + p[0] - drag.from[0]; floating.y = drag.oy + p[1] - drag.from[1]; draw(); }
    });
    const end = () => {
      if (!drag) return;
      if (drag.paint || drag.shape) { const s = preview; preview = null; commit(s); if (!s || !s.size) draw(); }
      if (drag.select && selection && selection.w === 1 && selection.h === 1) { selection = null; draw(); }
      if (drag.move) setDirty(true);
      drag = null;
    };
    view.addEventListener("pointerup", end);
    view.addEventListener("pointercancel", end);
    view.addEventListener("wheel", (e) => { if (!e.ctrlKey && !e.metaKey) return; e.preventDefault(); zoomBy(e.deltaY < 0 ? 1 : -1); }, { passive: false });
    view.addEventListener("dblclick", (e) => { if (mode === "sheet" && tool === "pick") { sel = tileAt(...at(e)); setMode("tile"); } });

    // ---- palette ----------------------------------------------------------------------------------------
    const pal = q("st-pal");
    const palAt = (e) => { const r = pal.getBoundingClientRect(); return Math.max(0, Math.min(255, Math.floor(((e.clientY - r.top) * pal.height) / r.height / 16) * 32 + Math.floor(((e.clientX - r.left) * pal.width) / r.width / 8))); };
    pal.addEventListener("contextmenu", (e) => { e.preventDefault(); c2 = palAt(e); drawPalette(); });
    pal.addEventListener("click", (e) => { c1 = palAt(e); drawPalette(); });
    pal.addEventListener("dblclick", (e) => { c1 = palAt(e); drawPalette(); q("st-coledit").click(); });
    q("st-c1").addEventListener("click", () => q("st-coledit").click());
    q("st-c2").addEventListener("click", () => { [c1, c2] = [c2, c1]; drawPalette(); });
    q("st-coledit").addEventListener("change", (e) => {
      if (c1 === 0) { say("Colour 0 is empty space; it stays black.", false); drawPalette(); return; }
      const v = e.target.value; snapshot();
      img.palette[c1 * 3] = parseInt(v.slice(1, 3), 16); img.palette[c1 * 3 + 1] = parseInt(v.slice(3, 5), 16); img.palette[c1 * 3 + 2] = parseInt(v.slice(5, 7), 16);
      setDirty(true); drawAll();
    });
    q("st-uses").addEventListener("click", () => {
      flashUntil = Date.now() + 1800;
      const tick = () => { draw(); if (Date.now() < flashUntil) requestAnimationFrame(tick); else draw(); };
      tick();
    });
    q("st-palfile").addEventListener("change", async (e) => {
      const f = e.target.files[0]; e.target.value = ""; if (!f) return;
      try {
        const bytes = new Uint8Array(await f.arrayBuffer());
        const other = /\.lvl$/i.test(f.name) ? LvlFile.tileset(bytes) : Bmp.read(bytes);
        if (!other) throw new Error(`${f.name} has no tileset of its own.`);
        snapshot();
        const map = new Uint8Array(256);
        for (let i = 1; i < 256; i++) map[i] = Bmp.nearest(other.palette, img.palette[i * 3], img.palette[i * 3 + 1], img.palette[i * 3 + 2], 1);
        for (let i = 0; i < img.pixels.length; i++) img.pixels[i] = map[img.pixels[i]];
        img.palette = other.palette.slice(); img.palette[0] = img.palette[1] = img.palette[2] = 0;
        setDirty(true); drawAll(); say(`Palette from ${f.name}; each colour moved to its nearest there.`, true);
      } catch (err) { say(err.message, false); }
    });

    // ---- reference image ---------------------------------------------------------------------------------
    q("st-reffile").addEventListener("change", async (e) => {
      const f = e.target.files[0]; e.target.value = ""; if (!f) return;
      try {
        const b = await createImageBitmap(f);
        refCanvas = document.createElement("canvas"); refCanvas.width = SHEET_W; refCanvas.height = SHEET_H;
        const ctx = refCanvas.getContext("2d"); ctx.imageSmoothingEnabled = false;
        const k = Math.min(SHEET_W / b.width, SHEET_H / b.height, 1) || 1;
        ctx.drawImage(b, 0, 0, b.width * k, b.height * k);
        if (q("st-refmode").value === "off") q("st-refmode").value = "under";
        draw(); say(`Reference: ${f.name}${k < 1 ? " (shrunk to fit the sheet)" : ""}.`, true);
      } catch { say("That image couldn't be read.", false); }
    });

    // ---- open -------------------------------------------------------------------------------------------
    function setImage(next, from, keepDirty) {
      dropFloating();
      img = next; src = from; selection = null; floating = null; suggestions = null;
      original = img.pixels.slice(); originalPal = img.palette.slice(); paint(origCanvas, original, originalPal);
      undoStack.length = 0; redoStack.length = 0; setDirty(!!keepDirty);
      q("st-name").textContent = from.label || from.name;
      drawAll();
    }
    async function confirmDiscard() { return !dirty || ask("Discard the unsaved changes to this tileset?", { ok: "Discard", danger: true }); }
    function fromLvl(name, bytes, extra) {
      const s = LvlFile.split(bytes);
      if (!s.bmp) throw new Error(`${name} uses the game's default tileset (it has none of its own). Open a .bmp, or start a new one, then put it into the map.`);
      return [Bmp.fitSheet(Bmp.read(s.bmp)), Object.assign({ name: name.replace(/\.lvl$/i, ""), elvl: s.elvl, tiles: s.tiles, from: "lvl" }, extra)];
    }
    async function fetchBytes(url) { const r = await fetch(url, { credentials: "same-origin" }).catch(() => null); if (!r || !r.ok) return null; return { bytes: new Uint8Array(await r.arrayBuffer()), r }; }
    // ---- default tilesets: tile art's generated sets (tileart.js TileArt.defaults / TileArt.sheet), each a
    // whole sheet in the standard layout. [value, label]: the mixed themes first, then one sheet per style.
    function defaults() {
      if (!window.TileArt || !TileArt.defaults || !TileArt.data) return [];
      return TileArt.defaults(TileArt.data).map((x) => [x.id, x.kind === "theme" ? `${x.name} (mixed)` : x.name]);
    }
    async function defaultSheet(id) {
      const canvas = await TileArt.sheet(id);
      const rgba = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
      return [Bmp.fitSheet(Bmp.quantize(rgba, canvas.width, canvas.height)), (defaults().find(([v]) => v === id) || [id, id])[1]];
    }
    // the newest open wins: an earlier one still loading (the arena's map on the first visit) doesn't
    // replace what the pilot picked after it
    let openSeq = 0;
    async function openChoice(v) {
      const [kind, id] = [v.slice(0, v.indexOf(":")), v.slice(v.indexOf(":") + 1)];
      const my = ++openSeq, stale = () => my !== openSeq;
      try {
        if (kind === "arena") {
          const f = await fetchBytes(API + "/map?arena=" + encodeURIComponent(arena));
          if (!f) { say(`Couldn't read ${arena}'s map.`, false); return; }
          if (stale()) return;
          const [im, from] = fromLvl(arena + ".lvl", f.bytes, { label: `${arena}'s map`, arena: true });
          libId = null; setImage(im, from); say(`Opened ${arena}'s current tileset.`, true);
        } else if (kind === "map") {
          const f = await fetchBytes(API + "/library/file?id=" + encodeURIComponent(id));
          if (!f) { say("Couldn't open that map from Your maps.", false); return; }
          if (stale()) return;
          const name = decodeURIComponent(f.r.headers.get("X-Map-Name") || "map");
          const [im, from] = fromLvl(name, f.bytes, { label: `${name} (Your maps)`, mapId: +id });
          libId = null; setImage(im, from); say(`Opened ${name}'s tileset.`, true);
        } else if (kind === "shared") {
          const f = await fetchBytes(API + "/maps/file?name=" + encodeURIComponent(id));
          if (!f) { say(`Couldn't open ${id}.`, false); return; }
          if (stale()) return;
          const [im, from] = fromLvl(id, f.bytes); libId = null; setImage(im, from); say(`Opened ${id}.`, true);
        } else if (kind === "std") {
          const f = await fetchBytes("/arena/standard-tiles.bmp");
          if (!f) { say("Couldn't load the standard tiles.", false); return; }
          if (stale()) return;
          libId = null; setImage(Bmp.fitSheet(Bmp.read(f.bytes)), { name: "standard tiles", elvl: null, tiles: new Uint8Array(0), from: "file" });
          say("Opened the standard tiles.", true);
        } else if (kind === "gen") {
          const [im, name] = await defaultSheet(id); if (stale()) return;
          libId = null; setImage(im, { name, label: `${name} (default tileset)`, elvl: null, tiles: new Uint8Array(0), from: "file" });
          renderLib(); say(`Opened the default tileset ${name}. Save keeps a copy in Your tilesets.`, true);
        } else if (kind === "set") await openTileset(+id, stale);
      } catch (err) { say(err.message, false); }
    }
    q("st-open").addEventListener("change", async (e) => { const v = e.target.value; e.target.value = ""; if (v && (await confirmDiscard())) openChoice(v); });
    q("st-file").addEventListener("change", async (e) => {
      const f = e.target.files[0]; e.target.value = "";
      if (!f || !(await confirmDiscard())) return;
      try {
        if (/\.png$/i.test(f.name) || /^image\/(png|gif|webp)/.test(f.type)) {
          const b = await createImageBitmap(f), c = document.createElement("canvas"); c.width = b.width; c.height = b.height;
          const ctx = c.getContext("2d"); ctx.drawImage(b, 0, 0);
          libId = null;
          setImage(Bmp.fitSheet(Bmp.quantize(ctx.getImageData(0, 0, c.width, c.height).data, c.width, c.height)), { name: f.name.replace(/\.[^.]+$/, ""), elvl: null, tiles: new Uint8Array(0), from: "file" }, true);
          say(`${f.name}: reduced to 256 colours${c.width !== SHEET_W || c.height !== SHEET_H ? `, and ${c.width} × ${c.height} cropped or padded to 304 × 160` : ""}.`, true);
        } else if (/\.lvl$/i.test(f.name)) {
          const [im, from] = fromLvl(f.name, new Uint8Array(await f.arrayBuffer())); libId = null; setImage(im, from); say(`${f.name} opened.`, true);
        } else {
          libId = null;
          setImage(Bmp.fitSheet(Bmp.read(new Uint8Array(await f.arrayBuffer()))), { name: f.name.replace(/\.[^.]+$/, ""), elvl: null, tiles: new Uint8Array(0), from: "file" });
          say(`${f.name} opened.`, true);
        }
      } catch (err) { say(err.message, false); }
    });
    q("st-new").addEventListener("click", async () => {
      if (!(await confirmDiscard())) return;
      libId = null; setImage(blankImage(), { name: "new tileset", elvl: null, tiles: new Uint8Array(0), from: "new" });
    });

    // ---- Your tilesets ---------------------------------------------------------------------------------------
    async function api(path, body) {
      const r = await fetch(API + path, body === undefined ? { credentials: "same-origin" }
        : { method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/json" }, HDR), body: JSON.stringify(body) }).catch(() => null);
      if (!r) return { status: 0, body: { ok: false, message: "No connection." } };
      return { status: r.status, body: await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status })) };
    }
    let tilesets = [], maps = [];
    async function loadLists() {
      const [t, m, s] = await Promise.all([api("/tilesets"), api("/library"), api("/maps")]);
      signedIn = t.status !== 401;
      tilesets = (t.body && t.body.tilesets) || []; maps = (m.body && m.body.maps) || [];
      const open = q("st-open"); open.replaceChildren(new Option("Open…", ""));
      const group = (label, items) => { if (!items.length) return; const g = document.createElement("optgroup"); g.label = label; for (const [v, t2] of items) g.append(new Option(t2, v)); open.append(g); };
      if (arena) group("This arena", [["arena:", `${arena}'s current map`]]);
      // as the map editor's menu: Standard first, then the generated sets
      group("Defaults", [["std:", "Standard tiles"], ...defaults().map(([v, t2]) => ["gen:" + v, t2])]);
      group("Your tilesets", tilesets.map((x) => ["set:" + x.id, x.name]));
      group("Your maps", maps.map((x) => ["map:" + x.id, x.name]));
      group("Ready-made maps", ((s.body && s.body.maps) || []).map((n) => ["shared:" + n, n]));
      const target = q("st-target"); target.replaceChildren();
      if (arena) target.append(new Option(`${arena}'s current map → a new draft in Your maps`, "arena:"));
      for (const x of maps) target.append(new Option(`${x.name} (Your maps)`, "map:" + x.id));
      if (src.mapId) target.value = "map:" + src.mapId;
      q("st-apply").disabled = !signedIn || !target.options.length;
      renderLib();
    }
    function renderLib() {
      const box = q("st-defaults"); box.replaceChildren();
      q("st-defhint").hidden = false;
      { const b = document.createElement("button"); b.type = "button"; b.className = "ghost small"; b.textContent = "Standard"; b.title = "Open the standard tiles";
        if (src.name === "standard tiles") b.classList.add("on");
        b.addEventListener("click", async () => { if (await confirmDiscard()) openChoice("std:"); }); box.append(b); }
      for (const [v, t2] of defaults()) {
        const b = document.createElement("button"); b.type = "button"; b.className = "ghost small"; b.textContent = t2;
        b.title = `Open the default tileset ${t2}`; if (src.label === `${t2} (default tileset)`) b.classList.add("on");
        b.addEventListener("click", async () => { if (await confirmDiscard()) openChoice("gen:" + v); });
        box.append(b);
      }
      const ul = q("st-lib"); ul.replaceChildren();
      q("st-libhint").textContent = signedIn === false ? "Sign in to keep tilesets in your account." : `Your tilesets: ${tilesets.length}${tilesets.length ? "" : " (Save keeps this one)"}.`;
      for (const t of tilesets) {
        const li = document.createElement("li"); if (t.id === libId) li.className = "on";
        const open = document.createElement("button"); open.type = "button"; open.className = "ghost small open"; open.textContent = t.name; open.title = `Open ${t.name}${t.hasRoles ? " (with its roles)" : ""}`;
        open.addEventListener("click", async () => { if (await confirmDiscard()) openTileset(t.id); });
        const mk = (label, fn, cls) => { const b = document.createElement("button"); b.type = "button"; b.className = "ghost small " + (cls || ""); b.textContent = label; b.setAttribute("aria-label", `${label} ${t.name}`); b.addEventListener("click", fn); return b; };
        li.append(open,
          mk("Rename", async () => { const n = await askText("New name", { value: t.name, ok: "Rename" }); if (!n || !n.trim()) return; const r = await api("/tilesets/rename", { id: t.id, name: n }); say(r.body.message, r.body.ok); loadLists(); }),
          mk("Copy", async () => { const r = await api("/tilesets/duplicate", { id: t.id }); say(r.body.message, r.body.ok); loadLists(); }),
          mk("Delete", async () => {
            if (!(await ask(`Delete ${t.name} from your tilesets? Maps it was put into keep their copy.`, { ok: "Delete", danger: true }))) return;
            const r = await api("/tilesets/remove", { id: t.id }); say(r.body.message, r.body.ok); if (r.body.ok && libId === t.id) libId = null; loadLists();
          }, "danger"));
        ul.append(li);
      }
    }
    async function openTileset(id, stale) {
      if (!stale) { const my = ++openSeq; stale = () => my !== openSeq; }
      const f = await fetchBytes(API + "/tilesets/file?id=" + id);
      if (!f) { say("Couldn't open that tileset.", false); return; }
      const name = decodeURIComponent(f.r.headers.get("X-Tileset-Name") || "tileset");
      const im = Bmp.fitSheet(Bmp.read(f.bytes));
      const t = tilesets.find((x) => x.id === id);
      let roles = null;
      if (!t || t.hasRoles) { const r = await fetch(API + "/tilesets/roles?id=" + id, { credentials: "same-origin" }).catch(() => null); if (r && r.ok) roles = TileLayout.parse(await r.text()).layout; }
      if (stale()) return;
      libId = id;
      if (roles) { layout = roles; packId = layout.packs[0] ? layout.packs[0].id : null; showPacks(); }
      setImage(im, { name, label: `${name} (Your tilesets)`, elvl: null, tiles: new Uint8Array(0), from: "set" });
      renderLib(); say(`Opened ${name}${roles ? " with its roles" : ""}.`, true);
    }
    const b64 = (bytes) => { let s = ""; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); return btoa(s); };
    async function saveTileset(asNew) {
      dropFloating();
      let name = src.name;
      if (asNew || !libId) { const n = await askText("Name for this tileset", { value: name, ok: "Save" }); if (!n || !n.trim()) return; name = n.trim(); }
      const id = asNew ? null : libId;
      const roles = TileLayout.toJson(Object.assign({}, layout, { name: `${name} roles` }));
      const r = await fetch(`${API}/tilesets/save?name=${encodeURIComponent(name)}${id ? "&id=" + id : ""}`, {
        method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/json" }, HDR),
        body: JSON.stringify({ bmp: b64(Bmp.write(img)), roles }),
      }).catch(() => null);
      const j = r ? await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status })) : { ok: false, message: "No connection." };
      if (r && r.status === 401) j.message = "Sign in to keep tilesets in your account.";
      say(j.message, j.ok);
      if (j.ok) { libId = j.id; src.name = name; src.label = `${name} (Your tilesets)`; q("st-name").textContent = src.label; setDirty(false); loadLists(); }
    }
    q("st-save").addEventListener("click", () => saveTileset(false));
    q("st-saveas").addEventListener("click", () => saveTileset(true));

    // ---- put into a map (a draft in Your maps) ---------------------------------------------------------------
    q("st-apply").addEventListener("click", async () => {
      dropFloating();
      const v = q("st-target").value; if (!v) return;
      const bmp = Bmp.write(img);
      let bytes, name, id = null;
      if (v === "arena:") {
        const f = await fetchBytes(API + "/map?arena=" + encodeURIComponent(arena));
        if (!f) { say(`Couldn't read ${arena}'s map.`, false); return; }
        const s = LvlFile.split(f.bytes);
        bytes = LvlFile.join(bmp, s.elvl, s.tiles);
        name = `${arena.replace(/^#/, "")} ${src.name}`.slice(0, 40);
        if (!(await ask(`Make a draft "${name}" in Your maps: ${arena}'s current walls with this tileset? ${arena} itself doesn't change until you Apply the draft on the Map tab.`, { ok: "Make draft" }))) return;
      } else {
        id = +v.slice(4);
        const m = maps.find((x) => x.id === id);
        const f = await fetchBytes(API + "/library/file?id=" + id);
        if (!f || !m) { say("Couldn't open that map from Your maps.", false); return; }
        const s = LvlFile.split(f.bytes);
        bytes = LvlFile.join(bmp, s.elvl, s.tiles); name = m.name;
        if (!(await ask(`Put this tileset into ${m.name} in Your maps? Its walls stay; its old tileset is replaced. Arenas using ${m.name} keep their copy until you Apply or Publish it.`, { ok: "Put it in" }))) return;
      }
      const r = await fetch(`${API}/library/save?name=${encodeURIComponent(name)}${id ? "&id=" + id : ""}`, {
        method: "POST", credentials: "same-origin", headers: Object.assign({ "Content-Type": "application/octet-stream" }, HDR), body: bytes }).catch(() => null);
      const j = r ? await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status })) : { ok: false, message: "No connection." };
      say(j.ok ? `${j.message} It's a draft: Apply it on the Map tab, or open it in the map editor and Publish.` : j.message, j.ok);
      if (j.ok) { loadLists(); if (opts.onMapSaved) opts.onMapSaved(j.id); }
    });

    // ---- downloads ----------------------------------------------------------------------------------------------
    function download(bytes, name, type) {
      const a = document.createElement("a");
      a.href = URL.createObjectURL(new Blob([bytes], { type })); a.download = name;
      document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    }
    q("st-dlbmp").addEventListener("click", () => { dropFloating(); download(Bmp.write(img), src.name + ".bmp", "image/bmp"); });
    q("st-dllvl").addEventListener("click", () => {
      dropFloating();
      download(LvlFile.join(Bmp.write(img), src.elvl, src.tiles), src.name + ".lvl", "application/octet-stream");
      say(src.from === "lvl" ? "The map with this tileset; its walls and eLVL data kept." : "A map with this tileset and no walls yet: draw them in the map editor.", true);
    });
    q("st-dlroles").addEventListener("click", () => download(new TextEncoder().encode(TileLayout.toJson(Object.assign({}, layout, { name: `${src.name} roles` }))), src.name + ".roles.json", "application/json"));

    // ---- preview cells ----------------------------------------------------------------------------------------
    function setShape(name) {
      cells.fill(0);
      if (name === "maze") {
        let seed = 7; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
        for (let y = 0; y < PH; y += 2) for (let x = 0; x < PW; x += 2) { cells[y * PW + x] = 1; if (rnd() < 0.5) { if (x + 1 < PW) cells[y * PW + x + 1] = 1; } else if (y + 1 < PH) cells[(y + 1) * PW + x] = 1; }
      } else if (SHAPES[name]) SHAPES[name].forEach((row, y) => [...row].forEach((ch, x) => { if (ch === "#" && x < PW && y < PH) cells[y * PW + x] = 1; }));
      drawPreview();
    }
    q("st-shape").addEventListener("change", (e) => setShape(e.target.value));
    let cellPaint = null;
    const prev = q("st-prev"), cellAt = (e) => { const r = prev.getBoundingClientRect(), k = prev.width / PW; return [Math.floor(((e.clientX - r.left) * prev.width) / r.width / k), Math.floor(((e.clientY - r.top) * prev.height) / r.height / k)]; };
    prev.addEventListener("pointerdown", (e) => { const [x, y] = cellAt(e); if (x < 0 || y < 0 || x >= PW || y >= PH) return; cellPaint = cells[y * PW + x] ? 0 : 1; cells[y * PW + x] = cellPaint; prev.setPointerCapture(e.pointerId); drawPreview(); });
    prev.addEventListener("pointermove", (e) => { if (cellPaint === null) return; const [x, y] = cellAt(e); if (x < 0 || y < 0 || x >= PW || y >= PH || cells[y * PW + x] === cellPaint) return; cells[y * PW + x] = cellPaint; drawPreview(); });
    prev.addEventListener("pointerup", () => { cellPaint = null; });

    // ---- keys -------------------------------------------------------------------------------------------------
    const active = () => root.isConnected && root.getClientRects().length > 0 && !document.querySelector("dialog[open]");
    document.addEventListener("keyup", (e) => { if (e.key === " ") { spaceDown = false; view.classList.remove("panning"); } });
    document.addEventListener("keydown", (e) => {
      if (!active() || (e.target.closest && e.target.closest("input:not([type=checkbox]):not([type=range]), select, textarea"))) return;
      const mod = e.metaKey || e.ctrlKey, k = e.key.toLowerCase();
      const go = (f) => { e.preventDefault(); f(); };
      if (e.key === " ") { spaceDown = true; view.classList.add("panning"); e.preventDefault(); return; }
      if (mod) {
        if (k === "z") return go(() => (e.shiftKey ? restore(redoStack, undoStack) : restore(undoStack, redoStack)));
        if (k === "y") return go(() => restore(redoStack, undoStack));
        if (k === "c") return go(copy);
        if (k === "x") return go(() => { copy(); clearRegion(); });
        if (k === "v") return go(paste);
        if (k === "a") return go(() => { dropFloating(); const [x, y] = slotXY(sel); selection = mode === "tile" ? { x, y, w: TS, h: TS } : { x: 0, y: 0, w: SHEET_W, h: SHEET_H }; setTool("select"); draw(); });
        if (k === "s") return go(() => saveTileset(false));
        return;
      }
      if (e.key === "Tab") return go(() => setMode(mode === "sheet" ? "tile" : "sheet"));
      if (e.key === "Escape") return go(cancelSelection);
      if (e.key === "Enter") return go(() => { dropFloating(); selection = null; draw(); });
      if (e.key === "Delete" || e.key === "Backspace") return go(clearRegion);
      if (e.key.startsWith("Arrow")) return go(() => {
        const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
        if (floating) { floating.x += d[0] * (e.shiftKey ? TS : 1); floating.y += d[1] * (e.shiftKey ? TS : 1); draw(); return; }
        sel = Math.max(1, Math.min(COLS * ROWS, sel + d[0] + d[1] * COLS)); selection = null; draw(); drawSeam(); showSlot();
      });
      if (e.key === "+" || e.key === "=") return go(() => zoomBy(1));
      if (e.key === "-" || e.key === "_") return go(() => zoomBy(-1));
      if (e.key === "[") return go(() => { q("st-size").value = Math.max(1, size() - 1); });
      if (e.key === "]") return go(() => { q("st-size").value = Math.min(8, size() + 1); });
      const keys = { b: "pencil", e: "eraser", l: "line", u: "rect", o: "ellipse", i: "pick", m: "select" };
      if (k === "g") return go(() => setTool(e.shiftKey ? "replace" : "fill"));
      if (keys[k]) return go(() => setTool(keys[k]));
      if (k === "x") return go(() => { [c1, c2] = [c2, c1]; drawPalette(); });
      if (k === "f") return go(() => { q("st-filled").checked = !q("st-filled").checked; });
      if (k === "h") return go(() => transform("flipH"));
      if (k === "j") return go(() => transform("flipV"));
      if (k === "r") return go(() => transform("rotateCW"));
    });
    // marching ants
    setInterval(() => { if ((selection || floating) && active()) draw(); }, 250);

    // ---- start ------------------------------------------------------------------------------------------------
    const ready = (async () => {
      const r = await fetch("/arena/tile-layout.json", { credentials: "same-origin" }).catch(() => null);
      const parsed = r && r.ok ? TileLayout.parse(await r.text()) : { layout: null, errors: ["couldn't load the standard layout"] };
      standard = parsed.layout || TileLayout.parse({ format: "dare-tile-layout", version: 1, roles: { single: { open: "" } }, packs: [] }).layout;
      layout = cloneLayout(standard); packId = layout.packs[0] ? layout.packs[0].id : null;
      zoom = Math.max(2, Math.min(4, Math.floor((q("st-scroll").clientWidth - 8) / SHEET_W) || 3));
      setTool("pencil"); setShape("sampler"); showPacks();
      setImage(img, src);
      if (!parsed.layout) say("The standard layout didn't load: " + parsed.errors.join("; "), false);
      await loadLists();
    })();

    return {
      ready,
      // the arena editor calls this when the selected arena changes; opens its map's tileset unless work is unsaved
      async setArena(name, { open } = {}) {
        await ready;
        const changed = name !== arena; arena = name || "";
        if (changed) await loadLists();
        if (open && (changed || src.from === "new") && !dirty) await openChoice("arena:");
      },
      open: (v) => ready.then(() => openChoice(v)),
    };
  }

  // the page /arena/tiles: ?arena=<name> opens that arena's map tileset, ?map=<ready-made>.lvl one of those
  const page = document.getElementById("studio");
  if (page && document.body.classList.contains("tilepage")) {
    const p = new URLSearchParams(location.search), arena = p.get("arena") || "", map = p.get("map");
    const s = mount(page, { arena });
    if (arena) s.open("arena:"); else if (map) s.open("shared:" + map);
  }

  return { mount };
})();
