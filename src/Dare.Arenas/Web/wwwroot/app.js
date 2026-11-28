// Foundation arena pages: public list, player hub (/arena/me), staff view (/arena/staff).
// All data is inserted with textContent (never innerHTML).
"use strict";

const API = "/arena/api";
const HDR = { "X-Requested-With": "dare-arena" };

function el(tag, props, ...children) {
  const e = document.createElement(tag);
  if (props) for (const [k, v] of Object.entries(props)) {
    if (k === "class") e.className = v; else if (k === "text") e.textContent = v; else e.setAttribute(k, v);
  }
  for (const c of children) if (c != null) e.append(c);
  return e;
}
const $ = (id) => document.getElementById(id);
const when = (t) => t ? new Date(t).toISOString().slice(0, 16).replace("T", " ") + " UTC" : "";
function fill(list, items, render, empty) {
  list.replaceChildren(...(items.length ? items.map(render) : [el("li", { class: "dim", text: empty })]));
}
function msg(node, text, ok) { node.textContent = text || ""; node.className = "msg " + (ok ? "ok" : "bad"); }

async function getJson(url) {
  const r = await fetch(url, { credentials: "same-origin" });
  let body = null;
  try { body = await r.json(); } catch { /* not json */ }
  return { status: r.status, body };
}
async function post(url, body, type) {
  const headers = Object.assign({}, HDR);
  if (type) headers["Content-Type"] = type;
  const r = await fetch(url, { method: "POST", credentials: "same-origin", headers, body });
  let data = null;
  try { data = await r.json(); } catch { data = { ok: false, message: "HTTP " + r.status }; }
  return { status: r.status, body: data };
}
const postJson = (path, obj) => post(API + path, JSON.stringify(obj), "application/json");

// ---- views: the side menu shows one <section data-view> at a time (location.hash) ----------------
function showView(name, fallback) {
  const views = [...document.querySelectorAll("section[data-view]")];
  const allowed = (v) => !document.querySelector(`nav.side a[data-view="${v}"]`)?.hidden;
  if (!views.some((s) => s.dataset.view === name) || !allowed(name)) name = fallback;
  for (const s of views) s.hidden = s.dataset.view !== name;
  for (const a of document.querySelectorAll("nav.side a[data-view]"))
    if (a.dataset.view === name) a.setAttribute("aria-current", "page"); else a.removeAttribute("aria-current");
  return name;
}

// ---- public page ------------------------------------------------------------------------------
async function publicPage() {
  const { body } = await getJson(API + "/public");
  if (!body) return;
  fill($("arenas"), body.arenas, (a) => el("li", null,
      el("b", { class: "name", text: a.name }), " ", el("span", { class: "tag " + a.status, text: a.status }),
      el("div", { class: "dim", text: `owner ${a.owner}` + (a.coMods.length ? ` · co-mods ${a.coMods.join(", ")}` : "")
        + (a.status === "Active" ? ` · ${a.players} playing` : "") })), "No player-owned arenas yet. Make the first one.");
  // Arenas launch without approval now; a request only waits when it could not go live.
  if ($("pendingpanel")) $("pendingpanel").hidden = !body.pending.length;
  fill($("pending"), body.pending, (r) => el("li", null,
      el("b", { class: "name", text: r.arena }), ` by ${r.player} · ${when(r.at)}`,
      el("div", { text: r.pitch })), "Nothing waiting.");
  fill($("decisions"), body.decisions, (r) => {
    const launched = r.status === "Approved" && r.decidedBy && r.decidedBy.toLowerCase() === r.player.toLowerCase();
    return el("li", null,
      el("b", { class: "name", text: r.arena }), " ",
      el("span", { class: "tag " + r.status, text: launched ? "Launched" : r.status }),
      launched ? ` by ${r.player} · ${when(r.at)}` : ` ${r.player} · by ${r.decidedBy || "?"}`,
      r.pitch && launched ? el("div", { text: r.pitch }) : null,
      r.reason ? el("div", { class: "dim", text: r.reason }) : null);
  }, "No arenas yet.");
  fill($("events"), body.events, eventItem, "No events yet.");
}

const eventItem = (e) => el("li", null,
  el("span", { class: "dim", text: when(e.at) + " " }), el("b", { class: "name", text: e.arena }),
  ` ${e.kind} by ${e.actor}`, e.detail ? el("div", { class: "dim", text: e.detail }) : null);

// ---- player hub -------------------------------------------------------------------------------
let current = null; // /api/me body

// Sign-in that comes back here: /account/login?next=<this page>, plus a short-lived dare_next
// cookie so a ?weblogin link (opened from the game) lands here too.
function signInHref(page = "/account/login") {
  const here = location.pathname + location.search + location.hash;
  try {
    document.cookie = "dare_next=" + encodeURIComponent(here) + "; Path=/; Max-Age=600; SameSite=Lax"
      + (location.protocol === "https:" ? "; Secure" : "");
  } catch { /* no cookies */ }
  return page + "?next=" + encodeURIComponent(here);
}
function signedIn() {
  if (document.cookie.includes("dare_next=")) {
    try { document.cookie = "dare_next=; Path=/; Max-Age=0; SameSite=Lax"; } catch { /* no cookies */ }
  }
}

async function mePage(arena) {
  const { status, body } = await getJson(API + "/me" + (arena ? "?arena=" + encodeURIComponent(arena) : ""));
  if (status === 401) {
    $("signin").hidden = false; $("hub").hidden = true;
    for (const a of $("signin").querySelectorAll('a[href="/account/login"], a[href="/account/signup"]'))
      a.href = signInHref(a.getAttribute("href"));
    return;
  }
  if (!body) return;
  signedIn();
  current = body;
  $("signin").hidden = true;
  $("hub").hidden = false;
  $("who").textContent = body.player;
  const staff = body.staff || {};
  $("stafflink").hidden = !(staff.approve || staff.decline || staff.create || staff.admin);
  $("staffcreate").hidden = !staff.create;

  const hasArena = !!body.arena;
  for (const a of document.querySelectorAll('nav.side a[data-needs="arena"]')) a.hidden = !hasArena;
  $("arena-context").hidden = !hasArena;
  $("botnav").hidden = !hasArena || body.role !== "owner" || body.status !== "Active" || body.arena.startsWith("#");
  if (hasArena) renderArena(body); else renderNoArena(body);
  route();
}

function route() {
  if (!current) return;
  const want = location.hash.slice(1);
  const shown = showView(want, current.arena ? "overview" : "make");
  $("arena-context").hidden = !current.arena || shown === "make";
  if (shown === "bots") loadBots(current.arena);
  if (shown === "overview") loadBotSummary(current.arena);
  if (shown === "map") loadLibrary();
  if (shown === "graphics") loadGraphics();
  if (shown === "tiles") openTiles();
}

function renderNoArena(body) {
  $("maketitle").textContent = "Make an arena";
  $("makeinfo").textContent = "It's yours at once. Up to 3 per pilot.";
  $("requestform").hidden = false;
  // an old request (from before arenas launched without approval): offer the same name
  if (body.pendingRequest && !$("reqname").value) {
    $("reqname").value = body.pendingRequest.arena;
    $("reqpitch").value = body.pendingRequest.pitch || "";
  }
}

function renderArena(body) {
  const owner = body.role === "owner";
  $("arenatitle").textContent = arenaLabel(body.arena);
  const st = $("arenastatus");
  st.textContent = body.status || ""; st.className = "tag " + (body.status || "");
  $("arenameta").textContent = `${owner ? "You own it" : "You are a co-mod"} · owner ${body.owner}`
    + ` · last visited ${when(body.lastEntered)}`;
  $("gocmd").textContent = `?go ${playName(body.arena)}`;
  const pick = $("arenapick");
  $("overview-comods").textContent = body.coMods.join(", ") || "None";
  $("overview-map").textContent = body.map || "No map";
  $("overview-graphics").textContent = String(body.levelFiles.length);
  $("botstile").hidden = $("botnav").hidden;
  $("overview-bots-label").hidden = $("botnav").hidden;
  $("overview-bots").hidden = $("botnav").hidden;
  pick.replaceChildren(...body.arenas.map((a) => {
    const o = el("option", { value: a.name, text: `${arenaLabel(a.name)} (${a.staff ? "staff" : a.role})` });
    if (a.name === body.arena) o.selected = true;
    return o;
  }));
  // make: up to maxPerPilot public arenas per pilot (drafts, named #..., don't count)
  const max = body.maxPerPilot || 3;
  const owned = body.arenas.filter((a) => a.role === "owner" && !a.staff && !a.name.startsWith("#")).length;
  const full = owned >= max;
  $("maketitle").textContent = full ? `You own ${max} arenas` : "Make an arena";
  $("makeinfo").textContent = full
    ? "That's the most per pilot. Discard one to make another."
    : `It's yours at once. You own ${owned} of ${max}.`;
  $("requestform").hidden = full;

  // owner: public/private switch and discard
  $("ownerbox").hidden = !owner;
  showPrivacy(!!body.private);

  // map
  $("maptitle").textContent = body.map || "No map yet";
  $("curmap").textContent = body.map || "(none)";
  $("mapowner").hidden = !owner;
  $("maponly").hidden = owner;
  // graphics
  renderLvz(body.levelFiles, body.limits.maxLevelFiles, owner);
  $("lvzdrop").hidden = !owner;
  $("lvzonly").hidden = owner;
  // settings
  lastSettings = body.settings || [];
  renderSettings(lastSettings);
  loadSettingSets();
  if (body.configMissing) msg($("setmsg"), "This arena's settings file is missing; ask staff.", false);
  // crew
  renderCrew(body.coMods, owner);
  const editor = "/arena/map?arena=" + encodeURIComponent(body.arena);
  $("openeditor").href = editor; $("viewmap").href = editor;
  drawThumb(body.arena);
  renderRestart(body);
  editingForChat(body.arena);
}

// The docked chat (chat.js) follows the arena being edited: "" is the public arenas (arena 0); a draft has no
// public chat, so the chat stays where it is.
function editingForChat(arena) {
  const name = arena === PUBLIC ? "" : arena.startsWith("#") ? null : arena;
  if (name == null) delete document.documentElement.dataset.editingArena;
  else document.documentElement.dataset.editingArena = name;
  dispatchEvent(new CustomEvent("foundation:editing", { detail: { arena: name } }));
}

// ---- restart and enter: the top bar's buttons, the "not live yet" notes, the two dialogs ----------
const PUBLIC = "(public)"; // the public arenas' shared folder; in game it is arena 0
const playName = (arena) => arena === PUBLIC ? "0" : arena;
const arenaLabel = (arena) => arena === PUBLIC ? "Public (0)" : arena;
const enterUrl = (arena) => "/?arena=" + encodeURIComponent(playName(arena)) + "&enter=1"; // the home page presses Enter
const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;
const andList = (xs) => xs.length < 2 ? xs.join("") : xs.slice(0, -1).join(", ") + " and " + xs[xs.length - 1];
let planTimer = 0;

