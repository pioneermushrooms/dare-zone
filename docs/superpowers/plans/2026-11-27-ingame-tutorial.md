# In-game Tutorial Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** New Foundation pilots are taken into a private, mentor-narrated tutorial arena that reveals the HUD piece by piece, checks each step on the server, and sends them on to where people play. It works in Continuum and the browser.

**Architecture:** The zone owns everything that decides anything: who is new, the step machine, checks, prizes, LVZ covers, the bot cast. It lives as a new `ArenaOwnership.Tutorial` partial over pure rules in `Dare.Arenas.Core`, unit-tested without a server. One JSON script drives both the zone and a Node tool that generates the tutorial LVZ (covers, mentor box, one image per line, for Continuum). The browser gets the mentor box as a DOM overlay (`site/tutorial.js`) fed by hint lines that the Dare client hides from chat and passes to the page. The swarm is a new `oss-bot` behaviour.

**Tech Stack:** C#/.NET 9 (SubspaceServer plugin, xUnit), Node (lvz.js tool + `node --test`), Rust (oss-bot, oss-client wasm, `cargo test`), vanilla JS/CSS on the site.

**Spec:** `docs/superpowers/specs/2026-11-27-ingame-tutorial-design.md`

## Global Constraints

- Up to **10** tutorials at once get the full cast (target, 5-bot swarm, teammate); each extra one gets **one** bot that plays every part; at **30** running, a newcomer gets the "Try the tutorial" prompt instead. All three are settings: `[Tutorial] FullCasts=10`, `MaxTutorials=30`, plus `Enabled=1`.
- New account = never logged in through the game. All accounts that exist when the table is first created count as not new.
- A second mid-tutorial drop-out by an account = left; it is no longer started for them automatically.
- The tutorial takes over only on entry to a public (starting) arena; a newcomer who picks another arena is prompted once.
- Bots (bot-pass sessions) and fake players are never taken in.
- A replay (`?tutorial`) ends back in the arena it was started from; a newcomer's first run ends in the public arena with the most real pilots, or, with nobody on, the main public arena plus the arena list (browser) or a line about arenas with bots (Continuum).
- Mentor box bottom-left, above the chat; covers on the TopMost layer.
- Wording lives only in the script; Continuum's images are regenerated from it, never hand-drawn.
- Ship: merge `origin/main` / `dare/main` into the branches, test, merge into `main` in both repos, push. **No deploy without asking Chris.**

## Review Focus

- **The player disconnects or leaves the arena mid-step** (closing the tab, `?go` elsewhere). The tutorial state, covers, bots and arena are cleaned up, and the drop-out is counted once. Tests: `Task 3: DropOut_CountsOncePerRun`, and `Task 8`'s play-through leaves via `?go`.
- **The player dies during the swarm or any step.** The step continues after respawn, and Engine Shutdown is re-applied in act 2. Test: `Task 2: Death_DoesNotResetStep`.
- **Two newcomers enter at the same moment.** Each gets its own `#trainingN`, with no shared bots and no name clash. Test: `Task 4: Allocate_GivesDistinctArenas`.
- **A step can't complete** (the bot host fails to start, or the flag was taken by a bot). The 60 s nudge and Skip always work, and a step without bots never waits on one. Test: `Task 2: Skip_AlwaysAdvances` and `Task 4: Cast_WhenHostDown_StepsDontWaitOnBots`.
- **A player types normal commands in the tutorial** (`?go public`, a ship change in act 1). Leaving through `?go` ends the tutorial like `?leave`. An early ship change is accepted, and they're put back in a Warbird. Test: `Task 7` step 4.

---

### Task 0: Spike on a local zone (throwaway; findings go into the plan before Task 1 starts)

**Files:** none committed except notes in this plan's "Spike findings" section below.

- [ ] **Step 1:** Build and run a local zone. Run `dotnet build` and then `deploy/apply-local.sh <zoneDir> out/modules/Dare.Arenas`. Find or make the zone dir per `deploy/README.md`. Start Continuum (`~/Applications/Continuum.app`) and the browser client (`~/oss-src/<worktree>/web`) against it.
- [ ] **Step 2: Covers.** Hand-make a tiny LVZ with `src/Dare.Arenas/Web/wwwroot/lvz.js`: one black 200×200 screen object, mode ShowAlways (0), layer TopMost (7), id 1000, anchored to the radar's CLV2 reference. Put it in a test arena (`Misc:LevelFiles`). Toggle it off with a throwaway `?objoff`-style call (`ILvzObjects.Toggle`). Record whether Continuum and the browser both hide it, and whether any HUD flashes on entry. If Continuum ignores it: use mode ServerControlled, toggled on in `PlayerAction.EnterArena`, and record which.
- [ ] **Step 3: `#training` config.** Create `arenas/#training/arena.conf` and enter `#training1`. Record whether the config loads (the server log names the conf). If not, record the fallback used (hidden numbered `training1` via `ArenaStore.Visibility`), which Task 4 then uses.
- [ ] **Step 4: Super.** `?prize` Super to a Warbird. Record what changes: bullet damage, or energy cost only.
- [ ] **Step 5: Commands.** Run `?help skip`, `?help leave` and `?help tutorial`. Record any that are already taken; a taken name is replaced by `?tskip` / `?tleave` in every task below.
- [ ] **Step 6:** Write the four findings into "Spike findings" below and commit the plan.

