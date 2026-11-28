# One account per person: saved items and editing rights follow the person, not the name

Status: design, waiting for Chris's approval. Nothing built.

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
- **Email belongs to the person.** Confirming an email on any name sets it on all the
  person's names (today that only happens for names without an email). If that email
  already has other names (another person), the confirmed code proves the same person
  controls both, so the two people merge into one (still max 10 names, else refused).
  There's no way to split a name off a person; to give a name up, delete it.
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

Every saved-item table gets `person_id` (who owns it). The old key stays as **"made by"**
(the name that saved it), used only for credit and for the public profile.

| Store | Today | After |
|---|---|---|
| `armory_items`, `shipyard_ships`, `gallery_items` | `account_id` | + `person_id` (owner); `account_id` = made by |
| `armory_slots`, `shipyard_slots`, `pilot_graphics`, `pilot_graphics_prefs`, `pilot_settings` (keys/macros) | PK on `account_id` | rebuilt with PK on `person_id` |
| `pilot_maps`, `pilot_tilesets`, `pilot_lvz`, `pilot_setting_sets` | `owner` name text | + `person_id` (owner); `owner` = made by |
| `pilot_profiles`, wall | `account_id` | **stay per name** (see below) |
| `game_passes`, `web_sessions`, `login_failures`, `email_codes` | per name | stay per name (they are about logging in as that name) |

- Store methods take the person instead of the owner name / account id, e.g.
  `ArenaStore.Maps(long person)`, `SaveMap(long person, string madeBy, ...)`. Callers in
  `Web/*Library.cs`, `SettingSetsApi.cs`, the armory/shipyard/gallery pages and the game's
  graphics lookup (flying name → `PersonOfName` → that person's graphics) change with them.
- **Joining the names of one person (migration and email merge):** lists (ships, armory
  items, gallery items, maps, tilesets, LVZ, settings sets) are simply put together; nothing
  is lost. Limits (25 maps etc.) count per person from now on; a person already over a limit
  keeps everything but can't add more until under it. For one-per-person things (equipped
  slots, own graphics on/off, keys/macros, each pilot-graphics slot) the name that signed in
  most recently wins; a pilot-graphics slot it left empty is filled from the others.
- `ArenaStore` libraries need name → person for the migration: the plugin runs it once
  after both stores are open (`store_markers` key `persons_v1`). Rows whose owner name no
  longer exists (deleted before this change) get no person and stop showing, which also
  closes the "new owner of a freed name inherits its maps" hole.
- **Gallery credit:** shown as the name that shared it; if that name is deleted, as the
  person's first remaining name (today the item vanishes from the list).

**Why profiles stay per name:** `/pilot/<name>` is the public face of one name and the profile
design deliberately hides which names share an email. Shared bio/ships/arenas would show that
link. So each name keeps its own profile; any of the person's names may edit any of them
(switching names on the site is already password-free). "Show my ships" shows ships that name
made; the avatar may be any of the person's ships.

## 4. Arena owner and co-mod rights follow the person

Yes, as Chris said. The data stays name text (`arenas.owner`, `drafts.owner`, `comods.player`,
`draft_invites.player`, `requests.player`): it's what is shown and what staff commands like
`?transfer` take. The **check** becomes per person:

- `ArenaStore` gets a resolver `Func<string, long?> PersonOfName` set by the plugin (default:
  every name is its own person, so store tests without accounts still work).
- `RoleIn(arena, player)`: **owner** when the owner name and `player` are the same person;
  **co-mod** when any co-mod name is `player`'s person; draft entry (invites) the same.
  `OwnedArenas`, `DraftsOwnedBy`, `RolesFor`, `Holdings` cover all of the person's names.
- Limits per person: 3 owned arenas, 3 drafts, 3 pending requests across all names. Adding
  one of your own names as co-mod or invitee is refused ("that's you").
- **In game nothing new is needed:** `ApplyGrant` already runs on every arena entry and asks
  `RoleIn` for the flying name. With `RoleIn` per person, whichever of your names enters gets
  the owner/co-mod group in that arena's staff.conf section (`SetPermGroup`, arena-scoped),
  and the stale-group revoke keeps working. The name still has to be password-authenticated
  in game, as today. No separate permission check.
- **On the site** `/arena/me`, `/arena/map`, uploads and the settings page all go through
  `RoleIn`, so any name signed in sees and edits all the person's arenas and drafts.
- **Zone staff powers stay per name** (`[(global)]` in staff.conf): giving every name a
  sysop has the sysop group is a staff decision, not something an account should do by
  itself. (Already relied on by the delete flow: a staff name can't be deleted.)

## 5. Deleting a name

- Saved items: nothing happens; they belong to the person. (Today they are deleted.)
- Per-name rows go: game passes, web sessions, login failures, email codes, its profile and
  wall posts on its wall.
- Arenas and drafts it owns, co-mod and invite entries naming it: renamed to the name that is
  signed in doing the delete (the person still owns them; nothing archived, no
  "keep with" choice needed). If that name is already co-mod there, the entry is just dropped.
  Grants are refreshed as today (`RefreshGrantsIn`).
- Items it made keep its name as "made by" only where it is still useful (gallery credit
  falls back as above).

## 6. Build steps (each with `dotnet test`)

1. `people`, `person_id`, migration with backup, `PilotsOf`/`AddPilot`/sign-ups/email merge,
   helpers `PersonOf`/`PersonOfName`/`NamesOf`.
2. AccountStore stores: armory, shipyard, pilot graphics, keys/macros, gallery, profile avatar.
3. ArenaStore libraries: maps, tilesets, LVZ, settings sets + their web callers (tell
   `tile-editor` and `editor-polish` before changing their tables).
4. Arena roles per person, per-person limits, delete flow.
5. Local zone with two names on one account: armory/shipyard/graphics/maps/LVZ/sets/arena
   editor visible and editable from both; game entry gives the owner group to either name;
   delete one name and everything is still there.
6. Merge into `main`, push. Deploy only when Chris says (one zone restart; the migration runs
   on start, with the db backup).

## Open points for Chris

- Merging two people when one confirms an email that already has other names: OK? (The
  alternative is refusing an email that already belongs to someone else's names.)
- When joining names, "last signed in wins" for equipped slots / keys / own-graphics slots.
- Zone staff powers stay per name.
- Profiles stay per name.
