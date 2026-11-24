# Arena builder suite, and drafts — design

Date: 2026-11-24 · Repo: dare-zone (branch `zone-directory`) · Status: approved by Chris (approach and open questions)

## Intent

A new Foundation pilot should be able to build an arena in their first hour, in the browser,
and fly in it while they build. They pick or draw a tileset, draw a map, start from a ship
preset, tune ships while flying, and adjust every game setting on a modern page with sliders
and toggles. When they like it, they apply to make it public. Asking for approval before
anything exists, which is what happens today, makes people do all the work blind. So the
building happens in a private draft arena they own from the first minute.

This doc covers the whole suite's shape and order, and designs its first piece, **drafts**,
in full. Each later piece gets its own design when its turn comes.

## The suite, in order

1. **Drafts** (this doc): an instant private `#arena` the pilot owns, invites, keeping
   uninvited pilots out, request to go public, publish update.
2. **Settings page**: every arena setting as sliders, toggles and choices, grouped (ships,
   weapons, bounty and prizes, flags, soccer, ...). Each has a plain description and is
   marked "applies live" or "applies on arena restart". Ship presets. It covers
   **tuning in flight**: the page stays open beside the game, and live changes apply right away.
3. **Map editor**: the Chart Room prototype (`spikes/map-editor`), finished inside the site
   and editing the draft's map.
4. **Tileset editor**: pick a tileset or draw 16×16 tiles. A tileset is the 304×160 image of
   190 tiles that a `.lvl` carries.
5. **Ship art editor**: edit the ships `.bm2`. The site packs it into a `.lvz` for players.
6. **Polish**: previews, templates, sharing.

Later and separate: editing inside the game client.

## How it is built (decided)

- **The Dare.Arenas plugin is the permission checker and the data API.** It decides who owns
  which draft, who is invited, and what may be saved or published. It changes rarely, and
  changing it restarts the zone (only with nobody online, via `deploy/deploy.sh`).
- **Editor and draft pages are plain files on the site,** served by Caddy from
  `/var/www/foundation` like `/play/`. Shipping a page needs no zone restart. The existing
  arena and account pages move out of the plugin over time (not in this piece).
- **Players building never restarts the zone.** Saving writes only that arena's files. At
  most that one arena reloads (`?recyclearena`), which affects only the pilots inside it.

Approaches considered:
- **A. Plugin + site pages (chosen).** One permission authority. Pages update freely.
- **B. A separate web app server.** Faster UI work, but a second service to host and secure,
  and permissions would still live in the plugin.
- **C. Editors inside the game client.** Native feel while flying, but slow to build, only
  for people using our client, and awkward for drawing. Later.

## Drafts

### Goals

1. **Instant.** A signed-in Foundation pilot creates a draft from the web
   ("+ Create arena") or in game (`?draft <name>`). It exists immediately as the private arena
   `#<name>`, copied from the arena template. No staff step.
2. **Owner powers inside the draft only.** The creator gets the owner group in `#<name>` only:
   settings, map and graphics uploads, recycle, co-mods. Same allow-list and file rules as
   owned arenas today.
3. **Isolated.** Drafts are unlisted (the server hides `#` arenas). Only the owner, their
   invited pilots (up to 5) and zone staff (mod and up) may enter. Anyone else entering is
   sent to the public arena with a short message.
4. **Request to go public.** The owner presses "Request to go public" with a short pitch.
   It joins today's public request queue. Staff review the draft itself (they can enter and
   fly). Approval creates the public arena `<name>` from the draft's files and makes the
   requester its owner.
5. **The draft stays as the workshop.** After approval the owner keeps editing `#<name>`.
   "Publish update" copies the draft's files to the live `<name>` and recycles it. Staff
   approve only the first publish. Every publish is in the public event log. Staff can roll
   back the last publish or suspend the arena, as today.
6. **Guardrails.** One draft per pilot. Same upload size limits. A draft untouched (not
   edited, not entered) for 30 days is archived, not deleted (as with public arenas, and
   revivable on request).

### Non-goals

- The settings page, map, tileset and ship editors (later pieces). Drafts use today's
  settings and upload paths.
