# Arena Bots Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One bot host per arena running every bot as its own connection, a match director in the zone (teams, match sizes, making room), plain bot names known to the zone by register, and bot connections that look like a real client's.

**Architecture:** The zone plugin (C#, dare-zone) owns the bot register, the team/priority/match-size settings, and a pure `BotDirector` that decides which bots are in, spectating or gone; its supervisor starts one `oss-bot --host` process per arena and streams it the lineup over stdin. The host (Rust, oss-client `crates/oss-bot`) applies lineup changes without touching unchanged bots, targets anyone on another freq (bots included), breaks off before spectating, and sends position reports the way the game client does.

**Tech Stack:** C# / .NET 9, SQLite (Microsoft.Data.Sqlite), xUnit (dare-zone); Rust 2024, `oss-session`, `oss-sim` (oss-client + oss-core); Docker `rust:1-bookworm` for the Linux build.

**Spec:** `docs/superpowers/specs/2026-11-26-arena-bots-foundation-design.md` (dare-zone). Read it first.

## Global Constraints

- Branches: dare-zone `arena-bots-foundation` from `custom-graphics` (worktree `.claude/worktrees/arena-bots-foundation`); oss-client `arena-bots-foundation` from `bots-menu-play` (worktree `~/oss-src/arena-bots-foundation`, so `../oss-core` resolves; oss-core at `eae56a0`). Other sessions commit to `custom-graphics` and `loading-flow`: never commit there from this plan.
- Starting points already on those bases: dare-zone `cd67dc8` (bots keyed by settings, `chat on|off`), oss-client `325f8da` (roaming).
- Bot names: plain, no suffix; 1–20 characters, letters, digits, single spaces, `_ - .`, starting with a letter or digit.
- At most 16 bots per arena; zone-wide cap `[DareArenas] MaxBots` (default 30) unchanged.
- Team (freq) 0–9999; priority 1–9 (9 = stays in longest); match size 0–16 per team.
- Making room: break off up to **8 s**, spectate, leave the arena **20–60 s** later (random); come back after **3–15 s** (random). Only real players **flying** on a team count toward its match size. Team switches only between teams that have a match size. Real players are never moved.
- Bots never count as players: zone ping, zone list, home page counts and home page/chat player lists all leave them out.
- A bot name may not equal a pilot account, a pilot name, or a guest name in use, and vice versa.
- Zone server is x86-64 Linux (glibc 2.43), 2 GB RAM; ship the host as `oss-bot` via `OSS_BOT_LINUX` in `deploy/deploy.sh`.
- dare-zone tests: `dotnet test tests/Dare.Arenas.Tests` (PATH needs `/usr/local/share/dotnet`). oss-client tests: `cargo test -p oss-bot` with `CARGO_TARGET_DIR=$HOME/oss-src/oss-client/target`; `cargo clippy -p oss-bot` and `cargo fmt --check` clean.

## Review Focus

1. **Flapping players**: a player hopping between ship and spectator, or between teams, every few seconds must not make bots churn; a bot changes seat at most once per 15 s (Task 2 test `a_bot_changes_seat_at_most_once_per_15_seconds`).
2. **Host restart mid-match**: the host crashes or the zone restarts; bots must come back once each, never twice under one name, and keep their teams (Task 4 test `a_host_that_exits_is_restarted_and_bots_log_in_once`).
3. **Old name collisions**: a bot saved before the two-way rule whose name now matches a pilot must not launch, and the owner's `?bots list` says why (Task 1 test `a_bot_named_like_a_pilot_is_not_launched`).
4. **Editing during a fight**: changing one bot's ship mid-match restarts only that bot (Task 6 test `a_lineup_change_touches_only_the_changed_bot`).
5. **Teams nobody configured**: players on a freq with no match size, a match size of 0, or more bots than the size must leave the right bots out and never move players (Task 2 tests `teams_without_a_match_size_are_left_alone`, `match_size_zero_takes_every_bot_out`).

---

### Task 1: Bot register, settings and name rules (dare-zone Core)

**Files:**
- Modify: `src/Dare.Arenas.Core/ArenaStore.Bots.cs`, `src/Dare.Arenas.Core/ArenaStore.cs` (schema), `src/Dare.Arenas.Core/BotCommand.cs`
- Modify: `src/Dare.Arenas.Core/AccountStore.cs` (`StartSignup`, `CreateWithoutEmail`), `src/Dare.Arenas.Core/AccountStore.Pilots.cs` (`AddPilot`), `src/Dare.Arenas.Core/GuestNames.cs`
- Test: `tests/Dare.Arenas.Tests/ArenaStoreTests.cs`, `tests/Dare.Arenas.Tests/BotCommandTests.cs`, `tests/Dare.Arenas.Tests/NoEmailAccountTests.cs`, `tests/Dare.Arenas.Tests/PilotNamesTests.cs`

**Interfaces:**
- Produces: `record ArenaBot(string Label, string Script, bool Deployed)` gains computed `int Team` (script `team` line, default 0) and `int Priority` (script `priority` line, default 5) via `BotCommand.Team(string script)` / `BotCommand.Priority(string script)`.
- Produces: `ArenaStore.IsBotName(string name) -> bool`; `ArenaStore.BotNames() -> IReadOnlySet<string>` (case-insensitive); `ArenaStore.MatchSizes(string arena) -> IReadOnlyDictionary<int,int>`; `ArenaStore.SetMatchSize(string arena, string owner, int freq, int? size) -> Result` (`null` removes).
- Produces: `BotCommand.Args` gains `int? Team, int? Priority`; `BotCommand.Apply(script, behaviour, ship, chat, team, priority)`; `Describe` adds `team N`.
- Removes: `ArenaStore.BotSuffix`. `BotCommand.Label` still strips a typed trailing `[bot]`.

- [ ] **Step 1: Write the failing tests**
  - `ArenaStoreTests.bot_names_are_plain_and_up_to_20_characters`: `SaveBot(...,"A Twenty Char Name12", ...)` Ok; 21 characters fails; `"_x"` fails.
  - `ArenaStoreTests.an_arena_holds_16_bots`: 16 saves Ok, the 17th returns "This arena already has 16 bots."
  - `ArenaStoreTests.team_and_priority_lines_are_valid`: script `"ship 1\nbehaviour fighter\nteam 9999\npriority 9"` Ok; `team 10000`, `priority 0`, `priority 10` refused.
  - `ArenaStoreTests.match_sizes_are_per_arena_and_owner_only`: `SetMatchSize("bigarena","Big Chris",0,4)` Ok, `MatchSizes` = `{0:4}`; other owner refused; size 17 refused; `null` removes.
  - `ArenaStoreTests.is_bot_name_reads_the_register`: after saving `Sparring`, `IsBotName("sparring")` true, `IsBotName("Other")` false.
  - `NoEmailAccountTests.a_bot_name_cannot_be_claimed`: with bot `Sparring` saved in the same db file, `CreateWithoutEmail("Sparring", ...)` fails with "That name is taken."; `StartSignup` and `AddPilot` likewise.
  - `ArenaStoreTests.a_bot_named_like_a_pilot_is_not_launched`: an account `Patrol` exists, a bot `Patrol` row inserted directly (old data) → `Store.LaunchableBots("bigarena", isAccount)` excludes it and `Store.BotProblem("bigarena","Patrol", isAccount)` returns "Not launched: a pilot uses this name."
  - `ArenaStoreTests.existing_bots_get_a_team_0_match_size_once`: an arena with 3 bots and no `bot_matches` rows, reopened → `MatchSizes` = `{0:3}`; reopening again after the owner removed it leaves it removed (a `bot_matches_migrated` marker row in an existing settings/markers table, or a `PRAGMA user_version` bump, records that it ran).
  - `BotCommandTests.team_and_priority_read_and_apply`: `Parse("Sparring team 2 priority 7")` → Team 2, Priority 7; `Apply(null,null,null,null,2,7)` contains `team 2` and `priority 7`; `Describe` of it is `"fighter, Warbird, team 2"`.
- [ ] **Step 2: Run** `dotnet test tests/Dare.Arenas.Tests --filter "ArenaStoreTests|BotCommandTests|NoEmailAccountTests|PilotNamesTests"` — expect the new tests to FAIL.
- [ ] **Step 3: Implement.** Schema: `CREATE TABLE IF NOT EXISTS bot_matches (arena TEXT NOT NULL COLLATE NOCASE, freq INTEGER NOT NULL, size INTEGER NOT NULL, PRIMARY KEY (arena, freq))`. `ValidBotScript` accepts `team 0-9999` (once) and `priority 1-9` (once). Label rule length 1–20; cap 16. `LaunchableBots(arena, Func<string,bool> isAccount) -> IReadOnlyList<ArenaBot>` (deployed, not an account). Account side: one helper `bool BotNameTaken(string name)` in `AccountStore` running `SELECT 1 FROM bots WHERE label=$n` guarded by `sqlite_master` (table may not exist yet), used by the three creators and by `GuestNames` when picking a guest name. Delete `BotSuffix`. `ArenaOwnership.IsBot(string)` becomes an instance method returning `Store.IsBotName(name)` (FoundationAuth reaches it through its existing `ArenaOwnership` access); update its callers (`FoundationAuth.cs:200`, `ArenaOwnership.cs:167`, `Restarts.cs:98`, the supervisor) and the launch name (no suffix). One-time migration for existing arenas: `bot_matches(arena, 0, number of bots)`.
- [ ] **Step 4: Run the same filter** — all PASS; then the full suite — PASS.
- [ ] **Step 5: Commit** `arena bots: plain names in a register, team/priority/match size, names exclusive both ways`.

### Task 2: The match director (dare-zone Core, pure)

**Files:**
- Create: `src/Dare.Arenas.Core/BotDirector.cs`
- Test: `tests/Dare.Arenas.Tests/BotDirectorTests.cs`

**Interfaces:**
- Consumes: `ArenaBot` with `Team`, `Priority` (Task 1).
- Produces:
  ```csharp
  public enum BotSeat { Out, Spectating, In }
  public sealed record BotPlacement(string Label, BotSeat Seat, int Team);
  public static class BotDirector
  {
      // Which seat and team each bot should have now. playersFlying: real players in a ship, by freq.
      public static IReadOnlyList<BotPlacement> Plan(IReadOnlyList<ArenaBot> bots,
          IReadOnlyDictionary<int,int> matchSizes, IReadOnlyDictionary<int,int> playersFlying);
  }
  public sealed class BotSeats  // timing: turns Plan's targets into paced changes
  {
      public BotSeats(Func<DateTimeOffset> clock, Random random);
      // Feed the latest plan; returns what each bot should be told now (its current seat and team).
      public IReadOnlyList<BotPlacement> Step(IReadOnlyList<BotPlacement> target);
  }
  ```
- Rules in `Plan`: teams without a match size: every deployed bot on it `In` on its own team. Team with size S: `In` = top `max(0, S − playersFlying[team])` bots by priority (desc), then label; the rest are spare. Then for each sized team still short, move spare bots from other sized teams (lowest priority first) to it (`Team` = the short team). Remaining spares `Out`.
- Rules in `BotSeats`: `In → Out` goes `In → Spectating` now, `Spectating → Out` after a random 20–60 s; `Out/Spectating → In` after a random 3–15 s; a team change is applied as `Spectating` (new team) then `In` after 3–15 s; a bot changes seat at most once per 15 s (later targets wait). The 8 s break-off happens in the host (Task 7), not here.

- [ ] **Step 1: Write the failing tests** (`BotDirectorTests`):
  - `bots_fill_the_places_players_have_not_taken`: 4 bots team 0 size 4, 1 player flying on 0 → 3 `In`, lowest priority `Out`.
  - `the_lowest_priority_makes_room_first`: priorities 9,5,5,1 with one player → the priority-1 bot is out; a tie goes by label.
  - `a_short_side_gets_a_spare_bot`: team 0 size 2 with 1 bot, team 1 size 2 with 3 bots, no players → team 1 keeps its 2 highest-priority bots, its third is placed `In` on team 0 (`Team` = 0).
  - `with_no_spare_the_short_side_stays_short`: sizes 2/2, 2 bots each, 2 players fly on team 1 → both team-1 bots `Out`, team 0 unchanged.
  - `teams_without_a_match_size_are_left_alone`: bots on team 5 (no size) all `In` on 5 whatever players do.
  - `match_size_zero_takes_every_bot_out`.
  - `more_players_than_the_size_takes_every_bot_out_and_moves_no_player`.
  - `BotSeats` with a fake clock: `leaving_spectates_first_then_goes_20_to_60_seconds_later`; `coming_back_waits_3_to_15_seconds`; `a_bot_changes_seat_at_most_once_per_15_seconds` (targets flipping every 2 s for a minute → at most 4 seat changes).
- [ ] **Step 2: Run** `dotnet test tests/Dare.Arenas.Tests --filter BotDirectorTests` — FAIL (type missing).
- [ ] **Step 3: Implement** `BotDirector.Plan` and `BotSeats` in `BotDirector.cs`.
- [ ] **Step 4: Run** the filter — PASS.
- [ ] **Step 5: Commit** `arena bots: match director (who is in, who makes room, paced seat changes)`.

### Task 3: The host protocol (oss-client `crates/oss-bot`)

**Files:**
- Create: `crates/oss-bot/src/lineup.rs`
- Modify: `crates/oss-bot/src/main.rs` (module declaration only)

**Interfaces:**
- Produces (Rust):
  ```rust
  pub enum Seat { In, Spectating }
  pub struct Entry { pub name: String, pub password: String, pub seat: Seat, pub team: i16, pub script: Script }
  pub struct Lineup { pub arena: String, pub controller: Option<String>, pub bots: Vec<Entry> }
  pub enum Change { Add(Entry), Remove(String), Restart(Entry), Seat(String, Seat), Team(String, i16) }
  pub fn read(text: &str) -> Result<Lineup, String>;          // one lineup message
  pub fn diff(old: &Lineup, new: &Lineup) -> Vec<Change>;
  ```
- Wire format (zone → host, stdin, text): a message is
  ```
  lineup <arena>
  controller <owner>
  bot <name>
  password <one-time pass>
  seat in|spec
  team <freq>
  <script lines as today: ship, behaviour, point, range, greet, reply, banter, announce>
  end
  ...more bot blocks...
  done
  ```
  The host acts on each `done`. A bot absent from the latest lineup is removed (disconnects). `Restart` when anything but `password`, `seat` and `team` differs. `password` changes alone are ignored (a fresh pass only matters when a bot (re)connects).

- [ ] **Step 1: Failing tests in `lineup.rs`**: `reads_a_lineup_with_two_bots`; `diff_adds_removes_and_restarts_only_what_changed` (change bot A's ship, bot B's seat, drop C, add D → `[Restart(A), Seat(B,Spectating), Remove(C), Add(D)]` in any order); `a_new_password_alone_changes_nothing`; `team_change_is_its_own_change`; `bad_script_line_names_the_bot` (error contains the bot name).
- [ ] **Step 2: Run** `cargo test -p oss-bot lineup` — FAIL.
- [ ] **Step 3: Implement** `read` (reuse `script::parse` for each block's script lines) and `diff`.
- [ ] **Step 4: Run** — PASS; clippy and fmt clean.
- [ ] **Step 5: Commit** `bot: lineup messages for a bot host, and what changed between two`.

### Task 4: Host mode and team targeting (oss-client)

**Files:**
- Modify: `crates/oss-bot/src/main.rs` (new `--host` option, bot creation pulled into `fn start_bot(entry: &Entry, opts: &Options, cache: &MapCache, now) -> Bot`, host loop)
- Modify: `crates/oss-bot/src/main.rs` `view()` (targets)

**Interfaces:**
- Consumes: `lineup::{read, diff, Change, Entry, Seat}` (Task 3).
- Produces: CLI `oss-bot --host --server <host:port> --map-cache <dir>`: reads lineup messages from stdin until it closes, then disconnects every bot and exits 0. Each bot runs on its own thread (session, driver, world, brain), receiving its `Change`s over a channel; the stdin reader thread owns the current `Lineup`.
- Targeting: `view()` enemies = alive ships on another freq, out of safe zones; drop the `peers` exclusion (`peer_suffix`) entirely; never a ship on the bot's own freq.
- Seat: `Seat::Spectating` → the bot goes to spectator (Task 7 adds the break-off); `Seat::In` → its script ship. Team: the bot does not set its freq; the zone does (Task 5).

- [ ] **Step 1: Failing tests**: `view_targets_bots_on_other_freqs_and_never_teammates` (a world with a bot on freq 1, another ship named like a bot on freq 2, a pilot on freq 1 → enemies = only the freq-2 ship); `host_args_parse` (`--host --server a:1` → host mode, no `--script` needed).
- [ ] **Step 2: Run** `cargo test -p oss-bot` — FAIL.
- [ ] **Step 3: Implement** host mode and targeting. Keep `--script` single-bot mode working (it becomes a one-entry lineup internally).
- [ ] **Step 4: Run** tests — PASS. Manual: against the local zone (see Task 9 setup) pipe a two-bot lineup into `oss-bot --host` and watch both enter; send a second lineup changing one bot's ship → only that bot reconnects.
- [ ] **Step 5: Commit** `bot: --host runs an arena's lineup, one thread per bot; bots fight anyone on another freq`.

### Task 5: Zone supervisor drives hosts (dare-zone)

**Files:**
- Modify: `src/Dare.Arenas/ArenaOwnership.Bots.cs` (supervisor), `src/Dare.Arenas/FoundationAuth.cs:200`, `src/Dare.Arenas/ArenaOwnership.cs:167`, `src/Dare.Arenas/Restarts.cs:98`
- Modify: `deploy/groupdef-sections.conf` (new `[arenabot]`), `deploy/groupdef.dir/` (new `arenabot`), `deploy/apply-local.sh` and `deploy/remote-install.sh` (copy it; `players_online` stops subtracting `pgrep -c oss-bot`), `deploy/test-remote-install.sh`
- Test: `tests/Dare.Arenas.Tests/GroupDefTests.cs`

**Interfaces:**
- Consumes: `Store.LaunchableBots`, `Store.MatchSizes`, `Store.BotNames` (Task 1); `BotDirector.Plan`, `BotSeats.Step` (Task 2); host CLI and lineup format (Tasks 3–4).
- Produces: per arena with launchable bots and an occupant (real player, any client type, in the arena within the last minute): one `HostRun { Process, BotSeats seats, string lastLineup, backoff }`. Each tick: count real players flying per freq, `Plan` → `seats.Step` → render the lineup (issue a one-time pass via `BotPasses.Issue(name)` for each bot not currently in the zone) → write it only when it differs from `lastLineup`. For each bot in the zone on the wrong freq, `_game.SetFreq(player, team)`. Host exit → restart with today's backoff (5 s doubling to 300 s) and resend the lineup; a bot whose name is still in the zone is not given a pass until it has left.
- Occupancy uses `Store.BotNames()` read once per tick (not a query per player). On a bot's arena entry: `_groupManager.SetTempGroup(player, "arenabot")`.
- `[arenabot]` includes `groupdef.dir/default` plus `excludepopulation`.

- [ ] **Step 1: Failing tests**: `GroupDefTests.bots_are_left_out_of_population` (`groupdef-sections.conf` has `[arenabot]`, and `groupdef.dir/arenabot` lists `excludepopulation`); a `BotLineupTests.renders_the_wire_format` test for the lineup renderer, which lives in Core as `BotLineup.Render(string arena, string controller, IReadOnlyList<(ArenaBot Bot, BotPlacement Placement, string Password)>) -> string` (Create `src/Dare.Arenas.Core/BotLineup.cs`; casual chat lines converted as `BotLaunchScript` does today); the Review Focus check `a_host_that_exits_is_restarted_and_bots_log_in_once` is manual, in Step 4 (the zone log shows one login per bot after a `kill` of the host).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Remove the per-bot `BotRun`/`_bots` dictionary in favour of `HostRun` per arena; keep casual-chat reply plumbing (`AddressBot`, banter) working against bots found by register.
- [ ] **Step 4: Run** the full suite — PASS. Manual on the local zone: 4 bots team 0 size 4 and 4 bots team 1 size 4 in one arena → 8 bots, one `oss-bot` process; `?lag` on one bot shows a normal ping; the zone list count (`deploy/remote-install.sh` `players_online` logic, or the web `/arena/api/public` `publicPlayers`) shows only the observer; `kill` the host → bots return once each.
- [ ] **Step 5: Commit** `arena bots: one host per arena, director-driven lineups, bots out of every player count`.

### Task 6: `?bots` for everyone, team/priority/match commands, web Team field (dare-zone)

**Files:**
- Modify: `src/Dare.Arenas/ArenaOwnership.Bots.cs` (`Command_bots`, `ListBots`, `BotHelp`, help attribute), `deploy/default-caps.txt` (`cmd_bots`), `src/Dare.Arenas/Web/WebChat.cs` (`ChatPlayers` skips `Store.IsBotName`), `src/Dare.Arenas/Web/wwwroot/arena.html` + `app.js` (Team number field 0–9999 written as `team N`; label `maxlength` 20), `docs/arena-owners.md`
- Test: `tests/Dare.Arenas.Tests/BotCommandTests.cs`, `tests/Dare.Arenas.Tests/GroupDefTests.cs`

**Interfaces:**
- Consumes: Task 1 settings API; Task 5 register.
- Commands: anyone: `?bots` → "Bots here: A, B" or "No bots here." Owner (and staff who own the arena) additionally: everything from today plus `?bots set <name> team <freq>`, `?bots set <name> priority <1-9>`, `?bots match <freq> <size>` / `?bots match <freq> off`; `?bots list` lines become `"<name>  <describe>  team T, priority P  [in|spectating|out|not launched: <why>]"`, and one line per team with a size: `"team T: size S, P players flying"`.

- [ ] **Step 1: Failing tests**: `GroupDefTests.every_player_can_ask_which_pilots_are_bots` (`default-caps.txt` contains `cmd_bots`); `BotCommandTests.match_command_reads` for a new `BotCommand.ParseMatch(string) -> (int Freq, int? Size)?` (`"1 4"` → (1,4), `"1 off"` → (1,null), `"x 4"` → null).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** the commands, web field, chat-player filter and docs.
- [ ] **Step 4: Run** the suite — PASS. Manual: a non-owner `?bots` sees only names; the home page chat panel's player list omits bots; `a_lineup_change_touches_only_the_changed_bot`: `?bots set A ship 3` mid-match → only A leaves and returns (observer log).
- [ ] **Step 5: Commit** `arena bots: ?bots tells anyone who the bots are; owners set teams, priority and match sizes`.

### Task 7: Breaking off before spectating (oss-client)

**Files:**
- Modify: `crates/oss-bot/src/main.rs` (seat handling), `crates/oss-bot/src/brain.rs` (new `Brain::disengage`)

**Interfaces:**
- Produces: on `Seat::Spectating` the bot stops choosing targets and flies away from the nearest enemy (`Brain::disengage(&mut self, v: &View, nav: &mut Nav) -> ShipInput`) until no enemy is within 40 tiles or 8 s have passed, then requests spectator.

- [ ] **Step 1: Failing tests** in `brain.rs`: `disengaging_flies_away_from_the_nearest_enemy` (enemy to the east → input turns/thrusts west, gun not pressed); a seat-timer test on a small `struct Leaving { since_ms: u64 }` with `fn ready(&self, now_ms: u64, enemy_within_40_tiles: bool) -> bool` (ready at 8000 ms, or at once with no enemy near).
- [ ] **Step 2–4:** run (FAIL), implement, run (PASS), clippy/fmt clean.
- [ ] **Step 5: Commit** `bot: break off a fight before spectating`.

### Task 8: Connection parity with the game client (oss-client)

**Files:**
- Modify: `crates/oss-bot/src/main.rs` (`Bot::fly`), `crates/oss-bot/src/brain.rs` / `nav.rs` (search budget)
- Reference: `crates/oss-client/src/game/sandbox.rs:2322-2425` (`report_motion_change`, `send_position_at`), `crates/oss-session/src/client.rs:140-160` (security answer)

**Interfaces:**
- Produces: every position report via `session.send_position_at(now, state_ms, pos)` with `state_ms` = the moment of the sim tick it describes; an extra report the tick thrust/afterburner/turn starts or stops, no closer than 20 ms to the previous one (same rule as `sandbox.rs`). Route searches capped at 20 000 tiles per call (`SEARCH`), so no single brain step blocks a bot's thread for long. Bots answer security checks with the same `SecurityAnswer` the game client profile gives.

- [ ] **Step 1: Measure first** (record numbers in the commit message): local zone, 8 bots in one host, sysop `?lag <bot>` for three bots after 2 minutes, and `grep -c "Exe checksum mismatch"` in the zone log over 3 minutes. Then check the live zone log for whether human `dare` sessions also log "Exe checksum mismatch" (read-only) — if humans do too, make bots answer as humans and note that the zone's VIE exe checksum setting is a separate zone issue.
- [ ] **Step 2: Failing tests**: `motion_change_sends_one_extra_report` (thrust on at tick t → two reports within 20 ms window → only one extra); `reports_are_dated_to_their_tick` (state_ms equals tick time, not wall time).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** tests — PASS; repeat the Step 1 measurement: every bot's average ping within 30 ms of an idle local client's, no "Exe checksum mismatch" lines for bots.
- [ ] **Step 5: Commit** `bot: position reports like the game client's, bounded route searches, client security answer`.

### Task 9: End-to-end check, Linux build, deploy notes

**Files:**
- Modify: `docs/arena-owners.md` (bots section: plain names, teams, match size, making room, `?bots` for everyone)

- [ ] **Step 1: Local end to end** (scratch zone as in this session: vendored `SubspaceServer.dll` with `DOTNET_ROLL_FORWARD=Major`, `deploy/apply-local.sh <zone> out/modules/Dare.Arenas`, `[DareArenas] BotProgram` → the debug `oss-bot`, a seeded owner account): 4v4 with sizes 4/4; one observer joins team 1 and flies → one team-1 bot breaks off, spectates, leaves 20–60 s later; observer spectates → a bot returns within 3–15 s; `?bots` from a non-owner lists names; web `/arena/api/public` `publicPlayers` counts only humans.
- [ ] **Step 2: Linux build:** `docker run --rm --platform linux/amd64 -v ~/oss-src/arena-bots-foundation:/src/client:ro -v ~/oss-src/oss-core:/src/oss-core:ro -v <out>:/target -e CARGO_TARGET_DIR=/target -w /src/client rust:1-bookworm cargo build --locked --release -p oss-bot` → `<out>/release/oss-bot`.
- [ ] **Step 3: Commit** docs; push both `arena-bots-foundation` branches. Deploy is a separate decision for Chris (zone restart; `OSS_BOT_LINUX=<out>/release/oss-bot deploy/deploy.sh`, run from a checkout whose `site/` matches the live site).