function renderRestart(body) {
  const owner = body.role === "owner";
  const r = body.restart || { running: false, online: 0, onlineNames: [], pending: [], plan: null };
  const pending = owner && r.pending.length > 0;
  $("enterarena").href = enterUrl(body.arena);
  $("ctxonline").textContent = !r.running ? "Nobody in it" : r.online ? `${plural(r.online, "pilot")} in it` : "Only you in it";
  $("restartenter").hidden = !owner;
  $("restartenter").disabled = !pending;
  $("restartenter").title = pending
    ? `Restart so the running arena uses your saved ${andList(r.pending)}, then enter`
    : "Nothing saved is waiting for a restart";
  for (const n of document.querySelectorAll("[data-restart-note]")) {
    n.hidden = !pending;
    if (pending) n.querySelector("span").textContent = `Saved. The running arena isn't using your new ${andList(r.pending)} yet.`;
  }
  clearInterval(planTimer);
  $("restartplan").hidden = !r.plan;
  $("restartcancel").hidden = !owner;
  if (r.plan) {
    const at = new Date(r.plan.at);
    const tick = () => {
      const left = Math.max(0, Math.round((at - Date.now()) / 1000));
      const inText = left >= 3600 ? `${Math.floor(left / 3600)}h ${Math.round(left % 3600 / 60)}m`
        : left >= 60 ? `${Math.floor(left / 60)}m ${left % 60}s` : `${left}s`;
      $("restartplantext").textContent = `Restart scheduled for ${at.toLocaleString()} (in ${inText}) by ${r.plan.by}.`;
      if (left === 0) { clearInterval(planTimer); setTimeout(refreshRestart, 3000); }
    };
    tick();
    planTimer = setInterval(tick, 1000);
  }
}

// /api/me again, but only the restart part is redrawn (keeps the settings list as it is)
async function refreshRestart() {
  const arena = current?.arena;
  if (!arena) return null;
  const { body } = await getJson(API + "/me?arena=" + encodeURIComponent(arena));
  if (!body || current?.arena !== arena) return null;
  current.restart = body.restart;
  renderRestart(current);
  return body.restart;
}

// The web hides the in-game "?recyclearena" advice: the Restart button is right there.
function webMessage(text) {
  if (current?.role !== "owner") return text;
  return (text || "").replace(/\s+The running arena isn't using[^.]*\?recyclearena\./, "")
    .replace(/[;.]\s*[^;.]*\?recyclearena[^.]*\./, ".");
}

async function startRestart(enter) {
  msg($("ctxmsg"), "", true);
  const r = await refreshRestart();
  if (!r) return msg($("ctxmsg"), "Could not reach the server.", false);
  if (!r.running || r.online === 0) return restartNow(enter, null); // nobody to warn: just do it
  openRestartDialog(r, enter);
}

async function restartNow(enter, message) {
  const arena = current.arena;
  const { body } = await postJson("/restart", { arena, message });
  if (body?.ok && enter) { location.href = enterUrl(arena); return true; }
  msg($("ctxmsg"), body?.message || "Could not restart.", !!body?.ok);
  await refreshRestart();
  return !!body?.ok;
}

let restartEnter = false;
function openRestartDialog(r, enter) {
  restartEnter = enter;
  const names = r.onlineNames || [];
  const more = r.online > names.length ? ` and ${r.online - names.length} more` : "";
  $("restarttitle").textContent = `Restart ${current.arena}?`;
  $("restartwho").textContent = `${plural(r.online, "pilot")} ${r.online === 1 ? "is" : "are"} in the arena right now`
    + (names.length ? ` (${names.join(", ")}${more})` : "")
    + ". Restarting disconnects them from the arena for a moment; they're put straight back in with your changes.";
  $("restartnow").textContent = enter ? "Restart now and enter" : "Restart now";
  if (!$("restartat").value) {
    const d = new Date(Date.now() + 15 * 60000 - new Date().getTimezoneOffset() * 60000);
    $("restartat").value = d.toISOString().slice(0, 16);
  }
  msg($("restartdlgmsg"), "", true);
  $("restartdlg").showModal();
  $("restartnow").focus();
}

function wireRestart() {
  $("restartenter").addEventListener("click", () => startRestart(true));
  for (const b of document.querySelectorAll("[data-restart]")) b.addEventListener("click", () => startRestart(false));
  for (const b of document.querySelectorAll("dialog [data-close]")) b.addEventListener("click", () => b.closest("dialog").close());

  $("enterarena").addEventListener("click", (e) => {
    const r = current?.restart;
    if (current?.role !== "owner" || !r?.pending?.length) return; // nothing waiting: just go
    e.preventDefault();
    $("entertext").textContent = `Your saved ${andList(r.pending)} isn't live in the running arena yet.`
      + (r.online ? ` ${plural(r.online, "pilot")} ${r.online === 1 ? "is" : "are"} in it.` : "")
      + " Restart it first?";
    $("enterdlg").showModal();
    $("enterrestart").focus();
  });
  $("enterrestart").addEventListener("click", () => { $("enterdlg").close(); startRestart(true); });
  $("enteranyway").addEventListener("click", () => { location.href = enterUrl(current.arena); });

  $("restartnow").addEventListener("click", async () => {
    $("restartnow").disabled = true;
    try {
      const ok = await restartNow(restartEnter, $("restartmsg").value.trim() || null);
      if (ok) { $("restartdlg").close(); $("restartmsg").value = ""; }
      else msg($("restartdlgmsg"), $("ctxmsg").textContent, false);
    } finally { $("restartnow").disabled = false; }
  });
  $("restartat").addEventListener("input", () => { document.querySelector('input[name="when"][value="at"]').checked = true; });
  $("restartin").addEventListener("input", () => { document.querySelector('input[name="when"][value="in"]').checked = true; });
  $("restartschedule").addEventListener("click", async () => {
    const mode = document.querySelector('input[name="when"]:checked').value;
    const at = mode === "in" ? new Date(Date.now() + Number($("restartin").value) * 60000) : new Date($("restartat").value);
    if (isNaN(at) || at - Date.now() < 10000) return msg($("restartdlgmsg"), "Pick a time in the future.", false);
    if (mode === "in" && !(Number($("restartin").value) >= 1)) return msg($("restartdlgmsg"), "Enter at least 1 minute.", false);
    $("restartschedule").disabled = true;
    try {
      const { body } = await postJson("/restart", { arena: current.arena, message: $("restartmsg").value.trim() || null,
        at: at.toISOString(), every: Number($("restartevery").value) });
      if (!body?.ok) return msg($("restartdlgmsg"), body?.message || "Could not schedule it.", false);
      $("restartdlg").close();
      $("restartmsg").value = "";
      msg($("ctxmsg"), body.message, true);
      await refreshRestart();
    } finally { $("restartschedule").disabled = false; }
  });
  $("restartcancel").addEventListener("click", async () => {
    const { body } = await postJson("/restart/cancel", { arena: current.arena });
    msg($("ctxmsg"), body?.message, !!body?.ok);
    await refreshRestart();
  });
}

// The arena's map, whole, on the overview (flat chart colours; lvl.js).
async function drawThumb(arena, c = $("thumb")) {
  const ctx = c.getContext("2d");
  ctx.fillStyle = "#04070e"; ctx.fillRect(0, 0, c.width, c.height);
  try {
    const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" });
    if (!r.ok || current?.arena !== arena) return;
    const bytes = new Uint8Array(await r.arrayBuffer());
    if (current?.arena !== arena) return;
    const grid = Lvl.parse(bytes).grid;
    if (c.id === "botmap") {
      botMapGrid = grid;
      drawBotWaypoints();
    } else Lvl.drawOverview(c, grid, true);
    return true;
  } catch { /* leave it dark */ }
}

async function loadBotSummary(arena) {
  if ($("botnav").hidden) return;
  const summary = $("overview-bots");
  summary.textContent = "Loading…";
  try {
    const { body } = await getJson(API + "/bots?arena=" + encodeURIComponent(arena));
    if (current?.arena !== arena) return;
    summary.textContent = body?.ok
      ? `${body.bots.filter(bot => bot.deployed).length} deployed / ${body.bots.length} saved`
      : "Unavailable";
  } catch {
    if (current?.arena === arena) summary.textContent = "Unavailable";
  }
}

let botsArena = "";
let botMapGrid = null;
function drawBotWaypoints() {
  if (!botMapGrid) return;
  const c = $("botmap");
  Lvl.drawOverview(c, botMapGrid, false);
  const ctx = c.getContext("2d");
  const points = botLines($("botpoints").value);
  const shown = $("botmode").value === "sentry" ? points.slice(0, 1) : points;
  ctx.font = "bold 10px sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  for (const [i, point] of shown.entries()) {
    const xy = point.split(/\s+/).map(Number);
    if (xy.length !== 2 || xy.some(n => !Number.isInteger(n) || n < 0 || n > 1023)) continue;
    const x = Math.max(8, Math.min(c.width - 8, xy[0] * c.width / 1024));
    const y = Math.max(8, Math.min(c.height - 8, xy[1] * c.height / 1024));
    ctx.fillStyle = "#68e7fa";
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, 2 * Math.PI);
    ctx.fill();
    ctx.fillStyle = "#06101a";
    ctx.fillText(String(i + 1), x, y);
  }
}
async function loadBots(arena) {
  if (botsArena !== arena) {
    botsArena = arena;
    botMapGrid = null;
    $("botform").reset();
    $("botlist").replaceChildren();
    msg($("botmsg"), "", true);
  }
  $("botmap").dataset.arena = "";
  try {
    const { body } = await getJson(API + "/bots?arena=" + encodeURIComponent(arena));
    if (current?.arena !== arena) return;
    if (!body?.ok) return msg($("botmsg"), body?.message || "Could not load bots.", false);
    fill($("botlist"), body.bots, (bot) => {
      const edit = el("button", { type: "button", text: bot.label });
      const remove = el("button", { type: "button", class: "small danger", text: "Remove",
        "aria-label": "Remove " + bot.label });
      edit.onclick = () => editBot(bot);
      remove.onclick = () => changeBot("remove", { arena, label: bot.label });
      edit.className = "bot-name";
      const input = el("input", { type: "checkbox", role: "switch", "aria-label": "Deploy " + bot.label });
      input.checked = bot.deployed;
      const status = el("span", { class: "dim", text: bot.deployed ? "Deployed" : "Stopped" });
      input.onchange = async () => {
        input.disabled = true;
        if (!await changeBot(input.checked ? "deploy" : "stop", { arena, label: bot.label })) {
          input.checked = bot.deployed;
          input.disabled = false;
        }
      };
      const toggle = el("label", { class: "switch bot-switch" }, input, el("span", { class: "track", "aria-hidden": "true" }), status);
      return el("li", { class: "bot-row" }, edit, toggle, remove);
    }, "No bots configured.");
    if (await drawThumb(arena, $("botmap"))) $("botmap").dataset.arena = arena;
  } catch {
    if (current?.arena === arena) msg($("botmsg"), "Could not load bots.", false);
  }
}
const botLines = (value) => value.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
function botFields() {
  const mode = $("botmode").value;
  $("botrangefield").hidden = mode !== "sentry";
  $("botwaypointsfield").hidden = mode === "idle";
  $("botwaypointhint").textContent = {
    patrol: "The bot flies the points in order, then loops.",
    sentry: "The first point is the guard post. Without one, it guards its spawn.",
    fighter: "The bot follows this route when there are no enemies to chase.",
  }[mode] || "";
  $("botchatfields").hidden = !$("botcasual").checked;
  $("botactivitylabel").textContent = ["Rare", "Occasional", "Regular", "Frequent", "Chatty"][$("botactivity").value - 1];
  drawBotWaypoints();
}
function editBot(bot) {
  $("botform").reset();
  $("botpriority").value = ""; // a hidden field is not cleared by reset
  $("botlabel").value = bot.label;
  for (const line of botLines(bot.script)) {
    const [, key, value] = /^(\S+) +(.*)$/.exec(line) || [];
    switch (key) {
      case "ship": $("botship").value = value; break;
      case "team": $("botteam").value = value; break;
      case "priority": $("botpriority").value = value; break; // set by ?bots; kept through a web save
      case "behaviour": $("botmode").value = value; break;
      case "range": $("botrange").value = value; break;
      case "greet": $("botgreet").value = value; break;
      case "botchat": $("botchat").checked = value === "on"; break;
      case "casual":
        [$("botactivity").value, $("botpersonality").value] = value.split(/\s+/);
        $("botcasual").checked = true;
        break;
      case "point": case "reply": {
        const field = $(key === "point" ? "botpoints" : "bot" + key);
        field.value = [field.value, key === "point" ? value : value.replace(/\s+/, " | ")].filter(Boolean).join("\n");
        break;
      }
    }
  }
  botFields();
}
async function changeBot(action, payload) {
  try {
    const { body } = await postJson("/bots/" + action, payload);
    if (current?.arena !== payload.arena) return false;
    msg($("botmsg"), body?.message, body?.ok);
    if (body?.ok) await loadBots(payload.arena);
    return !!body?.ok;
  } catch {
    if (current?.arena === payload.arena) msg($("botmsg"), "Could not update bots.", false);
    return false;
  }
}
function wireBots() {
  for (const id of ["botmode", "botcasual", "botactivity"]) $(id).addEventListener("input", botFields);
  $("botpoints").addEventListener("input", drawBotWaypoints);
  $("botform").addEventListener("reset", () => { $("botpriority").value = ""; setTimeout(botFields, 0); });
  botFields();
  $("botform").onsubmit = (event) => {
    event.preventDefault();
    const script = ["ship " + $("botship").value, "behaviour " + $("botmode").value, "team " + ($("botteam").value || 0)];
    if ($("botpriority").value) script.push("priority " + $("botpriority").value);
    for (const point of botLines($("botpoints").value)) script.push("point " + point);
    if ($("botrange").value) script.push("range " + $("botrange").value);
    if ($("botgreet").value.trim()) script.push("greet " + $("botgreet").value.trim());
    if ($("botcasual").checked) script.push("casual " + $("botactivity").value + " " + $("botpersonality").value);
    if ($("botcasual").checked && $("botchat").checked) script.push("botchat on");
    for (const line of botLines($("botreply").value)) {
      const match = /^(\S+)\s*\|\s*(.+)$/.exec(line);
      if (!match) return msg($("botmsg"), "Use | between the trigger and the custom message.", false);
      if (match[1].length > 24) return msg($("botmsg"), "A reply trigger can be at most 24 characters: " + match[1].slice(0, 24) + "…", false);
      script.push("reply " + match[1] + " " + match[2]);
    }
    changeBot("save", { arena: current.arena, label: $("botlabel").value, script: script.join("\n") });
  };
  $("botmap").onclick = (event) => {
    const c = $("botmap");
    if (c.dataset.arena !== current?.arena) return;
    const r = c.getBoundingClientRect();
    const tile = (v, origin, size) => Math.max(0, Math.min(1023, Math.floor((v - origin) * 1024 / size)));
    const point = tile(event.clientX, r.left, r.width) + " " + tile(event.clientY, r.top, r.height);
    const settings = $("botpoints");
    const points = botLines(settings.value);
    settings.value = ($("botmode").value === "sentry" ? [point, ...points.slice(1)] : [...points, point]).join("\n");
    drawBotWaypoints();
  };
}

