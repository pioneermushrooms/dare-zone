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

async function mePage(arena) {
  const { status, body } = await getJson(API + "/me" + (arena ? "?arena=" + encodeURIComponent(arena) : ""));
  if (status === 401) { $("signin").hidden = false; $("hub").hidden = true; return; }
  if (!body) return;
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
  if (body.configMissing) msg($("setmsg"), "This arena's settings file is missing; ask staff.", false);
  // crew
  renderCrew(body.coMods, owner);
  const editor = "/arena/map?arena=" + encodeURIComponent(body.arena);
  $("openeditor").href = editor; $("viewmap").href = editor;
  drawThumb(body.arena);
  renderRestart(body);
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
  $("botlabel").value = bot.label;
  for (const line of botLines(bot.script)) {
    const [, key, value] = /^(\S+) +(.*)$/.exec(line) || [];
    switch (key) {
      case "ship": $("botship").value = value; break;
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
  $("botform").addEventListener("reset", () => setTimeout(botFields, 0));
  botFields();
  $("botform").onsubmit = (event) => {
    event.preventDefault();
    const script = ["ship " + $("botship").value, "behaviour " + $("botmode").value];
    for (const point of botLines($("botpoints").value)) script.push("point " + point);
    if ($("botrange").value) script.push("range " + $("botrange").value);
    if ($("botgreet").value.trim()) script.push("greet " + $("botgreet").value.trim());
    if ($("botcasual").checked) script.push("casual " + $("botactivity").value + " " + $("botpersonality").value);
    if ($("botcasual").checked && $("botchat").checked) script.push("botchat on");
    for (const line of botLines($("botreply").value)) {
      const match = /^(\S+)\s*\|\s*(.+)$/.exec(line);
      if (!match) return msg($("botmsg"), "Use | between the trigger and the custom message.", false);
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

async function loadGraphics() {
  const arena = current?.arena;
  if (!arena) return;
  const host = $("gfxgroups");
  if (gfxArena !== arena) { host.replaceChildren(el("p", { class: "dim", text: "Loading…" })); gfxArena = arena; }
  const { body } = await getJson(API + "/graphics?arena=" + encodeURIComponent(arena));
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
}

function gfxCard(arena, slot, owner) {
  const canvas = el("canvas", { width: "1", height: "1", "aria-label": slot.label + " now" });
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
      if (f) await setGraphic(arena, slot, f);
    });
    const acts = el("div", { class: "acts" }, up, file);
    if (slot.generate.length) {
      const gen = el("select", { "aria-label": "Generate a new " + slot.label });
      gen.append(el("option", { value: "", text: "Generate…" }), ...slot.generate.map((m) => el("option", { value: m, text: GEN_LABELS[m] || m })));
      gen.addEventListener("change", () => { const m = gen.value; gen.value = ""; if (m) openGenerator(arena, slot, m); });
      acts.append(gen);
    }
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
  return card;
}

// The arena's current picture (or the stock one) onto a canvas; ships' stock pictures share one file.
async function drawGraphic(canvas, arena, slot) {
  try {
    const r = await fetch(`${API}/graphics/image?arena=${encodeURIComponent(arena)}&name=${slot.name}`, { credentials: "same-origin" });
    if (!r.ok) return;
    const img = await createImageBitmap(await r.blob());
    const ship = /^ship([1-8])$/.exec(slot.name);
    let sx = 0, sy = 0, sw = img.width, sh = img.height;
    if (ship && r.headers.get("X-Graphic-Source") === "stock") { sh = img.height / 8; sy = (Number(ship[1]) - 1) * sh; }
    paintSheet(canvas, img, sx, sy, sw, sh);
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

async function setGraphic(arena, slot, blob) {
  msg($("gfxmsg"), `Saving ${slot.label}…`, true);
  const { body } = await post(`${API}/graphics/set?arena=${encodeURIComponent(arena)}&name=${slot.name}`, blob, "application/octet-stream");
  msg($("gfxmsg"), webMessage(body?.message), body?.ok);
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
    const { body } = await postJson("/graphics/generate", { arena: run.arena, name: run.slot.name, mode: run.mode, prompt: run.prompt || null });
    if (gen !== run) return;
    if (!body?.ok || !body.sheet) { msg($("gfxdlgmsg"), body?.message || "Couldn't draw that.", false); return; }
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
    if (!gen.prompt) return msg($("gfxdlgmsg"), "Describe what to draw.", false);
    runGenerator();
  });
  $("gfxagain").addEventListener("click", runGenerator);
  $("gfxuse").addEventListener("click", async () => {
    if (!gen?.png) return;
    $("gfxuse").disabled = true;
    if (await setGraphic(gen.arena, gen.slot, new Blob([gen.png], { type: "image/png" }))) $("gfxdlg").close();
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

// ---- settings: grouped into tabs, with a switch for on/off settings and number boxes for numbers ----
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

async function saveSetting(section, key, value, node) {
  const { body } = await postJson("/settings", { arena: current.arena, section, key, value });
  msg(node, webMessage(body.message), body.ok);
  if (body.ok) {
    const st = lastSettings.find((x) => x.section.toLowerCase() === section.toLowerCase())
      ?.settings.find((x) => x.key.toLowerCase() === key.toLowerCase());
    if (st) st.value = value;
  }
  if (body.ok && /recyclearena/.test(body.message || "")) refreshRestart();
  return body.ok;
}

function settingInput(s, st, saveBtn) {
  const label = `${s.section}:${st.key}`;
  const num = (v) => v != null && v !== "" && /^-?\d+$/.test(String(v));
  const isTrue = (v) => /^(1|true|yes|on|-?[1-9]\d*)$/i.test(String(v ?? "").trim());
  if (/^(true|false)$/i.test(String(st.default ?? "")) || (String(st.min) === "0" && String(st.max) === "1")) {
    const box = el("input", { type: "checkbox", role: "switch", "aria-label": label });
    box.checked = isTrue(st.value != null && st.value.trim() !== "" ? st.value : st.default);
    const wrap = el("label", { class: "switch bot-switch" }, box, el("span", { class: "track", "aria-hidden": "true" }));
    box.addEventListener("change", async () => {
      box.disabled = true;
      if (!await saveSetting(s.section, st.key, box.checked ? "1" : "0", $("setmsg"))) box.checked = !box.checked;
      box.disabled = false;
    });
    saveBtn.hidden = true;
    return { node: wrap, value: () => (box.checked ? "1" : "0") };
  }
  const numeric = num(st.min) || num(st.max) || (num(st.default) && (st.value == null || num(st.value)));
  const input = el("input", { type: numeric ? "number" : "text", value: st.value ?? "", maxlength: "64", "aria-label": label,
    placeholder: st.default != null ? String(st.default) : "" });
  if (numeric) { input.step = "1"; if (num(st.min)) input.min = st.min; if (num(st.max)) input.max = st.max; }
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") saveBtn.click(); });
  input.addEventListener("input", () => { saveBtn.disabled = input.value === (st.value ?? ""); });
  saveBtn.disabled = true;
  return { node: input, value: () => input.value };
}

function renderSettings(sections) {
  renderMessages(sections);
  sections = sections.map((s) => ({ ...s, settings: s.settings.filter((st) => !isMessageKey(s.section, st.key)) }))
    .filter((s) => s.settings.length);
  const host = $("settings");
  host.replaceChildren(...sections.map((s) => {
    const d = el("details", { class: "section", "data-tab": tabOf(s.section) }, el("summary", { text: `${s.section} (${s.settings.length})` }));
    for (const st of s.settings) {
      const save = el("button", { type: "button", class: "small", text: "Save" });
      const input = settingInput(s, st, save);
      const range = [st.min != null || st.max != null ? `range ${st.min ?? ""}–${st.max ?? ""}` : st.range ? `range ${st.range}` : "",
                     st.default != null ? `default ${st.default}` : ""].filter(Boolean).join(" · ");
      const row = el("div", { class: "setting", "data-search": `${s.section} ${st.key} ${st.description || ""}`.toLowerCase() },
        el("span", { class: "key", text: st.key }), input.node, save,
        el("span", { class: "help", text: (st.description || "") + (range ? ` (${range})` : "") }));
      save.addEventListener("click", async () => {
        save.disabled = true;
        if (await saveSetting(s.section, st.key, input.value(), $("setmsg"))) st.value = input.value();
        else save.disabled = false;
      });
      d.append(row);
    }
    return d;
  }));
  renderSettingTabs(sections);
  applyFilter();
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
  if (page === "me") { wireBots(); wireHub(); wireRestart(); wireGraphics(); mePage(new URLSearchParams(location.search).get("arena") || undefined); }
  else publicPage();
});
