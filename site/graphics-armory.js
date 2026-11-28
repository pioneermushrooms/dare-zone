// The in-game Graphics overlay's Armory tab (in the game: Esc then G, or F8).
//
// Like the shipyard, but for weapons: bullets, bombs, mines, shrapnel, repel and rocket sheets
// the pilot uploaded or generated (Random: a fresh look; Prompt: drawn from a description), kept
// in their armory on their account (/account/api/armory) and worn whenever they fly. A weapon
// wears one for every ship, and bullets, bombs, mines and shrapnel can also wear one just for
// shots fired from one ship (over the every-ship one). A sheet keeps the game's own size and
// layout (rows are weapon levels, EMP, bouncing...), since the game cuts frames at fixed cells.
// The game is reached through globalThis.dareGraphics (play/start.mjs): `bombs` is the sheet for
// every ship, `bombs@3` the one for ship 3's shots.
// The Armory also holds banners (12×8, the game palette): not a weapon the browser game draws, but
// the picture beside the pilot's name. The one on "every ship" is their banner, which the zone
// shows (the server keeps it as the game's own 8-bit BMP and sets it as they enter an arena).
const SHIPS = ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];
const API = "/account/api/armory";
const HEADERS = { "Content-Type": "application/json", "X-Requested-With": "dare-arena" };
// Frame cells and how a preview plays: a row of a sprite sheet, or a burst's frames in order.
const CELLS = {
  bullets: { w: 5, h: 5, row: 4 },
  bombs: { w: 16, h: 16, row: 10 },
  mines: { w: 16, h: 16, row: 10 },
  shrapnel: { w: 8, h: 8, row: 10 },
  repel: { w: 96, h: 96 },
  rocket: { w: 24, h: 24 },
  banner: { w: 12, h: 8, row: 1 },
};
const BANNER = "banner";
const isBanner = (name) => name === BANNER;

const $ = (id) => document.getElementById(id);
const dialog = $("graphics");
const panel = $("gfx-armory");
const kindList = $("gfx-a-kinds");
const shipList = $("gfx-a-ships");
const current = $("gfx-a-current");
const currentSheet = $("gfx-a-sheet");
const actions = $("gfx-a-actions");
const genChoice = $("gfx-a-genchoice");
const promptForm = $("gfx-a-prompt");
const review = $("gfx-a-review");
const reviewSpin = $("gfx-a-review-spin");
const reviewSheet = $("gfx-a-review-sheet");
const applyTo = $("gfx-a-applyto");
const statusLine = $("gfx-a-status");
const list = $("gfx-a-list");
const fileInput = $("gfx-a-file");

let armory = null; // { kinds, items, slots: { kind: [every, ship1..ship8] }, max } when signed in
let kinds = []; // the weapons, signed in or not
let kind = "bombs";
let ship = 0; // 0 = every ship, else 1..8
let pending = null; // { sheet (canvas), name, source, again } awaiting Approve
let busy = false;
let shown = null; // { bytes, stem } of the sheet shown as current, for Download

const kindOf = (name) => kinds.find((k) => k.name === name);
const gameName = (k, s) => (s ? `${k}@${s}` : k);
const shipLabel = (s) => (s ? `ship ${s} (${SHIPS[s - 1]})` : "every ship");
/** What putting an item on the selected weapon does, in words. */
const putOn = (name, s) => (isBanner(name) ? "wear it as your banner" : `put it on ${shipLabel(s)}`);

