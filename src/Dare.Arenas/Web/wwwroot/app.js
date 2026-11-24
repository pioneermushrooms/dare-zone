// Dare arenas web pages. All data is inserted with textContent (never innerHTML).
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

// ---- public page --------------------------------------------------------------------------
async function publicPage() {
  const { body } = await getJson(API + "/public");
  if (!body) return;
  fill($("arenas"), body.arenas, (a) => el("li", null,
      el("b", { class: "name", text: a.name }), " ", el("span", { class: "tag " + a.status, text: a.status }),
      el("div", { class: "dim", text: `owner ${a.owner}` + (a.coMods.length ? ` · co-mods ${a.coMods.join(", ")}` : "")
        + (a.status === "Active" ? ` · ${a.players} playing` : "") })), "No player-owned arenas yet.");
  fill($("pending"), body.pending, (r) => el("li", null,
      el("b", { class: "name", text: r.arena }), ` by ${r.player} · ${when(r.at)}`,
      el("div", { text: r.pitch })), "No pending requests.");
  fill($("decisions"), body.decisions, (r) => el("li", null,
      el("b", { class: "name", text: r.arena }), " ", el("span", { class: "tag " + r.status, text: r.status }),
      ` ${r.player} · by ${r.decidedBy || "?"}`, r.reason ? el("div", { class: "dim", text: r.reason }) : null),
      "No decisions yet.");
  fill($("events"), body.events, (e) => el("li", null,
      el("span", { class: "dim", text: when(e.at) + " " }), el("b", { class: "name", text: e.arena }),
      ` ${e.kind} by ${e.actor}`, e.detail ? el("div", { class: "dim", text: e.detail }) : null), "No events yet.");
}

// ---- owner page ---------------------------------------------------------------------------
let current = null;

async function mePage(arena) {
  const { status, body } = await getJson(API + "/me" + (arena ? "?arena=" + encodeURIComponent(arena) : ""));
  if (status === 401) { $("signin").hidden = false; return; }
  if (status === 403 && body && body.needsEmail) {
    $("signin").hidden = false;
    $("signinmsg").replaceChildren(el("a", { href: "/account/", text: "Add a recovery email" }), " to your account first.");
    return;
  }
  if (!body) return;
  $("who").textContent = "SIGNED IN AS " + String(body.player).toUpperCase();
  if (!body.arena) { noRole(body); return; }
  current = body;
  $("norole").hidden = true;
  $("owner").hidden = false;
  $("arenatitle").textContent = body.arena;
  $("arenameta").textContent = `${body.role === "owner" ? "You own it" : "You are a co-mod"} · owner ${body.owner}`
    + ` · co-mods (${body.coMods.length}/5): ${body.coMods.join(", ") || "none"} · last entered ${when(body.lastEntered)}`
    + ` · in game: ?go ${body.arena}`;
  const pick = $("arenapick");
  pick.hidden = body.arenas.length < 2;
  pick.replaceChildren(...body.arenas.map((a) => {
    const o = el("option", { value: a.name, text: `${a.name} (${a.role})` });
    if (a.name === body.arena) o.selected = true;
    return o;
  }));
  $("curmap").textContent = body.map || "(none)";
  const owner = body.role === "owner";
  renderLvz(body.levelFiles, body.limits.maxLevelFiles, owner);
  $("drop").hidden = !owner;
  $("uponly").hidden = owner;
  renderSettings(body.settings || []);
  if (body.configMissing) msg($("setmsg"), "This arena's settings file is missing; ask staff.", false);
}

function noRole(body) {
  $("norole").hidden = false;
  $("owner").hidden = true;
  $("pendinginfo").textContent = body.pendingRequest
    ? `Your request for '${body.pendingRequest.arena}' is pending (${when(body.pendingRequest.at)}).`
    : "You don't own or co-mod an arena yet. Request one:";
}

