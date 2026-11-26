// The in-game Graphics overlay (in the game: Esc then G, or F8). Ships: each of the eight ships
// can wear a sheet of the pilot's own, from an upload or the ship generator, kept in their
// shipyard on their account and put back on whenever they fly.
//
// A sheet is the layout the game reads ship1..ship8 in: 40 square frames in 10 columns and 4 rows
// (360x144 for stock 36-pixel ships). Frame 0, top left, is the ship nose-up; each next frame, in
// row order, is turned 9 degrees further clockwise. "Turn 180°" moves frame 20 to frame 0 for art
// that faces backward. The game itself is reached through globalThis.dareShips (play/start.mjs).
const SHIPS = ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];
const FRAMES = 40, COLS = 10, ROWS = 4;
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
const API = "/account/api/shipyard";

const $ = (id) => document.getElementById(id);
const dialog = $("graphics");
const shipList = $("gfx-shiplist");
const current = $("gfx-current");
const currentSheet = $("gfx-current-sheet");
const currentLabel = $("gfx-current-label");
const actions = $("gfx-actions");
const genChoice = $("gfx-genchoice");
const promptForm = $("gfx-prompt");
const review = $("gfx-review");
const reviewSpin = $("gfx-review-spin");
const reviewSheet = $("gfx-review-sheet");
const reviewNote = $("gfx-review-note");
const applyTo = $("gfx-applyto");
const statusLine = $("gfx-status");
const yardBox = $("gfx-yard");
const yardList = $("gfx-yardlist");
const yardNote = $("gfx-yardnote");
const fileInput = $("gfx-file");

let selected = 1; // the ship (1..8) being looked at
let yard = null; // { ships, slots, max } when signed in, else null
let pending = null; // { sheet (canvas), name, source, again, flipped } awaiting Approve
let busy = false;

// ---- sheets: canvases holding a 10x4 sheet -------------------------------------------------
const isSheet = (w, h) => w >= 80 && w % COLS === 0 && h === (w / COLS) * ROWS;

function canvasOf(w, h) {
  const c = document.createElement("canvas");
  c.width = w;
  c.height = h;
  return c;
}

function sheetFromRgba({ width, height, rgba }) {
  const c = canvasOf(width, height);
  c.getContext("2d").putImageData(new ImageData(new Uint8ClampedArray(rgba), width, height), 0, 0);
  return c;
}

/** Decodes an image file; a sheet with no transparency has its black keyed out, as the game does. */
async function sheetFromBlob(blob) {
  const bitmap = await createImageBitmap(blob);
  if (!isSheet(bitmap.width, bitmap.height)) {
    const size = `${bitmap.width}×${bitmap.height}`;
    bitmap.close();
    throw new Error(`A ship sheet is 40 square frames in 10 columns and 4 rows (360×144 for stock ships). That one is ${size}.`);
  }
  const c = canvasOf(bitmap.width, bitmap.height);
  const g = c.getContext("2d");
  g.drawImage(bitmap, 0, 0);
  bitmap.close();
  const data = g.getImageData(0, 0, c.width, c.height);
  const px = data.data;
  let opaque = true;
  for (let i = 3; i < px.length; i += 4) if (px[i] !== 255) { opaque = false; break; }
  if (opaque) {
    for (let i = 0; i < px.length; i += 4) if (!px[i] && !px[i + 1] && !px[i + 2]) px[i + 3] = 0;
    g.putImageData(data, 0, 0);
  }
  return c;
}

const sheetFromBase64 = (b64) => sheetFromBlob(new Blob([Uint8Array.from(atob(b64), (ch) => ch.charCodeAt(0))], { type: "image/png" }));

/** The same ship turned half a turn: frame 20 becomes frame 0, still clockwise. */
function turned(sheet) {
  const side = sheet.width / COLS;
  const c = canvasOf(sheet.width, sheet.height);
  const g = c.getContext("2d");
  for (let i = 0; i < FRAMES; i++) {
    const from = (i + FRAMES / 2) % FRAMES;
    g.drawImage(sheet, (from % COLS) * side, Math.floor(from / COLS) * side, side, side, (i % COLS) * side, Math.floor(i / COLS) * side, side, side);
  }
  return c;
}