### Spike findings

(filled in by Task 0)

---

### Task 1: Tutorial script and its model

**Files:**
- Create: `src/Dare.Arenas/tutorial/script.json` (embedded resource, as `voice.phrases.json` is)
- Create: `src/Dare.Arenas.Core/TutorialScript.cs`
- Modify: `src/Dare.Arenas/Dare.Arenas.csproj` (embed `tutorial/script.json` as `tutorial.script.json`)
- Test: `tests/Dare.Arenas.Tests/TutorialScriptTests.cs`

**Interfaces:**
- Produces:
  - `enum HudPart { Radar, Energy, StatBox, Items, Chat }`
  - `enum CheckKind { None, Rotate, Reach, Weapon, Kills, KillAll, SafeZone, Green, PublicChat, ShipChange, Flag, BallGoal }`
  - `record TutorialCheck(CheckKind Kind, int X = 0, int Y = 0, int Radius = 0, string? Weapon = null, int Count = 0, int OrAfterSeconds = 0)`. `Weapon` is one of `bullet|bomb|mine|repel`; `Reach` uses tiles.
  - `record CastLine(string Role, string Seat, string Script)`. Role is `target|swarm|teammate`, Seat is `in|spec`, Script holds oss-bot script lines.
  - `record TutorialLine(int Index, string Text)`. `Index` is global, in file order, and is used as LVZ id `3000 + Index`.
  - `record TutorialStep(string Id, int Act, IReadOnlyList<TutorialLine> Lines, IReadOnlyList<HudPart> Reveal, IReadOnlyList<string> Grant, bool EngineOff, TutorialCheck Check, int PauseSeconds, IReadOnlyList<CastLine> Cast, IReadOnlyList<CastLine> LeanCast)`. `Grant` holds `SS.Packets.Game.Prize` names, kept as strings in Core.
  - `record TutorialScript(string MentorName, IReadOnlyDictionary<string,string> ContinuumKeys, IReadOnlyList<TutorialStep> Steps, IReadOnlyList<TutorialLine> Prompts)`. `Prompts` holds the skip/leave popup, the "Try the tutorial" prompt and the ending lines.
  - `static TutorialScript TutorialScript.Parse(string json)`, which throws `FormatException` naming the step on bad input.
  - `static string TutorialScript.ForContinuum(string text, IReadOnlyDictionary<string,string> keys)`, which replaces `{action}` with the key name and leaves unknown placeholders as they are.
  - `IEnumerable<TutorialLine> TutorialScript.AllLines()`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Parse_ReadsStepsInOrder() { var s = TutorialScript.Parse(Sample); Assert.Equal(new[]{"hello","turn"}, s.Steps.Select(x => x.Id)); }
