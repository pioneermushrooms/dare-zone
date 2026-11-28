# Game mode — Phase 1: game mode record and ship wizard (draft for Chris's approval)

Date: 2026-11-27 · Branch `claude/game-mode-wizard-904376` · Parent design:
`2026-11-27-game-mode-wizard-design.md` (approved). Mockup: `docs/design/game-mode-phase1-mockup.html`.

## What this phase delivers

1. **Game modes you keep.** A new "Game mode" tab in the arena editor lists your account's game
   modes. You can create, open, rename, duplicate and delete them, and apply one to an arena you
   own or co-mod. A game mode belongs to your account, not to the pilot name you're signed in as.
2. **The Ships panel.** It's the first working panel of a game mode:
   - Give each of the 8 ships a role.
   - Set the arena's pace and how many items there are.
   - Choose upgrades (greens) and starting items.
   - Fine-tune any ship value.
   The wizard turns all of this into ship and prize settings, and you see exactly which ones
   before applying.

No server module and no zone restart. The new code is a store table, a small web API and the page.

## Out of this phase

- The other panels (Objective, Teams & lives, Scoring, Time, Flags, Ball) and the "first time?"
  walkthrough. They're Phase 2. Phase 1 shows them as coming.
- **Spawning with random upgrades.** Continuum has no setting for it; the server has to hand out
  prizes on spawn. That's an action in the Phase 4 rules module. Phase 1 offers "fixed" (start with
  set levels) and shows "random" as coming with Phase 4.
- Allowing or forbidding a ship. The server's `LegalShip` enforcer covers that; it goes with
  Teams in Phase 2.

## Saving

**Table `pilot_game_modes`** (in `ArenaStore`, next to the settings sets):

| column | |
|---|---|
| `id` | integer key |
| `person_id` | the account (person) that owns it, from the account session's `AccountStore.PersonOf` |
| `made_by` | pilot name that created it (credit only) |
| `name` | up to 40 characters, same cleaning as settings sets |
| `data` | JSON: wizard answers (per panel), ship roles and tweaks; later labels, objects, rules |
| `settings` | JSON `Section:Key → value`, recomputed from `data` on every save |
| `updated` | time |

- Each account can keep up to 25 game modes, and each holds up to 2000 settings. `settings` is
  checked with the settings sets' own rule (`ArenaStore.TryCheckSettingSet`).
- `data` is kept up to 64 KB and has a `version` number, so later phases can add fields.
- **The page builds the settings and the server re-checks them.** The roles, pace and item tables
  live in one JS file the page uses, `gamemode-ships.js`. The server stores the result after the
  same checks as a settings set. This keeps one copy of the maths: the page needs it anyway for
  the live preview, and apply checks every value again (allow-list, min/max). My call; the
  alternative is a C# copy of the tables.

**API** (under `/arena/api`, signed in; POSTs need the usual Origin and `X-Requested-With: dare-arena`):

| endpoint | does |
|---|---|
| `GET gamemodes` | `{ok, max, modes:[{id, name, madeBy, count, updated}]}` for the account |
| `GET gamemodes/get?id=` | `{ok, id, name, madeBy, data, settings}` |
| `POST gamemodes/save` | `{id?, name, data, settings}`: create or replace (only its own) |
| `POST gamemodes/rename`, `/duplicate`, `/remove` | `{id[, name]}` |

**Apply** doesn't need a new endpoint. The page sends the game mode's `settings` inline to the
existing `/settings/preview`, shows the rows, and on "Apply" sends the same values to
`/settings/apply`. The usual rules hold: owner or co-mod only, the allow-list, value checks,
3 applies a minute, and a pending-restart note when needed. "Save as settings set" copies the
values into your normal settings sets.

**Account key dependency:** `person_id` comes from the account session, whose design Chris hasn't
approved yet. If that work isn't on `main` when Phase 1 is ready, Phase 1 waits for it rather than
keying on names and migrating later.

## Ships panel

### Pace (whole arena, 5 steps)

Slow & deliberate · Steady · **Standard** · Fast · Crazy mayhem. "Standard" is Foundation's
current stock ship values (`conf/svs`). Each step scales every ship after its role is applied:

| | Slow | Steady | Standard | Fast | Mayhem |
|---|---|---|---|---|---|
| speed, thrust, rotation (initial + max) | ×0.75 | ×0.9 | ×1 | ×1.15 | ×1.3 |
| bullet / bomb fire delay | ×1.8 | ×1.3 | ×1 | ×0.75 | ×0.5 |
| fire energy (bullets, bombs, mines) | ×1.4 | ×1.15 | ×1 | ×0.85 | ×0.6 |
| recharge (initial + max) | ×0.8 | ×0.9 | ×1 | ×1.15 | ×1.4 |
| max mines | −2 | −1 | 0 | +1 | +3 |

Every result is rounded and clamped to the setting's spec, so the wizard never produces a value
that apply would skip. The server's stricter checks (on `main` since 0cd91be) require ints to fit
their type and the help's Min/Max, bools to be 0/1, and enums to be a choice. The page reads each
spec from the settings API; the server side uses `ArenaOwnership.SpecOf(section, key)`.

