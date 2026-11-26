# Arena bots, part 1: the foundation

Date: 2026-11-26. Status: design, approved in conversation; not yet planned.

## Why

Arena owners should be able to fill an arena with bots that play like the people who
normally play there: teams of bots fighting each other, real players joining a side and
the bots making room, so the arena feels alive and a newcomer can barely tell who is a
bot. Today's bots can't do that:

- every bot gets its own team (freq 100 + its place in the list) and bots never target
  each other, so there are no bot teams and no bot wars;
- each bot is its own program, so a full match is heavy on the 2 GB zone server and bots
  can't coordinate;
- names end in " [bot]", and the zone recognises bots by that suffix;
- to other players bots drift and snap ("laggy"), and the zone has specced one for a
  618 ms ping although it runs on the same machine;
- they fight by flying straight at the nearest enemy holding fire.

This is part 1 of four. It builds what the rest stands on; it does not make bots fight
better (part 2).

| Part | What | Depends on |
|------|------|------------|
| 1 (this) | Bot host, match director, plain names, real-client connection | — |
| 2 | Combat skill: a dogfighting core with skill/temperament sliders; the spawn dueler and pubber play styles | 1 |
| 3 | The bot suite on the website: + bot, play style, team, sliders, match size, who makes room | 1, 2 |
| 4 | Arena objectives (flags, named bases), the baser and flagger, attaching, items | 1, 2 |

## Goals

1. One **bot host** process per arena runs all of that arena's bots. Each bot keeps its
   own connection to the zone, so it is a separate pilot to everyone else. The bots in a
   host share one view of the arena, ready for team planning in later parts.
2. A **match director** in the zone: each bot has a team (freq); each team has a match
   size; bots fill the places real players have not taken and make room when players
   join; bots on different teams fight each other, bots on the same team never shoot
   each other.
3. Bots fly under **plain names**. The zone knows which pilots are bots from its own
   register, not the name. `?bots` tells anyone which pilots in the arena are bots.
4. To the zone and to other players, a bot's **connection behaves like a real client's**:
   normal ping, smooth movement on other screens, security checks answered as the game
   client answers them.

## Non-goals

- Better fighting, new play styles, sliders (part 2).
- The full bot suite (part 3). This part adds only what is needed to use it: `?bots`
  options and a Team field in the existing web Bots tab.
- Objectives, flags, bases, attaching, items (part 4).
- Chat changes. Casual chat stays as it is (`chat on|off`, web switch).

## Anti-goals (would make this a failure even if it works)

- Bots that give themselves away in normal play: a " [bot]" tag, a different ping
  profile, ships that drift and snap, a player list where they arrive in a burst.
- Bots counted as players: the zone list and home page "players online" must not
  include them, and they must not keep an arena awake on their own.
- A bot able to take a real pilot's name, or a pilot able to register a bot's name.
- Making room in a way that looks scripted (a bot vanishing mid-fight the instant a
  player picks a ship).
- Restarting bots that did not change (the bug fixed in `cd67dc8`).

## How it works

### The bot host

- One process per arena that has deployed bots, started by the zone's supervisor when a
  real player is in the arena and stopped about a minute after the last one leaves (as
  bots are today). The supervisor talks to it over its standard input/output.
- The zone sends the host the arena's **lineup**: for each bot its name, one-time
  password, ship, behaviour, waypoints, chat settings, team, and whether it should be in
  the game, spectating, or gone. It sends a new lineup whenever that changes (an owner
  edit, a player joining or leaving a team). The host applies the difference: it starts
  and stops bots, changes ships and teams, without dropping bots that did not change.
- Each bot in the host has its own session (login, arena entry, position reports,
  security answers), as `oss-bot --count` already runs several sessions in one process.
- The host keeps one shared picture of the arena (ships, shots, the map, which bots are
  its own and their teams). Today's four behaviours (idle, patrol, sentry, fighter)
  read from it and work as they do now, with two changes: enemies are pilots **and bots**
  on other freqs, and teammates are never targets.
- Roaming from `325f8da` (fighters with nothing to do and patrols with no waypoints fly
  about) is kept.

### The match director (in the zone)

- Each saved bot gets a **team** (a freq, 0–9999) and a **priority** (who makes room
  first). Each arena's bot setup gets a **match size per team** (how many pilots, bots
  plus players, that team should have). Existing bots move to team 0 with match size =
  the number of bots on it, so they become teammates who fight players only, until the
  owner sets teams. (Today each bot is alone on its own freq.)
- The director runs in the supervisor's tick. For each team it counts the real players
  flying on that freq (not spectating) and puts `match size − players` of that team's
  bots in the game, highest priority first, the rest out.
- **Making room**: a bot told to leave the game finishes as a player would. It is given
  a few seconds (proposal: up to 8) to stop fighting, then switches to spectator, then
  leaves the arena a little later (proposal: 20–60 s, random). A bot told to come back
  enters (or leaves spectator) after a short random delay (proposal: 3–15 s), so bots
  never arrive or leave in a burst.