// ---- game graphics: a card per replaceable picture (current thumbnail, upload, generate, reset) ----
const GEN_LABELS = { recolor: "Recolour", new: "New look", prompt: "From a description…", ship: "Random ship", "ship-prompt": "Ship from a description…" };
const PROMPTED = new Set(["prompt", "ship-prompt"]);
let gfxArena = "";

// What you make here is also kept on your account, as in the game's Graphics overlay: weapons in
// your armory, ships in your shipyard (/account/api/...), so you can wear it, download it, or put it
// on another arena later. Other graphics are only the arena's.
const ARMORY_KINDS = new Set(["bullets", "bombs", "mines", "shrapnel", "repel", "rocket"]);
const ACCT = "/account/api";
let saved = null; // { armory: items[], ships: ships[] } of the signed-in pilot, or null
const isShipSlot = (name) => /^ship[1-8]$/.test(name);
const keepsOnAccount = (slot) => ARMORY_KINDS.has(slot.name) || isShipSlot(slot.name);
const slug = (text) => String(text).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);

async function loadSaved() {
  const [a, y] = await Promise.all([getJson(ACCT + "/armory"), getJson(ACCT + "/shipyard")]);
  saved = a.body?.ok || y.body?.ok ? { armory: a.body?.ok ? a.body.items : [], ships: y.body?.ok ? y.body.ships : [] } : null;
}

/** The pilot's saved items that fit `slot`: armory items of its kind, or any shipyard ship. */
function savedFor(slot) {
  if (!saved) return [];
  if (ARMORY_KINDS.has(slot.name)) return saved.armory.filter((i) => i.kind === slot.name).map((i) => ({ ...i, url: `${ACCT}/armory/sheet?id=${i.id}` }));
  if (isShipSlot(slot.name)) return saved.ships.map((s) => ({ ...s, url: `${ACCT}/shipyard/sheet?id=${s.id}` }));
  return [];
}

function toBase64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

/** Adds a picture to the pilot's armory (weapons) or shipyard (ships); returns a note for the message. */
async function keepOnAccount(slot, bytes, name, source, prompt = "") {
  if (!keepsOnAccount(slot)) return "";
  const ship = isShipSlot(slot.name);
  const where = ship ? "shipyard" : "armory";
  const payload = { name: (name || slot.label).slice(0, 40), source, sheet: toBase64(bytes) };
  if (prompt) payload.prompt = prompt; // kept, so shared ones can be found by it
  if (!ship) payload.kind = slot.name;
  try {
    const { status, body } = await post(`${ACCT}/${where}/add`, JSON.stringify(payload), "application/json");
    if (status === 401) return ` Sign in to the site to keep it in your ${where} as well.`;
    if (!body?.ok) return ` It wasn't added to your ${where}: ${body?.message || "try again"}`;
    await loadSaved();
    return ` Added to your ${where}.`;
  } catch {
    return ` It wasn't added to your ${where}.`;
  }
}

/** Saves bytes as a file in the browser's downloads (PNG or BMP, by what they are). */
function saveFile(bytes, stem) {
  const png = bytes[0] === 0x89 && bytes[1] === 0x50;
  const url = URL.createObjectURL(new Blob([bytes], { type: png ? "image/png" : "image/bmp" }));
  const a = el("a", { href: url, download: `${stem}.${png ? "png" : "bmp"}` });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** The picture a card shows, as a file: a stock ship is cut out of the shared ships sheet. */
async function downloadGraphic(arena, slot) {
  try {
    const r = await fetch(`${API}/graphics/image?arena=${encodeURIComponent(arena)}&name=${slot.name}`, { credentials: "same-origin" });
    if (!r.ok) throw new Error();
    const ship = /^ship([1-8])$/.exec(slot.name);
    const stem = ship ? `${slug(slot.label) || slot.name}-rotations` : slot.name;
    if (ship && r.headers.get("X-Graphic-Source") === "stock") {
      const img = await createImageBitmap(await r.blob());
      const h = img.height / 8, c = el("canvas", { width: String(img.width), height: String(h) });
      c.getContext("2d").drawImage(img, 0, (Number(ship[1]) - 1) * h, img.width, h, 0, 0, img.width, h);
      const blob = await new Promise((done) => c.toBlob(done, "image/png"));
      saveFile(new Uint8Array(await blob.arrayBuffer()), stem);
    } else {
      saveFile(new Uint8Array(await r.arrayBuffer()), stem);
    }
  } catch {
    msg($("gfxmsg"), `Couldn't download ${slot.label}.`, false);
  }
}

// ---- animated icons: one frame of a sheet at a time, in a fixed box ----
// How each sheet is cut (the stock layout, as the game and ships/graphics.py read it): columns,
// rows, and what plays: "all" frames in order, "row" (row 0 only: level 1, the enemy's...) or
// "sheet" (tiles and tables: the whole picture, still). A cell is the picture's size / cols × rows.
const SPRITES = {
  bullets: [4, 10, "row"], bombs: [10, 13, "row"], mines: [10, 8, "row"], shrapnel: [10, 6, "row"],
  repel: [5, 2, "all"], rocket: [13, 2, "all"],
  prizes: [10, 1, "all"], powerb: [10, 3, "row"], flag: [10, 2, "row"], goal: [9, 2, "row"],
  explode0: [7, 1, "all"], explode1: [6, 6, "all"], explode2: [4, 11, "all"], empburst: [5, 2, "all"],
  warp: [6, 3, "all"], warppnt: [10, 1, "all"], exhaust: [19, 2, "row"], shield: [10, 1, "all"], super: [10, 1, "all"],
  over1: [15, 2, "all"], over2: [10, 3, "all"], over3: [15, 2, "all"], over4: [5, 2, "all"], over5: [4, 6, "all"],
  wall: [10, 2, "sheet"], gradient: [1, 1, "sheet"],
};
// Ships: 40 headings in 10 columns and 4 rows, frame 0 nose-up, each next one 9° clockwise.
const layoutOf = (name) => (isShipSlot(name) ? [10, 4, "all"] : SPRITES[name] ?? [1, 1, "sheet"]);

const icons = new WeakMap(); // canvas -> { img, sx, sy, sw, sh, cols, n }
const shown = new Set(); // icons on screen; only these are redrawn
const observed = new Set();
const stillMotion = matchMedia("(prefers-reduced-motion: reduce)");
let iconTick = 0, iconTimer = 0;
const onScreen = "IntersectionObserver" in window ? new IntersectionObserver((entries) => {
  for (const e of entries) if (e.isIntersecting) shown.add(e.target); else shown.delete(e.target);
  runIcons();
}, { rootMargin: "64px" }) : null;

/** A canvas for an icon `size` CSS pixels square, drawn at the screen's own pixel density. */
function iconCanvas(size, label) {
  const k = Math.min(3, Math.max(1, Math.round(globalThis.devicePixelRatio || 1)));
  const c = el("canvas", { class: "icon", width: String(size * k), height: String(size * k), role: "img", "aria-label": label });
  c.style.width = c.style.height = size + "px";
  return c;
}

/** Shows `img` (the part `src` of it, if given) on `canvas` as graphic `name`'s animated icon. */
function showIcon(canvas, img, name, src = null) {
  const [cols, rows, mode] = layoutOf(name);
  const r = src ?? { sx: 0, sy: 0, sw: img.width, sh: img.height };
  const still = mode === "sheet";
  icons.set(canvas, { img, ...r, cols: still ? 1 : cols, rows: still ? 1 : rows, n: still ? 1 : mode === "row" ? cols : cols * rows });
  drawIcon(canvas, 0);
  if (onScreen) { onScreen.observe(canvas); observed.add(canvas); } else shown.add(canvas);
  runIcons();
}

function drawIcon(canvas, t) {
  const p = icons.get(canvas);
  if (!p) return;
  const fw = p.sw / p.cols, fh = p.sh / p.rows, f = t % p.n;
  const W = canvas.width, H = canvas.height;
  // Whole-number scale where the frame fits (tiny sprites at about half the box), else shrink to fit.
  let s = Math.min(W / fw, H / fh) * (fw <= 8 ? 0.55 : 1);
  if (s >= 1) s = Math.floor(s);
  const w = Math.round(fw * s), h = Math.round(fh * s);
  const g = canvas.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, W, H);
  g.drawImage(p.img, p.sx + (f % p.cols) * fw, p.sy + Math.floor(f / p.cols) * fh, fw, fh,
    Math.floor((W - w) / 2), Math.floor((H - h) / 2), w, h);
}

