# The bots window (in game)

Date: 2026-11-28. Status: design, approved in conversation. Builds on arena bots part 1
(`2026-11-26-arena-bots-foundation-design.md`, live as zone c3aebe7 / client 4714980).

## Why

Esc → B today opens a small panel whose rows mostly put a `?bots …` command on the chat line and
ask the owner to type the rest; the answers scroll past as chat. Chris wants the bots to be run
from a proper window, organised and editable like the Options window: create a bot by answering
one question at a time, see every bot with its status and settings, change them in place, deploy
or stop with a key, and stop them all at once.

## Goals

1. Esc → B opens a **bots window** drawn and driven like the settings window
   (`crates/oss-client/src/game/settings_ui.rs`): the game's box, green labels, white values,
   red headings, a yellow key line at the foot that is clickable; keyboard and pointer both work.
2. **List** (the window's main view): every bot in this arena, one row each: name, behaviour,
   ship, team, priority, chat on/off, and status (in / spectating / out / stopped /
   not launched: why). Updates while the window is open. The match size of each team that has
   one is shown under the list.
3. **Selected bot** (Enter on a row): its settings, each line editable the settings-window way
   (left/right or Enter to change; numbers typed for team and priority):
   behaviour, ship, team, priority, chat on/off. A changed value is sent at once; the row shows
   the zone's answer (saved, or the reason it was refused).
4. **Create** (C, or a "Create a bot" row at the top of the list): a short sequence, one box at a
   time, Esc to go back a step: **name** (typed, the bot name rules) → **behaviour** (pick) →
   **ship** (pick, 1–8 by name) → **team** (number, default 0) → **chat** (on/off) → a summary
   with **Deploy** and **Save stopped**.
5. **Keys** (shown on the foot line): D deploys / stops the selected bot; Enter opens or edits;
   C creates; Del removes (asks to confirm); M edits match sizes; Esc goes back / closes.
6. **Stop all**: a button on the foot line near the bottom; asks to confirm; stops every bot in
   the arena (keeps their settings).
7. **Who sees what**: anyone sees the list with status (read-only; no Create, edit, keys or Stop
   all). The arena owner (and staff who own it) get everything.

## Non-goals

- Waypoints, greetings and keyword replies: stay on the website. (Placing waypoints by clicking
  the map in game is a later idea, not designed yet.)
- New behaviours or the pet mode (separate design).
- Changing the website's Bots tab.

## How it works

### Zone ↔ game data

Today `?bots` answers are human chat lines. The window needs data it can read:

- **`?bots data`** (new): answers the asker with machine-readable lines, one arena message per
  line, each starting with a fixed marker the game recognises and hides from chat (proposal:
  `\u0001bots ` — a control character no player can type; builder confirms it survives the zone's
  chat path, or picks another unprintable marker): a header (`arena`, `owner yes|no`), one line per
  bot (`name`, behaviour, ship, team, priority, chat, deployed, seat/status, problem), one per
  team match size, and an end line. Fields are tab-separated; names never contain tabs.
- The window asks `?bots data` when it opens and then every ~2 s while open (only while open).
- Edits use the existing commands (`?bots create/set/deploy/stop/remove/match`); their human
  replies are also hidden while the window is open and shown on the edited row instead, so the
  chat stays clean. New command **`?bots stopall`** (owner only) stops every bot in the arena.
- A game that doesn't know the marker (Continuum, old clients) shows the lines as chat; harmless,
  and only sent to whoever asked `?bots data`.

### The window

- A new `bots_ui.rs` beside `settings_ui.rs`, reusing its drawing and input helpers where they're
  shared (extract small helpers rather than copy whole blocks). `Sandbox` holds it as it holds the
  settings window; Esc → B opens it (the old chat-prompt panel goes).
- The text box for the name uses the chat line's text rules (printable ASCII); the zone's answer
  is the final say on validity.

## Constraints

- The game is both the desktop client and browser play; both must work. Going live needs a zone
  deploy (`?bots data`, `?bots stopall`) and a browser-game deploy (`deploy/deploy-client.sh`),
  both by the guarded scripts after Chris approves.
- Other sessions work on main in both repos; work on a branch, merge main in before landing.

## Approaches considered

- **Data by machine-readable `?bots` replies** (chosen): works over the existing game connection
  for desktop and browser alike, owner checks stay in the zone, no new protocol.
- **Read the website's `/arena/api/bots`**: only browser play could reach it, and only with a
  website sign-in; the desktop game couldn't.
- **Parse the human `?bots list` lines**: fragile; any wording change breaks the window.

## Left to the builder

- Exact layout and column widths within the 40-column box (the settings window's sizing rules).
- The marker character, after checking the zone's chat path.
- Refresh interval details and how a row shows a pending edit.
