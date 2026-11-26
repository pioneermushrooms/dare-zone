// The home page's loading screen: after Enter, a warp through the stars under the arena's name, a status word
// and a progress line, from the game's download until the pilot is in the arena. It stays over the game's
// canvas the whole way: the game tells the page where it has got to through globalThis.dareLoadingStage
// (web.rs, loading_stage), and "playing" ends the warp in a flash that opens onto the arena.
// The desktop game draws the same warp itself (connect_screen.rs); the two share the numbers below.
const $ = (id) => document.getElementById(id);
const button = $("start"), label = $("start-label"), status = $("status"), loader = $("loader");
const TIPS = [
  "In the game, <b>Esc</b> opens the menu; <b>Esc</b> then <b>G</b> (or <b>F8</b>) opens Graphics to give your ships your own look.",
  "Type <b>?go &lt;arena&gt;</b> in chat to fly to another arena.",
  "Arena owners can draw maps, set rules, add graphics and bots in the <b>Arena Editor</b>.",
  "Your keys and macros live on your <b>Pilot</b> account and come with you to any computer.",
  "No pilot name? Fly as a <b>guest</b>: you'll get a quirky space name, new every time.",
];
// Each step's share of the progress line, in order, and the ship's preparations the line under it picks
// from while the step lasts (the desktop game has the same, in connect_screen.rs).
const STEPS = {
  game: [0, 0.45, ["Refuelling", "Loading the cargo bay", "Warming up the reactor", "Bolting on the wings",
    "Topping up the coolant", "Unpacking the hangar", "Stocking the galley"]],
  connect: [0.45, 0.6, ["Plotting a course", "Hailing the zone", "Aligning the antenna",
    "Calibrating the nav computer", "Locking onto the beacon"]],
  signin: [0.6, 0.7, ["Checking your pilot licence", "Clearing docking control", "Scanning your ID badge",
    "Confirming flight clearance"]],
  enter: [0.7, 0.78, ["Testing the hyperdrive", "Opening the jump gate", "Spooling up the warp core",
    "Charging the thrusters"]],
  download: [0.78, 1, ["Checking your engines", "Charting the arena", "Mapping the asteroid fields",
    "Downloading star charts", "Polishing the hull", "Arming the guns"]],
};
const ORDER = Object.keys(STEPS);
const PHRASE_EVERY = 2200; // ms
const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
let active = false, tipTimer = 0, tip = Math.floor(Math.random() * TIPS.length), lastPercent = 0;
let heard = false, fallback = 0, stage = "game", stageAt = 0, stagePercent = null, shown = 0, ending = 0;
let phrase = "", phraseAt = 0;

// ---- the warp --------------------------------------------------------------------------------
const canvas = $("loader-warp"), ctx = canvas?.getContext("2d");
const STARS = 520;
let stars = [], frame = 0, last = 0, w = 0, h = 0, dpr = 1;

function star(fresh) {
  // a point in a tunnel ahead of the ship: x, y across it, z how far ahead (1 = farthest)
  let x, y;
  do { x = Math.random() * 2 - 1; y = Math.random() * 2 - 1; } while (x * x + y * y < 0.004);
  return { x, y, z: fresh ? 1 : 0.05 + Math.random() * 0.95, hue: 195 + Math.random() * 40 };
}

function size() {
  dpr = Math.min(2, window.devicePixelRatio || 1);
  w = canvas.width = Math.round(innerWidth * dpr);
  h = canvas.height = Math.round(innerHeight * dpr);
}

// How fast the ship goes: cruising at first, faster as the way in gets on, all out at the end.
function speed() {
  if (reduced) return 0.06;
  const end = ending ? Math.min(1, (performance.now() - ending) / 450) : 0;
  return 0.12 + 0.9 * shown * shown + 3.2 * end;
}