function renderSettings(sections) {
  const host = $("settings");
  host.replaceChildren(...sections.map((s) => {
    const d = el("details", { class: "section" }, el("summary", { text: `${s.section} (${s.settings.length})` }));
    for (const st of s.settings) {
      const input = el("input", { type: "text", value: st.value ?? "", maxlength: "64", "aria-label": `${s.section}:${st.key}` });
      const save = el("button", { type: "button", text: "Save" });
      const range = [st.min != null || st.max != null ? `range ${st.min ?? ""}–${st.max ?? ""}` : st.range ? `range ${st.range}` : "",
                     st.default != null ? `default ${st.default}` : ""].filter(Boolean).join(" · ");
      const row = el("div", { class: "setting", "data-search": `${s.section} ${st.key} ${st.description || ""}`.toLowerCase() },
        el("span", { class: "key", text: st.key }), input, save,
        el("span", { class: "help", text: (st.description || "") + (range ? ` (${range})` : "") }));
      save.addEventListener("click", async () => {
        save.disabled = true;
        const { body } = await post(API + "/settings",
          JSON.stringify({ arena: current.arena, section: s.section, key: st.key, value: input.value }), "application/json");
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

// Graphics list; owners get a remove button per file (same rules as ?rmlvz).
function renderLvz(files, max, owner) {
  const span = $("curlvz");
  if (!files.length) { span.replaceChildren("none"); return; }
  const parts = [];
  files.forEach((f, i) => {
    if (i) parts.push(", ");
    parts.push(el("span", { class: "name", text: f }));
    if (owner) {
      const b = el("button", { type: "button", class: "small", title: `Remove ${f}`, text: "remove" });
      b.addEventListener("click", () => removeLvz(f));
      parts.push(" ", b);
    }
  });
  parts.push(` (${files.length}/${max})`);
  span.replaceChildren(...parts);
}

async function removeLvz(name) {
  if (!confirm(`Remove ${name} from this arena? The file is deleted.`)) return;
  const { body } = await post(API + "/rmlvz", JSON.stringify({ arena: current.arena, name }), "application/json");
  msg($("upmsg"), body.message, body.ok);
  if (body.ok) mePage(current.arena);
}

async function upload(file) {
  const lower = file.name.toLowerCase();
  const kind = lower.endsWith(".lvl") ? "map" : lower.endsWith(".lvz") ? "lvz" : null;
  if (!kind) { msg($("upmsg"), "Choose a .lvl or .lvz file.", false); return; }
  msg($("upmsg"), `Uploading ${file.name}…`, true);
  const url = `${API}/upload?kind=${kind}&arena=${encodeURIComponent(current.arena)}&name=${encodeURIComponent(file.name)}`;
  const { body } = await post(url, file, "application/octet-stream");
  msg($("upmsg"), body.message, body.ok);
  if (body.ok) mePage(current.arena);
}

function wireOwner() {
  $("filter").addEventListener("input", applyFilter);
  $("arenapick").addEventListener("change", (e) => mePage(e.target.value));
  const drop = $("drop");
  drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", (e) => {
    e.preventDefault(); drop.classList.remove("over");
    if (e.dataTransfer.files.length) upload(e.dataTransfer.files[0]);
  });
  $("file").addEventListener("change", (e) => { if (e.target.files.length) upload(e.target.files[0]); });
  $("requestform").addEventListener("submit", async (e) => {
    e.preventDefault();
    const { body } = await post(API + "/request",
      JSON.stringify({ arena: $("reqname").value, pitch: $("reqpitch").value }), "application/json");
    msg($("reqmsg"), body.message, body.ok);
    if (body.ok) mePage();
  });
}

document.addEventListener("DOMContentLoaded", () => {
  // /arena/me?arena=%23name (from the /build/ page) opens that arena, e.g. your draft
  if (document.body.dataset.page === "me") { wireOwner(); mePage(new URLSearchParams(location.search).get("arena") || undefined); } else { publicPage(); }
});
