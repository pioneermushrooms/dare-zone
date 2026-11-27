// The arena chat panel: the chosen arena's live chat and who is in it, for anyone; a signed-in pilot
// presses Chat to join the arena as a chat client and talk (public, private to a selected player,
// // team, ? commands). Everything is inserted with textContent.
//
// One file for every page that shows it: the home page (site/, as /chat.js; beside the arena list,
// arena-list.js) and the arena editor
// (Dare.Arenas embeds this file and serves it as /arena/chat.js). Both are on the same origin, so the
// API below and the dare_session cookie are the same from either. The panel's data attributes say how
// the page uses it:
//   data-picker="<id>"  the <select> that chooses the arena (default "arena", the home page's picker)
//   data-arenas="fill"  this script fills that select with the zone's arenas
//   data-dock="right"   docked on the right edge, opened and closed with #chatopen (the arena editor);
//                       it follows the arena being edited (the page sends "foundation:editing")
const API = "/arena/api";
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const $ = (id) => document.getElementById(id);

const panel = $("chat");
const picker = $(panel.dataset.picker || "arena");
const docked = panel.dataset.dock === "right";
const lines = $("chatlines");
const form = $("chatform");
const input = $("chatinput");
let arena = null;   // the arena shown
let after = 0;      // last line id seen
let joined = false; // chatting as the pilot in this arena
let target = null;  // private messages go to this player
let timer = null;

const SHIPS = ["spec", "Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];

function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
}

function wanted() { return picker.value || ""; }

function reset() {
  arena = wanted();
  after = 0;
  lines.replaceChildren();
  $("chatarena").textContent = arena || "public";
}

function addLine(m) {
  const li = el("li", "line k-" + m.kind.toLowerCase());
  const when = new Date(m.at);
  li.append(el("time", null, when.toTimeString().slice(0, 5)));
  if (m.from) {
    const who = el("b", "from", m.kind === "PRIV" ? (m.from.startsWith("to ") ? `→ ${m.from.slice(3)}` : `${m.from} →`) : m.from);
    if (m.from && !m.from.startsWith("to ")) {
      who.tabIndex = 0;
      who.title = "Message privately";
      who.addEventListener("click", () => pickTarget(m.from));
    }
    li.append(who);
  }
  li.append(el("span", "text", m.text));
  const atBottom = lines.scrollHeight - lines.scrollTop - lines.clientHeight < 40;
  lines.append(li);
  while (lines.children.length > 200) lines.firstChild.remove();
  if (atBottom) lines.scrollTop = lines.scrollHeight;
}

function renderPlayers(players) {
  $("chatcount").textContent = players.length ? `· ${players.length} here` : "· nobody here";
  const byFreq = new Map();
  for (const p of players) {
    if (!byFreq.has(p.freq)) byFreq.set(p.freq, []);
    byFreq.get(p.freq).push(p);
  }
  const items = [];
  for (const [freq, team] of byFreq) {
    items.push(el("li", "freq", freq >= 8000 ? "Spectating" : `Team ${freq}`));
    for (const p of team) {
      const b = el("button", "player" + (p.name === target ? " picked" : ""), p.name);
      b.type = "button";
      b.title = `${SHIPS[p.ship] ?? ""} · click to message privately`;
      b.append(el("span", "ship", p.ship ? SHIPS[p.ship] : ""));
      b.addEventListener("click", () => pickTarget(p.name));
      const prof = el("a", "prof", "↗");
      prof.href = "/pilot/" + encodeURIComponent(p.name);
      prof.title = `${p.name}'s profile`;
      prof.setAttribute("aria-label", `${p.name}'s profile`);
      const li = el("li");
      li.append(b, prof);
      items.push(li);
    }
  }
  $("chatplayers").replaceChildren(...items);
}

function pickTarget(name) {
  target = name === target ? null : name;
  $("chatto").hidden = !target;
  $("chattoname").textContent = target ?? "";
  if (joined) input.focus();
}

