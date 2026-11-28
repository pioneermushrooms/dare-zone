# Game Mode Wizard — phased design (draft for Chris's approval)

Date: 2026-11-27 · Repo: dare-zone (branch `claude/game-mode-wizard-904376`) · Status: **draft**.
Nothing is built yet. Each phase below gets its own short design, which Chris approves before
it is built.

## Why

The arena editor (`/arena/me`, map editor `/arena/map`) lets an owner change any setting, draw
a map and upload graphics. It doesn't help them make a *game*: a new owner has to know that
"capture the flag" means `Flag:CarryFlags`, `Flag:FlagMode`, `Flag:WinDelay`, `Flag:FlagReward`
and a dozen more. Chris wants the editor to become a **game mode editor**: an owner says what
game they want, in game words, and the editor sets the settings, places the pieces on the map,
puts timers on the map or screen, and saves it all as one game mode they can apply, share and
change.

Success: a first-time owner with a map answers about ten questions and flies a working CTF or
4v4 lives duel within fifteen minutes, with a countdown on screen that restarts when a flag is
grabbed. An expert can skip straight to any panel and fine-tune everything.

## Goals

1. **Game Mode Wizard.** Opens with "Is this your first time? Yes / No / I know what I'm doing".
   - *Yes*: one question per step, plain words, a picture per choice, sensible defaults.
   - *No*: the same steps on one scrolling page, defaults filled in.
   - *I know what I'm doing*: straight to the full game mode editor (all panels, no walkthrough).
2. **Ship wizard.** Eight ships, each given a role that fills in its values; overall pace,
   item amount, upgrades (greens) and starting items. Every value stays editable.
3. **Map labeller.** Mark bases, flag spots, ball spawns, goals and spawn points on the map;
   the game mode refers to them by name ("Red base", "Flag 2").
4. **Timers and rules.** A timer has a design (how it looks), a place (on the map or the
   screen), what starts/restarts it (a flag is grabbed, all flags held, a kill makes a new
   rabbit, a goal, the game starts) and what happens when it runs out (award points, end the
   game, restart it, warp everyone to spawn, reset flags/balls).
5. **Screen designer and map objects.** A left-hand menu of tools and objects (timers, score
   boards, banners, images) dragged onto the map or onto a simulated player screen.
6. **One saved game mode.** Settings, ship roles, labels, timers, rules and generated graphics
   are one named game mode per pilot, applied to any arena they can edit.

## Non-goals

- Changing Continuum. Everything must work for Continuum players through settings, standard
  map tiles and standard `.lvz` objects the server toggles.
