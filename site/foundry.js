// The Foundry (/foundry.html): ships and graphics pilots shared for everyone (/account/api/gallery).
// Banners: pilots' own are cards like any design; the Banner Emporium's library is foundry-banners.js.
// Anyone can browse and download; a signed-in pilot can keep a copy in their own Shipyard or Armory
// (other graphics become their own picture for that graphic), share their own, and take theirs down.
// The server decides what a sheet is from its size and frame grid; this page only asks the pilot
// when a size fits more than one kind, and then only among those.

const API = "/account/api";
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const PAGE = 24;
const $ = (id) => document.getElementById(id);

function el(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v == null || v === false) continue;
    if (k === "text") e.textContent = v; else if (k === "class") e.className = v; else e.setAttribute(k, v === true ? "" : v);
  }
  for (const kid of kids) if (kid != null) e.append(kid);
  return e;
}

// ---- animated icons (the Arena Editor's, wwwroot/app.js): one frame of a sheet at a time ----------
// How each sheet is cut: columns, rows, and what plays: "all" frames, "row" (row 0: level 1, the
// enemy's...) or "sheet" (tiles: the whole picture, still). Ships: 40 headings, 10×4, frame 0 nose-up.
const SPRITES = {
  bullets: [4, 10, "row"], bombs: [10, 13, "row"], mines: [10, 8, "row"], shrapnel: [10, 6, "row"],
  repel: [5, 2, "all"], rocket: [13, 2, "all"],
  prizes: [10, 1, "all"], powerb: [10, 3, "row"], flag: [10, 2, "row"], goal: [9, 2, "row"],
  explode0: [7, 1, "all"], explode1: [6, 6, "all"], explode2: [4, 11, "all"], empburst: [5, 2, "all"],
  warp: [6, 3, "all"], warppnt: [10, 1, "all"], exhaust: [19, 2, "row"], shield: [10, 1, "all"], super: [10, 1, "all"],
  over1: [15, 2, "all"], over2: [10, 3, "all"], over3: [15, 2, "all"], over4: [5, 2, "all"], over5: [4, 6, "all"],
  wall: [10, 2, "sheet"], gradient: [1, 1, "sheet"], ship: [10, 4, "all"],
};
const layoutOf = (kind) => SPRITES[kind] ?? [1, 1, "sheet"];
const icons = new WeakMap();
const shown = new Set();
const stillMotion = matchMedia("(prefers-reduced-motion: reduce)");
let iconTick = 0, iconTimer = 0;
const onScreen = "IntersectionObserver" in window ? new IntersectionObserver((entries) => {
  for (const e of entries) if (e.isIntersecting) shown.add(e.target); else shown.delete(e.target);
  runIcons();
}, { rootMargin: "64px" }) : null;

function iconCanvas(size, label, canvas = null) {
  const k = Math.min(3, Math.max(1, Math.round(globalThis.devicePixelRatio || 1)));
  const c = canvas ?? el("canvas", { class: "icon", role: "img" });
  c.width = c.height = size * k;
  c.style.width = c.style.height = size + "px";
  c.setAttribute("aria-label", label);
  return c;
}

function showIcon(canvas, img, kind) {
  const [cols, rows, mode] = layoutOf(kind);
  const still = mode === "sheet";
  icons.set(canvas, { img, cols: still ? 1 : cols, rows: still ? 1 : rows, n: still ? 1 : mode === "row" ? cols : cols * rows });
  drawIcon(canvas, 0);
  if (onScreen) onScreen.observe(canvas); else shown.add(canvas);
  runIcons();
}

function drawIcon(canvas, t) {
  const p = icons.get(canvas);
  if (!p) return;
  const fw = p.img.width / p.cols, fh = p.img.height / p.rows, f = t % p.n;
  const W = canvas.width, H = canvas.height;
  let s = Math.min(W / fw, H / fh) * (fw <= 8 ? 0.55 : 1);
  if (s >= 1) s = Math.floor(s);
  const w = Math.round(fw * s), h = Math.round(fh * s);
  const g = canvas.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, W, H);
  g.drawImage(p.img, (f % p.cols) * fw, Math.floor(f / p.cols) * fh, fw, fh, Math.floor((W - w) / 2), Math.floor((H - h) / 2), w, h);
}

function runIcons() {
  const move = !stillMotion.matches && !document.hidden && [...shown].some((c) => (icons.get(c)?.n ?? 1) > 1);
  if (move && !iconTimer) {
    iconTimer = setInterval(() => {
      iconTick++;
      for (const c of shown) if (c.isConnected) drawIcon(c, iconTick); else { shown.delete(c); onScreen?.unobserve(c); }
    }, 70);
  } else if (!move && iconTimer) { clearInterval(iconTimer); iconTimer = 0; }
}
stillMotion.addEventListener?.("change", () => { for (const c of shown) drawIcon(c, 0); runIcons(); });
document.addEventListener("visibilitychange", runIcons);

