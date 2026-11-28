// The home page's tutorial box (site/tutorial.js), its DOM-free half: node --test tests/js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const Tutorial = require("../../site/tutorial.js");

const KEYS = { gun: "Ctrl", bomb: "Tab", afterburner: "Shift", repel: "Shift+Ctrl" };
const keyFor = (action) => KEYS[action] ?? "";

test("placeholders take the player's own keys", () => {
  assert.equal(Tutorial.fillKeys("Press {gun} to fire, {bomb} to bomb.", keyFor), "Press Ctrl to fire, Tab to bomb.");
  assert.equal(Tutorial.fillKeys("Hold {afterburner}. {afterburner} again!", keyFor), "Hold Shift. Shift again!");
  assert.equal(Tutorial.fillKeys("{repel}", keyFor), "Shift+Ctrl");
  assert.equal(Tutorial.fillKeys("No keys here.", keyFor), "No keys here.");
});

test("an action the game can't name, or no game yet, reads as the action", () => {
  assert.equal(Tutorial.fillKeys("Press {warp}.", keyFor), "Press warp.");
  assert.equal(Tutorial.fillKeys("Press {gun}.", undefined), "Press gun.");
  assert.equal(Tutorial.fillKeys("Press {gun}.", () => { throw new Error("no game"); }), "Press gun.");
  // braces that aren't a placeholder stay as they are
  assert.equal(Tutorial.fillKeys("a {not one} b {}", keyFor), "a {not one} b {}");
});

test("each button sends its one command", () => {
  assert.equal(Tutorial.commandFor("skip"), "?skip");
  assert.equal(Tutorial.commandFor("leave"), "?leave");
  assert.equal(Tutorial.commandFor("try"), "?tutorial");
  assert.equal(Tutorial.commandFor("go", "duel2"), "?go duel2");
  assert.equal(Tutorial.commandFor("go", "#mine"), "?go #mine");
  // the page's own name for the main public arena is "", the zone's is 0
  assert.equal(Tutorial.commandFor("go", ""), "?go 0");
});

test("nothing else becomes a command", () => {
  assert.equal(Tutorial.commandFor("kill"), null);
  assert.equal(Tutorial.commandFor("go", "a b"), null);
  assert.equal(Tutorial.commandFor("go", "duel;?kill"), null);
  assert.equal(Tutorial.commandFor("go", "abcdefghijklmnopq"), null);
  assert.equal(Tutorial.commandFor("go", undefined), "?go 0");
});

test("hint lines read the same as the game reads them", () => {
  assert.deepEqual(Tutorial.parseHint("~tut~ say 3/20 Press {gun} to fire."), { kind: "say", step: 3, of: 20, text: "Press {gun} to fire." });
  assert.deepEqual(Tutorial.parseHint("~tut~ reveal radar"), { kind: "reveal", part: "radar" });
  assert.deepEqual(Tutorial.parseHint("~tut~ prompt skip"), { kind: "prompt", prompt: "skip" });
  assert.deepEqual(Tutorial.parseHint("~tut~ prompt try"), { kind: "prompt", prompt: "try" });
  assert.deepEqual(Tutorial.parseHint("~tut~ end list"), { kind: "end", list: true });
  assert.deepEqual(Tutorial.parseHint("~tut~ end none"), { kind: "end", list: false });
  for (const junk of ["", "say 1/2 hi", "~tut~ say 0/2 hi", "~tut~ say 3/2 hi", "~tut~ say x/2 hi", "~tut~ reveal map",
    "~tut~ prompt later", "~tut~ end maybe", "~tut~ dance", null, undefined, 7]) {
    assert.equal(Tutorial.parseHint(junk), null, String(junk));
  }
});

test("the chooser offers the open arenas, busiest first, and not the training ones", () => {
  const rows = [
    { value: "", name: "public", players: 3, mine: false },
    { value: "duel", name: "duel", players: 5, mine: false },
    { value: "#training2", name: "#training2", players: 1, mine: false },
    { value: "#mine", name: "#mine", players: null, mine: true },
    { value: "quiet", name: "quiet", players: 0, mine: false },
  ];
  assert.deepEqual(Tutorial.arenaChoices(rows), [
    { name: "duel", players: 5, command: "?go duel" },
    { name: "public", players: 3, command: "?go 0" },
    { name: "quiet", players: 0, command: "?go quiet" },
  ]);
  assert.deepEqual(Tutorial.arenaChoices(undefined), []);
});

test("the mentor's name is in one place", () => {
  assert.equal(Tutorial.MENTOR, "Vex");
});
