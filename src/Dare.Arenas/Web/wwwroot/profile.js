// A pilot's public profile (/pilot/<name>). Everything shown is inserted with textContent (never innerHTML).
"use strict";

const API = "/account/api";
// The shared gallery's page (The Foundry): ?pilot=<name> filters it, #d<id> opens one design.
const GALLERY_PAGE = "/foundry.html";
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
  ctx.font = "700 46px system-ui, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText((name[0] || "?").toUpperCase(), canvas.width / 2, canvas.height / 2 + 2);
}

let profile = null;
let me = null; // the signed-in viewer's name, when signed in

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
    let img;
    if (it.kind === "ship") { // a ship design is a whole rotation sheet: show it nose-up
      img = el("canvas");
      img.width = img.height = 72;
      img.setAttribute("role", "img");
      img.setAttribute("aria-label", it.name || "ship");
      drawShip(img, it.image);
    } else {
      img = el("img");
      img.src = it.image;
      img.alt = it.name || it.kindLabel || "design";
      img.loading = "lazy";
    }
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

// ---- the wall: dated messages pilots leave here (their own too), replies under them, @mentions ------------
let wallNext = null; // the id to page from (older threads), or null when there are none
let wallMax = 300;
let canPost = false;
const stamp = new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
const mentionText = (node, text, names) => globalThis.foundationMentionText ? globalThis.foundationMentionText(node, text, names) : (node.textContent = text);

function wallItem(m, isReply) {
  const li = el("li", isReply ? "wallpost reply" : "wallpost");
  li.id = "post-" + m.id;
  li.dataset.id = m.id;
  const head = el("div", "wallhead");
  const who = el("a", "pilotlink wallauthor", m.author);
  who.href = "/pilot/" + encodeURIComponent(m.author);
  const when = el("time", "dim", stamp.format(new Date(m.at)));
  when.dateTime = m.at;
  head.append(who, when);
  const tools = el("span", "walltools");
  if (canPost) {
    const r = el("button", "ghost small wallreply", "Reply");
    r.type = "button";
    r.addEventListener("click", () => replyBox(isReply ? li.closest("li.wallpost:not(.reply)") : li, m));
    tools.append(r);
  }
  if (m.canRemove) {
    const x = el("button", "ghost small wallremove", "Remove");
    x.type = "button";
    x.setAttribute("aria-label", `Remove ${m.author}'s message`);
    x.addEventListener("click", async () => {
      x.disabled = true;
      const body = await post("/profile/wall/remove", { id: m.id });
      if (body.ok) { li.remove(); if (!isReply) wallCount(-1); }
      else { x.disabled = false; msg($("wallmsg"), body.message, false); }
    });
    tools.append(x);
  }
  head.append(tools);
  const text = el("p", "walltext");
  mentionText(text, m.text, m.mentions);
  li.append(head, text);
  if (!isReply) {
    const replies = el("ol", "wallreplies");
    for (const r of m.replies ?? []) replies.append(wallItem(r, true));
    li.append(replies);
  }
  return li;
}

// the reply box under a thread: "@Author " to start, replying to message m
function replyBox(thread, m) {
  thread.querySelector(":scope > form.replyform")?.remove();
  const form = el("form", "replyform");
  const ta = el("textarea");
  ta.rows = 2;
  ta.maxLength = wallMax;
  ta.setAttribute("aria-label", `Reply to ${m.author}`);
  ta.value = m.author === me ? "" : `@${m.author} `;
  const row = el("div", "wallrow");
  const note = el("p", "msg");
  const send = el("button", "small", "Reply");
  send.type = "submit";
  const cancel = el("button", "ghost small", "Cancel");
  cancel.type = "button";
  cancel.addEventListener("click", () => form.remove());
  const btns = el("span", "replybtns");
  btns.append(cancel, send);
  row.append(note, btns);
  form.append(ta, row);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const text = ta.value.trim();
    if (!text) return;
    send.disabled = true;
    const body = await post("/profile/wall", { name: profile.name, text, replyTo: m.id });
    send.disabled = false;
    if (!body.ok) { msg(note, body.message, false); return; }
    thread.querySelector(":scope > ol.wallreplies").append(wallItem(body.post, true));
    form.remove();
  });
  ta.addEventListener("keydown", (e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) form.requestSubmit(); });
  thread.append(form);
  globalThis.foundationMentions?.(ta);
  ta.focus();
  ta.setSelectionRange(ta.value.length, ta.value.length);
}

