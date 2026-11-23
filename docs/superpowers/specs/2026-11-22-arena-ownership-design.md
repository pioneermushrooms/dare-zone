# Arena ownership for Dare — design

Date: 2026-11-22 · Status: draft for approval · Zone: Dare (`5.161.238.245:5000`, SubspaceServer .NET v4.0.0)

## Intent

Give Dare players a public, low-friction way to make and run their own arena, the way
Roblox lets creators publish into a shared world — without DMing whoever runs the zone.
The server can already do almost all of this (per-arena staff, arena folders, settings
editing, uploads) but only a sysop can reach it. This module is the self-serve front door
to those powers, with the rules written down and every decision visible.

It is the first piece of a larger direction: Dare as an open "commons" zone that
celebrates creatorship, where one operator cannot quietly become the gatekeeper. Hosting
your own zone is the later "graduate" path; arenas come first because the population can
only fill one or two zones.

## Goals

1. A player can **request** an arena in game (`?requestarena <name> <pitch>`) or on the
   web; the request sits in a **public queue** anyone can read.
2. Any staff member can **approve** (`?approvearena <name>`) or **decline with a public
   reason**. Approval creates the arena from a template and makes the requester its
   **owner** — automatically, scoped to that arena only.
3. An owner can change **settings, map and graphics (LVZ)** for their arena, and appoint
   up to **5 co-mods** for it. Nothing outside their arena.
4. **Guardrails:** one arena per player to start; arenas empty for **30 days** are
   **archived, not deleted**, and can be revived on request; staff can suspend, revive or
   transfer any arena.
5. **Transparency:** every request, approval, decline, transfer, suspension and archive is
   recorded and listed publicly (in game and on the web).
6. A **web page for owners**: sign in without a password, see the arena's settings with
   plain descriptions, change them, drag in a map or LVZ.

## Non-goals

- Owner-run bots or server modules (staff only).
- Owners scripting game rules (the later Roblox-style layer; this module is its base).
- Hosting zones for other people, and the one-command zone kit.
- The public hub site listing all zones (a separate project; the owner pages will live
  inside it later).
- Connecting Dare to the ISOJ biller (configuration, waiting on credentials).

## Anti-goals

- **An owner affecting anything beyond their arena** — other arenas' files, zone settings,
  global staff lists. Every write path checks scope.
- **Raw `?putfile`-style access** for owners: uploads land only in their arena's folder,
  only as `.lvl` / `.lvz`, with size limits.
- **Hidden decisions.** No silent approvals, declines or removals.
- **Forking SubspaceServer.** This is a plugin loaded through `Modules.config`.

## Constraints

- Plugin targets the same .NET version and `SS.Core` API as the running release
  (v4.0.0, .NET 9 runtime on the VPS). Built and tested against that release's
  `SS.Core.dll`.
- Arena groups: `turf1`, `turf2` share the group `turf` (trailing digits). Arena names are
  therefore **3–16 chars, `a-z`, `0-9`, `-`, starting with a letter and not ending in a
  digit**, so each owned arena is its own group. Reserved: every existing arena folder
  name, `public`, names starting with `(`, and the matchmaking base names.
- Continuum players can upload via the client file transfer (`?putfile`-style); oss-client
  and web players have no file transfer, so the web page is their upload path.
- The web page needs HTTPS for sign-in tokens to be safe. Domain: **subspacefoundation.com**
  (Cloudflare, DNS-only A records to the VPS); **Caddy** on the VPS terminates TLS and
  proxies `/arena/*` to the plugin's local listener. The site's other pages are static
  files served by Caddy from `/var/www/foundation`.

## Approaches considered

- **A. Server module + small web dashboard (chosen).** One C# plugin holds all rules
  (requests, approvals, ownership, permissions, safe uploads) and exposes a small HTTP
  API; the web pages only call that API. In-game commands and the web can never disagree,
  and the later scripting layer plugs into the same place.
- **B. Outside bot editing the server's files.** No server code, but fragile: edits
  `staff.conf` and arena folders then forces reloads; permissions and files can drift.
- **C. Web page alone writing files on disk.** Fastest demo, but nothing in game, and two
  writers on the same files.

## Design

### Pieces

- **`Dare.Arenas` plugin** (new repo `dare-zone`, project `src/Dare.Arenas`), loaded with
  `<module type="Dare.Arenas.ArenaOwnership" path="bin/modules/Dare.Arenas/Dare.Arenas.dll"/>`.
- **Store:** SQLite file `data/dare-arenas.db` (Microsoft.Data.Sqlite, already shipped
  with the server). Tables: `requests`, `arenas` (name, owner, status, created, last
  active, archived), `comods`, `events` (append-only public log).
