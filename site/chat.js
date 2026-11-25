// The home page's chat panel: the chosen arena's live chat and who is in it, for anyone; a
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
      const li = el("li");
      li.append(b);
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
$("chatfold").addEventListener("click", () => {
  const open = panel.classList.toggle("folded") === false;
  $("chatfold").textContent = open ? "Hide" : "Chat";
  $("chatfold").setAttribute("aria-expanded", String(open));
});

// Entering the game leaves the chat first: the same name can't be in the zone twice.
globalThis.foundationChatLeave = async () => { if (joined) await leave(); };

reset();
void poll();
timer = setInterval(() => { if (!document.hidden) void poll(); }, 2000);
