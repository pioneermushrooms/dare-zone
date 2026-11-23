# Arena Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Foundation players request an arena, have staff approve it in public, and run it as its owner (settings, map, LVZ, co-mods) from in game and from `subspacefoundation.com/arena`.

**Architecture:** Two .NET 9 class libraries in `~/dare-zone`: `Dare.Arenas.Core` holds every rule (names, settings policy, uploads, path scope, lifecycle store, tokens, rate limits) with no server dependency, so it is fully unit-tested; `Dare.Arenas` is the thin SubspaceServer plugin that wires those rules to commands, groups, files, config and an `HttpListener` for the web pages. Caddy proxies `/arena/*` to the plugin.

**Tech Stack:** C# / .NET 9, SubspaceServer v4.0.0 `SS.Core` (git submodule), Microsoft.Data.Sqlite, xUnit, vanilla HTML/JS pages, Caddy.

**Spec:** `docs/superpowers/specs/2026-11-22-arena-ownership-design.md`

## Global Constraints

- Build against SubspaceServer **v4.0.0** (`vendor/SubspaceServer`, submodule at tag `v4.0.0`), `net9.0`; plugin `ProjectReference` to `vendor/SubspaceServer/src/Core/Core.csproj` with `<Private>false</Private><ExcludeAssets>all</ExcludeAssets>`; `<EnableDynamicLoading>true</EnableDynamicLoading>`.
- Plugin type `Dare.Arenas.ArenaOwnership`, loaded from `bin/modules/Dare.Arenas/Dare.Arenas.dll`.
- Arena names: 3–15 chars, `a-z 0-9`, start with a letter, must not end in a digit; reserved: existing arena folder names, `public`, anything starting `(`, and `2v2pub 3v3pub 4v4pub duel`. (Changed after live testing from 3–16 with `-`: SubspaceServer v4.0.0 maps `-` to `x` and truncates arena names at 15, so such arenas could never be entered.)
- Player names compare case-insensitively.
- Limits: 1 arena per player; 1 pending request per player; 5 co-mods; `.lvl` ≤ 2 MB (1 per arena); `.lvz` ≤ 5 MB, ≤ 10 per arena; 20 setting changes / minute / arena; archive after 30 days with no entry.
- Owner-editable settings: only keys the server's config help documents with `Scope == Arena`, excluding section `Modules` and keys `General:Map`, `General:LevelFiles` (those change only via uploads). *(Ruling: v4.0.0 has no "restricted" flag, so this allow-list replaces the spec's deny-by-restricted.)*
- Groups: `arenaowner` (= `mod` caps + `cmd_quickfix` + owner commands), `arenamod` (= `mod` caps); granted only arena-scoped (`global: false`); never `smod`/`sysop`, never `[(global)]`.
- Store: SQLite `data/dare-arenas.db`. Web: `HttpListener` on `127.0.0.1:8088`; public URL `https://subspacefoundation.com/arena/...`; sign-in token 32 random bytes, 10 min, single use; session cookie 24 h, `HttpOnly; Secure; SameSite=Lax`.
- Every decision (request, approve, decline, transfer, suspend, revive, archive, co-mod change) appends a public event.

## Review Focus

- Player names with spaces or different case (`Chris` vs `chris`) — same person everywhere. → Task 3 test `owner_match_ignores_case_and_spaces_kept`.
- Requester offline when approved — becomes owner on next entry to the arena. → Task 5 test `grant_applies_on_entry_after_offline_approval`.
- Arena folder already exists on disk (made by hand) — approval refuses with a clear message, nothing overwritten. → Task 3 test `approve_refuses_when_folder_exists`.
- Two staff decide the same request at once — exactly one decision wins, the other gets "already decided". → Task 3 test `second_decision_is_rejected`.
- Upload with the right extension but junk content — rejected before touching disk. → Task 2 test `rejects_wrong_magic`.

---

### Task 1: Repo scaffold and arena name rules

**Files:**
- Create: `Dare.sln`, `src/Dare.Arenas.Core/Dare.Arenas.Core.csproj`, `src/Dare.Arenas.Core/ArenaName.cs`, `tests/Dare.Arenas.Tests/Dare.Arenas.Tests.csproj`, `tests/Dare.Arenas.Tests/ArenaNameTests.cs`, `.gitmodules` (+ `vendor/SubspaceServer` at `v4.0.0`)

