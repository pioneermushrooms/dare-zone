const statusLine = document.querySelector("#directory-status");
const list = document.querySelector("#arena-list");
const refresh = document.querySelector("#refresh");

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function arenaCard(arena) {
  const card = element("article", null, "arena-card");
  const name = element("h3", arena.name);
  const meta = element("p", arena.owner ? `Run by ${arena.owner}` : "Player-owned arena");
  const state = arena.status === "Active"
    ? `${arena.players ?? 0} ${arena.players === 1 ? "pilot" : "pilots"} online`
    : String(arena.status ?? "");
  card.append(name, meta, element("span", state, "arena-status"));
  return card;
}

function activity(selector, items, empty, detail) {
  const target = document.querySelector(selector);
  const rows = items.slice(0, 8).map(item => {
    const row = element("li");
    row.append(element("strong", item.arena), element("span", detail(item)));
    return row;
  });
  target.replaceChildren(...(rows.length ? rows : [element("li", empty)]));
}

let loaded = false;
async function loadDirectory() {
  if (refresh.disabled) return;
  refresh.disabled = true;
  try {
    const response = await fetch("/arena/api/public", { cache: "no-store", signal: AbortSignal.timeout(8000) });
    if (!response.ok) throw new Error("Directory unavailable");
    const { arenas, pending, decisions } = await response.json();
    const sorted = [...arenas].sort((a, b) =>
      Number(b.status === "Active") - Number(a.status === "Active") ||
      Number(b.players ?? 0) - Number(a.players ?? 0) ||
      a.name.localeCompare(b.name)
    );
    list.replaceChildren(...sorted.map(arenaCard));
    statusLine.textContent = sorted.length ? "" : "No player-owned arenas are listed yet.";
    activity("#pending", pending, "No requests are waiting.", item => item.pitch || item.player);
    activity("#decisions", decisions, "No recent decisions.", item =>
      [item.status, item.reason].filter(Boolean).join(" · ")
    );
    loaded = true;
  } catch {
    statusLine.textContent = loaded
      ? "Couldn’t refresh. Showing the last available listings."
      : "Arena listings are temporarily unavailable. Try refreshing.";
  } finally {
    refresh.disabled = false;
  }
}
refresh.addEventListener("click", loadDirectory);
void loadDirectory();
setInterval(() => {
  if (!document.hidden) void loadDirectory();
}, 30000);