/** Plays the icons on screen (one timer for all of them), or stops when none can move. */
function runIcons() {
  const move = !stillMotion.matches && !document.hidden && [...shown].some((c) => (icons.get(c)?.n ?? 1) > 1);
  if (move && !iconTimer) {
    iconTimer = setInterval(() => {
      iconTick++;
      for (const c of shown) if (c.isConnected) drawIcon(c, iconTick); else forgetIcon(c);
    }, 70); // the overlay's own preview speed
  } else if (!move && iconTimer) {
    clearInterval(iconTimer);
    iconTimer = 0;
  }
}

function forgetIcon(c) { shown.delete(c); observed.delete(c); onScreen?.unobserve(c); }
/** Lets go of icons whose cards were replaced. */
function sweepIcons() { for (const c of [...observed, ...shown]) if (!c.isConnected) forgetIcon(c); }

stillMotion.addEventListener?.("change", () => { for (const c of shown) drawIcon(c, 0); runIcons(); });
document.addEventListener("visibilitychange", runIcons);

/** The pilot's saved weapons (or ships) that fit this card: click one to use it here. */
function savedList(arena, slot) {
  const where = isShipSlot(slot.name) ? "shipyard" : "armory";
  const mine = savedFor(slot);
  const head = el("div", { class: "savedhead" }, el("span", { class: "title", text: `Your ${where}` }));
  if (saved) head.append(el("span", { class: "count", text: String(mine.length) }));
  const box = el("div", { class: "saved" }, head);
  const list = el("ul", { class: "savedlist", "aria-label": `Your ${where}` });
  if (!mine.length) {
    list.append(el("li", { class: "empty", text: saved ? `Ones you make or upload land here.` : `Sign in to keep them in your ${where}.` }));
    box.append(list);
    return box;
  }
  for (const it of mine) {
    const art = iconCanvas(40, it.name);
    const img = new Image();
    img.addEventListener("load", () => showIcon(art, img, slot.name));
    img.src = it.url;
    const b = el("button", { type: "button", class: "savedthumb", title: `${it.name}: use it for ${slot.label}` }, art, el("span", { class: "name", text: it.name }));
    b.addEventListener("click", () => void useSaved(arena, slot, it));
    list.append(el("li", null, b));
  }
  box.append(list);
  return box;
}

/** Puts one of the pilot's saved items on the arena's `slot`. */
async function useSaved(arena, slot, item) {
  msg($("gfxmsg"), `Putting ${item.name} on ${slot.label}…`, true);
  const r = await fetch(item.url, { credentials: "same-origin" });
  if (!r.ok) return msg($("gfxmsg"), `Couldn't load ${item.name}.`, false);
  await setGraphic(arena, slot, await r.blob());
}

async function loadGraphics() {
  const arena = current?.arena;
  if (!arena) return;
  const host = $("gfxgroups");
  if (gfxArena !== arena) { host.replaceChildren(el("p", { class: "dim", text: "Loading…" })); gfxArena = arena; sweepIcons(); }
  const [{ body }] = await Promise.all([getJson(API + "/graphics?arena=" + encodeURIComponent(arena)), loadSaved()]);
  if (current?.arena !== arena) return;
  if (!body?.ok) { host.replaceChildren(el("p", { class: "msg bad", text: body?.message || "Couldn't load the graphics." })); return; }
  const owner = current.role === "owner";
  const groups = new Map();
  for (const slot of body.graphics.slots) {
    if (!groups.has(slot.group)) groups.set(slot.group, []);
    groups.get(slot.group).push(slot);
  }
  host.replaceChildren(...[...groups].map(([group, slots]) =>
    el("div", null, el("h3", { text: group }), el("div", { class: "gfxgrid" }, ...slots.map((s) => gfxCard(arena, s, owner))))));
  sweepIcons();
}

function gfxCard(arena, slot, owner) {
  const canvas = iconCanvas(88, slot.label + " now");
  const srcText = slot.source === "arena" ? "Yours" : slot.source === "lvz" ? "From " + slot.lvzFile : "Stock";
  const card = el("div", { class: "gfxcard" },
    el("div", { class: "head" }, el("b", { text: slot.label }), el("span", { class: "src " + slot.source, text: srcText })),
    el("div", { class: "thumb" }, canvas),
    el("span", { class: "hint", text: slot.hint }));
  drawGraphic(canvas, arena, slot, false);
  if (owner) {
    const file = el("input", { type: "file", accept: ".png,.bmp,.bm2,image/png,image/bmp", hidden: "" });
    const up = el("button", { type: "button", class: "small", text: "Upload" });
    up.addEventListener("click", () => file.click());
    file.addEventListener("change", async () => {
      const f = file.files[0]; file.value = "";
      if (f) await setGraphic(arena, slot, f, { name: f.name.replace(/\.[^.]+$/, ""), source: "uploaded" });
    });
    const acts = el("div", { class: "acts" }, up, file);
    if (slot.generate.length) {
      const gen = el("select", { "aria-label": "Generate a new " + slot.label });
      gen.append(el("option", { value: "", text: "Generate…" }), ...slot.generate.map((m) => el("option", { value: m, text: GEN_LABELS[m] || m })));
      gen.addEventListener("change", () => { const m = gen.value; gen.value = ""; if (m) openGenerator(arena, slot, m); });
      acts.append(gen);
    }
    if (keepsOnAccount(slot)) card.append(savedList(arena, slot));
    if (slot.source === "arena") {
      const reset = el("button", { type: "button", class: "small danger", text: "Reset" });
      reset.addEventListener("click", async () => {
        if (!await Dlg.confirm(`Put ${slot.label} back to the game's own picture?`, { title: "Reset picture", ok: "Reset", danger: true })) return;
        const { body } = await postJson("/graphics/clear", { arena, name: slot.name });
        msg($("gfxmsg"), webMessage(body?.message), body?.ok);
        if (body?.ok) { loadGraphics(); refreshRestart(); }
      });
      acts.append(reset);
    }
    card.append(acts);
  }
  const download = el("button", { type: "button", class: "small ghost", text: "Download", title: `Save ${slot.label} as a file` });
  download.addEventListener("click", () => void downloadGraphic(arena, slot));
  (card.querySelector(".acts") ?? card.appendChild(el("div", { class: "acts" }))).append(download);
  return card;
}

// The arena's current picture (or the stock one) as the card's icon; ships' stock pictures share one file.
async function drawGraphic(canvas, arena, slot) {
  try {
    const r = await fetch(`${API}/graphics/image?arena=${encodeURIComponent(arena)}&name=${slot.name}`, { credentials: "same-origin" });
    if (!r.ok) return;
    const img = await createImageBitmap(await r.blob());
    const ship = /^ship([1-8])$/.exec(slot.name);
    const h = img.height / 8; // a stock ship is one of the eight in the shared sheet
    showIcon(canvas, img, slot.name, ship && r.headers.get("X-Graphic-Source") === "stock" ? { sx: 0, sy: (Number(ship[1]) - 1) * h, sw: img.width, sh: h } : null);
  } catch { /* leave it blank */ }
}

function paintSheet(canvas, img, sx = 0, sy = 0, sw = img.width, sh = img.height) {
  const k = Math.max(1, Math.min(4, Math.floor(Math.min(640 / sw, 480 / sh)) || 1));
  canvas.width = sw * k; canvas.height = sh * k;
  const ctx = canvas.getContext("2d");
  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = "#000"; ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.drawImage(img, sx, sy, sw, sh, 0, 0, sw * k, sh * k);
}

/** Saves a picture to the arena; a new one (`keep`: { name, source }) is also kept on the pilot's account. */
async function setGraphic(arena, slot, blob, keep = null) {
  msg($("gfxmsg"), `Saving ${slot.label}…`, true);
  const { body } = await post(`${API}/graphics/set?arena=${encodeURIComponent(arena)}&name=${slot.name}`, blob, "application/octet-stream");
  const note = body?.ok && keep ? await keepOnAccount(slot, new Uint8Array(await blob.arrayBuffer()), keep.name, keep.source, keep.prompt) : "";
  msg($("gfxmsg"), webMessage(body?.message) + note, body?.ok);
  if (body?.ok) { loadGraphics(); refreshRestart(); }
  return !!body?.ok;
}

