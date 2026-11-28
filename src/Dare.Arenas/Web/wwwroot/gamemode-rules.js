// Game mode wizard maths for the game panels (Objective, Teams, Scoring, Time, Flags / ball), with no DOM:
// shared by the Game mode tab and node tests. GameModeRules.settings(game, specs) gives Section:Key strings.
// Spec: docs/superpowers/specs/2026-11-28-game-mode-phase2-wizard-design.md. Times are seconds/minutes here,
// ticks (1/100 s) in the settings.
"use strict";

(function (root, factory) {
  const ships = typeof module === "object" && module.exports ? require("./gamemode-ships.js") : root.GameModeShips;
  const api = factory(ships);
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GameModeRules = api;
})(typeof self !== "undefined" ? self : this, (Ships) => {
  const OBJECTIVES = [
    { id: "fight", name: "Just fight", blurb: "Points for kills. Teams or everyone for themselves." },
    { id: "carryall", name: "Carry all flags", blurb: "Grab every flag and hold them all to win." },
    { id: "ownall", name: "Own all flags", blurb: "Drop flags to claim them. Own every one to win." },
    { id: "rabbit", name: "Rabbit", blurb: "One flag. Kill the carrier to take it. Hold it to win." },
    { id: "turf", name: "Turf", blurb: "Flags fixed on the map. Points every minute for each one you hold." },
    { id: "powerball", name: "Powerball", blurb: "Get the ball into the other team's goal." },
    { id: "king", name: "King of the hill", blurb: "Everyone wears a crown. Last one crowned wins." },
    { id: "ctf", name: "Capture the flag", blurb: "Take their flag back to your base.", soon: true },
    { id: "holdbase", name: "Hold the base", blurb: "Keep an area clear of enemies.", soon: true },
    { id: "lives", name: "Lives duel", blurb: "e.g. 4v4, 10 lives each. Last team flying wins.", soon: true },
  ];
  const GOAL_LAYOUTS = [
    { id: "LeftRight", name: "Left vs right" },
    { id: "TopBottom", name: "Top vs bottom" },
    { id: "All", name: "Any goal" },
    { id: "QuadrantsDefend1", name: "4 corners: defend 1" },
    { id: "QuadrantsDefend3", name: "4 corners: defend 3" },
    { id: "SidesDefend1", name: "4 sides: defend 1" },
    { id: "SidesDefend3", name: "4 sides: defend 3" },
  ];
  const FLAG_GAMES = new Set(["carryall", "ownall", "rabbit", "turf"]);
  const ATTACH_ORDER = ["Kills", "Flags", "Balls", "King", "Ships", "ShipChange"];
  // Flag:FlagMode is an enum setting: the server sends and checks it by name (None, CarryAll, OwnAllDropped).

  function defaults() {
    return {
      objective: "fight",
      teams: { count: 2, perTeam: 8, even: true, private: false, ships: 255, shipChangeSec: 0 },
      scoring: { kill: "bounty", fixedKill: 10, teamKills: false, winReward: 5000, split: false, turfPoints: 100, turfEverySec: 60, goalReward: 1000, kingReward: 1000 },
      time: { minutes: 0, warnings: [60, 30, 10] },
      flags: { count: 6, holdSec: 180, dropCenter: false, dropAfterSec: 0, carrierOnRadar: true, carrierSpeed: 0 },
      ball: { count: 1, layout: "LeftRight", bounce: true, guns: true, bombs: false, winBy: 0 },
      king: { deaths: 0, minPlayers: 2 },
    };
  }

  const isObj = (v) => v && typeof v === "object" && !Array.isArray(v);
  function merge(base, over) {
    const out = { ...base };
    if (!isObj(over)) return out;
    for (const [k, v] of Object.entries(over)) out[k] = isObj(base[k]) ? merge(base[k], v) : v;
    return out;
  }

  /** The game with every missing field from defaults(); an unknown or not-yet-available objective becomes "fight". */
  function withDefaults(game) {
    const g = merge(defaults(), game);
    const o = OBJECTIVES.find((x) => x.id === g.objective);
    if (!o || o.soon) g.objective = "fight";
    if (!Array.isArray(g.time.warnings)) g.time.warnings = defaults().time.warnings;
    return g;
  }

  const usesFlags = (g) => FLAG_GAMES.has(withDefaults(g).objective);
  const usesBall = (g) => withDefaults(g).objective === "powerball";

  /** GameMode:Attach names for the game, in the server's allow-list order. */
  function attach(game) {
    const g = withDefaults(game);
    const set = new Set(["Kills"]);
    if (["carryall", "ownall", "rabbit"].includes(g.objective)) set.add("Flags");
    if (g.objective === "powerball") set.add("Balls");
    if (g.objective === "king") set.add("King");
    if ((+g.teams.ships & 255) !== 255) set.add("Ships");
    if (+g.teams.shipChangeSec > 0) set.add("ShipChange");
    return ATTACH_ORDER.filter((n) => set.has(n));
  }

  const ticks = (sec) => Math.round(Math.max(0, +sec || 0) * 100);
  const b = (v) => (v ? 1 : 0);

  function raw(game) {
    const g = withDefaults(game);
    const t = g.teams, s = g.scoring, f = g.flags, ball = g.ball, k = g.king;
    const count = Math.max(0, Math.round(+t.count || 0));
    const v = {
      "Team:DesiredTeams": count || 1,
      "Team:MaxPerTeam": count ? +t.perTeam : 1,
      "Team:ForceEvenTeams": b(t.even && count > 0),
      "Team:MaxFrequency": t.private ? 10000 : Math.max(count, 1),
      "LegalShip:ArenaMask": +t.ships & 255,
      "Misc:ShipChangeInterval": ticks(t.shipChangeSec),
      "Kill:FixedKillReward": s.kill === "fixed" ? +s.fixedKill : -1,
      "Misc:TeamKillPoints": b(s.teamKills),
      "Misc:TimedGame": Math.round(Math.max(0, +g.time.minutes || 0) * 6000),
      "Misc:TimerWarnings": g.time.warnings.map((w) => Math.round(+w)).filter((w) => w > 0).join(","),
      "GameMode:Attach": attach(g).join(", ") || "None",
    };
    const noBall = { "Soccer:BallCount": 0 };
    const noFlags = { "Flag:FlagCount": 0, "Flag:FlagMode": "None" };
    const noPeriodic = { "Periodic:RewardDelay": 0 };
    switch (g.objective) {
      case "carryall": case "ownall": case "rabbit":
        Object.assign(v, noBall, noPeriodic, {
          "Flag:CarryFlags": 1,
          "Flag:FlagMode": g.objective === "ownall" ? "OwnAllDropped" : "CarryAll",
          "Flag:FlagCount": g.objective === "rabbit" ? 1 : +f.count,
          "Flag:WinDelay": ticks(f.holdSec),
          "Flag:FlagReward": +s.winReward,
          "Flag:SplitPoints": b(s.split),
          "Flag:DropOwned": 1,
          "Flag:DropCenter": b(f.dropCenter),
          "Flag:FlagDropDelay": ticks(f.dropAfterSec),
          "Flag:FlaggerOnRadar": b(f.carrierOnRadar),
          "Flag:FlaggerSpeedAdjustment": +f.carrierSpeed,
        });
        break;
      case "turf":
        Object.assign(v, noBall, {
          "Flag:CarryFlags": 0, "Flag:FlagMode": "None",
          "Periodic:RewardDelay": ticks(s.turfEverySec), "Periodic:RewardPoints": +s.turfPoints,
          "Flag:PersistentTurfOwners": 1,
        });
        break;
      case "powerball":
        Object.assign(v, noFlags, noPeriodic, {
          "Soccer:BallCount": +ball.count, "Soccer:Mode": ball.layout, "Soccer:Reward": +s.goalReward,
          "Soccer:WinBy": +ball.winBy, "Soccer:BallBounce": b(ball.bounce), "Soccer:AllowGuns": b(ball.guns),
          "Soccer:AllowBombs": b(ball.bombs),
        });
        break;
      case "king":
        Object.assign(v, noFlags, noBall, noPeriodic, {
          "King:DeathCount": +k.deaths, "King:MinPlayers": +k.minPlayers, "King:RewardFactor": +s.kingReward, "King:AutoStart": 1,
        });
        break;
      default:
        Object.assign(v, noFlags, noBall, noPeriodic);
    }
    return v;
  }

  /** The game's settings: only keys with a spec; ints and bools clamped, enums only from their choices, text and flags as built. */
  function settings(game, specs) {
    const out = {};
    for (const [name, val] of Object.entries(raw(game))) {
      const spec = specs && specs[name];
      if (!spec) continue;
      if (spec.type === "enum" || (spec.choices && spec.choices.length)) {
        const c = (spec.choices || []).find((x) => String(x).toLowerCase() === String(val).toLowerCase());
        if (c != null) out[name] = String(c);
      } else if (spec.type === "text" || spec.type === "flags") {
        out[name] = String(val);
      } else {
        out[name] = String(Ships.clamp(val, spec));
      }
    }
    return out;
  }

  // ---- summary --------------------------------------------------------------------------------
  const WORDS = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
  const word = (n) => (n >= 0 && n <= 10 ? WORDS[n] : String(n));
  const num = (n) => Number(n).toLocaleString("en-US");
  function dur(sec) {
    sec = Math.round(+sec || 0);
    if (sec % 60 === 0 && sec >= 60) return `${sec / 60} minute${sec === 60 ? "" : "s"}`;
    return `${sec} second${sec === 1 ? "" : "s"}`;
  }
  const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
  const PACE = { slow: "Slow and deliberate", steady: "Steady", standard: "Standard", fast: "Fast", mayhem: "Crazy mayhem" };

  /** A few plain sentences describing the game mode. */
  function summary(game, ships) {
    const g = withDefaults(game);
    const t = g.teams, s = g.scoring, f = g.flags;
    const parts = [];
    const count = Math.round(+t.count || 0);
    let teams = count ? `${cap(word(count))} teams of up to ${t.perTeam}` : "Everyone for themselves";
    if (count && t.even) teams += ", kept even";
    const banned = Ships.SHIPS.filter((_, i) => !((+t.ships >> i) & 1));
    if (banned.length) teams += `, no ${banned.join(" or ")}`;
    parts.push(teams + ".");
    const win = num(s.winReward);
    switch (g.objective) {
      case "carryall": parts.push(`Carry all ${f.count} flags for ${dur(f.holdSec)} to win: ${win} points.`); break;
      case "ownall": parts.push(`Own all ${f.count} dropped flags for ${dur(f.holdSec)} to win: ${win} points.`); break;
      case "rabbit": parts.push(`One flag: kill the carrier to take it; hold it for ${dur(f.holdSec)} to win ${win} points.`); break;
      case "turf": parts.push(`Turf: ${num(s.turfPoints)} points for each flag held, every ${dur(s.turfEverySec)}.`); break;
      case "powerball": parts.push(`Powerball with ${word(+g.ball.count)} ball${+g.ball.count === 1 ? "" : "s"}: ${num(s.goalReward)} points a goal.`); break;
      case "king": parts.push(`King of the hill: the last crowned pilot wins.`); break;
      default: parts.push("Just fight.");
    }
    parts.push(s.kill === "fixed" ? `Kills score ${num(s.fixedKill)} points.` : "Kills score the killed pilot's bounty.");
    const min = Math.round(+g.time.minutes || 0);
    parts.push(min ? `${min}-minute rounds; nothing happens at zero yet.` : "No time limit.");
    if (ships) parts.push(`${PACE[ships.pace] || "Standard"} pace, ${ships.items || "standard"} items.`);
    return parts.join(" ");
  }

  return { OBJECTIVES, GOAL_LAYOUTS, defaults, withDefaults, usesFlags, usesBall, attach, settings, summary };
});
