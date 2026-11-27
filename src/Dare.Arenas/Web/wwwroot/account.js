// Foundation account pages. All data is inserted with textContent (never innerHTML).
"use strict";

const API = "/account/api";
const $ = (id) => document.getElementById(id);
const store = {
  get(k) { try { return sessionStorage.getItem(k) || ""; } catch { return ""; } },
  set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* private mode */ } },
};
function msg(node, text, ok) { node.textContent = text || ""; node.className = "msg " + (ok ? "ok" : "bad"); }
function values(form) { return Object.fromEntries(new FormData(form).entries()); }

async function post(path, body) {
  let r;
  try {
    r = await fetch(API + path, {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" },
      body: JSON.stringify(body || {}),
    });
  } catch { return { status: 0, body: { ok: false, message: "Network error; try again." } }; }
  let data = null;
  try { data = await r.json(); } catch { data = { ok: false, message: "HTTP " + r.status }; }
  return { status: r.status, body: data };
}

// Wires a form: disables the button while waiting, shows the answer, then calls done(body, values).
// extra() adds fields to the request body (not to values).
function wire(form, out, path, done, hint, extra) {
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const btn = form.querySelector("button[type=submit]");
    const v = values(form);
    btn.disabled = true;
    msg(out, "…", true);
    const { body } = await post(path, extra ? { ...v, ...extra() } : v);
    btn.disabled = false;
    msg(out, body.ok && hint ? body.message + " " + hint : body.message, body.ok);
    if (body.ok && done) done(body, v);
  });
}

const NOTHING = "If nothing arrives in a few minutes, try again.";

// Where the pilot was going before signing in: ?next= on these pages (kept across sign-up and
// verify in sessionStorage). The server checks it and answers with the place to go (body.next);
// only a same-site path starting with a single "/" is ever followed.
const DEFAULT_NEXT = "/account/";
function wanted() {
  const q = new URLSearchParams(location.search).get("next");
  if (q) store.set("acct_next", q);
  return q || store.get("acct_next");
}
function safePath(p) { return typeof p === "string" && /^\/(?![\/\\])/.test(p) && !/[\\\s]/.test(p) ? p : ""; }
function goNext(body, fallback) {
  try { document.cookie = "dare_next=; Path=/; Max-Age=0; SameSite=Lax"; } catch { /* no cookies */ }
  store.set("acct_next", "");
  const next = safePath(body && body.next);
  location.href = next && next !== DEFAULT_NEXT ? next : fallback;
}
// carries ?next= on to the other sign-in pages' links
function keepNext() {
  const next = wanted();
  if (!next) return;
  for (const a of document.querySelectorAll('a[href^="/account/signup"], a[href^="/account/login"]')) {
    const u = new URL(a.getAttribute("href"), location.origin);
    u.searchParams.set("next", next);
    a.setAttribute("href", u.pathname + u.search + u.hash);
  }
}