function draw(now) {
  if (!active) { frame = 0; return; }
  frame = requestAnimationFrame(draw);
  const dt = Math.min(0.05, (now - (last || now)) / 1000);
  last = now;
  advance(dt);
  const v = speed();
  const cx = w / 2, cy = h / 2, r = Math.hypot(w, h) * 0.32;
  ctx.globalCompositeOperation = "source-over";
  ctx.fillStyle = "#02040a";
  ctx.fillRect(0, 0, w, h);
  // the glow ahead, brighter as the ship goes faster
  const glow = ctx.createRadialGradient(cx, cy, 0, cx, cy, r * (0.5 + v * 0.4));
  glow.addColorStop(0, `rgba(120, 200, 255, ${0.10 + 0.18 * Math.min(1, v)})`);
  glow.addColorStop(0.4, `rgba(42, 90, 255, ${0.05 + 0.06 * Math.min(1, v)})`);
  glow.addColorStop(1, "rgba(0, 0, 0, 0)");
  ctx.fillStyle = glow;
  ctx.fillRect(0, 0, w, h);
  ctx.globalCompositeOperation = "lighter";
  ctx.lineCap = "round";
  const trail = 0.012 + v * 0.07;
  for (const s of stars) {
    s.z -= v * dt;
    if (s.z <= 0.02) Object.assign(s, star(true));
    const hx = cx + (s.x / s.z) * r, hy = cy + (s.y / s.z) * r;
    const tz = Math.min(1.2, s.z + trail);
    const tx = cx + (s.x / tz) * r, ty = cy + (s.y / tz) * r;
    if (hx < -50 || hy < -50 || hx > w + 50 || hy > h + 50) { Object.assign(s, star(true)); continue; }
    const near = 1 - s.z;
    ctx.strokeStyle = `hsla(${s.hue}, 90%, ${70 + near * 25}%, ${Math.min(1, near * 1.5)})`;
    ctx.lineWidth = (0.4 + near * 2.2) * dpr;
    ctx.beginPath();
    ctx.moveTo(tx, ty);
    ctx.lineTo(hx, hy);
    ctx.stroke();
  }
}

// ---- where the way in has got to ---------------------------------------------------------------
// The line only moves forward, and never stands still: a step's downloads move it by their bytes, other
// steps ease it on toward the end of their share, and underneath both it creeps on by itself, slower the
// closer it gets to a little way into the next step's share. That creep is what carries it through the
// quiet stretches nothing reports on: the game compiling once its download is in, and starting up.
function advance(dt) {
  if (ending) {
    shown += (1 - shown) * Math.min(1, dt * 6);
  } else {
    const [lo, hi] = STEPS[stage] ?? STEPS.game;
    const next = STEPS[ORDER[ORDER.indexOf(stage) + 1]];
    const cap = next ? hi + (next[1] - hi) * 0.5 : 0.985;
    const since = (performance.now() - stageAt) / 1000;
    const target = stagePercent != null
      ? lo + (hi - lo) * stagePercent / 100
      : lo + (hi - lo) * 0.9 * (1 - Math.exp(-since / 2));
    const eased = shown + (target - shown) * Math.min(1, dt * 4);
    const crept = shown + (0.0015 + 0.06 * Math.max(0, cap - shown)) * dt;
    shown = Math.max(shown, Math.min(cap, Math.max(eased, crept)));
  }
  $("loader-bar").style.setProperty("--p", `${(shown * 100).toFixed(2)}%`);
  if (performance.now() - phraseAt > PHRASE_EVERY && $("loader-error").hidden) say();
}

// The next of the ship's preparations for this step, never the one just shown.
function say() {
  const pool = (STEPS[stage] ?? STEPS.game)[2];
  let next = phrase;
  while (pool.length > 1 && next === phrase) next = pool[Math.floor(Math.random() * pool.length)];
  phrase = next;
  phraseAt = performance.now();
  const el = $("loader-text");
  el.classList.add("fade");
  setTimeout(() => { el.textContent = phrase; el.classList.remove("fade"); }, 180);
}