- Moving the existing arena and account pages out of the plugin.
- More than one draft per pilot. Shared drafts with several owners.
- Joining a draft from the browser client's start screen list (a later touch. The login
  arena field already accepts `#name`).

### Anti-goals

- **A draft owner affecting anything outside their draft** (and, after approval, their live
  arena). Every write path checks scope, as today.
- **A public arena appearing without a staff decision.** The first publish always goes
  through the queue.
- **Silent changes.** Creating, inviting, requesting, approving, publishing, rolling back and
  archiving are all public events.
- **Losing work.** Archiving moves files aside. Publish keeps the previous live copy so it
  can be rolled back.

### Constraints

- SubspaceServer v4: an arena whose name starts with `#` is private (`Arena.IsPrivate`), and
  `?arena` lists hide it from other players. Its config is opened by base name, so a draft's
  settings live in `arenas/#<name>/arena.conf`. Base names drop trailing digits, which is why
  arena names may not end in a digit (the existing rule: 3–15 chars, `a-z0-9`, starting with
  a letter).
- Names: the draft `#<name>` and the public `<name>` are reserved together. Creating a draft
  is refused if either exists, is requested, or is reserved.
- Ownership grants use the existing arena-scoped `IGroupManager.SetPermGroup` (staff.conf's
  arena section `#<name>`). The `[(global)]` section is never touched.
- Path safety: `#` is allowed only as the first character of the draft folder name. Every
  write still resolves strictly inside `arenas/#<name>/` or `arenas/<name>/`, never through
  a link (see `ArenaPaths`).

### Design

**Store (`data/dare-arenas.db`).**
- New table `drafts`: `name` (without `#`), `owner`, `created`, `last_touched`, `status`
  (Active, Requested, Published, Archived), `live` (bool, set once approved).
- New table `draft_invites`: `name`, `player`.
- Requests gain a nullable `draft` link, so a request made from a draft is reviewed as one.

**Commands (in game).**

| Command | Who | Does |
|---|---|---|
| `?draft <name>` | signed-in pilot without a draft | Creates `#<name>` and sends them there |
| `?draft` | draft owner | Status, invites, how to request or publish |
| `?draftinvite add\|remove <player>` | owner | Up to 5 invited pilots |
| `?requestpublic <pitch>` | owner, in their draft | Request to go public |
| `?publish` | owner of an approved draft | Copy the draft to the live arena and recycle it |
| `?arenaadmin rollback <name>` | smod+ | Restore the previous live copy |

**Web API (plugin, under `/arena/api/`).** All of these need the existing session cookie
and are scoped by owner checks.
- `POST drafts`: create a draft (`{name}`).
- `GET drafts/mine`: the pilot's draft and its state.
- `POST drafts/invite`: add or remove an invite.
- `POST drafts/request`: request to go public (`{pitch}`).
- `POST drafts/publish`: publish an update.

**Entry control.** On arena entry, if the arena is a draft and the player is not its owner,
an invitee or mod-and-up staff, move them to the public arena. Use the server's own arena
change call, with the message "That arena is a private draft."

**Approve.**
- Staff `?approvearena <name>` (or the web staff action) on a draft-linked request copies
  `arenas/#<name>/` to `arenas/<name>/` (arena.conf, the map, and the `.lvz` files the
  config names).
- It then makes the requester the owner of `<name>`, marks the draft `live`, and logs it.

**Publish.**
- Copy the draft's files over the live arena's files.
- Before the copy, move the current live files to `arenas/_published/<name>/<utc time>/`,
  keeping the last 3.
- Then `?recyclearena` the live arena and log the publish.

**Pages (site, static).** `/build/` is the owner's "My draft" page:
- With no draft: a create form.
- With a draft: its name, invites, status, "Fly in it" (opens `/play/` with the arena set),
  "Request to go public" or "Publish update", and links to today's settings and upload page
  (`/arena/me`), which learns to edit the draft.

The browser client's "+ CREATE" opens `/build/` instead of the old request page.

**Archive.** The existing daily archive pass also archives drafts untouched for 30 days. It
moves the folder to `arenas/_archive/#<name>/` and frees the one-draft slot.