const pages = {
  signup() {
    keepNext();
    const form = $("form");
    const noEmail = () => form.mode.value === "none";
    const show = () => {
      $("emailrow").hidden = noEmail();
      form.email.required = !noEmail();
      form.email.disabled = noEmail();
      $("noemailwarn").hidden = !noEmail();
      $("go").textContent = noEmail() ? "Claim name" : "Send code";
    };
    for (const r of form.querySelectorAll("input[name=mode]")) r.addEventListener("change", show);
    if (location.hash === "#noemail") form.mode.value = "none";
    show();
    form.addEventListener("submit", async (e) => {
      e.preventDefault();
      const btn = $("go");
      const v = values(form);
      btn.disabled = true;
      msg($("msg"), "…", true);
      const next = wanted();
      const { body } = await post("/signup", noEmail()
        ? { name: v.name, password: v.password, noEmail: true, next }
        : { name: v.name, password: v.password, email: v.email });
      btn.disabled = false;
      msg($("msg"), body.message, body.ok);
      if (!body.ok) return;
      if (noEmail()) { setTimeout(() => goNext(body, "/"), 1500); return; }
      store.set("acct_email", v.email); store.set("acct_sent", body.message);
      location.href = "/account/verify";
    });
  },
  verify() {
    $("form").email.value = store.get("acct_email");
    if (store.get("acct_sent")) $("sent").textContent = store.get("acct_sent") + " It is valid for 10 minutes. " + NOTHING;
    wire($("form"), $("msg"), "/verify", (b) => goNext(b, DEFAULT_NEXT), undefined, () => ({ next: wanted() }));
  },
  login() {
    keepNext();
    wire($("form"), $("msg"), "/login", (b) => goNext(b, DEFAULT_NEXT), undefined, () => ({ next: wanted() }));
  },
  forgot() {
    wire($("form"), $("msg"), "/forgot", (b, v) => {
      store.set("acct_email", v.email); store.set("acct_sent", b.message);
      location.href = "/account/reset";
    });
  },
  reset() {
    $("form").email.value = store.get("acct_email");
    if (store.get("acct_sent")) $("sent").textContent = store.get("acct_sent") + " Enter it below. " + NOTHING;
    wire($("form"), $("msg"), "/reset", () => { $("form").reset(); });
  },
  async profile() {
    let r;
    try { r = await fetch(API + "/me", { credentials: "same-origin" }); } catch { r = null; }
    if (!r || r.status === 401) { location.href = "/account/login"; return; }
    const me = await r.json();
    $("loading").hidden = true;
    $("profile").hidden = false;
    $("name").textContent = me.name;
    $("email").textContent = me.emailVerified ? me.email : "none yet";
    $("isoj").textContent = me.isojName || "not linked";
    $("emailbox").hidden = false;
    $("emailsum").textContent = me.emailVerified ? me.email : "None: a forgotten password can't be recovered. Add one any time.";
    $("pwbox").hidden = false;
    if (!me.emailVerified) {
      $("email").textContent = "none (a forgotten password can't be recovered)";
      $("emailtitle").textContent = "Add a recovery email";
      $("emailnote").textContent = "Optional. With one, you can reset a forgotten password; all your pilot names share it. We email a code to confirm it.";
    } else {
      $("emailtitle").textContent = "Recovery email";
      $("emailnote").textContent = "We email a code to the new address.";
    }
    wire($("emailform"), $("emailmsg"), "/email", () => { $("codeform").hidden = false; $("codeform").code.focus(); }, NOTHING);
    wire($("codeform"), $("emailmsg"), "/email/verify", () => { setTimeout(() => location.reload(), 900); });
    wire($("pwform"), $("pwmsg"), "/password", () => { $("pwform").reset(); });
    $("signout").addEventListener("click", async () => {
      await post("/logout", {});
      location.href = "/account/login";
    });
    await pilots(me);
    if (location.hash === "#pilots") { $("pilots").open = true; $("pilots").scrollIntoView(); }
  },
};

// ---- pilot names on this email ------------------------------------------------------------------
function el(tag, props, ...kids) {
  const n = document.createElement(tag);
  Object.assign(n, props || {});
  for (const k of kids) if (k != null) n.append(k);
  return n;
}

