# Game mode — Phase 2: the wizard and the game panels (draft for Chris's approval)

Date: 2026-11-28 · Branch `claude/game-mode-wizard-904376` · Parent: `2026-11-27-game-mode-wizard-design.md`
(approved) · Phase 1 (game modes and the Ships panel) is live (8fb298c).
Mockup: `docs/design/game-mode-phase2-mockup.html`.

## What this phase delivers

1. **The opening question.** Making a game mode asks "Is this your first time?":
   - **Yes:** one step at a time with pictures and defaults.
   - **No:** every step on one page.
   - **I know what I'm doing:** the tabbed editor (what Phase 1 shows), with every panel open.
2. **Five new panels:** Objective, Teams, Scoring, Time and Flags / ball. They cover every game the
   server already runs. They sit next to Ships, and the walkthrough goes through all six panels.
3. **A plain summary** at the end of the walkthrough and at the top of every game mode, for example:
   "Two teams of up to 8. Carry all 6 flags for 3 minutes to win: 5,000 points. Kills score.
   30-minute rounds."
4. **Map checks.** The wizard reads the arena's map and warns when a game needs pieces the map
   doesn't have, e.g. "Turf needs flags on the map, and this map has none. Place them in the map
   editor" with a button that opens it.
5. **Scoring that actually runs in owner arenas** (the one server change, below).

## A finding that shapes this phase

Points for kills, flag-game wins, goals and king of the hill come from server modules that must be
*attached* to an arena. They're listed in the arena's `Modules:AttachModules`. Owner arenas are
copied from `deploy/arenas/_template/arena.conf`, which attaches **none** of them, and owners
can't edit the `Modules` section (`SettingsPolicy`).

So **today no owner arena awards points for kills, flag wins, goals or king of the hill**, and no
wizard setting could turn them on.

**Fix (needs the plugin, so a zone restart when deployed):**
- A new owner-editable setting `GameMode:Attach` takes short words:
  - `kills`: KillPoints, points for kills.
  - `flags`: FlagGamePoints, win points for flag games.
  - `balls`: BallGamePoints, goal points.
  - `king`: Koth, king of the hill.
  - `ships`: LegalShip, which ships are allowed.
  - `shipchange`: ShipChange, a ship-change cooldown.
- The plugin attaches exactly those modules to the arena when it's created or its settings change,
  and detaches ones that were removed. Only modules on that fixed list can ever be attached.
- The owner never types module names, and the `Modules` section stays closed.

**My calls** (change if you prefer):
- New arenas made from the template get `GameMode:Attach = kills`, so kills score like in every
  other zone.
- Existing owner arenas are left alone until their owner applies a game mode.

## The opening question

- It appears when you make a new game mode, and on the empty Game mode tab.
- The answer is kept in the game mode, as `data.wizard.experience`.
- A "Walk me through it" link switches any game mode to the step-by-step view.
- First-timers and "No" both end on a **Review** step: the summary, the settings count and Apply.

Steps, in order:

1. Objective
2. Teams
3. Scoring
4. Time
5. Flags / ball (skipped when the objective uses neither)
6. Ships
7. Review

## Objectives

Only games the server runs today can be picked. The other three appear with a "Coming with rules"
badge (Phase 4) so creators see what's planned:

| Objective | What players do | Main settings it sets |
|---|---|---|
| **Just fight** | team or free-for-all deathmatch | `Flag:FlagCount 0`, `Soccer:BallCount 0`, attach `kills` |
| **Carry all flags** (running / jackpot) | a team carrying every flag for a while wins | `Flag:CarryFlags 1`, `Flag:FlagMode 0`, `FlagCount`, `WinDelay`, `FlagReward`, attach `kills flags` |
| **Own all flags** (warzone) | drop flags; a team owning every dropped flag wins | `Flag:CarryFlags 1`, `Flag:FlagMode 1`, `DropOwned 1`, `FlagCount`, `WinDelay`, `FlagReward`, attach `kills flags` |
| **Rabbit** | one flag; kill the carrier to take it; hold it to win | `Flag:CarryFlags 1`, `FlagCount 1`, `FlagMode 0`, `WinDelay` = hold time, `FlaggerOnRadar 1`, attach `kills flags` |
| **Turf** | claim flags fixed on the map; points every interval per flag held | `Flag:CarryFlags 0` (static flags = flag tiles on the map), `Periodic:RewardDelay`, `RewardPoints`, `PersistentTurfOwners`, attach `kills` |
| **Powerball** | carry or shoot the ball into the other team's goal | `Soccer:BallCount`, `Soccer:Mode` (goal layout), `CapturePoints`, `WinBy`, `Reward`, `Flag:FlagCount 0`, attach `kills balls` |
| **King of the hill** | everyone wears a crown; last crowned pilot wins | `King:DeathCount`, `ExpireTime`, `RewardFactor`, `MinPlayers`, attach `kills king` |
| *Capture the flag* (return it to your base) | coming with rules | needs per-flag spots, bases and capture scoring (Phase 3 labels + Phase 4 module) |
| *Hold the base* | coming with rules | needs base areas (Phase 3) and area control (Phase 4) |
| *Lives duel* (e.g. 4v4, 10 lives) | coming with rules | needs lives (Phase 4) |

## Panels