/** A sheet with no transparency (the game's BMPs and many PNGs) shows black as clear, as the game does. */
function clearBlack(img) {
  const c = el("canvas", { width: String(img.width), height: String(img.height) });
  const g = c.getContext("2d", { willReadFrequently: true });
  g.drawImage(img, 0, 0);
  try {
    const data = g.getImageData(0, 0, c.width, c.height), px = data.data;
    for (let i = 3; i < px.length; i += 4) if (px[i] !== 255) return c;
    for (let i = 0; i < px.length; i += 4) if (!px[i] && !px[i + 1] && !px[i + 2]) px[i + 3] = 0;
    g.putImageData(data, 0, 0);
  } catch { /* unreadable: as it is */ }
  return c;
}

/** Loads `url` (or a Blob) onto an icon canvas as graphic `kind`. */
function iconFrom(canvas, src, kind) {
  const img = new Image();
  img.addEventListener("load", () => showIcon(canvas, clearBlack(img), kind));
  img.src = typeof src === "string" ? src : URL.createObjectURL(src);
  return canvas;
}

// ---- maps and tilesets (the map editor's lvl.js, /arena/lvl.js) --------------------------------
// A map's preview is built once, here, when it is shared: its tiles drawn in the colours of its own
// tileset (or the standard one when it has none), sent along and kept with it. Cards only load that PNG.
const THUMB = 256;
let stockTiles = null;

async function standardTiles() {
  if (stockTiles) return stockTiles;
  const r = await fetch("/arena/tiles.bm2");
  if (!r.ok) throw new Error("no standard tiles");
  return (stockTiles = Lvl.decodeBmp(new Uint8Array(await r.arrayBuffer())));
}

/** Each tile slot's average colour in a tileset (19×10 slots of 16 px); null where the slot is black. */
function slotColours(ts) {
  const out = new Array(256).fill(null);
  if (!ts) return out;
  const w = Math.min(ts.width, 304), h = Math.min(ts.height, 160);
  const d = ts.getContext("2d").getImageData(0, 0, w, h).data;
  for (let t = 1; t <= 190; t++) {
    const x0 = ((t - 1) % 19) * 16, y0 = Math.floor((t - 1) / 19) * 16;
    if (x0 + 16 > w || y0 + 16 > h) continue;
    let r = 0, g = 0, b = 0, n = 0;
    for (let y = y0; y < y0 + 16; y++) for (let x = x0; x < x0 + 16; x++) {
      const i = (y * w + x) * 4;
      if (d[i] + d[i + 1] + d[i + 2] < 24) continue; // black shows as space
      r += d[i]; g += d[i + 1]; b += d[i + 2]; n++;
    }
    // a mostly-black tile is a thin wall: its lit pixels, brightened a little so it still reads at a quarter size
    if (n) out[t] = [r / n, g / n, b / n].map((v) => Math.min(255, Math.round(v * (n < 64 ? 1.2 : 1))));
  }
  return out;
}

/** The PNG preview of a map: the part with tiles (as the editor's overview), square, THUMB a side. */
async function mapThumb(parsed) {
  const { grid, tileset } = parsed, N = Lvl.N;
  const colours = slotColours(tileset ?? await standardTiles().catch(() => null));
  let minX = N, minY = N, maxX = -1, maxY = -1;
  for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) if (grid[y * N + x]) {
    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
  }
  let side = N, x0 = 0, y0 = 0;
  if (maxX >= 0) {
    side = Math.min(N, Math.max(maxX - minX, maxY - minY) + 9);
    x0 = Math.max(0, Math.min(N - side, Math.floor((minX + maxX - side) / 2)));
    y0 = Math.max(0, Math.min(N - side, Math.floor((minY + maxY - side) / 2)));
  }
  // As the editor's overview draws it: every tile at least one whole pixel, so thin walls stay visible at a quarter size.
  const out = el("canvas", { width: String(THUMB), height: String(THUMB) });
  const o = out.getContext("2d"), img = o.createImageData(THUMB, THUMB), px = img.data;
  for (let i = 0; i < px.length; i += 4) { px[i] = 4; px[i + 1] = 7; px[i + 2] = 14; px[i + 3] = 255; }
  const k = THUMB / side, dot = Math.max(1, Math.ceil(k));
  for (let y = 0; y < side; y++) for (let x = 0; x < side; x++) {
    const t = grid[(y + y0) * N + x + x0];
    if (!t) continue;
    const c = colours[t] ?? hexRgb(Lvl.color(t));
    const ox = Math.floor(x * k), oy = Math.floor(y * k);
    for (let dy = 0; dy < dot && oy + dy < THUMB; dy++) for (let dx = 0; dx < dot && ox + dx < THUMB; dx++) {
      const i = ((oy + dy) * THUMB + ox + dx) * 4;
      px[i] = c[0]; px[i + 1] = c[1]; px[i + 2] = c[2];
    }
  }
  o.putImageData(img, 0, 0);
  const blob = await new Promise((r) => out.toBlob(r, "image/png"));
  return new Uint8Array(await blob.arrayBuffer());
}