- **Uneven sides**: if after making room one team is short and the other has a bot to
  spare, the spare bot switches team (spectate, change freq, re-enter) rather than
  leaving. Proposal: only between teams the owner has given a match size.
- When more players are on a team than its match size, all of its bots are out; the
  arena is then a normal player arena.
- Real players are never moved; only bots are.

### Identity and `?bots`

- Bot names are plain (no suffix). A bot's name is its label (1–14 characters, as today;
  proposal: allow up to the zone's 20-character name limit now that the suffix is gone).
- The zone keeps the **register** of bot names in its database (the existing `bots`
  table is it). Everything that used `IsBot(name)` (the suffix) uses the register:
  bot logins with the one-time pass, occupancy (bots don't keep an arena awake), the
  "players online" count, the restart notices, the zone list.
- Names are exclusive both ways: saving a bot whose name is a pilot account is refused
  (as today), and creating an account or claiming a pilot name that is a bot's name is
  refused (new).
- Existing bots: drop the " [bot]" suffix the next time they start. Their labels are
  already checked against accounts.
- **`?bots`**: anyone in the arena may type it and gets the list of pilots here that are
  bots (just the names). The arena owner (and staff who own it) also get the management
  commands as now.
  Every player needs `cmd_bots` for this (add it to `deploy/default-caps.txt` /
  `groupdef.dir/default`); the handler keeps the management commands owner-only.
- **The website** follows the same rule as the counts: the home page's per-arena player
  lists and the chat panel's "players" leave bots out, so a list never shows more names
  than its count. Inside the arena (game and chat) bots look like anyone else.

### Looking like a real client

- **Position reports**: dated to the moment their simulation tick stands for, and an
  extra report the tick a bot starts or stops thrusting, afterburning or turning (at
  most one per 20 ms): the game client's fix in oss-client `1bbde0f`
  ("tick-exact and on-change updates"), brought to the bots.
- **Ping**: find out why a bot on the zone's own machine reads 618 ms (first suspect:
  the single loop stalling during long route searches, so the zone's reliable packets
  are acknowledged late) and fix it so a bot's ping looks like a nearby player's.
  Route planning must never hold up the network side.
- **Security checks**: bots currently fail the zone's periodic check ("Exe checksum
  mismatch", logged as malicious). They should answer exactly as the game client does
  (see `oss-session/src/client.rs` `security`, which reports the measured real-client
  answer). Find what the zone expects from the game client and make bots match it.

### Controls in this part

- `?bots set <name> team <freq>`, `?bots set <name> priority <1-9>`,
  `?bots match <freq> <size>`, and `?bots list` showing team, priority and in/out.
- The web Bots tab gains a Team field per bot. (Match size and priority wait for the
  bot suite unless they are trivial to add.)

## Constraints

- Zone server: 2 GB RAM, x86-64 Linux (glibc 2.43). The host must ship as a Linux
  build next to the plugin (deploy.sh `OSS_BOT_LINUX`), built in Docker from a Mac.
- Zone caps stay: `MaxBots` (zone-wide, default 30) and 10 bots per arena; proposal:
  raise the per-arena cap to 16 so 8v8 fits.
- The zone plugin cannot be swapped without a restart; plan deploys for when the zone is
  empty (deploy.sh refuses otherwise unless forced).
- Other sessions work in the same repos (dare-zone `custom-graphics`/`loading-flow`,
  oss-client `loading-flow`); keep changes on their own branches and merge on purpose.
- Starting points not yet deployed: dare-zone `cd67dc8` (no restarts on add/remove,
  `chat on|off`) and oss-client `325f8da` on `bots-menu-play` (roaming).

## Approaches considered

- **A. Smarter single bots**: keep one program per bot; the zone directs them. Smallest
  step, but no teamwork and a full program per bot strains the server.
- **B. One host per arena with a team brain** (chosen): real connections per bot, a
  shared picture for team play later, far lighter on the server. More new work up front.
- **C. Bots inside the zone**: no connections at all; the zone would have to fake flight
  and weapons itself and they would be the most likely to look wrong.

Chosen: **B**, because team play is the point of bot wars that pass as real matches, and
only B gets it without straining the server.

## Calls made in this document (not discussed; check these)

- Anyone can type `?bots` to see which pilots are bots; the owner also gets management.
- Existing bots become teammates on team 0 until the owner sets teams.
- The website's player lists leave bots out, matching the counts.
- Making room: up to 8 s to break off, spectate, then leave the arena 20–60 s later;
  coming back after 3–15 s. Only real players flying on a team count toward its size.
- Team switches only between teams that have a match size.
- Per-arena cap 10 → 16; bot names up to 20 characters.
- Freqs are set by the zone (`SetFreq`), so arena freq limits for players don't bind
  bots; owners are trusted to pick sensible teams.

## Left to the builder

- The host's control protocol (lineup format, acknowledgements) and how a crashed host
  is restarted (today's per-bot backoff is a model).
- Whether the host is a new binary or `oss-bot` grown a host mode.
- The exact timings for making room and returning, within the "never in a burst, never
  mid-fight" rule.
- How priority ties and team switches are chosen when several bots qualify.