### Items (whole arena, 5 steps)

None · Few · **Standard** · Lots · Loads.

- Greens on the map: `Prize:PrizeFactor`, `PrizeMaxExist`, `PrizeMinExist`, `MultiPrizeCount`.
- Item caps per ship: `RepelMax`, `BurstMax`, `DecoyMax`, `ThorMax`, `BrickMax`, `PortalMax`,
  `RocketMax`.
- "None" sets `PrizeFactor` 0 (no greens appear at all) and caps of 0 unless a role needs the item.

### Upgrades

- **Off / Slow / Standard / Fast.**
  - Off: initial values equal the maximum values, and the upgrade prize weights
    (Gun, Bomb, Energy, Recharge, Thruster, TopSpeed, Rotation, MultiFire...) are 0.
  - Slow / Fast: scale the `Upgrade*` amounts per green and those prize weights.
- **Start with:** *fixed* (the ship's initial levels; set per role and editable) or *random*
  (coming with Phase 4).
- **Pilots can pick up greens: yes / no.** Continuum has no per-ship switch, so this is
  arena-wide: "no" means no greens appear (`PrizeFactor` 0). Items then come only from starting
  items. The page says so.

### Starting items (whole arena, per item; a ship's role can raise it)

Repels, bursts, decoys, thors, bricks, rockets, portals: 0–3 each, clamped to that ship's cap.

### Roles

A role is a set of values on top of the Standard ship, before pace is applied. Proposed set
(exact numbers are tuned while building, by flying them):

| Role | Feel | What it changes |
|---|---|---|
| **Standard** | stock ship | nothing |
| **Gunner** | many fast bullets | guns L2→L3, double barrel, short bullet delay, bombs L1 max |
| **Bomber** | heavy bombs, slow | bombs L2→L3, 1–2 bounces, more shrapnel, more mines, high bomb energy, lower speed |
| **Sniper** | long range, picks shots | very fast bullets, long delay, guns L3 (gun level is how damage differs per ship: damage per level is arena-wide), low thrust, X-radar on |
| **Cloaker** | ambush | cloak and stealth start on, X-radar off, cheap cloak, light guns |
| **Deployer** | carries the team in | turret slots (others attach to it), portals to start, bricks, sturdy |
| **Anchor** | the team's tank and rally point | high energy and recharge, turret slots, antiwarp starts on, slow, repels |
| **Interceptor** | dogfighter | top rotation and speed, rockets to start, low energy |
| **Support** | keeps the team alive | repels, decoys, bricks and bursts to start, medium everything |
| **Flag runner** | outruns everyone | highest speed and thrust, cheap afterburner, weak guns |
| **Brawler** | close range | high energy, wide multifire, slow bullets, short delay |

Attaching (my call, change if you prefer): only ships with turret slots can be attached to.
Deployer and Anchor get `TurretLimit` 4–5 and every other role 0, so the role decides who carries
the team. Today every stock ship has 5. Attaching also needs bounty: `AttachBounty` on the ship
that attaches drops to 0 for all roles, so a newly spawned pilot can attach at once.

Default roles when a game mode is created: Warbird Gunner, Javelin Bomber, Spider Interceptor,
Leviathan Anchor, Terrier Deployer, Weasel Cloaker, Lancaster Sniper, Shark Support.

### Fine-tune

Each ship card has "Fine-tune". It opens that ship's values in groups: Movement, Energy, Guns,
Bombs & mines, Items, Specials (cloak, stealth, X-radar, antiwarp, turrets). They use the same
sliders, switches and lists as the Settings page, with the min/max the server sends. A tuned value
is marked and kept when pace or items change (it wins over the computed value). Changing the role
asks first: "Keep your 6 tweaks?".

### What you see before applying

The panel footer shows "This game mode sets 214 settings" with a **Show** link listing each one
(grouped by ship), and **Apply to arena** with an arena picker (your arenas and drafts). Apply
opens the preview first: what changes, what stays, anything skipped and why. Then "Apply" or
"Cancel".

## Pages and files

- `arena.html`: a "Game mode" entry in the side nav (between Settings and Messages), and the
  section with the game mode list and the panel tabs.
- `gamemode.js`: the tab (list, open, save, apply).
- `gamemode-ships.js`: the role/pace/items tables and `GameModeShips.settings(data, help)`. It's a
  UMD module like `tiles.js`, so `node --test` covers it.
- Server: `ArenaStore.GameModes.cs` (table, caps, checks), `Web/GameModesApi.cs` (routes), and a
  route line in `WebServer.cs`.

## Tests

- `dotnet test`: the store (caps, per-account access, a second name on the same account sees the
  same modes, a name on another account doesn't, bad JSON refused) and the API (auth, Origin).
- `node --test`:
  - every role × pace × items × upgrades combination gives only values within config help min/max;
  - Standard/Standard/Standard gives the stock values back;
  - tweaks win over pace;
  - "no greens" sets `PrizeFactor` 0.
- Browser, against a local zone: make a game mode, set roles, apply to a draft, fly each ship in
  the game client, and check the values with `?geta` (spot check).
