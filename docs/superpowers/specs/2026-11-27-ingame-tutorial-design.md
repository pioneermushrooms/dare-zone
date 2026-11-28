# In-game tutorial

Date: 2026-11-27. Status: design, approach chosen by Chris in conversation; this document
awaits his approval.

## Why

New pilots reach Foundation through the home page's Play button (the browser client) or
through Continuum, and land in an arena with no idea what the screen shows, which keys do
what, or what the arenas are for. The tutorial teaches them by doing, in three acts:
**learn the controls, learn the screen, learn the objectives**, then sends them somewhere
with people in it.

## What Chris decided

- It runs in a **private tutorial arena, one per player**, and the player should not be
  able to tell it is private: it looks and feels like an ordinary arena.
- The **HUD is revealed slowly**: at the start everything except the ship is covered in
  black, and each part of the screen is uncovered when it starts to matter. In Continuum
  the covers are black LVZ screen objects toggled by the server; the browser can use
  richer tools on top.
- Approach: **one shared tutorial, plus browser extras** (see Approaches). The zone runs
  the steps, their checks and the black covers for both clients; the Dare client adds a
  large instruction banner, a highlight around the part being revealed, and the player's
  own key bindings.
- Afterwards the player goes to a **public arena with people in it**, or, when nobody is
  on, is shown the arena list.

## Goals

1. A new pilot is taken into the tutorial on their first connection, without a menu to
   get through first, and can leave it at any moment.
2. Every step waits for the player to **actually do the thing** (checked on the server)
   before going on; no step is just a wall of text.
3. The same steps, in the same order, in Continuum and the Dare client (browser and
   desktop builds), with the differences stated in the step text where keys or screens
   differ.
4. `?tutorial` replays it at any time, for anyone.
5. When it ends (finished or left), the player lands in the busiest public arena, or the
   main public arena plus a pointer to arenas with bots if nobody is on.

## Non-goals

- Teaching advanced play (bounty tactics, team strategy, specific game modes).
- A tutorial for building arenas; the tutorial only points at the arena editor.
- Localisation.
- A website version of the tutorial.

## Anti-goals (a failure even if it works)

- The player feels trapped: no obvious way out, or a step that cannot be completed
  (an action the server cannot see, a broken map spot) with no way past it.
- The player can tell they were put in a special place: an arena named like
  "#tutorial-Wobbly Nebula", other players' chat missing in a way that looks broken.
- Veterans get it forced on them: an existing account that has logged in before must
  never be sent into it unasked.
- The covers break normal play: a cover left on after the tutorial, or LVZ objects
  leaking into other arenas.
- Wording that can only be changed by redrawing images.

## Approaches considered

1. **One shared tutorial, plus browser extras (chosen).** The zone owns steps, checks
   and covers; both clients get the black reveal. The Dare client additionally renders
   each instruction as a banner, outlines the part being revealed, and substitutes the
   player's real key bindings. Continuum reads the same instruction as chat text naming
   the default keys. Cost: a small client addition on top of the server work.
2. **LVZ only.** Instructions and arrows are pre-drawn images toggled like the covers.
   Identical in both clients, one piece of work, but any wording change means redrawing
   images, and it cannot show the player's real keys. Rejected: wording would freeze.
3. **Browser-first.** A rich browser tutorial; Continuum gets chat lines and no covers.
   Rejected: Continuum newcomers get much less, and Chris wants the reveal there too.

## How it works

### Who is new, offering, declining, replaying

- **Accounts:** an account is new when it has **never logged in through the game**.
  `accounts.last_login` can't be used: web logins and game passes also set it, and
  imported legacy accounts have it null. Instead the new tutorial table records each
  account's first game login. When the table is first created it is filled with every
  account that already exists, so no existing or imported account is ever counted as
  new (my call). A new account's first arena entry goes to the tutorial.