const hexRgb = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));

/** A tileset sheet as a canvas (8-bit, RLE8 or 24-bit BMP, as the editor reads them). */
async function tilesetCanvas(src) {
  const bytes = src instanceof Uint8Array ? src : new Uint8Array(await (await fetch(src)).arrayBuffer());
  return Lvl.decodeBmp(bytes);
}

/** Card art for maps (the stored preview) and tilesets (the sheet); a click or Enter shows it bigger. */
const KIND_RENDER = {
  map: (it) => zoomable(el("img", { class: "sheet map", src: it.image, alt: `Map preview: ${it.name}`, loading: "lazy", width: "132", height: "132" }), it),
  tileset: (it) => {
    const c = el("canvas", { class: "sheet tileset", role: "img", "aria-label": `Tileset: ${it.name}`, width: "304", height: "160" });
    tilesetCanvas(it.file ?? it.image).then((ts) => c.getContext("2d").drawImage(ts, 0, 0)).catch(() => c.setAttribute("aria-label", `${it.name} (couldn't be drawn)`));
    return zoomable(c, it);
  },
  banner: (it) => zoomable(el("img", { class: "sheet banner", src: it.image, alt: `Banner: ${it.name}`, loading: "lazy", width: "96", height: "64" }), it),
};

function zoomable(art, it) {
  const b = el("button", { type: "button", class: "zoomer", title: "Show it bigger", "aria-label": `Show ${it.name} bigger` }, art);
  b.addEventListener("click", () => showZoom(art, it));
  return b;
}

function showZoom(art, it) {
  const box = $("zoom");
  const big = art.tagName === "IMG" ? el("img", { src: art.src, alt: art.alt }) : el("canvas", { width: String(art.width), height: String(art.height), role: "img", "aria-label": art.getAttribute("aria-label") });
  if (big.tagName === "CANVAS") big.getContext("2d").drawImage(art, 0, 0);
  big.className = "zoomed " + (it.kind === "tileset" || it.kind === "banner" ? it.kind : "map");
  $("zoom-title").textContent = `${it.name} · ${kindText(it)}`;
  $("zoom-art").replaceChildren(big);
  box.showModal();
}

// ---- the server ---------------------------------------------------------------------------------
async function call(path, body) {
  const r = await fetch(API + path, {
    method: body ? "POST" : "GET", credentials: "same-origin", cache: "no-store",
    headers: body ? HEADERS : undefined, body: body ? JSON.stringify(body) : undefined,
  });
  const data = await r.json().catch(() => ({}));
  if (r.status === 401) throw Object.assign(new Error("Sign in first."), { status: 401, data });
  if (!r.ok || data.ok === false) throw Object.assign(new Error(data.message || "That didn't work. Try again."), { status: r.status, data });
  return data;
}

function base64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

// ---- state and filters (kept in the address, so a filtered view can be linked) -----------------
let kinds = [];
let viewer = null; // { name, staff } when signed in
let next = 0;
let loading = 0;
const filter = { q: "", kind: "", pilot: "" };
const kindOf = (name) => kinds.find((k) => k.name === name);
const kindText = (item) => item.kindLabel;

function readAddress() {
  const q = new URLSearchParams(location.search);
  filter.q = q.get("q") ?? "";
  filter.kind = q.get("kind") ?? "";
  filter.pilot = (q.get("pilot") ?? "").slice(0, 40);
}

function writeAddress() {
  const q = new URLSearchParams();
  if (filter.q) q.set("q", filter.q);
  if (filter.kind) q.set("kind", filter.kind);
  if (filter.pilot) q.set("pilot", filter.pilot);
  const s = q.toString();
  // A new filter leaves any linked design (#d<id>) behind, so it isn't put back on top of results it doesn't match.
  history.replaceState(null, "", location.pathname + (s ? "?" + s : ""));
}

const PLURAL = { map: "Maps", tileset: "Tilesets", banner: "Banners" };

