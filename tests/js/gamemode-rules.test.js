// node --test tests/js/*.test.js   (no packages needed)
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const R = require("../../src/Dare.Arenas/Web/wwwroot/gamemode-rules.js");

// Specs like the arena's /api/me settings list: every key the rules module may write.
const INT_KEYS = ["Team:DesiredTeams", "Team:MaxPerTeam", "Team:MaxFrequency", "Team:MaxTeamDifference", "LegalShip:ArenaMask",
  "Misc:ShipChangeInterval", "Kill:FixedKillReward", "Misc:TimedGame", "GameMode:RoundTime", "Flag:CarryFlags", "Flag:FlagCount",
  "Flag:WinDelay", "Flag:FlagReward", "Flag:FlagDropDelay", "Flag:FlaggerSpeedAdjustment", "Soccer:BallCount", "Soccer:Reward",
  "Soccer:WinBy", "GameMode:KingReward", "Periodic:RewardDelay", "Periodic:RewardPoints", "King:DeathCount", "King:MinPlayers", "King:RewardFactor"];
const BOOL_KEYS = ["GameMode:FlatFlagReward", "Team:ForceEvenTeams", "Misc:TeamKillPoints", "Flag:SplitPoints", "Flag:DropOwned", "Flag:DropCenter",
  "Flag:FlaggerOnRadar", "Flag:PersistentTurfOwners", "Soccer:BallBounce", "Soccer:AllowGuns", "Soccer:AllowBombs", "King:AutoStart"];
function specs(extra = {}) {
  const s = {};
  for (const k of INT_KEYS) s[k] = { type: "int", min: k === "Kill:FixedKillReward" ? -1 : k === "Soccer:Reward" ? -1000000 : 0, max: k === "LegalShip:ArenaMask" ? 255 : k === "Flag:FlagCount" ? 256 : k === "Soccer:BallCount" ? 8 : 1000000 };
  for (const k of BOOL_KEYS) s[k] = { type: "bool" };
  s["Misc:TimerWarnings"] = { type: "text" };
  s["GameMode:Attach"] = { type: "flags" };
  s["Soccer:Mode"] = { type: "enum", choices: R.GOAL_LAYOUTS.map((g) => g.id) };
  s["Flag:FlagMode"] = { type: "enum", choices: ["CarryAll", "OwnAllDropped", "None"] }; // as the server sends it
  return { ...s, ...extra };
}
const game = (objective, more = {}) => R.withDefaults({ objective, ...more });

test("fight turns flags, balls and periodic off", () => {
  const out = R.settings(game("fight"), specs());
  assert.equal(out["Flag:FlagCount"], "0");
  assert.equal(out["Soccer:BallCount"], "0");
  assert.equal(out["Periodic:RewardDelay"], "0");
  assert.equal(out["Flag:FlagMode"], "None");
  assert.equal(out["GameMode:Attach"], "Kills");
});

test("rabbit is one carried flag with hold time", () => {
  const out = R.settings(game("rabbit"), specs());
  assert.equal(out["Flag:CarryFlags"], "1");
  assert.equal(out["Flag:FlagCount"], "1");
  assert.equal(out["Flag:FlagMode"], "CarryAll");
  assert.equal(out["Flag:WinDelay"], "18000");
});

test("turf uses static flags and periodic", () => {
  const out = R.settings(game("turf"), specs());
  assert.equal(out["Flag:CarryFlags"], "0");
  assert.equal(out["Periodic:RewardDelay"], "6000");
  assert.equal(out["Periodic:RewardPoints"], "100");
  assert.equal(out["Flag:PersistentTurfOwners"], "1");
});

test("ownall sets flag mode 1 and carryall 0", () => {
  assert.equal(R.settings(game("ownall"), specs())["Flag:FlagMode"], "OwnAllDropped");
  assert.equal(R.settings(game("carryall"), specs())["Flag:FlagMode"], "CarryAll");
  assert.equal(R.settings(game("turf"), specs())["Flag:FlagMode"], "None");
  assert.equal(R.settings(game("carryall"), specs())["Flag:FlagCount"], "6");
});