- A scripting language. Rules are picked from menus (trigger → action), not written.
- Matchmaking queues and leagues (SubspaceServer's `TeamVersusMatch` exists; using it is later).
- Per-pilot stats pages for game modes.

## Anti-goals

- A wizard that hides settings: every choice shows which settings it changed, and the full
  editor can change them.
- A game mode that only works in the Dare client. If something can't be shown in Continuum,
  the wizard doesn't offer it.
- A second settings store or a second LVZ writer next to the ones other sessions are building.
- Rules that can hurt the zone: no unbounded timers, no rule fires more than a few times a
  second, every action is scoped to its own arena.

## What already exists (checked in SubspaceServer v4 and dare-zone `main`)

| Need | What's there | Gap |
|---|---|---|
| Carry flags, win by carrying all (running, jackpot, **rabbit** = 1 flag) | `CarryFlags` + `FlagGamePoints`, `Flag:FlagMode=0`, `Flag:CarryFlags`, `Flag:WinDelay`, `FlagCount`, kill transfers flags | none |
| Own all dropped flags (warzone) | `Flag:FlagMode=1` | none |
| Static flags / turf (map tile 170) | `StaticFlags`, `PeriodicReward` (points per owned flag on an interval) | none |
| Flag spawn place | one spot for all flags: `Flag:SpawnX/Y/Radius` | **per-flag spots** need a custom `ICarryFlagBehavior` (the server lets a module replace flag behaviour per arena) |
| CTF capture-and-return to your own base | — | **new** (custom flag behaviour + base areas) |
| Hold a base / king of the hill area | `Koth` (crowns, last pilot standing, not an area) | **new** (area control) |
| Powerball | `Balls` (1–8 balls, per-ball `Soccer:SpawnX1..`), `BallGamePoints` (`CapturePoints`, `WinBy`, `Reward`), `Soccer:Mode` goal ownership, goal tile 172 | none |
| Game time limit | `GameTimer`: `Misc:TimedGame`, `TimerWarnings`, `?timer` | Nothing in the server acts when it ends (`GameTimerEnded` has no listener): **new** |
| Lives (4v4, 10 lives each) | only inside matchmaking `TeamVersusMatch` (queues, boxes) | **new** simple in-arena lives |
| Points for kills | `KillPoints` (`FixedKillReward`, `PointsPerKilledFlag`, `PointsPerCarriedFlag`, `TeamKillPoints`) | none |
| Warp, ship reset, prizes | server interfaces (`IGame`) | called by the new rules module |
| Turn LVZ objects on/off, move them, change image or display time | `LvzObjects` (`ILvzObjects.Toggle/SetImage/SetTimer/SetPosition`), `?objon/?objoff` | none |
| Ship values | ~90 per-ship settings (`InitialGuns`, `MaxBombs`, `BulletSpeed`, `CloakEnergy`, `AttachBounty`, `TurretLimit`, `Upgrade*`, ...), `PrizeWeight:*`, `Prize:*` | none |
| Named settings sets | `ingame-settings` session's 972214b (not on main yet): `pilot_setting_sets`, `/api/settingsets/*`, `/api/settings/preview` + `/apply` | reuse |
| LVZ read/write | `tile-editor` session: `lvz.js` (CLV1/CLV2, byte-exact round trip), LVZ library store `/api/lvzlib` (not on main yet) | reuse |
| Browser client shows LVZ | dare-client `lvz.rs`: map and screen objects, all display modes, animation, server toggle `0x35` and change `0x36` | none expected; check screen anchors match Continuum |
| Map areas | eLVL regions are read by the server; our map editor keeps eLVL bytes but can't edit regions | labels stored in the game mode instead (below) |

So roughly: **objectives, scoring, powerball, rabbit, turf and the ship wizard need no new
server code**; per-flag spots, capture-and-return CTF, hold-the-base, lives, and every timer
trigger/action need one new plugin module.

## Shape of a game mode

A game mode is a pilot's named record (`pilot_game_modes`, alongside settings sets) holding:

- `answers`: what the wizard asked and the pilot chose (so the wizard can reopen on it).
- `settings`: a settings set (reuses `pilot_setting_sets`; the game mode points at it).
- `ships`: role and tweaks per ship (the values themselves land in `settings`).
- `labels`: named map spots and areas in tile coordinates: `{ kind: base|flag|ball|goal|spawn|area, name, team?, x, y, w?, h? }`.
- `objects`: timers, boards and images placed on the map (`x,y` pixels) or screen (anchor + offset).
- `rules`: `{ when: trigger, do: [actions] }` lists, from fixed menus.

**Apply to an arena** writes three things into the arena's draft folder (drafts → Publish as today):
1. the settings, through `/api/settings/apply` (allow-list, rate limit, diff preview);
2. `gamemode.lvz`, generated with `lvz.js` (images + objects, ids in a reserved range), listed in the arena's LVZ files;
3. `gamemode.json` (labels, objects' ids, rules), which the new server module reads.

Labels live in `gamemode.json`, not in the map's eLVL regions: our module is the only reader,
the map file stays untouched, and the same map can carry different game modes.

## New server module: `Dare.GameRules` (in `src/Dare.Arenas`)

Attached per arena when `gamemode.json` exists. Listens to server events and runs the rules.

- **Triggers** (all are existing server callbacks): game start / timer start, flag grabbed,
  flag dropped, a team holds all flags, flag captured (new, below), kill, kill by/of the flag
  carrier ("new rabbit"), goal scored, ball picked up, pilot enters/leaves an area, timer ends,
  team out of lives, score reaches N.
- **Actions**: start/restart/pause/stop a named timer, award points (to a pilot, their team,
  the carrier, the holders, everyone), end the game with a winner, reset flags, reset balls,
  warp pilots (all, a team, the trigger pilot) to spawn or to a label, reset ships, give a
  prize, show/hide an LVZ object, send an arena message.
- **Timers**: named, server-side, 1 s resolution. A visible timer drives its LVZ objects:
  for short countdowns one animated image started with display time (one packet); for long ones
  digit objects (minutes:seconds = 4 digits × 10 images), changing only the digits that change,
  ≤ 2 packets a second for the whole arena.
- **CTF capture-and-return**: a custom `ICarryFlagBehavior` that spawns each flag at its flag
  label, and scores a capture when a carrier enters their own base label with the enemy flag
  (optionally only while their own flag is home). Fires "flag captured".
- **Hold the base**: a team that has pilots in an area label and none of the others for N
  seconds gains points or wins.
- **Lives**: N lives per pilot; at 0 they're put in spectator mode until the round ends;
  a team with no lives left loses the round.
- Limits: at most 32 rules, 8 timers, 64 labels per game mode; each rule fires at most 5 times
  a second; all actions stay in the arena.

Server changes restart the zone (only with nobody online, via the guarded deploy), so this
module is built once with every trigger and action, then the web side grows freely.

## Pages

**Game mode editor** (new tab "Game mode" in `/arena/me`, next to Settings):
panels Objective, Teams & lives, Scoring, Time, Flags, Ball, Ships, Timers & rules, with the
wizard as a guided path through the same panels. Each panel shows the settings it changed.

**Ship wizard** (the Ships panel): eight ship cards; pick a role per ship; sliders for Pace and
Items; Upgrades (off / slow / fast; spawn with fixed or random upgrades); "Pilots can pick up
greens" yes/no; starting items. Proposed roles (values derived from the pace slider):

| Role | Character | Main levers |
|---|---|---|
| Gunner | fast, many bullets | high gun level, multifire, low bullet delay, few bombs |
| Bomber | slow, heavy bombs | bomb level 3, bounce, shrapnel, mines, high energy |
| Sniper | long-range one-shot | very fast bullets, long delay, high damage level, low thrust |
| Cloaker | ambush | cloak + stealth on, XRadar off, light guns |
| Deployer | brings the team in | attach target (`AttachBounty` 0), turret limit, portals, bricks |
| Anchor | team's spawn point and tank | high energy/recharge, `TurretLimit`, slow, repels |
| Interceptor (new) | dogfighter | top speed and rotation, rockets, low energy |
| Support (new) | keeps others alive | repels, decoys, bricks, burst, energy-transfer feel via low cost |
| Flag runner (new) | carries the flag | speed, afterburner, `Flag:FlaggerSpeedAdjustment` friendly |
| Ram / Brawler (new) | close range | shields, high damage factor, short bullet life |

**Map editor** (`/arena/map`): a new "Game" tool group in the left rail (agreed with the
`auto-tiler` session, which owns the rail): Label tools (base, flag spot, ball, goal, spawn,
area) and an Objects drawer (timers, boards, images) you drag onto the map. A **Screen** toggle
draws a player's screen (choose 640×480 / 1024×768 / 1920×1080) over the map at the cursor's
spot; screen objects are dragged onto it and snap to its corners, edges and centre, which is
how LVZ anchors them.

## Canned timer designs (first set)

1. **Digital** (seven-segment, 4 digits, colours per team).
2. **Big centre countdown** (last 10 seconds, animated, one packet).
3. **Bar** (shrinking horizontal bar, 10 steps).
4. **Ring** (12-step circle, good on a map flag spot or base).
5. **Banner** ("Flag captured!", "Round over", "New rabbit!") shown for a few seconds.

Images generated procedurally at build time (exact pixels, like the tile art), stored once,
reused by every game mode.

## Phases (each: own design → Chris approves → build → test → merge to main)

| # | Phase | New server code? | Who sees it |
|---|---|---|---|
| 1 | **Game mode record + Ship wizard**: `pilot_game_modes`, Game mode tab shell, Ships panel with roles, pace, items, upgrades | no (store + API only) | owners |
| 2 | **Wizard core**: first-time question, Objective / Teams / Scoring / Time / Flags / Ball panels for everything the server already does (carry, warzone, rabbit, turf, powerball, time limit, kill points) | no | owners |
| 3 | **Map labeller**: Game tool group, labels saved in the game mode, flag/ball spots fill the spawn settings | no | owners |
| 4 | **`Dare.GameRules` module**: triggers, actions, timers (text only), per-flag spots, capture-and-return CTF, hold the base, lives | **yes** (zone restart) | everyone in the arena |
| 5 | **Timers, objects and screen designer**: canned designs, drag onto map or screen, `gamemode.lvz` generation, rules drive them | small (LVZ driving in the module) | everyone |
| 6 | **Templates and polish**: starting points (Classic CTF, Rabbit, Turf, Powerball, 4v4 lives duel), share a game mode, in-game `?gamemode` | no | everyone |

Phases 1–3 ship value without a zone restart. Phase 4 is the single server change. Phase 5
waits for `tile-editor`'s LVZ library to reach `main`.

## Questions for Chris

1. **Screen designer placement**: a toggleable screen overlay on the map editor (recommended:
   screen objects only make sense over a map, and one place to drag things), or a separate
   "Screen" tab with a still map picture behind it?
2. **Order**: start with Phase 1 (ship wizard, quick, no server change) as proposed, or with the
   timers/screen designer you listed first (needs the rules module, so it lands after Phase 4)?
3. **Where game modes live**: per pilot and applied to arenas (recommended: reusable across
   your arenas and shareable later), or one per arena?

## Left to each phase's design

Exact setting values per role and pace step; the wizard's question wording and pictures; label
drawing UI; the rule menu layout; timer art. Each comes with a mockup for approval.
