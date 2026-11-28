# Game mode Phase 2 (wizard + game panels + GameMode:Attach) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Owners walk through "Is this your first time?" and the Objective, Teams, Scoring, Time and Flags / ball panels, and get a working game in their arena. That includes scoring, which owner arenas can't switch on today.

**Architecture:**
- A pure C# helper in `Dare.Arenas.Core` turns `GameMode:Attach` into module type names and an attach/detach diff.
- A plugin part (`ArenaOwnership.GameMode.cs`) applies that diff when an arena is created or its config changes.
- A pure JS module, `gamemode-rules.js`, mirrors `gamemode-ships.js` for the game panels.
- `gamemode.js` gains the opening question, the step and one-page views, the five panels, map checks and the Review.

**Tech Stack:** C# (.NET, xUnit) in `src/Dare.Arenas.Core` and `src/Dare.Arenas`; plain browser JS in `src/Dare.Arenas/Web/wwwroot`; `node --test tests/js/*.test.js` (Node 25 needs the glob, not the folder).

**Spec:** `docs/superpowers/specs/2026-11-28-game-mode-phase2-wizard-design.md` (approved 2026-11-28). Parent: `2026-11-27-game-mode-wizard-design.md`. Phase 1 plan: `2026-11-27-game-mode-phase1-ships.md`.

## Global Constraints

- `GameMode:Attach` is the only way a game mode turns modules on.
  - The allow-list: Kills → `SS.Core.Modules.Scoring.KillPoints`, Flags → `SS.Core.Modules.Scoring.FlagGamePoints`, Balls → `SS.Core.Modules.Scoring.BallGamePoints`, King → `SS.Core.Modules.Scoring.Koth`, Ships → `SS.Core.Modules.Enforcers.LegalShip`, ShipChange → `SS.Core.Modules.Enforcers.ShipChange`.
  - `Modules:AttachModules` stays closed to owners and is never written.
  - Modules attached by `Modules:AttachModules` are never detached by us.