**Interfaces:**
- Produces: `public static class ArenaName { public static bool TryValidate(string input, IReadOnlySet<string> existing, out string normalized, out string error); }` — `normalized` is lower-case; `existing` holds folder names already in `arenas/` (case-insensitive).

- [ ] **Step 1:** `git submodule add https://github.com/gigamon-dev/SubspaceServer vendor/SubspaceServer && git -C vendor/SubspaceServer checkout v4.0.0`; create the solution and the two projects (`dotnet new classlib`, `dotnet new xunit`, `net9.0`).
- [ ] **Step 2: Failing tests** `ArenaNameTests`: valid `"Pipe-Dreams"` → `"pipe-dreams"`; rejects `"ab"`, 17 chars, `"turf1"` (ends in digit), `"1turf"`, `"a b"`, `"(x)"`, `"public"`, `"duel"`, `"2v2pub"`, and a name in `existing` (case-insensitive); each rejection has a non-empty `error`.
- [ ] **Step 3: Run** `dotnet test` — FAIL (type missing).
- [ ] **Step 4: Implement** `ArenaName.TryValidate`.
- [ ] **Step 5: Run** `dotnet test` — PASS. **Commit** `core: arena name rules`.

### Task 2: Settings, upload and path-scope policy

**Files:** Create `src/Dare.Arenas.Core/SettingsPolicy.cs`, `UploadPolicy.cs`, `ArenaPaths.cs`; tests `SettingsPolicyTests.cs`, `UploadPolicyTests.cs`, `ArenaPathsTests.cs`.

**Interfaces:**
- Produces:
  - `public sealed class SettingsPolicy { public SettingsPolicy(IEnumerable<(string Section, string Key)> documentedArenaKeys); public bool IsEditable(string section, string key); }` (case-insensitive; denies `Modules:*`, `General:Map`, `General:LevelFiles`, anything undocumented).
  - `public enum UploadKind { Map, Lvz }`; `public static class UploadPolicy { public static bool TryCheck(UploadKind kind, string fileName, ReadOnlySpan<byte> content, int existingLvzCount, out string error); }` — map: `.lvl`, ≤ 2 MB, starts with `BM`, `elvl`, or is a whole number of 4-byte tile entries; lvz: `.lvz`, ≤ 5 MB, starts with `CONT`, `existingLvzCount < 10`.
  - `public static class ArenaPaths { public static string? Resolve(string arenasRoot, string arenaName, string fileName); }` — returns the full path inside `arenasRoot/arenaName/` or `null` if `fileName` has a directory part, `..`, is rooted, or the target exists as a symlink.
- [ ] **Step 1: Failing tests**: `SettingsPolicyTests` (documented arena key editable; `Modules:AttachModules`, `General:Map`, `general:levelfiles`, undocumented key not); `UploadPolicyTests` (valid lvl/lvz pass; oversize; wrong extension; `rejects_wrong_magic` for `.lvz` not starting `CONT` and `.lvl` of 5 junk bytes; 11th lvz refused); `ArenaPathsTests` (`"map.lvl"` → inside; `"../x"`, `"/etc/passwd"`, `"a/b.lvl"`, `"..\\x"` → null; existing symlink → null).
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS. **Step 5: Commit** `core: settings, upload and path policy`.

### Task 3: Arena store and lifecycle

**Files:** Create `src/Dare.Arenas.Core/ArenaStore.cs`, `Models.cs`; test `ArenaStoreTests.cs`.