**Teams**
- Number of teams (`Team:DesiredTeams`), or free-for-all.
- Players per team (`Team:MaxPerTeam`).
- Keep teams even (`Team:ForceEvenTeams`, `MaxTeamDifference`).
- Allow private teams: `Team:MaxFrequency` set to the team count when private teams are off.
- Spectators pick their own team or are placed.
- Which ships may fly: eight toggles, written as `LegalShip:ArenaMask`, attach `ships`.
- Ship-change cooldown: `Misc:ShipChangeInterval`, attach `shipchange`.
- Lives: a row marked "coming with rules".

**Scoring**
- Points for a kill: the killed pilot's bounty, or a fixed number (`Kill:FixedKillReward`).
- Team kills score? (`Misc:TeamKillPoints`).
- A flag-game win reward (`Flag:FlagReward`, `Flag:SplitPoints`).
- Turf points per flag and interval (`Periodic:RewardPoints`, `RewardDelay`).
- Goal reward (`Soccer:Reward`, `CapturePoints`, `WinBy`).
- King reward (`King:RewardFactor`).
- Only the rows that fit the chosen objective are shown.
- "First to N points" is marked "coming with rules": the server has no such ending today, except
  goals in powerball.

**Time**
- A time limit, written as `Misc:TimedGame` (shown in minutes, stored in ticks).
- Warnings at chosen seconds (`Misc:TimerWarnings`, e.g. 60, 30, 10).
- Players see the countdown with `?time`.
- "When time runs out" lists the endings (points to the leader, restart, warp to spawn) marked
  "coming with rules". **The server does nothing when the timer ends today.** I checked: no module
  reacts to it, not even powerball. The page says so plainly rather than hiding it.

**Flags / ball** (shown when the objective uses them)
- Flags:
  - how many (`Flag:FlagCount`, also as a range like 6–8);
  - where they appear (`Flag:SpawnX/Y/Radius`; centre of the map by default, set from the map in Phase 3);
  - dropped flags go near the carrier or back to the centre (`DropCenter`);
  - carrier speed, damage and radar adjustments (`Flag:Flagger*`);
  - auto-drop after N seconds (`FlagDropDelay`).
- Ball:
  - how many balls, 1–8;
  - goal layout (`Soccer:Mode`), shown as small pictures: left vs right, top vs bottom, four corners
    and so on;
  - ball bounces off walls; carrier can shoot guns or bombs; pass delay;
  - the ball carrier uses the flag-carrier adjustments.
- Map checks: flag tiles for turf, goal tiles for powerball. The count comes from the arena's own
  map, read with `/api/map` and `Lvl.parse`.

## How it fits with Phase 1

- The new panels are written as one more pure module, `gamemode-rules.js`, like
  `gamemode-ships.js`: `GameModeRules.settings(data.game, specs)` → `Section:Key` strings.
- The game mode's settings are the Ships output plus the game output. The game output wins where
  both set a key: only `Flag:Flagger*` overlaps.
- The same clamping applies, and node tests cover every objective × team choice × time choice
  staying within spec.
- `GameMode:Attach` is computed from the objective plus the Teams panel's ship choices.
- Saving and applying are unchanged: autosave, then the preview dialog.
- One new thing in the preview: when `GameMode:Attach` changes, the dialog says "Takes effect when
  the arena restarts", and the pending-restart note offers Restart, as the Settings tab does today.

## Out of this phase

- Labelling bases and flag spots on the map: Phase 3.
- Timer endings, lives, capture-the-flag, hold-the-base and "first to N": Phase 4.
- Timers on screen and the screen designer: Phase 5.
- Ready-made starting points (Classic CTF, 4v4 duel…): Phase 6.

## Server work (plugin)

- **`GameModeAttach`** (in `src/Dare.Arenas`), the per-arena attach and detach:
  - Allow-list: `kills` → `SS.Core.Modules.Scoring.KillPoints`, `flags` → `…FlagGamePoints`,
    `balls` → `…BallGamePoints`, `king` → `…Koth`, `ships` → `SS.Core.Modules.Enforcers.LegalShip`,
    `shipchange` → `…ShipChange`.
  - It reacts to the arena being created and to its settings changing, and uses the server's own
    module manager to attach or detach.
  - Unknown words are ignored and logged.
  - It never touches `Modules:AttachModules`, so staff arenas keep working as they are.
- **`GameMode:Attach` documented with a config help attribute,** so it appears in the editable
  list. Its value check allows only the words above.
- **Template:** `deploy/arenas/_template/arena.conf` gains `GameMode:Attach = kills`.
- **Tests:**
  - parsing the words (unknown words, duplicates, case);
  - which modules are attached or detached for a change from one list to another;
  - the value check refuses anything off the list.
  - Then a local zone check that a kill gives points only after `kills` is attached.

## Tests for the page

- `node --test`:
  - every objective's settings stay within spec;
  - "Just fight" turns flags and balls off;
  - Rabbit is one flag;
  - Turf uses static flags;
  - powerball goal layouts map to the right `Soccer:Mode` names;
  - `GameMode:Attach` holds exactly the right words;
  - minutes ↔ ticks round-trip.
- Browser, local zone:
  - the walkthrough from "Yes" to Apply for each objective;
  - the map warning on a map with no flags;
  - fly a carry-flag game with two test pilots and see the win reward. Bots can be the second
    pilot if a human isn't around.
