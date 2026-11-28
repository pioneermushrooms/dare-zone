# Pet bots: the basic version

Date: 2026-11-28. Status: **draft**. It sets down what Chris has described so far and has not been approved.
Branches: dare-zone `claude/pet-bots-90ee75`, oss-client `claude/pet-bots`.
It builds on part 1 of arena bots (`2026-11-26-arena-bots-foundation-design.md`, live as zone c3aebe7
and client 4714980).

## What it is

An arena mode, switched on per arena. While it is on, every player in the arena gets one bot of their
own, "like a pet". The pet listens only to its owner. It flies and fights like any pilot and can be
killed like one. Chris sees room for much more later ("that could be an entire arena mode itself").
This document covers only the smallest version that is playable.

## Goals (basic version)

1. **A switch.** An owner turns pets on or off for an arena. It shows as a bots option in the game
   mode editor. Default: off.
2. **One pet per player.** A real player flying in a pets arena gets one pet. The pet:
   - comes in a few seconds after its owner takes a ship;
   - flies on its owner's team (freq) and follows the owner's freq changes;
   - leaves when the owner leaves the arena. When the owner spectates, it leaves after a short grace
     period.
3. **Four commands, from the owner only:**
   - **Follow**: stay with me. This is the default when the pet arrives.
   - **Turret**: attach to my ship as a turret.
   - **Detach**: leave the attach and go back to following.
   - **Post**: hold the spot where the pet is now, and fight from there.
4. **Killable.** The pet takes damage, dies and respawns like a player, then carries on with its last
   command. A turret pet whose owner died attaches again once both are alive.
5. **Light enough.** About 20 pets fit on the 2 GB server next to the zone (about 1.25 GB is free
   today).

## Non-goals (for now)

- More commands: Recall/come here, Guard me, Attack a target, Hold fire, Fetch the flag.
- Hotkeys in the game client.
- Pet scoring, pet-versus-pet games, pets that grow or persist between visits.
- A web page for pets. The mode is switched in the game mode editor and needs no other settings
  screen.
- Better fighting. Pets fight as today's bots do. Combat is part 2 of arena bots.

## Anti-goals

- A pet that obeys anyone but its owner, or that the arena owner can take over.
- Pets counted as players (zone list, "players online"), or pets keeping an arena awake.
- A pet taking a real pilot's name.
- Pets crowding out the arena's own bots, or the reverse: enabling pets must not stop deployed bots
  from launching.
- A server that runs out of memory at 20 players.

## How it works

### Zone (dare-zone plugin)

- **The switch** is an owner-editable arena setting. *Call:* `GameMode:Pets` (0/1), placed next to
  `GameMode:Attach`. The game mode editor is being built in another session
  (`claude/game-mode-wizard-904376`). That session is asked to add the toggle; its panels are not
  edited here.
- **Who gets a pet:** the supervisor (`ArenaOwnership.Bots.cs`) already takes a census each tick. In a
  pets arena, every real player (not a bot, not a fake player) who is flying gets a pet entry. The
  arena's own deployed bots are planned as now, and pets are added to the same lineup and the same
  host process.
- **Pacing:** a pet's arrival and departure go through the existing `BotSeats` pacing (a short wait
  coming in, a grace period going out). Pets never arrive in a burst.
- **Names:** each pet gets a name from a built-in list of pet names. It must be valid for bots (1–20
  characters), must not be an account name, and must not be in use in the zone. The owner is told by
  private message: "Biscuit is your pet. PM it: follow, turret, detach, post." *Call:* the name is
  picked fresh each visit.
- **Passes, freq and exclusion** work as they do for bots today: a one-time bot pass, the
  `[arenabot]` group, `excludepopulation`, and the freq set by the zone (`SetFreq`) to the owner's
  freq.
- **Caps:** pets get a budget of their own. *Call:* a zone-wide `MaxPets` (default 24), separate from
  `MaxBots` and outside the 16-per-arena bot cap. When the budget is full, a player gets no pet and is
  told why.
