# In-game tutorial

Date: 2026-11-27. Status: design, approved by Chris 2026-11-27.

## Why

New pilots reach Foundation through the home page's Play button (the browser client) or
through Continuum, and land in an arena with no idea what the screen shows, which keys
do what, or what the arenas are for. The tutorial teaches them by doing, in three acts
(**learn the controls, learn the screen, learn the objectives**), told as a short story
by a mentor, and then sends them somewhere with people in it.

## What Chris decided

- **A private tutorial arena, one per player**, that the player can't tell is private.
  A quiet arena is fine: the player list shows the player and bots, and no chat is
  relayed in from other arenas.
- **The HUD is revealed slowly**: at first everything but the ship is covered in black,
  and each part is uncovered when it starts to matter. In Continuum the covers are black
  LVZ screen objects; the browser uses the same covers plus its own tools.
- **One shared tutorial, plus browser extras.** The zone runs the steps, checks and
  covers for both clients. The Dare client adds what its own tools allow.
- **Not exhaustive.** Short and fun beats complete; at the end the player is told that
  **F1** opens the full manual (both clients have F1 help pages).
- **Narrated by a mentor**: a talking character with a portrait in the **bottom-left**
  corner (clear of the radar), Star Fox style, speaking in character.
- **Green chat text is ignored by new players**, so the mentor speaks in **speech
  bubbles drawn as LVZ images** in Continuum; the Dare client draws the same box itself.
- **No `?` commands for newcomers to learn.** In the browser, controls such as "skip"
  and "leave" are buttons on the screen. In Continuum, an obvious LVZ popup says exactly
  what to type.