- **Groups** (new files in `conf/groupdef.dir`, referenced from `groupdef.conf`):
  - `arenaowner` = the `mod` capabilities + `cmd_quickfix` + this module's owner commands.
  - `arenamod` = the `mod` capabilities.
  Granted with `IGroupManager.SetPermGroup(player, group, global: false, comment)` while
  the player is in their arena, which writes the arena-group section of `staff.conf`. The
  module re-applies grants on arena entry so they survive renames of the player's session.
- **Template:** `arenas/_template/arena.conf` (Dare's sensible defaults: a standard ship
  set, public map, no modules attached). Approval copies it to `arenas/<name>/`.

### Commands

| Command | Who | Does |
|---|---|---|
| `?requestarena <name> <pitch>` | any logged-in player | Validates name and limits, records a pending request |
| `?arenarequests` | anyone | Lists pending requests and recent decisions |
| `?approvearena <name>` / `?declinearena <name> <reason>` | `mod` and up | Decides; approval creates the arena and grants ownership |
| `?myarena` | owner | Status, co-mods, last activity, web sign-in hint |
| `?comod add|remove <player>` | owner (in their arena) | Up to 5 `arenamod` grants, arena-scoped |
| `?arenaset <Section:Key> <value>` / `?arenaget <Section:Key>` | owner, co-mods | Change/read one arena setting (allow-list below) |
| `?putmap` / `?putlvz` | owner (Continuum) | Requests the file from the client, writes only into `arenas/<name>/` |
| `?weblogin` | owner, co-mods | Sends a one-time sign-in link by private message |
| `?arenaadmin suspend|revive|transfer|archive <name> [arg]` | `smod` and up | Staff overrides, each logged |
| `?arenalog [name]` | anyone | Recent public events |

`?quickfix` (Continuum's settings editor) stays available to owners in their own arena.

### What an owner may change

- **Settings:** any arena setting except a deny-list: the `[Modules]` section (attaching
  server modules), `[Misc]`/`[Security]` keys the config help marks *restricted*, and
  `General:Map` / `General:LevelFiles` (set only by uploads). Restricted is read from the
  server's config help (`IConfigHelp`) so the list follows the server, not a copy.
- **Map:** one `.lvl` up to 2 MB; becomes `arenas/<name>/<name>.lvl` and `General:Map`.
- **Graphics:** up to 10 `.lvz` files, 5 MB each; added to `General:LevelFiles`.
- **Co-mods:** up to 5.

### Lifecycle

`requested → approved (active) | declined`; `active → archived` after 30 days with no
player entering (checked daily); `archived → active` on owner request + staff `revive`;
`active → suspended` by staff (arena closed to players, reason public). Archiving moves
the folder to `arenas/_archive/<name>/` and keeps ownership records.

### Web dashboard (served by the plugin)

- `HttpListener` on `127.0.0.1:8088`, reached as `https://subspacefoundation.com/arena/…`
  through Caddy (`handle_path /arena/*` → `reverse_proxy 127.0.0.1:8088`).
- **Sign-in:** `?weblogin` creates a random 32-byte token (10-minute, single use) and PMs
  `https://subspacefoundation.com/arena/login?t=<token>`; the page swaps it for a session cookie
  (24 h). No passwords on the web.
- **Public pages:** request queue, decisions, event log, list of active arenas with owner
  and population.
- **Owner pages:** arena settings grouped by section with each setting's description from
  `IConfigHelp`, edit + save (same allow-list and code path as `?arenaset`); drag-and-drop
  map/LVZ upload (same checks as `?putmap`); co-mod list.
- **Request form:** a player can also request on the web after `?weblogin`.

### Safety rules (all paths)

- Every write resolves the target path and refuses anything outside `arenas/<name>/`
  (no `..`, no absolute paths, no symlinks followed).
- Uploads: extension and size checked, `.lvl` header sanity-checked, stored under a fixed
  name — never the client-supplied path.
- Rate limits: one pending request per player; 20 setting changes per minute per arena.
- The module never grants `smod`/`sysop`, never edits the `[(global)]` staff section.

## Testing

- Unit tests (xUnit, as in `SubspaceServer/src/Core.Tests`): name validation and reserved
  names; allow/deny of setting keys; path-scope checks (`..`, absolute, symlink); state
  transitions; token expiry and single use; rate limits.
- Integration on a local server copy: request → approve as staff → enter arena as owner →
  `?arenaset`, `?comod`, `?quickfix`; non-owner refused; archive after a faked 30 days.
- Web: sign-in round trip, settings save, map upload, scope refusal.

## Left to the builder

Exact help/error wording, table schemas, the template's default settings, page styling
(match the Dare skin's look), and how the daily archive check is scheduled.
