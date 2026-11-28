# Live timers and scoreboards: native HUD for our clients, LVZ for Continuum (proposal)

Status: proposal for Chris, not approved, nothing built. Replaces the "timers" part of Phase 5 in
`2026-11-27-game-mode-wizard-design.md`.

## Why

Chris: "the lvz images dont have a clock? hoping to have a live countdown that i can tie to events.
is this (lvz) even the best method … lvz was the ancient way of making these solutions".

He's right that LVZ is the wrong base layer. An LVZ image can't show a number the zone works out
during the game. The zone can only switch prepared images on and off, or start an animation that
always runs the same way. The game modes need clocks that start, stop and restart on game events.

## What each client can do

| Client | What it understands | A live countdown means |
|---|---|---|
| Continuum | LVZ only (objects switched on and off: packets 0x35 and 0x36) | the zone switches digit objects on and off each second (the stock digits), or starts a canned animation for short fixed countdowns |
| Our clients (oss-client: the browser game and the native one) | anything we add | the zone sends "timer X ends at T, label, place on screen", and the client draws it natively: sharp at any size and resolution, smooth, no packet per second |

## Proposal

**Game modes describe timers and scoreboards, not images.** The zone runs them. How each player
sees them depends on their client:
- our clients get native HUD data;
- Continuum gets an LVZ fallback built from the stock digits.

The game mode author never handles LVZ ids for a timer.

### 1. A timer in a game mode

```json
{ "id": "hold", "label": "Hold", "seconds": 360,
  "show": { "anchor": "top-centre", "dx": 0, "dy": 40, "style": "large" },
  "start":   ["flag.hold.started"],
  "stop":    ["flag.hold.cancelled", "flag.hold.won"],
  "restart": [],
  "onEnd":   [{ "do": "win", "who": "holder" }] }
```

- The `show` anchor is one of the 12 HUD anchors the LVZ screen designer already uses (`lvz-screen.js`), so a
  timer is dragged onto the same mock screen as an image.
- **Events that can start, stop or restart a timer.** The zone raises these already, or will in Phase 4:
  - `round.started`, `round.ended`;
  - `flag.hold.started`, `flag.hold.cancelled` and `flag.hold.won` (they exist since the flag hold fix);
  - `flag.taken`, `flag.dropped`;
  - `king.started`, `king.ended`;
  - `goal.scored`;
  - `base.captured` (Phase 4);
  - `players.atLeast:N`;
  - `timer.ended:<id>` (chains one timer to another);
  - a staff or owner command (`?timer start hold`).
- **What can happen when it ends** (`onEnd`):
  - end the round (most flags or points wins, as the round clock does now);
  - win for a team;
  - give points;
  - reset the flags;
  - show a banner;
  - start another timer;
  - send an arena message.
- **Scoreboards** work the same way. Each board names its source, such as team kills, flags held,
  goals or round wins, and where it sits on the screen.

The flag hold countdown and the round clock built today become the first two timers, with nothing
lost: their announcements stay.

### 2. Our clients: a small "Dare HUD" packet

- **Hello (client to zone).** After entering an arena, oss-client sends a one-off packet on an unused
  C2S type byte: `[type][version=1][capabilities]`. The plugin registers that type with
  `INetwork.AddPacket`, and SubspaceServer allows a module to add one. It's needed because the zone
  can't otherwise tell oss-client from VIE: both log in as SubSpace version 134.
- **HUD updates (zone to client).** These go only to players who sent the hello, on an unused S2C type
  byte, reliable. Continuum never receives one. They carry:
  - `timer set {id, label, endsAt in server time, paused, style, anchor+offset}`
  - `timer clear {id}`
  - `board set {id, title, rows[], anchor}`
  - `banner {text or stock id, seconds, style}`

  The client counts down by itself from `endsAt`, so a 6-minute timer costs one packet, not 360.
- **Drawing.** The client draws these on the HUD layer in its own font. It uses the anchor positions it
  already computes (`hud.rs` `screen_refs()`, the same ones `lvz-screen.js` follows).
- **Why not chat or LVZ for this?** Chat text with a hidden prefix would show up as junk in
  Continuum. LVZ can't show a live number. The voice menu's "chat sound 128+" trick suits one sound,
  not structured data.

### 3. Continuum: an LVZ fallback the zone builds itself

When a game mode with a visible timer is applied, the zone adds objects for that timer to the
arena's `gamemode.lvz` (ids 2000–2999):
- 4 positions × 10 digits plus a colon, using the stock digit images;
- off by default.

Each second it switches on only the digits that change, usually one or two toggles in one packet.
Our clients don't need these objects. The zone sends them only to Continuum players, so oss-client
players don't see a second copy.

For short fixed countdowns (3-2-1-GO!, 10→0), the zone can start the stock animated countdown with a
single packet.

## Cost and order

1. Zone: run timers and boards, and raise events (the flag hold and round clock become timers). This
   is in Phase 4's `Dare.GameRules` anyway.
2. Our clients: the hello, the HUD packet and native drawing. This is client work in oss-client and
   dare-client, which another session can build against the packet spec.
3. The Continuum LVZ fallback (digit objects in `gamemode.lvz`, toggling).
4. Wizard: a Timers panel, and timers dragged onto the screen mock.

Steps 1 and 2 give our clients live timers first. Step 3 brings Continuum players along.

## Questions for Chris

1. Should Continuum get the LVZ fallback in the first version, or later (our clients first)?
2. Should timers and boards be visible to everyone in the arena, or can some be per team (e.g. your
   own team's hold timer)?

## Not in this proposal

- Changing how LVZ images work.
- Replacing LVZ for decorative screen or map images.
- Sounds.
