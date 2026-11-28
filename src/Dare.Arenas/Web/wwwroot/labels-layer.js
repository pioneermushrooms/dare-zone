// Game labels in the map editor (/arena/map): team spawns, the flag spawn area, ball spawns, bases, flag
// spots and areas with rules. Labels are kept in the map's own eLVL regions (elvl.js); spawn labels also
// become arena settings on Publish. Pure part (fromRegions, toRegions, spawnSettings) runs in node tests.
// Hooks: GameLabels.mount(host, { get: () => ({ prefix, arena, grid }), set: (prefix, note) }) from editor.js,
// window.mapView and window.mapOverlays like lvz-layer.js. Text goes in with textContent only.
// Spec: docs/superpowers/specs/2026-11-28-game-mode-phase3-labeller-design.md.
"use strict";

(function (root, factory) {
  const elvl = typeof module === "object" && module.exports ? require("./elvl.js") : root.Elvl;
  const api = factory(elvl);
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GameLabels = api;
})(typeof self !== "undefined" ? self : this, (Elvl) => {
  const N = 1024;
  const TEAMS = ["Red", "Blue", "Green", "Yellow"];
  const TEAM_COLORS = ["#f87171", "#60a5fa", "#4ade80", "#facc15"];
  const KINDS = [
    { id: "spawn", name: "Team spawn", hint: "Where a team's pilots appear. Works now.", shape: "point" },
    { id: "flagspawn", name: "Flag spawn area", hint: "Where carried flags appear. Works now.", shape: "point" },
    { id: "ballspawn", name: "Ball spawn", hint: "Where a ball starts. Works now.", shape: "point" },
    { id: "area", name: "Area with rules", hint: "No weapons, no antiwarp or no flag drops inside. Works now.", shape: "rect" },
    { id: "base", name: "Base", hint: "A team's base, for capture-the-flag and hold-the-base (rules, Phase 4).", shape: "rect" },
    { id: "flagspot", name: "Flag spot", hint: "Where one flag lives in capture-the-flag (rules, Phase 4).", shape: "tile" },
  ];
  const kindOf = (id) => KINDS.find((k) => k.id === id);
  const RULES = [["noWeapons", "No weapons"], ["noAntiwarp", "No antiwarp"], ["noFlagDrops", "No flag drops"]];

  function bbox(tiles) {
    let x0 = N, y0 = N, x1 = -1, y1 = -1;
    for (const v of tiles) { const x = v % N, y = Math.floor(v / N); if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
    return x1 < 0 ? { x: 0, y: 0, w: 0, h: 0 } : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
  }

  /** Labels from a map's regions: ours (with an rDGM meta) by kind; any other region as "foreign". */
  function fromRegions(regions) {
    return regions.map((r) => {
      const m = r.meta && r.meta.v === 1 && kindOf(r.meta.kind) ? r.meta : null;
      const flags = { noWeapons: !!r.noWeapons, noAntiwarp: !!r.noAntiwarp, noFlagDrops: !!r.noFlagDrops };
      if (!m) return { kind: "foreign", name: r.name, region: r, ...flags, ...bbox(r.tiles) };
      const shape = kindOf(m.kind).shape;
      const l = { kind: m.kind, name: r.name };
      if (m.team != null) l.team = m.team;
      if (m.index != null) l.index = m.index;
      if (shape === "point") Object.assign(l, { x: m.x, y: m.y, r: m.r });
      else if (shape === "tile") Object.assign(l, { x: m.x, y: m.y });
      else Object.assign(l, bbox(r.tiles), flags);
      return l;
    });
  }

  /** Regions for labels (names made unique for the server). */
  function toRegions(labels) {
    const regs = labels.map((l) => {
      if (l.kind === "foreign") return { ...l.region, name: l.name, noWeapons: !!l.noWeapons, noAntiwarp: !!l.noAntiwarp, noFlagDrops: !!l.noFlagDrops };
      const meta = { v: 1, kind: l.kind };
      if (l.team != null) meta.team = l.team;
      if (l.index != null) meta.index = l.index;
      const shape = kindOf(l.kind).shape;
      if (shape === "point" || shape === "tile") {
        meta.x = l.x; meta.y = l.y;
        if (shape === "point") meta.r = l.r;
        return { name: l.name, tiles: new Set([l.y * N + l.x]), meta };
      }
      return { name: l.name, tiles: Elvl.rect(l.x, l.y, l.w, l.h), meta, base: l.kind === "base",
        noWeapons: !!l.noWeapons, noAntiwarp: !!l.noAntiwarp, noFlagDrops: !!l.noFlagDrops };
    });
    return Elvl.uniqueNames(regs);
  }

  /** The arena settings the spawn labels stand for (tiles). Balls are numbered in order: the server stops at the first gap. */
  function spawnSettings(labels) {
    const out = {}, put = (k, v) => { out[k] = String(Math.round(+v || 0)); };
    for (const l of labels.filter((x) => x.kind === "spawn" && x.team >= 0 && x.team <= 3))
      { put(`Spawn:Team${l.team}-X`, l.x); put(`Spawn:Team${l.team}-Y`, l.y); put(`Spawn:Team${l.team}-Radius`, l.r); }
    const flag = labels.find((x) => x.kind === "flagspawn");
    if (flag) { put("Flag:SpawnX", flag.x); put("Flag:SpawnY", flag.y); put("Flag:SpawnRadius", flag.r); }
    labels.filter((x) => x.kind === "ballspawn").sort((a, b) => (a.index ?? 0) - (b.index ?? 0)).slice(0, 8).forEach((b, i) => {
      const k = i === 0 ? "" : String(i);
      put(`Soccer:SpawnX${k}`, b.x); put(`Soccer:SpawnY${k}`, b.y); put(`Soccer:SpawnRadius${k}`, b.r);
    });
    return out;
  }

  /**
   * Defaults for the spawn settings no label covers any more (a deleted label must not keep its spot):
   * a team spawn 0 (unset), the flag spawn the server's 512/512/50, and ball spawns past the labelled ones
   * the first ball's spot (the server can't unset them: it reads ball spawns until the first missing one).
   */
  function resetSettings(labels) {
    const have = spawnSettings(labels), out = {};
    const put = (k, v) => { if (!(k in have)) out[k] = String(v); };
    for (let t = 0; t < 4; t++) for (const k of ["X", "Y", "Radius"]) put(`Spawn:Team${t}-${k}`, 0);
    put("Flag:SpawnX", 512); put("Flag:SpawnY", 512); put("Flag:SpawnRadius", 50);
    const bx = have["Soccer:SpawnX"] ?? 512, by = have["Soccer:SpawnY"] ?? 512, br = have["Soccer:SpawnRadius"] ?? 20;
    put("Soccer:SpawnX", bx); put("Soccer:SpawnY", by); put("Soccer:SpawnRadius", br);
    for (let k = 1; k < 8; k++) { put(`Soccer:SpawnX${k}`, bx); put(`Soccer:SpawnY${k}`, by); put(`Soccer:SpawnRadius${k}`, br); }
    return out;
  }

  const colorOf = (l) => l.kind === "spawn" || l.kind === "base" ? TEAM_COLORS[l.team ?? 0] : l.kind === "flagspawn" ? "#facc15"
    : l.kind === "ballspawn" ? "#c4b5fd" : l.kind === "area" ? "#4ade80" : l.kind === "flagspot" ? "#ffcf7a" : "#94a3b8";

  // ---- the panel (browser only) -------------------------------------------------------------------
  let api = null; // set by mount; publishSettings needs it
  /** The LVZ panel's "Place and move LVZ" checkbox (lvz-layer.js: class lzp-edit, no id). */
  const lvzToggle = (doc) => doc.querySelector(".lzp-edit") || doc.getElementById("lzp-edit");

  function mount(host, hooks) {
    const mv = typeof window !== "undefined" && window.mapView;
    if (!host || !mv || !window.mapOverlays) return null;
    const el = (tag, props, ...kids) => {
      const e = document.createElement(tag);
      for (const [k, v] of Object.entries(props || {})) { if (k === "text") e.textContent = v; else if (k === "class") e.className = v; else e.setAttribute(k, v); }
      for (const c of kids) if (c != null) e.append(c);
      return e;
    };
    let labels = [], sel = -1, armed = null, drag = null, lastPrefix, classic = null;
    // A map with no tileset of its own keeps its regions on Continuum's classic tiles (it looks the same).
    fetch("/arena/tiles.bm2", { credentials: "same-origin" }).then((r) => (r.ok ? r.arrayBuffer() : null))
      .then((b) => { if (b) classic = new Uint8Array(b); }).catch(() => {});
    const load = () => { const p = hooks.get().prefix; lastPrefix = p; labels = fromRegions(Elvl.regions(p)); if (sel >= labels.length) sel = -1; };
    load();
    function save(note) {
      let next;
      try { next = Elvl.withRegions(hooks.get().prefix, toRegions(labels), { bmp: classic }); }
      catch { load(); render(); mv.redraw(); hint.textContent = "This map has no tileset yet, so labels can't be kept in it. Try again in a moment, or pick tiles first."; return; }
      lastPrefix = next;
      hooks.set(next, note || "Labels changed. Publish to use them.");
      render();
    }

    const show = el("input", { type: "checkbox", id: "lbl-show" }); show.checked = true;
    const edit = el("input", { type: "checkbox", id: "lbl-edit" });
    const list = el("ul", { class: "lblist" });
    const add = el("select", { "aria-label": "Add a label" });
    add.append(el("option", { value: "", text: "+ Add a label…" }), ...KINDS.map((k) => el("option", { value: k.id, text: k.name })));
    const hint = el("p", { class: "hint lbhint" });
    const form = el("div", { class: "lbform" });
    const counts = el("p", { class: "hint lbcounts" });
    host.replaceChildren(el("p", { class: "kicker", text: "Game labels" }),
      el("div", { class: "row lbtoggles" }, el("label", { class: "hint" }, show, " Show"), el("label", { class: "hint" }, edit, " Edit")),
      list, add, hint, form, counts);

    // Label editing and LVZ placing both take the map's clicks, and LVZ's handler comes first: only one is on.
    const lvzEdit = () => lvzToggle(document);
    edit.addEventListener("change", () => {
      // The map editor's shared handover: one panel owns map clicks; "map:pointer" { owner } says who took them.
      if (edit.checked) window.dispatchEvent(new CustomEvent("map:pointer", { detail: { owner: "labels" } }));
      if (edit.checked && lvzEdit()?.checked) { lvzEdit().checked = false; lvzEdit().dispatchEvent(new Event("change")); }
      if (!edit.checked) { armed = null; add.value = ""; }
      render(); mv.redraw();
    });
    const lvzTookOver = () => // another panel has the map: drop Edit and the armed kind (and the "Add…" choice showing it)
      { if (edit.checked) { edit.checked = false; armed = null; add.value = ""; render(); mv.redraw(); } };
    document.addEventListener("change", (e) => { if (e.target === lvzEdit() && e.target.checked) lvzTookOver(); });
    window.addEventListener("map:pointer", (e) => { if (e.detail && e.detail.owner !== "labels") lvzTookOver(); });
    // LVZ also starts placing without a change event (picking an image): it marks the page with body.lvzplacing.
    new MutationObserver(() => { if (document.body.classList.contains("lvzplacing")) lvzTookOver(); })
      .observe(document.body, { attributes: true, attributeFilter: ["class"] });
    show.addEventListener("change", () => mv.redraw());
    add.addEventListener("change", () => {
      armed = add.value || null;
      if (armed) { edit.checked = true; edit.dispatchEvent(new Event("change")); show.checked = true; }
      render();
    });

    const nextTeam = () => [0, 1, 2, 3].find((t) => !labels.some((l) => l.kind === "spawn" && l.team === t)) ?? 0;
    function create(kind, x, y) {
      const n = (k) => labels.filter((l) => l.kind === k).length;
      if (kind === "spawn") { const t = nextTeam(); return { kind, team: t, name: `${TEAMS[t]} spawn`, x, y, r: 3 }; }
      if (kind === "flagspawn") return { kind, name: "Flag spawn area", x, y, r: 10 };
      if (kind === "ballspawn") return { kind, index: n(kind), name: `Ball ${n(kind) + 1}`, x, y, r: 2 };
      if (kind === "base") { const t = Math.min(3, n(kind)); return { kind, team: t, name: `${TEAMS[t]} base`, x, y, w: 1, h: 1, noFlagDrops: false, noWeapons: false, noAntiwarp: false }; }
      if (kind === "flagspot") return { kind, index: n(kind), name: `Flag ${n(kind) + 1}`, x, y };
      return { kind: "area", name: `Area ${n("area") + 1}`, x, y, w: 1, h: 1, noWeapons: true, noAntiwarp: false, noFlagDrops: false };
    }

    function render() {
      list.replaceChildren(...labels.map((l, i) => {
        const dot = el("span", { class: "lbdot" });
        dot.style.background = colorOf(l); // a style property, not a style attribute: the site's CSP blocks inline style attributes
        const li = el("li", { class: i === sel ? "on" : "" }, el("span", null, dot, l.name),
          el("span", { class: "lbkind", text: l.kind === "foreign" ? "Region" : kindOf(l.kind).name }));
        li.addEventListener("click", () => { sel = i; render(); mv.redraw(); });
        return li;
      }));
      if (!labels.length) list.append(el("li", { class: "dim", text: "No labels yet." }));
      const k = armed && kindOf(armed);
      hint.textContent = k ? `${k.hint} ${k.shape === "rect" ? "Drag a rectangle on the map." : "Click the map to place it" + (k.shape === "point" ? ", then drag out its radius." : ".")} Esc to stop.`
        : edit.checked ? "Drag a label to move it. The Delete key removes the selected one (no undo yet)." : "";
      renderForm();
      const g = hooks.get().grid;
      let flags = 0, goals = 0;
      if (g) for (const t of g) { if (t === 170) flags++; else if (t === 172) goals++; }
      const foreign = labels.filter((l) => l.kind === "foreign").length;
      counts.textContent = `On this map: ${flags} turf flag tile${flags === 1 ? "" : "s"} · ${goals} goal tile${goals === 1 ? "" : "s"} · ${labels.length - foreign} label${labels.length - foreign === 1 ? "" : "s"}` + (foreign ? ` · ${foreign} region${foreign === 1 ? "" : "s"} from another editor (kept)` : "");
    }

    function renderForm() {
      const l = labels[sel];
      if (!l) { form.replaceChildren(); return; }
      const name = el("input", { value: l.name, maxlength: "40", "aria-label": "Label name" });
      name.addEventListener("change", () => { l.name = name.value.trim() || l.name; save(); });
      const rows = [el("label", { class: "lbrow" }, el("span", { text: "Name" }), name)];
      if (l.kind === "spawn" || l.kind === "base") {
        const team = el("select", { "aria-label": "Team" });
        TEAMS.forEach((t, i) => team.append(el("option", { value: String(i), text: `${t} (team ${i + 1})` })));
        team.value = String(l.team ?? 0);
        team.addEventListener("change", () => { l.team = +team.value; save(); mv.redraw(); });
        rows.push(el("label", { class: "lbrow" }, el("span", { text: "Team" }), team));
      }
      const num = (label, key, min, max) => {
        const n = el("input", { type: "number", min: String(min), max: String(max), value: String(l[key] ?? 0), "aria-label": label });
        n.addEventListener("change", () => { l[key] = Math.max(min, Math.min(max, Math.round(+n.value) || 0)); n.value = l[key]; save(); mv.redraw(); });
        return el("label", { class: "lbrow" }, el("span", { text: label }), n);
      };
      const shape = l.kind === "foreign" ? "foreign" : kindOf(l.kind).shape;
      if (shape === "point") rows.push(num("Radius", "r", 0, 200), el("p", { class: "hint", text: `At ${l.x}, ${l.y} (tiles).` }));
      if (shape === "tile") rows.push(el("p", { class: "hint", text: `At ${l.x}, ${l.y} (tiles).` }));
      if (shape === "rect") rows.push(num("Width", "w", 1, N), num("Height", "h", 1, N), el("p", { class: "hint", text: `From ${l.x}, ${l.y} (tiles).` }));
      if (shape === "foreign") rows.push(el("p", { class: "hint", text: `A region from another editor (${l.w}×${l.h} tiles around ${l.x}, ${l.y}). Its shape is kept.` }));
      if (shape === "rect" || shape === "foreign") {
        const sw = el("div", { class: "lbrules" });
        for (const [key, text] of RULES) {
          const c = el("input", { type: "checkbox" }); c.checked = !!l[key];
          c.addEventListener("change", () => { l[key] = c.checked; save(); });
          sw.append(el("label", { class: "hint" }, c, " " + text));
        }
        rows.push(el("div", { class: "lbrow" }, el("span", { text: "Inside" }), sw));
      }
      const del = el("button", { type: "button", class: "ghost small", text: "Delete label" });
      del.addEventListener("click", remove);
      form.replaceChildren(...rows, del);
    }
    function remove() { if (sel < 0) return; labels.splice(sel, 1); sel = -1; save("Label deleted. Publish to use it."); mv.redraw(); }

    // ---- drawing ---------------------------------------------------------------------------------
    window.mapOverlays.push((phase, ctx, s, ox, oy) => {
      if (hooks.get().prefix !== lastPrefix) { load(); render(); } // the map was replaced (opened, generated, undone)
      if (phase !== "above" || !show.checked) return;
      ctx.save();
      ctx.font = "12px Inter, system-ui, sans-serif"; ctx.textBaseline = "top";
      labels.forEach((l, i) => {
        const c = colorOf(l), on = i === sel && edit.checked;
        ctx.strokeStyle = c; ctx.fillStyle = c; ctx.lineWidth = on ? 3 : 2;
        let tx, ty;
        const shape = l.kind === "foreign" ? "foreign" : kindOf(l.kind).shape;
        if (shape === "point") {
          const cx = ox + (l.x + 0.5) * s, cy = oy + (l.y + 0.5) * s, rad = Math.max(6, (l.r || 0) * s);
          ctx.beginPath(); ctx.arc(cx, cy, rad, 0, Math.PI * 2); ctx.stroke();
          ctx.beginPath(); ctx.arc(cx, cy, 3, 0, Math.PI * 2); ctx.fill();
          tx = cx - rad; ty = cy + rad + 2;
        } else if (shape === "tile") {
          const x = ox + l.x * s, y = oy + l.y * s, z = Math.max(s, 10);
          ctx.beginPath(); ctx.moveTo(x, y + z); ctx.lineTo(x, y); ctx.lineTo(x + z, y + z * 0.3); ctx.lineTo(x, y + z * 0.6); ctx.fill();
          tx = x; ty = y + z + 2;
        } else {
          ctx.setLineDash(shape === "foreign" ? [2, 4] : [6, 4]);
          ctx.strokeRect(ox + l.x * s, oy + l.y * s, l.w * s, l.h * s);
          ctx.setLineDash([]);
          tx = ox + l.x * s + 3; ty = oy + l.y * s + 3;
        }
        if (on) { ctx.strokeStyle = "#ffffff"; ctx.lineWidth = 1; ctx.strokeText(l.name, tx, ty); }
        ctx.fillStyle = c; ctx.fillText(l.name, tx, ty);
      });
      ctx.restore();
    });

    // ---- pointer: capture phase while Edit is on and a label tool is armed or a label is hit ---------
    const view = mv.view;
    const tileAt = (e) => {
      const cam = mv.cam, r = view.getBoundingClientRect();
      const sx = ((e.clientX - r.left) * view.width) / r.width, sy = ((e.clientY - r.top) * view.height) / r.height;
      const px = (sx - view.width / 2) / cam.z + cam.x, py = (sy - view.height / 2) / cam.z + cam.y;
      return [Math.max(0, Math.min(N - 1, Math.floor(px / 16))), Math.max(0, Math.min(N - 1, Math.floor(py / 16)))];
    };
    function hit(x, y) {
      for (let i = labels.length - 1; i >= 0; i--) {
        const l = labels[i], shape = l.kind === "foreign" ? "foreign" : kindOf(l.kind).shape;
        if (shape === "point" && Math.hypot(x - l.x, y - l.y) <= Math.max(1.5, l.r || 0)) return i;
        if (shape === "tile" && Math.abs(x - l.x) <= 1 && Math.abs(y - l.y) <= 1) return i;
        if ((shape === "rect" || shape === "foreign") && x >= l.x && y >= l.y && x < l.x + l.w && y < l.y + l.h) return i;
      }
      return -1;
    }
    view.addEventListener("pointerdown", (e) => {
      if (!edit.checked || !show.checked || e.button !== 0 || e.ctrlKey || e.metaKey) return;
      const [x, y] = tileAt(e);
      let i = armed ? -1 : hit(x, y);
      if (!armed && i < 0) return; // let the editor's own tools have it
      e.stopImmediatePropagation(); e.preventDefault();
      view.setPointerCapture(e.pointerId);
      if (armed) {
        labels.push(create(armed, x, y)); i = labels.length - 1;
        drag = { i, mode: kindOf(armed).shape, sx: x, sy: y };
        if (armed !== "ballspawn" && armed !== "flagspot" && armed !== "area") { armed = null; add.value = ""; } // one of each kind per pick, except repeats
      } else {
        const l = labels[i];
        drag = { i, mode: "move", sx: x, sy: y, ox: l.x, oy: l.y, foreign: l.kind === "foreign" };
      }
      sel = i; render(); mv.redraw();
    }, true);
    view.addEventListener("pointermove", (e) => {
      if (!drag) return;
      e.stopImmediatePropagation();
      const [x, y] = tileAt(e), l = labels[drag.i];
      if (drag.mode === "point") { const d = Math.round(Math.hypot(x - drag.sx, y - drag.sy)); if (d >= 1) l.r = d; }
      else if (drag.mode === "rect") { l.x = Math.min(x, drag.sx); l.y = Math.min(y, drag.sy); l.w = Math.abs(x - drag.sx) + 1; l.h = Math.abs(y - drag.sy) + 1; }
      else if (drag.mode === "move" && !drag.foreign) { l.x = Math.max(0, Math.min(N - 1, drag.ox + x - drag.sx)); l.y = Math.max(0, Math.min(N - 1, drag.oy + y - drag.sy)); }
      mv.redraw();
    }, true);
    const up = (e) => {
      if (!drag) return;
      e.stopImmediatePropagation();
      const moved = drag.mode !== "move" || labels[drag.i].x !== drag.ox || labels[drag.i].y !== drag.oy;
      drag = null;
      if (moved) save(); else render();
    };
    view.addEventListener("pointerup", up, true);
    view.addEventListener("pointercancel", up, true);
    // Esc / Delete would reach the editor's tool keys first (drawtools): take them while editing labels.
    window.addEventListener("keydown", (e) => {
      if (!edit.checked || e.target.closest?.("input, select, textarea, dialog")) return;
      const stop = () => { e.preventDefault(); e.stopImmediatePropagation(); };
      if (e.key === "Escape" && (armed || sel >= 0)) { stop(); armed = null; add.value = ""; sel = -1; render(); mv.redraw(); }
      else if (e.key === "Delete" && sel >= 0) { stop(); remove(); } // not Backspace: labels have no undo yet
    }, true);

    render();
    api = { labels: () => labels };
    return api;
  }

  /** On Publish: write the spawn labels' settings into the arena; returns a sentence for the publish message. */
  async function publishSettings(arena) {
    if (!api) return "";
    const labelled = spawnSettings(api.labels()), resets = resetSettings(api.labels());
    const post = (path, settings) => fetch("/arena/api/settings/" + path, { method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" }, body: JSON.stringify({ arena, settings })});
    try {
      // resets only for spawn settings the arena has (a removed label's), so an arena that never had them stays clean
      const pv = await (await post("preview", { ...resets, ...labelled })).json().catch(() => null);
      const settings = { ...labelled };
      for (const row of pv?.rows || []) {
        const k = `${row.section}:${row.key}`;
        if (k in resets && row.status === "change" && row.old != null) settings[k] = resets[k];
      }
      if (!Object.keys(settings).length) return "";
      const r = await post("apply", settings);
      const j = await r.json().catch(() => ({ ok: false, message: "HTTP " + r.status }));
      if (!j.ok) return `Spawn settings not set: ${j.message}`;
      const skipped = (j.rows || []).filter((x) => x.status === "skipped");
      const changed = (j.rows || []).filter((x) => x.status === "change").length;
      return (changed ? `Also set ${changed} spawn setting${changed === 1 ? "" : "s"}.` : "Spawn settings already up to date.")
        + (skipped.length ? ` Skipped: ${skipped.map((x) => `${x.section}:${x.key}`).join(", ")}.` : "");
    } catch { return "Spawn settings not set: the server didn't answer."; }
  }

  return { KINDS, TEAMS, fromRegions, toRegions, spawnSettings, resetSettings, mount, publishSettings, lvzToggle };
});