async function pilots(me) {
  const box = $("pilots");
  box.hidden = false;
  let data;
  try {
    const r = await fetch(API + "/pilots", { credentials: "same-origin", cache: "no-store" });
    data = await r.json();
  } catch { data = { ok: false }; }
  if (!data.ok) {
    msg($("pilotsmsg"), "Couldn't load your names.", false);
    $("pilotssum").textContent = "Couldn't load your names.";
    return;
  }
  $("pilotssum").textContent = `${data.pilots.length} name${data.pilots.length === 1 ? "" : "s"}${data.emailVerified ? " on your email" : ""} · signed in as ${data.current}`;
  if (!data.emailVerified)
    $("pilotsnote").textContent = "Fly as any of them; each keeps its own keys and macros. They share one account (no email: their passwords can't be recovered).";
  const names = data.pilots.map((p) => p.name);
  $("pilotlist").replaceChildren(...data.pilots.map((p) => {
    const current = p.name === data.current;
    const owned = p.arenas.length + p.drafts.length;
    const actions = el("span", { className: "row" });
    // each name has its own public profile (the others on this email are never shown there)
    actions.append(el("a", { className: "small profilelink", href: "/pilot/" + encodeURIComponent(p.name), textContent: "Profile" }));
    if (current) actions.append(el("span", { className: "tag Active", textContent: "signed in" }));
    else {
      const fly = el("button", { type: "button", className: "small", textContent: "Switch to" });
      fly.addEventListener("click", async () => {
        const { body } = await post("/pilots/switch", { name: p.name });
        if (body.ok) location.reload(); else msg($("pilotsmsg"), body.message, false);
      });
      actions.append(fly);
    }
    if (names.length > 1) {
      const del = el("button", { type: "button", className: "small", textContent: "Delete" });
      del.addEventListener("click", () => askDelete(p, names.filter((n) => n !== p.name)));
      actions.append(del);
    }
    return el("li", { className: "pilotrow" }, el("b", { className: "name", textContent: p.name }),
      el("span", { className: "dim", textContent: owned ? ` · ${p.arenas.length} arena${p.arenas.length === 1 ? "" : "s"}, ${p.drafts.length} draft${p.drafts.length === 1 ? "" : "s"}` : "" }),
      actions);
  }));
  if (!keysWired) {
    keysWired = true;
    wire($("addform"), $("pilotsmsg"), "/pilots/add", () => { $("addform").reset(); location.reload(); });
    await keysPanel(names, data.current);
  }
}
let keysWired = false;

let deleting = null;
function askDelete(p, others) {
  deleting = p;
  $("deletebox").hidden = false;
  $("deletetitle").textContent = `Delete ${p.name}?`;
  $("deletenote").textContent = "The name is freed at once: anyone can claim it. This can't be undone."
    + (p.arenas.length + p.drafts.length ? " Choose what happens to what it owns:" : "");
  const rows = [...p.arenas.map((a) => [a, a]), ...p.drafts.map((d) => [d, "#" + d + " (draft)"])].map(([name, label]) => {
    const pick = el("select", { name });
    pick.append(el("option", { value: "", textContent: "Archive it (files kept)" }));
    for (const o of others) pick.append(el("option", { value: o, textContent: `Keep it, owned by ${o}` }));
    return el("label", { className: "arenachoice" }, el("b", { className: "name", textContent: label }), pick);
  });
  $("deletearenas").replaceChildren(...rows);
  $("deleteconfirm").value = "";
  $("deleteconfirm").focus();
}

document.addEventListener("DOMContentLoaded", () => {
  if (!$("deleteform")) return;
  $("deletecancel").addEventListener("click", () => { $("deletebox").hidden = true; deleting = null; });
  $("deleteform").addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!deleting) return;
    if ($("deleteconfirm").value.trim().toLowerCase() !== deleting.name.toLowerCase()) {
      msg($("pilotsmsg"), `Type ${deleting.name} to confirm.`, false);
      return;
    }
    const keep = {};
    for (const s of $("deletearenas").querySelectorAll("select")) if (s.value) keep[s.name] = s.value;
    $("deletego").disabled = true;
    const { body } = await post("/pilots/delete", { name: deleting.name, keep });
    $("deletego").disabled = false;
    msg($("pilotsmsg"), body.message, body.ok);
    if (body.ok) setTimeout(() => location.reload(), 1200);
  });
});

document.addEventListener("DOMContentLoaded", () => {
  const f = pages[document.body.dataset.page];
  if (f) f();
});