### Decided by Chris

- **Live arenas change only through Publish.**
  - Once a draft is live, the owner's and co-mods' content changes go to the draft: settings
    (`?arenaset`, the web settings), the map and graphics uploads.
  - In the live arena those paths answer "Edit your draft #<name>, then publish".
  - Moderation keeps working live: kick, lock, spec, warn, messages, recycle.
- **Existing owners get a draft automatically.** On the first plugin load with this
  feature, every owned arena without a draft gets `#<name>`, copied from its current live
  files. It is owned by the arena's owner and marked `live`. Co-mods carry over to the draft.
  `?requestarena` without a draft keeps working for the old request flow, but approval of
  such a request now also creates the draft, so every owned arena ends up with one.

### Settled details (calls made while designing, marked as such)

- **Names (call).** The public name is always the draft name without `#`. There is no rename.
  A pilot's draft and live arena share one name, so a pilot holds at most one name at a
  time.
- **Accounts (answered).** Every game login is a Foundation account (FoundationAuth, no
  guests), so invites name accounts. An invite to a name with no account is refused.
- **Entry control (call).** It acts after entry: the pilot briefly reaches the draft, then is
  moved out. Pre-entry denial isn't required. Removing an invite moves that pilot out if
  they're inside. Bots are treated like any other player unless they're staff. The builder
  confirms the hook and the `#` config lookup in `vendor/SubspaceServer`. Creating a draft
  makes `arenas/#<name>/` before the pilot is sent there, so it never opens on the default
  config.
- **Lifecycle (call).**
  - Draft status runs Active → Requested → (declined: Active again, and the owner may edit
    and re-request) or (approved: Published, `live` = true).
  - After approval it stays Published. An archived draft can be revived on request.
  - While archived, Publish is unavailable. The live arena is untouched by the draft's
    archiving.
  - The name stays reserved while either the draft or the live arena exists, archived or not.
- **Ownership moves (call).**
  - When staff transfer the live arena, the draft moves with it: the new owner gets the
    draft, and the old owner's grants in both arenas are removed.
  - Publishing into a suspended or archived live arena is refused.
- **Publish safety (call).**
  - A publish copies into a staging folder first, then swaps it in. If anything fails, the
    previous live files stay or are restored, and nothing is recycled.
  - The last 3 published copies are kept per arena. `?arenaadmin rollback` can step back
    repeatedly through them.
  - Each arena is limited to one publish per 2 minutes.
- **Files that travel (call).** A fixed allow-list: `arena.conf`, the `.lvl` named by
  `General:Map`, and the `.lvz` files named by `General:LevelFiles`. `#include` lines refer
  to shared server files and are copied as written. Later pieces add their file types to
  the list explicitly.
- **Web safety (call).**
  - The new POST endpoints use the same checks as today's arena API: the `dare_session`
    cookie, the `X-Requested-With` header, and JSON bodies.
  - `/build/` is a static page. Without a session it sends the visitor to `/account/login`
    and back.
- **"Fly in it" (call).** It links to `/play/?arena=%23<name>`. The play page reads `arena`
  and pre-fills the game's arena field.

### Testing

- **Unit tests (xUnit, as in the existing tests).**
  - Draft name and reservation rules.
  - One draft per pilot.
  - Invite cap.
  - Entry decision (owner, invitee, staff, other).
  - Path scope for `#` folders, including `#` mid-name, `..` and links.
  - Approve and publish file sets.
  - Rollback keeping 3 copies.
  - The 30-day archive rule.
- **Live check on Foundation (nobody online).**
  - Create a draft from the web, fly in it, and change a setting.
  - A second test pilot is sent out, then invited and let in.
  - Request, have staff approve, check the public arena exists, then publish an update and roll it back.

### Left to the builder

- Exact messages and help text.
- Table schemas beyond the fields named.
- Whether the "My draft" page lives at `/build/` or `/arena/build/` on disk.
- How `/arena/me` switches between the draft and the live arena.

`?requestarena` without a draft keeps working (see "Decided by Chris": approval also creates the draft).
