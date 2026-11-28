// Site-styled dropdowns for the map editor: each <select> on the page gets a button and a list
// styled like the home page's arena list, instead of the browser's own pop-up. The <select> stays
// in the page (hidden) and is still what code reads and listens to: picking an item sets its value
// and fires input + change on it. Options, optgroups, disabled state and values set from code
// (sel.value = ..., sel.selectedIndex = ...) are followed; selects added later are upgraded too.
// Keys: Enter/Space/Down open; Up/Down/Home/End move; Enter picks; Escape closes; a letter jumps.
"use strict";

window.Dropdown = (() => {
  const done = new WeakSet();
  let openOne = null; // { sel, btn, menu, close }

  function labelOf(sel) {
    const o = sel.options[sel.selectedIndex];
    return o ? o.textContent : "";
  }

  // the words of the select's <label>, without the option texts inside a wrapping label
  function labelText(sel) {
    const l = sel.labels && sel.labels[0];
    return l ? [...l.childNodes].filter((n) => n.nodeType === 3).map((n) => n.textContent).join(" ").trim() : "";
  }

  function enhance(sel) {
    if (done.has(sel) || sel.multiple || sel.size > 1 || sel.dataset.native !== undefined) return;
    done.add(sel);
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "dd-btn";
    btn.setAttribute("aria-haspopup", "listbox");
    btn.setAttribute("aria-expanded", "false");
    const name = document.createElement("span"); name.className = "dd-name";
    const chev = document.createElement("span"); chev.className = "dd-chev"; chev.setAttribute("aria-hidden", "true");
    btn.append(name, chev);
    sel.classList.add("dd-native");
    sel.tabIndex = -1;
    sel.setAttribute("aria-hidden", "true");
    sel.after(btn);
    // a <label for=select> or a wrapping <label> should open this, not the hidden select
    if (sel.id) for (const l of document.querySelectorAll(`label[for="${CSS.escape(sel.id)}"]`)) l.htmlFor = "";

    const sync = () => {
      name.textContent = labelOf(sel) || " ";
      btn.disabled = sel.disabled;
      btn.hidden = sel.hidden;
      const lab = sel.getAttribute("aria-label") || labelText(sel);
      btn.setAttribute("aria-label", lab ? `${lab}: ${name.textContent}` : name.textContent);
      if (sel.title) btn.title = sel.title; else btn.removeAttribute("title");
      if (openOne && openOne.sel === sel) render(openOne);
    };
    // values set from code fire no event: wrap this select's value and selectedIndex setters
    for (const prop of ["value", "selectedIndex"]) {
      const d = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, prop);
      Object.defineProperty(sel, prop, {
        configurable: true,
        get() { return d.get.call(this); },
        set(v) { d.set.call(this, v); sync(); },
      });
    }
    new MutationObserver(sync).observe(sel, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["disabled", "hidden", "title", "aria-label", "selected", "label"] });
    sel.addEventListener("change", sync);
    btn.addEventListener("click", () => (openOne && openOne.sel === sel ? close() : open(sel, btn)));
    btn.addEventListener("keydown", (e) => {
      if (["ArrowDown", "ArrowUp", "Enter", " "].includes(e.key) && !(openOne && openOne.sel === sel)) { e.preventDefault(); e.stopPropagation(); open(sel, btn); return; }
      if (openOne && openOne.sel === sel) { e.stopPropagation(); menuKey(e); } // keys typed into the list are not page shortcuts
    });
    sync();
  }

  function render(st) {
    const { sel, list } = st;
    list.replaceChildren();
    st.items = [];
    for (const node of sel.children) {
      if (node.tagName === "OPTGROUP") {
        const head = document.createElement("li");
        head.className = "dd-group"; head.setAttribute("role", "presentation");
        head.textContent = node.label;
        list.append(head);
        for (const o of node.children) if (o.tagName === "OPTION") add(o, node.disabled);
      } else if (node.tagName === "OPTION") add(node, false);
    }
    function add(o, groupOff) {
      if (o.hidden) return;
      const li = document.createElement("li");
      li.setAttribute("role", "option");
      li.id = st.id + "-" + st.items.length;
      li.textContent = o.textContent || " ";
      const off = o.disabled || groupOff;
      if (off) { li.classList.add("off"); li.setAttribute("aria-disabled", "true"); }
      if (o.index === sel.selectedIndex) { li.classList.add("current"); li.setAttribute("aria-selected", "true"); }
      li.addEventListener("pointerdown", (e) => e.preventDefault()); // keep focus on the button
      li.addEventListener("click", () => { if (!off) pick(o.index); });
      li.addEventListener("pointermove", () => setActive(st.items.findIndex((it) => it.li === li)));
      list.append(li);
      st.items.push({ li, index: o.index, off });
    }
    setActive(Math.max(0, st.items.findIndex((it) => it.index === sel.selectedIndex)), true);
  }

  function setActive(n, scroll) {
    const st = openOne || pending;
    if (!st || !st.items.length) return;
    n = Math.max(0, Math.min(st.items.length - 1, n));
    st.active = n;
    st.items.forEach((it, i) => it.li.classList.toggle("active", i === n));
    st.btn.setAttribute("aria-activedescendant", st.items[n].li.id);
    if (scroll) st.items[n].li.scrollIntoView({ block: "nearest" });
  }

  let pending = null;
  let seq = 0;
  function open(sel, btn) {
    close();
    if (btn.disabled) return;
    const menu = document.createElement("div");
    menu.className = "dd-menu";
    const list = document.createElement("ul");
    list.className = "dd-list";
    list.setAttribute("role", "listbox");
    const id = "dd" + (++seq);
    list.id = id;
    btn.setAttribute("aria-controls", id);
    menu.append(list);
    document.body.append(menu);
    const st = { sel, btn, menu, list, id, items: [], active: 0, typed: "", typedAt: 0 };
    pending = st;
    render(st);
    pending = null;
    openOne = st;
    btn.setAttribute("aria-expanded", "true");
    place(st);
    setActive(st.active, true);
    btn.focus({ preventScroll: true });
  }

  // fixed to the viewport (the sidebar scrolls and would clip it), below the button or above it
  function place(st) {
    const r = st.btn.getBoundingClientRect();
    const m = st.menu;
    const width = Math.max(r.width, 180);
    m.style.minWidth = width + "px";
    m.style.maxWidth = Math.max(width, Math.min(360, innerWidth - 16)) + "px";
    const below = innerHeight - r.bottom - 8, above = r.top - 8;
    const up = below < 200 && above > below;
    m.style.maxHeight = Math.max(120, Math.min(420, up ? above : below) - 6) + "px";
    const mw = m.offsetWidth;
    m.style.left = Math.max(8, Math.min(r.left, innerWidth - mw - 8)) + "px";
    if (up) { m.style.top = ""; m.style.bottom = (innerHeight - r.top + 6) + "px"; }
    else { m.style.bottom = ""; m.style.top = (r.bottom + 6) + "px"; }
    m.classList.toggle("up", up);
  }

  function pick(index) {
    const st = openOne;
    if (!st) return;
    const { sel, btn } = st;
    close();
    btn.focus({ preventScroll: true });
    if (sel.selectedIndex === index) return;
    sel.selectedIndex = index;
    sel.dispatchEvent(new Event("input", { bubbles: true }));
    sel.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function close() {
    if (!openOne) return;
    const st = openOne;
    openOne = null;
    st.menu.remove();
    st.btn.setAttribute("aria-expanded", "false");
    st.btn.removeAttribute("aria-activedescendant");
  }

  function menuKey(e) {
    const st = openOne;
    const step = (from, d) => {
      let i = from;
      for (let n = 0; n < st.items.length; n++) { i += d; if (i < 0 || i >= st.items.length) return from; if (!st.items[i].off) return i; }
      return from;
    };
    switch (e.key) {
      case "ArrowDown": e.preventDefault(); setActive(step(st.active, 1), true); return;
      case "ArrowUp": e.preventDefault(); setActive(step(st.active, -1), true); return;
      case "Home": e.preventDefault(); setActive(step(-1, 1), true); return;
      case "End": e.preventDefault(); setActive(step(st.items.length, -1), true); return;
      case "Enter": case " ": e.preventDefault(); { const it = st.items[st.active]; if (it && !it.off) pick(it.index); } return;
      case "Escape": e.preventDefault(); e.stopPropagation(); close(); return;
      case "Tab": close(); return;
    }
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const now = Date.now();
      st.typed = (now - st.typedAt < 700 ? st.typed : "") + e.key.toLowerCase();
      st.typedAt = now;
      const n = st.items.findIndex((it) => !it.off && it.li.textContent.toLowerCase().startsWith(st.typed));
      if (n >= 0) setActive(n, true);
    }
  }

  document.addEventListener("pointerdown", (e) => {
    if (openOne && !openOne.menu.contains(e.target) && !openOne.btn.contains(e.target)) close();
  }, true);
  window.addEventListener("resize", close);
  document.addEventListener("scroll", (e) => { if (openOne && !openOne.menu.contains(e.target)) close(); }, true);

  function enhanceAll(root) {
    if (root.tagName === "SELECT") { enhance(root); return; }
    if (root.querySelectorAll) for (const s of root.querySelectorAll("select")) enhance(s);
  }
  function start() {
    enhanceAll(document);
    new MutationObserver((recs) => { for (const r of recs) for (const n of r.addedNodes) if (n.nodeType === 1) enhanceAll(n); })
      .observe(document.body, { childList: true, subtree: true });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();

  return { enhance, enhanceAll, close };
})();