- **Guests** get a fresh random name every login (`GuestNames`), so the zone can never
  tell a returning guest. Every guest is taken into the tutorial (my call: guests are by
  far the likeliest newcomers). The very first line says how to leave. The Dare client
  remembers on that machine that the player left or finished the tutorial and tells the
  zone at login (mechanism left to the builder; for example the client sends
  `?tutorial skip` as its first command, which the zone honours only for guests).
- **Bots** (bot-pass sessions) and fake players are never taken in.
- **Leaving:** `?tutorial off` (also `?skip`) at any point ends it and sends the player
  on as in "Afterwards". Stated in the first message and repeated every few steps.
- **Skipping one step:** `?next` skips the current step. Every step has a fallback: if
  the player hasn't completed it after a while (about 90 s), the hint says `?next` skips
  it. Steps the server cannot check (see the table) say "type `?next` when ready".
- **Replay:** `?tutorial` (no arguments) starts it from the beginning for anyone, from
  any arena. `?tutorial <act>` (controls, screen, objectives) starts at that act. A
  replay ends by returning the player to the arena they came from (my call); only a
  newcomer's first run ends as in "Afterwards".
- **Disconnects:** if a new account drops out mid-tutorial, its next login takes it back
  in at the start of the act it was in. A second drop-out counts as having left (my
  call). Guests can't be recognised, so they simply start again.
- **Records:** the zone stores, per account, when the tutorial was finished or left, and
  at which step (new table in the accounts db). Used only for the "never force it twice"
  rule and for a staff count of how far people get; not shown publicly.

### The tutorial arena

