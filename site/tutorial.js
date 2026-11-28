// The zone's tutorial, as the home page shows it over the game: the mentor's box bottom-left above
// the game's chat (portrait, name, "step n of m", one line typed out), with Skip and Leave; the
// "Try the tutorial" offer; and, when a newcomer's first run ends, a small arena chooser.
//
// The game (play/start.mjs) hands over the zone's hint lines through globalThis.foundationTutorial,
// and takes the buttons' commands and the player's keys through globalThis.dareTutorial
// ({ command, keyFor }). Hint lines: "~tut~ " then
//   say <n>/<of> <text>   the mentor's line; {gun}, {bomb}... are the game's action names
//   reveal <part>          a HUD part was uncovered (the game draws the glow; nothing to do here)
//   prompt skip | try      point out Skip | offer the tutorial
//   end list | none        it's over: offer the arena list | don't
// and an empty line when the player leaves an arena, which ends whatever the box was showing there
// (a ?go out of the tutorial gets no "end"); the chooser stays, since "end list" comes just before
// the zone moves the player on.
// The buttons are for the mouse and touch only: nothing here takes the keyboard from the game.
//
// The DOM-free half is exported for node (tests/js/tutorial.test.js).
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root && root.document) api.mount(root);
})(typeof self !== "undefined" ? self : this, () => {
  /** The mentor's name: src/Dare.Arenas/tutorial/script.json's "mentor" (hint lines don't carry it). */
  const MENTOR = "Vex";
  /** Set when a tutorial ends; play/start.mjs then logs a guest in as "guest:done". */
  const DONE_KEY = "dare.tutorial.done";
  const PREFIX = "~tut~ ";
  const PARTS = new Set(["radar", "energy", "statbox", "items", "chat"]);
  /** The zone's name for the main public arena (the page lists it as ""). */
  const MAIN_PUBLIC = "0";

  /** A hint line, read: { kind: "say", step, of, text } | { kind: "reveal", part } |
   *  { kind: "prompt", prompt } | { kind: "end", list } | null for anything else. */
  function parseHint(line) {
    if (typeof line !== "string" || !line.startsWith(PREFIX)) return null;
    const body = line.slice(PREFIX.length);
    const space = body.indexOf(" ");
    const word = space < 0 ? body : body.slice(0, space);
    const rest = space < 0 ? "" : body.slice(space + 1);
    switch (word) {
      case "say": {
        const m = /^(\d+)\/(\d+)(?: ([\s\S]*))?$/.exec(rest);
        if (!m) return null;
        const step = Number(m[1]), of = Number(m[2]);
        if (step < 1 || step > of) return null;
        return { kind: "say", step, of, text: m[3] ?? "" };
      }
      case "reveal": return PARTS.has(rest) ? { kind: "reveal", part: rest } : null;
      case "prompt": return rest === "skip" || rest === "try" ? { kind: "prompt", prompt: rest } : null;
      case "end": return rest === "list" || rest === "none" ? { kind: "end", list: rest === "list" } : null;
      default: return null;
    }
  }

  /** `text` with each {action} replaced by how the player's keys for it read; the action's own
   *  name where the game has no answer (an unknown action, or no game yet). */
  function fillKeys(text, keyFor) {
    return String(text).replace(/\{([a-z_]+)\}/g, (_, action) => {
      let key = "";
      try { key = typeof keyFor === "function" ? String(keyFor(action) ?? "") : ""; } catch {}
      return key || action;
    });
  }

  /** The line a button sends: skip, leave, try (the tutorial), or go to `arena`; null for
   *  anything else (the game refuses every other line too). */
  function commandFor(button, arena) {
    switch (button) {
      case "skip": return "?skip";
      case "leave": return "?leave";
      case "try": return "?tutorial";
      case "go": {
        const name = arena ? String(arena) : MAIN_PUBLIC;
        return /^[A-Za-z0-9#]{1,16}$/.test(name) ? `?go ${name}` : null;
      }
      default: return null;
    }
  }

  /** The chooser's rows, from the arena list's ({ value, name, players, mine }): the open
   *  arenas, busiest first, without the tutorial's own. */
  function arenaChoices(rows) {
    return (Array.isArray(rows) ? rows : [])
      .filter((a) => a && !a.mine && !/^#training\d*$/i.test(a.value ?? ""))
      .map((a) => ({ name: a.name || "public", players: a.players ?? 0, command: commandFor("go", a.value) }))
      .filter((a) => a.command)
      .sort((a, b) => b.players - a.players || a.name.localeCompare(b.name, undefined, { sensitivity: "base", numeric: true }));
  }

  // ---- the page ------------------------------------------------------------------------------
  const TYPE_MS = 28; // per character
  const MOUTH_MS = 120; // per mouth frame while typing
  const NUDGE_MS = 10000;

  function mount(win) {
    const doc = win.document;
    const el = (tag, cls, text) => {
      const e = doc.createElement(tag);
      if (cls) e.className = cls;
      if (text != null) e.textContent = text;
      return e;
    };
    // Mouse and touch only: a press doesn't move the keyboard focus off the game's canvas.
    const button = (cls, text, onClick) => {
      const b = el("button", cls, text);
      b.type = "button";
      b.tabIndex = -1;
      b.addEventListener("pointerdown", (e) => e.preventDefault());
      b.addEventListener("mousedown", (e) => e.preventDefault());
      b.addEventListener("click", onClick);
      return b;
    };
    const send = (cmd) => {
      if (!cmd) return;
      try { win.dareTutorial?.command(cmd); } catch (error) { console.warn(`tutorial: ${error?.message ?? error}`); }
    };
    const keyFor = (action) => win.dareTutorial?.keyFor(action);
    const reducedMotion = () => !!win.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

    // the mentor's box
    const box = el("section", "tut tut-box");
    box.hidden = true;
    box.setAttribute("aria-label", "Tutorial");
    const face = el("div", "tut-face");
    face.setAttribute("aria-hidden", "true");
    const main = el("div", "tut-main");
    const head = el("div", "tut-head");
    const name = el("span", "tut-name", MENTOR);
    const step = el("span", "tut-step");
    head.append(name, step);
    const line = el("p", "tut-line");
    line.setAttribute("aria-live", "polite");
    const skip = button("tut-btn", "Skip", () => send(commandFor("skip")));
    skip.title = "Skip this step";
    const leave = button("tut-btn ghost", "Leave tutorial", () => send(commandFor("leave")));
    const buttons = el("div", "tut-buttons");
    buttons.append(skip, leave);
    main.append(head, line, buttons);
    box.append(face, main);

    // the offer, for a newcomer the tutorial wasn't started for
    const offer = el("section", "tut tut-offer");
    offer.hidden = true;
    offer.setAttribute("aria-label", "Tutorial");
    const offerFace = el("div", "tut-face");
    offerFace.setAttribute("aria-hidden", "true");
    const offerMain = el("div", "tut-main");
    const offerButtons = el("div", "tut-buttons");
    offerButtons.append(
      button("tut-btn", "Try the tutorial", () => { offer.hidden = true; send(commandFor("try")); }),
      button("tut-btn ghost", "Not now", () => { offer.hidden = true; }),
    );
    offerMain.append(el("div", "tut-head", "New here?"), el("p", "tut-line", "A few minutes of flying lessons, then you're in."), offerButtons);
    offer.append(offerFace, offerMain);

    // where to go once it's over
    const chooser = el("section", "tut tut-choose");
    chooser.hidden = true;
    chooser.setAttribute("aria-label", "Pick an arena");
    const list = el("ul", "tut-arenas");
    const close = button("tut-btn ghost", "Stay here", () => { chooser.hidden = true; });
    chooser.append(el("div", "tut-head", "Where next?"), list, close);

    doc.body.append(box, offer, chooser);

    // the line, typed out with the mouth moving
    let typing = null, mouth = null, nudge = null;
    const stopTyping = () => {
      clearInterval(typing); clearInterval(mouth);
      typing = mouth = null;
      face.classList.remove("open");
    };
    function say(text, n, of) {
      stopTyping();
      offer.hidden = true;
      chooser.hidden = true;
      box.hidden = false;
      step.textContent = `Step ${n} of ${of}`;
      const full = fillKeys(text, keyFor);
      line.setAttribute("aria-label", full);
      if (reducedMotion()) { line.textContent = full; return; }
      const chars = [...full];
      let shown = 0;
      line.textContent = "";
      typing = setInterval(() => {
        shown += 1;
        line.textContent = chars.slice(0, shown).join("");
        if (shown >= chars.length) stopTyping();
      }, TYPE_MS);
      mouth = setInterval(() => face.classList.toggle("open"), MOUTH_MS);
    }

    function nudgeSkip() {
      clearTimeout(nudge);
      skip.classList.add("nudge");
      nudge = setTimeout(() => skip.classList.remove("nudge"), NUDGE_MS);
    }

    function showChooser() {
      const render = () => {
        const rows = arenaChoices(win.foundationArenaRows?.());
        list.replaceChildren(...rows.map((a) => {
          const li = el("li");
          const b = button("tut-arena", "", () => { chooser.hidden = true; send(a.command); });
          b.append(el("span", "aname", a.name), el("span", "acount" + (a.players ? " live" : ""), String(a.players)));
          b.title = a.players === 1 ? "1 pilot here" : `${a.players} pilots here`;
          li.append(b);
          return li;
        }));
        if (!rows.length) list.replaceChildren(el("li", "tut-none", "No arena list just now: press Esc then A in the game."));
      };
      render();
      chooser.hidden = false;
      win.refreshFoundationArenas?.();
      setTimeout(() => { if (!chooser.hidden) render(); }, 1500);
    }

    function end(withList) {
      try { win.localStorage.setItem(DONE_KEY, "1"); } catch {}
      leftArena();
      if (withList) showChooser();
    }

    function leftArena() {
      stopTyping();
      clearTimeout(nudge);
      skip.classList.remove("nudge");
      box.hidden = true;
      offer.hidden = true;
    }
    // back on the home page: nothing of the tutorial stays up
    new win.MutationObserver(() => {
      if (!doc.body.classList.contains("playing")) { leftArena(); chooser.hidden = true; }
    }).observe(doc.body, { attributes: true, attributeFilter: ["class"] });

    win.foundationTutorial = (raw) => {
      if (raw === "") { leftArena(); return; }
      const hint = parseHint(raw);
      if (!hint) return;
      if (hint.kind === "say") say(hint.text, hint.step, hint.of);
      else if (hint.kind === "prompt" && hint.prompt === "skip") nudgeSkip();
      else if (hint.kind === "prompt" && hint.prompt === "try") { chooser.hidden = true; offer.hidden = false; }
      else if (hint.kind === "end") end(hint.list);
      // reveal: the game draws the glow round the part itself
    };
  }

  return { MENTOR, DONE_KEY, parseHint, fillKeys, commandFor, arenaChoices, mount };
});