[Fact] public void Parse_NumbersLinesGloballyInFileOrder() { var s = TutorialScript.Parse(Sample); Assert.Equal(new[]{0,1,2}, s.AllLines().Select(l => l.Index)); }
[Fact] public void Parse_RejectsUnknownCheck() => Assert.Contains("turn", Assert.Throws<FormatException>(() => TutorialScript.Parse(Sample.Replace("rotate","spin"))).Message);
[Fact] public void ForContinuum_FillsKeys() => Assert.Equal("Ctrl fires.", TutorialScript.ForContinuum("{gun} fires.", new Dictionary<string,string>{["gun"]="Ctrl"}));
[Fact] public void ForContinuum_KeepsUnknownPlaceholder() => Assert.Equal("{zap}", TutorialScript.ForContinuum("{zap}", new Dictionary<string,string>()));
[Fact] public void ShippedScript_Parses_AndEveryLineFitsOneBubble() // loads the embedded resource
{ var s = TutorialScript.Parse(Embedded()); Assert.All(s.AllLines(), l => Assert.True(l.Text.Length <= 90, l.Text)); Assert.Equal(20, s.Steps.Count); }
```

- [ ] **Step 2:** Run `dotnet test --filter TutorialScriptTests`. Expected: FAIL (type missing).
- [ ] **Step 3:** Implement `TutorialScript` with `System.Text.Json`, as `VoicePhrases.Parse` does. Write `script.json` with the spec's 20 steps, in character. The mentor is a veteran pilot with a callsign, dry and a little cocky. Each line is at most 90 characters. Placeholders use the oss-client `Action` names (`{gun}`, `{bomb}`, `{repel}`, `{afterburner}`). `ContinuumKeys` take the defaults from `oss-client/src/game/input.rs`. Step 5 is "shoot it down" (check `Kills` count 1) rather than "hit it". Step 11 is check `Kills` count 2 with `OrAfterSeconds` 20, and step 12's first line is the taunt. Step 7 sets `EngineOff`. Steps 9 and 12 grant `Shield`,`Repel` and `Super` (plus `Gun` if the spike says Super doesn't add damage). Steps 4, 7, 9 and 13 have `PauseSeconds` 4. Positions are placeholders until Task 5 fixes the map and updates them.
- [ ] **Step 4:** Run `dotnet test --filter TutorialScriptTests`. Expected: PASS.
- [ ] **Step 5:** Commit: `Tutorial: script and its model`.

### Task 2: The step machine

**Files:**
- Create: `src/Dare.Arenas.Core/TutorialRun.cs`
- Test: `tests/Dare.Arenas.Tests/TutorialRunTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `abstract record TutorialEvent`, with records: `Rotated(int Rotation)` (0–39), `Moved(int TileX, int TileY)`, `Fired(string Weapon)`, `Killed(string Role)`, `EnteredSafe`, `PickedGreen`, `SaidPublic`, `ChangedShip(int Ship)`, `GotFlag`, `ScoredGoal`, `Died`, `Skip`, `Tick`.
  - `abstract record TutorialEffect`, with records: `Say(TutorialLine Line, int Step, int Of)`, `RevealPart(HudPart Part)`, `Grant(string Prize)`, `EngineOff`, `EngineOn`, `SetCast(IReadOnlyList<CastLine> Cast)`, `ShowSkipPrompt`, `Finished`.
  - `sealed class TutorialRun(TutorialScript script, Func<DateTimeOffset> clock, bool lean, int startStep = 0)`, with:
    - `IReadOnlyList<TutorialEffect> Start()`
    - `IReadOnlyList<TutorialEffect> Handle(TutorialEvent e)`
    - `int StepIndex { get; }`
    - `bool Done { get; }`
    - `int ActOf(int stepIndex)`
    - `static int FirstStepOfAct(TutorialScript s, int act)`
  - `Start` and each advance emit, in order: reveals, grants, engine change, cast (`LeanCast` when `lean`), lines (`Say`), with `Step`/`Of` 1-based. A step whose check is `None` advances on the first `Tick` after `PauseSeconds`. A step that isn't done 60 s after it started emits `ShowSkipPrompt` once, then again every 60 s. `KillAll` counts kills of the step's `swarm` cast lines (1 when lean). `Rotate` is done once the accumulated turning reaches 40 steps in either direction.

- [ ] **Step 1: Write failing tests** (a 4-step script built in the test file, and a fake clock)

```csharp
[Fact] public void Start_SaysFirstLines_WithStepCount() { var fx = Run().Start(); Assert.Contains(fx, e => e is Say { Step: 1, Of: 4 }); }
[Fact] public void RotateStep_NeedsAFullTurn() { /* feed Rotated 0..39 in steps of 5 */ Assert.Equal(2, run.StepIndex); /* after 20 only: still 1 */ }
[Fact] public void WeaponStep_IgnoresOtherWeapons() { run.Handle(new Fired("bomb")); Assert.Equal(1, run.StepIndex); run.Handle(new Fired("bullet")); Assert.Equal(2, run.StepIndex); }
[Fact] public void KillsStep_AdvancesOnTimerWithoutKills() { clock += 21s; run.Handle(new Tick()); Assert.Equal(next, run.StepIndex); }
[Fact] public void KillAll_Lean_NeedsOneKill() { /* lean run */ run.Handle(new Killed("swarm")); Assert.True(advanced); }
[Fact] public void KillAll_Full_NeedsEverySwarmBot() { /* 5 swarm cast lines: 4 kills not enough, 5th advances */ }
[Fact] public void PauseStep_AdvancesAfterPause() { }
[Fact] public void Advance_EmitsRevealGrantEngineCastThenLines() { Assert.Equal(new[]{typeof(RevealPart),typeof(Grant),typeof(EngineOff),typeof(SetCast),typeof(Say)}, fx.Select(f => f.GetType())); }
[Fact] public void Lean_UsesLeanCast() { }
[Fact] public void NoProgress_For60s_ShowsSkipPromptOnce_ThenEvery60s() { }
[Fact] public void Skip_AlwaysAdvances() { /* on every step kind, incl. KillAll with no bots */ }
[Fact] public void Death_DoesNotResetStep() { run.Handle(new Died()); Assert.Equal(before, run.StepIndex); }
[Fact] public void LastStep_EmitsFinished_AndDone() { }
[Fact] public void StartAtAct_BeginsAtThatActsFirstStep() { }
```