let wallShown = 0;
function wallCount(delta) {
  wallShown += delta;
  $("wallnone").hidden = wallShown > 0;
}

async function loadWall(more) {
  const list = $("wallposts");
  let url = `${API}/profile/wall?name=${encodeURIComponent(profile.name)}`;
  if (more && wallNext != null) url += `&before=${wallNext}`;
  const { body } = await getJson(url);
  if (!body || !body.ok) return;
  $("wall").hidden = false;
  canPost = !!body.canPost;
  if (!more) { list.replaceChildren(); wallShown = 0; }
  wallMax = body.max || wallMax;
  for (const m of body.posts) list.append(wallItem(m, false));
  wallCount(body.posts.length);
  wallNext = body.more && body.posts.length ? body.posts[body.posts.length - 1].id : null;
  $("wallmore").hidden = wallNext == null;
  // anyone signed in can write, here on their own wall too
  const form = $("wallform"), note = $("wallnote");
  form.hidden = !canPost;
  note.hidden = canPost;
  if (!canPost) {
    note.replaceChildren();
    const a = el("a", null, "Sign in");
    a.href = "/account/login?next=" + encodeURIComponent(location.pathname);
    note.append(a, ` to leave ${profile.name} a message.`);
  }
  $("walltext").placeholder = profile.mine ? "Write on your wall (type @ to mention a pilot)" : `Leave ${profile.name} a message (type @ to mention a pilot)`;
  wallLeft();
  if (!more) showLinkedPost();
}

// /pilot/<name>#post-<id> (from a notification): bring that message into view
function showLinkedPost() {
  const m = /^#post-(\d+)$/.exec(location.hash);
  const li = m && document.getElementById("post-" + m[1]);
  if (!li) return;
  li.classList.add("linked");
  li.scrollIntoView({ block: "center" });
}

function wallLeft() {
  const n = $("walltext").value.length;
  $("wallleft").textContent = n ? `${n} / ${wallMax}` : "";
}

async function postWall(e) {
  e.preventDefault();
  const text = $("walltext").value.trim();
  if (!text) return;
  const btn = $("wallpost");
  btn.disabled = true;
  const body = await post("/profile/wall", { name: profile.name, text });
  btn.disabled = false;
  if (!body.ok) { msg($("wallmsg"), body.message, false); return; }
  $("walltext").value = "";
  msg($("wallmsg"), "", true);
  $("wallposts").prepend(wallItem(body.post, false));
  wallCount(1);
  wallLeft();
}

// Message: someone else's profile, signed in, opens a conversation with them in the inbox
function renderMessageButton() {
  const b = $("msgbtn");
  b.hidden = !me || profile.mine;
  b.href = "/account/inbox?to=" + encodeURIComponent(profile.name) + "#messages";
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
    me = (await getJson(`${API}/me`)).body?.name ?? null;
    renderMessageButton();
    await Promise.all([loadWall(false), loadContribs(false)]);
  }
}

$("editbtn").addEventListener("click", openEdit);
$("editcancel").addEventListener("click", closeEdit);
$("edit").addEventListener("submit", saveEdit);
$("biofield").addEventListener("input", countBio);
$("modclear").addEventListener("click", () => moderate({ clearBio: true }));
$("modhide").addEventListener("click", () => moderate({ hidden: !profile.hidden }));
$("contribmore").addEventListener("click", () => loadContribs(true));
$("wallform").addEventListener("submit", postWall);
globalThis.foundationMentions?.($("walltext"));
addEventListener("hashchange", showLinkedPost);
$("walltext").addEventListener("input", wallLeft);
$("walltext").addEventListener("keydown", (e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) $("wallform").requestSubmit(); });
$("wallmore").addEventListener("click", () => loadWall(true));
load();