test("powerball layout goes to Soccer:Mode", () => {
  const out = R.settings(game("powerball", { ball: { layout: "TopBottom", count: 2 } }), specs());
  assert.equal(out["Soccer:Mode"], "TopBottom");
  assert.equal(out["Soccer:BallCount"], "2");
  assert.equal(out["Flag:FlagCount"], "0");
});

test("enum value outside choices is left out", () => {
  const out = R.settings(game("powerball", { ball: { layout: "TopBottom" } }), specs({ "Soccer:Mode": { type: "enum", choices: ["LeftRight"] } }));
  assert.ok(!("Soccer:Mode" in out));
});

test("attach words per objective", () => {
  assert.deepEqual(R.attach(game("fight")), ["Kills"]);
  assert.deepEqual(R.attach(game("carryall", { teams: { ships: 127, shipChangeSec: 5 } })), ["Kills", "Flags", "Ships", "ShipChange"]);
  assert.deepEqual(R.attach(game("powerball", { teams: { shipChangeSec: 0 } })), ["Kills", "Balls"]);
  assert.deepEqual(R.attach(game("king", { teams: { shipChangeSec: 0 } })), ["Kills", "King"]);
  assert.equal(R.settings(game("rabbit", { teams: { shipChangeSec: 0 } }), specs())["GameMode:Attach"], "Kills, Flags");
});

test("minutes to ticks and warnings as text", () => {
  const out = R.settings(game("fight", { time: { minutes: 30, warnings: [60, 10] } }), specs());
  assert.equal(out["GameMode:RoundTime"], "180000");
  assert.equal(out["Misc:TimerWarnings"], "60,10");
});

test("rounds run on the plugin's clock, so a flag win can start the next one", () => {
  // Misc:TimedGame can't be restarted after a flag win; GameMode:RoundTime can, and decides who wins at zero.
  for (const o of ["fight", "carryall", "ownall", "rabbit", "powerball"]) {
    const out = R.settings(game(o, { time: { minutes: 30 } }), specs());
    assert.equal(out["Misc:TimedGame"], "0", o);
    assert.equal(out["GameMode:RoundTime"], "180000", o);
  }
  assert.equal(R.settings(game("fight", { time: { minutes: 0 } }), specs())["GameMode:RoundTime"], "0");
});

test("the summary says what happens when a flag round runs out", () => {
  assert.match(R.summary(game("carryall", { time: { minutes: 30 } })), /most flags wins/i);
  assert.doesNotMatch(R.summary(game("carryall", { time: { minutes: 30 } })), /nothing happens/i);
  assert.match(R.summary(game("fight", { time: { minutes: 30 } })), /30-minute rounds/);
});

test("free-for-all is one per team; private teams off caps MaxFrequency", () => {
  const ffa = R.settings(game("fight", { teams: { count: 0 } }), specs());
  assert.equal(ffa["Team:MaxPerTeam"], "1");
  const two = R.settings(game("fight", { teams: { count: 2, private: false } }), specs());
  assert.equal(two["Team:MaxFrequency"], "2");
  assert.equal(two["Team:DesiredTeams"], "2");
  assert.equal(R.settings(game("fight", { teams: { private: true } }), specs())["Team:MaxFrequency"], "10000");
});

test("free-for-all leaves room for every pilot", () => {
  const ffa = R.settings(game("fight", { teams: { count: 0, private: false } }), specs());
  assert.equal(ffa["Team:MaxPerTeam"], "1");
  assert.equal(ffa["Team:MaxFrequency"], "100"); // up to PrivFreqStart: one public freq per pilot, private freqs still off
  assert.equal(R.settings(game("fight", { teams: { count: 0, private: true } }), specs())["Team:MaxFrequency"], "10000");
});

test("games without flags switch the static flag game off too (turf then fight)", () => {
  for (const o of ["fight", "powerball", "king"]) {
    const out = R.settings(game(o), specs());
    assert.equal(out["Flag:CarryFlags"], "1", o);
    assert.equal(out["Flag:FlagCount"], "0", o);
  }
});

test("fixed kill reward or bounty", () => {
  assert.equal(R.settings(game("fight"), specs())["Kill:FixedKillReward"], "-1");
  assert.equal(R.settings(game("fight", { scoring: { kill: "fixed", fixedKill: 25 } }), specs())["Kill:FixedKillReward"], "25");
});

