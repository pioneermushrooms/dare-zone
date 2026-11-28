# Bots Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Esc → B opens an in-game bots window (list with live status, step-by-step Create, edit in place, D to deploy/stop, Stop all) fed by machine-readable `?bots data` replies from the zone.

**Architecture:** The zone (C#, dare-zone) answers `?bots data` with marker-prefixed lines rendered by a pure Core formatter, and gains `?bots stopall`. The game (Rust, oss-client `crates/oss-client`) hides marker lines from chat, parses them into a pure `BotsView` model, and draws a `bots_ui` window built like the settings window; the window turns keys and clicks into existing `?bots …` commands.

**Tech Stack:** C# / .NET 9 + xUnit (dare-zone); Rust 2024 (oss-client, needs `../oss-core`).

**Spec:** `docs/superpowers/specs/2026-11-28-bots-window-design.md` (dare-zone). Read it first.

## Global Constraints

- Branches: dare-zone `bots-window` (worktree `/Users/chris/dare-zone/.claude/worktrees/bots-window`, from origin/main 89489d7); oss-client `bots-window` from `dare/main` (worktree `/Users/chris/oss-src/bots-window`, so `../oss-core` resolves). Never commit to `main` or another session's branch.
- Data line marker: `\u0001bots\t` (the control character U+0001, then `bots`, then a tab). Fields after it are tab-separated. Line kinds, in order: `head\t<arena>\t<owner yes|no>`; one `bot\t<name>\t<behaviour>\t<ship 1-8>\t<team>\t<priority>\t<chat on|off>\t<deployed yes|no>\t<status>\t<problem or empty>` per bot (status: `in`, `spectating`, `out`, `stopped`, `not launched`); one `match\t<freq>\t<size>` per team size; `end`.
- Only the asker receives `?bots data` lines. Non-owners get `head … no` plus bot lines (no problem text) and can do nothing but look.
- Refresh: the window sends `?bots data` when it opens and every 2 s while it is open; never when closed.
- Keys (on the yellow foot line, each also clickable): `D` deploy/stop the selected bot, `Enter` open/edit, `C` create, `Del` remove (confirm), `M` match sizes, `Esc` back/close; **Stop all** on the foot line, confirm first.
- Create sequence, one box at a time, Esc goes back one step: name → behaviour (`fighter`, `patrol`, `sentry`, `idle`) → ship (1–8 by name: Warbird, Javelin, Spider, Leviathan, Terrier, Weasel, Lancaster, Shark) → team (number, default 0) → chat (on/off, default off) → summary with **Deploy** and **Save stopped**.
- Editable in the window: behaviour, ship, team (0–9999), priority (1–9), chat on/off, match size per team (0–16, or off). Not editable: waypoints, greetings, replies (website).
- Every line fits the 40-column box (the settings window's sizing rules).
- Tests: dare-zone `PATH=/usr/local/share/dotnet:$PATH dotnet test tests/Dare.Arenas.Tests` and `dotnet build src/Dare.Arenas`; oss-client `export PATH=$HOME/.cargo/bin:$PATH CARGO_TARGET_DIR=$HOME/oss-src/oss-client/target`, `cargo test -p oss-client`, `cargo clippy -p oss-client`, `cargo fmt -p oss-client --check`; a wasm build check `cargo build -p oss-client --target wasm32-unknown-unknown --lib`.

## Review Focus

1. **A name or problem text with odd characters** (spaces, `|`, a reason containing a name): the data line still parses and shows correctly (Task 1 test `bot_lines_survive_names_with_spaces`, Task 2 test `parses_a_name_with_spaces_and_an_empty_problem`).
2. **A non-owner opens the window**: no Create/edit/D/Del/M/Stop all reachable by key or click (Task 3 test `a_non_owner_window_sends_nothing`).
3. **The window closes mid-refresh or the zone's answer is cut off** (no `end` line): the view keeps the last complete list and doesn't show a half list (Task 2 test `an_unfinished_answer_keeps_the_last_list`).
4. **Chat stays clean**: while the window is open, `Bots: …` replies to the window's own commands are shown on the row, not in chat; with the window closed, `?bots` typed by hand still answers in chat (Task 3 test `bots_replies_go_to_the_window_only_while_open`).
5. **Old game clients** (Continuum) asking nothing see nothing new: data lines go only to whoever typed `?bots data` (Task 1 test `data_goes_only_to_the_asker`).

---

### Task 1: Zone: `?bots data` and `?bots stopall` (dare-zone)

**Files:** Create `src/Dare.Arenas.Core/BotData.cs`; Modify `src/Dare.Arenas/ArenaOwnership.Bots.cs` (`Command_bots`, help text), `docs/arena-owners.md`; Test `tests/Dare.Arenas.Tests/BotDataTests.cs`.

**Interfaces:**
- Produces: `public static class BotData { public const string Marker = "\u0001bots\t"; public sealed record Row(string Name, string Behaviour, int Ship, int Team, int Priority, bool Chat, bool Deployed, string Status, string? Problem); public static IReadOnlyList<string> Render(string arena, bool owner, IReadOnlyList<Row> bots, IReadOnlyDictionary<int,int> matchSizes); }` — every returned line starts with `Marker`; non-owner rows render an empty problem.
- `?bots data` (anyone in the arena): sends `Render(...)` lines to the asker only, using the same status the owner list uses (supervisor seat, `BotProblem`, deployed flag). `?bots stopall` (owner only): `SetBotDeployment(..., false)` for every deployed bot; replies `Bots: stopped N bots.`

- [ ] **Step 1: Failing tests** (`BotDataTests`): `renders_head_bots_matches_and_end_in_order`; `bot_lines_survive_names_with_spaces` (name `Big Bob`, problem `Not launched: a pilot uses this name` → exact tab-separated line); `a_non_owner_sees_no_problem_text`; `every_line_starts_with_the_marker`. Plus `data_goes_only_to_the_asker` as a review note for the manual check (Step 4).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** `BotData.Render`, the two subcommands, help text, docs line.
- [ ] **Step 4: Run** the suite and the zone build — PASS. Manual (local zone, see Task 5 setup): `?bots data` as Botowner shows the lines (with the raw marker) in an old client's chat; a guest typing nothing sees nothing; confirm in the receiving client's log that U+0001 arrived intact (if the zone strips it, pick another unprintable marker and update both sides).
- [ ] **Step 5: Commit** `arena bots: ?bots data for the in-game window, ?bots stopall`.

### Task 2: Game: data lines into a model, hidden from chat (oss-client)

**Files:** Create `crates/oss-client/src/game/bots_view.rs`; Modify `crates/oss-client/src/game/mod.rs`, `crates/oss-client/src/game/sandbox.rs` (the `SessionEvent::Chat` arm beside `settings_chat`).

**Interfaces:**
- Produces: `pub struct BotRow { pub name: String, pub behaviour: String, pub ship: u8, pub team: i32, pub priority: u8, pub chat: bool, pub deployed: bool, pub status: String, pub problem: Option<String> }`; `pub struct BotsView { pub arena: String, pub owner: bool, pub bots: Vec<BotRow>, pub matches: Vec<(i32, i32)> }`; `pub struct BotsFeed` with `fn line(&mut self, text: &str) -> bool` (true = it was a data line, swallow it) and `fn latest(&self) -> Option<&BotsView>` (the last complete answer; replaced only when an `end` arrives).
- In `sandbox.rs`: for server lines (`name.is_none()`), `if self.bots_feed.line(&text) { continue; }` — always swallowed, window open or not.

- [ ] **Step 1: Failing tests** in `bots_view.rs`: `parses_a_full_answer`; `parses_a_name_with_spaces_and_an_empty_problem`; `an_unfinished_answer_keeps_the_last_list`; `ignores_ordinary_chat` (`"Bots: created …"` → false); `a_malformed_line_is_swallowed_but_ignored` (marker + garbage → true, no change to latest).
- [ ] **Step 2–4:** run (FAIL), implement, run (PASS); clippy/fmt clean.
- [ ] **Step 5: Commit** `bots window: read the zone's bot data, keep it out of chat`.

### Task 3: Game: the window, list and edit (oss-client)

**Files:** Create `crates/oss-client/src/game/bots_ui.rs`; Modify `crates/oss-client/src/game/sandbox.rs` (hold it, open from the Esc menu, refresh timer, draw), `crates/oss-client/src/game/sandbox/menu.rs` (Esc → B opens the window; remove the chat-prompt bot panel and its `BOT_ROWS`/`BOT_NOTES`), and shared drawing/input helpers extracted from `settings_ui.rs` only where both use them.

**Interfaces:**
- Consumes: `BotsView` (Task 2).
- Produces: `pub enum BotsAction { None, Send(String), Close }`; `pub struct BotsUi` with `fn new() -> Self`, `fn set_view(&mut self, v: &BotsView)`, `fn input(&mut self, strokes: &[Stroke]) -> BotsAction`, `fn click(&mut self, x: f32, y: f32) -> BotsAction`, `fn draw(&mut self, c: &Canvas, r: &mut Renderer, cursor: Option<(f32,f32)>)`, `fn reply(&mut self, text: &str) -> bool` (a `Bots: …` line while open: shown on the edited row, true = swallow).
- Views: **List** (rows: a "Create a bot" row for owners, then one row per bot: name, behaviour, ship, team, status; match sizes under the list); **Bot** (Enter on a row: behaviour, ship, team, priority, chat as settings-window lines; changing a value returns `Send("?bots set <name> <...>")`).
- Keys per Global Constraints; D on a selected bot returns `Send("?bots deploy <name>")` or `Send("?bots stop <name>")` by its deployed flag; Del asks "Remove <name>? Y/N" then `Send("?bots remove <name>")`; Stop all asks "Stop all bots? Y/N" then `Send("?bots stopall")`; M opens match sizes (per team: size or off → `Send("?bots match <freq> <size|off>")`).
- Sandbox: while open, send `?bots data` every 2 s (not when closed); route `Bots:` server lines through `BotsUi::reply` before chat.

- [ ] **Step 1: Failing tests** in `bots_ui.rs` (drive `input`/`click` with synthetic strokes and a `BotsView`): `d_deploys_a_stopped_bot_and_stops_a_deployed_one`; `editing_ship_sends_set_ship`; `del_and_stop_all_ask_first`; `a_non_owner_window_sends_nothing`; `every_line_fits_the_box`; in sandbox tests (or a small pure helper): `bots_replies_go_to_the_window_only_while_open`; `refresh_runs_only_while_open`.
- [ ] **Step 2–4:** run (FAIL), implement, run (PASS); clippy/fmt clean; wasm lib build passes.
- [ ] **Step 5: Commit** `bots window: list, edit in place, deploy key, stop all`.

### Task 4: Game: Create step by step (oss-client)

**Files:** Modify `crates/oss-client/src/game/bots_ui.rs`.

**Interfaces:** Create flow inside `BotsUi`: steps Name → Behaviour → Ship → Team → Chat → Summary; Esc goes back one step (Esc on Name returns to the list); typed name uses the chat line's printable-ASCII rule, max 20 characters; Summary offers **Deploy** (`Send("?bots create <name> <behaviour> ship <n> team <t> chat <on|off>")` then, when the zone's reply says created, nothing more — create already deploys) and **Save stopped** (same create, then `Send("?bots stop <name>")`).

- [ ] **Step 1: Failing tests**: `create_walks_the_steps_and_sends_one_create`; `esc_goes_back_one_step`; `save_stopped_creates_then_stops`; `a_name_over_20_characters_is_cut`.
- [ ] **Step 2–4:** run (FAIL), implement, run (PASS); clippy/fmt clean.
- [ ] **Step 5: Commit** `bots window: create a bot one step at a time`.

### Task 5: End to end (both repos)

- [ ] **Step 1:** Local zone as in arena-bots part 1 (scratch zone `/private/tmp/claude-501/-Users-chris--claude-spares-spare-s4DzY9/a4945a10-5c0e-4209-a49a-b9b939cf3421/scratchpad/zone`; `deploy/apply-local.sh <zone> out/modules/Dare.Arenas` after a Release build; check the Listen port is free; owner `Botowner`/`testpass123`, arena `botlab`). Run the desktop game (`cargo run -p oss-client --release -- …` pointed at 127.0.0.1:<port>, signed in as Botowner): Esc → B opens the window with live statuses; create a bot through the steps and deploy it; D stops and redeploys it; edit its ship; Stop all; a guest's window is read-only. Screenshot each (computer-use or the client's screenshot option) for the report.
- [ ] **Step 2:** Merge `origin/main` / `dare/main` into the branches, re-run all tests. Landing on `main` and deploying (zone + `deploy/deploy-client.sh`) are Chris's decision after review.
