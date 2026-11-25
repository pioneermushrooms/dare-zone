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
  if (status === 403 && body && body.needsEmail) {
    $("signin").hidden = false; $("hub").hidden = true;
    $("signinmsg").replaceChildren(el("a", { href: "/account/", text: "Add a recovery email" }), " to your account first.");
    return;
  }
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
  if (hasArena) renderArena(body); else renderNoArena(body);
  route();
}

function route() {
  if (!current) return;
  const want = location.hash.slice(1);
  const shown = showView(want, current.arena ? "overview" : "make");
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
    + ` · co-mods ${body.coMods.length}/5 · last visited ${when(body.lastEntered)}`;
  $("gocmd").textContent = `?go ${body.arena}`;
  const pick = $("arenapick");
  pick.hidden = body.arenas.length < 2;
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
async function drawThumb(arena) {
  const c = $("thumb");
  const ctx = c.getContext("2d");
  ctx.fillStyle = "#04070e"; ctx.fillRect(0, 0, c.width, c.height);
  try {
    const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" });
    if (!r.ok || current?.arena !== arena) return;
    Lvl.drawOverview(c, Lvl.parse(new Uint8Array(await r.arrayBuffer())).grid, true);
  } catch { /* leave it dark */ }
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
  $("arenapick").addEventListener("change", (e) => mePage(e.target.value));
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
  if (page === "me") { wireHub(); mePage(new URLSearchParams(location.search).get("arena") || undefined); }
  else publicPage();
});