test("soon objectives fall back to fight", () => {
  for (const id of ["ctf", "holdbase", "lives", "bogus"]) assert.equal(R.withDefaults({ objective: id }).objective, "fight");
  assert.ok(R.OBJECTIVES.filter((o) => o.soon).length === 3);
});

test("defaults fill missing game fields", () => {
  const g = R.withDefaults({ objective: "rabbit", flags: { count: 9 } });
  assert.equal(g.flags.holdSec, 180);
  assert.equal(g.flags.count, 9);
  assert.equal(g.teams.perTeam, 8);
  assert.equal(R.withDefaults(undefined).objective, "fight");
});

test("usesFlags and usesBall", () => {
  assert.ok(R.usesFlags(game("turf")) && !R.usesBall(game("turf")));
  assert.ok(R.usesBall(game("powerball")) && !R.usesFlags(game("powerball")));
  assert.ok(!R.usesFlags(game("king")) && !R.usesBall(game("king")));
});

test("every objective stays within spec", () => {
  const sp = specs();
  for (const o of R.OBJECTIVES.filter((x) => !x.soon).map((x) => x.id))
    for (const count of [0, 2, 4]) for (const minutes of [0, 30]) {
      const out = R.settings(game(o, { teams: { count }, time: { minutes }, flags: { count: 999, holdSec: 99999 } }), sp);
      for (const [k, v] of Object.entries(out)) {
        const s = sp[k];
        if (s.type === "int") { assert.match(v, /^-?\d+$/, `${o}: ${k}=${v}`); assert.ok(+v >= s.min && +v <= s.max, `${o}: ${k}=${v}`); }
        if (s.type === "bool") assert.ok(v === "0" || v === "1", `${o}: ${k}=${v}`);
      }
    }
});

test("summary mentions the objective, teams and time", () => {
  const s = R.summary(game("carryall", { time: { minutes: 30 } }), { pace: "fast", items: "standard" });
  assert.match(s, /flags/i);
  assert.match(s, /30-minute/);
  assert.match(s, /two teams/i);
  assert.match(s, /fast pace/i);
  assert.match(R.summary(game("fight", { teams: { count: 0 } })), /everyone for themselves/i);
});

test("rewards pay as written by default", () => {
  const flags = R.settings(game("carryall", { scoring: { winReward: 5000 } }), specs());
  assert.equal(flags["Flag:FlagReward"], "5000"); assert.equal(flags["GameMode:FlatFlagReward"], "1");
  const ball = R.settings(game("powerball", { scoring: { goalReward: 1000 } }), specs());
  assert.equal(ball["Soccer:Reward"], "-1000"); // the server pays a negative Soccer:Reward as written
  const king = R.settings(game("king", { scoring: { kingReward: 800 } }), specs());
  assert.equal(king["King:RewardFactor"], "0"); assert.equal(king["GameMode:KingReward"], "800"); // the plugin pays it
});

test("rewards can scale with the number of pilots, as the server does", () => {
  const sc = { winScale: "players", goalScale: "players", kingScale: "players", winReward: 5000, goalReward: 1000, kingReward: 800 };
  assert.equal(R.settings(game("ownall", { scoring: sc }), specs())["GameMode:FlatFlagReward"], "0");
  assert.equal(R.settings(game("powerball", { scoring: sc }), specs())["Soccer:Reward"], "1000");
  const king = R.settings(game("king", { scoring: sc }), specs());
  assert.equal(king["King:RewardFactor"], "800"); assert.equal(king["GameMode:KingReward"], "0");
});

test("the summary says how a reward pays", () => {
  assert.match(R.summary(game("carryall", { scoring: { winReward: 5000 } })), /5,000 points/);
  assert.match(R.summary(game("carryall", { scoring: { winReward: 5000, winScale: "players" } })), /more pilots/i);
  assert.match(R.summary(game("king", { scoring: { kingReward: 800 } })), /800 points/);
  assert.match(R.summary(game("powerball", { scoring: { goalReward: 1000, goalScale: "players" } })), /more pilots/i);
});
