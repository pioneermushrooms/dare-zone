// The Foundry's banner library (/foundry.html?kind=banner): banners from the SubSpace Banner
// Emporium, credited to their authors, shown under the banners pilots shared here. A page is 200
// banners and one atlas PNG holding all of them (/account/api/gallery/banners), so the grid costs
// two requests a page whatever its size; each tile is a slice of the atlas, scaled up pixelated.
// More pages load as the grid scrolls into view. Keywords (the Foundry's own box) search authors
// and colours on the server. foundry.js says what the filters are with a "foundry-filter" event.

const API = "/account/api/gallery/banners";
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const SCALE = 4; // a 12×8 banner as a 48×32 tile
const BIG = 16; // and 192×128 in the detail view
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

const section = $("emporium");
const grid = $("bgrid");
const sentinel = $("bmore");
const box = $("bdetail");
let shown = false;
let filter = { q: "", author: "", sort: "new" };
let page = 0, pages = 0, loading = 0, busy = false;
let viewer = null;
const byId = new Map(); // banner id -> { it, atlas, n, cols }
const atlases = new Map(); // atlas url -> Promise<HTMLImageElement>

function say(text, tone = "") {
  const s = $("bstatus");
  s.textContent = text;
  s.className = "status" + (tone ? " " + tone : "");
}

async function call(path, body) {
  const r = await fetch(API + path, {
    method: body ? "POST" : "GET", credentials: "same-origin", cache: body ? "no-store" : "default",
    headers: body ? HEADERS : undefined, body: body ? JSON.stringify(body) : undefined,
  });
  const data = await r.json().catch(() => ({}));
  if (r.status === 401) throw Object.assign(new Error("Sign in first."), { status: 401 });
  if (!r.ok || data.ok === false) throw new Error(data.message || "That didn't work. Try again.");
  return data;
}

const signInHref = () => "/account/login?next=" + encodeURIComponent(location.pathname + location.search);

function atlasImage(url) {
  if (!atlases.has(url)) atlases.set(url, new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("Couldn't load those banners."));
    img.src = url;
  }));
  return atlases.get(url);
}

function tile(it, atlas, n, cols, rows) {
  const b = el("button", { type: "button", class: "btile", title: `${it.author || "Unknown"}${it.added ? " · " + it.added.slice(0, 10) : ""}`,
    "aria-label": `Banner by ${it.author || "an unknown pilot"}` });
  b.style.backgroundImage = `url("${atlas}")`;
  b.style.backgroundSize = `${cols * 12 * SCALE}px ${rows * 8 * SCALE}px`;
  b.style.backgroundPosition = `${-(n % cols) * 12 * SCALE}px ${-Math.floor(n / cols) * 8 * SCALE}px`;
  b.addEventListener("click", () => void openDetail(it.id));
  byId.set(it.id, { it, atlas, n, cols });
  return el("li", null, b);
}

async function loadPage(more) {
  if (!shown || (more && (busy || page + 1 >= pages))) return;
  const ticket = more ? loading : ++loading;
  busy = true;
  const want = more ? page + 1 : 0;
  const q = new URLSearchParams({ sort: filter.sort, page: String(want) });
  if (filter.q) q.set("q", filter.q);
  if (filter.author) q.set("author", filter.author);
  if (!more) say("Loading banners…");
  try {
    const d = await call("?" + q);
    if (ticket !== loading) return;
    viewer = d.viewer;
    if (!more) { grid.replaceChildren(); byId.clear(); }
    page = d.page;
    pages = d.pages;
    const rows = Math.max(1, Math.ceil(d.items.length / d.cols));
    grid.append(...d.items.map((it, n) => tile(it, d.atlas, n, d.cols, rows)));
    if (!grid.children.length) grid.append(el("li", { class: "empty", text: "No Emporium banners match that." }));
    const what = filter.q || filter.author ? `${d.count.toLocaleString()} ${d.count === 1 ? "banner matches" : "banners match"}` : `${d.count.toLocaleString()} banners`;
    say(d.count ? `${what}; showing ${Math.min(d.count, (page + 1) * d.per).toLocaleString()}.` : "");
    sentinel.hidden = page + 1 >= pages;
  } catch (e) {
    if (ticket === loading) say(e.message, "bad");
  } finally {
    busy = false;
  }
  // a tall screen may show the end of the grid before any scrolling: keep filling it
  if (!sentinel.hidden && sentinel.getBoundingClientRect().top < innerHeight + 400) void loadPage(true);
}

