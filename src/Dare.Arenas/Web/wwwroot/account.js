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
function wire(form, out, path, done) {
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const btn = form.querySelector("button[type=submit]");
    const v = values(form);
    btn.disabled = true;
    msg(out, "…", true);
    const { body } = await post(path, v);
    btn.disabled = false;
    msg(out, body.message, body.ok);
    if (body.ok && done) done(body, v);
  });
}

const pages = {
  signup() {
    wire($("form"), $("msg"), "/signup", (b, v) => {
      store.set("acct_email", v.email); store.set("acct_sent", b.message);
      location.href = "/account/verify";
    });
  },
  verify() {
    $("form").email.value = store.get("acct_email");
    if (store.get("acct_sent")) $("sent").textContent = store.get("acct_sent") + " It is valid for 10 minutes.";
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
    if (store.get("acct_sent")) $("sent").textContent = store.get("acct_sent") + " Enter it below.";
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
    wire($("emailform"), $("emailmsg"), "/email", () => { $("codeform").hidden = false; $("codeform").code.focus(); });
    wire($("codeform"), $("emailmsg"), "/email/verify", () => { setTimeout(() => location.reload(), 900); });
    wire($("pwform"), $("pwmsg"), "/password", () => { $("pwform").reset(); });
    $("signout").addEventListener("click", async () => {
      await post("/logout", {});
      location.href = "/account/login";
    });
  },
};

document.addEventListener("DOMContentLoaded", () => {
  const f = pages[document.body.dataset.page];
  if (f) f();
});
