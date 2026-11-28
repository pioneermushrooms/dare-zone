# Game mode Phase 1 (game mode record + ship wizard) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Owners keep named game modes on their account and use a Ships panel to give each ship a role, set pace, items, upgrades and starting items, then apply the resulting settings to an arena they own or co-mod.

**Architecture:**
- A new SQLite table `pilot_game_modes` in `ArenaStore` is keyed by the account's person id, with a small JSON API.
- A DOM-free UMD module `gamemode-ships.js` turns the panel's answers into `Section:Key → value` settings, clamped to each setting's spec.
- A new "Game mode" tab in `/arena/me` (`gamemode.js`) edits and saves game modes. It applies them through the existing `/api/settings/preview` and `/api/settings/apply`.

**Tech Stack:** C# (.NET, xUnit, Microsoft.Data.Sqlite) in `src/Dare.Arenas.Core` and `src/Dare.Arenas`; plain browser JS (no build step) in `src/Dare.Arenas/Web/wwwroot`; `node --test tests/js`.

**Spec:** `docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md` (parent: `2026-11-27-game-mode-wizard-design.md`). Mockup: `docs/design/game-mode-phase1-mockup.html`.

## Global Constraints

- A game mode belongs to the account (person), never to a pilot name. `made_by` is the creating pilot name, kept for credit only.
- 25 game modes per person (`MaxPilotGameModes = 25`). `settings` holds at most 2000 entries and is checked with `ArenaStore.TryCheckSettingSet`. `data` is at most 64 KB, a JSON object with an integer `version` (1 for this phase).
- Names: same cleaning as settings sets (`ArenaStore.SettingSetName`), at most 40 characters.
- POSTs need `X-Requested-With: dare-arena` and a site Origin (the existing `Gate` rules). A weblogin-only session has no account and gets 401 "Sign in with your account to keep game modes."
- Apply only through `/api/settings/preview` then `/api/settings/apply` with inline `settings:{...}`. No new apply path.
- Booleans are written as "1"/"0". Ship values go in one entry per ship section (`Warbird`, `Javelin`, `Spider`, `Leviathan`, `Terrier`, `Weasel`, `Lancaster`, `Shark`), never `[All]`.
- Every generated value fits its spec: an int within min/max (and its type's range), a bool 0/1, an enum one of its choices (server checks from 0cd91be).
- "Standard" means the stock values in SubspaceServer's `Zone/conf/svs` (ship-*.conf, `prizeweights`, `[Prize]` in `misc`), which the template includes.
- No `alert`/`confirm`/`prompt`: use `Dlg.confirm`, `Dlg.prompt`, `Dlg.alert` and `Dlg.choose` from `dialogs.js`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The game mode was saved from a different pilot name on the same account.** The second name sees, edits and applies it, and a name on another account can't see it. Pinned in Task 1 (`second_name_on_same_person_sees_mode`). Task 5 checks it end to end.
2. **Applying to an arena whose allow-list lacks some generated keys.** Only keys in that arena's editable settings are sent, so nothing is skipped for being off-list. Pinned in Task 2 (`settings_only_includes_keys_with_specs`).
3. **Fine-tune values typed out of range, or an old saved tweak whose limits changed.** They're clamped on every recompute, never sent raw. Pinned in Task 2 (`tweaks_are_clamped_to_spec`).
4. **Starting items above a ship's cap (e.g. Items "None" and Repel 3).** The start is limited to the cap, and a role needing an item keeps a cap of at least its start. Pinned in Task 2 (`start_items_never_exceed_cap`).
5. **Corrupt or oversized `data` posted by a modified page.** Refused with 400 and the stored mode unchanged. Pinned in Task 1 (`refuses_bad_or_oversized_data`).

---

### Task 1: Game mode store

**Files:**
- Create: `src/Dare.Arenas.Core/ArenaStore.GameModes.cs`
- Modify: `src/Dare.Arenas.Core/ArenaStore.cs` (call `EnsureGameModeTable()` next to `EnsureSettingSetTable()`, line ~106)
- Test: `tests/Dare.Arenas.Tests/PilotGameModesTests.cs` (same fixture shape as `PilotSettingSetsTests`: temp db, fixed clock)

**Interfaces:**
- Produces:
  - `public sealed record PilotGameMode(long Id, string Name, string MadeBy, int Count, DateTimeOffset Updated);`
  - `public const int MaxPilotGameModes = 25; public const int MaxGameModeDataBytes = 64 * 1024;`
  - `IReadOnlyList<PilotGameMode> GameModes(long person)`: newest first.
  - `(string Name, string MadeBy, string Data, IReadOnlyDictionary<string,string> Settings)? GameModeOf(long person, long id)`
  - `Result SaveGameMode(long person, string madeBy, long? id, string name, string data, IReadOnlyDictionary<string,string> settings, out long savedId)`
  - `Result RenameGameMode(long person, long id, string name)`
  - `Result DuplicateGameMode(long person, long id, out long newId)`: the copy is named "<name> (copy)", trimmed to 40, and counts against the cap.
  - `Result RemoveGameMode(long person, long id)`
  - `public static bool TryCheckGameModeData(string data, out string error)`: non-empty, ≤ `MaxGameModeDataBytes` UTF-8 bytes, parses as a JSON object, and has an integer `version` ≥ 1.

Table: `pilot_game_modes(id INTEGER PRIMARY KEY AUTOINCREMENT, person_id INTEGER NOT NULL, made_by TEXT NOT NULL, name TEXT NOT NULL, data TEXT NOT NULL, settings TEXT NOT NULL, count INTEGER NOT NULL, updated TEXT NOT NULL)`, plus an index on `person_id`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void saves_lists_and_reads_back_a_mode()
{
    var r = _s.SaveGameMode(7, "Chris", null, "Fast CTF", "{\"version\":1}", Set(("Warbird:InitialGuns", "2")), out var id);
    Assert.True(r.Ok, r.Message);
    var list = _s.GameModes(7);
    Assert.Equal(("Fast CTF", "Chris", 1), (list.Single().Name, list[0].MadeBy, list[0].Count));
    Assert.Equal("2", _s.GameModeOf(7, id)!.Value.Settings["Warbird:InitialGuns"]);
    Assert.Equal("{\"version\":1}", _s.GameModeOf(7, id)!.Value.Data);
}
[Fact] public void second_name_on_same_person_sees_mode()
{   // the store keys on person only; made_by never restricts access
    _s.SaveGameMode(7, "Chris", null, "M", "{\"version\":1}", Set(("Warbird:InitialGuns", "2")), out var id);
    Assert.NotNull(_s.GameModeOf(7, id));
    Assert.Null(_s.GameModeOf(8, id));
    Assert.Empty(_s.GameModes(8));
    Assert.False(_s.RenameGameMode(8, id, "x").Ok);
    Assert.False(_s.RemoveGameMode(8, id).Ok);
    Assert.False(_s.SaveGameMode(8, "Other", id, "x", "{\"version\":1}", Set(("Warbird:InitialGuns", "1")), out _).Ok);
}
[Fact] public void refuses_bad_or_oversized_data()
{
    var s = Set(("Warbird:InitialGuns", "2"));
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "", s, out _).Ok);
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "[1]", s, out _).Ok);
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"x\":1}", s, out _).Ok);          // no version
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "{not json", s, out _).Ok);
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"version\":1,\"p\":\"" + new string('a', 70_000) + "\"}", s, out _).Ok);
    Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"version\":1}", Set(("Bad", "1")), out _).Ok); // TryCheckSettingSet
    _s.SaveGameMode(7, "C", null, "M", "{\"version\":1}", s, out var id);
    Assert.False(_s.SaveGameMode(7, "C", id, "M", "[1]", s, out _).Ok);
    Assert.Equal("{\"version\":1}", _s.GameModeOf(7, id)!.Value.Data);                    // unchanged
}
[Fact] public void caps_at_25_per_person_including_duplicates()
{
    long last = 0;
    for (var i = 0; i < 25; i++) Assert.True(_s.SaveGameMode(7, "C", null, $"m{i}", "{\"version\":1}", Set(("Warbird:InitialGuns", "1")), out last).Ok);
    Assert.False(_s.SaveGameMode(7, "C", null, "m25", "{\"version\":1}", Set(("Warbird:InitialGuns", "1")), out _).Ok);
    Assert.False(_s.DuplicateGameMode(7, last, out _).Ok);
    Assert.True(_s.SaveGameMode(8, "D", null, "other", "{\"version\":1}", Set(("Warbird:InitialGuns", "1")), out _).Ok);
}
[Fact] public void rename_duplicate_remove()
{
    _s.SaveGameMode(7, "C", null, "Duel", "{\"version\":1}", Set(("Warbird:InitialGuns", "1")), out var id);
    Assert.True(_s.RenameGameMode(7, id, "Duel 4v4!").Ok);
    Assert.Equal("Duel 4v4", _s.GameModeOf(7, id)!.Value.Name);   // SettingSetName cleaning
    Assert.True(_s.DuplicateGameMode(7, id, out var copy).Ok);
    Assert.Equal("Duel 4v4 (copy)", _s.GameModeOf(7, copy)!.Value.Name);
    Assert.True(_s.RemoveGameMode(7, id).Ok);
    Assert.Null(_s.GameModeOf(7, id));
    Assert.Single(_s.GameModes(7));
}
```

- [ ] **Step 2: Run them and check they fail**
  Run: `dotnet test tests/Dare.Arenas.Tests --filter PilotGameModesTests`
  Expected: build error, `SaveGameMode` doesn't exist.

- [ ] **Step 3: Implement `ArenaStore.GameModes.cs`**
  Follow `ArenaStore.SettingSets.cs`: `Tx`, `Ok`/`Fail`, `lock (_gate)`, settings serialized with `JsonSerializer`. Messages are short and plain, e.g. "You have 25 game modes; delete one to add another." and "That game mode isn't in your collection."

- [ ] **Step 4: Run them and check they pass**
  Run: `dotnet test tests/Dare.Arenas.Tests --filter PilotGameModesTests`
  Expected: 5 passed. Then run `dotnet test` to check nothing else broke.

- [ ] **Step 5: Commit**: "Game modes: pilot_game_modes store keyed by person, caps and checks".

### Task 2: Ship tables and settings maths (`gamemode-ships.js`)

**Files:**
- Create: `src/Dare.Arenas/Web/wwwroot/gamemode-ships.js` (UMD like `tiles.js`, global `GameModeShips`)
- Test: `tests/js/gamemode-ships.test.js`

**Interfaces:**
- Produces (on `GameModeShips`):
  - `SHIPS`: `["Warbird","Javelin","Spider","Leviathan","Terrier","Weasel","Lancaster","Shark"]`
  - `ROLES`: `[{ id, name, blurb }]`. Ids: `standard, gunner, bomber, sniper, cloaker, deployer, anchor, interceptor, support, flagrunner, brawler`.
  - `PACES = ["slow","steady","standard","fast","mayhem"]`, `ITEMS = ["none","few","standard","lots","loads"]`, `UPGRADES = ["off","slow","standard","fast"]`, `START_ITEMS = ["Repel","Burst","Decoy","Thor","Brick","Rocket","Portal"]`.
  - `STANDARD`: `{ Warbird: { Key: number }, …, Prize: {…}, PrizeWeight: {…} }`, copied from SubspaceServer `Zone/conf/svs` (all ship keys except `Soccer*`; `[Prize]` keys `PrizeFactor, PrizeMaxExist, PrizeMinExist, MultiPrizeCount, PrizeDelay, UpgradeVirtual`; every `[PrizeWeight]` key).
  - `defaults()` → `{ pace:"standard", items:"standard", upgrades:"standard", greens:true, start:{Repel:0,…,Portal:0}, roles:{Warbird:"gunner", Javelin:"bomber", Spider:"interceptor", Leviathan:"anchor", Terrier:"deployer", Weasel:"cloaker", Lancaster:"sniper", Shark:"support"}, tweaks:{} }`
  - `settings(ships, specs)` → `{ "Section:Key": "value" }`. `specs` is `{ "Section:Key": { type, min, max, choices } }`, built from the arena's `/api/me` settings list. Only keys present in `specs` are output, and every value is a string.
  - `clamp(value, spec)` → number. Rounds; clamps to `spec.min`/`spec.max` when given, and bools to 0/1.
  - `changedFromRole(ships, ship)` → the count of tweaks on that ship (for the "Keep your N tweaks?" prompt).

**Order of application, per ship:**
1. Start from `STANDARD[ship]`.
2. Apply the role overrides.
3. Apply pace.
4. Apply items caps.
5. Apply upgrades.
6. Apply starting items: `Initial<Item> = max(roleInitial, start[item])`, then `min(…, <Item>Max)`. If the role's own initial is above the cap, raise the cap to it.
7. Apply tweaks (`tweaks["Ship:Key"]` wins).
8. Clamp to the spec.

Arena-wide `Prize`/`PrizeWeight` values come from the items, upgrades and greens choices.

**Values this task pins** (from the spec; role numbers are a first cut, tuned in Task 6 by flying):

- Pace multipliers. "Speed group": `InitialSpeed, MaximumSpeed, InitialThrust, MaximumThrust, InitialRotation, MaximumRotation`. "Delay group": `BulletFireDelay, MultiFireDelay, BombFireDelay, LandmineFireDelay`. "Energy cost": `BulletFireEnergy, MultiFireEnergy, BombFireEnergy, LandmineFireEnergy`. "Recharge": `InitialRecharge, MaximumRecharge`.

  | | slow | steady | standard | fast | mayhem |
  |---|---|---|---|---|---|
  | speed group | 0.75 | 0.9 | 1 | 1.15 | 1.3 |
  | delay group | 1.8 | 1.3 | 1 | 0.75 | 0.5 |
  | energy cost | 1.4 | 1.15 | 1 | 0.85 | 0.6 |
  | recharge | 0.8 | 0.9 | 1 | 1.15 | 1.4 |
  | MaxMines | −2 | −1 | 0 | +1 | +3 |

- Items. Caps are `RepelMax, BurstMax, DecoyMax, ThorMax, BrickMax, PortalMax, RocketMax`:

  | | none | few | standard | lots | loads |
  |---|---|---|---|---|---|
  | Prize:PrizeFactor | 0 | 600 | stock | 2200 | 3500 |
  | Prize:MultiPrizeCount | 0 | 5 | stock | 15 | 20 |
  | item caps | 0 | 1 | stock | 5 | 8 |

- Upgrades:
  - `off`: every `Initial*` movement/energy value equals its `Maximum*`, `InitialGuns = MaxGuns`, `InitialBombs = MaxBombs`, and the weights `Gun, Bomb, Energy, Recharge, Thruster, TopSpeed, Rotation, MultiFire, BouncingBullets, Proximity, Shrapnel, QuickCharge, AllWeapons` are 0.
  - `slow`: `UpgradeEnergy, UpgradeRecharge, UpgradeThrust, UpgradeSpeed, UpgradeRotation` and those weights ×0.5.
  - `fast`: the same ×1.5.
- Greens off: `Prize:PrizeFactor = 0`, overriding items.
- Roles. Every non-standard role sets `AttachBounty 0`, and `TurretLimit 0` unless it says otherwise. Overrides:
  - **gunner**: InitialGuns 2, MaxGuns 3, DoubleBarrel 1, BulletFireDelay 18, InitialBombs 0, MaxBombs 1.
  - **bomber**: InitialBombs 1, MaxBombs 3, BombBounceCount 1, ShrapnelMax 16, MaxMines 6, BombFireEnergy 400, MaximumSpeed 3000, InitialSpeed 1900.
  - **sniper**: BulletSpeed 5000, BulletFireDelay 70, InitialGuns 2, MaxGuns 3, DoubleBarrel 0, MaximumThrust 14, InitialThrust 12, XRadarStatus 2.
  - **cloaker**: CloakStatus 2, StealthStatus 2, XRadarStatus 0, CloakEnergy 50, StealthEnergy 200, MaxGuns 2.
  - **deployer**: TurretLimit 5, InitialPortal 1, PortalMax 3, InitialBrick 1, BrickMax 3, MaximumEnergy 1900.
  - **anchor**: TurretLimit 5, MaximumEnergy 2400, InitialEnergy 1500, MaximumRecharge 1500, AntiWarpStatus 2, MaximumSpeed 2800, InitialSpeed 1800, InitialRepel 1.
  - **interceptor**: MaximumRotation 350, InitialRotation 260, MaximumSpeed 3800, InitialRocket 2, MaximumEnergy 1400, InitialEnergy 900.
  - **support**: InitialRepel 2, RepelMax 4, InitialDecoy 1, InitialBrick 1, InitialBurst 1.
  - **flagrunner**: MaximumSpeed 4200, InitialSpeed 2600, MaximumThrust 22, InitialThrust 18, AfterburnerEnergy 700, MaxGuns 2, InitialGuns 1.
  - **brawler**: MaximumEnergy 2200, InitialEnergy 1400, MultiFireAngle 900, MultiFireDelay 30, BulletSpeed 1400, BulletFireDelay 20.
  - **standard**: nothing (keeps stock `TurretLimit`/`AttachBounty`).

- [ ] **Step 1: Write the failing tests** in `tests/js/gamemode-ships.test.js`.
  Test helper: `specsAll()` builds a spec for every key in `STANDARD` (type int, no min/max), plus min/max for the bit fields `InitialGuns/MaxGuns/InitialBombs/MaxBombs 0..3` and `CloakStatus/StealthStatus/XRadarStatus/AntiWarpStatus 0..2`.

```js
test("standard everything with standard roles gives the stock values back", () => {
  const d = { ...GameModeShips.defaults(), roles: Object.fromEntries(GameModeShips.SHIPS.map((s) => [s, "standard"])) };
  const out = GameModeShips.settings(d, specsAll());
  for (const ship of GameModeShips.SHIPS)
    for (const [k, v] of Object.entries(GameModeShips.STANDARD[ship])) assert.equal(out[`${ship}:${k}`], String(v), `${ship}:${k}`);
});
test("STANDARD matches SubspaceServer's conf/svs when it is on this machine", (t) => { /* read ~/SubspaceServer/src/SubspaceServer/Zone/conf/svs/ship-*.conf; t.skip() if missing; compare every key */ });
test("every role x pace x items x upgrades x greens stays within spec", () => { /* loop all; for each output key with spec min/max assert in range; all values are integer strings */ });
test("settings_only_includes_keys_with_specs", () => {
  const out = GameModeShips.settings(GameModeShips.defaults(), { "Warbird:InitialGuns": { type: "int", min: 0, max: 3 } });
  assert.deepEqual(Object.keys(out), ["Warbird:InitialGuns"]);
});
test("tweaks_are_clamped_to_spec and win over pace", () => {
  const d = { ...GameModeShips.defaults(), pace: "mayhem", tweaks: { "Warbird:MaxGuns": "9", "Warbird:MaximumSpeed": "3000" } };
  const out = GameModeShips.settings(d, specsAll());
  assert.equal(out["Warbird:MaxGuns"], "3");
  assert.equal(out["Warbird:MaximumSpeed"], "3000");
});
test("start_items_never_exceed_cap", () => {
  const d = { ...GameModeShips.defaults(), items: "none", start: { ...GameModeShips.defaults().start, Repel: 3 } };
  const out = GameModeShips.settings(d, specsAll());
  for (const s of GameModeShips.SHIPS) assert.ok(+out[`${s}:InitialRepel`] <= +out[`${s}:RepelMax`], s);
  assert.equal(out["Shark:InitialRepel"], "2"); assert.ok(+out["Shark:RepelMax"] >= 2); // support role keeps its own start
});
test("greens off sets PrizeFactor 0", () => {
  assert.equal(GameModeShips.settings({ ...GameModeShips.defaults(), greens: false }, specsAll())["Prize:PrizeFactor"], "0");
});
test("upgrades off: initial equals maximum and upgrade weights are 0", () => { /* Warbird:InitialSpeed === Warbird:MaximumSpeed; Warbird:InitialGuns === Warbird:MaxGuns; PrizeWeight:Gun === "0" */ });
test("only deployer and anchor carry turrets; every role lets pilots attach at once", () => { /* Terrier/Leviathan TurretLimit 5, Warbird 0, all AttachBounty 0 */ });
```

- [ ] **Step 2: Run them and check they fail**
  Run: `node --test tests/js/gamemode-ships.test.js`. Expected: fails with "Cannot find module".
- [ ] **Step 3: Implement `gamemode-ships.js`** with the tables and order above. Fill in `STANDARD` by copying the svs files once by hand. The "matches conf/svs" test guards the copy.
- [ ] **Step 4: Run them and check they pass**
  Run: `node --test tests/js`. Expected: all pass, the svs test passes here and skips where the files are absent.
- [ ] **Step 5: Commit**: "Game modes: ship roles, pace, items and upgrades as settings (gamemode-ships.js)".

### Task 3: Game mode web API

**Files:**
- Create: `src/Dare.Arenas/Web/GameModesApi.cs` (`internal sealed partial class WebServer`, like `SettingSetsApi.cs`)
- Modify: `src/Dare.Arenas/Web/WebServer.cs` (add `?? await GameModesRouteAsync(req, path, method)` next to `SettingSetsRouteAsync`, line ~507)

**Interfaces:**
- Consumes: Task 1's store methods; `Gate(req, write, out player)`; `CurrentAccount(req)`; `ReadJsonAsync`, `SettingsMap`, `Str`, `JsonReply`, `ResultReply`.
- Produces:
  - `GET /arena/api/gamemodes` → `{ok, max, modes:[{id,name,madeBy,count,updated}]}`
  - `GET /arena/api/gamemodes/get?id=` → `{ok,id,name,madeBy,data,settings}` (`data` is the stored JSON object, not a string)
  - `POST /arena/api/gamemodes/save {id?, name, data, settings}` → `{ok,message,id}`
  - `POST /arena/api/gamemodes/rename {id,name}`, `/duplicate {id}` → `{ok,message,id}`, `/remove {id}`
  - `private long? PersonKey(HttpListenerRequest req)`: the only place the person is resolved. It returns `CurrentAccount(req) is { } cur ? _m.Accounts.PersonOf(cur.Account.Id) : null` (the account-person key, on `main` since 6a397a2). A null result means 401 with "Sign in with your account to keep game modes."

- [ ] **Step 1: Implement the routes.**
  - `data` is posted as a JSON object; serialize it with `GetRawText()` before `SaveGameMode`.
  - The body limit is `MaxSettingsJsonBytes + MaxGameModeDataBytes`.
  - `madeBy` is the `player` from `Gate`.
- [ ] **Step 2: Build and check it compiles.**
  Run: `dotnet build`. Expected: 0 errors.
- [ ] **Step 3: Check against a local zone.** Follow the notes' local-zone tip: copy `~/dare-zone/.local-zone/zone` to the scratchpad, set the port, `WebListen` and `WebBaseUrl`, and run with `DOTNET_ROLL_FORWARD=Major`. Sign in with a throwaway account, then use `curl` with the session cookie and `X-Requested-With: dare-arena` to:
  - save → list → get → rename → duplicate → remove;
  - check that a weblogin-only session gets 401;
  - check that a POST without the header gets 403.
- [ ] **Step 4: Commit**: "Game modes: /arena/api/gamemodes list/get/save/rename/duplicate/remove".

### Task 4: Game mode tab and Ships panel

**Files:**
- Create: `src/Dare.Arenas/Web/wwwroot/gamemode.js`
- Modify:
  - `src/Dare.Arenas/Web/wwwroot/arena.html`:
    - nav link `<a href="#gamemode" data-view="gamemode" data-needs="arena">Game mode</a>` between Settings and Messages;
    - `<section data-view="gamemode" hidden>` with the hosts below;
    - scripts `/arena/gamemode-ships.js` and `/arena/gamemode.js` after `app.js`, both with `defer`.
  - `src/Dare.Arenas/Web/wwwroot/style.css`: `.gm*` classes following the mockup.
  - `src/Dare.Arenas/Web/WebServer.cs`: serve `gamemode.js` and `gamemode-ships.js` if static files are listed explicitly (check how `lvz.js` is served and do the same).
  - `deploy/install-public-site.sh`: only if `/arena/*` static files are listed there (check).

**Interfaces:**
- Consumes:
  - From `app.js`: `$`, `el`, `getJson`, `post`, and `current` (the `/api/me` body, whose `settings` are sections of `{key, value, type, min, max, choices}`).
  - `GameModeShips` (Task 2), the Task 3 endpoints, and `Dlg`.
- Produces: `GameModeTab.show()`, called when the view opens. It hooks `showView`'s existing pattern; read how other views load and follow it.

What the tab does (layout as in the mockup):
- **Top row:** a game mode picker (`+ New game mode` last), Rename, Duplicate, Delete, and the note "Saved to your account: every pilot name on it sees these."
- **Panel tabs:** Objective, Teams & lives, Scoring, Time, Flags / ball, Ships, Timers & rules. Only Ships is enabled; the rest show "Coming next" when clicked.
- **Ships panel:**
  - Pace and Items as 5-segment controls; Upgrades as 4 segments.
  - "Start with: fixed levels" plus a disabled "random (coming with rules)".
  - "Pilots pick up greens" yes/no. When "no", note: "No greens appear in this arena; items come only from starting items."
  - Starting items as 7 number boxes (0–3).
  - 8 ship cards, each with a role select, the role's blurb and Fine-tune.
- **Fine-tune drawer:** that ship's keys grouped as Movement, Energy, Guns, Bombs & mines, Items, Specials.
  - It reuses the Settings page's control kinds (switch, slider plus number, select) from each spec.
  - A changed value is stored in `tweaks` and marked with •.
  - Changing the role with tweaks present asks `Dlg.choose("Keep your N tweaks?", …)` with choices Keep / Clear.
- **Footer:**
  - "This game mode sets N settings · Show · Save as settings set". Show lists them grouped by section; Save as settings set posts to `/api/settingsets/save`.
  - Arena picker (the arenas from `current`) and "Apply to arena…". It posts `/api/settings/preview {arena, settings}` and shows the rows table: change / same / skipped with reason, and counts. Apply posts `/api/settings/apply` with the same body and shows the message, including any pending-restart text.
- **Saving:** autosaves 800 ms after the last change through `gamemodes/save`, with `data = { version: 1, ships }`, and shows "Saved".
- **Errors:** shown inline in the footer, never as alerts.

- [ ] **Step 1: Build the tab and panel** as above.
- [ ] **Step 2: Check in the browser against the local zone (Task 3's zone).**
  - Create a mode, change roles, pace, items and greens, and check the settings count and the Show list update.
  - Reload and check everything persists.
  - Fine-tune a value, change pace, and check the tweak stays.
  - Preview and apply to a draft you own, then check `?geta Warbird:InitialGuns` in game (or the Settings tab).
- [ ] **Step 3: Check at 375 px width: no horizontal scroll, and the cards wrap to 2 columns.**
- [ ] **Step 4: Commit**: "Game mode tab: game modes and the Ships panel (roles, pace, items, upgrades, fine-tune, apply)".

### Task 5: Two-name check, fly-test, merge

- [ ] **Step 1:** `git merge origin/main`. The person key is already in use from Task 3.
- [ ] **Step 2:** In the browser, make two pilot names on one account. Save a mode as name A, switch to name B, and check B sees and edits it. A name on another account must not see it.
- [ ] **Step 3:** Fly each role on the local zone in the game client (Dare client or Continuum). Tune the role numbers in `gamemode-ships.js` where a role doesn't feel like its name. Rerun `node --test tests/js`.
- [ ] **Step 4:** Run `dotnet test` and `node --test tests/js`. Both must pass.
- [ ] **Step 5:** Commit, then `git merge origin/main`, retest, merge into `main`, `git push origin main`. Do not deploy; ask Chris.
