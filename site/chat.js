// The home page's chat panel (beside the arena list, arenas.js): the chosen arena's live chat and who is in it, for anyone; a
// signed-in pilot presses Chat to join the arena as a chat client and talk (public, private to
// a selected player, // team, ? commands). Everything is inserted with textContent.
const API = "/arena/api";
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const $ = (id) => document.getElementById(id);

const panel = $("chat");
const picker = $("arena");
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

async function poll() {
  if ($("entry").hidden) return; // in the game: the panel rests
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
    for (const m of data.messages) addLine(m);
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
    const me = await globalThis.foundationSignIn?.();
    if (!me) return;
    const r = await fetch(`${API}/chat/join`, { method: "POST", credentials: "same-origin", headers: HEADERS, body: JSON.stringify({ arena: wanted() }) });
    const data = await r.json().catch(() => ({}));
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
$("chatfold").addEventListener("click", () => {
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
const MIN_W = 520, MIN_H = 260, EDGE = 8; // the arena list and the chat side by side
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
// The header's buttons line up with the panel: its left and right edges, wherever it's been sized to.
function dockEdges() {
  const r = panel.getBoundingClientRect(), root = document.documentElement.style;
  if (!wide.matches) { root.removeProperty("--dock-w"); root.removeProperty("--dock-right"); return; }
  if (panel.classList.contains("folded")) root.removeProperty("--dock-w"); // too narrow to hold them: just share the right edge
  else root.setProperty("--dock-w", Math.round(r.width) + "px");
  root.setProperty("--dock-right", Math.round(innerWidth - r.right) + "px");
}

for (const dir of ["n", "s", "e", "w", "nw", "ne", "sw", "se"]) {
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
applySaved();
addEventListener("resize", applySaved);
wide.addEventListener("change", applySaved);

// Entering the game leaves the chat first: the same name can't be in the zone twice.
globalThis.foundationChatLeave = async () => { if (joined) await leave(); };

reset();
void poll();
timer = setInterval(() => { if (!document.hidden) void poll(); }, 2000);