// One poll at a time. A poll asks for the lines after `after`, and `after` only moves when its answer
// comes back; two polls in flight at once (the 2-second timer and the one right after Send, with a
// poll taking a second or more) both got the same new lines and showed them twice. A poll asked for
// while one is running runs once more when it ends, so nothing waits a whole interval.
let inflight = null, again = false;
function poll() {
  if (inflight) { again = true; return inflight; }
  inflight = (async () => {
    try {
      do { again = false; await pollOnce(); } while (again);
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

async function pollOnce() {
  if ($("entry")?.hidden) return; // in the game: the panel rests
  if (docked && panel.hidden) return; // closed: nothing to show
  if (arena !== wanted()) reset();
  const at = arena;
  try {
    const r = await fetch(`${API}/chat?arena=${encodeURIComponent(at)}&after=${after}`, { credentials: "same-origin", cache: "no-store" });
    const data = await r.json().catch(() => ({}));
    if (at !== arena) return;
    if (!r.ok || !data.ok) {
      $("chatnote").textContent = data.message || "Chat is unavailable right now.";
      return;
    }
    // a bridge in another arena (the pilot picked another one): show this arena publicly
    const chat = data.chat;
    joined = !!chat && chat.state !== "closed" && data.mine;
    if (data.mine && data.last < after) after = 0; // a new chat session
    for (const m of data.messages) if (!(m.id <= after)) addLine(m); // never a line already shown
    after = Math.max(after, data.last);
    renderPlayers(data.players);
    form.hidden = !joined;
    $("chatjoin").hidden = joined;
    if (chat && chat.state === "closed" && chat.error && data.mine) $("chatnote").textContent = chat.error;
    else if (!joined) $("chatnote").textContent = "Read along, or join to talk.";
  } catch {
    $("chatnote").textContent = "Chat is unavailable right now.";
  }
}

async function join() {
  const go = $("chatgo");
  go.disabled = true;
  try {
    // the home page signs in through the game's dialog; elsewhere the account session is the sign-in
    if (globalThis.foundationSignIn && !(await globalThis.foundationSignIn())) return;
    const r = await fetch(`${API}/chat/join`, { method: "POST", credentials: "same-origin", headers: HEADERS, body: JSON.stringify({ arena: wanted() }) });
    const data = await r.json().catch(() => ({}));
    if (r.status === 401 && !globalThis.foundationSignIn) {
      location.href = "/account/login?next=" + encodeURIComponent(location.pathname + location.search + location.hash);
      return;
    }
    if (!r.ok || !data.ok) { $("chatnote").textContent = data.message || "Couldn't join the chat."; return; }
    $("chatnote").textContent = "Joining…";
    reset();
    setTimeout(poll, 600);
  } finally {
    go.disabled = false;
  }
}

async function leave() {
  try { await fetch(`${API}/chat/leave`, { method: "POST", credentials: "same-origin", headers: HEADERS, body: "{}" }); } catch {}
  joined = false;
  form.hidden = true;
  $("chatjoin").hidden = false;
  reset();
  void poll();
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const text = input.value.trim();
  if (!text) return;
  const r = await fetch(`${API}/chat/say`, { method: "POST", credentials: "same-origin", headers: HEADERS, body: JSON.stringify({ text, to: target }) });
  const data = await r.json().catch(() => ({}));
  if (r.ok && data.ok) { input.value = ""; void poll(); }
  else addLine({ at: new Date().toISOString(), kind: "ERROR", from: null, text: data.message || "Not sent." });
});

$("chatgo").addEventListener("click", join);
$("chatleave").addEventListener("click", leave);
$("chattoclear").addEventListener("click", () => pickTarget(target));
picker.addEventListener("change", () => { if (joined) void leave(); else { reset(); void poll(); } });
// Hide folds the chat away to a slim tab; the arena list stays, so you can still pick where to fly.
// (Docked, Hide closes the panel instead: below.)
if (!docked) $("chatfold").addEventListener("click", () => {
  const before = panel.getBoundingClientRect();
  const open = panel.classList.toggle("folded") === false;
  $("chatfold").textContent = open ? "Hide" : "Chat";
  $("chatfold").setAttribute("aria-expanded", String(open));
  if (open) applySaved();
  else if (panel.style.left) panel.style.left = before.right - panel.getBoundingClientRect().width + "px"; // keep the right edge
  dockEdges();
});

// Resizing: drag any side or corner. The first drag pins the panel by left/top/width/height; the size is
// remembered (per browser) and kept inside the window. Double-click a handle for the original size.
const SIZE_KEY = "foundation.chatSize";
const MIN_W = 580, MIN_H = 260, EDGE = 8; // arena list 200 + players 140 + room for the chat and Send
const wide = matchMedia("(min-width: 861px)");

function clampBox(b) {
  const vw = innerWidth, vh = innerHeight;
  const w = Math.min(Math.max(b.w, MIN_W), vw - 2 * EDGE), h = Math.min(Math.max(b.h, MIN_H), vh - 2 * EDGE);
  const x = Math.min(Math.max(b.x, EDGE), vw - EDGE - w), y = Math.min(Math.max(b.y, EDGE), vh - EDGE - h);
  return { x, y, w, h };
}
function placePanel(b) {
  Object.assign(panel.style, { left: b.x + "px", top: b.y + "px", width: b.w + "px", height: b.h + "px", right: "auto", bottom: "auto" });
}
function unplacePanel() {
  for (const k of ["left", "top", "width", "height", "right", "bottom"]) panel.style.removeProperty(k);
}
function savedBox() {
  try { const b = JSON.parse(localStorage.getItem(SIZE_KEY)); return b && [b.x, b.y, b.w, b.h].every(Number.isFinite) ? b : null; }
  catch { return null; }
}
function applySaved() {
  const b = wide.matches ? savedBox() : null;
  if (b) placePanel(clampBox(b)); else unplacePanel();
  dockEdges();
}
// Where the panel is, for the page to keep the Next stop card clear of it (the header's buttons stay put).
function dockEdges() {
  const r = panel.getBoundingClientRect(), root = document.documentElement.style;
  if (!wide.matches) { root.removeProperty("--dock-w"); root.removeProperty("--dock-right"); return; }
  if (panel.classList.contains("folded")) root.removeProperty("--dock-w"); // too narrow to hold them: just share the right edge
  else root.setProperty("--dock-w", Math.round(r.width) + "px");
  root.setProperty("--dock-right", Math.round(innerWidth - r.right) + "px");
}

if (!docked) for (const dir of ["n", "s", "e", "w", "nw", "ne", "sw", "se"]) {
  const grip = el("div", `grip grip-${dir}`);
  grip.setAttribute("aria-hidden", "true");
  grip.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    grip.setPointerCapture(event.pointerId);
    const r = panel.getBoundingClientRect();
    const start = { x: r.left, y: r.top, w: r.width, h: r.height }, px = event.clientX, py = event.clientY;
    panel.classList.add("resizing");
    const move = (e) => {
      const dx = e.clientX - px, dy = e.clientY - py, b = { ...start };
      if (dir.includes("e")) b.w = start.w + dx;
      if (dir.includes("s")) b.h = start.h + dy;
      if (dir.includes("w")) { b.w = Math.max(MIN_W, start.w - dx); b.x = start.x + start.w - b.w; }
      if (dir.includes("n")) { b.h = Math.max(MIN_H, start.h - dy); b.y = start.y + start.h - b.h; }
      placePanel(clampBox(b));
      dockEdges();
    };
    const end = () => {
      grip.removeEventListener("pointermove", move);
      panel.classList.remove("resizing");
      const q = panel.getBoundingClientRect();
      try { localStorage.setItem(SIZE_KEY, JSON.stringify({ x: q.left, y: q.top, w: q.width, h: q.height })); } catch {}
    };
    grip.addEventListener("pointermove", move);
    grip.addEventListener("pointerup", end, { once: true });
    grip.addEventListener("pointercancel", end, { once: true });
  });
  grip.addEventListener("dblclick", () => {
    try { localStorage.removeItem(SIZE_KEY); } catch {}
    unplacePanel();
    dockEdges();
  });
  panel.append(grip);
}
if (!docked) {
  applySaved();
  addEventListener("resize", applySaved);
  wide.addEventListener("change", applySaved);
}

// ---- docked (the arena editor): a column on the right edge that the page makes room for -------------
// #chatopen opens it, Hide (#chatfold) closes it; open or closed and the width are remembered. The width
// is dragged from the left edge (double-click it for the original width). The page's CSS reads the
// width from --chat-dock-w on <html> and the class chat-docked-open on <html>.
const DOCK_OPEN_KEY = "foundation.chatDockOpen", DOCK_W_KEY = "foundation.chatDockWidth";
const DOCK_MIN = 300, DOCK_MAX = 720, DOCK_DEFAULT = 400;
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch {} },
};