- [ ] **Step 2:** Run `dotnet test --filter TutorialRunTests`. Expected: FAIL.
- [ ] **Step 3:** Implement `TutorialRun`. It is pure and has no server types.
- [ ] **Step 4:** Run `dotnet test --filter TutorialRunTests`. Expected: PASS.
- [ ] **Step 5:** Commit: `Tutorial: step machine`.

### Task 3: Who is new: records and decisions

**Files:**
- Create: `src/Dare.Arenas.Core/AccountStore.Tutorial.cs` (partial of `AccountStore`)
- Create: `src/Dare.Arenas.Core/TutorialEntry.cs`
- Modify: `src/Dare.Arenas.Core/AccountStore.cs` (table creation, as the other partials add theirs)
- Test: `tests/Dare.Arenas.Tests/TutorialStoreTests.cs`, `tests/Dare.Arenas.Tests/TutorialEntryTests.cs`

**Interfaces:**
- Produces:
  - Table `tutorial_pilots(account_id INTEGER PRIMARY KEY, first_game_login TEXT NULL, outcome TEXT NULL, step INTEGER NULL, act INTEGER NULL, dropouts INTEGER NOT NULL DEFAULT 0)`. On first creation (a `store_markers`-style marker `tutorial_backfill`), every existing account is inserted with `outcome='existing'`.
  - `AccountStore` gets:
    - `TutorialRecord? TutorialOf(string name)`
    - `void NoteGameLogin(string name)`, which inserts the row with `first_game_login=now` when it's missing; the caller checks `TutorialOf` first.
    - `void SetTutorialProgress(string name, int step, int act)`
    - `void SetTutorialOutcome(string name, string outcome /* finished|left */)`
    - `int AddTutorialDropout(string name)`, which returns the new count.
    - `(int Finished, int Left, IReadOnlyDictionary<int,int> StoppedAtStep) TutorialCounts()`
  - `record TutorialRecord(DateTimeOffset? FirstGameLogin, string? Outcome, int? Step, int? Act, int Dropouts)`
  - `enum EntryKind { Account, Guest, Bot, Fake }`
  - `enum TutorialStart { No, Start, Resume, Offer }`
  - `static (TutorialStart What, int Act) TutorialEntry.Decide(EntryKind kind, TutorialRecord? record, bool guestSaysDone, bool enteringPublic, bool firstEntryThisLogin, bool capacityLeft)`. The rules:
    - bots and fakes get `No`;
    - a guest with done gets `No`;
    - otherwise only the first entry this login counts;
    - an account with no row is new; an account with a row and no outcome and `Dropouts < 2` gets `Resume` from its stored act;
    - outcomes `existing|finished|left` get `No`;
    - a newcomer entering a non-public arena gets `Offer`, and so does one with no capacity left; one entering public with capacity gets `Start`.

- [ ] **Step 1: Write failing tests**

```csharp
// TutorialStoreTests (temp db, as AccountStoreTests do)
[Fact] public void Backfill_MarksExistingAccounts_OnFirstCreate() { /* account made before the table → Outcome "existing" */ }
[Fact] public void NewAccountAfterBackfill_HasNoRow() { }
[Fact] public void Dropouts_Count() { Assert.Equal(1, s.AddTutorialDropout("a")); Assert.Equal(2, s.AddTutorialDropout("a")); }
[Fact] public void WebLogin_DoesNotCountAsGameLogin() { /* CheckLogin(game:false) leaves TutorialOf null */ }
// TutorialEntryTests
[Theory] /* the table: Bot→No; Fake→No; Guest+done→No; Guest public→Start; Guest other arena→Offer; Account no row public→Start;
            Account row existing→No; finished→No; left→No; Account in progress dropouts 1→Resume act; dropouts 2→No;
            not first entry→No; no capacity→Offer */
```