- **The swarm skit** (Act 2's centrepiece), as described below.
- Afterwards the player goes to a public arena with people in it, or is shown the arena
  list.

## Goals

1. A new pilot is taken into the tutorial on their first connection, without a menu to
   get through first, and can leave at any moment without knowing any commands.
2. Every step waits for the player to **actually do the thing** (checked on the server)
   before going on.
3. The same story in Continuum and the Dare client (browser and desktop builds), with
   the differences stated in the mentor's lines where keys or screens differ.
4. It can be replayed at any time.
5. When it ends, the player lands where people are playing.

## Non-goals

- Covering every feature. Macros, most items (decoy, thor, brick, portal, rocket, warp,
  stealth, cloak and the rest), stat-box cycling and advanced play are left to the F1
  manual.
- Teaching how to build arenas (the end only points at the arena editor).
- Relaying chat from other arenas into the tutorial.
- Localisation; a website version.

## Anti-goals (a failure even if it works)

- The player feels trapped, or meets a step they can't complete with no way past it.
- The player can tell they were put in a special place (an odd arena name, a broken-
  looking screen).
- Veterans get it forced on them.
- The covers or the mentor box break normal play: anything left on screen after the
  tutorial, or LVZ objects leaking into other arenas.
- Changing a line of dialogue means redrawing an image by hand.
- It drags: a step that waits on something slow without the mentor keeping it lively.

## Approaches considered

1. **One shared tutorial, plus browser extras (chosen).** The zone owns the story,
   checks and covers; the Dare client draws the mentor box natively, with the player's
   real keys and clickable buttons.
2. **LVZ only.** Everything as images in both clients. Rejected: no real keys or
   buttons in the browser.
3. **Browser-first.** Rejected: Continuum newcomers get much less.

For Continuum's dialogue, Chris chose **generated LVZ speech bubbles** over chat plus an
LVZ arrow pointing at it, and over big centre-screen text for key lines only.

## How it works

### Who is new, leaving, skipping, replaying

- **Accounts:** an account is new when it has **never logged in through the game**.
  `accounts.last_login` can't be used: web logins and game passes also set it, and
  imported legacy accounts have it null. Instead a new tutorial table records each
  account's first game login. When the table is first created it is filled with every
  account that already exists, so no existing or imported account is ever new (my
  call). A new account's first arena entry goes to the tutorial.
- **Guests** get a fresh random name every login (`GuestNames`), so the zone can't tell
  a returning guest. Every guest is taken in (my call: they are the likeliest
  newcomers). The Dare client remembers on that machine that the player left or
  finished, and tells the zone at login so it skips the tutorial (mechanism left to the
  builder; honoured only for guests).
- **Choosing another arena first** (Chris): the tutorial takes over when a newcomer
  enters the normal starting arena (`public`, Continuum's default and "public" on the
  browser's arena list). A newcomer who picks a different arena on their first
  connection goes there and is prompted once: a mentor popup with a **Try the
  tutorial** button (browser), or the line to type (Continuum).
- **Bots** (bot-pass sessions) and fake players are never taken in.
- **Leaving and skipping, without commands to learn:**
  - Dare client: **Skip** (this step) and **Leave tutorial** buttons beside the mentor
    box. The client sends the commands for them.
  - Continuum: an LVZ popup in the mentor box's style, impossible to miss, saying
    *"Want to skip ahead? Type ?skip and press Enter. To leave: ?leave"*. It shows at
    the start, and again whenever the player hasn't finished a step for about 60 s.
  - Underneath, the commands are `?skip` (skip this step) and `?leave` (end the
    tutorial). Steps the server can't check move on by themselves after the mentor's
    line, or on Skip.
- **Replay:** `?tutorial` replays it for anyone, from any arena. A replay ends by
  returning the player to the arena they came from (my call). The F1 manual and the
  end of the tutorial mention it.
- **Disconnects:** a new account that drops out mid-tutorial is taken back in at the
  start of the act it was in on its next login. A second drop-out counts as having left
  (my call). Guests simply start again.
- **Records:** per account, the first game login, and when the tutorial was finished or
  left and at which step. Used for the rules above and a staff count of how far people
  get; not shown publicly.

### The tutorial arena

- One arena per player, created on demand. **My call on naming:** a private arena
  (`#` prefix, unlisted for others) whose name reads like an ordinary one, e.g.
  `#training1`. The builder checks how SubspaceServer resolves `arena.conf` for a `#`
  name (the base name is the name without trailing digits, so `#training`). If `#`
  names don't resolve, the fallback is numbered names hidden by `ArenaStore.Visibility`.
  Where the Dare client shows the arena name, it shows "Training".
- All tutorial arenas share one checked-in arena folder: `arena.conf`, a small
  purpose-built map and the tutorial LVZ file. A staff arena, not offered on the home
  page or in the zone list. Destroyed when its player leaves; its bots stop with it.
- **The map** (small, about 200×200 tiles): an open area around the start, a spot to
  fly to, an open ring where the swarm skit happens, a safe zone, a field where greens
  appear, one flag, one ball and goal. The builder records the coordinates in the
  tutorial's step data.
- **Settings** (values left to the builder): Warbird-centred. For the tutorial player,
  very high energy (they effectively can't die in the skit), and weak bullets that still
  do damage, so the swarm takes a while by hand. Prizes enabled for the greens step. No
  stats that count elsewhere.
- **How many at once** (Chris): the first **10** tutorials running at the same time get
  the full cast (target, swarm, teammate), each in its own bot host. When more are
  running, each extra tutorial gets **one bot**, which plays every part: the target,
  a swarm of one (the mentor's lines adjust, e.g. "a scout"), and the teammate. Both
  numbers are settings. Bots only fly during the steps that use them. A ceiling
  setting (default 30 tutorials) stops new ones beyond it; a newcomer then gets the
  "Try the tutorial" prompt instead (my call, a safety valve only). The builder
  measures one tutorial host's memory and CPU on a local zone and reports it before
  shipping. **Measured 2026-11-28** (local zone, release oss-bot, full cast of 7 bots
  during the swarm): about 78 MB resident and 1% CPU per tutorial host (28 MB before
  the bots connect). So 10 full casts would be about 780 MB on top of the zone. Bot names are unique across the zone but look like ordinary pilot names,
  from a tutorial-specific lineup rather than an arena record in `ArenaStore`.

### The mentor

- **Character (my call; Chris may change it):** a veteran pilot with a callsign, dry and
  a little cocky, who teases the player and cheers when they get it. One portrait,
  roughly 64×64, plus a second "talking" frame for a simple mouth flap. The builder
  draws a placeholder portrait in the zone's pixel style; Chris can swap in his own.
- **The box**: bottom-left, above the chat lines, pinned to the chat anchor so it
  follows the chat's size. It holds the portrait, the character's name and one line of
  dialogue (two at most). The box appears with each new line and stays until the next;
  it disappears when the tutorial ends.
- **The script** lives in one file in the repo: the steps, each step's mentor lines,
  what each step reveals, grants and checks, and its bot lineup. Lines use placeholders
  for keys (`{gun}`, `{repel}`), named after the Dare client's `Action` names.
- **Continuum (generated LVZ):** a build tool renders each line of the script, with
  Continuum's default keys filled in, into an image in a pixel font. It packs those
  images, the portrait frames, the box and the covers into the tutorial LVZ file. Each
  line is a "server controlled" screen object; the tutorial toggles the box, portrait
  and the current line on and the previous line off. Changing wording = edit the
  script, rerun the tool, redeploy the arena. The tool, its font and how its output is
  checked in or built are left to the builder; it reuses `LvzFile.cs`. (`tile-editor`
  is extending LVZ support in parallel; coordinate before touching the same files.)
- **Dare client (native):** the zone sends the same line as a structured hint line the
  client recognises and hides from chat. The client draws the box itself, with the
  player's real keys in place of the placeholders and a typewriter reveal, plus the
  Skip and Leave buttons. The client hides the LVZ mentor objects (by object id range)
  so the box isn't drawn twice. The covers stay LVZ in both clients.
- **Older Dare clients** (my call): a client that handles tutorial hints says so at
  login. A client that doesn't (an older desktop build) gets exactly the Continuum
  experience: LVZ bubbles, and the popup with what to type. So the zone can ship before
  every client updates. The zone, client and bot changes still ship together.
- Each line is also sent as an arena message to that player, so it is in the chat log
  for anyone who scrolls back. The chat isn't the main channel.

### The black covers (both clients)

- The LVZ file defines black rectangles for each HUD part, each anchored to that part's
  screen reference point (the CLV2 anchors both clients support), on the TopMost layer.
  The parts are the radar, energy gauge, stat box, item icons and the chat. Display mode
  is **"show always"**, so they are on from the first frame with no flash. Toggling off
  per player is done with `ILvzObjects.Toggle`. The Dare client toggles any object off
  whatever its mode (`lvz.rs` `toggle`); Continuum is expected to, but **this is the
  first thing to check on a local zone**, before the steps are built. If it doesn't,
  the fallback is server-controlled covers toggled on as the player enters, before
  their ship spawns.
- Leaving or finishing turns every cover and mentor object off, then moves the player.
  The LVZ file is served only in the tutorial arena.
- The builder checks at common window sizes (Continuum 1024×768 and 1920×1080; browser
  at phone and desktop sizes) that each cover hides its part and the mentor box doesn't
  cover anything that is meant to be visible.

### The story

Mentor lines below give the gist; the builder writes them, in character, short. Checks
are server-side. "Reveal" means that cover comes off, and in the Dare client the part
glows briefly.

**Act 1: Controls** (only the ship and the mentor box are visible)

| # | What happens | Check |
|---|--------------|-------|
| 1 | Mentor introduces themselves; the skip/leave popup appears (Continuum) or the buttons glow (browser). Player is in a Warbird. | none (moves on after the line) |
| 2 | "Turn with ← and →." | rotation goes through a full turn |
| 3 | "↑ thrusts, ↓ reverses. Fly to the beacon." (a marker on the map) | position reaches the spot |
| 4 | Reveal **energy**. "That bar is your life *and* your ammo." | none |
| 5 | A target bot drifts in. "{gun} fires your guns. Hit it." | bullets fired and the target hit |
| 6 | "{bomb} drops a bomb. Try it." | bomb fired |

**Act 2: The swarm** (the screen)

| # | What happens | Check |
|---|--------------|-------|
| 7 | The zone gives the player **Engine Shutdown**, so they can't move. Mentor: their engines are down, sit tight. Reveal **radar**: red dots closing in. | none |
| 8 | A swarm of bots flies in, stops in a ring around the player and fires slowly, again and again. | none |
| 9 | The zone gives **Shields** (and a Repel). Mentor announces "I've given you Shields", with a brief in-character description, then reveals the **item icons**. | none |
| 10 | "Now {repel}: push them back!" | repel fired |
| 11 | "Now shoot them down with {gun}." The swarm drifts back in. Bullets are weak, so it's slow. After a few kills, or about 20 s, the mentor taunts that this will take forever. | at least one kill, or the timer |
| 12 | The zone gives **Super**. Mentor: "This'll help." Player kills the rest. | every swarm bot killed |
| 13 | Engines come back on. Reveal **stat box**: "That's who's out here with you." | none |

Super's exact effect in Continuum is checked in the play-through. If it only makes
weapons cost no energy and the kills don't speed up noticeably, the zone also gives a
gun upgrade with it, so the payoff is real.

**Act 3: Objectives**

| # | What happens | Check |
|---|--------------|-------|
| 14 | "Hurt? Fly into the safe zone, there on your radar." Energy refills. | safe-zone entry |
| 15 | "Greens are prizes. Grab one." | green picked up |
| 16 | Reveal **chat**. "Say hi." The teammate bot answers. | public chat line |
| 17 | "Change ship: Esc, then 1–8." (browser: its own way). Then back to the Warbird. | ship change |
| 18 | "Flags and balls are what most games are about. Grab the flag." | flag picked up |
| 19 | "Now the ball: pick it up, shoot it into the goal." | ball goal |
| 20 | Ending: "That's the basics. **F1** has the full manual. Arena editor: on the website. Want to see this again? ?tutorial." | none |

The builder may merge or reorder steps within an act, or drop a check that proves
unreliable, provided it moves on with a mentor line instead. Changes are noted in the
plan. If the player dies, the current step continues after respawn.

### The bots

- **Target bot** (act 1): flies in slowly and sits still, holding fire.
- **Swarm** (act 2, about 5 bots): a **new behaviour** in the bot program (`oss-bot`,
  dare-client repo). The bots fly toward a given player, stop at a set distance in a
  ring, face them and fire slowly, and after being repelled come back to the ring. Low
  energy so kills land.
- **Teammate bot** (act 3): answers the chat step with a canned line.
- The lineup changes per step through the existing bot host: bots spectate when not
  needed.

### Afterwards

- Every cover and mentor object goes off, the outcome is recorded, and a newcomer goes
  to the public arena with the most real pilots (bots and fake players not counted).
- If no public arena has a real pilot, Continuum players go to the main public arena and
  get a line about arenas with bots and how to go there. Dare client players get the
  arena list opened for them.

## Constraints

- Plugin code in `src/Dare.Arenas` (a new `Tutorial` partial or module beside it), with
  rules in `Dare.Arenas.Core` so they are unit-tested without a server.
- dare-client: the swarm behaviour in `oss-bot`, and the mentor box, buttons and hint
  handling in `oss-client`, each with tests (`cargo test`).
- `dotnet test` passes; played through end to end on a local zone in both Continuum and
  the browser.
- Shipping: own branches, merged into `main` in both repos and pushed; no deploy without
  asking Chris.

## Left to the builder

- Arena naming and config resolution (within the rules above).
- Map layout, cover and mentor art, the pixel font, arena settings values.
- All dialogue (in character, short), within the story above.
- The hint-line format, the LVZ object id ranges, the client's local storage key.
- Timeouts and pauses.
- How the bot lineup changes per step, within the existing bot host.