function showChips() {
  const chips = $("bchips");
  chips.replaceChildren();
  if (filter.author) {
    const c = el("button", { type: "button", class: "chip", text: `By ${filter.author}  ✕` });
    c.addEventListener("click", () => { filter.author = ""; showChips(); void loadPage(false); });
    chips.append(c);
  }
  chips.hidden = !filter.author;
}

// ---- one banner, big ------------------------------------------------------------------------------
let current = null;

async function openDetail(id) {
  const entry = byId.get(id);
  if (!entry) return;
  current = entry;
  const { it, atlas, n, cols } = entry;
  const canvas = $("bbig");
  canvas.width = 12 * BIG;
  canvas.height = 8 * BIG;
  const g = canvas.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, canvas.width, canvas.height);
  canvas.setAttribute("aria-label", `Banner by ${it.author || "an unknown pilot"}, shown 16 times its size`);
  $("bwho").textContent = it.author || "Unknown pilot";
  $("bwhen").textContent = it.added ? `Added ${it.added.replace("T", " ")}` : "";
  $("bplayers").textContent = it.players ? `${it.players.toLocaleString()} ${it.players === 1 ? "pilot" : "pilots"} used it on the Emporium` : "";
  $("bsource").hidden = !it.source;
  if (it.source) $("bsource").href = it.source;
  $("bdownload").href = `${it.file}&download=1`;
  $("bbyauthor").hidden = !it.author || filter.author === it.author;
  $("bbyauthor").textContent = `More by ${it.author}`;
  $("bremove").hidden = !viewer?.staff;
  $("bdstatus").textContent = "";
  $("bdstatus").className = "status";
  if (!box.open) box.showModal();
  try {
    const img = await atlasImage(atlas);
    if (current !== entry) return;
    g.drawImage(img, (n % cols) * 12, Math.floor(n / cols) * 8, 12, 8, 0, 0, canvas.width, canvas.height);
  } catch (e) {
    $("bdstatus").textContent = e.message;
  }
}

async function keep(use, button) {
  if (!current) return;
  if (!viewer) { location.href = signInHref(); return; }
  const s = $("bdstatus");
  button.disabled = true;
  try {
    const r = await call("/keep", { id: current.it.id, use });
    s.className = "status good";
    s.textContent = use ? `${r.message} It shows beside your name in the game (change it in the Graphics overlay's Armory, Esc → G).`
      : `${r.message} Wear it from the Graphics overlay's Armory (Esc → G), or press Use as my banner.`;
  } catch (e) {
    if (e.status === 401) { location.href = signInHref(); return; }
    s.className = "status bad";
    s.textContent = e.message;
  } finally {
    button.disabled = false;
  }
}

async function takeDown() {
  if (!current || !confirm(`Take this banner by ${current.it.author || "an unknown pilot"} out of the Foundry for everyone? Copies pilots already kept stay theirs.`)) return;
  try {
    await call("/remove", { id: current.it.id });
    box.close();
    say("Taken down.", "good");
    void loadPage(false);
  } catch (e) {
    $("bdstatus").className = "status bad";
    $("bdstatus").textContent = e.message;
  }
}

// ---- wiring ---------------------------------------------------------------------------------------
document.addEventListener("foundry-filter", (event) => {
  const f = event.detail ?? {};
  const want = f.kind === "banner" && !f.pilot;
  const q = (f.q ?? "").trim();
  section.hidden = !want;
  if (!want) { shown = false; return; }
  if (shown && q === filter.q) return;
  shown = true;
  filter.q = q;
  void loadPage(false);
});
$("bsort").addEventListener("change", () => { filter.sort = $("bsort").value; void loadPage(false); });
if ("IntersectionObserver" in window)
  new IntersectionObserver((entries) => { if (entries.some((e) => e.isIntersecting)) void loadPage(true); }, { rootMargin: "600px" }).observe(sentinel);
$("bmorebtn").addEventListener("click", () => void loadPage(true));
$("bclose").addEventListener("click", () => box.close());
box.addEventListener("click", (e) => { if (e.target === box) box.close(); });
$("bkeep").addEventListener("click", (e) => void keep(false, e.currentTarget));
$("buse").addEventListener("click", (e) => void keep(true, e.currentTarget));
$("bremove").addEventListener("click", () => void takeDown());
$("bbyauthor").addEventListener("click", () => {
  if (!current) return;
  filter.author = current.it.author;
  box.close();
  showChips();
  void loadPage(false);
  section.scrollIntoView({ block: "start" });
});