// Generate: a preview first; "Use this" saves it to the arena.
let gen = null; // { arena, slot, mode, prompt, png }
function openGenerator(arena, slot, mode) {
  gen = { arena, slot, mode, prompt: "", png: null };
  $("gfxdlgtitle").textContent = `${GEN_LABELS[mode].replace("…", "")}: ${slot.label}`;
  $("gfxpromptform").hidden = !PROMPTED.has(mode);
  const c = $("gfxcanvas"); c.width = 1; c.height = 1;
  $("gfxuse").disabled = true; $("gfxagain").disabled = true;
  msg($("gfxdlgmsg"), PROMPTED.has(mode) ? "Describe it, then Draw. It takes up to a minute." : "", true);
  $("gfxdlg").showModal();
  if (PROMPTED.has(mode)) $("gfxprompt").focus(); else runGenerator();
}

async function runGenerator() {
  if (!gen) return;
  const run = gen;
  const dlg = $("gfxdlg");
  dlg.classList.add("busy");
  $("gfxuse").disabled = true; $("gfxagain").disabled = true; $("gfxdraw").disabled = true;
  msg($("gfxdlgmsg"), "", true);
  const stopWait = waitingFor(run);
  try {
    // Again on a prompt continues its run (run.batch): the generator shows a take unlike the ones shown.
    const { body } = await postJson("/graphics/generate", run.batch
      ? { arena: run.arena, name: run.slot.name, mode: run.mode, batch: run.batch }
      : { arena: run.arena, name: run.slot.name, mode: run.mode, prompt: run.prompt || null });
    if (gen !== run) return;
    if (!body?.ok || !body.sheet) { run.batch = null; msg($("gfxdlgmsg"), body?.message || "Couldn't draw that.", false); return; }
    run.batch = body.batch || null;
    run.png = Uint8Array.from(atob(body.sheet), (ch) => ch.charCodeAt(0));
    paintSheet($("gfxcanvas"), await createImageBitmap(new Blob([run.png], { type: "image/png" })));
    msg($("gfxdlgmsg"), "Use this, or try Again.", true);
    $("gfxuse").disabled = false;
  } catch {
    msg($("gfxdlgmsg"), "Couldn't reach the generator.", false);
  } finally {
    stopWait();
    dlg.classList.remove("busy");
    $("gfxagain").disabled = PROMPTED.has(run.mode) && !run.prompt;
    $("gfxdraw").disabled = false;
  }
}

// The spinner over the preview while the generator works: what it's doing and the time so far.
function waitingFor(run) {
  const prompted = PROMPTED.has(run.mode);
  const what = run.mode === "ship" ? "Building a random ship" : run.mode === "ship-prompt" ? "Painting your ship"
    : run.mode === "recolor" ? "Recolouring" : run.mode === "new" ? `Drawing a new ${run.slot.label.toLowerCase()}` : `Painting your ${run.slot.label.toLowerCase()}`;
  const started = Date.now();
  const tick = () => {
    const s = Math.round((Date.now() - started) / 1000);
    $("gfxloadingtime").textContent = prompted ? `${s} s · usually 20–60 s` : `${s} s`;
  };
  $("gfxloadingtext").textContent = what + "…";
  tick();
  $("gfxloading").hidden = false;
  const timer = setInterval(tick, 1000);
  return () => { clearInterval(timer); $("gfxloading").hidden = true; };
}

function wireGraphics() {
  $("gfxpromptform").addEventListener("submit", (e) => {
    e.preventDefault();
    if (!gen) return;
    gen.prompt = $("gfxprompt").value.trim();
    gen.batch = null; // Draw starts a new run of tries
    if (!gen.prompt) return msg($("gfxdlgmsg"), "Describe what to draw.", false);
    runGenerator();
  });
  $("gfxagain").addEventListener("click", runGenerator);
  $("gfxuse").addEventListener("click", async () => {
    if (!gen?.png) return;
    $("gfxuse").disabled = true;
    const keep = { name: gen.prompt ? gen.prompt.slice(0, 40) : `${GEN_LABELS[gen.mode].replace("…", "")} ${gen.slot.label.toLowerCase()}`, source: PROMPTED.has(gen.mode) ? "generated" : "random", prompt: PROMPTED.has(gen.mode) ? gen.prompt : "" };
    if (await setGraphic(gen.arena, gen.slot, new Blob([gen.png], { type: "image/png" }), keep)) $("gfxdlg").close();
    else { msg($("gfxdlgmsg"), $("gfxmsg").textContent, false); $("gfxuse").disabled = false; }
  });
  $("gfxdlg").addEventListener("close", () => { gen = null; });
}

function renderLvz(files, max, owner) {
  $("lvzcount").textContent = `${files.length} of ${max} files.`;
  fill($("lvzlist"), files, (f) => {
    const li = el("li", null, el("span", { class: "name", text: f }));
    if (owner) {
      const b = el("button", { type: "button", class: "small danger", text: "Remove" });
      b.setAttribute("aria-label", `Remove ${f}`);
      b.addEventListener("click", () => removeLvz(f));
      li.append(b);
    }
    return li;
  }, "No graphics yet.");
}

function renderCrew(coMods, owner) {
  fill($("comodlist"), coMods, (name) => {
    const li = el("li", null, el("span", { class: "name", text: name }));
    if (owner) {
      const b = el("button", { type: "button", class: "small danger", text: "Remove" });
      b.setAttribute("aria-label", `Remove ${name} as co-mod`);
      b.addEventListener("click", () => changeCoMod("remove", name));
      li.append(b);
    }
    return li;
  }, "No co-mods yet.");
  $("comodform").hidden = !owner || coMods.length >= 5;
  $("crewonly").hidden = owner;
}

async function changeCoMod(action, name) {
  if (action === "remove" && !await Dlg.confirm(`Remove ${name} as co-mod of ${current.arena}?`, { title: "Remove co-mod", ok: "Remove", danger: true })) return;
  const { body } = await postJson("/comod", { arena: current.arena, action, player: name });
  msg($("comodmsg"), body.message, body.ok);
  if (body.ok) { $("comodname").value = ""; mePage(current.arena); }
}

// ---- settings: grouped into tabs. Switches for on/off settings, a slider and number box for numbers with a
// known range, a list for named choices, text otherwise. Each setting's help shows on hover, focus or tap. ----
const SHIP_SECTIONS = ["All", "Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark", "LegalShip"];
const SETTING_TABS = [
  { id: "ships", label: "Ships", sections: SHIP_SECTIONS },
  { id: "weapons", label: "Weapons", sections: ["Bullet", "Bomb", "Mine", "Burst", "Repel", "Rocket", "Brick", "Decoy", "Shrapnel", "Thor", "Portal"] },
  { id: "prizes", label: "Prizes and buying", sections: ["Prize", "PrizeWeight", "DPrizeWeight", "Cost", "Buy"] },
  { id: "game", label: "Game rules", sections: ["General", "Misc", "Kill", "Team", "Spawn", "Spectator", "Periodic", "Radar", "Toggle", "Door", "Wormhole", "Notify"] },
  { id: "flags", label: "Flags and balls", sections: ["Flag", "Soccer", "King", "Koth", "Bounty"] },
  { id: "chat", label: "Chat and voice", sections: ["Chat", "Message", "Voice"] },
  { id: "net", label: "Lag and network", sections: ["Lag", "Latency", "Net", "Routing", "Security"] },
];
const isMessageKey = (section, key) => section.toLowerCase() === "misc" && /^(greetmessage|periodicmessage\d)$/i.test(key);
const tabOf = (section) => SETTING_TABS.find((t) => t.sections.some((x) => x.toLowerCase() === section.toLowerCase()))?.id ?? "other";
let settingTab = "all";
const isInt = (v) => v != null && v !== "" && /^-?\d+$/.test(String(v).trim());
const isTrue = (v) => /^(1|true|yes|on|-?[1-9]\d*)$/i.test(String(v ?? "").trim());
const isBoolSetting = (st) => st.type === "bool" || /^(true|false)$/i.test(String(st.default ?? "")) || (String(st.min) === "0" && String(st.max) === "1");
// the value a setting has, normalised so "True" and "1" compare equal
const norm = (st, v) => (isBoolSetting(st) ? (isTrue(v) ? "1" : "0") : String(v ?? "").trim());
const isChanged = (st) => st.value != null && st.default != null && norm(st, st.value) !== norm(st, st.default);

async function saveSetting(section, key, value, node) {
  const { body } = await postJson("/settings", { arena: current.arena, section, key, value });
  msg(node, webMessage(body.message), body.ok);
  if (body.ok) {
    const st = findSetting(section, key);
    if (st) st.value = value;
  }
  if (body.ok && /recyclearena/.test(body.message || "")) refreshRestart();
  return body.ok;
}
const findSetting = (section, key) => lastSettings.find((x) => x.section.toLowerCase() === section.toLowerCase())
  ?.settings.find((x) => x.key.toLowerCase() === key.toLowerCase());

function settingInput(s, st, saveBtn, row) {
  const label = `${s.section}:${st.key}`;
  const shown = st.value != null && st.value.trim() !== "" ? st.value : st.default;
  if (isBoolSetting(st)) {
    const box = el("input", { type: "checkbox", role: "switch", "aria-label": label });
    box.checked = isTrue(shown);
    const wrap = el("label", { class: "switch bot-switch" }, box, el("span", { class: "track", "aria-hidden": "true" }));
    box.addEventListener("change", async () => {
      box.disabled = true;
      if (!await saveSetting(s.section, st.key, box.checked ? "1" : "0", $("setmsg"))) box.checked = !box.checked;
      box.disabled = false;
      row.classList.toggle("changed", isChanged(st));
    });
    saveBtn.hidden = true;
    return { node: wrap, value: () => (box.checked ? "1" : "0") };
  }
  const dirty = (v) => { saveBtn.disabled = v === (st.value ?? ""); };
  const onEnter = (e) => { if (e.key === "Enter" && !saveBtn.disabled) { e.preventDefault(); saveBtn.click(); } };
  if (st.choices && st.choices.length) {
    const sel = el("select", { "aria-label": label });
    const opts = [...st.choices];
    if (shown && !opts.some((c) => c.toLowerCase() === String(shown).toLowerCase())) opts.unshift(shown);
    for (const c of opts) sel.append(el("option", { value: c, text: c }));
    sel.value = opts.find((c) => c.toLowerCase() === String(shown ?? "").toLowerCase()) ?? opts[0];
    sel.addEventListener("change", () => dirty(sel.value));
    sel.addEventListener("keydown", onEnter);
    saveBtn.disabled = true;
    return { node: sel, value: () => sel.value };
  }
  const numeric = st.type === "int" || isInt(st.min) || isInt(st.max) || (isInt(st.default) && (st.value == null || isInt(st.value)));
  const input = el("input", { type: numeric ? "number" : "text", value: st.value ?? "", maxlength: "64", "aria-label": label,
    placeholder: st.default != null ? String(st.default) : "" });
  input.addEventListener("keydown", onEnter);
  input.addEventListener("input", () => dirty(input.value));
  saveBtn.disabled = true;
  if (!numeric) { input.className = "textval"; return { node: input, value: () => input.value }; }
  input.step = "1";
  if (isInt(st.min)) input.min = st.min;
  if (isInt(st.max)) input.max = st.max;
  input.className = "numval";
  if (!(isInt(st.min) && isInt(st.max) && +st.max > +st.min)) return { node: input, value: () => input.value };
  // a known range: a slider next to the number box, kept in step with it
  const slider = el("input", { type: "range", min: st.min, max: st.max, step: "1", "aria-label": label + " slider", tabindex: "-1" });
  const toSlider = () => { const v = isInt(input.value) ? input.value : isInt(shown) ? shown : st.min; slider.value = v; };
  toSlider();
  slider.addEventListener("input", () => { input.value = slider.value; dirty(input.value); });
  input.addEventListener("input", toSlider);
  return { node: el("span", { class: "rangeval" }, slider, input), value: () => input.value };
}