function fillKinds() {
  const sel = $("kind");
  if (sel.options.length > 1) return;
  sel.append(el("option", { value: "ship", text: "Ships" }));
  for (const k of kinds) if (PLURAL[k.name]) sel.append(el("option", { value: k.name, text: PLURAL[k.name] }));
  const groups = new Map();
  for (const k of kinds) if (k.name !== "ship" && !PLURAL[k.name]) (groups.get(k.group) ?? groups.set(k.group, []).get(k.group)).push(k);
  for (const [g, list] of groups) sel.append(el("optgroup", { label: g }, ...list.map((k) => el("option", { value: k.name, text: k.label }))));
  sel.value = filter.kind;
}

function showActive() {
  const box = $("active");
  const chips = [];
  if (filter.pilot) chips.push(chip(`Shared by ${filter.pilot}  ✕`, () => { filter.pilot = ""; refresh(); }));
  box.replaceChildren(...chips);
  box.hidden = !chips.length;
}

function chip(text, onClick, title) {
  const b = el("button", { type: "button", class: "chip", text, title });
  b.addEventListener("click", onClick);
  return b;
}

function say(text, tone = "", where = "status") {
  const s = $(where);
  s.textContent = text;
  s.className = "status" + (tone ? " " + tone : "");
}

// ---- the list -----------------------------------------------------------------------------------
async function load(more = false) {
  const ticket = ++loading;
  const q = new URLSearchParams({ limit: String(PAGE) });
  if (filter.q) q.set("q", filter.q);
  if (filter.kind) q.set("kind", filter.kind);
  if (filter.pilot) q.set("pilot", filter.pilot);
  if (more && next) q.set("before", String(next));
  if (!more) say("Loading…");
  try {
    const data = await call("/gallery?" + q);
    if (ticket !== loading) return;
    kinds = data.kinds;
    viewer = data.viewer;
    fillKinds();
    const grid = $("grid");
    if (!more) grid.replaceChildren();
    for (const it of data.items) grid.append(card(it));
    next = data.next;
    $("more").hidden = !next;
    if (!grid.children.length) {
      grid.append(el("li", { class: "empty", text: filtered() ? "Nothing shared matches that yet." : "Nothing shared yet. Be the first: Share a design." }));
    }
    say(filtered() ? `${data.count} ${data.count === 1 ? "design matches" : "designs match"}.` : `${data.count} ${data.count === 1 ? "design" : "designs"} shared.`);
    if (!more) await showLinked();
  } catch (e) {
    if (ticket === loading) say(e.message, "bad");
  }
}

const filtered = () => !!(filter.q || filter.kind || filter.pilot);

/** Tells the banner library (foundry-banners.js) what the filters are: it shows with Kind = Banners. */
const announce = () => document.dispatchEvent(new CustomEvent("foundry-filter", { detail: { ...filter } }));

function refresh() {
  writeAddress();
  showActive();
  announce();
  next = 0;
  void load();
}

/** #d<id>: that design, highlighted; fetched on its own when the list doesn't hold it. */
async function showLinked() {
  const m = /^#d(\d+)$/.exec(location.hash);
  if (!m) return;
  let c = document.getElementById("d" + m[1]);
  if (!c) {
    try {
      const data = await call(`/gallery?before=${Number(m[1]) + 1}&limit=1`);
      const it = data.items[0];
      if (it && String(it.id) === m[1]) { c = card(it); $("grid").prepend(c); $("grid").querySelector(".empty")?.remove(); }
    } catch { /* gone */ }
  }
  if (!c) { say("That design has been taken down.", "bad"); return; }
  c.classList.add("linked");
  c.scrollIntoView({ block: "center" });
}

