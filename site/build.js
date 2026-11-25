// The arena builder page: the signed-in pilot's drafts (up to 3) and waiting requests,
// through /arena/api/drafts* and /arena/api/requests/withdraw.
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

function el(tag, props = {}, ...children) {
  const node = Object.assign(document.createElement(tag), props);
  node.append(...children);
  return node;
}

function say(target, body) {
  target.textContent = body?.message ?? "";
  target.className = "msg " + (body?.ok ? "ok" : "bad");
}

const STATUS = {
  Active: ["Private draft", ""],
  Requested: ["Waiting for staff", "wait"],
  Published: ["Public", "live"],
  Archived: ["Archived", "wait"],
};

// A form with one text field (or none) and a button that posts and reloads on success.
function actionForm({ placeholder, maxLength, button, outline, path, payload, msg }) {
  const input = placeholder ? el("input", { placeholder, maxLength, required: true, autocomplete: "off" }) : null;
  if (input) input.setAttribute("aria-label", placeholder);
  const submit = el("button", { type: "submit", textContent: button, className: outline ? "outline" : "" });
  const form = el("form", {}, ...(input ? [input] : []), submit);
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    submit.disabled = true;
    const { body } = await call(path, payload(input?.value ?? ""));
    submit.disabled = false;
    say(msg, body);
    if (body?.ok) load();
  });
  return form;
}

function draftCard(draft, maxInvites) {
  const hash = "#" + draft.name;
  const [label, cls] = STATUS[draft.status] ?? [draft.status, ""];
  const note = draft.live
    ? `Public as '${draft.name}'${draft.liveStatus && draft.liveStatus !== "Active" ? ` (${draft.liveStatus.toLowerCase()})` : ""}. Change the draft, then publish.`
    : draft.status === "Requested" ? "Staff will fly in it and decide in public." : "In game: ?go " + hash;
  const msg = el("p", { className: "msg", role: "status" });

  const invites = el("ul", { className: "invites" },
    ...(draft.invited.length ? draft.invited.map((name) => {
      const remove = el("button", { type: "button", className: "outline", textContent: "Remove" });
      remove.setAttribute("aria-label", `Remove ${name} from ${hash}`);
      remove.addEventListener("click", async () => {
        const { body } = await call("/drafts/invite", { name: draft.name, player: name, add: false });
        say(msg, body);
        if (body?.ok) load();
      });
      return el("li", {}, el("span", { textContent: name }), remove);
    }) : [el("li", { textContent: "Nobody invited yet." })]));

  const card = el("section", { className: "panel draft" },
    el("div", { className: "status-line" }, el("h2", { textContent: hash }), el("span", { className: "tag " + cls, textContent: label })),
    el("p", { className: "msg", textContent: note }),
    el("div", { className: "actions" },
      el("a", { className: "primary", href: "/?arena=" + encodeURIComponent(hash), textContent: "Fly in it" }),
      el("a", { className: "outline", href: "/arena/me?arena=" + encodeURIComponent(hash), textContent: "Edit settings, map and graphics" })),
    el("div", {},
      el("h3", { textContent: `Who can enter (up to ${maxInvites} invited)` }),
      invites,
      draft.invited.length < maxInvites
        ? actionForm({ placeholder: "pilot name", maxLength: 24, button: "Invite", outline: true, path: "/drafts/invite", payload: (v) => ({ name: draft.name, player: v, add: true }), msg })
        : el("span")));

  if (!draft.live && draft.status === "Active")
    card.append(el("div", {}, el("h3", { textContent: "Go public" }),
      actionForm({ placeholder: "what it's for (optional)", maxLength: 120, button: "Go public", path: "/drafts/request", payload: (v) => ({ name: draft.name, pitch: v }), msg })));
  if (draft.live)
    card.append(el("div", {}, el("h3", { textContent: "Publish an update" }),
      el("p", { textContent: "Copies this draft's settings, map and graphics over the public arena and restarts it." }),
      actionForm({ button: "Publish update", path: "/drafts/publish", payload: () => ({ name: draft.name }), msg })));
  card.append(msg);
  return card;
}

function renderRequests(requests) {
  $("requests").hidden = requests.length === 0;
  $("requestlist").replaceChildren(...requests.map((r) => {
    const withdraw = el("button", { type: "button", className: "outline", textContent: "Withdraw" });
    withdraw.setAttribute("aria-label", `Withdraw the request for ${r.arena}`);
    withdraw.addEventListener("click", async () => {
      const { body } = await call("/requests/withdraw", { arena: r.arena });
      say($("requestsmsg"), body);
      if (body?.ok) load();
    });
    return el("li", {}, el("span", {}, el("strong", { textContent: r.arena }), " — " + r.pitch), withdraw);
  }));
}

async function load() {
  const { status, body } = await call("/drafts/mine");
  $("signin").hidden = status !== 401 && status !== 403;
  if (status === 401 || status === 403) {
    $("create").hidden = true;
    $("requests").hidden = true;
    $("drafts").replaceChildren();
    if (status === 403 && body?.needsEmail) {
      // signed in, but the account has no confirmed recovery email yet
      $("signin").querySelector("h2").textContent = "One more step";
      const p = $("signin").querySelector("p");
      p.textContent = "Arena building needs a recovery email on your account, so you can't be locked out of your arena. ";
      p.append(el("a", { href: "/account/", textContent: "Add it on your account page" }), ", then come back here.");
    } else if (status === 403 && body?.message) {
      $("signin").querySelector("p").textContent = body.message;
    }
    return;
  }
  const drafts = body?.drafts ?? [];
  const max = body?.max ?? 3;
  $("create").hidden = drafts.length >= max;
  $("createtitle").textContent = drafts.length === 0 ? "Make your draft" : `Make another draft (${drafts.length} of ${max})`;
  renderRequests(body?.requests ?? []);
  $("drafts").replaceChildren(...drafts.map((d) => draftCard(d, body?.maxInvites ?? 5)));
}

$("createform").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = $("createform").querySelector("button");
  button.disabled = true;
  const { body } = await call("/drafts", { name: $("draftname").value });
  button.disabled = false;
  say($("createmsg"), body);
  if (body?.ok) {
    $("createform").reset();
    load();
  }
});
load();