- One arena per player, created on demand. **My call on naming:** a private arena
  (`#` prefix, so it is not listed in `?arena` to others) whose name reads like an
  ordinary arena to the player who is in it, e.g. `#training1`, `#training2`... The
  builder must check how SubspaceServer resolves `arena.conf` for a `#` name (base name
  is the name without trailing digits, so `#training`), and fall back to numbered
  public-style names hidden by `ArenaStore.Visibility` if `#` names don't resolve.
  Where a client shows the arena name (the Dare client's HUD or chat on entry), the Dare
  client shows "Training" without the `#` and number.
- All tutorial arenas share one checked-in arena folder (`deploy/arenas/training/` or
  the base name the resolution above requires): `arena.conf`, a small purpose-built map
  and an LVZ file. The arena is a staff arena (not owned by a player) and is not offered
  in the home page arena list or the zone list.
- The arena is destroyed when its player leaves (SubspaceServer does this for empty
  arenas). Its arena bots stop with it, as bots do today.
- **The map** (small, about 200×200 tiles, built with the zone's map tools) has: an open
  practice area around the start, a corridor to practise turning and thrusting through,
  a safe zone, a field where greens appear, one flag, one ball with a goal, and room for
  the target bots. The builder places these and records their coordinates in the
  tutorial's step data.
- **How many at once:** each tutorial arena with bots runs its own bot-host process,
  and every guest goes in. My call: at most **3** tutorials run with bots at the same
  time (a setting). Beyond that, a tutorial runs without bots, and the bot steps (7, 16,
  18, 19, 23) fall back to targets that don't need a bot, or to `?next`. The bot names
  are unique across the zone but look like ordinary pilot names, and come from a
  tutorial-specific lineup rather than an arena record in `ArenaStore`.
- **A quiet arena is fine** (Chris): the player list shows the player and the bots, and
  no chat is relayed in from other arenas. The chat steps are answered by the
  teammate bot, so they don't fall flat. Relaying public chat may come later.
- **Bots:** the arena uses the existing arena-bot host. A **target bot** sits still
  (sentry, holds fire) for shooting practice; a **teammate bot** on the player's team
  for the attach step; a weak **fighter bot** for the final duel. They fly under plain
  names like other arena bots. Which bot is present is changed per step through the bot
  lineup (bots spectate when not needed).
- **Arena settings:** Warbird-centred, generous energy, prizes enabled with items the
  steps grant, attaching allowed on the teammate's ship, short respawn. The builder
  chooses values; the arena has no kill rewards or stats that count elsewhere.

### The black covers (both clients)

- The LVZ file defines one or more black rectangles per HUD part, each anchored to that
  part's screen reference point (the CLV2 anchors both clients support), on the
  TopMost layer, display mode **"show always"**, so they are on from the first frame
  with no toggle at entry. Server-controlled objects start hidden, which would flash
  the HUD. The Dare client toggles any object off whatever its mode (`lvz.rs`
  `toggle`). Continuum is expected to do the same, but **this is the first thing to
  check in a local play-through**, before the steps are built. If Continuum doesn't,
  the fallback is server-controlled covers toggled on as the player enters the arena,
  before their ship spawns. The parts:
  radar, energy bar/gauge, stat box (player list), item and special icons (right side),
  chat (lines and input), the top/bottom info lines (bounty, ship name, etc.).
- The tutorial switches covers off per player with `ILvzObjects.Toggle` as steps reveal
  each part. Leaving or finishing the tutorial switches every cover off first, then
  moves the player. The LVZ file is served only in the tutorial arena, so it cannot
  leak elsewhere.
- **Continuum and chat:** Continuum shows the tutorial's instructions as chat, so in
  Continuum the bottom few chat lines are uncovered from the start (my call) and the
  rest of the chat area is revealed in the chat step. In the Dare client the banner
  carries the instructions, so its chat stays covered until the chat step.
- The builder verifies in both clients that each cover hides its part at common window
  sizes (Continuum 1024×768 and 1920×1080; the browser at phone and desktop sizes) and
  that nothing important is under a cover when it should be visible.

### The steps

The step list is data (a table in code or an embedded file): id, act, the instruction
text (with key placeholders, see the browser extras), what it reveals, the bot lineup,
items granted, and its completion check. Continuum default keys are named in the text;
the builder takes the defaults from the Dare client's `input.rs`, which mirrors
Continuum's, and notes any place they differ.

| # | Act | Step | Check (server side) |
|---|-----|------|---------------------|
| 1 | Controls | Welcome; how to leave. Player is placed in a Warbird. | none (short pause, then on) |
| 2 | Controls | Turn left and right. | rotation in position packets changes through a full turn |
| 3 | Controls | Thrust forward and back; fly to the marked spot. | position reaches the spot |
| 4 | Controls | Fly through the corridor. | position reaches its end |
| 5 | Screen | **Reveal energy gauge.** Energy is your health and your ammo. | none (pause) |
| 6 | Controls | Afterburner: hold it and watch energy drop. | speed above the ship's normal top speed for about 1 s |
| 7 | Controls | Fire bullets at the target bot. | a bullet weapon packet, then a hit or kill on the target |
| 8 | Controls | Fire a bomb. | bomb weapon packet |
| 9 | Controls | Lay a mine. | bomb packet with the mine flag |
| 10 | Screen | **Reveal radar.** Find the safe zone on it and fly there. | safe-zone entry callback |
| 11 | Screen | Safe zone: you can't be hurt or shoot here; energy refills. | none (pause) |
| 12 | Objectives | Greens: fly over a prize to pick it up. | green callback |
| 13 | Screen | **Reveal item icons.** The zone grants items one at a time: | |
| 13a | | repel | repel weapon packet |
| 13b | | burst | burst weapon packet |
| 13c | | decoy | decoy weapon packet |
| 13d | | thor | thor weapon packet |
| 13e | | rocket | speed above top speed without afterburner energy drain |
| 13f | | brick | bricks-placed callback |
| 13g | | portal: drop one, fly away, warp back | not visible to the server: explain, `?next` |
| 13h | | warp (back to spawn) | large position jump to a spawn point |
| 13i | | stealth, cloak, x-radar, antiwarp toggles | status bits in position packets |
| 14 | Screen | **Reveal stat box.** Names, bounty; cycling the box. | none (pause; `?next`) |
| 15 | Screen | **Reveal chat.** Say something in public chat. | chat message callback (public) |
| 16 | Screen | Team chat (`//`), private chat (`:name:`). | team message; private message to the teammate bot |
| 17 | Screen | Macros: what they are and where they're set (Continuum: options menu; Dare client: its own). | not checkable: explain, `?next` |
| 18 | Objectives | Change team: join the teammate bot's team. | freq change callback |
| 19 | Objectives | Attach to the teammate bot. | attach callback |
| 20 | Objectives | Change ship, then back to the Warbird. | ship change callbacks |
| 21 | Objectives | Flags: pick up the flag. | flag gain callback |
| 22 | Objectives | Ball: pick up, shoot, score in the goal. | ball pickup, shoot, goal callbacks |
| 23 | Objectives | Short duel with the fighter bot. | the player kills it (or `?next` after a while) |
| 24 | Objectives | The zone's own features: `?go <arena>`, `?bots` (which pilots are bots), arena editor and `?makearena` on the website, `?tutorial` to replay. | none |
| 25 | | Ending: covers off, move on (see Afterwards). | |

The table is the intended order and content; the builder may reorder within an act,
merge small steps or drop a check that proves unreliable (falling back to `?next`), and
notes each change in the plan.

Each step text is short (one or two lines). When a step completes, the zone says so
briefly ("Nice.") and sends the next one. If the player dies, the current step
continues after respawn.

### Messages to the player

- **Continuum:** each instruction is sent as an arena message to that player only
  (green text in the chat lines kept visible), naming the default keys.
- **Dare client:** the zone also sends a structured hint line the client recognises and
  hides from chat, carrying the step number and count, the instruction text with
  action placeholders (e.g. `{gun}`, `{afterburner}` from the client's `Action` names),
  and which HUD part to highlight. The client already reacts to zone notice lines this
  way (the voice silence notice), and the zone already tells the clients apart
  (`player.Type == ClientType.Continuum`). The exact line format is left to the builder;
  it must be ignored harmlessly by any other client.
- The instruction text is kept in one place on the server, so changing wording never
  needs a client release.

### Browser extras (dare-client)

- **Banner:** the current instruction, large, top-centre, with "step n of m", replacing
  the need to read chat.
- **Real keys:** placeholders are replaced with the player's own binding for that
  action (`key_name` already exists).
- **Highlight:** when a step reveals a HUD part, the client draws a glowing outline
  around it for a few seconds.
- **Arena list at the end:** when the zone ends the tutorial with "nobody is on", the
  client opens its arena list (the same one as the start screen).
- **Remembering:** the client stores locally that this machine has finished or left the
  tutorial, for the guest rule above.
- All of this is inert outside the tutorial: with no hint lines, nothing shows.

### Afterwards

- The zone switches every cover off, records the outcome, and sends the player to the
  public arena with the most real pilots (bots and fake players not counted; the
  existing counting code does this already).
- If no public arena has a real pilot: Continuum players go to the main public arena
  and get a line naming arenas that have bots and `?go <name>`; Dare client players get
  the arena list opened for them.

## Constraints

- Plugin code in `src/Dare.Arenas` (a new `Tutorial` partial or module beside it) with
  rules in `Dare.Arenas.Core` so they are unit-tested without a server, as the rest of
  the plugin does.
- `dotnet test` passes; the dare-client change has its own tests (`cargo test`).
- Played through end to end on a local zone in both Continuum and the browser client.
- LVZ building uses the zone's own LVZ tooling (`LvzFile.cs`); `tile-editor` is
  extending LVZ object support in parallel, so coordinate with it before touching the
  same files.
- Shipping: own branch, merged into `main` in both repos and pushed; no deploy without
  asking Chris.

## Left to the builder

- Arena naming and config resolution details (within "Who is new" and "The tutorial
  arena" above).
- The map layout, cover images and sizes, arena settings values.
- The hint line format and the client's local storage key.
- Timeouts, pauses, the "Nice." wording and all instruction wording (short, friendly,
  plain).
- How the bot lineup changes per step, within the existing bot host.
