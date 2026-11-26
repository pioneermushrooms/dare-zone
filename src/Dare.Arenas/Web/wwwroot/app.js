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
  $("stafflink").hidden = !(staff.approve || staff.decline || staff.create);
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
  $("arenatitle").textContent = body.arena;
  const st = $("arenastatus");
  st.textContent = body.status || ""; st.className = "tag " + (body.status || "");
  $("arenameta").textContent = `${owner ? "You own it" : "You are a co-mod"} · owner ${body.owner}`
    + ` · last visited ${when(body.lastEntered)}`;
  $("gocmd").textContent = `?go ${body.arena}`;
  const pick = $("arenapick");
  $("overview-comods").textContent = body.coMods.join(", ") || "None";
  $("overview-map").textContent = body.map || "No map";
  $("overview-graphics").textContent = String(body.levelFiles.length);
  $("botstile").hidden = $("botnav").hidden;
  $("overview-bots-label").hidden = $("botnav").hidden;
  $("overview-bots").hidden = $("botnav").hidden;
  pick.replaceChildren(...body.arenas.map((a) => {
    const o = el("option", { value: a.name, text: `${a.name} (${a.role})` });
    if (a.name === body.arena) o.selected = true;
    return o;
  }));
  // make: up to maxPerPilot public arenas per pilot (drafts, named #..., don't count)
  const max = body.maxPerPilot || 3;
  const owned = body.arenas.filter((a) => a.role === "owner" && !a.name.startsWith("#")).length;
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
  renderSettings(body.settings || []);
  if (body.configMissing) msg($("setmsg"), "This arena's settings file is missing; ask staff.", false);
  // crew
  renderCrew(body.coMods, owner);
  const editor = "/arena/map?arena=" + encodeURIComponent(body.arena);
  $("openeditor").href = editor; $("viewmap").href = editor;
  drawThumb(body.arena);
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
  if (action === "remove" && !confirm(`Remove ${name} as co-mod of ${current.arena}?`)) return;
  const { body } = await postJson("/comod", { arena: current.arena, action, player: name });
  msg($("comodmsg"), body.message, body.ok);
  if (body.ok) { $("comodname").value = ""; mePage(current.arena); }
}

function renderSettings(sections) {
  const host = $("settings");
  host.replaceChildren(...sections.map((s) => {
    const d = el("details", { class: "section" }, el("summary", { text: `${s.section} (${s.settings.length})` }));
    for (const st of s.settings) {
      const input = el("input", { type: "text", value: st.value ?? "", maxlength: "64", "aria-label": `${s.section}:${st.key}` });
      const save = el("button", { type: "button", class: "small", text: "Save" });
      const range = [st.min != null || st.max != null ? `range ${st.min ?? ""}–${st.max ?? ""}` : st.range ? `range ${st.range}` : "",
                     st.default != null ? `default ${st.default}` : ""].filter(Boolean).join(" · ");
      const row = el("div", { class: "setting", "data-search": `${s.section} ${st.key} ${st.description || ""}`.toLowerCase() },
        el("span", { class: "key", text: st.key }), input, save,
        el("span", { class: "help", text: (st.description || "") + (range ? ` (${range})` : "") }));
      save.addEventListener("click", async () => {
        save.disabled = true;
        const { body } = await postJson("/settings", { arena: current.arena, section: s.section, key: st.key, value: input.value });
        msg($("setmsg"), body.message, body.ok);
        save.disabled = false;
      });
      input.addEventListener("keydown", (e) => { if (e.key === "Enter") save.click(); });
      d.append(row);
    }
    return d;
  }));
}

function applyFilter() {
  const q = $("filter").value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  for (const d of document.querySelectorAll("details.section")) {
    let any = false;
    for (const row of d.querySelectorAll(".setting")) {
      const show = q.every((w) => row.dataset.search.includes(w));
      row.hidden = !show; any = any || show;
    }
    d.hidden = !any;
    d.open = q.length > 0 && any;
  }
}

async function removeLvz(name) {
  if (!confirm(`Remove ${name} from ${current.arena}? The file is deleted.`)) return;
  const { body } = await postJson("/rmlvz", { arena: current.arena, name });
  msg($("lvzmsg"), body.message, body.ok);
  if (body.ok) mePage(current.arena);
}

async function upload(file, kind, out) {
  const lower = file.name.toLowerCase();
  if (!lower.endsWith(kind === "map" ? ".lvl" : ".lvz")) { msg(out, kind === "map" ? "Choose a .lvl file." : "Choose a .lvz file.", false); return; }
  msg(out, `Uploading ${file.name}…`, true);
  const url = `${API}/upload?kind=${kind}&arena=${encodeURIComponent(current.arena)}&name=${encodeURIComponent(file.name)}`;
  const { body } = await post(url, file, "application/octet-stream");
  msg(out, body.message, body.ok);
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
  if (page === "me") { wireBots(); wireHub(); mePage(new URLSearchParams(location.search).get("arena") || undefined); }
  else publicPage();
});
