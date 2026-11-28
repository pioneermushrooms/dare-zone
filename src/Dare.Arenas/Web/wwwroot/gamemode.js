// The Game mode tab of /arena/me: the account's game modes and, for now, their Ships panel.
// Uses app.js (el, $, getJson, postJson, msg, current, lastSettings, openDiff) and gamemode-ships.js.
// Spec: docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md.
"use strict";

const GameModeTab = (() => {
  const G = GameModeShips;
  const PANELS = [["objective", "Objective"], ["teams", "Teams & lives"], ["scoring", "Scoring"], ["time", "Time"],
    ["flags", "Flags / ball"], ["ships", "Ships"], ["rules", "Timers & rules"]];
  const PACE_LABELS = { slow: "Slow & deliberate", steady: "Steady", standard: "Standard", fast: "Fast", mayhem: "Crazy mayhem" };
  const ITEM_LABELS = { none: "None", few: "Few", standard: "Standard", lots: "Lots", loads: "Loads" };
  const UPGRADE_LABELS = { off: "Off", slow: "Slow", standard: "Standard", fast: "Fast" };
  const GROUPS = [
    ["Movement", ["InitialSpeed", "MaximumSpeed", "InitialThrust", "MaximumThrust", "InitialRotation", "MaximumRotation", "AfterburnerEnergy", "Gravity", "GravityTopSpeed"]],
    ["Energy & upgrades", ["InitialEnergy", "MaximumEnergy", "InitialRecharge", "MaximumRecharge", "UpgradeEnergy", "UpgradeRecharge", "UpgradeSpeed", "UpgradeThrust", "UpgradeRotation"]],
    ["Guns", ["InitialGuns", "MaxGuns", "DoubleBarrel", "BulletSpeed", "BulletFireDelay", "BulletFireEnergy", "MultiFireAngle", "MultiFireDelay", "MultiFireEnergy"]],
    ["Bombs & mines", ["InitialBombs", "MaxBombs", "BombBounceCount", "BombSpeed", "BombThrust", "BombFireDelay", "BombFireEnergy", "BombFireEnergyUpgrade", "ShrapnelMax", "ShrapnelRate", "EmpBomb", "MaxMines", "LandmineFireDelay", "LandmineFireEnergy", "LandmineFireEnergyUpgrade", "SeeMines"]],
    ["Items", [...G.START_ITEMS.flatMap((i) => [`Initial${i}`, `${i}Max`]), "BurstSpeed", "BurstShrapnel", "RocketTime"]],
    ["Specials", ["CloakStatus", "StealthStatus", "XRadarStatus", "AntiWarpStatus", "CloakEnergy", "StealthEnergy", "XRadarEnergy", "AntiWarpEnergy", "TurretLimit", "TurretThrustPenalty", "TurretSpeedPenalty", "AttachBounty", "SuperTime", "ShieldsTime", "InitialBounty", "DamageFactor", "PrizeShareLimit"]],
  ];
  const STATUS = ["No", "Can get", "Starts with"]; // CloakStatus etc. 0/1/2

  const st = { modes: [], max: 25, id: null, name: "", data: null, open: null, saveTimer: 0, saving: null, loaded: false };

  const words = (k) => k.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^Initial /, "Start ").replace(/^Maximum |^Max /, "Max ");

  /** Each editable setting's spec, from the arena's settings list; every stock key with no limits when there is none. */
  function specs() {
    const out = {};
    for (const s of (typeof lastSettings !== "undefined" && lastSettings) || [])
      for (const x of s.settings) out[`${s.section}:${x.key}`] = {
        type: x.type, choices: x.choices,
        min: x.min != null && x.min !== "" && Number.isFinite(+x.min) ? +x.min : null,
        max: x.max != null && x.max !== "" && Number.isFinite(+x.max) ? +x.max : null,
      };
    if (Object.keys(out).length) return out;
    for (const [sec, keys] of Object.entries(G.STANDARD)) for (const k of Object.keys(keys)) out[`${sec}:${k}`] = { type: "int" };
    return out;
  }

  const ships = () => st.data.ships;
  const computed = () => G.settings(ships(), specs());
  function withoutTweak(name) {
    const t = { ...ships().tweaks }; delete t[name];
    return G.settings({ ...ships(), tweaks: t }, specs())[name];
  }

  // ---- loading and saving -------------------------------------------------------------------
  async function show() {
    if (!st.loaded) { st.loaded = true; await loadList(); }
    render();
  }

  async function loadList(selectId) {
    const { status, body } = await getJson(API + "/gamemodes");
    if (status === 401) { st.modes = []; st.error = body?.message || "Sign in with your account to keep game modes."; return; }
    st.error = null;
    st.modes = body?.modes || []; st.max = body?.max || 25;
    const want = selectId ?? st.id ?? st.modes[0]?.id ?? null;
    if (want != null && st.modes.some((m) => m.id === want)) await open(want); else { st.id = null; st.data = null; }
  }

  async function open(id) {
    const { body } = await getJson(API + "/gamemodes/get?id=" + id);
    if (!body?.ok) { note(body?.message || "That game mode didn't open.", false); return; }
    st.id = id; st.name = body.name;
    const d = body.data || {};
    st.data = { ...d, version: 1, ships: { ...G.defaults(), ...(d.ships || {}) } };
    st.data.ships.start = { ...G.defaults().start, ...(st.data.ships.start || {}) };
    st.data.ships.roles = { ...G.defaults().roles, ...(st.data.ships.roles || {}) };
    st.data.ships.tweaks = { ...(st.data.ships.tweaks || {}) };
    st.open = null;
  }

  async function create() {
    await flush();
    if (st.modes.length >= st.max) { note(`You have ${st.max} game modes; delete one to add another.`, false); return; }
    const name = await Dlg.prompt("Name your game mode.", { value: "My game mode", maxLength: 40 });
    if (name == null) return;
    const data = { version: 1, ships: G.defaults() };
    const { body } = await postJson("/gamemodes/save", { name, data, settings: G.settings(data.ships, specs()) });
    if (!body?.ok) { note(body?.message, false); return; }
    await loadList(body.id);
    note(body.message, true);
    render();
  }

  function changed() {
    renderShips();
    clearTimeout(st.saveTimer);
    note("Saving…", true);
    st.saveTimer = setTimeout(() => { st.saveTimer = 0; st.saving = save(); }, 800);
  }

  // What a save sends, taken when it starts (not when its reply comes back).
  const saveBody = () => ({ id: st.id, name: st.name, data: st.data, settings: computed() });

  async function save() {
    if (st.id == null) return;
    try {
      const { body } = await postJson("/gamemodes/save", saveBody());
      note(body?.ok ? "Saved." : body?.message || "Not saved.", !!body?.ok);
    } catch {
      note("Not saved: the server didn't answer. Your changes are still here; change anything to try again.", false);
    }
  }

  /** Sends a waiting save now and waits for any save on its way, so nothing that follows can overtake it. */
  async function flush() {
    if (st.saveTimer) { clearTimeout(st.saveTimer); st.saveTimer = 0; st.saving = save(); }
    if (st.saving) { await st.saving; st.saving = null; }
  }

  // Leaving the page with a save waiting: send it anyway.
  window.addEventListener("pagehide", () => {
    if (!st.saveTimer || st.id == null) return;
    clearTimeout(st.saveTimer); st.saveTimer = 0;
    try {
      fetch(API + "/gamemodes/save", { method: "POST", keepalive: true, credentials: "same-origin",
        headers: { ...HDR, "Content-Type": "application/json" }, body: JSON.stringify(saveBody()) });
    } catch { /* nothing more to do while the page goes away */ }
  });

  async function rename() {
    await flush();
    const name = await Dlg.prompt("New name for this game mode.", { value: st.name, maxLength: 40 });
    if (name == null) return;
    const { body } = await postJson("/gamemodes/rename", { id: st.id, name });
    note(body?.message, !!body?.ok);
    if (body?.ok) { await loadList(st.id); render(); }
  }

  async function duplicate() {
    await flush();
    const { body } = await postJson("/gamemodes/duplicate", { id: st.id });
    note(body?.message, !!body?.ok);
    if (body?.ok) { await loadList(body.id); render(); }
  }

  async function remove() {
    await flush();
    if (!await Dlg.confirm(`Delete the game mode ${st.name}? This can't be undone.`, { title: "Delete game mode", ok: "Delete", danger: true })) return;
    const { body } = await postJson("/gamemodes/remove", { id: st.id });
    note(body?.message, !!body?.ok);
    if (body?.ok) { st.id = null; await loadList(); render(); }
  }

  async function saveAsSet() {
    const name = await Dlg.prompt("Name for the settings set.", { value: st.name, maxLength: 40 });
    if (name == null) return;
    const { body } = await postJson("/settingsets/save", { name, settings: computed() });
    note(body?.message, !!body?.ok);
    if (body?.ok && typeof loadSettingSets === "function") loadSettingSets(body.id);
  }

  function apply() {
    openDiff({ settings: computed() }, `Apply ${st.name}`, st.name, $("gmmsg"));
  }

  const note = (text, ok) => msg($("gmmsg"), text || "", ok);

  // ---- rendering ----------------------------------------------------------------------------
  function render() {
    const host = $("gamemode");
    if (!host) return;
    if (st.error) { host.replaceChildren(el("p", { class: "dim", text: st.error })); return; }
    const pick = el("select", { "aria-label": "Game mode" });
    for (const m of st.modes) pick.append(el("option", { value: String(m.id), text: m.name + (m.madeBy ? ` (by ${m.madeBy})` : "") }));
    pick.append(el("option", { value: "new", text: "+ New game mode" }));
    pick.value = st.id != null ? String(st.id) : "new";
    pick.addEventListener("change", async () => {
      if (pick.value === "new") { pick.value = st.id != null ? String(st.id) : "new"; await create(); return; }
      await flush(); await open(+pick.value); render();
    });
    const btn = (text, fn, cls = "small ghost") => { const b = el("button", { type: "button", class: cls, text }); b.addEventListener("click", fn); return b; };
    const top = el("div", { class: "gmtop" }, pick,
      st.id != null ? btn("Rename", rename) : null, st.id != null ? btn("Duplicate", duplicate) : null, st.id != null ? btn("Delete", remove) : null,
      el("span", { class: "dim small", text: "Saved to your account: every pilot name on it sees these." }));
    if (st.id == null) {
      const start = btn("Make a game mode", create, "hot");
      host.replaceChildren(top, el("div", { class: "gmempty" },
        el("p", { text: "A game mode is a set of rules you keep and apply to any of your arenas. Start with the ships: give each one a role, then set the pace and items." }), start));
      return;
    }
    const tabs = el("div", { class: "gmtabs", role: "tablist" });
    for (const [id, label] of PANELS) {
      const t = el("button", { type: "button", role: "tab", class: id === "ships" ? "on" : "soon", "aria-selected": String(id === "ships"), text: label });
      if (id !== "ships") t.addEventListener("click", () => note(`${label} is coming next. Ships works now.`, true));
      tabs.append(t);
    }
    host.replaceChildren(top, tabs, el("div", { id: "gmships" }));
    renderShips();
  }

  function seg(label, values, labels, current, onPick, hint) {
    const box = el("div", { class: "gmseg", role: "radiogroup", "aria-label": label });
    for (const v of values) {
      const b = el("button", { type: "button", role: "radio", "aria-checked": String(v === current), class: v === current ? "on" : "", text: labels[v] });
      b.addEventListener("click", () => onPick(v));
      box.append(b);
    }
    return el("div", { class: "gmctl" }, el("b", { text: label }), box, hint ? el("div", { class: "dim small", text: hint }) : null);
  }

  function renderShips() {
    const host = $("gmships");
    if (!host) return;
    const s = ships();
    const set = (k, v) => { s[k] = v; changed(); };

    const greens = el("span", { class: "gmyn" }, ...[[true, "yes"], [false, "no"]].map(([v, t]) => {
      const b = el("button", { type: "button", class: s.greens === v ? "on" : "", "aria-pressed": String(s.greens === v), text: t });
      b.addEventListener("click", () => set("greens", v));
      return b;
    }));
    const upgrades = el("div", { class: "gmctl" },
      seg("Upgrades", G.UPGRADES, UPGRADE_LABELS, s.upgrades, (v) => set("upgrades", v), "How fast greens make a ship stronger."),
      el("div", { class: "small gmline" }, "Start with: ", el("b", { text: "fixed levels" }), " · ",
        el("span", { class: "dim", text: "random (coming with rules)" })),
      el("div", { class: "small gmline" }, "Pilots pick up greens: ", greens),
      s.greens === false ? el("div", { class: "dim small", text: "No greens appear in this arena; items come only from starting items." }) : null);

    const start = el("div", { class: "gmitems" });
    for (const i of G.START_ITEMS) {
      const n = el("input", { type: "number", min: "0", max: "3", step: "1", value: String(s.start[i] ?? 0), "aria-label": `Starting ${i}` });
      n.addEventListener("change", () => { s.start[i] = Math.max(0, Math.min(3, Math.round(+n.value) || 0)); n.value = s.start[i]; changed(); });
      start.append(el("label", null, i + " ", n));
    }

    const grid = el("div", { class: "gmgrid" },
      seg("Pace", G.PACES, PACE_LABELS, s.pace, (v) => set("pace", v), "Speed, fire rate, energy costs and recharge for every ship."),
      seg("Items", G.ITEMS, ITEM_LABELS, s.items, (v) => set("items", v), "How many greens appear and how many items a ship can hold."),
      upgrades,
      el("div", { class: "gmctl" }, el("b", { text: "Starting items" }), el("div", { class: "dim small", text: "Every ship; a role can add more." }), start));

    const cards = el("div", { class: "gmships" });
    for (const ship of G.SHIPS) {
      const role = G.ROLES.find((r) => r.id === s.roles[ship]) || G.ROLES[0];
      const sel = el("select", { "aria-label": `${ship} role` });
      for (const r of G.ROLES) sel.append(el("option", { value: r.id, text: r.name }));
      sel.value = role.id;
      sel.addEventListener("change", async () => {
        const n = G.tweakCount(s, ship);
        if (n) {
          const keep = await Dlg.choose(`Keep your ${n} tweak${n === 1 ? "" : "s"} on the ${ship}?`, {
            title: "Change role", choices: [{ value: "keep", label: "Keep them" }, { value: "clear", label: "Clear them", danger: true }] });
          if (keep == null) { sel.value = role.id; return; }
          if (keep === "clear") for (const k of Object.keys(s.tweaks)) if (k.startsWith(ship + ":")) delete s.tweaks[k];
        }
        s.roles[ship] = sel.value; changed();
      });
      const n = G.tweakCount(s, ship);
      const ft = el("button", { type: "button", class: "linkish", text: st.open === ship ? "Close" : "Fine-tune" });
      ft.addEventListener("click", () => { st.open = st.open === ship ? null : ship; renderShips(); });
      cards.append(el("div", { class: "gmship" + (st.open === ship ? " open" : "") },
        el("div", { class: "gmshipname", text: ship }), sel,
        el("small", { class: "dim" }, role.blurb, n ? el("span", { class: "gmtuned", text: ` ${n} tuned` }) : null), ft));
    }

    const out = computed();
    const list = el("details", { class: "gmlist" }, el("summary", { text: "Show" }),
      el("ul", null, ...Object.entries(out).map(([k, v]) => el("li", { text: `${k} = ${v}` }))));
    const foot = el("div", { class: "gmfoot" },
      el("span", null, "This game mode sets ", el("b", { text: String(Object.keys(out).length) }), " settings "), list,
      (() => { const b = el("button", { type: "button", class: "small ghost", text: "Save as settings set" }); b.addEventListener("click", saveAsSet); return b; })(),
      (() => { const b = el("button", { type: "button", class: "hot", text: "Apply to arena…" }); b.addEventListener("click", apply); return b; })());

    host.replaceChildren(...[grid, cards, st.open ? drawer(st.open) : null, foot].filter(Boolean));
  }

  function drawer(ship) {
    const s = ships(), sp = specs(), out = computed();
    const groups = el("div", { class: "gmgroups" });
    for (const [title, keys] of GROUPS) {
      const rows = keys.filter((k) => sp[`${ship}:${k}`]).map((k) => tuneRow(ship, k, sp[`${ship}:${k}`], out[`${ship}:${k}`], s));
      if (rows.length) groups.append(el("div", null, el("b", { class: "small dim", text: title }), ...rows));
    }
    const role = G.ROLES.find((r) => r.id === s.roles[ship])?.name || "Standard";
    return el("div", { class: "gmdrawer" },
      el("h3", null, `${ship}: ${role}, fine-tune `, el("span", { class: "dim small", text: "(• is your change; it stays when pace or items change)" })),
      groups);
  }

  function tuneRow(ship, key, spec, value, s) {
    const name = `${ship}:${key}`;
    const tuned = name in s.tweaks;
    const commit = (v) => {
      if (String(v) === withoutTweak(name)) delete s.tweaks[name]; else s.tweaks[name] = String(v);
      changed();
    };
    let input;
    if (spec.type === "bool") {
      input = el("input", { type: "checkbox", role: "switch", "aria-label": name });
      input.checked = value === "1";
      input.addEventListener("change", () => commit(input.checked ? 1 : 0));
    } else if (/Status$/.test(key) && spec.max === 2) {
      input = el("select", { "aria-label": name });
      STATUS.forEach((t, i) => input.append(el("option", { value: String(i), text: t })));
      input.value = value;
      input.addEventListener("change", () => commit(+input.value));
    } else {
      const num = el("input", { type: "number", step: "1", value, "aria-label": name, class: "numval" });
      if (spec.min != null) num.min = spec.min;
      if (spec.max != null) num.max = spec.max;
      num.addEventListener("change", () => { num.value = G.clamp(num.value, spec); commit(num.value); });
      if (spec.min != null && spec.max != null && spec.max > spec.min) {
        const slider = el("input", { type: "range", min: spec.min, max: spec.max, step: "1", value, tabindex: "-1", "aria-label": name + " slider" });
        slider.addEventListener("input", () => { num.value = slider.value; });
        slider.addEventListener("change", () => commit(slider.value));
        input = el("span", { class: "rangeval" }, slider, num);
      } else input = num;
    }
    const reset = tuned ? el("button", { type: "button", class: "linkish small", text: "reset", "aria-label": `Reset ${name}` }) : null;
    reset?.addEventListener("click", () => { delete s.tweaks[name]; changed(); });
    return el("div", { class: "gmrow" + (tuned ? " tuned" : "") }, el("span", { text: words(key) + (tuned ? " •" : "") }), input, reset);
  }

  return { show };
})();
