// The inbox (/account/inbox): direct messages with other pilots, and notifications (wall messages, replies,
// @mentions). Signed-in only. #messages or #notes picks the tab; ?to=<name> opens a conversation with that pilot.
// Everything shown is inserted with textContent (never innerHTML).
"use strict";

const API = "/account/api";
const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
};
const stamp = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
const SAID = { wall: "wrote on your wall", reply: "replied to you", mention: "mentioned you" };

async function getJson(url) {
  try {
    const r = await fetch(url, { credentials: "same-origin", cache: "no-store" });
    let body = null;
    try { body = await r.json(); } catch {}
    return { status: r.status, body };
  } catch { return { status: 0, body: null }; }
}
async function post(path, body) {
  try {
    const r = await fetch(API + path, { method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" }, body: JSON.stringify(body || {}) });
    try { return await r.json(); } catch { return { ok: false, message: "HTTP " + r.status }; }
  } catch { return { ok: false, message: "Network error; try again." }; }
}
function ago(iso) {
  const s = (Date.now() - new Date(iso)) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return stamp.format(new Date(iso));
}

// ---- tabs ---------------------------------------------------------------------------------------
function showTab(name) {
  for (const b of document.querySelectorAll(".inboxtabs .settab")) b.setAttribute("aria-selected", String(b.dataset.tab === name));
  for (const p of document.querySelectorAll("[data-pane]")) p.hidden = p.dataset.pane !== name;
  history.replaceState(null, "", location.pathname + location.search + "#" + name);
  if (name === "notes") void readNotes();
}
for (const b of document.querySelectorAll(".inboxtabs .settab")) b.addEventListener("click", () => showTab(b.dataset.tab));

function setCount(node, n) { node.hidden = !n; node.textContent = n ? String(n) : ""; }

// ---- notifications ------------------------------------------------------------------------------
function renderNotes(notes) {
  const list = $("notelist");
  list.replaceChildren(...notes.map((n) => {
    const li = el("li", "note" + (n.read ? "" : " unread"));
    const a = el("a");
    a.href = `/pilot/${encodeURIComponent(n.wall)}#post-${n.post}`;
    const head = el("div", "notehead");
    head.append(el("b", null, n.actor), document.createTextNode(` ${SAID[n.kind] ?? "wrote"}`));
    if (n.kind !== "wall") head.append(document.createTextNode(` on ${n.wall === me ? "your" : n.wall + "'s"} wall`));
    head.append(el("time", "dim", ago(n.at)));
    a.append(head, el("p", "notetext", n.text));
    li.append(a);
    return li;
  }));
  $("notenone").hidden = notes.length > 0;
}
let notesUnread = 0;
async function readNotes() {
  if (!notesUnread) return;
  await post("/inbox/read");
  notesUnread = 0;
  setCount($("notecount"), 0);
  globalThis.foundationCheckInbox?.();
}

// ---- conversations ------------------------------------------------------------------------------
let me = null, open = null, oldest = null;
function renderConvs(convs) {
  const list = $("convlist");
  list.replaceChildren(...convs.map((c) => {
    const li = el("li");
    const b = el("button", "conv" + (c.unread ? " unread" : "") + (open && c.with.toLowerCase() === open.toLowerCase() ? " current" : ""));
    b.type = "button";
    const top = el("span", "convtop");
    top.append(el("b", null, c.with), el("time", "dim", ago(c.at)));
    b.append(top, el("span", "convlast", (c.fromMe ? "You: " : "") + c.last));
    if (c.unread) b.append(el("span", "convunread", String(c.unread)));
    b.addEventListener("click", () => void openThread(c.with));
    li.append(b);
    return li;
  }));
  $("convnone").hidden = convs.length > 0;
}

function msgItem(m) {
  const li = el("li", "dm" + (m.mine ? " mine" : ""));
  li.dataset.id = m.id;
  li.append(el("p", "dmtext", m.text));
  const t = el("time", "dim", stamp.format(new Date(m.at)));
  t.dateTime = m.at;
  li.append(t);
  return li;
}

async function openThread(name, { older = false } = {}) {
  const url = `${API}/messages?with=${encodeURIComponent(name)}` + (older && oldest ? `&before=${oldest}` : "");
  const { status, body } = await getJson(url);
  if (status === 401) return signIn();
  if (!body || !body.ok) { $("sendmsg").textContent = body?.message || "Couldn't open that conversation."; $("sendmsg").className = "msg bad"; return; }
  const list = $("msglist");
  if (!older) {
    open = body.with;
    list.replaceChildren();
    $("toform").hidden = true;
    $("threadwith").textContent = open;
    const prof = $("threadprofile");
    prof.hidden = false;
    prof.href = "/pilot/" + encodeURIComponent(open);
    $("compose").hidden = false;
    $("threadempty").hidden = body.messages.length > 0;
    $("threadempty").textContent = `No messages with ${open} yet. Say hello.`;
    $("sendmsg").textContent = "";
    const q = new URLSearchParams(location.search);
    q.set("to", open);
    history.replaceState(null, "", location.pathname + "?" + q + "#messages");
    $("msgtext").focus();
  }
  const items = body.messages.map(msgItem);
  if (older) list.prepend(...items); else list.append(...items);
  if (body.messages.length) oldest = body.messages[0].id;
  $("older").hidden = !body.more;
  if (!older) list.scrollTop = list.scrollHeight;
  void refresh(); // its messages are read now
}

async function send(e) {
  e.preventDefault();
  const text = $("msgtext").value.trim();
  if (!text || !open) return;
  $("send").disabled = true;
  const body = await post("/messages", { to: open, text });
  $("send").disabled = false;
  if (!body.ok) { $("sendmsg").textContent = body.message; $("sendmsg").className = "msg bad"; return; }
  $("msgtext").value = "";
  $("sendmsg").textContent = "";
  $("threadempty").hidden = true;
  const list = $("msglist");
  list.append(msgItem(body.sent));
  list.scrollTop = list.scrollHeight;
  void refresh();
}

function newMessage() {
  open = null;
  $("msglist").replaceChildren();
  $("compose").hidden = true;
  $("older").hidden = true;
  $("threadprofile").hidden = true;
  $("threadwith").textContent = "New message";
  $("threadempty").hidden = true;
  $("toform").hidden = false;
  $("to").value = "";
  $("to").focus();
  for (const b of document.querySelectorAll(".conv.current")) b.classList.remove("current");
}

// ---- load ---------------------------------------------------------------------------------------
function signIn() { location.href = "/account/login?next=" + encodeURIComponent(location.pathname + location.search + location.hash); }

async function refresh() {
  const { status, body } = await getJson(`${API}/inbox`);
  if (status === 401) return signIn();
  if (!body || !body.ok) return;
  notesUnread = body.counts.notes;
  setCount($("notecount"), body.counts.notes);
  setCount($("msgcount"), body.counts.messages);
  renderNotes(body.notes);
  renderConvs(body.conversations);
  if ($("tab-notes").getAttribute("aria-selected") === "true") void readNotes();
  globalThis.foundationCheckInbox?.();
}

async function load() {
  const who = await getJson(`${API}/me`);
  if (who.status === 401 || !who.body?.name) return signIn();
  me = who.body.name;
  $("loading").hidden = true;
  $("page").hidden = false;
  await refresh();
  const to = new URLSearchParams(location.search).get("to");
  showTab(location.hash === "#notes" ? "notes" : "messages");
  if (to) await openThread(to);
}

$("compose").addEventListener("submit", send);
$("msgtext").addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("compose").requestSubmit(); } });
$("newmsg").addEventListener("click", newMessage);
$("toform").addEventListener("submit", (e) => { e.preventDefault(); const n = $("to").value.trim(); if (n) void openThread(n); });
$("older").addEventListener("click", () => open && openThread(open, { older: true }));
globalThis.foundationMentions?.($("to"), { at: false });
globalThis.foundationMentions?.($("msgtext"));
// a new message while this page is open: the header noticed; bring the list (and the open conversation) up to date
let lastMsgs = null;
addEventListener("foundation:inbox", (e) => {
  const n = e.detail?.messages ?? 0;
  if (lastMsgs != null && n > lastMsgs) { void refresh(); if (open) void openThread(open); }
  lastMsgs = n;
});
load();
