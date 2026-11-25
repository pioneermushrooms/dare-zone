// The staff desk: decide waiting arena requests, create arenas, see every owned arena.
"use strict";
const API = "/arena/api";
const $ = (id) => document.getElementById(id);
const when = (t) => t ? new Date(t).toISOString().slice(0, 16).replace("T", " ") + " UTC" : "";

function el(tag, props = {}, ...children) {
  const node = Object.assign(document.createElement(tag), props);
  node.append(...children);
  return node;
}

async function call(path, payload) {
  const init = payload === undefined
    ? { credentials: "same-origin", cache: "no-store" }
    : { method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" },
        body: JSON.stringify(payload) };
  try {
    const r = await fetch(API + path, init);
    let body = null;
    try { body = await r.json(); } catch {}
    return { status: r.status, body };
  } catch {
    return { status: 0, body: { ok: false, message: "Couldn't reach Foundation; try again." } };
  }
}

function say(target, body) {
  target.textContent = body?.message ?? "";
  target.className = "msg " + (body?.ok ? "ok" : "bad");
}

function requestRow(r, can, maxReason) {
  const msg = el("p", { className: "msg", role: "status" });
  const actions = el("div", { className: "row" });
  if (r.fromDraft) actions.append(el("a", { href: "/?arena=" + encodeURIComponent("#" + r.arena), textContent: "Fly in #" + r.arena }));
  if (can.approve) {
    const approve = el("button", { type: "button", textContent: "Approve" });
    approve.addEventListener("click", async () => {
      approve.disabled = true;
      const { body } = await call("/staff/approve", { arena: r.arena, player: r.player });
      approve.disabled = false;
      say(msg, body);
      if (body?.ok) setTimeout(load, 1200);
    });
    actions.append(approve);
  }
  if (can.decline) {
    const reason = el("input", { type: "text", placeholder: "reason to decline", maxLength: maxReason, autocomplete: "off" });
    reason.setAttribute("aria-label", "Reason to decline " + r.arena);
    const decline = el("button", { type: "button", className: "hot", textContent: "Decline" });
    decline.addEventListener("click", async () => {
      decline.disabled = true;
      const { body } = await call("/staff/decline", { arena: r.arena, reason: reason.value, player: r.player });
      decline.disabled = false;
      say(msg, body);
      if (body?.ok) setTimeout(load, 1200);
    });
    actions.append(reason, decline);
  }
  return el("div", { className: "req" },
    el("div", {}, el("b", { className: "name", textContent: r.arena }), r.fromDraft ? " (from a draft)" : ""),
    el("div", { className: "who", textContent: `${r.player} · ${when(r.at)}` }),
    el("div", { textContent: r.pitch }),
    actions, msg);
}

async function load() {
  const { status, body } = await call("/staff");
  if (status !== 200 || !body) {
    $("desk").hidden = true;
    $("gate").hidden = false;
    $("gatemsg").replaceChildren(status === 401
      ? el("span", {}, "Sign in first: ", el("a", { href: "/account/login", textContent: "sign in" }), ", then come back.")
      : el("span", { textContent: body?.message ?? "This page is for zone staff." }));
    return;
  }
  $("gate").hidden = true;
  $("desk").hidden = false;
  $("who").textContent = "SIGNED IN AS " + String(body.player).toUpperCase();
  const pending = body.pending ?? [];
  $("pending").replaceChildren(...(pending.length
    ? pending.map((r) => requestRow(r, body.can, body.maxReason ?? 120))
    : [el("p", { className: "dim", textContent: "Nothing is waiting." })]));
  $("createpanel").hidden = !body.can.create;
  const arenas = body.arenas ?? [];
  $("arenas").replaceChildren(...(arenas.length
    ? arenas.map((a) => el("tr", {},
        el("td", {}, el("b", { className: "name", textContent: a.name })),
        el("td", { textContent: a.owner }),
        el("td", {}, el("span", { className: "tag " + a.status, textContent: a.status })),
        el("td", { textContent: String(a.players) })))
    : [el("tr", {}, el("td", { colSpan: 4, className: "dim", textContent: "No player-owned arenas yet." }))]));
}

$("createform").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = $("createform").querySelector("button");
  button.disabled = true;
  const { body } = await call("/staff/create", { arena: $("newname").value, owner: $("newowner").value });
  button.disabled = false;
  say($("createmsg"), body);
  if (body?.ok) { $("createform").reset(); load(); }
});
load();