// ---- the server --------------------------------------------------------------------------------
async function call(path, body) {
  const response = await fetch(API + path, {
    method: body ? "POST" : "GET",
    credentials: "same-origin",
    cache: "no-store",
    headers: body ? HEADERS : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json().catch(() => ({}));
  if (response.status === 401) { armory = null; throw new Error("Sign in to keep an armory."); }
  if (!response.ok || data.ok === false) throw new Error(data.message || "That didn't work. Try again.");
  return data;
}

async function load() {
  try {
    armory = await call("");
    kinds = armory.kinds;
  } catch {
    armory = null;
  }
  return armory;
}

async function itemBytes(id) {
  const response = await fetch(`${API}/sheet?id=${encodeURIComponent(id)}`, { credentials: "same-origin" });
  if (!response.ok) throw new Error("Couldn't load that sheet.");
  return new Uint8Array(await response.arrayBuffer());
}

async function stockBytes(name) {
  const response = await fetch(`/account/api/graphics/image?slot=${encodeURIComponent(name)}&stock=1`);
  return response.ok ? new Uint8Array(await response.arrayBuffer()) : null;
}

/** Puts the pilot's armory on, as the game starts (and again whenever asked). */
async function applyArmory() {
  const game = globalThis.dareGraphics;
  if (!game || !(await load())) return;
  const jobs = [];
  for (const [name, row] of Object.entries(armory.slots))
    if (!isBanner(name)) row.forEach((id, s) => {
      if (id) jobs.push(itemBytes(id).then((bytes) => game.set(gameName(name, s), bytes))
        .catch((error) => console.warn(`${gameName(name, s)}:`, error)));
    });
  await Promise.all(jobs);
}
globalThis.addEventListener("dare-ships-ready", () => void applyArmory());

// ---- sheets ------------------------------------------------------------------------------------
function canvasOf(w, h) {
  const c = document.createElement("canvas");
  c.width = w;
  c.height = h;
  return c;
}

/** Decodes an image; one with no transparency has its black keyed out, as the game does. */
async function sheetFromBlob(blob, need) {
  const bitmap = await createImageBitmap(blob);
  if (need && (bitmap.width !== need.width || bitmap.height !== need.height)) {
    const size = `${bitmap.width}×${bitmap.height}`;
    bitmap.close();
    throw new Error(`A ${need.label.toLowerCase()} sheet is ${need.width}×${need.height}, laid out like the game's own (shown above). That one is ${size}.`);
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

const sheetFromBytes = (bytes, need) => sheetFromBlob(new Blob([bytes]), need);

async function pngBytes(sheet) {
  const blob = await new Promise((resolve) => sheet.toBlob(resolve, "image/png"));
  return new Uint8Array(await blob.arrayBuffer());
}

/** A banner as a 24-bit BMP (clear pixels black): the server turns it into the game's 8-bit file. */
function bannerBmp(sheet) {
  const g = sheet.getContext("2d");
  const px = g.getImageData(0, 0, 12, 8).data;
  const out = new Uint8Array(54 + 36 * 8);
  const v = new DataView(out.buffer);
  out[0] = 0x42; out[1] = 0x4d;
  v.setUint32(2, out.length, true); v.setUint32(10, 54, true); v.setUint32(14, 40, true);
  v.setInt32(18, 12, true); v.setInt32(22, 8, true); v.setUint16(26, 1, true); v.setUint16(28, 24, true);
  for (let y = 0; y < 8; y++)
    for (let x = 0; x < 12; x++) {
      const s = (y * 12 + x) * 4, d = 54 + (7 - y) * 36 + x * 3;
      if (px[s + 3] < 128) continue; // clear: black
      out[d] = px[s + 2]; out[d + 1] = px[s + 1]; out[d + 2] = px[s];
    }
  return out;
}

/** A file name from a pilot's name for a sheet: "Green plasma" -> "green-plasma". */
const slug = (text) => String(text).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);

/** Saves bytes as a file in the browser's downloads (PNG or BMP, by what they are). */
function saveFile(bytes, stem) {
  const png = bytes[0] === 0x89 && bytes[1] === 0x50;
  const url = URL.createObjectURL(new Blob([bytes], { type: png ? "image/png" : "image/bmp" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = `${stem}.${png ? "png" : "bmp"}`;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

function base64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

// ---- drawing: the whole sheet, scaled up, and its animation playing -------------------------------
const players = new Map(); // display canvas -> { sheet, kind }
let playTimer = 0;
let tick = 0;

/** The frames a preview plays: level 1's row of a sprite sheet, or every frame of a burst. */
function frames(sheet, name) {
  const cell = CELLS[name];
  const cols = Math.floor(sheet.width / cell.w), rows = Math.floor(sheet.height / cell.h);
  const n = cell.row ? Math.min(cell.row, cols) : cols * rows;
  return { cell, cols, n: Math.max(1, n) };
}

function drawFrame(display, sheet, name, i) {
  const { cell, cols, n } = frames(sheet, name);
  const f = i % n;
  const g = display.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, display.width, display.height);
  // Small sprites are drawn at a whole-number scale, centred.
  const scale = Math.max(1, Math.floor(Math.min(display.width / cell.w, display.height / cell.h) * (cell.w < 30 ? 0.7 : 1)));
  const w = cell.w * scale, h = cell.h * scale;
  g.drawImage(sheet, (f % cols) * cell.w, Math.floor(f / cols) * cell.h, cell.w, cell.h,
    Math.round((display.width - w) / 2), Math.round((display.height - h) / 2), w, h);
}

function play(display, sheet, name) {
  if (sheet) players.set(display, { sheet, name }); else { players.delete(display); display.getContext("2d").clearRect(0, 0, display.width, display.height); }
  if (!playTimer && players.size)
    playTimer = setInterval(() => { tick++; for (const [d, p] of players) drawFrame(d, p.sheet, p.name, tick); }, 70);
  if (sheet) drawFrame(display, sheet, name, tick);
}

function showSheet(display, sheet) {
  const scale = sheet ? Math.max(1, Math.min(4, Math.floor(480 / sheet.width), Math.floor(420 / sheet.height))) : 1;
  display.width = sheet ? sheet.width * scale : 160;
  display.height = sheet ? sheet.height * scale : 16;
  const g = display.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, display.width, display.height);
  if (sheet) g.drawImage(sheet, 0, 0, display.width, display.height);
}

function stopPlaying() {
  clearInterval(playTimer);
  playTimer = 0;
  players.clear();
}

// ---- the tab -----------------------------------------------------------------------------------
function say(text, bad = false) {
  statusLine.textContent = text;
  statusLine.className = bad ? "gfxstatus bad" : "gfxstatus";
}

function setBusy(on, text = "") {
  busy = on;
  for (const b of panel.querySelectorAll("button, textarea, input, select")) b.disabled = on;
  if (!on) { $("gfx-a-restore").disabled = !worn().own; $("gfx-a-download").disabled = !shown; }
  if (text) say(text);
  dialog.classList.toggle("busy", on);
}

const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));

/** What the selected weapon wears on the selected ship: its own item, the every-ship one, or the arena's. */
function worn() {
  const row = armory?.slots?.[kind] ?? [];
  const own = row[ship] || 0;
  const id = own || row[0] || 0;
  return { own, id, item: id ? armory.items.find((i) => i.id === id) : null };
}

function renderPickers() {
  const k = kindOf(kind);
  kindList.replaceChildren(...kinds.map((w) => {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = w.label;
    b.setAttribute("aria-pressed", String(w.name === kind));
    b.addEventListener("click", () => {
      if (busy) return;
      kind = w.name;
      if (!w.perShip) ship = 0;
      showReview(null);
      void render();
    });
    return b;
  }));
  shipList.hidden = !k?.perShip;
  shipList.replaceChildren(...[0, 1, 2, 3, 4, 5, 6, 7, 8].map((s) => {
    const b = document.createElement("button");
    b.type = "button";
    b.innerHTML = s ? `<b>${s}</b> ${SHIPS[s - 1]}` : "Every ship";
    b.setAttribute("aria-pressed", String(s === ship));
    if (s && armory?.slots?.[kind]?.[s]) b.classList.add("has");
    b.addEventListener("click", () => { if (busy) return; ship = s; showReview(null); void render(); });
    return b;
  }));
  const options = k?.perShip ? [0, 1, 2, 3, 4, 5, 6, 7, 8] : [0];
  applyTo.replaceChildren(...options.map((s) => new Option(s ? `${s} · ${SHIPS[s - 1]}` : "Every ship", String(s))));
  applyTo.value = String(ship);
}

async function showCurrent() {
  const k = kindOf(kind);
  const asked = `${kind}@${ship}`;
  const { own, item } = worn();
  const where = k?.perShip ? ` on ${shipLabel(ship)}` : "";
  const label = $("gfx-a-label");
  $("gfx-a-hint").textContent = k?.hint ?? "";
  $("gfx-a-restore").disabled = busy || !own;
  $("gfx-a-prompted").hidden = !k?.generate?.includes("prompt");
  $("gfx-a-generate").hidden = !k?.generate?.length;
  $("gfx-a-restore").textContent = isBanner(kind) ? "No banner" : "Restore default";
  let sheet = null, bytes = null, stem = "";
  try {
    if (item) {
      bytes = await itemBytes(item.id);
      stem = `${kind}-${slug(item.name) || item.id}`;
      sheet = await sheetFromBytes(bytes);
      label.textContent = `${k.label}${where}: ${item.name} (from your armory${own || !ship ? "" : ", for every ship"})`;
    } else if (isBanner(kind)) {
      label.textContent = "Banner: none yet. Upload one (12×8), or keep one from the Foundry.";
    } else {
      bytes = await stockBytes(kind);
      stem = kind;
      sheet = bytes ? await sheetFromBytes(bytes) : null;
      label.textContent = `${k?.label ?? kind}${where}: the game's own (an arena may show its own)`;
    }
  } catch (error) {
    label.textContent = error.message;
  }
  if (asked !== `${kind}@${ship}`) return; // another weapon or ship was picked meanwhile
  shown = sheet ? { bytes, stem } : null;
  $("gfx-a-download").disabled = busy || !shown;
  play(current, sheet, kind);
  showSheet(currentSheet, sheet);
  $("gfx-a-note").textContent = armory
    ? `${k?.label ?? ""}${isBanner(kind) ? "s" : ""} you've saved. Click one to ${putOn(kind, k?.perShip ? ship : 0)}.`
    : "";
}

function renderList() {
  $("gfx-a-yard").classList.toggle("signedout", !armory);
  $("gfx-a-signin").hidden = !!armory;
  $("gfx-a-count").textContent = armory ? `${armory.items.length} / ${armory.max}` : "";
  const k = kindOf(kind);
  const items = (armory?.items ?? []).filter((i) => i.kind === kind);
  list.replaceChildren(...items.map((it) => {
    const li = document.createElement("li");
    const pick = document.createElement("button");
    pick.type = "button";
    pick.className = "thumb";
    pick.title = `${it.name}: ${putOn(kind, k?.perShip ? ship : 0)}`;
    const art = document.createElement("span");
    art.className = "art";
    const cell = CELLS[kind];
    // The first frame: the sheet is (width / cell) frames across and (height / cell) down.
    art.style.backgroundImage = `url("${API}/sheet?id=${it.id}")`;
    art.style.backgroundSize = `${(k.width / cell.w) * 100}% ${(k.height / cell.h) * 100}%`;
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = it.name;
    pick.append(art, name);
    const on = (armory.slots[kind] ?? []).map((id, s) => (id === it.id ? (s ? String(s) : "all") : "")).filter(Boolean);
    if (on.length) {
      const badge = document.createElement("span");
      badge.className = "on";
      badge.textContent = isBanner(kind) ? "on" : on.join(" ");
      badge.title = isBanner(kind) ? "Your banner" : `On ${on.map((s) => (s === "all" ? "every ship" : `ship ${s}`)).join(", ")}`;
      pick.append(badge);
    }
    pick.addEventListener("click", () => void useItem(it, ship));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "remove";
    remove.setAttribute("aria-label", `Remove ${it.name}`);
    remove.textContent = "✕";
    remove.addEventListener("click", () => void removeItem(it));
    const save = document.createElement("button");
    save.type = "button";
    save.className = "download";
    save.title = `Download ${it.name}`;
    save.setAttribute("aria-label", `Download ${it.name}`);
    save.textContent = "⤓";
    save.addEventListener("click", () => void downloadItem(it));
    li.append(pick, remove, save);
    return li;
  }));
  if (armory && !items.length) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = `${k?.label ?? "Weapons"} you approve land here.`;
    list.append(empty);
  }
}

async function render() {
  renderPickers();
  renderList();
  await showCurrent();
}

async function refresh() {
  await load();
  if (!kinds.length) kinds = FALLBACK_KINDS;
  await render();
}

// Shown signed out, before the server has said (the server's list wins once signed in).
const FALLBACK_KINDS = [
  { name: "bullets", label: "Bullets", width: 20, height: 50, perShip: true, generate: ["random", "prompt"], hint: "" },
  { name: "bombs", label: "Bombs", width: 160, height: 208, perShip: true, generate: ["random", "prompt"], hint: "" },
  { name: "mines", label: "Mines", width: 160, height: 128, perShip: true, generate: ["random", "prompt"], hint: "" },
  { name: "shrapnel", label: "Shrapnel", width: 80, height: 48, perShip: true, generate: ["random", "prompt"], hint: "" },
  { name: "repel", label: "Repel", width: 480, height: 192, perShip: false, generate: ["random", "prompt"], hint: "" },
  { name: "rocket", label: "Rocket", width: 312, height: 48, perShip: false, generate: ["random", "prompt"], hint: "" },
  { name: "banner", label: "Banner", width: 12, height: 8, perShip: false, generate: [], hint: "" },
];

/** Puts a saved item on the selected weapon for ship `s` (0 = every ship): on the account, then in the game. */
async function useItem(it, s) {
  if (busy) return;
  setBusy(true, `Putting ${it.name} on ${shipLabel(s)}…`);
  try {
    await call("/slot", { kind, ship: s, id: it.id });
    if (isBanner(kind)) { say(`${it.name} is your banner now.`); return; }
    globalThis.dareGraphics?.set(gameName(kind, s), await itemBytes(it.id));
    await nextFrame();
    say(`${it.name} is on ${shipLabel(s)}.`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

async function downloadItem(it) {
  try {
    saveFile(await itemBytes(it.id), `${it.kind}-${slug(it.name) || it.id}`);
  } catch (error) {
    say(error.message, true);
  }
}

async function removeItem(it) {
  if (busy || !confirm(`Remove ${it.name} from your armory?`)) return;
  setBusy(true, `Removing ${it.name}…`);
  try {
    const on = (armory.slots[it.kind] ?? []).map((id, s) => (id === it.id ? s : -1)).filter((s) => s >= 0);
    await call("/remove", { id: it.id });
    if (!isBanner(it.kind)) for (const s of on) globalThis.dareGraphics?.clear(gameName(it.kind, s));
    await nextFrame();
    say(`Removed ${it.name}.`);
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
    if (armory) await call("/slot", { kind, ship, id: 0 });
    if (isBanner(kind)) { say("No banner now."); return; }
    globalThis.dareGraphics?.clear(gameName(kind, ship));
    await nextFrame();
    say(ship ? `${kindOf(kind).label} on ship ${ship}: back to ${armory?.slots?.[kind]?.[0] ? "your every-ship one" : "the default"}.`
      : `${kindOf(kind).label}: back to the default.`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

// ---- new sheets: upload or generate, then approve --------------------------------------------------
function needArmory() {
  if (armory) return true;
  say("Sign in to make and keep your own weapons.", true);
  return false;
}

function showReview(next) {
  pending = next;
  review.hidden = !next;
  actions.hidden = !!next;
  genChoice.hidden = true;
  promptForm.hidden = true;
  if (!next) { play(reviewSpin, null, kind); return; }
  play(reviewSpin, next.sheet, kind);
  showSheet(reviewSheet, next.sheet);
  $("gfx-a-review-note").textContent = isBanner(kind) ? `Banner: ${next.name}. Colours become the game's nearest.` : `${kindOf(kind).label}: ${next.name}. The preview plays level 1.`;
  $("gfx-a-again").hidden = !next.again;
  applyTo.value = String(ship);
  $("gfx-a-approve").focus();
}

async function generate(request, name) {
  if (busy || !needArmory()) return;
  setBusy(true, request.batch ? "Finding a different one…"
    : request.mode === "prompt" ? "Drawing it… this takes up to a minute." : "Making one…");
  try {
    const data = await call("/generate", request);
    const sheet = await sheetFromBytes(Uint8Array.from(atob(data.sheet), (ch) => ch.charCodeAt(0)), kindOf(request.kind));
    say("Approve it to add it to your armory, or Try again for a different one.");
    // A prompt's Try again continues its run (data.batch): a take unlike the ones shown.
    const fresh = request.batch ? request.fresh : request;
    showReview({
      sheet,
      name,
      source: request.mode === "prompt" ? "generated" : "random",
      prompt: fresh.mode === "prompt" ? fresh.prompt || "" : "",
      again: () => generate(data.batch ? { kind: fresh.kind, mode: fresh.mode, batch: data.batch, fresh } : fresh, name),
    });
  } catch (error) {
    say(error.message, true);
    if (request.batch && pending) pending.again = () => generate(request.fresh, name); // expired: draw afresh
  } finally {
    setBusy(false);
  }
}

async function upload(file) {
  if (!file || busy || !needArmory()) return;
  try {
    const sheet = await sheetFromBlob(file, kindOf(kind));
    say("Check it, then approve it to add it to your armory.");
    showReview({ sheet, name: file.name.replace(/\.[^.]+$/, "").slice(0, 40) || `Uploaded ${kind}`, source: "uploaded", again: null });
  } catch (error) {
    say(error.message || "That file isn't an image the browser can read.", true);
  }
}

async function approve() {
  if (!pending || busy) return;
  const s = Number(applyTo.value) || 0;
  setBusy(true, "Adding it to your armory…");
  try {
    const bytes = isBanner(kind) ? bannerBmp(pending.sheet) : await pngBytes(pending.sheet);
    const added = await call("/add", { kind, name: pending.name, source: pending.source, prompt: pending.prompt || undefined, sheet: base64(bytes) });
    await call("/slot", { kind, ship: s, id: added.id });
    if (!isBanner(kind)) globalThis.dareGraphics?.set(gameName(kind, s), bytes);
    ship = s;
    showReview(null);
    await nextFrame();
    say(isBanner(kind) ? "Added to your armory; it's your banner now." : `Added to your armory and put on ${shipLabel(s)}.`);
  } catch (error) {
    say(error.message, true);
  } finally {
    setBusy(false);
    await refresh();
  }
}

// ---- wiring ----------------------------------------------------------------------------------------
$("gfx-a-restore").addEventListener("click", () => void restoreDefault());
$("gfx-a-download").addEventListener("click", () => { if (shown) saveFile(shown.bytes, shown.stem); });
$("gfx-a-upload").addEventListener("click", () => { if (needArmory()) fileInput.click(); });
fileInput.addEventListener("change", () => { void upload(fileInput.files?.[0]); fileInput.value = ""; });
$("gfx-a-generate").addEventListener("click", () => {
  if (!needArmory()) return;
  genChoice.hidden = !genChoice.hidden;
  promptForm.hidden = true;
});
$("gfx-a-random").addEventListener("click", () => void generate({ kind, mode: "random" }, `Random ${kindOf(kind).label.toLowerCase()}`));
$("gfx-a-prompted").addEventListener("click", () => {
  genChoice.hidden = true;
  promptForm.hidden = false;
  $("gfx-a-prompt-label").textContent = `Describe your ${kindOf(kind).label.toLowerCase()}`;
  $("gfx-a-prompt-text").focus();
});
promptForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = $("gfx-a-prompt-text").value.trim();
  if (!prompt) { say("Describe what you want.", true); return; }
  void generate({ kind, mode: "prompt", prompt }, prompt.slice(0, 40));
});
$("gfx-a-prompt-cancel").addEventListener("click", () => { promptForm.hidden = true; });
$("gfx-a-approve").addEventListener("click", () => void approve());
$("gfx-a-again").addEventListener("click", () => { const again = pending?.again; showReview(null); void again?.(); });
$("gfx-a-decline").addEventListener("click", () => { showReview(null); say("Declined. Nothing was saved."); });
$("gfx-a-signin").addEventListener("click", async () => { await globalThis.foundationSignIn?.(); await refresh(); });
dialog.addEventListener("cancel", (event) => { if (busy) event.preventDefault(); });
dialog.addEventListener("close", () => { stopPlaying(); showReview(null); });
// Reopening the overlay on this tab shows it afresh.
new MutationObserver(() => { if (dialog.open && !panel.hidden) void refresh(); })
  .observe(dialog, { attributes: true, attributeFilter: ["open"] });
// The tab bar (graphics-other.js) says which panel is showing.
dialog.addEventListener("gfx-tab", (event) => {
  if (event.detail === "gfx-armory") { say(""); void refresh(); } else stopPlaying();
});