// ---- keys and macros per pilot name (the game's [keys] and [macros] settings) -----------------
// The game's own defaults (oss-client game/input.rs): action, label, keys.
const ACTIONS = [
  ["forward", "Thrust forward", "ArrowUp"], ["backward", "Thrust backward", "ArrowDown"],
  ["left", "Turn left", "ArrowLeft"], ["right", "Turn right", "ArrowRight"],
  ["afterburner", "Afterburner", "ShiftLeft, ShiftRight"],
  ["gun", "Fire guns", "NoShift+ControlLeft, NoShift+ControlRight"],
  ["bomb", "Fire bomb", "NoShift+Tab"], ["mine", "Lay mine", "Shift+Tab"],
  ["repel", "Repel", "Shift+ControlLeft, Shift+ControlRight, Backquote"],
  ["burst", "Burst", "Shift+Delete"], ["thor", "Thor's hammer", "F6"], ["decoy", "Decoy", "F5"],
  ["rocket", "Rocket", "F3"], ["brick", "Brick", "F4"], ["portal", "Portal", "Shift+Insert"],
  ["warp", "Warp", "NoShift+Insert"], ["multifire", "Multifire", "NoShift+Delete"],
  ["stealth", "Stealth", "NoShift+Home"], ["cloak", "Cloak", "Shift+Home"],
  ["xradar", "X-radar", "NoShift+End"], ["antiwarp", "Antiwarp", "Shift+End"],
  ["menu", "Menu", "Escape"], ["statbox", "Stat box", "F2"],
  ["statbox_up", "Stat box up", "PageUp"], ["statbox_down", "Stat box down", "PageDown"],
  ["big_radar", "Big radar (hold)", "AltLeft, AltRight"], ["show_fps", "Frame rate", "F12"],
  ["zoom_in", "Zoom in", "Ctrl+Equal, Ctrl+NumpadAdd"], ["zoom_out", "Zoom out", "Ctrl+Minus, Ctrl+NumpadSubtract"],
  ["attach", "Attach", "NoShift+F7"], ["detach_turrets", "Detach turrets", "Ctrl+F7"],
  ["screenshot", "Screenshot", "PrintScreen"], ["spectate", "Spectate", "F11"],
];
const MODIFIER_KEYS = new Set(["ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight"]);

let keys = {};   // action -> [binding text]
let macros = []; // [binding text, chat line]

function parseSettings(text) {
  keys = Object.fromEntries(ACTIONS.map(([a, , d]) => [a, d.split(",").map((k) => k.trim()).filter(Boolean)]));
  macros = [];
  let section = "";
  for (const raw of (text || "").split("\n")) {
    const t = raw.trim();
    if (!t || t.startsWith(";")) continue;
    if (t.startsWith("[") && t.endsWith("]")) { section = t.slice(1, -1).trim().toLowerCase(); continue; }
    const eq = raw.indexOf("=");
    if (eq < 0) continue;
    const k = raw.slice(0, eq).trim();
    const v = raw.slice(eq + 1);
    if (section === "keys" && k in keys) keys[k] = v.split(",").map((x) => x.trim()).filter(Boolean);
    if (section === "macros" && k) macros.push([k, v.trim()]);
  }
}

function settingsText() {
  const lines = ["[keys]", ...ACTIONS.map(([a]) => `${a} = ${keys[a].join(", ")}`), "[macros]"];
  for (const [k, v] of macros) if (k && v.trim()) lines.push(`${k} = ${v.trim()}`);
  return lines.join("\n") + "\n";
}

// "Shift+KeyA" -> "Shift + A": a friendlier label for a binding
function keyLabel(b) {
  return b.replace(/^NoShift\+/, "").replace(/\+/g, " + ").replace(/\bKey([A-Z])\b/, "$1").replace(/\bDigit(\d)\b/, "$1")
    .replace(/Control(Left|Right)/, "Ctrl $1").replace(/Shift(Left|Right)/, "Shift $1").replace(/Alt(Left|Right)/, "Alt $1")
    .replace(/Arrow(Up|Down|Left|Right)/, "$1 arrow").replace("Backquote", "`");
}