function card(it) {
  const k = kindOf(it.kind);
  const art = KIND_RENDER[it.kind]?.(it) ?? iconFrom(iconCanvas(104, `${it.name}, ${kindText(it)}`), it.image, it.kind);
  const by = el("span", { class: "by" }, "by ", el("a", { href: `/pilot/${encodeURIComponent(it.by)}`, text: it.by, title: `${it.by}'s profile` }));
  const pilotFilter = el("button", { type: "button", class: "seeall", text: `See all by ${it.by}`, title: `Everything ${it.by} shared` });
  pilotFilter.addEventListener("click", () => { filter.pilot = it.by; refresh(); });
  const tags = el("div", { class: "tags" }, ...it.keywords.slice(0, 8).map((w) => chip(w, () => { filter.q = w; $("q").value = w; refresh(); }, `Find designs about “${w}”`)));
  const body = el("div", { class: "body" },
    el("h3", { text: it.name }),
    el("p", { class: "meta" }, el("span", { class: "kind", text: kindText(it) }), " · ", by),
    it.prompt ? el("p", { class: "prompt", text: `“${it.prompt}”`, title: "What it was drawn from" }) : null,
    tags);
  const acts = el("div", { class: "acts" });
  const keep = el("button", { type: "button", class: "btn small primary", text: keepLabel(k) });
  keep.addEventListener("click", () => void keepIt(it, k, keep));
  const dl = el("a", { class: "btn small", href: `${it.file ?? it.image}&download=1`, download: "", text: "Download",
    title: k?.file ? `The .${k.file} file` : undefined });
  acts.append(keep);
  if (it.kind === "banner") {
    const use = el("button", { type: "button", class: "btn small", text: "Use as my banner" });
    use.addEventListener("click", () => void useBanner(it, use));
    acts.append(use);
  }
  acts.append(dl);
  if (viewer && (it.mine || viewer.staff)) {
    const rm = el("button", { type: "button", class: "btn small danger", text: it.mine ? "Take down" : "Take down (staff)" });
    rm.addEventListener("click", () => void takeDown(it, rm));
    acts.append(rm);
  }
  const li = el("li", { class: "card", id: "d" + it.id }, el("div", { class: "art" + (KIND_RENDER[it.kind] ? " wide" : "") }, art), body, acts);
  if (!filter.pilot) body.querySelector(".meta").after(el("p", { class: "seeallrow" }, pilotFilter));
  return li;
}

const KEEP = { shipyard: "Keep in my Shipyard", armory: "Keep in my Armory", maps: "Keep in my maps", tilesets: "Keep in my tilesets" };
const keepLabel = (k) => KEEP[k?.save] ?? `Use as my ${(k?.label ?? "graphic").toLowerCase()}`;

async function keepIt(it, k, button) {
  if (!viewer) { location.href = signInHref(); return; }
  if (k?.save === "graphics" && !confirm(`Use “${it.name}” as your ${k.label.toLowerCase()}? It replaces the one you have now (you can change it back in the Graphics overlay).`)) return;
  button.disabled = true;
  try {
    const r = await call("/gallery/save", { id: it.id });
    if (r.kept === "maps" || r.kept === "tilesets") {
      // Kept in the Arena Editor's library: the map editor opens a map as a draft, or puts a tileset into the map there.
      const href = r.kept === "maps" ? `/arena/map?lib=${r.keptId}` : `/arena/map?tileset=${r.keptId}`;
      const s = $("status");
      s.className = "status good";
      s.replaceChildren(`${it.name}: ${r.message} `, el("a", { href, text: r.kept === "maps" ? "Open it in the map editor" : "Use it in the map editor" }),
        r.kept === "maps" ? " (a draft: your arena keeps its map until you Publish or Apply)." : " (it goes into the map you're editing; nothing changes until you Publish).");
      return;
    }
    say(`${it.name}: ${r.message}${k?.save === "graphics" ? "" : " Put it on in the game's Graphics overlay (Esc → G)."}`, "good");
  } catch (e) {
    say(e.message, "bad");
  } finally {
    button.disabled = false;
  }
}

/** Keeps a shared banner in the Armory and makes it the pilot's banner. */
async function useBanner(it, button) {
  if (!viewer) { location.href = signInHref(); return; }
  button.disabled = true;
  try {
    const r = await call("/gallery/save", { id: it.id, use: true });
    say(`${it.name}: ${r.message} It shows beside your name in the game.`, "good");
  } catch (e) {
    say(e.message, "bad");
  } finally {
    button.disabled = false;
  }
}

async function takeDown(it, button) {
  if (!confirm(`Take “${it.name}” out of the Foundry for everyone? Copies pilots already kept stay theirs.`)) return;
  button.disabled = true;
  try {
    await call("/gallery/remove", { id: it.id });
    document.getElementById("d" + it.id)?.remove();
    say(`“${it.name}” was taken down.`, "good");
  } catch (e) {
    say(e.message, "bad");
    button.disabled = false;
  }
}

const signInHref = () => "/account/login?next=" + encodeURIComponent(location.pathname + location.search + location.hash);

// ---- sharing ------------------------------------------------------------------------------------
const dlg = $("share");
let picked = null; // { from: "file", bytes, thumb?, previews? } | { from: "armory"|"shipyard", id, kind, prompt }
let choices = []; // kinds the picture can be

function openShare() {
  if (!viewer) { location.href = signInHref(); return; }
  resetPick();
  setPickTab("file");
  dlg.showModal();
}

