// The top bar on every page, so it always matches the home page: Home, Foundry, Arena Editor, Admin (zone staff),
// the inbox (signed in: unread count, and a pop-up when something new arrives), and the pilot button with its menu
// (fly as another of your names, Account, Sign out). Not signed in, the pilot button is Sign in, and signing in
// comes back to this page.
// It runs right after the page's <header class="bar"> (not deferred) and draws from what it knew last time
// (localStorage), so the buttons are in place before the page shows and don't shift on every page change; then it
// checks with the server. The home page keeps its own markup and pilot menu (start.mjs); here it gets the inbox and Admin.
// Also: globalThis.foundationMentions(textarea) suggests pilot names after "@" (the wall, messages).
// One file for the site (/header.js) and the plugin's pages (Dare.Arenas embeds it: /arena/header.js, /account/header.js).
// Everything is inserted with textContent.
(() => {
  const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
  const KEY = "foundation.header"; // { name, staff, seen } from last time
  const path = location.pathname;
  const home = path === "/" || path === "/index.html";
  const bar = document.querySelector("header.bar");
  if (!bar) return;

  const store = {
    get() { try { return JSON.parse(localStorage.getItem(KEY)) || {}; } catch { return {}; } },
    set(v) { try { localStorage.setItem(KEY, JSON.stringify({ ...store.get(), ...v })); } catch {} },
  };
  const last = store.get();

  function el(tag, props, text) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(props ?? {})) {
      if (k === "class") n.className = v;
      else n.setAttribute(k, v);
    }
    if (text != null) n.textContent = text;
    return n;
  }

  // ---- the buttons ----------------------------------------------------------------------------------
  let right, admin, box = null, button = null, label = null, name = null, caret = null, menu = null, list = null, account = null;
  const inbox = el("a", { class: "inboxbtn", href: "/account/inbox", title: "Inbox: messages, replies and mentions", "aria-label": "Inbox" });
  inbox.append(el("span", { class: "inboxicon", "aria-hidden": "true" }));
  const badge = el("span", { class: "inboxbadge", "aria-hidden": "true" });
  badge.hidden = true;
  inbox.append(badge);
  if (path.startsWith("/account/inbox")) inbox.setAttribute("aria-current", "page");

  if (home) {
    // the home page's own bar: add the inbox before its pilot button; Admin is its #adminlink
    right = bar.querySelector(".topright");
    admin = document.getElementById("adminlink");
    right?.insertBefore(inbox, right.querySelector(".pilotbox"));
  } else {
    const here = path === "/foundry.html" ? "foundry"
      : path.startsWith("/arena/staff") ? "admin"
      : path.startsWith("/arena/") ? "editor" : null;
    const link = (key, href, text, title) => {
      const a = el("a", { href, title }, text);
      if (key === here) a.setAttribute("aria-current", "page");
      return a;
    };
    const nav = el("nav", { class: "toplinks", "aria-label": "Site" });
    admin = link("admin", "/arena/staff", "Admin", "Zone staff: arenas, server commands, how-tos");
    nav.append(
      link("home", "/", "Home", "The home page: pick an arena, chat, and fly"),
      link("foundry", "/foundry.html", "Foundry", "The Foundry: ships and graphics pilots designed, for everyone"),
      link("editor", "/arena/me#overview", "Arena Editor", "Make an arena; draw its map, set its rules, graphics and crew"),
      admin);

    box = el("div", { class: "pilotbox" });
    button = el("button", { type: "button", class: "pilot", "aria-haspopup": "menu", "aria-expanded": "false" });
    label = el("span", { class: "pilot-label" }, "Pilot");
    name = el("span", { class: "pilot-name" }, "Sign in");
    caret = el("span", { class: "caret", "aria-hidden": "true" }, "▾");
    button.append(label, name, caret);
    menu = el("div", { class: "pilotmenu", role: "menu" });
    menu.hidden = true;
    list = el("div", { class: "pilotlist" });
    account = el("a", { role: "menuitem", class: "acctbtn", href: "/account/" }, "Account");
    const signout = el("button", { type: "button", role: "menuitem", class: "signout" }, "Sign out");
    menu.append(el("p", { class: "menuhead" }, "Fly as"), list, account, signout);
    box.append(button, menu);

    right = el("div", { class: "topright" });
    right.append(nav, inbox, box);
    const old = bar.querySelector(".topright, nav.toplinks");
    if (old) old.replaceWith(right); else bar.append(right);

    button.addEventListener("click", () => {
      if (!me) { location.href = "/account/login?next=" + encodeURIComponent(path + location.search + location.hash); return; }
      if (menu.hidden) void openMenu(); else closeMenu();
    });
    signout.addEventListener("click", async () => {
      try { await fetch("/account/api/logout", { method: "POST", credentials: "same-origin", headers: HEADERS, body: "{}" }); } catch {}
      store.set({ name: null, staff: false });
      location.reload();
    });
    document.addEventListener("click", (e) => { if (!menu.hidden && !box.contains(e.target)) closeMenu(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !menu.hidden) { closeMenu(); button.focus(); } });
  }

  let me = null;
  function showMe(n, staff) {
    me = n || null;
    if (name) {
      name.textContent = me ?? "Sign in";
      label.hidden = caret.hidden = !me;
      if (me && path.startsWith("/account") && !path.startsWith("/account/inbox")) account.setAttribute("aria-current", "page");
    }
    inbox.hidden = !me;
    if (admin) { admin.hidden = !staff && !admin.hasAttribute("aria-current"); admin.style.display = admin.hidden ? "none" : ""; }
  }
  showMe(last.name, last.staff); // as it was last time: no shift when the page shows

  function closeMenu() { menu.hidden = true; button.setAttribute("aria-expanded", "false"); }
  async function openMenu() {
    menu.hidden = false;
    button.setAttribute("aria-expanded", "true");
    list.replaceChildren(el("p", { class: "menunote" }, "…"));
    let names = [me];
    try {
      const r = await fetch("/account/api/pilots", { cache: "no-store", credentials: "same-origin" });
      if (r.status === 401) { store.set({ name: null }); location.reload(); return; }
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
      if (r.ok && body.ok) { store.set({ name: body.name ?? n }); location.reload(); return; }
      list.prepend(el("p", { class: "menunote warn" }, body.message || "Couldn't switch names."));
    } catch {}
  }

  // ---- the inbox: unread count, and pop-ups for what's new ------------------------------------------
  const toasts = el("div", { class: "toasts", role: "status", "aria-live": "polite" });
  const toastHost = () => { if (!toasts.isConnected) document.body.append(toasts); };
  const SAID = { wall: "wrote on your wall", reply: "replied to you", mention: "mentioned you" };
  function toast(title, text, href) {
    toastHost();
    const t = el("a", { class: "toast", href });
    t.append(el("b", null, title));
    if (text) t.append(el("span", null, text.length > 120 ? text.slice(0, 119) + "…" : text));
    const x = el("button", { type: "button", class: "toastx", "aria-label": "Dismiss" }, "✕");
    x.addEventListener("click", (e) => { e.preventDefault(); t.remove(); });
    t.append(x);
    toasts.append(t);
    setTimeout(() => t.classList.add("gone"), 9000);
    setTimeout(() => t.remove(), 9600);
  }

  let seen = last.seen ?? null;          // the newest notification id already popped up
  let seenMsgs = last.seenMsgs ?? null;   // unread messages last time
  async function checkInbox() {
    if (!me || document.hidden) return;
    try {
      const r = await fetch(`/account/api/inbox/count?after=${seen ?? 0}`, { cache: "no-store", credentials: "same-origin" });
      if (r.status === 401) { showMe(null, false); store.set({ name: null }); return; }
      if (!r.ok) return;
      const b = await r.json();
      const unread = (b.notes || 0) + (b.messages || 0);
      badge.hidden = unread === 0;
      badge.textContent = unread > 99 ? "99+" : String(unread);
      inbox.setAttribute("aria-label", unread ? `Inbox, ${unread} unread` : "Inbox");
      const onInbox = path.startsWith("/account/inbox");
      if (seen != null && !onInbox) {
        for (const n of (b.fresh || []).reverse())
          toast(`${n.actor} ${SAID[n.kind] ?? "wrote"}`, n.text, `/pilot/${encodeURIComponent(n.wall)}#post-${n.post}`);
        if (seenMsgs != null && b.messages > seenMsgs) toast("New message", b.messages === 1 ? "You have an unread message." : `You have ${b.messages} unread messages.`, "/account/inbox#messages");
      }
      seen = b.latest || 0;
      seenMsgs = b.messages || 0;
      store.set({ seen, seenMsgs });
      globalThis.dispatchEvent(new CustomEvent("foundation:inbox", { detail: b }));
    } catch {}
  }
  globalThis.foundationCheckInbox = checkInbox;

  // ---- who's signed in, and staff -------------------------------------------------------------------
  Promise.all([
    fetch("/account/api/me", { cache: "no-store", credentials: "same-origin" }).then((r) => (r.ok ? r.json() : null)).catch(() => undefined),
    fetch("/arena/api/staff/can", { credentials: "same-origin", cache: "no-store" }).then((r) => (r.ok ? r.json() : null)).catch(() => undefined),
  ]).then(([who, staff]) => {
    if (who === undefined) return; // offline: keep what we showed
    const n = who?.name ?? null, s = !!staff?.staff;
    if (n !== store.get().name) { seen = null; seenMsgs = null; store.set({ seen: null, seenMsgs: null }); }
    store.set({ name: n, staff: s });
    showMe(n, s);
    void checkInbox();
  });
  setInterval(checkInbox, 30000);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) void checkInbox(); });

  // ---- @name suggestions for a textarea or input ----------------------------------------------------
  // After "@" and a letter or two, a list of pilot names; Enter, Tab or a click puts "@Name " in.
  globalThis.foundationMentions = (field, { at = true } = {}) => {
    const pop = el("ul", { class: "mentionpop", role: "listbox" });
    pop.hidden = true;
    field.insertAdjacentElement("afterend", pop);
    let start = -1, items = [], pick = 0, ticket = 0;
    const close = () => { pop.hidden = true; items = []; };
    function render() {
      pop.replaceChildren(...items.map((n, i) => {
        const li = el("li", { role: "option", "aria-selected": String(i === pick) }, n);
        li.addEventListener("mousedown", (e) => { e.preventDefault(); choose(n); });
        return li;
      }));
      pop.hidden = items.length === 0;
      // just under the field, whatever its parent lays out
      pop.style.left = field.offsetLeft + "px";
      pop.style.top = field.offsetTop + field.offsetHeight + 2 + "px";
    }
    function choose(n) {
      const v = field.value, end = field.selectionStart;
      if (at) {
        field.value = v.slice(0, start) + "@" + n + " " + v.slice(end);
        const pos = start + n.length + 2;
        field.setSelectionRange(pos, pos);
      } else field.value = n;
      close();
      field.dispatchEvent(new Event("input", { bubbles: true }));
      field.focus();
    }
    field.addEventListener("input", async () => {
      const v = field.value, end = field.selectionStart;
      let q;
      if (at) {
        const i = v.lastIndexOf("@", end - 1);
        if (i < 0 || (i > 0 && /[\p{L}\p{N}]/u.test(v[i - 1]))) return close();
        q = v.slice(i + 1, end);
        if (q.length < 1 || q.length > 24 || /\n/.test(q)) return close();
        start = i;
      } else q = v.trim();
      if (!q) return close();
      const mine = ++ticket;
      try {
        const r = await fetch(`/account/api/names?q=${encodeURIComponent(q)}`, { credentials: "same-origin" });
        const b = r.ok ? await r.json() : null;
        if (mine !== ticket) return;
        items = (b?.names ?? []).slice(0, 6);
        pick = 0;
        render();
      } catch { close(); }
    });
    field.addEventListener("keydown", (e) => {
      if (pop.hidden) return;
      if (e.key === "ArrowDown") { e.preventDefault(); pick = (pick + 1) % items.length; render(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); pick = (pick - 1 + items.length) % items.length; render(); }
      else if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); e.stopPropagation(); choose(items[pick]); }
      else if (e.key === "Escape") close();
    });
    field.addEventListener("blur", () => setTimeout(close, 150));
  };

  // Text with its @mentions as links to the pilots' profiles (names the server matched).
  globalThis.foundationMentionText = (node, text, names) => {
    const parts = [];
    let rest = text;
    const found = (names ?? []).slice().sort((a, b) => b.length - a.length);
    while (rest.length) {
      let best = -1, who = null;
      for (const n of found) {
        const i = rest.toLowerCase().indexOf("@" + n.toLowerCase());
        if (i >= 0 && (best < 0 || i < best)) { best = i; who = n; }
      }
      if (best < 0) { parts.push(document.createTextNode(rest)); break; }
      if (best > 0) parts.push(document.createTextNode(rest.slice(0, best)));
      const a = el("a", { class: "mention", href: "/pilot/" + encodeURIComponent(who) }, rest.slice(best, best + who.length + 1));
      parts.push(a);
      rest = rest.slice(best + who.length + 1);
    }
    node.replaceChildren(...parts);
  };
})();