**Interfaces:**
- Produces (in `Models.cs`): `public enum ArenaStatus { Active, Suspended, Archived }`; `public enum RequestStatus { Pending, Approved, Declined }`; `public sealed record ArenaRequest(long Id, string Arena, string Player, string Pitch, RequestStatus Status, string? DecidedBy, string? Reason, DateTimeOffset At)`; `public sealed record OwnedArena(string Name, string Owner, ArenaStatus Status, DateTimeOffset Created, DateTimeOffset LastEntered, IReadOnlyList<string> CoMods)`; `public sealed record ArenaEvent(long Id, DateTimeOffset At, string Arena, string Actor, string Kind, string Detail)`; `public sealed record Result(bool Ok, string Message)`.
- Produces: `public sealed class ArenaStore : IDisposable` with ctor `(string dbPath, Func<DateTimeOffset> clock)` and methods `Result Request(string player, string arena, string pitch)`, `Result Approve(string arena, string staff, Func<string, bool> folderExists)`, `Result Decline(string arena, string staff, string reason)`, `Result AddCoMod(string arena, string owner, string player)`, `Result RemoveCoMod(string arena, string owner, string player)`, `Result Suspend/Revive/Transfer(...)`, `IReadOnlyList<string> ArchiveIdle(TimeSpan idle)` (returns names archived), `void MarkEntered(string arena)`, `OwnedArena? Get(string arena)`, `OwnedArena? OwnedBy(string player)`, `string? RoleIn(string arena, string player)` (`"owner"`, `"comod"` or null), `IReadOnlyList<ArenaRequest> Requests(bool pendingOnly, int max)`, `IReadOnlyList<ArenaEvent> Events(string? arena, int max)`. Names compared case-insensitively; each mutation runs in one SQLite transaction and appends an event.
- [ ] **Step 1: Failing tests** (temp db, fake clock): request→approve makes owner and event; `owner_match_ignores_case_and_spaces_kept` (`"Big Chris"` requests, `"big chris"` is owner, stored as `"Big Chris"`); one pending request per player; one arena per player; decline needs reason and is public; `approve_refuses_when_folder_exists`; `second_decision_is_rejected`; co-mod limit 5 and only owner adds; `ArchiveIdle(30d)` archives an arena last entered 31 days ago and not one at 29; revive returns to Active; transfer changes owner and clears nothing else.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** (Microsoft.Data.Sqlite; schema created on open). **Step 4: Run** — PASS. **Step 5: Commit** `core: arena store and lifecycle`.

### Task 4: Web sign-in tokens and rate limits

**Files:** Create `src/Dare.Arenas.Core/WebAuth.cs`, `RateLimiter.cs`; tests `WebAuthTests.cs`, `RateLimiterTests.cs`.

**Interfaces:**
- Produces: `public sealed class WebAuth { public WebAuth(Func<DateTimeOffset> clock); public string IssueLoginToken(string player); public string? Redeem(string token); /* returns session id */ public string? PlayerFor(string session); }` (token single use, 10 min; session 24 h; tokens/sessions are 32 random bytes, URL-safe base64).
- Produces: `public sealed class RateLimiter { public RateLimiter(int perMinute, Func<DateTimeOffset> clock); public bool TryTake(string key); }`
- [ ] **Step 1: Failing tests**: token redeems once; second redeem null; expired after 10 min; session valid 24 h then null; two tokens differ; limiter allows 20 then refuses 21st within a minute and allows again after.
- [ ] **Step 2–5:** run FAIL → implement → run PASS → **Commit** `core: web sign-in and rate limits`.

### Task 5: The plugin — requests, approvals, ownership

**Files:** Create `src/Dare.Arenas/Dare.Arenas.csproj` (plugin settings per Global Constraints, `OutDir` = `../../out/modules/Dare.Arenas`), `src/Dare.Arenas/ArenaOwnership.cs`, `src/Dare.Arenas/Commands.cs`, `deploy/groupdef.dir/arenaowner`, `deploy/groupdef.dir/arenamod`, `deploy/arenas/_template/arena.conf`; test `tests/Dare.Arenas.Tests/GrantLogicTests.cs`.

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: `public sealed class ArenaOwnership : IModule` (injects `ICommandManager`, `IChat`, `IGroupManager`, `IArenaManager`, `IPlayerData`, `IConfigManager`, `IConfigHelp`, `ILogManager`, `IMainloopTimer`); commands `?requestarena ?arenarequests ?approvearena ?declinearena ?myarena ?arenalog`; on `PlayerAction.EnterArena` calls `GrantLogic.GroupFor(role)` and `IGroupManager.SetPermGroup(player, group, false, "Dare.Arenas")` when the player's role in that arena lacks the group, and `ArenaStore.MarkEntered`.
- Produces: `public static class GrantLogic { public static string? GroupFor(string? role); }` (`owner`→`arenaowner`, `comod`→`arenamod`, else null).
- Approval copies `arenas/_template/` to `arenas/<name>/` (folder-exists check passed to `ArenaStore.Approve`), then PMs the requester if online.
- [ ] **Step 1: Failing test** `GrantLogicTests` incl. `grant_applies_on_entry_after_offline_approval` (store approves while player absent; `RoleIn` later returns `owner` → `GroupFor` = `arenaowner`).
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Local integration:** extract the v4.0.0 `osx-arm64` release to `~/dare-zone/.local-zone`, copy `out/modules/Dare.Arenas`, the group files and template, add the module line and `[Staff]`-free sysop (`staff.conf [(global)] Tester = sysop`), run with `DOTNET_ROLL_FORWARD=Major`; with `oss-bot` as `Asker` send `?requestarena testland my pitch`, as `Tester` send `?approvearena testland`; check log for the folder, the event and `staff.conf [testland] Asker = arenaowner` after `Asker` enters `testland`.
- [ ] **Step 6: Commit** `plugin: requests, approvals and ownership`.

