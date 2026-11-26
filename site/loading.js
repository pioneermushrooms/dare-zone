// The home page's loading screen: after Enter, a panel with where the game is (download, start-up,
// sign-in, joining the arena), a progress bar and a few tips, until the game takes over the page.
// It only watches what /play/start.mjs already shows (the Enter button's "Loading N%", the status
// line, the page switching to the game), so it needs nothing from the game's own code.
const $ = (id) => document.getElementById(id);
const button = $("start"), label = $("start-label"), status = $("status"), loader = $("loader");
const TIPS = [
  "In the game, Esc opens the menu; Esc then G (or F8) opens Graphics to give your ships your own look.",
  "Type ?go <arena> in chat to fly to another arena.",
  "Arena owners can draw maps, set rules, add graphics and bots in the Arena Editor.",
  "Your keys and macros live on your Pilot account and come with you to any computer.",
  "No pilot name? Fly as a guest: you'll get a quirky space name, new every time.",
];
const STEPS = ["download", "start", "join"];
let active = false, tipTimer = 0, tip = Math.floor(Math.random() * TIPS.length), lastPercent = 0;

function step(name, text) {
  for (const li of loader.querySelectorAll("[data-step]")) {
    const i = STEPS.indexOf(li.dataset.step), at = STEPS.indexOf(name);
    li.className = i < at ? "done" : i === at ? "now" : "";
  }
  $("loader-text").textContent = text;
}

function bar(percent) {
  $("loader-bar").style.setProperty("--p", percent + "%");
  $("loader-bar").classList.toggle("unknown", percent == null);
  $("loader-pct").textContent = percent == null ? "" : percent + "%";
}

function showTip() {
  $("loader-tip").textContent = TIPS[tip % TIPS.length];
  tip++;
}

function open() {
  if (active) return;
  active = true;
  const arena = $("arena")?.selectedOptions[0]?.textContent?.trim() || "public";
  $("loader-arena").textContent = arena;
  $("loader-error").hidden = true;
  loader.hidden = false;
  document.body.classList.add("loading-game");
  showTip();
  tipTimer = setInterval(showTip, 6000);
}

function close() {
  active = false;
  clearInterval(tipTimer);
  loader.hidden = true;
  document.body.classList.remove("loading-game");
}

function update() {
  if (!active) return;
  if (document.body.classList.contains("playing")) { close(); return; }
  if (status.classList.contains("error") && status.textContent) {
    $("loader-error").hidden = false;
    $("loader-errortext").textContent = status.textContent;
    return;
  }
  const m = /Loading (\d+)%/.exec(label.textContent);
  if (button.classList.contains("downloading") && m) {
    lastPercent = Number(m[1]);
    step("download", "Downloading the game");
    bar(lastPercent);
  } else if (lastPercent > 0 || label.textContent === "Enter") {
    step(lastPercent >= 99 || label.textContent === "Enter" ? "join" : "start",
      lastPercent >= 99 || label.textContent === "Enter" ? "Signing you in and joining the arena" : "Starting the game");
    bar(null);
  }
  // Enter came back without the game starting: cancelled (e.g. closed the sign-in box)
  if (!button.disabled && !button.classList.contains("downloading") && label.textContent === "Enter" && !document.body.classList.contains("playing")) {
    setTimeout(() => { if (!button.disabled && !document.body.classList.contains("playing")) close(); }, 400);
  }
}

if (button && loader) {
  button.addEventListener("click", () => {
    // signing in first: the sign-in box is on top; the screen waits underneath
    lastPercent = 0;
    open();
    step("download", "Getting ready");
    bar(null);
    setTimeout(update, 50);
  });
  const watch = new MutationObserver(update);
  watch.observe(button, { attributes: true, attributeFilter: ["class", "disabled"] });
  watch.observe(label, { childList: true, characterData: true, subtree: true });
  watch.observe(status, { childList: true, attributes: true, characterData: true, subtree: true });
  watch.observe(document.body, { attributes: true, attributeFilter: ["class"] });
  $("loader-back").addEventListener("click", close);
}
