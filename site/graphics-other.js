// The in-game Graphics overlay's "Other graphics" tab and its "Use my own" switch.
//
// A pilot can wear their own picture for any graphic an arena could replace (bombs, bullets,
// items, explosions...): uploaded, or made by the same generators as the arena editor's. They are
// kept on the pilot's account (/account/api/graphics) and laid over the arena's own whenever the
// pilot flies. "Use my own" off shows the arena's ships and graphics instead, without forgetting
// the pilot's, so switching back is instant. Ships stay on the Ships tab (graphics.js), weapons on
// the Armory tab (graphics-armory.js).
// The game is reached through globalThis.dareGraphics (play/start.mjs).
const API = "/account/api/graphics";
const JSON_HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const GEN_LABELS = { recolor: "Recolour", new: "New look", prompt: "From a description…" };

const $ = (id) => document.getElementById(id);
const dialog = $("graphics");
const statusLine = $("gfx-o-status");
const sheetView = $("gfx-o-sheet");
const reviewBox = $("gfx-o-review");
const reviewSheet = $("gfx-o-review-sheet");
const promptForm = $("gfx-o-prompt");
const useOwnBox = $("gfx-useown");

let list = null; // { useOwn, slots: [{ name, label, group, hint, generate, own }] } when signed in
let selected = "bombs";
let pending = null; // { bytes, again } awaiting Approve
let busy = false;