### Task 6: Owner tools in game

**Files:** Modify `src/Dare.Arenas/Commands.cs`; create `src/Dare.Arenas/Uploads.cs`.

**Interfaces:**
- Consumes: `SettingsPolicy`, `UploadPolicy`, `ArenaPaths`, `RateLimiter`, `ArenaStore.RoleIn/AddCoMod/RemoveCoMod`.
- Produces: `?arenaset <Section:Key> <value>` / `?arenaget <Section:Key>` (owner or co-mod, in their arena; `IConfigManager.SetStr(arena.Cfg, …, permanent: true)`), `?comod add|remove <name>` (owner), `?putmap <clientfile>` / `?putlvz <clientfile>` (owner; `IFileTransfer.RequestFileAsync`, then `UploadPolicy.TryCheck`, then move to `ArenaPaths.Resolve` result; map becomes `<arena>.lvl` and sets `General:Map`; lvz appended to `General:LevelFiles`), `?weblogin` (PMs `https://subspacefoundation.com/arena/login?t=<token>`).
- [ ] **Step 1:** Local integration as in Task 5 Step 5: owner `?arenaset Bullet:BulletSpeed 3000` succeeds and `?arenaget` shows it; `?arenaset Modules:AttachModules X` refused; co-mod add (6th refused); non-owner refused; 21st setting in a minute refused. (Uploads need Continuum; verify with the web path in Task 7.)
- [ ] **Step 2: Commit** `plugin: owner tools`.

### Task 7: Staff tools, daily archive, web API and pages

**Files:** Create `src/Dare.Arenas/StaffCommands.cs`, `src/Dare.Arenas/Web/WebServer.cs`, `src/Dare.Arenas/Web/wwwroot/{index.html,arena.html,app.js,style.css}`.

**Interfaces:**
- Produces: `?arenaadmin suspend|revive|transfer|archive <name> [arg]` (smod+); a daily `IMainloopTimer` calling `ArchiveIdle(30 days)` and moving folders to `arenas/_archive/<name>/`.
- Produces HTTP (all under `/arena/`, JSON unless noted): `GET /` (public page: requests, decisions, active arenas, events), `GET /login?t=` (redeem → cookie → redirect `/arena/me`), `GET /api/public` (requests, arenas, events), `GET /api/me` (role, arena, settings with config-help descriptions), `POST /api/settings` (`{section,key,value}`, same policy + rate limit as `?arenaset`), `POST /api/upload?kind=map|lvz&name=` (raw body, same checks as `?putmap`), `POST /api/request` (`{arena,pitch}`); all writes require a session and the owner/co-mod role.
- Pages styled like the Dare skin (gunmetal panels, cyan/orange), mobile-friendly.
- [ ] **Step 1:** Local integration: `?weblogin` link → cookie → change a setting on the page and see it with `?arenaget`; upload `maps/smallmape.lvl` as the arena map and enter the arena with oss-client (screenshot); junk `.lvl` refused; non-owner session gets 403.
- [ ] **Step 2: Commit** `plugin: staff tools, archive and web pages`.

### Task 8: Deploy to Foundation

**Files:** Create `deploy/deploy.sh`, `deploy/Caddyfile.snippet`, `docs/arena-owners.md`.

- [ ] **Step 1:** `deploy/deploy.sh` builds Release, copies the module, group files and template to `/opt/subspace/zone`, adds the `Modules.config` line and the two `groupdef.conf` sections if absent, restarts `subspace`; Caddy gets `handle_path /arena/* { reverse_proxy 127.0.0.1:8088 }` inside the `subspacefoundation.com` block.
- [ ] **Step 2:** Make Chris sysop: ask his in-game name, add `[(global)] <name> = sysop` to `staff.conf`; he sets his password in game.
- [ ] **Step 3:** Live check with oss-bot and the website; `docs/arena-owners.md` explains commands and the web page in plain words; link it from the site.
- [ ] **Step 4: Commit** `deploy: Foundation`.