- **Value format (ruling, against the spec's "short words").** `GameMode:Attach` is a `[Flags]` enum setting, so the existing server value check (`SettingSpec` Flags) accepts only those names. The stored form is comma-separated names, e.g. `Kills, Flags`, or a number.
- **No restart (ruling).** Attach and detach on `ArenaAction.Create` and `ArenaAction.ConfChanged`, so a change takes effect at once. No restart note is needed for it.
- The template `deploy/arenas/_template/arena.conf` gets `[ GameMode ]` `Attach = Kills`. Existing arenas are untouched until a game mode is applied.
- Times on the page are seconds and minutes. Settings are stored in ticks (1/100 s): `Misc:TimedGame`, `Flag:WinDelay`, `Periodic:RewardDelay`, `Misc:ShipChangeInterval`, `Flag:FlagDropDelay`.
- Only objectives the server runs are pickable. `ctf`, `holdbase` and `lives` show "Coming with rules" and can't be chosen.
- The game mode's settings are `{...GameModeShips.settings(ships), ...GameModeRules.settings(game)}`: game wins on overlap. Only keys with a spec are sent. Ints and bools are clamped as in Phase 1. Enum values must be one of the spec's choices or are left out. Text (`Misc:TimerWarnings`) and flags (`GameMode:Attach`) are passed as built.
- `data.version` stays 1. New fields are `data.game` and `data.wizard.experience` (`"first" | "some" | "expert"`), with missing fields filled from defaults (like Phase 1's `open()`).
- No `alert`/`confirm`/`prompt`: use `Dlg.*`. All text goes in with `textContent`/`el({text})`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A staff arena that already lists KillPoints in `Modules:AttachModules` also gets `GameMode:Attach = Kills`, then removes it.** KillPoints must stay attached. Pinned in Task 1 (`never_detaches_staff_attached`).
2. **`GameMode:Attach` typed by hand in `?arenaset` as `kills flags` (spaces, lower case) or `Kills,Bogus`.** The server value check refuses it with the names listed, and the plugin's own parse ignores unknown words and logs them. Pinned in Task 1 (`parse_ignores_unknown_and_accepts_bits`).
3. **Powerball goal layout on an arena whose `Soccer:Mode` spec offers different choice names.** A layout outside the spec's choices isn't sent. Pinned in Task 3 (`enum_value_outside_choices_is_left_out`).
4. **Switching objective from Turf to Just fight.** Static-flag and periodic settings go back to off, so no leftover turf points. Pinned in Task 3 (`fight_turns_flags_balls_and_periodic_off`).
5. **An old Phase 1 game mode (no `data.game`) opened after this ships.** It opens with defaults, the Ships values are unchanged, and saving adds `game`. Pinned in Task 4 (browser step) and Task 3 (`defaults_fill_missing_game_fields`).

---

### Task 1: GameMode:Attach parsing and the attach/detach diff (Core)

**Files:**
- Create: `src/Dare.Arenas.Core/GameModeAttach.cs`
- Test: `tests/Dare.Arenas.Tests/GameModeAttachTests.cs`

**Interfaces:**
- Produces:
  - `[Flags] public enum GameModeModules { None = 0, Kills = 1, Flags = 2, Balls = 4, King = 8, Ships = 16, ShipChange = 32 }`
  - `public static class GameModeAttach`
    - `GameModeModules Parse(string? value, out IReadOnlyList<string> unknown)`: comma, space, tab or semicolon separated, case-insensitive names, or a decimal bit number; unknown words are collected.
    - `IReadOnlyList<string> TypeNames(GameModeModules m)`: the allow-list type names, in enum order.
    - `(IReadOnlyList<string> Attach, IReadOnlyList<string> Detach) Diff(IReadOnlySet<string> attachedByUs, GameModeModules wanted, IReadOnlySet<string> staffAttached)`
      - Attach = wanted types that aren't in `attachedByUs` and aren't in `staffAttached`.
      - Detach = `attachedByUs` minus wanted, never in `staffAttached`.
      - Type names compare ordinally ignoring case.
    - `IReadOnlySet<string> StaffAttached(string? attachModules)`: the type names in a `Modules:AttachModules` value, split on whitespace, `:` and `;` like the server does.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void parse_names_any_case_and_separator() =>
    Assert.Equal(GameModeModules.Kills | GameModeModules.Flags, GameModeAttach.Parse("kills, FLAGS", out _));
[Fact] public void parse_ignores_unknown_and_accepts_bits()
{
    Assert.Equal(GameModeModules.Kills, GameModeAttach.Parse("Kills Bogus", out var unknown));
    Assert.Equal(["Bogus"], unknown);
    Assert.Equal(GameModeModules.Balls | GameModeModules.King, GameModeAttach.Parse("12", out _));
    Assert.Equal(GameModeModules.None, GameModeAttach.Parse(null, out _));
    Assert.Equal(GameModeModules.None, GameModeAttach.Parse("999", out _)); // bits outside the list: nothing
}
[Fact] public void type_names_follow_the_allow_list() =>
    Assert.Equal(["SS.Core.Modules.Scoring.KillPoints", "SS.Core.Modules.Enforcers.ShipChange"],
        GameModeAttach.TypeNames(GameModeModules.Kills | GameModeModules.ShipChange));
[Fact] public void diff_attaches_new_and_detaches_removed()
{
    var ours = new HashSet<string>(["SS.Core.Modules.Scoring.KillPoints", "SS.Core.Modules.Scoring.Koth"]);
    var (a, d) = GameModeAttach.Diff(ours, GameModeModules.Kills | GameModeModules.Flags, new HashSet<string>());
    Assert.Equal(["SS.Core.Modules.Scoring.FlagGamePoints"], a);
    Assert.Equal(["SS.Core.Modules.Scoring.Koth"], d);
}
[Fact] public void never_detaches_staff_attached()
{
    var staff = GameModeAttach.StaffAttached("SS.Core.Modules.Scoring.KillPoints \\\n\tSS.Core.Modules.Buy");
    var (a, d) = GameModeAttach.Diff(new HashSet<string>(), GameModeModules.Kills, staff);
    Assert.Empty(a);   // already attached by staff
    (a, d) = GameModeAttach.Diff(new HashSet<string>(["SS.Core.Modules.Scoring.KillPoints"]), GameModeModules.None, staff);
    Assert.Empty(d);
}
```

- [ ] **Step 2:** `dotnet test tests/Dare.Arenas.Tests --filter GameModeAttachTests`. Expected: build error, the type doesn't exist.
- [ ] **Step 3:** Implement `GameModeAttach.cs`.
- [ ] **Step 4:** Rerun. Expected: 5 passed. Then `dotnet test` passes.
- [ ] **Step 5:** Commit "Game modes: GameMode:Attach allow-list, parse and attach/detach diff".

### Task 2: Plugin applies GameMode:Attach; template default

**Files:**
- Create: `src/Dare.Arenas/ArenaOwnership.GameMode.cs` (partial `ArenaOwnership`)
- Modify:
  - `src/Dare.Arenas/ArenaOwnership.cs`: get `IModuleManager` in Load and register/unregister the callback (follow how Restarts.cs registers `ArenaActionCallback`).
  - `deploy/arenas/_template/arena.conf`: add `[ GameMode ]` and `Attach = Kills`.

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `[ConfigHelp<GameModeModules>("GameMode", "Attach", ConfigScope.Arena, Default = GameModeModules.None, Description = "Scoring and rules this arena runs: Kills, Flags, Balls, King, Ships, ShipChange (names separated by commas).")]` on the method that reads it, so `DocumentedArenaSettings` lists it, `SettingsPolicy` allows it, and `SpecOf` gives a Flags spec.
  - `void Callback_ArenaActionGameMode(Arena arena, ArenaAction action)`:
    - On Create and ConfChanged: read `GameMode:Attach` and `Modules:AttachModules` from `arena.Cfg`, compute `Diff` against `_gameModeAttached[arena.BaseName]`, then call `_moduleManager.AttachModuleAsync(type, arena)` / `DetachModuleAsync` for each. Record what succeeded, and log unknown words once per arena.
    - On Destroy: forget the arena.
  - `private readonly Dictionary<string, HashSet<string>> _gameModeAttached`: keyed by arena name (with `#` for drafts), touched on the mainloop only.

- [ ] **Step 1:** Implement. `AttachModuleAsync` returns a Task: await it inside an `async void` handler wrapped in try/catch with a log, or follow whatever pattern the plugin already uses for async work started from a callback. Check that `ArenaAction.ConfChanged` fires when the plugin's own settings writes land. If it doesn't, call the same apply method from the settings-write paths after a successful write, and ledger the ruling.
- [ ] **Step 2:** `dotnet build`. Expected: 0 errors. `dotnet test` passes.
- [ ] **Step 3: Local zone check.** Use scratchpad `restart.sh`, ports 6460/8196, account GmTester, draft `#gmtest`.
  - `?arenaset GameMode:Attach Kills` through the web settings API: the zone log shows KillPoints attached to `#gmtest`.
  - Set it to `Kills, Flags`: FlagGamePoints is attached.
  - Clear it: both are detached.
  - Post `GameMode:Attach = kills flags` to `/api/settings/preview`: it's a skipped row naming the allowed values.
  - A new draft gets `Attach = Kills` from the template.
  - Paste the log lines into the ledger.
- [ ] **Step 4:** Commit "Game modes: plugin attaches scoring modules from GameMode:Attach (allow-list); template attaches Kills".

### Task 3: Game rules maths (`gamemode-rules.js`)

**Files:**
- Create: `src/Dare.Arenas/Web/wwwroot/gamemode-rules.js` (UMD, global `GameModeRules`, served like `gamemode-ships.js`: add it to `GameModeFiles` and the static route in `WebServer.cs`)
- Test: `tests/js/gamemode-rules.test.js`

**Interfaces:**
- Produces (on `GameModeRules`):
  - `OBJECTIVES`: `[{ id, name, blurb, soon? }]`. Ids: `fight, carryall, ownall, rabbit, turf, powerball, king`, plus `ctf, holdbase, lives` with `soon: true`.
  - `GOAL_LAYOUTS`: `[{ id, name }]`. Ids are the `Soccer:Mode` names `LeftRight, TopBottom, All, QuadrantsDefend1, QuadrantsDefend3, SidesDefend1, SidesDefend3`.
  - `defaults()` → `{ objective: "fight", teams: { count: 2, perTeam: 8, even: true, private: false, ships: 255, shipChangeSec: 5 }, scoring: { kill: "bounty", fixedKill: 10, teamKills: false, winReward: 5000, split: false, turfPoints: 100, turfEverySec: 60, goalReward: 1000, kingReward: 1000 }, time: { minutes: 0, warnings: [60, 30, 10] }, flags: { count: 6, holdSec: 180, dropCenter: false, dropAfterSec: 0, carrierOnRadar: true, carrierSpeed: 0 }, ball: { count: 1, layout: "LeftRight", bounce: true, guns: true, bombs: false, winBy: 0 }, king: { deaths: 0, minPlayers: 2 } }`
  - `withDefaults(game)` → a deep merge of `game` over `defaults()`. Unknown objective → `"fight"`; a `soon` objective → `"fight"`.
  - `usesFlags(game)`, `usesBall(game)` → booleans (flags: carryall, ownall, rabbit, turf; ball: powerball).
  - `attach(game)` → names in enum order. `Kills` always; `Flags` for carryall, ownall and rabbit; `Balls` for powerball; `King` for king; `Ships` when `teams.ships !== 255`; `ShipChange` when `teams.shipChangeSec > 0`.
  - `settings(game, specs)` → `{ "Section:Key": "value" }`, only keys with a spec. Per objective (all ticks = seconds × 100):
    - **every objective:** `Team:DesiredTeams = count || 1`, `Team:MaxPerTeam = count ? perTeam : 1`, `Team:ForceEvenTeams = even ? 1 : 0`, `Team:MaxFrequency = private ? 10000 : max(count, 1)`, `LegalShip:ArenaMask = ships`, `Misc:ShipChangeInterval = shipChangeSec*100`, `Kill:FixedKillReward = kill === "fixed" ? fixedKill : -1`, `Misc:TeamKillPoints = teamKills ? 1 : 0`, `Misc:TimedGame = minutes*6000`, `Misc:TimerWarnings = warnings.join(",")`, `GameMode:Attach = attach(game).join(", ") || "None"`.
    - **fight:** `Flag:FlagCount 0`, `Soccer:BallCount 0`, `Periodic:RewardDelay 0`, `Flag:FlagMode -1`.
    - **carryall / ownall / rabbit:** `Flag:CarryFlags 1`, `Flag:FlagMode` (0 for carryall and rabbit, 1 for ownall), `Flag:FlagCount` (count, or 1 for rabbit), `Flag:WinDelay holdSec*100`, `Flag:FlagReward winReward`, `Flag:SplitPoints`, `Flag:DropOwned 1`, `Flag:DropCenter`, `Flag:FlagDropDelay dropAfterSec*100`, `Flag:FlaggerOnRadar`, `Flag:FlaggerSpeedAdjustment carrierSpeed`, `Soccer:BallCount 0`, `Periodic:RewardDelay 0`.
    - **turf:** `Flag:CarryFlags 0`, `Flag:FlagMode -1`, `Periodic:RewardDelay turfEverySec*100`, `Periodic:RewardPoints turfPoints`, `Flag:PersistentTurfOwners 1`, `Soccer:BallCount 0`.
    - **powerball:** `Soccer:BallCount count`, `Soccer:Mode layout`, `Soccer:Reward goalReward`, `Soccer:WinBy winBy`, `Soccer:BallBounce`, `Soccer:AllowGuns`, `Soccer:AllowBombs`, `Flag:FlagCount 0`, `Flag:FlagMode -1`, `Periodic:RewardDelay 0`.
    - **king:** `King:DeathCount deaths`, `King:MinPlayers minPlayers`, `King:RewardFactor kingReward`, `King:AutoStart 1`, `Flag:FlagCount 0`, `Soccer:BallCount 0`, `Periodic:RewardDelay 0`.
  - `summary(game, ships)` → one plain sentence or a few, e.g. "Two teams of up to 8, kept even. Carry all 6 flags for 3 minutes to win: 5,000 points. Kills score the killed pilot's bounty. 30-minute rounds; nothing happens at zero yet." Ships adds "Fast pace, standard items." from `ships.pace` and `ships.items`.

- [ ] **Step 1: Write the failing tests**:
  - `fight_turns_flags_balls_and_periodic_off`
  - `rabbit_is_one_carried_flag_with_hold_time`: `Flag:FlagCount "1"`, `Flag:WinDelay "18000"` at holdSec 180.
  - `turf_uses_static_flags_and_periodic`
  - `ownall_sets_flag_mode_1`
  - `powerball_layout_goes_to_soccer_mode`
  - `enum_value_outside_choices_is_left_out`: spec `Soccer:Mode` with choices `["LeftRight"]` and layout `TopBottom`; the key is absent.
  - `attach_words_per_objective`: fight → "Kills"; carryall with ships 127 and shipChangeSec 5 → "Kills, Flags, Ships, ShipChange"; powerball → "Kills, Balls"; king → "Kills, King".
  - `minutes_to_ticks`: 30 min → `Misc:TimedGame "180000"`.
  - `free_for_all_is_one_per_team`
  - `private_teams_off_caps_max_frequency`
  - `soon_objectives_fall_back_to_fight`
  - `defaults_fill_missing_game_fields`: `withDefaults({ objective: "rabbit" }).flags.holdSec === 180`.
  - `every_objective_stays_within_spec`: loop objectives × team counts {0, 2, 4} × minutes {0, 30}, with specs from a helper giving int min/max.
  - `summary_mentions_objective_and_time`: contains "flags" and "30-minute".
- [ ] **Step 2:** `node --test tests/js/gamemode-rules.test.js`. Expected: fails with "Cannot find module".
- [ ] **Step 3:** Implement. Reuse `GameModeShips.clamp` (require it in node; read the global in the browser).
- [ ] **Step 4:** `node --test tests/js/*.test.js`. Expected: all pass.
- [ ] **Step 5:** Commit "Game modes: objective, teams, scoring, time, flags and ball as settings (gamemode-rules.js)".

### Task 4: The wizard and panels in the Game mode tab

**Files:**
- Modify:
  - `src/Dare.Arenas/Web/wwwroot/gamemode.js`
  - `src/Dare.Arenas/Web/wwwroot/arena.html`: add the `gamemode-rules.js` script before `gamemode.js`.
  - `src/Dare.Arenas/Web/wwwroot/style.css`: `.gm*` additions.

**Interfaces:**
- Consumes: `GameModeRules` (Task 3), `GameModeShips`, `/api/map?arena=` with `Lvl.parse` from `lvl.js` (add the script to arena.html if it isn't there; it is, line 13), and the existing `openDiff`.
- Produces:
  - `computed()` becomes `{...GameModeShips.settings(ships, specs), ...GameModeRules.settings(game, specs)}`.
  - `create()` asks the opening question first with `Dlg.choose("Is this your first time making a game mode?", { choices: [{value:"first",label:"Yes"},{value:"some",label:"No"},{value:"expert",label:"I know what I'm doing"}] })`, then the name. It stores `data.wizard.experience`.
  - Views by `data.wizard.experience`:
    - `first`: one step at a time with Back/Next and a step bar.
    - `some`: every step on one page.
    - `expert`: the Phase 1 tabs, now all enabled.
  - A "Walk me through it" link sets `first`; a "Show all as tabs" link sets `expert`.
  - Steps:
    1. Objective (cards, soon cards disabled with the badge)
    2. Teams
    3. Scoring (rows for the objective)
    4. Time (end-of-time choices disabled, labelled "coming with rules", with the plain sentence "The server does nothing when the timer ends today.")
    5. Flags / ball (only if `usesFlags` or `usesBall`)
    6. Ships (the existing panel)
    7. Review (summary, settings count, Save as settings set, Apply)
  - The summary line also shows above the panels in every view.
  - Each step has a "What this changes (N settings)" `details` listing that step's keys.
  - Map check: on show, fetch `/api/map?arena=<current.arena>`, parse, and count tiles 170 (flag) and 172 (goal).
    - Turf with 0 flags → warning plus "Open the map editor" (link `/arena/map?arena=…`).
    - Powerball with 0 goals → warning.
    - Powerball with goals → "This map has N goals."
    - If the map can't be read, show nothing.

- [ ] **Step 1:** Build it. Keep Phase 1's autosave, flush and `pagehide` code as is. `open()` fills `data.game` with `GameModeRules.withDefaults` and `data.wizard` with `{ experience: "expert" }` when missing, so existing modes open as before.
- [ ] **Step 2: Browser check on the local zone:**
  - an existing Phase 1 mode opens, with Ships values unchanged;
  - a new mode as "Yes" goes step by step for every objective;
  - soon cards can't be picked;
  - Review shows a sensible summary;
  - Apply → preview → applied, and `arena.conf` has `GameMode:Attach`, `Flag:*` and `Team:*` as expected;
  - the turf map warning appears on a map without flag tiles;
  - "No" shows one page, "expert" shows the tabs;
  - at 375 px there's no horizontal scroll.
- [ ] **Step 3:** In game on the local zone with two pilots (two clients, or one client plus a bot from the Bots tab), check that a kill gives points after a game mode is applied, and none before on a fresh draft whose Attach was cleared. If no client can run in this session, ledger it as a ruling for Chris.
- [ ] **Step 4:** Commit "Game mode tab: the wizard (first time? yes/no/expert), Objective/Teams/Scoring/Time/Flags panels, map checks, Review".

### Task 5: Review, merge, push

- [ ] **Step 1:** `dotnet test` and `node --test tests/js/*.test.js`: both pass.
- [ ] **Step 2:** Final whole-branch review (fresh reviewer), with a fix pass for Critical and Important findings, each test-first.
- [ ] **Step 3:** `git merge origin/main`, retest, `git push origin HEAD:main` (fast-forward), and push the branch. Don't deploy; ask Chris (the plugin change means a zone restart).
