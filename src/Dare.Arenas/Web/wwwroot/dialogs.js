// In-page replacements for the browser's confirm() / alert() / prompt(), styled like the site's
// other <dialog>s. Each returns a promise:
//   await Dlg.confirm("Remove it?", { ok: "Remove", danger: true })  -> true / false
//   await Dlg.alert("Done.")                                          -> undefined
//   await Dlg.prompt("Why?", { value: "", placeholder: "…" })          -> string, or null when cancelled
//   await Dlg.choose("Publish first?", { choices: [{ value: "pub", label: "Publish" }, { value: "go", label: "Enter" }] })
//                                                                      -> the chosen value, or null when cancelled
//     (the first choice is the main button and what Enter picks; a choice with danger: true is red)
// Enter confirms (in a prompt too), Escape cancels, focus returns to where it was.
// Text goes in with textContent only.
"use strict";

window.Dlg = (() => {
  let open = null; // one at a time: a second call waits for the first to close

  function show(kind, message, opts = {}) {
    const run = () => new Promise((resolve) => {
      const back = document.activeElement;
      const dlg = document.createElement("dialog");
      dlg.className = "askdlg";
      const id = "askdlg-" + Math.random().toString(36).slice(2);
      dlg.setAttribute("aria-labelledby", id + "-t");
      dlg.setAttribute("aria-describedby", id + "-m");

      const form = document.createElement("form");
      form.method = "dialog";
      const h = document.createElement("h3");
      h.id = id + "-t";
      h.textContent = opts.title || (kind === "alert" ? "Note" : kind === "prompt" ? "One more thing" : "Are you sure?");
      const p = document.createElement("p");
      p.id = id + "-m";
      p.className = "askmsg";
      p.textContent = message;
      form.append(h, p);

      let input = null;
      if (kind === "prompt") {
        input = document.createElement(opts.multiline ? "textarea" : "input");
        if (!opts.multiline) input.type = "text";
        else input.rows = 2;
        input.className = "askinput";
        input.value = opts.value || "";
        if (opts.placeholder) input.placeholder = opts.placeholder;
        if (opts.maxLength) input.maxLength = opts.maxLength;
        input.setAttribute("aria-labelledby", id + "-m");
        form.append(input);
      }

      const row = document.createElement("div");
      row.className = "row dlg-actions";
      const choices = kind === "choose" ? opts.choices || [] : [];
      const ok = document.createElement("button");
      ok.type = "submit";
      ok.value = "ok";
      ok.className = (choices[0] ? choices[0].danger : opts.danger) ? "danger" : "hot";
      ok.textContent = choices[0] ? choices[0].label : opts.ok || "OK";
      row.append(ok);
      for (const c of choices.slice(1)) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = c.danger ? "danger" : "ghost";
        b.textContent = c.label;
        b.addEventListener("click", () => finish(c.value));
        row.append(b);
      }
      let cancel = null;
      if (kind !== "alert") {
        cancel = document.createElement("button");
        cancel.type = "button";
        cancel.className = "ghost";
        cancel.textContent = opts.cancel || "Cancel";
        row.append(cancel);
      }
      form.append(row);
      dlg.append(form);
      document.body.append(dlg);

      let result = kind === "alert" ? undefined : kind === "confirm" ? false : null;
      const finish = (value) => { result = value; dlg.close(); };
      form.addEventListener("submit", (e) => {
        e.preventDefault();
        finish(kind === "prompt" ? input.value : kind === "alert" ? undefined : kind === "choose" ? choices[0]?.value ?? null : true);
      });
      // Enter in a textarea makes a new line only with Shift; plain Enter confirms
      if (input && opts.multiline) input.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); form.requestSubmit(ok); }
      });
      if (cancel) cancel.addEventListener("click", () => dlg.close());
      dlg.addEventListener("close", () => {
        dlg.remove();
        if (back && typeof back.focus === "function" && document.contains(back)) back.focus();
        resolve(result);
      });
      dlg.showModal();
      if (input) { input.focus(); input.select(); } else ok.focus();
    });
    const next = (open || Promise.resolve()).then(run, run);
    open = next.catch(() => {});
    return next;
  }

  return {
    confirm: (message, opts) => show("confirm", message, opts),
    alert: (message, opts) => show("alert", message, opts),
    prompt: (message, opts) => show("prompt", message, opts),
    choose: (message, opts) => show("choose", message, opts),
  };
})();