let tipSeq = 0;
function settingTip(s, st) {
  const range = st.min != null || st.max != null ? `${st.min ?? ""}–${st.max ?? ""}` : st.range || "";
  const parts = [range && `Range ${range}`, st.default != null && st.default !== "" ? `Default ${st.default}` : "",
    st.choices?.length ? `Choices: ${st.choices.join(", ")}` : ""].filter(Boolean);
  return el("span", { class: "tip", role: "tooltip", id: "tip" + (++tipSeq) },
    el("span", { class: "tipname", text: `${s.section}:${st.key}` }),
    el("span", { text: st.description || "No description from the server." }),
    parts.length ? el("span", { class: "tipmeta", text: parts.join(" · ") }) : null);
}

function renderSettings(sections) {
  renderMessages(sections);
  sections = sections.map((s) => ({ ...s, settings: s.settings.filter((st) => !isMessageKey(s.section, st.key)) }))
    .filter((s) => s.settings.length);
  const host = $("settings");
  const open = new Set([...host.querySelectorAll("details.section[open]")].map((d) => d.dataset.section));
  host.replaceChildren(...sections.map((s) => {
    const d = el("details", { class: "section", "data-tab": tabOf(s.section), "data-section": s.section },
      el("summary", { text: `${s.section} (${s.settings.length})` }));
    const grid = el("div", { class: "setgrid" });
    for (const st of s.settings) {
      const save = el("button", { type: "button", class: "small setsave", text: "Save", "aria-label": `Save ${s.section}:${st.key}` });
      const row = el("div", { class: "setting" + (isChanged(st) ? " changed" : ""),
        "data-search": `${s.section} ${st.key} ${st.description || ""}`.toLowerCase(), "data-key": `${s.section}:${st.key}` });
      const input = settingInput(s, st, save, row);
      const tip = settingTip(s, st);
      const key = el("button", { type: "button", class: "key", "aria-describedby": tip.id, "aria-expanded": "false" },
        el("span", { class: "keyname", text: st.key }), el("span", { class: "i", "aria-hidden": "true", text: "i" }));
      // tap (touch) or click pins the help open; hover and keyboard focus show it too (CSS)
      key.addEventListener("click", () => {
        const on = !row.classList.contains("tipopen");
        for (const r of host.querySelectorAll(".setting.tipopen")) { r.classList.remove("tipopen"); r.querySelector(".key").setAttribute("aria-expanded", "false"); }
        row.classList.toggle("tipopen", on); key.setAttribute("aria-expanded", String(on));
      });
      key.addEventListener("keydown", (e) => { if (e.key === "Escape" && row.classList.contains("tipopen")) { e.stopPropagation(); key.click(); } });
      row.append(key, input.node, save, tip);
      save.addEventListener("click", async () => {
        save.disabled = true;
        if (await saveSetting(s.section, st.key, input.value(), $("setmsg"))) { st.value = input.value(); row.classList.toggle("changed", isChanged(st)); }
        else save.disabled = false;
      });
      grid.append(row);
    }
    d.append(grid);
    return d;
  }));
  renderSettingTabs(sections);
  applyFilter();
  for (const d of host.querySelectorAll("details.section")) if (open.has(d.dataset.section) && !d.hidden) d.open = true; // a redraw keeps what was open
}

function renderSettingTabs(sections) {
  const count = (id) => sections.filter((s) => id === "all" || tabOf(s.section) === id).reduce((n, s) => n + s.settings.length, 0);
  const tabs = [{ id: "all", label: "All" }, ...SETTING_TABS, { id: "other", label: "Other" }].filter((t) => count(t.id) > 0);
  if (!tabs.some((t) => t.id === settingTab)) settingTab = "all";
  $("settabs").replaceChildren(...tabs.map((t) => {
    const b = el("button", { type: "button", role: "tab", class: "settab", "aria-selected": String(t.id === settingTab) },
      t.label, el("span", { class: "count", text: String(count(t.id)) }));
    b.addEventListener("click", () => { settingTab = t.id; renderSettingTabs(sections); applyFilter(); });
    return b;
  }));
}

function applyFilter() {
  const q = $("filter").value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  for (const d of document.querySelectorAll("details.section")) {
    const inTab = q.length > 0 || settingTab === "all" || d.dataset.tab === settingTab; // a search looks in every tab
    let any = false;
    for (const row of d.querySelectorAll(".setting")) {
      const show = inTab && q.every((w) => row.dataset.search.includes(w));
      row.hidden = !show; any = any || show;
    }
    d.hidden = !any;
    d.open = (q.length > 0 || settingTab !== "all") && any;
  }
}

// ---- settings sets (saved to the pilot's account) and settings files ------------------------------
let settingSets = [];

async function loadSettingSets(selectId) {
  const { body } = await getJson(API + "/settingsets");
  if (!body?.ok) return;
  settingSets = body.sets || [];
  const list = $("setlist");
  const keep = selectId != null ? String(selectId) : list.value;
  list.replaceChildren(...(settingSets.length
    ? settingSets.map((s) => el("option", { value: String(s.id), text: `${s.name} (${s.count})` }))
    : [el("option", { value: "", text: "No sets yet" })]));
  if (settingSets.some((s) => String(s.id) === keep)) list.value = keep;
  for (const id of ["setapply", "setrename", "setdelete"]) $(id).disabled = !settingSets.length;
}
const pickedSet = () => settingSets.find((s) => String(s.id) === $("setlist").value);

// settings the page shows as Section:Key -> value, for "Save as a set"
function currentSettings(which) {
  const out = {};
  for (const s of lastSettings) for (const st of s.settings) {
    if (st.value == null) continue;
    if (which === "changed" && !isChanged(st)) continue;
    if (which === "shown") {
      const row = document.querySelector(`.setting[data-key="${CSS.escape(`${s.section}:${st.key}`)}"]`);
      if (!row || row.hidden || row.closest("details").hidden) continue;
    }
    out[`${s.section}:${st.key}`] = st.value;
  }
  return out;
}

function openSaveSet(fixed) {
  const dlg = $("savesetdlg");
  const counts = { changed: Object.keys(currentSettings("changed")).length, shown: Object.keys(currentSettings("shown")).length,
    all: Object.keys(currentSettings("all")).length };
  $("savesetwhich").hidden = !!fixed;
  if (fixed) $("savesetsub").textContent = `${Object.keys(fixed).length} settings from ${$("diffsub").dataset.source || "the file"}.`;
  else {
    $("savesetsub").textContent = `From ${current.arena}.`;
    for (const k of Object.keys(counts)) $("savesetcount-" + k).textContent = String(counts[k]);
    if (!counts.changed) document.querySelector('input[name="savesetwhich"][value="all"]').checked = true;
  }
  const replace = $("savesetreplace");
  replace.replaceChildren(el("option", { value: "", text: "A new set" }),
    ...settingSets.map((s) => el("option", { value: String(s.id), text: `Replace ${s.name}` })));
  $("savesetname").value = fixed ? ($("diffsub").dataset.name || "") : current.arena + " settings";
  msg($("savesetmsg"), "", true);
  dlg._fixed = fixed || null;
  dlg.showModal();
  $("savesetname").select();
}

async function saveSetSubmit(e) {
  e.preventDefault();
  const dlg = $("savesetdlg");
  const which = document.querySelector('input[name="savesetwhich"]:checked')?.value || "changed";
  const settings = dlg._fixed || currentSettings(which);
  if (!Object.keys(settings).length) { msg($("savesetmsg"), "There are no settings to save in that choice.", false); return; }
  const replace = $("savesetreplace").value;
  if (replace && !await Dlg.confirm(`Replace ${settingSets.find((s) => String(s.id) === replace)?.name} with these ${Object.keys(settings).length} settings?`,
    { title: "Replace set", ok: "Replace", danger: true })) return;
  const { body } = await postJson("/settingsets/save", { name: $("savesetname").value, settings, id: replace || undefined });
  if (!body.ok) { msg($("savesetmsg"), body.message, false); return; }
  dlg.close();
  msg($("setsmsg"), body.message, true);
  if ($("diffdlg").open) msg($("diffmsg"), body.message, true); // saved from a file: say so where the pilot is looking
  await loadSettingSets(body.id);
}

// The diff dialog: what a set, a file or a list of settings would change, applied to the chosen arena.
const diff = { source: null, rows: [], fileSkipped: [] };

async function openDiff(source, title, name) {
  diff.source = source;
  $("difftitle").textContent = title;
  $("diffsub").dataset.source = title.replace(/^Apply /, "");
  $("diffsub").dataset.name = name || "";
  const target = $("difftarget");
  target.replaceChildren(...current.arenas.map((a) => el("option", { value: a.name, text: a.name + (a.name === current.arena ? " (this arena)" : "") })));
  target.value = current.arena;
  msg($("diffmsg"), "", true);
  if (!await previewDiff()) return;
  if (!$("diffdlg").open) $("diffdlg").showModal();
}

async function previewDiff() {
  const dlg = $("diffdlg");
  dlg.classList.add("busy");
  const { body } = await postJson("/settings/preview", { arena: $("difftarget").value, ...diff.source });
  dlg.classList.remove("busy");
  if (!body?.ok) {
    if (dlg.open) msg($("diffmsg"), body?.message || "That didn't work.", false);
    else msg($("setsmsg"), body?.message || "That didn't work.", false);
    return false;
  }
  diff.rows = body.rows; diff.fileSkipped = body.fileSkipped || [];
  msg($("diffmsg"), "", true);
  renderDiff();
  return true;
}