async function pngBytes(sheet) {
  const blob = await new Promise((resolve) => sheet.toBlob(resolve, "image/png"));
  return new Uint8Array(await blob.arrayBuffer());
}

function base64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

// ---- drawing: spinning previews and whole sheets -------------------------------------------
const spinners = new Map(); // display canvas -> sheet canvas
let spinTimer = 0;

function spin(display, sheet) {
  if (sheet) spinners.set(display, sheet); else { spinners.delete(display); display.getContext("2d").clearRect(0, 0, display.width, display.height); }
  if (!spinTimer && spinners.size) {
    let frame = 0;
    spinTimer = setInterval(() => {
      frame = (frame + 1) % FRAMES;
      for (const [d, s] of spinners) drawFrame(d, s, frame);
    }, 65); // the generator's own spin preview speed
  }
  if (sheet) drawFrame(display, sheet, 0);
}

function drawFrame(display, sheet, frame) {
  const side = sheet.width / COLS;
  const g = display.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, display.width, display.height);
  g.drawImage(sheet, (frame % COLS) * side, Math.floor(frame / COLS) * side, side, side, 0, 0, display.width, display.height);
}

function showSheet(display, sheet) {
  display.width = sheet ? sheet.width : 360;
  display.height = sheet ? sheet.height : 144;
  const g = display.getContext("2d");
  g.clearRect(0, 0, display.width, display.height);
  if (sheet) g.drawImage(sheet, 0, 0);
}

function stopSpinning() {
  clearInterval(spinTimer);
  spinTimer = 0;
  spinners.clear();
}

// ---- the server's shipyard -------------------------------------------------------------------
async function call(path, body) {
  const response = await fetch(API + path, {
    method: body ? "POST" : "GET",
    credentials: "same-origin",
    cache: "no-store",
    headers: body ? HEADERS : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json().catch(() => ({}));
  if (response.status === 401) { yard = null; throw new Error("Sign in to keep a shipyard."); }
  if (!response.ok || data.ok === false) throw new Error(data.message || "That didn't work. Try again.");
  return data;
}

async function loadYard() {
  try {
    yard = await call("");
  } catch {
    yard = null;
  }
  return yard;
}

async function sheetBytes(id) {
  const response = await fetch(`${API}/sheet?id=${encodeURIComponent(id)}`, { credentials: "same-origin" });
  if (!response.ok) throw new Error("Couldn't load that ship.");
  return new Uint8Array(await response.arrayBuffer());
}

/** Puts the pilot's saved ships on, as the game starts (and again whenever asked). */
async function applyYard() {
  const ships = globalThis.dareShips;
  if (!ships || !(await loadYard())) return;
  await Promise.all(yard.slots.map(async (id, i) => {
    if (!id) return;
    try { ships.set(i + 1, await sheetBytes(id)); } catch (error) { console.warn(`ship ${i + 1}:`, error); }
  }));
}
globalThis.addEventListener("dare-ships-ready", () => void applyYard());

// ---- the overlay -----------------------------------------------------------------------------
function say(text, bad = false) {
  statusLine.textContent = text;
  statusLine.className = bad ? "bad" : "";
}

function setBusy(on, text = "") {
  busy = on;
  for (const b of dialog.querySelectorAll("button, textarea, input, select")) if (b.dataset.always === undefined) b.disabled = on;
  if (text) say(text);
  dialog.classList.toggle("busy", on);
}

const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));

/** The ship being looked at, as the game draws it now. */
async function showCurrent() {
  for (const b of shipList.children) b.setAttribute("aria-pressed", String(Number(b.dataset.ship) === selected));
  applyTo.value = String(selected);
  const id = yard?.slots?.[selected - 1] ?? 0;
  const own = id ? yard.ships.find((s) => s.id === id) : null;
  currentLabel.textContent = own ? `${SHIPS[selected - 1]}: ${own.name} (from your shipyard)` : `${SHIPS[selected - 1]}: the game's own ship`;
  $("gfx-restore").disabled = !own;
  const shown = globalThis.dareShips?.sheet(selected);
  const sheet = shown ? sheetFromRgba(shown) : null;
  spin(current, sheet);
  showSheet(currentSheet, sheet);
  if (!sheet) currentLabel.textContent += " (it appears once the game has drawn)";
  yardNote.textContent = yard ? `Click a ship to put it on ship ${selected} (${SHIPS[selected - 1]}).` : "";
}

