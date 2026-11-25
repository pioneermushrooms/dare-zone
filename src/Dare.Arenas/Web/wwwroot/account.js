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
function wire(form, out, path, done, hint) {
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const btn = form.querySelector("button[type=submit]");
    const v = values(form);
    btn.disabled = true;
    msg(out, "…", true);
    const { body } = await post(path, v);
    btn.disabled = false;
    msg(out, body.ok && hint ? body.message + " " + hint : body.message, body.ok);
    if (body.ok && done) done(body, v);
  });
}

const NOTHING = "If nothing arrives in a few minutes, try again.";

const pages = {
  signup() {
    wire($("form"), $("msg"), "/signup", (b, v) => {
      store.set("acct_email", v.email); store.set("acct_sent", b.message);
      location.href = "/account/verify";
    });
  },
  verify() {
    $("form").email.value = store.get("acct_email");
    if (store.get("acct_sent")) $("sent").textContent = store.get("acct_sent") + " It is valid for 10 minutes. " + NOTHING;
    wire($("form"), $("msg"), "/verify", () => { location.href = "/account/"; });
  },
  login() {
    wire($("form"), $("msg"), "/login", () => { location.href = "/account/"; });
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
    if (me.needsEmail) {
      $("emailtitle").textContent = "Add a recovery email";
      $("emailnote").textContent = "Your name came from the old zone. Add a recovery email before anything else; it is used only to recover your password.";
    } else {
      $("emailtitle").textContent = "Change recovery email";
      $("emailnote").textContent = "We email a code to the new address.";
      $("pwbox").hidden = false;
    }
    wire($("emailform"), $("emailmsg"), "/email", () => { $("codeform").hidden = false; $("codeform").code.focus(); }, NOTHING);
    wire($("codeform"), $("emailmsg"), "/email/verify", () => { setTimeout(() => location.reload(), 900); });
    wire($("pwform"), $("pwmsg"), "/password", () => { $("pwform").reset(); });
    $("signout").addEventListener("click", async () => {
      await post("/logout", {});
      location.href = "/account/login";
    });
    await pilots(me);
    if (location.hash === "#pilots") $("pilots").scrollIntoView();
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
  if (!data.ok) { msg($("pilotsmsg"), "Couldn't load your names.", false); return; }
  if (!data.emailVerified) {
    $("pilotsnote").textContent = "Confirm a recovery email (below) to have more than one name: your names share it.";
    $("addform").hidden = true;
  }
  const names = data.pilots.map((p) => p.name);
  $("pilotlist").replaceChildren(...data.pilots.map((p) => {
    const current = p.name === data.current;
    const owned = p.arenas.length + p.drafts.length;
    const actions = el("span", { className: "row" });
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
  wire($("addform"), $("pilotsmsg"), "/pilots/add", () => { $("addform").reset(); void pilots(me); });
}

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
