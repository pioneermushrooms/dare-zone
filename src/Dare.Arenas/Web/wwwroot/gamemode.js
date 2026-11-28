// The Game mode tab of /arena/me: the account's game modes, the wizard (first time? yes / no / expert) and
// its panels: Objective, Teams, Scoring, Time, Flags / ball, Ships, Review.
// Uses app.js (el, $, getJson, postJson, msg, current, lastSettings, openDiff), lvl.js (Lvl.parse),
// gamemode-ships.js and gamemode-rules.js.
// Specs: docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md, 2026-11-28-game-mode-phase2-wizard-design.md.
"use strict";

const GameModeTab = (() => {
  const G = GameModeShips;
  const R = GameModeRules;
  const PANELS = [["objective", "Objective"], ["teams", "Teams"], ["scoring", "Scoring"], ["time", "Time"],
    ["flags", "Flags / ball"], ["ships", "Ships"], ["review", "Review"], ["rules", "Timers & rules"]];
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

  const st = { modes: [], max: 25, id: null, name: "", data: null, open: null, saveTimer: 0, saving: null, loaded: false,
    tab: "objective", step: 0, map: null, mapArena: null };

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
  // A Phase 1 game mode has no game yet (data.game null): its panels show the defaults, but nothing of the
  // game is sent until the owner changes one of those panels (setG makes it real).
  const game = () => st.data.game || R.withDefaults(undefined);
  // Ships first, then the game panels: the game wins where both set a key (only Flag:Flagger* can overlap).
  const computed = () => ({ ...G.settings(ships(), specs()), ...(st.data.game ? R.settings(st.data.game, specs()) : {}) });
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
    st.data.game = d.game ? R.withDefaults(d.game) : null;
    // Phase 1 game modes have no wizard answer: they keep opening as tabs.
    st.data.wizard = { experience: "expert", ...(d.wizard || {}) };
    st.open = null; st.step = 0; st.tab = "objective";
  }

  async function create() {
    await flush();
    if (st.modes.length >= st.max) { note(`You have ${st.max} game modes; delete one to add another.`, false); return; }
    const experience = await Dlg.choose("Is this your first time making a game mode?", {
      title: "New game mode", choices: [{ value: "first", label: "Yes, walk me through it" },
        { value: "some", label: "No, show me everything" }, { value: "expert", label: "I know what I'm doing" }] });
    if (experience == null) return;
    const name = await Dlg.prompt("Name your game mode.", { value: "My game mode", maxLength: 40 });
    if (name == null) return;
    const data = { version: 1, ships: G.defaults(), game: R.defaults(), wizard: { experience } };
    const settings = { ...G.settings(data.ships, specs()), ...R.settings(data.game, specs()) };
    const { body } = await postJson("/gamemodes/save", { name, data, settings });
    if (!body?.ok) { note(body?.message, false); return; }
    await loadList(body.id);
    note(body.message, true);
    render();
  }

  function changed() {
    renderBody();
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
        el("p", { text: "A game mode is a set of rules you keep and apply to any of your arenas: what pilots try to do, the teams, scoring, time and ships. First time? The wizard walks you through it." }), start));
      return;
    }
    host.replaceChildren(top, el("div", { id: "gmbody" }));
    renderBody();
    loadMap();
  }

  // ---- views: first time (one step at a time), some (one page), expert (tabs) -------------------
  const STEP_LABELS = Object.fromEntries(PANELS);
  /** The steps for this game: Flags / ball only when the objective uses flags or a ball. */
  const steps = () => ["objective", "teams", "scoring", "time", ...(R.usesFlags(game()) || R.usesBall(game()) ? ["flags"] : []), "ships", "review"];

  function renderBody() {
    const host = $("gmbody");
    if (!host || !st.data) return;
    const exp = st.data.wizard.experience;
    const link = (text, to) => {
      const b = el("button", { type: "button", class: "linkish small", text });
      b.addEventListener("click", () => { st.data.wizard.experience = to; st.step = 0; changed(); });
      return b;
    };
    const head = el("div", { class: "gmsummary" }, el("p", { text: st.data.game ? R.summary(st.data.game, ships())
      : "Ships only so far: applying changes ship and prize settings. Choose an objective, teams, scoring or time to add the game." }),
      el("div", { class: "gmviews small" }, exp !== "first" ? link("Walk me through it", "first") : null,
        exp !== "some" ? link("Show everything on one page", "some") : null, exp !== "expert" ? link("Show as tabs", "expert") : null));
    const list = steps();
    let body;
    if (exp === "first") {
      st.step = Math.max(0, Math.min(st.step, list.length - 1));
      const bar = el("div", { class: "gmsteps" }, ...list.map((id, i) => {
        const b = el("button", { type: "button", class: i === st.step ? "on" : i < st.step ? "done" : "", text: `${i + 1} ${STEP_LABELS[id]}` });
        b.addEventListener("click", () => { st.step = i; renderBody(); });
        return b;
      }));
      const back = el("button", { type: "button", class: "small ghost", text: "Back" });
      back.disabled = st.step === 0;
      back.addEventListener("click", () => { st.step--; renderBody(); });
      const last = st.step === list.length - 1;
      const next = el("button", { type: "button", class: "hot", text: last ? "Apply to arena…" : `Next: ${STEP_LABELS[list[st.step + 1]]}` });
      next.addEventListener("click", () => { if (last) apply(); else { st.step++; renderBody(); } });
      body = el("div", null, bar, panel(list[st.step]), el("div", { class: "gmnav" }, back, next));
    } else if (exp === "some") {
      body = el("div", null, ...list.filter((id) => id !== "review").map((id) => el("section", { class: "gmsection" }, el("h3", { text: STEP_LABELS[id] }), panel(id))));
    } else {
      if (!list.includes(st.tab)) st.tab = "objective";
      const tabs = el("div", { class: "gmtabs", role: "tablist" });
      for (const [id, label] of PANELS) {
        if (id === "flags" && !list.includes("flags")) continue;
        const soon = id === "rules";
        const t = el("button", { type: "button", role: "tab", class: soon ? "soon" : id === st.tab ? "on" : "", "aria-selected": String(id === st.tab), text: label });
        t.addEventListener("click", () => {
          if (soon) { note("Timers & rules come with the rules module (Phase 4).", true); return; }
          st.tab = id; renderBody();
        });
        tabs.append(t);
      }
      body = el("div", null, tabs, panel(st.tab));
    }
    const onReview = exp === "first" && list[st.step] === "review";
    host.replaceChildren(...[head, body, onReview ? null : footer()].filter(Boolean));
    if (host.querySelector("#gmships")) renderShips();
  }

  function footer() {
    const out = computed();
    const list = el("details", { class: "gmlist" }, el("summary", { text: "Show" }),
      el("ul", null, ...Object.entries(out).map(([k, v]) => el("li", { text: `${k} = ${v}` }))));
    const b1 = el("button", { type: "button", class: "small ghost", text: "Save as settings set" }); b1.addEventListener("click", saveAsSet);
    const b2 = el("button", { type: "button", class: "hot", text: "Apply to arena…" }); b2.addEventListener("click", apply);
    return el("div", { class: "gmfoot" }, el("span", null, "This game mode sets ", el("b", { text: String(Object.keys(out).length) }), " settings "), list, b1, b2);
  }

  // ---- panels -------------------------------------------------------------------------------
  // Which keys each panel shows under "What this changes"; anything else lands under Objective.
  const PANEL_KEYS = {
    teams: (k) => k.startsWith("Team:") || k.startsWith("LegalShip:") || k === "Misc:ShipChangeInterval",
    scoring: (k) => k.startsWith("Kill:") || ["Misc:TeamKillPoints", "Flag:FlagReward", "Flag:SplitPoints", "Periodic:RewardPoints", "Soccer:Reward", "King:RewardFactor"].includes(k),
    time: (k) => k === "Misc:TimedGame" || k === "GameMode:RoundTime" || k === "Misc:TimerWarnings",
    flags: (k) => (k.startsWith("Flag:") && !["Flag:CarryFlags", "Flag:FlagMode", "Flag:FlagReward", "Flag:SplitPoints"].includes(k)) || (k.startsWith("Soccer:") && k !== "Soccer:Reward") || (k.startsWith("King:") && k !== "King:RewardFactor"),
  };
  function changes(id) {
    if (!st.data.game) return null;
    const out = R.settings(st.data.game, specs());
    const mine = Object.entries(out).filter(([k]) => id === "objective"
      ? !Object.values(PANEL_KEYS).some((f) => f(k)) : PANEL_KEYS[id]?.(k));
    if (!mine.length) return null;
    return el("details", { class: "gmchanges" }, el("summary", { text: `What this changes (${mine.length} setting${mine.length === 1 ? "" : "s"})` }),
      el("p", { class: "small", text: mine.map(([k, v]) => `${k} = ${v}`).join(" · ") }));
  }

  function panel(id) {
    const box = el("div", { class: "gmpanel" });
    if (id === "ships") { box.append(el("div", { id: "gmships" })); return box; }
    const parts = { objective: objectivePanel, teams: teamsPanel, scoring: scoringPanel, time: timePanel, flags: flagsPanel, review: reviewPanel }[id]?.() || [];
    box.append(...parts.filter(Boolean), ...(id === "review" ? [] : [changes(id)].filter(Boolean)));
    return box;
  }

  const setG = (fn) => { if (!st.data.game) st.data.game = R.withDefaults(undefined); fn(st.data.game); changed(); };
  function row(label, hint, ...controls) {
    return el("div", { class: "gmrow2" }, el("div", { class: "gmlab" }, el("b", { text: label }), hint ? el("span", { class: "dim small", text: hint }) : null),
      el("div", { class: "gmval" }, ...controls.filter(Boolean)));
  }
  function choice(values, current, onPick, label) {
    const box = el("div", { class: "gmseg gmsegin", role: "radiogroup", "aria-label": label || "" });
    for (const [v, text] of values) {
      const b = el("button", { type: "button", role: "radio", "aria-checked": String(v === current), class: v === current ? "on" : "", text });
      b.addEventListener("click", () => onPick(v));
      box.append(b);
    }
    return box;
  }
  const yesNo = (value, onSet, label) => choice([[true, "Yes"], [false, "No"]], !!value, onSet, label);
  function numBox(value, min, max, onSet, label, suffix) {
    const n = el("input", { type: "number", min: String(min), max: String(max), step: "1", value: String(value), "aria-label": label, class: "numval" });
    n.addEventListener("change", () => { const v = Math.max(min, Math.min(max, Math.round(+n.value) || 0)); n.value = v; onSet(v); });
    return suffix ? el("span", { class: "gmnum" }, n, el("span", { class: "dim small", text: " " + suffix })) : n;
  }
  const soonRow = (label, text) => el("div", { class: "gmrow2 gmsoonrow" }, el("div", { class: "gmlab" }, el("b", { text: label })),
    el("div", { class: "gmval dim small" }, text, " ", el("span", { class: "gmbadge", text: "COMING WITH RULES" })));

  function objectivePanel() {
    const g = game();
    const cards = el("div", { class: "gmcards" });
    for (const o of R.OBJECTIVES) {
      const c = el("button", { type: "button", class: "gmcard" + (o.id === g.objective ? " on" : "") + (o.soon ? " soon" : ""), "aria-pressed": String(o.id === g.objective) },
        o.soon ? el("span", { class: "gmbadge", text: "COMING WITH RULES" }) : null, el("b", { text: o.name }), el("span", { class: "dim small", text: o.blurb }));
      if (o.soon) c.disabled = true;
      else c.addEventListener("click", () => setG((x) => { x.objective = o.id; }));
      cards.append(c);
    }
    return [el("p", { class: "gmq", text: "What do pilots try to do?" }), cards, mapNote()];
  }

  function teamsPanel() {
    const t = game().teams;
    const shipsRow = el("div", { class: "gmshiptoggles" });
    G.SHIPS.forEach((name, i) => {
      const on = ((t.ships >> i) & 1) === 1;
      const b = el("button", { type: "button", class: on ? "on" : "", "aria-pressed": String(on), text: name });
      b.addEventListener("click", () => setG((g) => { const m = g.teams.ships ^ (1 << i); g.teams.ships = m === 0 ? g.teams.ships : m; }));
      shipsRow.append(b);
    });
    return [
      el("p", { class: "gmq", text: "Who plays against whom?" }),
      row("Teams", "How many teams the zone forms as pilots arrive.", choice([[0, "Free-for-all"], [2, "2"], [3, "3"], [4, "4"]], +t.count, (v) => setG((g) => { g.teams.count = v; }), "Teams")),
      +t.count ? row("Pilots per team", "The most on one team.", numBox(t.perTeam, 1, 100, (v) => setG((g) => { g.teams.perTeam = v; }), "Pilots per team")) : null,
      +t.count ? row("Keep teams even", "New pilots join the smaller team.", yesNo(t.even, (v) => setG((g) => { g.teams.even = v; }), "Keep teams even")) : null,
      row("Private teams", "Pilots may make their own team with ?team.", yesNo(t.private, (v) => setG((g) => { g.teams.private = v; }), "Private teams")),
      row("Ships allowed", "Tap a ship to turn it off. At least one stays on.", shipsRow),
      row("Ship change wait", "Seconds between ship changes; 0 for none.", numBox(t.shipChangeSec, 0, 600, (v) => setG((g) => { g.teams.shipChangeSec = v; }), "Ship change wait", "seconds")),
      soonRow("Lives", "Each pilot gets a set number of lives."),
    ];
  }

  function scoringPanel() {
    const g = game(), s = g.scoring, o = g.objective;
    return [
      el("p", { class: "gmq", text: "How do pilots score?" }),
      row("Points for a kill", "The killed pilot's bounty, or the same number every time.",
        choice([["bounty", "Their bounty"], ["fixed", "Fixed"]], s.kill, (v) => setG((x) => { x.scoring.kill = v; }), "Points for a kill"),
        s.kill === "fixed" ? numBox(s.fixedKill, 0, 100000, (v) => setG((x) => { x.scoring.fixedKill = v; }), "Fixed kill points", "points") : null),
      row("Team kills score", "Points for killing a teammate.", yesNo(s.teamKills, (v) => setG((x) => { x.scoring.teamKills = v; }), "Team kills score")),
      ["carryall", "ownall", "rabbit"].includes(o) ? row("Win reward", "Points for winning the flag game.", numBox(s.winReward, 0, 1000000, (v) => setG((x) => { x.scoring.winReward = v; }), "Win reward", "points")) : null,
      ["carryall", "ownall", "rabbit"].includes(o) ? row("Split the reward", "Share it across the team instead of each getting it all.", yesNo(s.split, (v) => setG((x) => { x.scoring.split = v; }), "Split the reward")) : null,
      o === "turf" ? row("Points per flag", "For each flag a team holds, each time.", numBox(s.turfPoints, 0, 100000, (v) => setG((x) => { x.scoring.turfPoints = v; }), "Points per flag", "points")) : null,
      o === "turf" ? row("Every", "How often turf points are handed out.", numBox(s.turfEverySec, 10, 3600, (v) => setG((x) => { x.scoring.turfEverySec = v; }), "Turf interval", "seconds")) : null,
      o === "powerball" ? row("Goal reward", "Points for scoring a goal.", numBox(s.goalReward, 0, 1000000, (v) => setG((x) => { x.scoring.goalReward = v; }), "Goal reward", "points")) : null,
      o === "king" ? row("Winner's reward", "Grows with the number of pilots.", numBox(s.kingReward, 0, 100000, (v) => setG((x) => { x.scoring.kingReward = v; }), "King reward")) : null,
      soonRow("First to N points", "End the game when a team reaches a score."),
    ];
  }

  function timePanel() {
    const t = game().time;
    const warn = el("input", { type: "text", value: t.warnings.join(", "), "aria-label": "Warnings", class: "textval", maxlength: "40" });
    warn.addEventListener("change", () => setG((g) => {
      g.time.warnings = warn.value.split(/[\s,]+/).map((x) => Math.round(+x)).filter((x) => x > 0 && x < 100000).slice(0, 8);
    }));
    return [
      el("p", { class: "gmq", text: "How long is a round?" }),
      row("Time limit", "0 for no limit. Pilots see the time left with ?time.", numBox(t.minutes, 0, 600, (v) => setG((g) => { g.time.minutes = v; }), "Time limit", "minutes")),
      +t.minutes ? row("Warnings", "Seconds before the end, separated by commas.", warn) : null,
      soonRow("When time runs out", "Points to the leader, restart, or warp everyone to spawn."),
      +t.minutes ? el("p", { class: "dim small", text: ["carryall", "ownall", "rabbit"].includes(game().objective)
        ? "When time runs out, the team with the most flags wins the flag reward (a tie is a draw), and the next round starts. A flag win starts the next round too."
        : "When time runs out, the next round starts; nothing else happens yet." }) : null,
    ];
  }

  function flagsPanel() {
    const g = game(), f = g.flags, b = g.ball, o = g.objective;
    const out = [];
    if (R.usesFlags(g)) {
      out.push(el("p", { class: "gmq", text: o === "turf" ? "Turf flags" : "The flags" }));
      if (o === "turf") out.push(el("p", { class: "dim small", text: "Turf flags are the flag tiles on your map: place them in the map editor." }));
      if (o === "carryall" || o === "ownall") out.push(row("How many flags", "They appear near the middle of the map.", numBox(f.count, 1, 256, (v) => setG((x) => { x.flags.count = v; }), "How many flags")));
      if (o !== "turf") {
        out.push(row(o === "rabbit" ? "Hold to win" : "Hold all to win", "How long the winning team must keep them.", numBox(f.holdSec, 1, 3600, (v) => setG((x) => { x.flags.holdSec = v; }), "Hold time", "seconds")));
        out.push(row("Dropped flags go", "Where a flag lands when its carrier drops it.", choice([[false, "Near the carrier"], [true, "Back to the middle"]], !!f.dropCenter, (v) => setG((x) => { x.flags.dropCenter = v; }), "Dropped flags go")));
        out.push(row("Carrier drops it after", "Seconds; 0 to keep it until killed.", numBox(f.dropAfterSec, 0, 3600, (v) => setG((x) => { x.flags.dropAfterSec = v; }), "Auto drop", "seconds")));
        out.push(row("Carrier shows on radar", "Everyone sees the carrier as a red dot.", yesNo(f.carrierOnRadar, (v) => setG((x) => { x.flags.carrierOnRadar = v; }), "Carrier on radar")));
        out.push(row("Carrier speed change", "Negative slows the carrier down.", numBox(f.carrierSpeed, -5000, 5000, (v) => setG((x) => { x.flags.carrierSpeed = v; }), "Carrier speed")));
      }
    }
    if (R.usesBall(g)) {
      out.push(el("p", { class: "gmq", text: "The ball" }));
      out.push(row("How many balls", "", numBox(b.count, 1, 8, (v) => setG((x) => { x.ball.count = v; }), "How many balls")));
      const layouts = el("div", { class: "gmlayouts" });
      for (const l of R.GOAL_LAYOUTS) {
        const c = el("button", { type: "button", class: l.id === b.layout ? "on" : "", "aria-pressed": String(l.id === b.layout), text: l.name });
        c.addEventListener("click", () => setG((x) => { x.ball.layout = l.id; }));
        layouts.append(c);
      }
      out.push(row("Goals each team defends", "Goals are the goal tiles on your map.", layouts));
      out.push(row("Ball bounces off walls", "", yesNo(b.bounce, (v) => setG((x) => { x.ball.bounce = v; }), "Ball bounces")));
      out.push(row("Carrier can fire guns", "", yesNo(b.guns, (v) => setG((x) => { x.ball.guns = v; }), "Carrier guns")));
      out.push(row("Carrier can fire bombs", "", yesNo(b.bombs, (v) => setG((x) => { x.ball.bombs = v; }), "Carrier bombs")));
      out.push(row("Win by", "Goals ahead needed to win; 0 for any lead.", numBox(b.winBy, 0, 100, (v) => setG((x) => { x.ball.winBy = v; }), "Win by", "goals")));
    }
    out.push(mapNote());
    return out;
  }

  function reviewPanel() {
    const n = Object.keys(computed()).length;
    return [el("p", { class: "gmq", text: `${st.name}, ready to apply` }),
      el("p", { class: "gmbig", text: st.data.game ? R.summary(st.data.game, ships()) : "Ships only: no game chosen yet." }),
      el("p", { class: "dim small", text: `Sets ${n} settings. You see every change before it's applied.` })];
  }

  // ---- map checks: flag tiles (170) for turf, goal tiles (172) for powerball ----------------------
  async function loadMap() {
    const arena = typeof current !== "undefined" && current?.arena;
    if (!arena || st.mapArena === arena || typeof Lvl === "undefined") return;
    st.mapArena = arena; st.map = null;
    try {
      const r = await fetch(API + "/map?arena=" + encodeURIComponent(arena), { credentials: "same-origin" });
      if (!r.ok) return;
      const parsed = Lvl.parse(new Uint8Array(await r.arrayBuffer()));
      let flags = 0, goals = 0;
      for (const t of parsed.grid) { if (t === 170) flags++; else if (t === 172) goals++; }
      // the map's Game labels (map editor), kept as eLVL regions
      let labels = [];
      try { if (typeof Elvl !== "undefined" && typeof GameLabels !== "undefined") labels = GameLabels.fromRegions(Elvl.regions(parsed.prefix)); } catch { labels = []; }
      if (st.mapArena === arena) { st.map = { arena, flags, goals, labels }; renderBody(); }
    } catch { /* no map check */ }
  }

  function mapNote() {
    const m = st.map, o = game().objective;
    if (!m) return null;
    const editor = () => { const a = el("a", { href: "/arena/map?arena=" + encodeURIComponent(m.arena), text: "Open the map editor" }); return a; };
    if (o === "turf" && m.flags === 0) return el("div", { class: "gmwarn" }, el("span", { text: `Turf needs flags on the map, and ${m.arena}'s map has none.` }), editor());
    if (o === "turf") return el("div", { class: "gmok small", text: `${m.arena}'s map has ${m.flags} flag${m.flags === 1 ? "" : "s"}.` });
    if (o === "powerball" && m.goals === 0) return el("div", { class: "gmwarn" }, el("span", { text: `Powerball needs goals on the map, and ${m.arena}'s map has none.` }), editor());
    const parts = [];
    if (o === "powerball") {
      parts.push(`${m.arena}'s map has ${m.goals} goal tile${m.goals === 1 ? "" : "s"}.`);
      const balls = m.labels.filter((l) => l.kind === "ballspawn").length;
      parts.push(balls ? `Balls start at the map's ${balls} ball spawn label${balls === 1 ? "" : "s"}.` : "Balls start in the middle of the map; place Ball spawn labels in the map editor to choose.");
    }
    if (["carryall", "ownall", "rabbit"].includes(o)) {
      const f = m.labels.find((l) => l.kind === "flagspawn");
      parts.push(f ? `Flags appear at the map's Flag spawn area (${f.x}, ${f.y}, radius ${f.r}).` : "Flags appear near the middle of the map; place a Flag spawn area label in the map editor to choose.");
    }
    const bases = m.labels.filter((l) => l.kind === "base").length, spots = m.labels.filter((l) => l.kind === "flagspot").length;
    if (bases || spots) parts.push(`The map has ${bases} base${bases === 1 ? "" : "s"} and ${spots} flag spot${spots === 1 ? "" : "s"}, ready for capture-the-flag and hold-the-base (rules, Phase 4).`);
    if (!parts.length) return null;
    return el("div", { class: "gmok small" }, parts.join(" "), " ", editor());
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

    host.replaceChildren(...[grid, cards, st.open ? drawer(st.open) : null].filter(Boolean));
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