function renderDiff() {
  const changes = diff.rows.filter((r) => r.status === "change");
  const same = diff.rows.filter((r) => r.status === "same");
  const skipped = diff.rows.filter((r) => r.status === "skipped");
  const show = (v) => (v == null ? "(unset)" : v === "" ? "(off)" : v);
  $("diffrows").replaceChildren(...(changes.length ? changes.map((r) => {
    const box = el("input", { type: "checkbox", "aria-label": `Apply ${r.section}:${r.key}` });
    box.checked = true; box.dataset.key = `${r.section}:${r.key}`;
    box.addEventListener("change", diffCount);
    return el("tr", null, el("td", null, box), el("td", { class: "dkey", text: `${r.section}:${r.key}` }),
      el("td", { class: "dold", text: show(r.old) }), el("td", { class: "dnew", text: show(r.value) }));
  }) : [el("tr", null, el("td", { colspan: "4", class: "dim", text: "Nothing would change: the arena already has every one of these values." }))]));
  $("diffall").checked = true;
  $("diffall").disabled = !changes.length;
  $("diffsame").hidden = !same.length;
  $("diffsamesum").textContent = `${same.length} already the same`;
  $("diffsamelist").replaceChildren(...same.map((r) => el("li", { text: `${r.section}:${r.key} = ${show(r.value)}` })));
  const skipName = (r) => (r.key ? `${r.section}:${r.key}` : r.section);
  const skips = [...skipped.map((r) => (r.reason.startsWith(skipName(r)) ? r.reason : `${skipName(r)}: ${r.reason}`)),
    ...diff.fileSkipped.map((s) => `Line ${s.line}, ${s.text}: ${s.reason}`)];
  $("diffskipped").hidden = !skips.length;
  $("diffskipsum").textContent = `${skips.length} skipped`;
  $("diffskiplist").replaceChildren(...skips.map((t) => el("li", { text: t })));
  $("diffsub").textContent = [`${changes.length} would change`, same.length && `${same.length} already the same`,
    skips.length && `${skips.length} skipped`].filter(Boolean).join(", ") + (changes.length > 1 ? ". Untick any you want to leave as they are." : ".");
  $("diffsaveset").hidden = !("text" in diff.source); // a set is already saved; a file can become one
  diffCount();
}

function diffChecked() {
  const keys = new Set([...$("diffrows").querySelectorAll("input[type=checkbox]:checked")].map((b) => b.dataset.key));
  return diff.rows.filter((r) => r.status === "change" && keys.has(`${r.section}:${r.key}`));
}
function diffCount() {
  const n = diffChecked().length;
  const total = diff.rows.filter((r) => r.status === "change").length;
  $("diffall").checked = n === total && total > 0;
  $("diffall").indeterminate = n > 0 && n < total;
  $("diffapply").disabled = n === 0;
  $("diffapply").textContent = n === 0 ? "Apply" : `Apply ${n} change${n === 1 ? "" : "s"}`;
}

async function applyDiff(e) {
  e.preventDefault();
  const rows = diffChecked();
  if (!rows.length) return;
  const arena = $("difftarget").value;
  const settings = Object.fromEntries(rows.map((r) => [`${r.section}:${r.key}`, r.value]));
  $("diffapply").disabled = true;
  const { body } = await postJson("/settings/apply", { arena, settings });
  if (!body.ok) { msg($("diffmsg"), webMessage(body.message), false); diffCount(); return; }
  $("diffdlg").close();
  msg($("setsmsg"), webMessage(body.message), true);
  if (arena === current.arena) {
    for (const r of body.rows || []) if (r.status === "change") { const st = findSetting(r.section, r.key); if (st) st.value = r.value; }
    renderSettings(lastSettings);
    refreshRestart();
  }
}

async function readSettingsFile(file) {
  if (!file) return;
  if (!/\.(conf|cfg|ini|set|txt)$/i.test(file.name)) { msg($("setsmsg"), "Drop a settings file: .conf, .cfg, .ini or .set.", false); return; }
  if (file.size > 256 * 1024) { msg($("setsmsg"), "A settings file must be 256 KB or smaller.", false); return; }
  const text = await file.text();
  if (text.includes("\u0000")) { msg($("setsmsg"), "That file isn't text; a settings file has [Section] and Key=Value lines.", false); return; }
  msg($("setsmsg"), "", true);
  await openDiff({ text }, `Apply ${file.name}`, file.name.replace(/\.[^.]+$/, ""));
}

function wireSettingSets() {
  const drop = $("setdrop"), input = $("setfile");
  drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", (e) => { e.preventDefault(); drop.classList.remove("over"); readSettingsFile(e.dataTransfer.files[0]); });
  drop.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); input.click(); } });
  input.addEventListener("change", () => { readSettingsFile(input.files[0]); input.value = ""; });
  $("setsave").addEventListener("click", () => openSaveSet(null));
  $("savesetform").addEventListener("submit", saveSetSubmit);
  $("setapply").addEventListener("click", () => { const s = pickedSet(); if (s) openDiff({ set: s.id }, `Apply ${s.name}`, s.name); });
  $("setrename").addEventListener("click", async () => {
    const s = pickedSet(); if (!s) return;
    const name = await Dlg.prompt(`New name for ${s.name}:`, { title: "Rename set", ok: "Rename", value: s.name, maxLength: 40 });
    if (name == null || !name.trim()) return;
    const { body } = await postJson("/settingsets/rename", { id: s.id, name });
    msg($("setsmsg"), body.message, body.ok);
    if (body.ok) loadSettingSets(s.id);
  });
  $("setdelete").addEventListener("click", async () => {
    const s = pickedSet(); if (!s) return;
    if (!await Dlg.confirm(`Delete ${s.name} (${s.count} settings) from your sets? Arenas keep the settings they have.`, { title: "Delete set", ok: "Delete", danger: true })) return;
    const { body } = await postJson("/settingsets/remove", { id: s.id });
    msg($("setsmsg"), body.message, body.ok);
    if (body.ok) loadSettingSets();
  });
  $("difftarget").addEventListener("change", previewDiff);
  $("diffall").addEventListener("change", () => {
    for (const b of $("diffrows").querySelectorAll("input[type=checkbox]")) b.checked = $("diffall").checked;
    diffCount();
  });
  $("diffform").addEventListener("submit", applyDiff);
  $("diffsaveset").addEventListener("click", () => {
    // what the file sets that this zone lets owners change: the checked changes and the ones already the same
    const rows = [...diffChecked(), ...diff.rows.filter((r) => r.status === "same")];
    if (!rows.length) { msg($("diffmsg"), "None of the file's settings can be saved: every one was skipped.", false); return; }
    openSaveSet(Object.fromEntries(rows.map((r) => [`${r.section}:${r.key}`, r.value])));
  });
  // a tapped-open help closes when tapping elsewhere
  document.addEventListener("click", (e) => {
    if (e.target.closest?.(".setting .key")) return;
    for (const r of document.querySelectorAll(".setting.tipopen")) { r.classList.remove("tipopen"); r.querySelector(".key").setAttribute("aria-expanded", "false"); }
  });
}

// ---- messages: Misc:GreetMessage and Misc:PeriodicMessage0-9 ("<every> <first after> <text>", minutes) ----
const PERIODIC_SLOTS = 10;
let lastSettings = [];
function parsePeriodic(v) {
  const m = /^\s*(-?\d+)\s+(-?\d+)\s+(.*\S)\s*$/.exec(v || "");
  return m ? { every: +m[1], first: +m[2], text: m[3] } : null;
}

function renderMessages(sections) {
  const misc = sections.find((s) => s.section.toLowerCase() === "misc")?.settings || [];
  const value = (key) => misc.find((st) => st.key.toLowerCase() === key.toLowerCase())?.value ?? "";
  const writable = misc.some((st) => /^greetmessage$/i.test(st.key));
  $("greetform").hidden = !writable;
  $("greettext").value = value("GreetMessage");
  const list = $("periodiclist");
  const slots = [];
  for (let i = 0; i < PERIODIC_SLOTS; i++) {
    const key = `PeriodicMessage${i}`;
    if (!misc.some((st) => st.key.toLowerCase() === key.toLowerCase())) continue;
    const raw = value(key);
    slots.push({ i, key, raw, parsed: parsePeriodic(raw) });
  }
  const rows = slots.filter((s) => s.raw.trim() !== "").map((s) => periodicRow(s, false));
  const free = slots.filter((s) => s.raw.trim() === "");
  if (free.length) {
    const add = el("button", { type: "button", class: "small", text: "Add a timed message" });
    add.addEventListener("click", () => { add.replaceWith(periodicRow(free[0], true)); });
    rows.push(el("div", { class: "row" }, add));
  }
  if (!writable) rows.unshift(el("p", { class: "dim", text: "This arena's messages can't be changed here." }));
  list.replaceChildren(...rows);
}

function periodicRow(slot, fresh) {
  const p = slot.parsed || { every: 5, first: 0, text: slot.raw.trim() };
  const on = el("input", { type: "checkbox", role: "switch", "aria-label": `Message ${slot.i + 1} on` });
  on.checked = fresh || p.every > 0;
  const every = el("input", { type: "number", min: "1", max: "1440", step: "1", value: String(Math.abs(p.every) || 5), "aria-label": "Every (minutes)" });
  const first = el("input", { type: "number", min: "0", max: "1440", step: "1", value: String(Math.max(0, p.first)), "aria-label": "First after (minutes)" });
  const text = el("input", { type: "text", maxlength: "190", value: p.text, placeholder: "What to say", "aria-label": "Message text" });
  const save = el("button", { type: "button", class: "small", text: "Save" });
  const remove = el("button", { type: "button", class: "small danger", text: fresh ? "Cancel" : "Remove" });
  const row = el("div", { class: "pmsg" + (on.checked ? "" : " off") },
    el("label", { class: "switch bot-switch", title: "On or off" }, on, el("span", { class: "track", "aria-hidden": "true" })),
    el("label", { class: "num" }, "Every", every, "min"),
    el("label", { class: "num" }, "First after", first, "min"),
    el("span"), save, remove, text);
  const write = async (v) => {
    save.disabled = remove.disabled = on.disabled = true;
    const ok = await saveSetting("Misc", slot.key, v, $("msgmsg"));
    save.disabled = remove.disabled = on.disabled = false;
    if (ok) { slot.raw = v; slot.parsed = parsePeriodic(v); }
    return ok;
  };
  const compose = () => {
    const n = Math.round(+every.value), f = Math.round(+first.value), t = text.value.trim();
    if (!t) { msg($("msgmsg"), "Write the message first.", false); return null; }
    if (!(n >= 1) || !(f >= 0)) { msg($("msgmsg"), "Every must be at least 1 minute and First after 0 or more.", false); return null; }
    return `${on.checked ? n : -n} ${f} ${t}`; // the server skips an interval of 0 or less: off, but remembered
  };
  save.addEventListener("click", async () => {
    const v = compose();
    if (v && await write(v) && fresh) renderMessages(lastSettings);
  });
  text.addEventListener("keydown", (e) => { if (e.key === "Enter") save.click(); });
  on.addEventListener("change", async () => {
    row.classList.toggle("off", !on.checked);
    if (fresh && slot.raw.trim() === "") return; // not saved yet: Save writes it
    const v = compose();
    if (!v || !await write(v)) { on.checked = !on.checked; row.classList.toggle("off", !on.checked); }
  });
  remove.addEventListener("click", async () => {
    if (fresh && slot.raw.trim() === "") { renderMessages(lastSettings); return; }
    if (!await Dlg.confirm(`Remove timed message ${slot.i + 1}?`, { title: "Remove message", ok: "Remove", danger: true })) return;
    if (await write("")) renderMessages(lastSettings);
  });
  return row;
}

