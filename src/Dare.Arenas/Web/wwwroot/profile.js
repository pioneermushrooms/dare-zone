// A pilot's public profile (/pilot/<name>). Everything shown is inserted with textContent (never innerHTML).
"use strict";

const API = "/account/api";
// The shared gallery's page; "" until it has a fixed address (then contributions link into it).
const GALLERY_PAGE = "";
const PAGE_SIZE = 12;

const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
};

const pilotName = (() => {
  const raw = location.pathname.replace(/^\/pilot\//, "").replace(/\/+$/, "");
  try { return decodeURIComponent(raw); } catch { return raw; }
})();

async function getJson(url) {
  try {
    const r = await fetch(url, { credentials: "same-origin" });
    let body = null;
    try { body = await r.json(); } catch { /* not JSON */ }
    return { status: r.status, body };
  } catch { return { status: 0, body: null }; }
}

async function post(path, body) {
  try {
    const r = await fetch(API + path, {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" },
      body: JSON.stringify(body || {}),
    });
    try { return await r.json(); } catch { return { ok: false, message: "HTTP " + r.status }; }
  } catch { return { ok: false, message: "Network error; try again." }; }
}

function msg(node, text, ok) { node.textContent = text || ""; node.className = "msg " + (ok ? "ok" : "bad"); }

const shipUrl = (id) => `${API}/profile/ship?name=${encodeURIComponent(profile.name)}&id=${id}`;

// A ship sheet is 10 x 4 frames; frame 0 (top left) is the ship nose-up.
function drawShip(canvas, url) {
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  const img = new Image();
  img.onload = () => {
    const fw = img.width / 10, fh = img.height / 4;
    ctx.imageSmoothingEnabled = false;
    const s = Math.min(canvas.width / fw, canvas.height / fh);
    const w = fw * s, h = fh * s;
    ctx.drawImage(img, 0, 0, fw, fh, (canvas.width - w) / 2, (canvas.height - h) / 2, w, h);
  };
  img.src = url;
}

function drawInitial(canvas, name) {
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#6ee7ff";
  ctx.font = "700 34px system-ui, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText((name[0] || "?").toUpperCase(), canvas.width / 2, canvas.height / 2 + 2);
}

let profile = null;

function since(iso) {
  const d = new Date(iso);
  return isNaN(d) ? "" : "Pilot since " + d.toLocaleDateString(undefined, { month: "long", year: "numeric" });
}

function renderHead() {
  const p = profile;
  document.title = `${p.name} — SubSpace Foundation`;
  $("pname").textContent = p.name;
  $("since").textContent = since(p.since);
  $("bio").textContent = p.bio;
  $("bio").hidden = !p.bio;
  $("hiddennote").hidden = !p.hidden;
  const av = $("avatar");
  av.classList.toggle("ship", p.avatar != null);
  if (p.avatar != null) drawShip(av, shipUrl(p.avatar)); else drawInitial(av, p.name);
  $("editbtn").hidden = !p.mine;
  $("modbox").hidden = !p.staff || p.mine;
  $("modhide").textContent = p.hidden ? "Show profile" : "Hide profile";
  $("modhide").className = (p.hidden ? "ghost" : "hot") + " small";
}

function renderArenas() {
  const p = profile;
  const show = p.show.arenas || p.mine;
  $("arenas").hidden = !show;
  if (!show) return;
  const list = $("arenalist");
  list.replaceChildren(...p.arenas.map((a) => el("li", "chip", a)));
  $("arenanone").hidden = p.arenas.length > 0;
  $("arenas").querySelector("h2").textContent = p.show.arenas ? "Arenas" : "Arenas (hidden from others)";
}

function renderShips() {
  const p = profile;
  $("ships").hidden = !p.ships.length;
  $("shiplist").replaceChildren(...p.ships.map((s) => {
    const li = el("li", "tile");
    const c = el("canvas");
    c.width = c.height = 72;
    drawShip(c, shipUrl(s.id));
    li.append(c, el("span", "cap", s.name));
    return li;
  }));
}

// Contributions come from the shared gallery (its API); this section stays out of the way if the gallery is not there.
let contribNext = null;
async function loadContribs(more) {
  const p = profile;
  if (!p.show.contribs && !p.mine) return;
  let url = `${API}/gallery?pilot=${encodeURIComponent(p.name)}&limit=${PAGE_SIZE}`;
  if (more && contribNext != null) url += `&before=${encodeURIComponent(contribNext)}`;
  const { body } = await getJson(url);
  if (!body || !body.ok || !Array.isArray(body.items)) return;
  $("contribs").hidden = false;
  if (!p.show.contribs) $("contribs").querySelector("h2").firstChild.textContent = "Contributions (hidden from others) ";
  $("contribcount").textContent = body.count ? `(${body.count})` : "";
  const list = $("contriblist");
  for (const it of body.items) {
    const li = el("li", "tile");
    const img = el("img");
    img.src = it.image;
    img.alt = it.name || it.kindLabel || "design";
    img.loading = "lazy";
    const cap = el("span", "cap", it.name || it.kindLabel || "");
    if (GALLERY_PAGE) {
      const a = el("a");
      a.href = `${GALLERY_PAGE}?pilot=${encodeURIComponent(p.name)}#d${it.id}`;
      a.append(img, cap);
      li.append(a);
    } else li.append(img, cap);
    if (it.kindLabel) li.append(el("span", "kind", it.kindLabel));
    list.append(li);
  }
  contribNext = body.next ?? null;
  $("contribmore").hidden = contribNext == null;
  $("contribnone").hidden = list.children.length > 0;
  if (GALLERY_PAGE && body.count) {
    $("contriball").href = `${GALLERY_PAGE}?pilot=${encodeURIComponent(p.name)}`;
    $("contriball").hidden = false;
  }
}

// ---- owner editing -----------------------------------------------------------------------------
function openEdit() {
  const p = profile;
  $("biofield").value = p.bio;
  countBio();
  const sel = $("avatarfield");
  sel.replaceChildren(el("option", null, "None"));
  sel.firstChild.value = "0";
  for (const s of p.own || []) {
    const o = el("option", null, s.name);
    o.value = String(s.id);
    sel.append(o);
  }
  sel.value = String(p.avatar ?? 0);
  $("showContribs").checked = p.show.contribs;
  $("showArenas").checked = p.show.arenas;
  $("showShips").checked = p.show.ships;
  msg($("editmsg"), "", true);
  $("edit").hidden = false;
  $("editbtn").hidden = true;
  $("biofield").focus();
}

function closeEdit() { $("edit").hidden = true; $("editbtn").hidden = !profile.mine; }

function countBio() { $("biocount").textContent = `${$("biofield").value.length} / 280`; }

async function saveEdit(e) {
  e.preventDefault();
  const btn = $("edit").querySelector("button[type=submit]");
  btn.disabled = true;
  msg($("editmsg"), "…", true);
  const body = await post("/profile", {
    bio: $("biofield").value,
    avatar: Number($("avatarfield").value) || 0,
    showContribs: $("showContribs").checked,
    showArenas: $("showArenas").checked,
    showShips: $("showShips").checked,
  });
  btn.disabled = false;
  msg($("editmsg"), body.message, body.ok);
  if (body.ok) { await load(); closeEdit(); }
}

async function moderate(change) {
  const body = await post("/profile/moderate", { name: profile.name, ...change });
  msg($("modmsg"), body.message, body.ok);
  if (body.ok) await load();
}

// ---- load --------------------------------------------------------------------------------------
async function load() {
  const { status, body } = await getJson(`${API}/profile?name=${encodeURIComponent(pilotName)}`);
  $("loading").hidden = true;
  if (!body || !body.ok) {
    $("missing").hidden = false;
    if (status === 0) $("missing").querySelector("p").textContent = "Could not reach the server; try again.";
    return;
  }
  const first = profile == null;
  profile = body;
  // the canonical address uses the stored casing
  const canonical = "/pilot/" + encodeURIComponent(body.name);
  if (location.pathname !== canonical) history.replaceState(null, "", canonical + location.search + location.hash);
  $("page").hidden = false;
  renderHead();
  renderArenas();
  renderShips();
  if (first) {
    await loadContribs(false);
    if (body.mine) $("acctlink").textContent = "Your account";
  }
}

$("editbtn").addEventListener("click", openEdit);
$("editcancel").addEventListener("click", closeEdit);
$("edit").addEventListener("submit", saveEdit);
$("biofield").addEventListener("input", countBio);
$("modclear").addEventListener("click", () => moderate({ clearBio: true }));
$("modhide").addEventListener("click", () => moderate({ hidden: !profile.hidden }));
$("contribmore").addEventListener("click", () => loadContribs(true));
load();