function step(name, percent = null) {
  if (!STEPS[name]) return;
  stagePercent = percent;
  if (name !== stage) { stage = name; stageAt = performance.now(); say(); }
}

function showTip() {
  const el = $("loader-tip");
  el.classList.add("fade");
  setTimeout(() => { el.innerHTML = `Tip: ${TIPS[tip++ % TIPS.length]}`; el.classList.remove("fade"); }, 400);
}

function open() {
  if (active) return;
  active = true;
  heard = false;
  ending = 0;
  shown = 0;
  stage = "game";
  stageAt = performance.now();
  phrase = "";
  say();
  $("loader-arena").textContent = $("arena")?.value || "public";
  $("loader-error").hidden = true;
  $("loader-tip").hidden = false;
  loader.classList.remove("done");
  loader.hidden = false;
  document.body.classList.add("loading-game");
  showTip();
  tipTimer = setInterval(showTip, 7000);
  if (ctx) {
    size();
    stars = Array.from({ length: STARS }, () => star(false));
    last = 0;
    if (!frame) frame = requestAnimationFrame(draw);
  }
}

function close() {
  active = false;
  clearInterval(tipTimer);
  clearTimeout(fallback);
  fallback = 0;
  loader.hidden = true;
  loader.classList.remove("done");
  document.body.classList.remove("loading-game");
}

// Into the arena: all out for a moment, then a flash that opens onto the game.
function finish(flash = true) {
  if (!active || loader.classList.contains("done")) return;
  clearTimeout(fallback);
  if (!flash) { loader.classList.add("done"); setTimeout(close, 600); return; }
  ending = performance.now();
  setTimeout(() => loader.classList.add("done"), 380);
  setTimeout(close, 1000);
}

// The game's word on where it has got to: a step, the line it shows under its bar and how far through this
// step's downloads (-1 when unknown); "playing" once the arena is on screen; "prompt" when the zone asks the
// pilot something, which the game's own screen shows.
globalThis.dareLoadingStage = (name, detail, percent) => {
  if (!active) return;
  heard = true;
  if (name === "playing") return finish(true);
  if (name === "prompt") return finish(false);
  step(name, percent >= 0 ? percent : null);
};

function update() {
  if (!active) return;
  if (status.classList.contains("error") && status.textContent) {
    $("loader-error").hidden = false;
    $("loader-tip").hidden = true;
    $("loader-errortext").textContent = status.textContent;
    $("loader-text").textContent = "";
    return;
  }
  if (document.body.classList.contains("playing")) {
    if (heard) return; // the game says where it is now
    if (stage === "game") step("connect");
    // a game too old to say where it is: let it show itself after a while
    if (!fallback) fallback = setTimeout(() => { if (!heard) finish(false); }, 8000);
    return;
  }
  const m = /Loading (\d+)%/.exec(label.textContent);
  if (button.classList.contains("downloading") && m) {
    lastPercent = Number(m[1]);
    step("game", lastPercent);
  } else if (lastPercent > 0 && label.textContent === "Enter") {
    step("game", 100);
  }
  // Enter came back without the game starting: cancelled (e.g. closed the sign-in box)
  if (!button.disabled && !button.classList.contains("downloading") && label.textContent === "Enter") {
    setTimeout(() => { if (!button.disabled && !document.body.classList.contains("playing")) close(); }, 400);
  }
}

if (button && loader) {
  button.addEventListener("click", () => {
    // signing in first: the sign-in box is on top; the warp waits underneath
    lastPercent = 0;
    open();
    setTimeout(update, 50);
  });
  const watch = new MutationObserver(update);
  watch.observe(button, { attributes: true, attributeFilter: ["class", "disabled"] });
  watch.observe(label, { childList: true, characterData: true, subtree: true });
  watch.observe(status, { childList: true, attributes: true, characterData: true, subtree: true });
  watch.observe(document.body, { attributes: true, attributeFilter: ["class"] });
  $("loader-back").addEventListener("click", close);
  addEventListener("resize", () => { if (active && ctx) size(); });
}