function resetPick() {
  picked = null;
  choices = [];
  $("chosen").hidden = true;
  $("share-go").disabled = true;
  $("file").value = "";
  $("keywords").value = "";
  say("", "", "share-status");
}

function setPickTab(which) {
  $("pick-file").setAttribute("aria-pressed", String(which === "file"));
  $("pick-saved").setAttribute("aria-pressed", String(which === "saved"));
  $("from-file").hidden = which !== "file";
  $("from-saved").hidden = which !== "saved";
  if (which === "saved") void loadSaved();
}

async function loadSaved() {
  const list = $("savedlist");
  list.replaceChildren(el("p", { class: "empty", text: "Loading…" }));
  try {
    const [a, y] = await Promise.all([call("/armory"), call("/shipyard")]);
    const labels = Object.fromEntries(a.kinds.map((k) => [k.name, k.label]));
    const items = [
      ...y.ships.map((s) => ({ from: "shipyard", id: s.id, kind: "ship", name: s.name, prompt: s.prompt ?? "", created: s.created ?? "", url: `${API}/shipyard/sheet?id=${s.id}`,
        label: "Ship" })),
      ...a.items.map((i) => ({ from: "armory", id: i.id, kind: i.kind, name: i.name, prompt: i.prompt ?? "", created: i.created ?? "", url: `${API}/armory/sheet?id=${i.id}`,
        label: labels[i.kind] ?? i.kind })),
    ];
    if (!items.length) { list.replaceChildren(el("p", { class: "empty", text: "Your Shipyard and Armory are empty. Make some in the game's Graphics overlay (Esc → G)." })); return; }
    list.replaceChildren(...savedGroups(items).map(([title, group]) =>
      el("section", { class: "savedgroup", "aria-label": title },
        el("h3", null, title, el("small", { text: String(group.length) })),
        el("ul", { class: "savedgrid" }, ...group.map((it) => {
          const b = el("button", { type: "button", "aria-pressed": "false", title: it.prompt || it.name },
            iconFrom(iconCanvas(48, it.name), it.url, it.kind), el("span", { class: "name", text: it.name }));
          b.addEventListener("click", () => {
            for (const x of list.querySelectorAll("button")) x.setAttribute("aria-pressed", String(x === b));
            void pickSaved(it);
          });
          return el("li", null, b);
        })))));
  } catch (e) {
    list.replaceChildren(el("p", { class: "empty", text: e.status === 401 ? "Sign in to share from your Shipyard or Armory." : e.message }));
  }
}

/** The pilot's saved designs grouped by kind, in the Kind menu's order (Ships, Maps, Tilesets, Banners, then the
 *  graphics), newest first within each kind. Returns [heading, items][]. */
function savedGroups(items) {
  const order = ["ship", ...kinds.filter((k) => PLURAL[k.name]).map((k) => k.name), ...kinds.filter((k) => k.name !== "ship" && !PLURAL[k.name]).map((k) => k.name)];
  const rank = (kind) => { const i = order.indexOf(kind); return i < 0 ? order.length : i; };
  const byKind = new Map();
  for (const it of items) (byKind.get(it.kind) ?? byKind.set(it.kind, []).get(it.kind)).push(it);
  const newest = (a, b) => String(b.created).localeCompare(String(a.created)) || b.id - a.id;
  return [...byKind].sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b)).map(([kind, group]) =>
    [kind === "ship" ? "Ships" : PLURAL[kind] ?? kindOf(kind)?.label ?? group[0].label, group.sort(newest)]);
}

async function pickSaved(it) {
  picked = { from: it.from, id: it.id, kind: it.kind, prompt: it.prompt };
  choices = [kindOf(it.kind)].filter(Boolean);
  showChosen(it.url, it.kind, it.name);
  $("detect").replaceChildren("From your ", it.from === "shipyard" ? "Shipyard" : "Armory", ": ", el("b", { text: kindOf(it.kind)?.label ?? it.kind }), ".");
  $("prompthint").hidden = !it.prompt;
  $("prompthint").textContent = it.prompt ? `Drawn from “${it.prompt}”: those words come along, so pilots can find it by them.` : "";
}

/** A BMP that is a ship's 10×4 grid becomes a PNG (ships are PNGs), black turned clear as the game does. */
async function shipBmpToPng(bytes, w, h) {
  const bitmap = await createImageBitmap(new Blob([bytes]));
  const c = el("canvas", { width: String(w), height: String(h) });
  const g = c.getContext("2d");
  g.drawImage(bitmap, 0, 0);
  bitmap.close();
  const data = g.getImageData(0, 0, w, h);
  for (let i = 0; i < data.data.length; i += 4) if (!data.data[i] && !data.data[i + 1] && !data.data[i + 2]) data.data[i + 3] = 0;
  g.putImageData(data, 0, 0);
  const blob = await new Promise((r) => c.toBlob(r, "image/png"));
  return new Uint8Array(await blob.arrayBuffer());
}