- [ ] **Step 2:** Run `dotnet test --filter "TutorialStoreTests|TutorialEntryTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Note: the second drop-out makes `Decide` return `No` because `Dropouts < 2` fails. The zone also writes `outcome='left'` then.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Commit: `Tutorial: who is new (records, entry rules)`.

### Task 4: Arenas, capacity and casts

**Files:**
- Create: `src/Dare.Arenas.Core/TutorialCast.cs`
- Test: `tests/Dare.Arenas.Tests/TutorialCastTests.cs`

**Interfaces:**
- Consumes: `CastLine` (Task 1), `GuestNames.PickFree` (existing), `ArenaBot` and `BotPlacement`/`BotSeat` (existing models).
- Produces:
  - `const string TutorialCast.ArenaBase = "#training"`, or the fallback from the spike.
  - `static string TutorialCast.Allocate(IReadOnlySet<string> inUse)`, which gives the lowest free `#training1..`.
  - `static bool TutorialCast.IsTutorialArena(string name)`
  - `static bool TutorialCast.Lean(int runningFull, int fullCasts)`, which is `runningFull >= fullCasts`.
  - `static bool TutorialCast.HasCapacity(int running, int max)`
  - `static IReadOnlyList<(ArenaBot Bot, BotPlacement Placement)> TutorialCast.Lineup(string arena, IReadOnlyList<CastLine> cast, IReadOnlyList<string> names)`. Bot labels come from `names` in cast order. The swarm goes on team 1; target and teammate on team 0, except that the target goes on team 1 so it can be shot. `script` comes from the `CastLine`; the seat from the `CastLine`.
  - `static IReadOnlyList<string> TutorialCast.PickNames(int count, Func<int,int> next, Func<string,bool> taken)`, which uses `GuestNames`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Allocate_GivesLowestFree() => Assert.Equal("#training2", TutorialCast.Allocate(new HashSet<string>{"#training1","#training3"}));
[Fact] public void Allocate_GivesDistinctArenas() { /* allocate twice, adding the first to inUse → different */ }
[Theory][InlineData(9,10,false)][InlineData(10,10,true)] public void Lean_BeyondFullCasts(int r,int f,bool lean) => Assert.Equal(lean, TutorialCast.Lean(r,f));
[Fact] public void Lineup_TeamsAndSeats() { /* swarm team 1 seat in; teammate team 0 */ }
[Fact] public void PickNames_SkipsTaken_AndAreValidBotNames() { Assert.All(names, n => Assert.Null(BotLineup.HostProblem(new ArenaBot(/*…*/ n, "ship 1"))));}
[Fact] public void Cast_WhenHostDown_StepsDontWaitOnBots() { /* covered in Task 2: Skip_AlwaysAdvances; here: an empty cast is valid */ }
```

- [ ] **Step 2:** Run `dotnet test --filter TutorialCastTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Commit: `Tutorial: arenas, capacity, casts`.

### Task 5: The training arena folder and the generated LVZ

