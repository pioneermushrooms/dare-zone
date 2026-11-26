// The home page's loading screen: after Enter, a copy of the panel the game itself draws while it connects
// (the same steps under the same names, the same bar and line, at the same place and size), so the whole
// way in reads as one screen. The page fills in the first step, downloading the game, from what
// /play/start.mjs shows (the Enter button's "Loading N%", the status line, the page switching to the game).
// Once the game runs it reports each step through globalThis.dareLoadingStage, and its first report means it
// has drawn its own panel on the canvas underneath: this one then fades out over it.
const $ = (id) => document.getElementById(id);
const button = $("start"), label = $("start-label"), status = $("status"), loader = $("loader");
const TIPS = [
  "In the game, Esc opens the menu; Esc then G (or F8) opens Graphics to give your ships your own look.",
  "Type ?go <arena> in chat to fly to another arena.",
  "Arena owners can draw maps, set rules, add graphics and bots in the Arena Editor.",
  "Your keys and macros live on your Pilot account and come with you to any computer.",
  "No pilot name? Fly as a guest: you'll get a quirky space name, new every time.",
];
// the game's steps (its Stage keys), with the page's own in front
const STEPS = ["game", "connect", "signin", "enter", "download"];
const ROWS = STEPS.length;
let active = false, tipTimer = 0, tip = Math.floor(Math.random() * TIPS.length), lastPercent = 0;
let heard = false, fallback = 0;

// Where the game puts its panel, worked out as the game does (connect_screen.rs): one game pixel is
// round(devicePixelRatio) device pixels, twice that on a big window, and the panel is 304 wide (less on a
// narrow window), centred a little above the middle.
function layout() {
  const dpr = window.devicePixelRatio || 1;
  const scale = Math.min(8, Math.max(1, Math.round(dpr)));
  const physW = Math.round(innerWidth * dpr), physH = Math.round(innerHeight * dpr);
  const zoom = Math.floor(physW / scale) >= 1100 && Math.floor(physH / scale) >= 640 ? 2 : 1;
  const px = scale * zoom; // device pixels per game pixel
  const u = px / dpr; // CSS pixels per game pixel
  const viewW = physW / px, viewH = physH / px;
  const w = Math.floor(Math.min(304, viewW - 16));
  const h = 65 + ROWS * 15;
  const x = Math.floor((viewW - w) / 2), y = Math.max(4, Math.floor((viewH - h) / 2 - 8));
  const s = loader.style;
  s.setProperty("--u", `${u}px`);
  s.setProperty("--x", `${Math.round(x * px) / dpr}px`);
  s.setProperty("--y", `${Math.round(y * px) / dpr}px`);
  s.setProperty("--w", w);
  s.setProperty("--h", h);
  // the game's font is 8 game pixels a letter: space this one out to the same
  const ctx = document.createElement("canvas").getContext("2d");
  ctx.font = `${16 * u}px VT323, ui-monospace, Menlo, monospace`;
  const advance = ctx.measureText("MMMMMMMMMM").width / 10;
  s.setProperty("--ls", `${8 * u - advance}px`);
}

function step(name, text, percent) {
  const at = STEPS.indexOf(name);
  for (const li of loader.querySelectorAll("[data-step]")) {
    const i = STEPS.indexOf(li.dataset.step);
    li.className = i < at ? "done" : i === at ? "now" : "";
  }
  $("loader-text").textContent = text;
  const bar = $("loader-bar");
  bar.classList.toggle("unknown", percent == null);
  bar.style.setProperty("--p", `${percent ?? 0}%`);
  $("loader-pct").textContent = percent == null ? "" : `${percent}%`;
}

function showTip() {
  $("loader-tip").textContent = `Tip: ${TIPS[tip % TIPS.length]}`;
  tip++;
}

function open() {
  if (active) return;
  active = true;
  heard = false;
  $("loader-arena").textContent = $("arena")?.value || "a public arena";
  $("loader-error").hidden = true;
  $("loader-tip").hidden = false;
  loader.classList.remove("handoff");
  layout();
  loader.hidden = false;
  document.body.classList.add("loading-game");
  showTip();
  tipTimer = setInterval(showTip, 6000);
}

function close() {
  active = false;
  clearInterval(tipTimer);
  clearTimeout(fallback);
  loader.hidden = true;
  loader.classList.remove("handoff");
  document.body.classList.remove("loading-game");
}

// The game's panel is on the canvas underneath: fade this copy of it away.
function handOff() {
  if (!active || loader.classList.contains("handoff")) return;
  clearTimeout(fallback);
  loader.classList.add("handoff");
  setTimeout(() => { if (loader.classList.contains("handoff")) close(); }, 400);
}

// The game's word on where it has got to (see web.rs, loading_stage): a step, the line under the bar and how
// far through this step's downloads (-1 when unknown), or "playing" once the arena is on screen.
globalThis.dareLoadingStage = (stage, detail, percent) => {
  if (!active) return;
  heard = true;
  if (stage === "playing") { close(); return; }
  if (STEPS.includes(stage)) step(stage, detail, percent >= 0 ? percent : null);
  handOff();
};

function update() {
  if (!active) return;
  const playing = document.body.classList.contains("playing");
  if (status.classList.contains("error") && status.textContent) {
    $("loader-error").hidden = false;
    $("loader-tip").hidden = true;
    $("loader-errortext").textContent = status.textContent;
    return;
  }
  if (playing) {
    if (heard) return; // the game says where it is now
    step("connect", "Starting the game", null);
    // a game too old to report its steps: let it show itself after a moment
    if (!heard && !fallback) fallback = setTimeout(() => { if (!heard) handOff(); }, 6000);
    return;
  }
  const m = /Loading (\d+)%/.exec(label.textContent);
  if (button.classList.contains("downloading") && m) {
    lastPercent = Number(m[1]);
    step("game", "Downloading the game", lastPercent);
  } else if (lastPercent > 0 && label.textContent === "Enter") {
    step("connect", "Starting the game", null);
  }
  // Enter came back without the game starting: cancelled (e.g. closed the sign-in box)
  if (!button.disabled && !button.classList.contains("downloading") && label.textContent === "Enter") {
    setTimeout(() => { if (!button.disabled && !document.body.classList.contains("playing")) close(); }, 400);
  }
}

if (button && loader) {
  button.addEventListener("click", () => {
    // signing in first: the sign-in box is on top; the screen waits underneath
    lastPercent = 0;
    open();
    step("game", "Getting ready", null);
    setTimeout(update, 50);
  });
  const watch = new MutationObserver(update);
  watch.observe(button, { attributes: true, attributeFilter: ["class", "disabled"] });
  watch.observe(label, { childList: true, characterData: true, subtree: true });
  watch.observe(status, { childList: true, attributes: true, characterData: true, subtree: true });
  watch.observe(document.body, { attributes: true, attributeFilter: ["class"] });
  $("loader-back").addEventListener("click", close);
  addEventListener("resize", () => { if (active) layout(); });
  document.fonts?.load("16px VT323").then(() => { if (active) layout(); }, () => {});
}
