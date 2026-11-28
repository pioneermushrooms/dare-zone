// The home page's arena list, attached to the chat: every open arena with how many are in it, busiest
// first or A–Z. Picking one picks the arena the chat shows and the one Enter flies into. Double-click
// (or Enter on the keyboard) to fly straight in.
// The game's start script (/play/start.mjs) still owns the arena: it fills the hidden <select id="arena">
// (the zone's arenas as "name · players", then the pilot's own private ones) and remembers the choice;
// this list mirrors that select and sets it.
const $ = (id) => document.getElementById(id);
const picker = $("arena");
const list = $("arenalist");
const SORT_KEY = "foundation.arenaSort";
let sort = "players";
try { if (localStorage.getItem(SORT_KEY) === "name") sort = "name"; } catch {}

// what the select holds, as { value, name, players (null for a private arena), mine }
function read() {
  const out = [];
  for (const o of picker.querySelectorAll("option")) {
    const mine = o.parentElement?.tagName === "OPTGROUP";
    const [name, count] = o.textContent.split(" · ");
    const n = Number.parseInt(count, 10);
    out.push({ value: o.value, name: (name || o.value || "public").trim(), players: Number.isFinite(n) ? n : null, mine });
  }
  return out;
}

function byName(a, b) {
  if (a.value === "") return -1; // public first among equals
  if (b.value === "") return 1;
  return a.name.localeCompare(b.name, undefined, { sensitivity: "base", numeric: true });
}
function ordered(items) {
  const open = items.filter((a) => !a.mine), mine = items.filter((a) => a.mine);
  open.sort(sort === "name" ? byName : (a, b) => (b.players ?? 0) - (a.players ?? 0) || byName(a, b));
  mine.sort(byName);
  return { open, mine };
}

function row(a) {
  const li = document.createElement("li");
  li.setAttribute("role", "option");
  li.dataset.value = a.value;
  li.id = "arena-opt-" + (a.value || "public").replace(/[^\w-]/g, "_");
  const sel = a.value === picker.value;
  li.setAttribute("aria-selected", String(sel));
  if (sel) li.className = "current";
  const name = document.createElement("span");
  name.className = "aname";
  name.textContent = a.name;
  li.append(name);
  const count = document.createElement("span");
  count.className = "acount" + (a.players ? " live" : "");
  count.textContent = a.mine ? "yours" : String(a.players ?? 0);
  if (!a.mine) count.title = a.players === 1 ? "1 pilot here" : `${a.players ?? 0} pilots here`;
  li.append(count);
  li.title = "Double-click to enter";
  return li;
}

function render() {
  const { open, mine } = ordered(read());
  const rows = open.map(row);
  if (mine.length) {
    const head = document.createElement("li");
    head.className = "group";
    head.setAttribute("role", "presentation");
    head.textContent = "Your private arenas";
    rows.push(head, ...mine.map(row));
  }
  list.replaceChildren(...rows);
  const cur = list.querySelector("li.current");
  if (cur) list.setAttribute("aria-activedescendant", cur.id); else list.removeAttribute("aria-activedescendant");
  for (const b of document.querySelectorAll(".sortby button")) b.setAttribute("aria-pressed", String(b.dataset.sort === sort));
  showDest();
}

// the destination card over Enter: the arena's name, and who's there
function showDest() {
  const a = read().find((x) => x.value === picker.value) ?? { name: "public", players: null, mine: false };
  $("destname").textContent = a.name;
  $("destmeta").textContent = a.mine ? "Your private arena"
    : a.players == null ? " "
    : a.players === 0 ? "Nobody flying yet" : a.players === 1 ? "1 pilot flying" : `${a.players} pilots flying`;
  $("destmeta").classList.toggle("live", !!a.players);
}

function choose(value) {
  if (picker.value === value) return;
  picker.value = value;
  picker.dispatchEvent(new Event("change", { bubbles: true })); // start.mjs remembers it; chat.js follows it
  render();
  list.querySelector("li.current")?.scrollIntoView({ block: "nearest" });
}

function enter() {
  const start = $("start");
  if (start && !start.disabled) start.click();
}

list.addEventListener("click", (event) => {
  const li = event.target.closest("li[role=option]");
  if (li) choose(li.dataset.value);
});
list.addEventListener("dblclick", (event) => {
  if (event.target.closest("li[role=option]")) enter();
});
list.addEventListener("keydown", (event) => {
  const opts = [...list.querySelectorAll("li[role=option]")];
  const i = opts.findIndex((li) => li.classList.contains("current"));
  let next = null;
  if (event.key === "ArrowDown") next = opts[Math.min(i + 1, opts.length - 1)];
  else if (event.key === "ArrowUp") next = opts[Math.max(i - 1, 0)];
  else if (event.key === "Home") next = opts[0];
  else if (event.key === "End") next = opts[opts.length - 1];
  else if (event.key === "Enter") { event.preventDefault(); enter(); return; }
  else return;
  event.preventDefault();
  if (next) choose(next.dataset.value);
});
for (const b of document.querySelectorAll(".sortby button")) {
  b.addEventListener("click", () => {
    sort = b.dataset.sort;
    try { localStorage.setItem(SORT_KEY, sort); } catch {}
    render();
  });
}

// the open arenas as this list has them ({ value, name, players, mine }), for the tutorial's chooser
globalThis.foundationArenaRows = () => read();

// start.mjs refills the select whenever it fetches the arenas or the pilot's own; follow along
new MutationObserver(render).observe(picker, { childList: true, subtree: true, characterData: true });
picker.addEventListener("change", render);
// keep the counts fresh while the home page is showing
setInterval(() => {
  if (!document.hidden && !$("entry").hidden) globalThis.refreshFoundationArenas?.();
}, 20000);
render();