// ---- the server ------------------------------------------------------------------------------
async function call(path, body) {
  const response = await fetch(API + path, {
    method: body ? "POST" : "GET",
    credentials: "same-origin",
    cache: "no-store",
    headers: body ? JSON_HEADERS : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json().catch(() => ({}));
  if (response.status === 401) { list = null; throw new Error("Sign in to use your own graphics."); }
  if (!response.ok || data.ok === false) throw new Error(data.message || "That didn't work. Try again.");
  return data;
}

async function load() {
  try { list = await call(""); } catch { list = null; }
  return list;
}

async function imageBytes(slot, stock = false) {
  const response = await fetch(`${API}/image?slot=${encodeURIComponent(slot)}${stock ? "&stock=1" : ""}`, { credentials: "same-origin", cache: "no-store" });
  if (!response.ok) return null;
  return new Uint8Array(await response.arrayBuffer());
}

/** Puts the pilot's own graphics and their switch into the game, as it starts. */
async function applyAll() {
  const game = globalThis.dareGraphics;
  if (!game || !(await load())) return;
  game.useOwn(list.useOwn);
  useOwnBox.checked = list.useOwn;
  await Promise.all(list.slots.filter((s) => s.own).map(async (s) => {
    try {
      const bytes = await imageBytes(s.name);
      if (bytes) game.set(s.name, bytes);
    } catch (error) { console.warn(`${s.name}:`, error); }
  }));
}
globalThis.addEventListener("dare-ships-ready", () => void applyAll());

// ---- drawing ---------------------------------------------------------------------------------
/** Draws image bytes on `canvas`, black keyed out as the game does, scaled up for small sheets. */
async function draw(canvas, bytes) {
  const g = canvas.getContext("2d");
  if (!bytes) { canvas.width = 160; canvas.height = 16; g.clearRect(0, 0, 160, 16); return; }
  const bitmap = await createImageBitmap(new Blob([bytes]));
  const scale = Math.max(1, Math.min(4, Math.floor(480 / bitmap.width), Math.floor(300 / bitmap.height)));
  canvas.width = bitmap.width * scale;
  canvas.height = bitmap.height * scale;
  g.imageSmoothingEnabled = false;
  g.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  const data = g.getImageData(0, 0, canvas.width, canvas.height);
  const px = data.data;
  for (let i = 0; i < px.length; i += 4) if (!px[i] && !px[i + 1] && !px[i + 2]) px[i + 3] = 0;
  g.putImageData(data, 0, 0);
}

function say(text, bad = false) {
  statusLine.textContent = text;
  statusLine.className = bad ? "gfxstatus bad" : "gfxstatus";
}

function setBusy(on, text = "") {
  busy = on;
  for (const b of $("gfx-other").querySelectorAll("button, textarea, input")) b.disabled = on;
  if (text) say(text);
  dialog.classList.toggle("busy", on);
}

const slotOf = (name) => list?.slots.find((s) => s.name === name);

// ---- the tab ---------------------------------------------------------------------------------
function renderSlots() {
  const box = $("gfx-o-slots");
  $("gfx-o-signin").hidden = !!list;
  box.replaceChildren();
  let group = "";
  for (const s of list?.slots ?? []) {
    if (s.group !== group) {
      group = s.group;
      const h = document.createElement("h4");
      h.textContent = group;
      box.append(h);
    }
    const b = document.createElement("button");
    b.type = "button";
    b.setAttribute("aria-pressed", String(s.name === selected));
    b.textContent = s.label;
    if (s.own) {
      const mine = document.createElement("span");
      mine.className = "mine";
      mine.textContent = "yours";
      b.append(mine);
    }
    b.addEventListener("click", () => { if (busy) return; selected = s.name; showReview(null); renderSlots(); void showCurrent(); });
    box.append(b);
  }
}

async function showCurrent() {
  const s = slotOf(selected);
  const gens = $("gfx-o-gens");
  gens.replaceChildren();
  if (!s) {
    $("gfx-o-label").textContent = "Sign in to make and keep your own graphics.";
    $("gfx-o-hint").textContent = "";
    $("gfx-o-clear").disabled = true;
    await draw(sheetView, null);
    return;
  }
  $("gfx-o-label").textContent = s.own
    ? `${s.label}: yours${list.useOwn ? "" : " (hidden: Use my own is off)"}`
    : `${s.label}: the arena's (the game's own is shown here)`;
  $("gfx-o-hint").textContent = s.hint;
  $("gfx-o-clear").disabled = !s.own;
  for (const mode of s.generate) {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = GEN_LABELS[mode] ?? mode;
    b.addEventListener("click", () => {
      if (mode === "prompt") { promptForm.hidden = false; $("gfx-o-prompt-text").focus(); }
      else void generate({ slot: s.name, mode });
    });
    gens.append(b);
  }
  await draw(sheetView, s.own ? await imageBytes(s.name) : await imageBytes(s.name, true));
}

async function refresh() {
  await load();
  if (list) useOwnBox.checked = list.useOwn;
  renderSlots();
  await showCurrent();
}

function showReview(next) {
  pending = next;
  reviewBox.hidden = !next;
  $("gfx-o-actions").hidden = !!next;
  promptForm.hidden = true;
  if (!next) return;
  void draw(reviewSheet, next.bytes);
  $("gfx-o-review-note").textContent = `${slotOf(selected)?.label ?? selected}: ${next.note}`;
  $("gfx-o-again").hidden = !next.again;
  $("gfx-o-approve").focus();
}

async function generate(request) {
  if (busy || !list) return;
  setBusy(true, request.mode === "prompt" ? "Painting it… this takes up to a minute." : "Making it…");
  try {
    const data = await call("/generate", request);
    const bytes = Uint8Array.from(atob(data.sheet), (ch) => ch.charCodeAt(0));
    say("Here it is. Approve it to wear it.");
    showReview({ bytes, note: GEN_LABELS[request.mode] ?? request.mode, again: () => generate(request) });
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function upload(file) {
  if (!file || busy) return;
  if (!list) { say("Sign in to use your own graphics.", true); return; }
  const bytes = new Uint8Array(await file.arrayBuffer());
  try {
    await draw(reviewSheet, bytes);
  } catch {
    say("That file isn't an image the browser can read.", true);
    return;
  }
  say("Check it, then approve it to wear it. Keep the stock sheet's size and frame layout.");
  showReview({ bytes, note: file.name, again: null });
}

async function approve() {
  if (!pending || busy) return;
  setBusy(true, "Saving it…");
  try {
    const response = await fetch(`${API}/set?slot=${encodeURIComponent(selected)}`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/octet-stream", "X-Requested-With": "dare-arena" },
      body: pending.bytes,
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok || data.ok === false) throw new Error(data.message || "Couldn't save it.");
    globalThis.dareGraphics?.set(selected, pending.bytes);
    showReview(null);
    say(data.message || "Saved.");
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

async function clearOwn() {
  if (busy) return;
  setBusy(true, "Back to the arena's…");
  try {
    const data = await call("/clear", { slot: selected });
    globalThis.dareGraphics?.clear(selected);
    say(data.message);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

async function setUseOwn(on) {
  globalThis.dareGraphics?.useOwn(on); // at once, signed in or not
  if (!list) return;
  list.useOwn = on;
  try {
    const data = await call("/use", { own: on });
    say(data.message);
  } catch (error) {
    say(error.message, true);
  }
  await showCurrent();
}

/** Shows one of the overlay's panels (Ships, Armory, Other graphics) and tells the others. */
function showTab(id) {
  for (const tab of document.querySelectorAll("#graphics .gfxtabs .tab")) {
    const on = tab.dataset.panel === id;
    tab.setAttribute("aria-current", on ? "page" : "false");
    $(tab.dataset.panel).hidden = !on;
  }
  if (id === "gfx-other") void refresh();
  dialog.dispatchEvent(new CustomEvent("gfx-tab", { detail: id }));
}

// ---- wiring ----------------------------------------------------------------------------------
for (const tab of document.querySelectorAll("#graphics .gfxtabs .tab")) tab.addEventListener("click", () => showTab(tab.dataset.panel));
useOwnBox.addEventListener("change", () => void setUseOwn(useOwnBox.checked));
$("gfx-o-clear").addEventListener("click", () => void clearOwn());
$("gfx-o-upload").addEventListener("click", () => { if (list) $("gfx-o-file").click(); else say("Sign in to use your own graphics.", true); });
$("gfx-o-file").addEventListener("change", () => { void upload($("gfx-o-file").files?.[0]); $("gfx-o-file").value = ""; });
promptForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = $("gfx-o-prompt-text").value.trim();
  if (!prompt) { say("Describe what you want.", true); return; }
  void generate({ slot: selected, mode: "prompt", prompt });
});
$("gfx-o-prompt-cancel").addEventListener("click", () => { promptForm.hidden = true; });
$("gfx-o-approve").addEventListener("click", () => void approve());
$("gfx-o-again").addEventListener("click", () => { const again = pending?.again; showReview(null); void again?.(); });
$("gfx-o-decline").addEventListener("click", () => { showReview(null); say("Declined. Nothing was saved."); });
$("gfx-o-signin").addEventListener("click", async () => { await globalThis.foundationSignIn?.(); await refresh(); });
dialog.addEventListener("close", () => showReview(null));
// Opening the overlay refreshes the switch from the account.
new MutationObserver(() => { if (dialog.open) void load().then(() => { if (list) useOwnBox.checked = list.useOwn; }); })
  .observe(dialog, { attributes: true, attributeFilter: ["open"] });