**Files:**
- Create: `deploy/arenas/#training/arena.conf` (folder name per the spike), `deploy/arenas/#training/training.lvl`, `deploy/arenas/#training/tutorial.lvz` (generated, checked in)
- Create: `tools/tutorial-lvz/build.js`, `tools/tutorial-lvz/font.js` (an embedded 8×12 pixel font, ASCII 32–126, drawn by the tool; no external font file), `tools/tutorial-lvz/portrait.js` (two 64×64 placeholder frames drawn in code)
- Modify: `deploy/apply-local.sh` and `deploy/remote-install.sh`, so they copy `arenas/#training/` the way `_template` is copied (overwrite, since it's module-owned). Check `deploy/test-remote-install.sh` still passes.
- Modify: `src/Dare.Arenas/tutorial/script.json` (real map coordinates)
- Test: `tests/js/tutorial-lvz.test.js`

**Interfaces:**
- Consumes: `src/Dare.Arenas/Web/wwwroot/lvz.js` (read/write model `{version, files, images, objects}`), and `tests/js` mapgen/lvl helpers to build the map.
- Produces, as fixed object ids the zone uses:
  - covers `1000 + HudPart` (Radar 1000, Energy 1001, StatBox 1002, Items 1003, Chat 1004), plus `1005` for the upper chat area in Continuum;
  - mentor frame `2000`, portrait frames `2001`/`2002`, skip popup `2010`, "Try the tutorial" popup `2011`, beacon map object `2100`;
  - line `3000 + TutorialLine.Index`; prompt lines continue the same numbering.
  - Mode is ShowAlways for covers (or per the spike) and ServerControlled for everything else. Screen anchors: mentor objects use the chat CLV2 anchor with negative y.
  - `node tools/tutorial-lvz/build.js` writes `deploy/arenas/#training/tutorial.lvz` from `script.json`, and the two portrait frames to `site/tutorial-mentor.png` (used by Task 8), putting Continuum keys in via the same `{action}` rule as `TutorialScript.ForContinuum`.

- [ ] **Step 1: Write failing tests**

```js
test("every script line has an object 3000+index and an image", ...)
test("covers 1000-1005 are TopMost and on from the start", ...)
test("mentor objects are server-controlled and start off", ...)
test("placeholders are filled with Continuum keys", /* render text of line with {gun} → contains "Ctrl" via the tool's text-layout fn */)
test("output round-trips through lvz.js", ...)
```

- [ ] **Step 2:** Run `node --test tests/js/tutorial-lvz.test.js`. Expected: FAIL.
- [ ] **Step 3:** Implement the tool. Build the map, about 200×200, with the spec's features: start area, beacon spot, open ring, safe zone, green field, flag, ball and goal. Write `arena.conf`: `#include conf/svs/svs.conf`, `Misc:LevelFiles = tutorial.lvz`, and Warbird `InitialEnergy`/`MaximumEnergy` very high (for example 3000/3000). Warbird `BulletDamageLevel` is low but nonzero. The swarm ship (Javelin, ship 2) gets low `MaximumEnergy` so kills land. `Prize:EngineShutdownTime` is the longest value. Prizes are enabled in the green field. One flag, one ball. Spec freq 0 only, with private freqs off. No kill points that persist. Then update the coordinates in `script.json`.
- [ ] **Step 4:** Run `node --test tests/js/tutorial-lvz.test.js` and `dotnet test --filter TutorialScriptTests`. Expected: PASS.
- [ ] **Step 5:** Commit: `Tutorial: training arena, map, generated LVZ`.

### Task 6: oss-bot swarm behaviour (dare-client repo)

**Files** (new worktree `~/oss-src/ingame-tutorial` on branch `claude/ingame-tutorial` from `dare/main`):
- Modify: `crates/oss-bot/src/script.rs` (`Behaviour::Swarm`, name `"swarm"`)
- Modify: `crates/oss-bot/src/brain.rs` (`fn swarm`)
- Modify: `src/Dare.Arenas.Core/BotLineup.cs` in dare-zone (allow `"swarm"` in `HostProblem`), plus `tests/Dare.Arenas.Tests/BotLineupTests.cs`

**Interfaces:**
- Produces: the bot script line `behaviour swarm`, with `range` = ring radius in tiles. The swarm bot:
  - flies to the nearest enemy and holds at `range` tiles, braking as the sentry does;
  - turns to aim at that enemy and fires bullets at most once every 1.5 s;
  - uses no afterburner and doesn't dodge;
  - after being pushed out by a repel, returns to the ring.

- [ ] **Step 1: Write failing tests** in `brain.rs` tests:

```rust
#[test] fn swarm_closes_to_its_ring_and_stops() { /* enemy 600px away, range 12 tiles: forward pressed; at 192px ± 32: no forward/backward drift */ }
#[test] fn swarm_fires_no_more_than_every_1_5_s() { /* 300 ticks lined up: ≤ 2 gun presses */ }
#[test] fn swarm_never_afterburns() { }
```

and in dare-zone `BotLineupTests`: `HostProblem_AcceptsSwarm`.

- [ ] **Step 2:** Run `cargo test -p oss-bot swarm` and `dotnet test --filter BotLineupTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. `Brain` gains a `last_shot_tick: Option<u32>`; the tick comes from `View` as `planned_tick` does.
- [ ] **Step 4:** Run the same commands. Expected: PASS.
- [ ] **Step 5:** Commit in both repos: `oss-bot: swarm behaviour (tutorial)` and `Bot lineup: allow the swarm behaviour`.

### Task 7: The zone's tutorial module

**Files:**
- Create: `src/Dare.Arenas/ArenaOwnership.Tutorial.cs`
- Modify:
  - `src/Dare.Arenas/ArenaOwnership.cs`: Load/Unload register and unregister the tutorial, alongside the existing `PlayerActionCallback`.
  - `src/Dare.Arenas/ArenaOwnership.Bots.cs`: in `ReconcileBots`, add tutorial arenas to `desired` with fixed placements.
  - `src/Dare.Arenas/FoundationAuth.cs`: accept tutorial bot names for bot-pass logins; read the guest "done" flag.
  - `src/Dare.Arenas/Commands.cs`: register commands.
- Test: the logic is covered by Tasks 1–4; this task is verified by the play-through in Task 9.

**Interfaces:**
- Consumes: everything above; SubspaceServer `ILvzObjects.Toggle`, `IGame.GivePrize/ShipReset/SetShipAndFreq`, `IArenaManager.SendToArena`, `IChat.SendArenaMessage`, and the callbacks `PlayerPositionPacketCallback`, `KillCallback`, `SafeZoneCallback`, `GreenCallback`, `ChatMessageCallback`, `ShipFreqChangeCallback`, `FlagGainCallback`, `BallGoalCallback`, `PlayerActionCallback`.
- Produces:
  - Commands:
    - `?tutorial [controls|swarm|objectives]`
    - `?skip`, `?leave` (or the spike's names)
    - `?tutorialclient`: sent by capable Dare clients on arena entry. It marks the player for hint lines and is otherwise silent.
    - `?tutorialstats`: staff only, prints `TutorialCounts`.
  - Hint line (to capable clients only), as an arena message to that player: `~tut~ say <step>/<of> <text with {action} placeholders>`, `~tut~ reveal <part>`, `~tut~ prompt <skip|try>`, `~tut~ end <list|none>`.
  - `internal static bool TutorialBots.Holds(string name)`, a registry `FoundationAuth` checks next to `store.IsBotLogin`.

- [ ] **Step 1: Newcomer placement.**
  - On `PlayerAction.EnterArena`, compute `TutorialEntry.Decide` with:
    - `EntryKind`: guest per `FoundationAuth`'s guest marker; bot via `IsBot`; `ClientType.Fake`;
    - `enteringPublic = arena.IsPublic`;
    - `firstEntryThisLogin` from a per-player flag.
  - `Start`/`Resume`: `Allocate` an arena and `SendToArena`.
  - `Offer`: send the "Try the tutorial" prompt (LVZ 2011 for non-capable players, a `~tut~ prompt try` hint for capable ones).
  - Accounts: call `NoteGameLogin` only after `Decide`, so the "no row" case is seen first.
- [ ] **Step 2: Running a tutorial.**
  - On entering a tutorial arena, make a `TutorialRun`, lean per `Lean(runningFull, FullCasts)`. Wait up to 2 s for `?tutorialclient`, then `Start()`.
  - Map callbacks to events:
    - rotation and tile from the position packet;
    - `Fired`: bullet/bomb from the weapon type, mine = bomb with the alternate flag, repel;
    - kills of a cast bot become `Killed(role)`;
    - other callbacks become their events;
    - a 1 s timer sends `Tick`.
  - Apply effects:
    - `Say`: an arena message to the player with `ForContinuum` text. Non-capable players get toggles for LVZ 2000, 2001, `3000+idx` on and the previous line off; capable players get a `~tut~ say` hint.
    - `RevealPart`: toggle the cover off, plus a `~tut~ reveal` hint.
    - `Grant`: `GivePrize`.
    - `EngineOff`: `GivePrize(Shutdown)`.
    - `EngineOn`: `ShipReset`.
    - `SetCast`: store the run's lineup.
    - `ShowSkipPrompt`: LVZ 2010, or the hint.
    - `Finished`: see Step 4.
  - Put the player in Warbird freq 0 on entry. An early ship change before step 17 is put back to the Warbird. Re-apply `Shutdown` after a respawn while act 2 has the engine off.
- [ ] **Step 3: Bots.**
  - In `ReconcileBots`, for each tutorial arena with a run, add `desired[arena]` with owner `"tutorial"` and the `TutorialCast.Lineup` bots.
  - Make `HostRun` hold `Fixed` placements: when present, `run.Shown = Fixed` and skip `BotDirector.Plan`/`Seats`/`Stagger`.
  - Tutorial bots don't count toward `MaxBots`.
  - Names come from `PickNames` (taken = online, an account, a store bot) and are registered in `TutorialBots` until the arena closes.
  - The whole cast connects at run start with seat `spec`; steps move bots `in`.
- [ ] **Step 4: Ending and cleanup.**
  - On `Finished`, `?leave`, `?go` elsewhere, or a disconnect: toggle every cover and mentor object off, and record progress and outcome (`finished`, `left`, or a disconnect counted with `AddTutorialDropout`, plus `left` at 2).
  - Send the player on: a replay goes back to its origin arena. A newcomer goes to the listed arena with the most real pilots (from `ZoneCensus`), or else to `public`, with a Continuum line about bot arenas (`?go <name>`) or `~tut~ end list` for capable clients.
  - Drop the run so the bots stop; `TutorialBots` forgets the names.
- [ ] **Step 5: FoundationAuth.** Accept `TutorialBots.Holds(name)` as a bot-pass login. In `LetGuestIn`, a password of `guest:done` marks the session "guest says done" (`TutorialEntry` reads it). Any other guest password behaves as before.
- [ ] **Step 6:** Run `dotnet build` and `dotnet test`. Expected: build succeeds and every test passes.
- [ ] **Step 7:** Commit: `Tutorial: zone module (placement, run, bots, ending)`.

### Task 8: Browser extras

**Files (dare-client, same worktree as Task 6):**
- Modify:
  - `crates/oss-client/src/game/sandbox.rs`: chat lines starting `~tut~ ` aren't shown in chat but passed to the page. Send `?tutorialclient` on every arena entry (wasm build only).
  - `crates/oss-client/src/game/hud.rs`: on `reveal <part>`, glow an outline around that part for 3 s.
  - `crates/oss-client/src/web.rs`:
    - JS import `foundationTutorial(line: &str)`;
    - export `tutorial_command(cmd: &str)`, which pushes onto a queue the sandbox drains, sending each as a chat line; only `?skip`, `?leave`, `?tutorial` and `?go <arena>` are accepted;
    - export `key_for(action: &str) -> String`, the player's binding through `key_name`.
  - `web/client/start.mjs`: the guest login sends password `guest:done` when `localStorage["dare.tutorial.done"]` is set (wrapped in try/catch).
- Create `crates/oss-client/src/game/tutorial.rs`: `fn parse_hint(text: &str) -> Option<Hint>` with `enum Hint { Say{step,of,text}, Reveal(HudPart), Prompt(Prompt), End{list: bool} }`, and its tests.
- **Files (dare-zone):**
  - Create `site/tutorial.js` and `site/tutorial.css`:
    - the mentor box bottom-left above the chat, with the portrait (the same placeholder art as the LVZ, exported PNG), name and a typewriter line;
    - `{action}` replaced through `key_for`;
    - **Skip** and **Leave tutorial** buttons that call `tutorial_command`;
    - the "Try the tutorial" prompt with a button sending `?tutorial`;
    - `end list` opens a small arena chooser built from the list `arena-list.js` already fetches, whose clicks send `?go <name>`;
    - on `end`, set `localStorage["dare.tutorial.done"]`.
  - Modify `site/index.html` to load `tutorial.js` beside `graphics.js`.
- Test:
  - `cargo test -p oss-client tutorial`: `parse_hint` cases, and that a `~tut~` line never reaches the chat log.
  - `tests/js/tutorial.test.js`: placeholder filling with a fake `key_for`, and button-to-command mapping. Keep the DOM-free parts in exported functions.

- [ ] **Step 1:** Write the failing tests above: `parse_hint` (say, reveal, prompt, end, junk → None), `hint_lines_are_not_logged`, and JS `fillKeys`/`commandFor`.
- [ ] **Step 2:** Run `cargo test -p oss-client tutorial` and `node --test tests/js/tutorial.test.js`. Expected: FAIL.
- [ ] **Step 3:** Implement. Where the client shows the arena name, show `Training` for any `#training*` arena.
- [ ] **Step 4:** Run both commands, plus `cargo test` (the whole workspace). Expected: PASS.
- [ ] **Step 5:** Commit in both repos: `Tutorial: browser mentor box, hints, buttons`.

### Task 9: Play-through, measurement, ship

- [ ] **Step 1:** Run the local zone (as in Task 0) with the built plugin, the `#training` folder and the new oss-bot. Run as a new account and as a guest, once in Continuum and once in the browser.
  - Play all 20 steps.
  - Check each cover hides its part at Continuum 1024×768 and 1920×1080, and at browser desktop and phone sizes.
  - Check the mentor box covers nothing that should be visible.
  - Try Skip and Leave.
  - Drop out twice as a new account and confirm it isn't started a third time.
  - Use `?go public` mid-tutorial.
  - Start 11 tutorials with bots (`?tutorial` from fake accounts or a script) and confirm the 11th is lean.
- [ ] **Step 2:** Measure one full-cast bot host (RSS and CPU over the swarm step, using `ps`). Write the numbers into the spec's "How many at once" bullet, and report them to Chris.
- [ ] **Step 3:** Run `dotnet test`, `node --test tests/js`, and `cargo test` in dare-client. Expected: all pass.
- [ ] **Step 4:** Merge `origin/main` into the dare-zone branch and `dare/main` into the dare-client branch, then re-run Step 3. Merge both into `main` and push (`origin`, `dare`).
- [ ] **Step 5:** Tell Chris it's merged, with the measurements. Ask before any deploy; the deploy needs `deploy.sh`, `publish-site-from-mac.sh` and `deploy-client.sh` with `--by 'ingame-tutorial'`.