async function removeLvz(name) {
  if (!await Dlg.confirm(`Remove ${name} from ${current.arena}? The file is deleted.`, { title: "Remove graphics file", ok: "Remove", danger: true })) return;
  const { body } = await postJson("/rmlvz", { arena: current.arena, name });
  msg($("lvzmsg"), webMessage(body.message), body.ok);
  if (body.ok) mePage(current.arena);
}

// ---- Tile Editor tab: the tile studio (tile-studio.js), mounted on the first visit --------------------
let studio = null;
function openTiles() {
  if (!window.TileStudio || !current?.arena) return;
  if (!studio) studio = TileStudio.mount($("tilestudio"), { arena: current.arena, onMapSaved: () => loadLibrary() });
  $("tilespage").href = "/arena/tiles?arena=" + encodeURIComponent(current.arena);
  studio.setArena(current.arena, { open: true });
}

// ---- your maps: a dropped .lvl goes to the pilot's collection and opens as a draft in the editor ----
const editorUrl = (arena, id) => "/arena/map?arena=" + encodeURIComponent(arena) + (id ? "&lib=" + id : "");
const kb = (n) => n >= 1024 * 1024 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB";

async function loadLibrary() {
  const list = $("maplib");
  if (!list || $("mapowner").hidden) return;
  const { body } = await getJson(API + "/library");
  if (!body?.ok) { list.replaceChildren(el("li", { class: "dim", text: "Couldn't load your maps." })); return; }
  const arena = current.arena;
  const applied = await appliedMapId(arena, body.maps);
  if (current?.arena !== arena) return;
  fill(list, body.maps, (m) => {
    const open = el("a", { class: "btn small", href: editorUrl(current.arena, m.id), text: "Open in editor" });
    let apply;
    if (m.id === applied) apply = el("span", { class: "tag Active", text: "Applied", title: `${arena} is using this map` });
    else {
      apply = el("button", { type: "button", class: "small", text: "Apply", "aria-label": `Apply ${m.name} to ${arena}` });
      apply.addEventListener("click", () => applyLibraryMap(m, apply));
    }
    const rm = el("button", { type: "button", class: "small danger", text: "Remove", "aria-label": "Remove " + m.name });
    rm.addEventListener("click", async () => {
      if (!await Dlg.confirm(`Remove ${m.name} from your maps? Arenas using it keep their copy.`, { title: "Remove map", ok: "Remove", danger: true })) return;
      const { body: r } = await postJson("/library/remove", { id: m.id });
      msg($("mapmsg"), r?.message, r?.ok);
      loadLibrary();
    });
    return el("li", null, el("span", { class: "name", text: m.name }),
      el("span", { class: "dim grow", text: `${kb(m.size)} · ${when(m.updated)}` }), apply, open, rm);
  }, "No maps yet. Maps you drop here, or add, download or publish in the editor, are kept here.");
}

// Which library map the arena is using now: the one whose bytes match the arena's map file
// (Publish copies the bytes in as <arena>.lvl, so no name links them). Only same-size maps are fetched.
async function appliedMapId(arena, maps) {
  try {
    const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" });
    if (!r.ok) return null;
    const live = new Uint8Array(await r.arrayBuffer());
    for (const m of maps) {
      if (m.size !== live.length) continue;
      const f = await fetch(API + "/library/file?id=" + encodeURIComponent(m.id), { credentials: "same-origin" });
      if (!f.ok) continue;
      const bytes = new Uint8Array(await f.arrayBuffer());
      if (bytes.length === live.length && bytes.every((b, i) => b === live[i])) return m.id;
    }
  } catch { /* unknown: every map shows Apply */ }
  return null;
}

// Apply: the map editor's Publish without opening the editor (save as <arena>.lvl, then restart).
async function applyLibraryMap(m, button) {
  const arena = current.arena;
  if (!await Dlg.confirm(`Apply ${m.name} to ${arena}? It replaces the arena's current map, then the arena restarts so it loads.`, { title: "Apply map", ok: "Apply and restart" })) return;
  button.disabled = true;
  msg($("mapmsg"), `Applying ${m.name}…`, true);
  try {
    const f = await fetch(API + "/library/file?id=" + encodeURIComponent(m.id), { credentials: "same-origin" });
    if (!f.ok) { msg($("mapmsg"), "Couldn't open that map from your maps.", false); return; }
    const name = arena.replace(/^#/, "") + ".lvl";
    const { body } = await post(`${API}/upload?kind=map&arena=${encodeURIComponent(arena)}&name=${encodeURIComponent(name)}`,
      await f.arrayBuffer(), "application/octet-stream");
    msg($("mapmsg"), body?.ok ? `Applied ${m.name}.` : webMessage(body?.message), body?.ok);
    if (!body?.ok) return;
    await mePage(arena);
    startRestart(false); // nobody inside: restarts at once; pilots inside: the restart dialog asks
  } finally { button.disabled = false; }
}

async function addToLibrary(file, out) {
  if (!file.name.toLowerCase().endsWith(".lvl")) { msg(out, "Choose a .lvl file.", false); return; }
  msg(out, `Adding ${file.name} to your maps…`, true);
  const { body } = await post(`${API}/library/save?name=${encodeURIComponent(file.name)}`, file, "application/octet-stream");
  msg(out, body?.message, body?.ok);
  if (body?.ok) location.href = editorUrl(current.arena, body.id); // a draft: nothing is published yet
  else loadLibrary();
}

async function upload(file, kind, out) {
  if (kind === "map") return addToLibrary(file, out);
  const lower = file.name.toLowerCase();
  if (!lower.endsWith(".lvz")) { msg(out, "Choose a .lvz file.", false); return; }
  msg(out, `Uploading ${file.name}…`, true);
  const url = `${API}/upload?kind=${kind}&arena=${encodeURIComponent(current.arena)}&name=${encodeURIComponent(file.name)}`;
  const { body } = await post(url, file, "application/octet-stream");
  msg(out, webMessage(body.message), body.ok);
  if (body.ok) mePage(current.arena);
}

function wireDrop(drop, input, kind, out) {
  drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", (e) => {
    e.preventDefault(); drop.classList.remove("over");
    if (e.dataTransfer.files.length) upload(e.dataTransfer.files[0], kind, out);
  });
  input.addEventListener("change", (e) => { if (e.target.files.length) upload(e.target.files[0], kind, out); });
}

async function createArena(form, nameInput, ownerInput, out, after) {
  const { body } = await postJson("/staff/create", { arena: nameInput.value, owner: ownerInput.value.trim() || null });
  msg(out, body.message, body.ok);
  if (body.ok) { form.reset(); after(); }
}

function showPrivacy(isPrivate) {
  $("privacy").checked = isPrivate;
  $("privacylabel").textContent = isPrivate ? "Private" : "Public";
  $("privacynote").textContent = isPrivate
    ? "Left off the site's lists. Anyone who knows the name can still enter."
    : "Listed on the site for everyone.";
}

function wireHub() {
  $("privacy").addEventListener("change", async (e) => {
    const want = e.target.checked;
    const { body } = await postJson("/visibility", { arena: current.arena, private: want });
    msg($("ownermsg"), body.message, body.ok);
    showPrivacy(body.ok ? want : !want);
  });
  $("discardform").addEventListener("submit", async (e) => {
    e.preventDefault();
    const { body } = await postJson("/discard", { arena: current.arena, confirm: $("discardname").value.trim() });
    msg($("ownermsg"), body.message, body.ok);
    if (body.ok) setTimeout(() => { location.hash = ""; mePage(); }, 1200);
  });
  window.addEventListener("hashchange", route);
  $("filter").addEventListener("input", applyFilter);
  $("greetform").addEventListener("submit", (e) => { e.preventDefault(); saveSetting("Misc", "GreetMessage", $("greettext").value, $("msgmsg")); });
  $("greetclear").addEventListener("click", async () => {
    if (await saveSetting("Misc", "GreetMessage", "", $("msgmsg"))) $("greettext").value = "";
  });
  $("arenapick").addEventListener("change", async (e) => {
    const pick = e.currentTarget;
    const arena = pick.value;
    pick.value = current.arena;
    pick.disabled = true;
    try {
      await mePage(arena);
      if (current?.arena === arena) {
        const url = new URL(location.href);
        url.searchParams.set("arena", arena);
        history.replaceState(null, "", url);
      }
    } finally {
      pick.disabled = false;
    }
  });
  wireDrop($("mapdrop"), $("mapfile"), "map", $("mapmsg"));
  wireDrop($("lvzdrop"), $("lvzfile"), "lvz", $("lvzmsg"));
  $("comodform").addEventListener("submit", (e) => { e.preventDefault(); changeCoMod("add", $("comodname").value.trim()); });
  $("requestform").addEventListener("submit", async (e) => {
    e.preventDefault();
    const { body } = await postJson("/request", { arena: $("reqname").value, pitch: $("reqpitch").value });
    msg($("reqmsg"), body.message, body.ok);
    if (body.ok) { location.hash = "#overview"; mePage(); }
  });
  $("createform").addEventListener("submit", (e) => {
    e.preventDefault();
    createArena(e.target, $("crname"), $("crowner"), $("crmsg"), () => mePage());
  });
}

document.addEventListener("DOMContentLoaded", () => {
  const page = document.body.dataset.page;
  // /arena/me?arena=%23name (from the builder page) opens that arena, e.g. your draft
  if (page === "me") { wireBots(); wireHub(); wireRestart(); wireGraphics(); wireSettingSets(); mePage(new URLSearchParams(location.search).get("arena") || undefined); }
  else publicPage();
});
