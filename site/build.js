// The arena builder page: the signed-in pilot's draft, through /arena/api/drafts*.
const API = "/arena/api";
const $ = (id) => document.getElementById(id);

async function call(path, payload) {
  const init = payload === undefined
    ? { credentials: "same-origin", cache: "no-store" }
    : {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-Requested-With": "dare-arena" },
        body: JSON.stringify(payload),
      };
  try {
    const response = await fetch(API + path, init);
    let body = null;
    try { body = await response.json(); } catch {}
    return { status: response.status, body };
  } catch {
    return { status: 0, body: { ok: false, message: "Couldn't reach Foundation; try again." } };
  }
}

function say(el, body) {
  el.textContent = body?.message ?? "";
  el.className = "msg " + (body?.ok ? "ok" : "bad");
}

const STATUS = {
  Active: ["Private draft", ""],
  Requested: ["Waiting for staff", "wait"],
  Published: ["Public", "live"],
  Archived: ["Archived", "wait"],
};

function render(draft) {
  $("create").hidden = !!draft;
  $("mine").hidden = !draft;
  if (!draft) return;
  const hash = "#" + draft.name;
  $("draftname-title").textContent = hash;
  const [label, cls] = STATUS[draft.status] ?? [draft.status, ""];
  $("draftstatus").textContent = label;
  $("draftstatus").className = "tag " + cls;
  $("draftnote").textContent = draft.live
    ? `Public as '${draft.name}'${draft.liveStatus && draft.liveStatus !== "Active" ? ` (${draft.liveStatus.toLowerCase()})` : ""}. Change the draft, then publish.`
    : draft.status === "Requested" ? "Staff will fly in it and decide in public." : "In game: ?go " + hash;
  $("fly").href = "/play/?arena=" + encodeURIComponent(hash);
  $("edit").href = "/arena/me?arena=" + encodeURIComponent(hash);
  $("maxinv").textContent = draft.maxInvites;
  $("invites").replaceChildren(...(draft.invited.length ? draft.invited.map((name) => {
    const li = document.createElement("li");
    const who = document.createElement("span");
    who.textContent = name;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "outline";
    remove.textContent = "Remove";
    remove.setAttribute("aria-label", `Remove ${name}`);
    remove.addEventListener("click", async () => {
      const { body } = await call("/drafts/invite", { player: name, add: false });
      say($("invitemsg"), body);
      if (body?.ok) load();
    });
    li.append(who, remove);
    return li;
  }) : [Object.assign(document.createElement("li"), { textContent: "Nobody yet." })]));
  $("inviteform").querySelector("button").disabled = draft.invited.length >= draft.maxInvites;
  $("requestpanel").hidden = draft.live || draft.status !== "Active";
  $("publishpanel").hidden = !draft.live;
}

async function load() {
  const { status, body } = await call("/drafts/mine");
  $("signin").hidden = status !== 401 && status !== 403;
  if (status === 401 || status === 403) {
    $("create").hidden = true;
    $("mine").hidden = true;
    if (status === 403 && body?.message) $("signin").querySelector("p").textContent = body.message;
    return;
  }
  render(body?.draft ?? null);
}

function wire(formId, msgId, path, payload) {
  $(formId).addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = $(formId).querySelector("button");
    button.disabled = true;
    const { body } = await call(path, payload());
    button.disabled = false;
    say($(msgId), body);
    if (body?.ok) {
      $(formId).reset();
      load();
    }
  });
}

wire("createform", "createmsg", "/drafts", () => ({ name: $("draftname").value }));
wire("inviteform", "invitemsg", "/drafts/invite", () => ({ player: $("invitee").value, add: true }));
wire("requestform", "requestmsg", "/drafts/request", () => ({ pitch: $("pitch").value }));
wire("publishform", "publishmsg", "/drafts/publish", () => ({}));
load();