/** A 12×8 picture as a 24-bit BMP (clear pixels black), which the server matches to the game's palette. */
async function bannerBmp(bytes) {
  const bitmap = await createImageBitmap(new Blob([bytes]));
  const c = el("canvas", { width: "12", height: "8" });
  const g = c.getContext("2d");
  g.fillStyle = "#000";
  g.fillRect(0, 0, 12, 8);
  g.drawImage(bitmap, 0, 0);
  bitmap.close();
  const px = g.getImageData(0, 0, 12, 8).data;
  const out = new Uint8Array(54 + 36 * 8);
  const v = new DataView(out.buffer);
  out[0] = 0x42; out[1] = 0x4d;
  v.setUint32(2, out.length, true); v.setUint32(10, 54, true); v.setUint32(14, 40, true);
  v.setInt32(18, 12, true); v.setInt32(22, 8, true); v.setUint16(26, 1, true); v.setUint16(28, 24, true);
  for (let y = 0; y < 8; y++)
    for (let x = 0; x < 12; x++) {
      const s = (y * 12 + x) * 4, d = 54 + (7 - y) * 36 + x * 3;
      out[d] = px[s + 2]; out[d + 1] = px[s + 1]; out[d + 2] = px[s];
    }
  return out;
}

function sizeOf(b) {
  if (b.length >= 24 && b[0] === 0x89 && b[1] === 0x50) return { png: true, w: (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19]) >>> 0, h: (b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]) >>> 0 };
  if (b.length >= 26 && b[0] === 0x42 && b[1] === 0x4d) {
    const v = new DataView(b.buffer, b.byteOffset);
    return { png: false, w: v.getInt32(18, true), h: Math.abs(v.getInt32(22, true)) };
  }
  return null;
}

/** For a map file: its preview PNG, and what each kind it could be looks like (the map, or the tileset inside it). */
async function mapPreviews(bytes, matches) {
  if (!matches.some((k) => k.name === "map" || k.name === "tileset") || !window.Lvl) return {};
  const out = {};
  const isMap = matches.some((k) => k.name === "map");
  const parsed = isMap ? Lvl.parse(bytes) : null;
  if (isMap) out.thumb = await mapThumb(parsed);
  if (out.thumb) out.map = new Blob([out.thumb], { type: "image/png" });
  if (matches.some((k) => k.name === "tileset")) {
    try {
      const ts = parsed ? parsed.tileset : Lvl.decodeBmp(bytes);
      if (ts) out.tileset = await new Promise((r) => ts.toBlob(r, "image/png"));
    } catch { /* the server checked it; no preview */ }
  }
  return out;
}

async function pickFile(file) {
  if (!file) return;
  resetPick();
  let bytes = new Uint8Array(await file.arrayBuffer());
  const size = sizeOf(bytes);
  if (size && !size.png && size.w % 10 === 0 && size.h === size.w / 10 * 4) {
    try { bytes = await shipBmpToPng(bytes, size.w, size.h); } catch { /* the server will say */ }
  }
  if (size?.png && size.w === 12 && size.h === 8) {
    try { bytes = await bannerBmp(bytes); } catch { /* the server will say */ }
  }
  say("Checking what it is…", "", "share-status");
  try {
    const d = await call("/gallery/detect", { sheet: base64(bytes) });
    choices = d.matches;
    const previews = await mapPreviews(bytes, choices);
    picked = { from: "file", bytes, thumb: previews.thumb, previews };
    const stem = file.name.replace(/\.[^.]+$/, "").replace(/[_-]+/g, " ").slice(0, 40);
    showChosen(new Blob([bytes]), choices[0].name, stem || choices[0].label);
    $("prompthint").hidden = true;
    const what = choices[0].name === "map" ? `${kb(bytes.length)} map` : `${d.width}×${d.height}`;
    if (choices.length === 1) $("detect").replaceChildren(`${what}: `, el("b", { text: choices[0].label }), ".");
    else if (choices[0].name === "map") $("detect").replaceChildren(`A ${kb(bytes.length)} map with its own tileset: share the map, or just its tileset.`);
    else $("detect").replaceChildren(`${d.width}×${d.height} is the size of ${choices.length} kinds.`);
    say("", "", "share-status");
  } catch (e) {
    say(e.message, "bad", "share-status");
  }
}

const kb = (n) => Math.max(1, Math.round(n / 1024)) + " KB";