function renderYard() {
  yardBox.classList.toggle("signedout", !yard);
  $("gfx-signin").hidden = !!yard;
  $("gfx-yardcount").textContent = yard ? `${yard.ships.length} / ${yard.max}` : "";
  yardList.replaceChildren(...(yard?.ships ?? []).map((ship) => {
    const item = document.createElement("li");
    const pick = document.createElement("button");
    pick.type = "button";
    pick.className = "thumb";
    pick.title = `${ship.name}: put it on ship ${selected}`;
    const art = document.createElement("span");
    art.className = "art";
    art.style.backgroundImage = `url("${API}/sheet?id=${ship.id}")`;
    const on = yard.slots.map((id, i) => (id === ship.id ? i + 1 : 0)).filter(Boolean);
    const label = document.createElement("span");
    label.className = "name";
    label.textContent = ship.name;
    pick.append(art, label);
    if (on.length) {
      const badge = document.createElement("span");
      badge.className = "on";
      badge.textContent = on.join(" ");
      badge.title = `On ship ${on.join(", ")}`;
      pick.append(badge);
    }
    pick.addEventListener("click", () => void useShip(ship, selected));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "remove";
    remove.setAttribute("aria-label", `Remove ${ship.name}`);
    remove.textContent = "✕";
    remove.addEventListener("click", () => void removeShip(ship));
    item.append(pick, remove);
    return item;
  }));
  if (yard && !yard.ships.length) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = "Ships you approve land here.";
    yardList.append(empty);
  }
}

async function refresh() {
  await loadYard();
  renderYard();
  await showCurrent();
}

