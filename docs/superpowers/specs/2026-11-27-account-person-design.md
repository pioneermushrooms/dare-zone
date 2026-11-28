# One account per person: saved items and editing rights follow the person, not the name

Status: design, revised with Chris's answers (2026-11-27), waiting for a final OK. Nothing built.

Chris: switching to another fly-as pilot name, or renaming, must not change what you can see,
edit or own. Every name on the account gets the same game/arena editing powers and the same
saved items.

## Today

- An "account" row is one pilot name. Names are grouped on the fly by `AccountStore.PilotsOf`:
  by confirmed recovery email, else by `link_id`. There is no id for the person, and the
  grouping changes whenever an email changes.
- Armory, shipyard, pilot graphics, keys/macros (`pilot_settings`), gallery: keyed by one
  name's `accounts.id`. `DeletePilot` deletes them all with the name.
- Maps, tilesets, LVZ, settings sets: keyed by the owner's name text in `ArenaStore`. Deleting
  the name leaves them behind, and whoever claims the freed name later gets them (bug).
- Arena owner, co-mods, draft invites: name text. Only that exact name gets the powers.

## 1. The person id

- New table `people(id INTEGER PRIMARY KEY AUTOINCREMENT, created TEXT NOT NULL)` and a new
  column `accounts.person_id INTEGER` (indexed). Every name row has one, always.
- **Existing rows (one-time migration on startup, marker `persons_v1` in `account_meta`):** the
  store copies the db file to `<db>.before-persons` first. Then each group `PilotsOf` makes
  today becomes one person: names on one confirmed email share one; names without an email
  share one per `link_id` group; a name with an unconfirmed email and any non-active row
  (reserved legacy names) gets its own.
- From then on `PilotsOf` is just "active names with my `person_id`". `link_id` is no longer
  written or read (the column stays, unused).
- **New sign-up** (with or without email): new person. **Add a name:** same person.
  **Delete a name:** only that row goes; the person and everything it owns stay (the last
  name can't be deleted, so a person always has a name). **Rename** (doesn't exist yet): the
  row keeps its `person_id`, so nothing moves.
- **Email belongs to the person.** A new name takes the account's recovery email and its
  current password (as today). Changing the email on any name changes it for all the
  person's names. An email already used by another account is refused ("That email is
  already used by another account"), so one email always means one account.
- Recovery (email code resets one name's password) and game login are unchanged: each name
  keeps its own password.

## 2. The helper (for every store, including `pilot_game_modes`)

```csharp
// AccountStore
public long? PersonOf(long accountId);          // the person a name row belongs to; null if no such row
public long? PersonOfName(string name);         // same, by pilot name (case-insensitive); null if unknown
public IReadOnlyList<Account> NamesOf(long person); // the person's active names, by name
```

New per-person tables key on `person_id INTEGER NOT NULL`. Web code gets it from the
signed-in session: `Accounts.PersonOf(cur.Account.Id)`.

## 3. Moving the stores

Two kinds of saved things:

- **Libraries** belong to the account: every name sees, uses, edits and deletes the same
  ships (shipyard), armory items, Foundry/gallery items, maps, tilesets, LVZ and settings
  sets. They get `person_id`. The old key stays only as **"made by"** (the name that saved
  it), for credit and the public profile.
- **Loadouts** stay per name, like playing a different game mode: keys/macros
  (`pilot_settings`), equipped ship slots (`shipyard_slots`), equipped armory slots
  (`armory_slots`), pilot graphics slots (`pilot_graphics`) and the own-graphics switch
  (`pilot_graphics_prefs`). They keep `account_id` (the name). A slot may hold any of the
  account's ships or items, not just ones that name made.
- **Load from another name:** on each loadout page (keys/macros, shipyard, armory, pilot
  graphics) a "Load from..." menu lists your other names; picking one copies that name's
  loadout onto the name you're on (it asks first; it replaces what's there). One store call:
  `CopyLoadout(long person, long fromAccountId, long toAccountId, LoadoutPart parts)`.

| Store | Today | After |
|---|---|---|
| `armory_items`, `shipyard_ships`, `gallery_items` | `account_id` | + `person_id` (owner); `account_id` = made by |
| `pilot_maps`, `pilot_tilesets`, `pilot_lvz`, `pilot_setting_sets` | `owner` name text | + `person_id` (owner); `owner` = made by |
| keys/macros, ship slots, armory slots, pilot graphics, own-graphics switch | per name | stay per name; + "load from" |
| `pilot_profiles`, wall | per name | stay per name (approved) |
| `game_passes`, `web_sessions`, `login_failures`, `email_codes` | per name | stay per name (logging in as that name) |

- Library methods take the person instead of the owner name / account id, e.g.
  `ArenaStore.Maps(long person)`, `SaveMap(long person, string madeBy, ...)`. Callers in
  `Web/*Library.cs`, `SettingSetsApi.cs` and the armory/shipyard/gallery APIs change with them.