function dockWidth(w) {
  const max = Math.min(DOCK_MAX, Math.round(innerWidth * 0.5));
  const px = Math.round(Math.min(Math.max(w, DOCK_MIN), Math.max(DOCK_MIN, max)));
  document.documentElement.style.setProperty("--chat-dock-w", px + "px");
  return px;
}
function setDockOpen(open, focus) {
  panel.hidden = !open;
  document.documentElement.classList.toggle("chat-docked-open", open);
  const opener = $("chatopen");
  if (opener) { opener.setAttribute("aria-expanded", String(open)); opener.hidden = open; }
  $("chatfold").setAttribute("aria-expanded", String(open));
  store.set(DOCK_OPEN_KEY, open ? "1" : null);
  if (open) { void poll(); if (focus) $("chatfold").focus(); }
  else if (focus && opener) opener.focus();
}

// The arenas to choose from: the zone's arenas (public is "", arena 0), plus whatever the page asks to show.
let fillTimer = 0;
async function fillArenas() {
  try {
    const r = await fetch(`${API}/arenas`, { credentials: "same-origin", cache: "no-store" });
    const data = await r.json().catch(() => ({}));
    if (!r.ok || !data.ok) return;
    const keep = picker.value;
    const opts = [el("option", null, "public")];
    opts[0].value = "";
    for (const a of data.arenas) {
      if (a.name === "0") { opts[0].textContent = a.players ? `public · ${a.players}` : "public"; continue; }
      const o = el("option", null, a.players ? `${a.name} · ${a.players}` : a.name);
      o.value = a.name;
      opts.push(o);
    }
    // the arena being edited stays choosable even when it isn't running
    if (editing && !opts.some((o) => o.value === editing)) { const o = el("option", null, editing); o.value = editing; opts.splice(1, 0, o); }
    picker.replaceChildren(...opts);
    picker.value = opts.some((o) => o.value === keep) ? keep : "";
  } catch {}
}