/** Puts a saved ship on ship `slot` (1..8): on the account, then in the game. */
async function useShip(ship, slot) {
  if (busy) return;
  setBusy(true, `Putting ${ship.name} on ship ${slot}…`);
  try {
    await call("/slot", { slot, id: ship.id });
    globalThis.dareShips?.set(slot, await sheetBytes(ship.id));
    selected = slot;
    await nextFrame();
    say(`${ship.name} is on ship ${slot} (${SHIPS[slot - 1]}).`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

async function removeShip(ship) {
  if (busy || !confirm(`Remove ${ship.name} from your shipyard?`)) return;
  setBusy(true, `Removing ${ship.name}…`);
  try {
    const slots = yard.slots.map((id, i) => (id === ship.id ? i + 1 : 0)).filter(Boolean);
    await call("/remove", { id: ship.id });
    for (const slot of slots) globalThis.dareShips?.clear(slot);
    await nextFrame();
    say(`Removed ${ship.name}.`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

async function restoreDefault() {
  if (busy) return;
  setBusy(true, "Back to the default…");
  try {
    if (yard) await call("/slot", { slot: selected, id: 0 });
    globalThis.dareShips?.clear(selected);
    await nextFrame();
    say(`Ship ${selected} is back to the default.`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

// ---- new ships: upload or generate, then approve -------------------------------------------
function needYard() {
  if (yard) return true;
  say("Sign in to make and keep custom ships.", true);
  return false;
}

function showReview(next) {
  pending = next;
  review.hidden = !next;
  actions.hidden = !!next;
  genChoice.hidden = true;
  promptForm.hidden = true;
  if (!next) { spin(reviewSpin, null); return; }
  spin(reviewSpin, next.sheet);
  showSheet(reviewSheet, next.sheet);
  reviewNote.textContent = `${next.name}${next.flipped ? " · turned 180°" : ""}. The top-left frame should point its nose up; if it faces backward, turn it.`;
  $("gfx-again").hidden = !next.again;
  $("gfx-approve").focus();
}

async function generate(request, name) {
  if (busy || !needYard()) return;
  setBusy(true, request.batch ? "Next option…"
    : request.mode === "prompt" ? "Drawing your ship… this takes up to a minute."
    : "Drawing a ship… (when none are waiting, this takes up to a minute)");
  try {
    const data = await call("/generate", request);
    const sheet = await sheetFromBase64(data.sheet);
    // A prompt is drawn as a batch of options; Try again shows the next one (instant, free),
    // and draws a fresh batch once they're used up.
    const more = Number.isInteger(data.left) && data.left > 0;
    say(more ? `Approve it to add it to your shipyard, or Try again: ${data.left} more option${data.left === 1 ? "" : "s"} ready.`
      : "Here it is. Approve it to add it to your shipyard.");
    const fresh = request.batch ? request.fresh : request;
    showReview({
      sheet,
      name,
      source: request.mode === "prompt" ? "generated" : "random",
      again: () => generate(more ? { mode: "prompt", batch: data.batch, fresh } : { ...fresh, variation: true }, name),
    });
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function upload(file) {
  if (!file || busy || !needYard()) return;
  try {
    const sheet = await sheetFromBlob(file);
    say("Check it, then approve it to add it to your shipyard.");
    showReview({ sheet, name: file.name.replace(/\.[^.]+$/, "").slice(0, 40) || "Uploaded ship", source: "uploaded", again: null });
  } catch (error) {
    say(error.message || "That file isn't an image the browser can read.", true);
  }
}

async function approve() {
  if (!pending || busy) return;
  const slot = Number(applyTo.value) || selected;
  setBusy(true, "Adding it to your shipyard…");
  try {
    const bytes = await pngBytes(pending.sheet);
    const added = await call("/add", { name: pending.name, source: pending.source, sheet: base64(bytes) });
    await call("/slot", { slot, id: added.id });
    globalThis.dareShips?.set(slot, bytes);
    selected = slot;
    showReview(null);
    await nextFrame();
    say(`Added to your shipyard and put on ship ${slot} (${SHIPS[slot - 1]}).`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

// ---- wiring ----------------------------------------------------------------------------------
shipList.replaceChildren(...SHIPS.map((name, i) => {
  const b = document.createElement("button");
  b.type = "button";
  b.dataset.ship = String(i + 1);
  b.innerHTML = `<b>${i + 1}</b> ${name}`;
  b.addEventListener("click", () => { selected = i + 1; void showCurrent(); renderYard(); });
  return b;
}));
applyTo.replaceChildren(...SHIPS.map((name, i) => new Option(`${i + 1} · ${name}`, String(i + 1))));

$("gfx-restore").addEventListener("click", () => void restoreDefault());
$("gfx-upload").addEventListener("click", () => { if (needYard()) fileInput.click(); });
fileInput.addEventListener("change", () => { void upload(fileInput.files?.[0]); fileInput.value = ""; });
$("gfx-generate").addEventListener("click", () => {
  if (!needYard()) return;
  genChoice.hidden = !genChoice.hidden;
  promptForm.hidden = true;
});
$("gfx-random").addEventListener("click", () => void generate({ mode: "random" }, "Random ship"));
$("gfx-prompted").addEventListener("click", () => {
  genChoice.hidden = true;
  promptForm.hidden = false;
  $("gfx-prompt-text").focus();
});
promptForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = $("gfx-prompt-text").value.trim();
  if (!prompt) { say("Describe the ship you want.", true); return; }
  const creative = $("gfx-creative").checked;
  void generate({ mode: "prompt", prompt, creative }, prompt.slice(0, 40));
});
$("gfx-prompt-cancel").addEventListener("click", () => { promptForm.hidden = true; });
$("gfx-flip").addEventListener("click", () => {
  if (pending) showReview({ ...pending, sheet: turned(pending.sheet), flipped: !pending.flipped });
});
$("gfx-approve").addEventListener("click", () => void approve());
$("gfx-again").addEventListener("click", () => { const again = pending?.again; showReview(null); void again?.(); });
$("gfx-decline").addEventListener("click", () => { showReview(null); say("Declined. Nothing was saved."); });
$("gfx-signin").addEventListener("click", async () => {
  await globalThis.foundationSignIn?.();
  await refresh();
});
$("gfx-close").addEventListener("click", () => dialog.close());
dialog.addEventListener("cancel", (event) => { if (busy) event.preventDefault(); });
dialog.addEventListener("close", () => {
  stopSpinning();
  showReview(null);
  document.querySelector("body > canvas")?.focus(); // back to flying
});

/** Called by the game (Esc then G, or F8). */
globalThis.foundationOpenGraphics = () => {
  if (!dialog.open) {
    say("");
    showReview(null);
    dialog.showModal();
    $("gfx-close").focus();
    void refresh();
  }
  return true;
};