/** The preview of the picked file as `kind`: a map's preview, a tileset sheet, or the sheet's animated icon. */
function previewAs(canvas, src, kind) {
  icons.delete(canvas);
  const own = picked?.previews?.[kind];
  if (own || KIND_RENDER[kind]) {
    const img = new Image();
    img.addEventListener("load", () => {
      const g = canvas.getContext("2d"), s = Math.min(canvas.width / img.width, canvas.height / img.height);
      g.imageSmoothingEnabled = false;
      g.clearRect(0, 0, canvas.width, canvas.height);
      g.drawImage(img, (canvas.width - img.width * s) / 2, (canvas.height - img.height * s) / 2, img.width * s, img.height * s);
    });
    img.src = URL.createObjectURL(own ?? src);
    return;
  }
  iconFrom(canvas, src, kind);
}

function showChosen(src, kind, name) {
  $("chosen").hidden = false;
  const canvas = iconCanvas(128, `Preview of ${name}`, $("chosen-icon"));
  previewAs(canvas, src, kind);
  $("name").value = name;
  const box = $("kindchoices");
  $("kinds").hidden = choices.length < 2;
  box.replaceChildren(...(choices.length < 2 ? [] : choices.map((k) => {
    const input = el("input", { type: "radio", name: "kindpick", value: k.name });
    input.addEventListener("change", () => { previewAs(canvas, src, k.name); $("share-go").disabled = false; });
    return el("label", null, input, `${k.label} `, el("small", { text: `(${k.group})` }));
  })));
  // a map that carries a tileset is most likely shared as the map: chosen already, the pilot can switch
  const first = box.querySelector('input[value="map"]');
  if (first) first.checked = true;
  $("share-go").disabled = choices.length !== 1 && !first;
}

async function share(event) {
  event.preventDefault();
  if (!picked) return;
  const kind = choices.length === 1 ? choices[0].name : document.querySelector('input[name="kindpick"]:checked')?.value;
  if (!kind) { say("Choose which kind it is.", "bad", "share-status"); return; }
  const body = { kind, name: $("name").value.trim(), keywords: $("keywords").value.trim() };
  if (picked.from === "file") {
    body.sheet = base64(picked.bytes);
    if (kind === "map" && picked.thumb) body.thumb = base64(picked.thumb);
  } else { body.from = picked.from; body.id = picked.id; }
  $("share-go").disabled = true;
  say("Sharing…", "", "share-status");
  try {
    const r = await call("/gallery/add", body);
    dlg.close();
    filter.q = ""; filter.kind = ""; filter.pilot = ""; $("q").value = ""; $("kind").value = "";
    history.replaceState(null, "", location.pathname + "#d" + r.id);
    showActive();
    await load();
    say(r.message + " Thanks, pilot.", "good");
  } catch (e) {
    if (e.status === 409 && e.data?.choices?.length) { choices = e.data.choices; showChosen(new Blob([picked.bytes ?? []]), choices[0].name, body.name); }
    say(e.message, "bad", "share-status");
    $("share-go").disabled = false;
  }
}

// ---- wiring -------------------------------------------------------------------------------------
readAddress();
$("q").value = filter.q;
let typing = 0;
$("q").addEventListener("input", () => { clearTimeout(typing); typing = setTimeout(() => { filter.q = $("q").value.trim(); refresh(); }, 250); });
$("filters").addEventListener("submit", (e) => { e.preventDefault(); clearTimeout(typing); filter.q = $("q").value.trim(); refresh(); });
$("kind").addEventListener("change", () => {
  filter.kind = $("kind").value;
  refresh();
});
$("more").addEventListener("click", () => void load(true));
$("contribute").addEventListener("click", openShare);
$("share-close").addEventListener("click", () => dlg.close());
$("share-cancel").addEventListener("click", () => dlg.close());
$("pick-file").addEventListener("click", () => { resetPick(); setPickTab("file"); });
$("pick-saved").addEventListener("click", () => { resetPick(); setPickTab("saved"); });
$("file").addEventListener("change", () => void pickFile($("file").files[0]));
const drop = $("drop");
drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
drop.addEventListener("dragleave", () => drop.classList.remove("over"));
drop.addEventListener("drop", (e) => { e.preventDefault(); drop.classList.remove("over"); void pickFile(e.dataTransfer.files[0]); });
$("shareform").addEventListener("submit", (e) => void share(e));
$("zoom-close").addEventListener("click", () => $("zoom").close());
$("zoom").addEventListener("click", (e) => { if (e.target === $("zoom")) $("zoom").close(); });
addEventListener("hashchange", () => void showLinked());
showActive();
announce();
void load();