- Migrating existing rows: libraries of all names in one group are put together; nothing
  is lost. Library limits (25 maps etc.) count per account from now on; an account already
  over a limit keeps everything but can't add more until under it. Loadouts need no merging.
- `ArenaStore` libraries need name → person for the migration: the plugin runs it once
  after both stores are open (`store_markers` key `persons_v1`). Rows whose owner name no
  longer exists (deleted before this change) get no person and stop showing, which closes
  the "new owner of a freed name inherits its maps" hole.
- **Gallery credit:** shown as the name that shared it; if that name is deleted, as the
  account's first remaining name (today the item vanishes from the list).

**Profiles stay per name** (approved): `/pilot/<name>` shows that name only and never which
names share an account. "Show my ships" shows ships that name made; any of the account's
names may edit any of its profiles.

## 4. Arenas belong to the account

- `arenas`, `drafts` (the `#name` working copy of a live arena) and `comods` get
  `person_id`: the account that owns the arena, and each co-mod account. Owner and co-mod
  rights are checked by account, never by name.
- The shown owner is the name that made the arena (`owner` text, kept as "made by"); if
  that name is deleted, the account's first remaining name. A co-mod is shown by the name
  they were added as, with the same fallback. Staff commands that take a name
  (`?transfer arena <name>`, adding a co-mod) look the name up and store its account.
- `RoleIn(arena, player)` = the role of `player`'s account. `OwnedArenas`, `RolesFor`,
  `Holdings` list everything the account owns or co-mods. Adding one of your own names as
  co-mod is refused ("that's you").
- **In game nothing new is needed:** `ApplyGrant` already runs on every arena entry and asks
  `RoleIn` for the flying name. With `RoleIn` by account, whichever of your names enters gets
  the owner/co-mod group in that arena's staff.conf section (`SetPermGroup`, arena-scoped),
  and the stale-group revoke keeps working. The name still has to be password-authenticated
  in game, as today.
- **On the site** `/arena/me`, `/arena/map`, uploads and the settings page all go through
  `RoleIn`, so any name signed in sees and edits all the account's arenas.
- **Zone staff powers stay per name** (approved).

### Arena limit: 10 per account, and "request more"

- Each account may own **10** public arenas (all names together). The `#name` working
  copies don't count. The old limits on drafts and pending requests go.
- New table `arena_limits(person_id PRIMARY KEY, max INTEGER, by TEXT, at TEXT)`: a raised
  limit for one account; no row = 10.
- At the limit, `/arena/me` shows **Request more arenas** (how many, and a short "what for").
  One open request per account at a time. It goes to staff:
  - a card at the top of `/arena/staff` with the account's names, its arenas, the reason,
    a number box (prefilled with what they asked for) and **Approve** / **Decline**;
  - an in-game message to arena admins who are online, and an email to the zone's staff
    address (new `[DareArenas] StaffEmail` in global.conf) with a link to the card.
  - Approve sets the account's limit to the number you typed; the pilot gets an in-game
    note (if online) and sees the new limit on `/arena/me`. Decline sends the reason.

## 5. Deleting a name

- Arenas, co-mod rights and library items belong to the account, so nothing moves and
  nothing is archived. Where the deleted name was shown as owner, co-mod or "made by", the
  account's first remaining name is shown instead. The delete prompt no longer asks where
  to keep arenas. Grants in game are refreshed as today (`RefreshGrantsIn`), so the deleted
  name's arena-scoped staff.conf groups are removed.
- That name's own rows go: its loadouts, game passes, web sessions, login failures, email
  codes, its profile and the posts on its wall.

## 6. Build steps (each with `dotnet test`)

1. `people`, `person_id`, migration with backup, `PilotsOf`/`AddPilot`/sign-ups/email per account,
   helpers `PersonOf`/`PersonOfName`/`NamesOf`.
2. AccountStore libraries (armory, shipyard, gallery) by account; loadouts stay per name
   with "load from another name"; profile avatar.
3. ArenaStore libraries: maps, tilesets, LVZ, settings sets + their web callers (tell
   `tile-editor` and `editor-polish` before changing their tables).
4. Arenas, drafts and co-mods by account; limit 10 per account with "request more arenas"
   (staff card, in-game note, email); delete flow.
5. Local zone with two names on one account: libraries and the arena editor visible and
   editable from both; loadouts separate, "load from" copies; game entry gives the owner
   group to either name; request more arenas end to end; delete one name and everything
   is still there.
6. Merge into `main`, push. Deploy only when Chris says (one zone restart; the migration runs
   on start, with the db backup).

## Decided with Chris (2026-11-27)

- An email already used by another account is refused (one email = one account).
- Loadouts per name with "load from another name"; libraries per account.
- Arena limit 10 per account, raised by staff through "request more arenas".
- Zone staff powers stay per name. Profiles stay per name.

## Still to confirm

- Staff address for the "request more arenas" email (`[DareArenas] StaffEmail`).