// Waits for the next key press and gives its binding text, "" to remove, or null to cancel.
function captureKey(button) {
  const before = button.textContent;
  button.textContent = "press a key…";
  button.classList.add("capturing");
  return new Promise((resolve) => {
    const done = (value) => {
      window.removeEventListener("keydown", onKey, true);
      button.classList.remove("capturing");
      button.textContent = before;
      resolve(value);
    };
    const onKey = (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (MODIFIER_KEYS.has(e.code) && !["ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight"].includes(e.code)) return;
      if (e.code === "Escape" && !e.shiftKey && !e.ctrlKey) return done(null);
      if (e.code === "Backspace" && !e.shiftKey && !e.ctrlKey) return done("");
      const own = MODIFIER_KEYS.has(e.code); // a modifier key alone is never its own modifier
      const prefix = own ? "" : e.ctrlKey ? "Ctrl+" : e.shiftKey ? "Shift+" : "";
      done(prefix + e.code);
    };
    window.addEventListener("keydown", onKey, true);
  });
}

function renderKeys() {
  $("keysbody").replaceChildren(...ACTIONS.map(([a, label]) => {
    const cells = el("td", { className: "bindings" });
    keys[a].forEach((b, i) => {
      const chip = el("button", { type: "button", className: "chip", textContent: keyLabel(b), title: b });
      chip.addEventListener("click", async () => {
        const got = await captureKey(chip);
        if (got === null) return;
        if (got === "") keys[a].splice(i, 1); else keys[a][i] = got;
        renderKeys();
      });
      cells.append(chip);
    });
    const add = el("button", { type: "button", className: "chip add", textContent: "+", title: "Add a key" });
    add.addEventListener("click", async () => {
      const got = await captureKey(add);
      if (got) { keys[a].push(got); renderKeys(); }
    });
    cells.append(add);
    return el("tr", null, el("td", { textContent: label }), cells);
  }));
}

function renderMacros() {
  $("macrolist").replaceChildren(...macros.map((m, i) => {
    const key = el("button", { type: "button", className: "chip", textContent: m[0] ? keyLabel(m[0]) : "set key", title: m[0] });
    key.addEventListener("click", async () => {
      const got = await captureKey(key);
      if (got) { macros[i][0] = got; renderMacros(); }
    });
    const line = el("input", { value: m[1], maxLength: 200, placeholder: "chat line", ariaLabel: "Chat line" });
    line.addEventListener("input", () => { macros[i][1] = line.value; });
    const del = el("button", { type: "button", className: "small", textContent: "Remove" });
    del.addEventListener("click", () => { macros.splice(i, 1); renderMacros(); });
    return el("li", { className: "macro" }, key, line, del);
  }));
}

async function loadKeys(name) {
  msg($("keysmsg"), "", true);
  try {
    const r = await fetch(API + "/pilots/settings?name=" + encodeURIComponent(name), { credentials: "same-origin", cache: "no-store" });
    const data = await r.json();
    if (!data.ok) throw new Error(data.message);
    parseSettings(data.text);
  } catch (e) {
    parseSettings("");
    msg($("keysmsg"), "Couldn't load the saved keys; showing the defaults.", false);
  }
  renderKeys();
  renderMacros();
}

async function keysPanel(names, current) {
  $("keysbox").hidden = false;
  const pick = $("keysname");
  pick.replaceChildren(...names.map((n) => el("option", { value: n, textContent: n })));
  pick.value = current;
  pick.addEventListener("change", () => loadKeys(pick.value));
  $("macroadd").addEventListener("click", () => { macros.push(["", ""]); renderMacros(); });
  $("keyssave").addEventListener("click", async () => {
    const { body } = await post("/pilots/settings", { name: pick.value, text: settingsText() });
    msg($("keysmsg"), body.message, body.ok);
  });
  $("keysdefault").addEventListener("click", async () => {
    const { body } = await post("/pilots/settings", { name: pick.value, text: "" });
    msg($("keysmsg"), body.message, body.ok);
    if (body.ok) await loadKeys(pick.value);
  });
  await loadKeys(current);
}