// The arena editor says which arena is being edited ("" for the public arenas, null for a draft, which has
// no public chat). The chat follows it until the pilot picks another arena by hand.
let editing = null, following = true;
function followEditing(name) {
  editing = name;
  if (!following || name == null || joined) return;
  if (![...picker.options].some((o) => o.value === name)) { const o = el("option", null, name || "public"); o.value = name; picker.append(o); }
  if (picker.value !== name) { picker.value = name; reset(); void poll(); }
}

if (docked) {
  dockWidth(Number(store.get(DOCK_W_KEY)) || DOCK_DEFAULT);
  addEventListener("resize", () => dockWidth(Number(store.get(DOCK_W_KEY)) || DOCK_DEFAULT));
  $("chatfold").textContent = "Hide";
  $("chatfold").addEventListener("click", () => setDockOpen(false, true));
  $("chatopen")?.addEventListener("click", () => setDockOpen(true, true));
  picker.addEventListener("change", () => { following = picker.value === editing; });
  addEventListener("foundation:editing", (e) => followEditing(e.detail?.arena ?? null));
  if (document.documentElement.dataset.editingArena !== undefined) followEditing(document.documentElement.dataset.editingArena || "");

  const grip = el("div", "grip grip-w");
  grip.setAttribute("aria-hidden", "true");
  grip.title = "Drag to resize; double-click for the original width";
  grip.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    grip.setPointerCapture(event.pointerId);
    const startW = panel.getBoundingClientRect().width, px = event.clientX;
    panel.classList.add("resizing");
    const move = (e) => dockWidth(startW + (px - e.clientX));
    const end = () => {
      grip.removeEventListener("pointermove", move);
      panel.classList.remove("resizing");
      store.set(DOCK_W_KEY, String(Math.round(panel.getBoundingClientRect().width)));
    };
    grip.addEventListener("pointermove", move);
    grip.addEventListener("pointerup", end, { once: true });
    grip.addEventListener("pointercancel", end, { once: true });
  });
  grip.addEventListener("dblclick", () => { store.set(DOCK_W_KEY, null); dockWidth(DOCK_DEFAULT); });
  panel.append(grip);
  setDockOpen(store.get(DOCK_OPEN_KEY) === "1", false);
}
if (panel.dataset.arenas === "fill") {
  void fillArenas();
  fillTimer = setInterval(() => { if (!document.hidden && !panel.hidden) void fillArenas(); }, 30000);
}

// Entering the game leaves the chat first: the same name can't be in the zone twice.
globalThis.foundationChatLeave = async () => { if (joined) await leave(); };

reset();
void poll();
timer = setInterval(() => { if (!document.hidden) void poll(); }, 2000);
