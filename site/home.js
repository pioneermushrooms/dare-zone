// Home page: the live zone list. Every zone is listed equally, by name. Text only (textContent).
"use strict";

function cell(text, cls) {
  const td = document.createElement("td");
  if (cls) td.className = cls;
  td.textContent = text;
  return td;
}

async function loadZones() {
  const rows = document.getElementById("zonerows");
  const note = document.getElementById("zonenote");
  let data = null;
  try {
    const r = await fetch("/arena/api/zones", { credentials: "omit" });
    data = await r.json();
  } catch { /* offline */ }
  if (!data || !Array.isArray(data.zones)) {
    rows.replaceChildren(tr([cell("The zone list is not available right now.", "dim")], 4));
    return;
  }
  if (data.zones.length === 0) {
    // The server asks the directories in the background; the first visit after a quiet minute waits for it.
    rows.replaceChildren(tr([cell("Asking the directory servers…", "dim")], 4));
    if (!loadZones.retried) { loadZones.retried = true; setTimeout(loadZones, 9000); }
    return;
  }
  rows.replaceChildren(...data.zones.map((z) => {
    const row = document.createElement("tr");
    row.append(cell(z.name, "name"), cell(z.description || "", "desc"), cell(z.address, "addr"), cell(String(z.players), "num"));
    return row;
  }));
  if (data.at) {
    const t = new Date(data.at);
    note.textContent = `${data.zones.length} zones from ${data.answered} of ${data.asked} directory servers, ${t.toISOString().slice(11, 16)} UTC.`;
  }
}

function tr(cells, span) {
  const row = document.createElement("tr");
  if (span && cells.length === 1) cells[0].colSpan = span;
  row.append(...cells);
  return row;
}

document.addEventListener("DOMContentLoaded", loadZones);