- **Turret support:** attaching needs the owner's ship to allow turrets (`TurretLimit` > 0). *Call:*
  the plugin does not change ship settings. If the owner's ship takes no turrets, the pet answers
  "Your ship can't carry a turret here".
- **`?bots`** lists pets as bots. *Call:* each pet is shown with its owner, e.g. "Biscuit (Chris's
  pet)".

### Bot host (oss-client `crates/oss-bot`)

- **Lineup:** a bot block gains `master <name>`. A bot with a master takes orders from that player
  only, instead of the lineup's `controller`. Changing the master is a `Tell`, not a restart. Format
  is in `lineup.rs`.
- **Orders:** the owner sends private messages to the pet. They extend today's `parse_order` /
  `FlightOrder`:
  - `follow` (or `follow me`) → `Follow(owner)`
  - `turret` (or `attach`) → `Attach(owner)`: calls `session.attach(owner pid)`. The pet attaches
    again after a death, and falls back to Follow if attaching is refused.
  - `detach` → `session.attach(-1)`, then Follow.
  - `post` (or `stay here`) → `Position(here)`
  - `help` → the list of orders.
  - Each order gets a one-line reply, rate-limited as today.
  - A pet with a master never takes orders from the arena owner, and does not advertise "flight
    orders from the arena owner".
- **Fighting:** when following or posted, the pet engages enemies (other freqs, players and bots)
  near it, as `brain.obey` does today. As a turret it only shoots.
- **Memory: the "smaller bots" work.** It was measured on 2026-11-28 at about 11 MB a bot, almost all
  in `nav.rs`: the `blocked`/`solid` grids (1 MB each) and the `cost`/`from` search arrays (4 MB
  each). Each bot also builds its own `World` (arena simulation and map). The plan:
  - share one read-only map grid per host and ship size (`Arc`), built once;
  - give pets a bounded search window around themselves and their owner instead of full-map arrays
    (a pet mostly follows its owner);
  - look at whether the `World` map can be shared too.

  The target is about 1–2 MB a pet. **Measure** the host's memory with 1, 10 and 20 pets before and
  after, and record the numbers here.

## Constraints

- 2 GB zone server, x86-64 Linux. The host ships as the Linux `oss-bot` next to the plugin
  (`deploy.sh`, `OSS_BOT_LINUX`). Client builds need `~/oss-src/oss-core` on
  `dare/netcode-precision-dev9`.
- A plugin change needs a zone restart. Deploy when the zone is empty, from the deploy checkouts with
  the guarded scripts and `--by`, and only with Chris's OK.
- Other sessions work in parallel: the game mode editor (`claude/game-mode-wizard-904376`) and the
  in-game bots window (`bots-window`). The pets toggle and any bots-window entry for pets are
  coordinated by message.

## Approaches considered

- **A. Pets as entries in the existing arena host** (proposed): each gets a `master`, and the zone
  adds them to the lineup. This reuses the connections, passes, pacing and orders already built, and
  makes the memory work pay off for all bots. It costs a per-bot master, a separate pet budget and
  the nav sharing.
- **B. A separate pet host process per arena:** this keeps pets apart from the owner's bots but
  doubles the processes and their fixed memory. It adds nothing a `master` field doesn't give.
- **C. Pets simulated inside the zone, with no connections:** this is cheapest on memory, but the
  zone would have to fake flight and weapons. Part 1 already rejected this as the most likely to look
  and play wrong.

Not yet chosen by Chris; A is the recommendation.

## Open: Chris's to answer

- What the mode is for: is a pet a wingman in an ordinary fight, or is the pet the game itself? This
  decides how strong pets are, whether they score, and whether they last past one visit.
- Which of the further commands (Recall, Guard me, Attack, Hold fire, Fetch the flag) come next, and
  whether hotkeys are wanted in the first playable version.

## Left to the builder

- The exact pet-name list, grace periods and reply wording.
- How the search window is sized, and the fallback when the owner is out of the window.
- Where pets appear in the in-game bots window (by message with that session).
