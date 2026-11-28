// The top bar's buttons on every page but the home page, so they always match it: Home, Foundry, Arena Editor,
// Admin (zone staff only), and the pilot button with its menu (fly as another of your names, Account, Sign out).
// Not signed in, the pilot button is Sign in, and signing in comes back to this page.
// One file for the site (/header.js) and the plugin's pages (Dare.Arenas embeds it: /arena/header.js, /account/header.js).
// The page's own <nav class="toplinks"> is replaced; header.css styles all of it. Everything is inserted with textContent.
(() => {
  const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
  const bar = document.querySelector("body > header.bar");
  if (!bar) return;
  const path = location.pathname;

  function el(tag, props = {}, text) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (k === "class") n.className = v;
      else n.setAttribute(k, v);
    }
    if (text != null) n.textContent = text;
    return n;
  }

  // the links, as the home page has them, plus Home
  const here = path === "/foundry.html" ? "foundry"
    : path.startsWith("/arena/staff") ? "admin"
    : path.startsWith("/arena/") ? "editor" : null;
  const link = (key, href, label, title) => {
    const a = el("a", { href, title }, label);
    if (key === here) a.setAttribute("aria-current", "page");
    return a;
  };
  const nav = el("nav", { class: "toplinks", "aria-label": "Site" });
  const admin = link("admin", "/arena/staff", "Admin", "Zone staff: arenas, server commands, how-tos");
  admin.hidden = here !== "admin";
  nav.append(
    link("home", "/", "Home", "The home page: pick an arena, chat, and fly"),
    link("foundry", "/foundry.html", "Foundry", "The Foundry: ships and graphics pilots designed, for everyone"),
    link("editor", "/arena/me#overview", "Arena Editor", "Make an arena; draw its map, set its rules, graphics and crew"),
    admin);

  // the pilot button and its menu
  const box = el("div", { class: "pilotbox" });
  const button = el("button", { type: "button", class: "pilot", "aria-haspopup": "menu", "aria-expanded": "false" });
  const label = el("span", { class: "pilot-label" }, "Pilot");
  const name = el("span", { class: "pilot-name" }, "Sign in");
  const caret = el("span", { class: "caret", "aria-hidden": "true" }, "▾");
  label.hidden = caret.hidden = true;
  button.append(label, name, caret);
  const menu = el("div", { class: "pilotmenu", role: "menu" });
  menu.hidden = true;
  const list = el("div", { class: "pilotlist" });
  const account = el("a", { role: "menuitem", class: "acctbtn", href: "/account/" }, "Account");
  const signout = el("button", { type: "button", role: "menuitem", class: "signout" }, "Sign out");
  menu.append(el("p", { class: "menuhead" }, "Fly as"), list, account, signout);
  box.append(button, menu);

  const right = el("div", { class: "topright" });
  right.append(nav, box);
  const old = bar.querySelector("nav.toplinks");
  if (old) old.replaceWith(right); else bar.append(right);

  let me = null;
  const close = () => { menu.hidden = true; button.setAttribute("aria-expanded", "false"); };
  async function open() {
    menu.hidden = false;
    button.setAttribute("aria-expanded", "true");
    list.replaceChildren(el("p", { class: "menunote" }, "…"));
    let names = [me];
    try {
      const r = await fetch("/account/api/pilots", { cache: "no-store", credentials: "same-origin" });
      if (r.status === 401) { location.reload(); return; }
      if (r.ok) names = ((await r.json()).pilots ?? []).map((p) => p.name);
    } catch {}
    list.replaceChildren(...names.map((n) => {
      const b = el("button", { type: "button", role: "menuitemradio", "aria-checked": String(n === me) }, n);
      if (n === me) b.className = "current";
      else b.addEventListener("click", () => void switchTo(n));
      return b;
    }));
  }
  async function switchTo(n) {
    try {
      const r = await fetch("/account/api/pilots/switch", { method: "POST", credentials: "same-origin", headers: HEADERS, body: JSON.stringify({ name: n }) });
      const body = await r.json().catch(() => ({}));
      if (r.ok && body.ok) { location.reload(); return; }
      list.prepend(el("p", { class: "menunote warn" }, body.message || "Couldn't switch names."));
    } catch {}
  }
  button.addEventListener("click", () => {
    if (!me) {
      location.href = "/account/login?next=" + encodeURIComponent(path + location.search + location.hash);
      return;
    }
    if (menu.hidden) void open(); else close();
  });
  signout.addEventListener("click", async () => {
    try { await fetch("/account/api/logout", { method: "POST", credentials: "same-origin", headers: HEADERS, body: "{}" }); } catch {}
    location.reload();
  });
  document.addEventListener("click", (e) => { if (!menu.hidden && !box.contains(e.target)) close(); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !menu.hidden) { close(); button.focus(); } });

  fetch("/account/api/me", { cache: "no-store", credentials: "same-origin" })
    .then((r) => (r.ok ? r.json() : null))
    .then((b) => {
      me = b?.name ?? null;
      name.textContent = me ?? "Sign in";
      label.hidden = caret.hidden = !me;
      if (me && path.startsWith("/account")) account.setAttribute("aria-current", "page");
    })
    .catch(() => {});
  // staff see Admin (the server says who is staff; the Admin page checks again)
  fetch("/arena/api/staff/can", { credentials: "same-origin", cache: "no-store" })
    .then((r) => (r.ok ? r.json() : null))
    .then((b) => { if (b?.staff) admin.hidden = false; })
    .catch(() => {});
})();
