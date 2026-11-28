# Public pilot profiles — design (approved by Chris 2026-11-26)

## Why
The shared graphics gallery credits contributors. Each credit links to the contributor's public
profile, which keeps their contributions together.

## Goals
- `/pilot/<name>`: one public page per pilot name, viewable signed out.
- Shows: name, "Pilot since <month year>", optional avatar (one of their Shipyard ships), optional
  bio (≤ 280 chars, plain text), contributions (count + newest designs, from the gallery API),
  public arenas they own, ships in their Shipyard slots.
- The owner (signed in as that exact name) edits bio, avatar and the three show/hide switches
  (contributions, arenas, ships; all shown by default).
- Staff moderate: clear a bio, hide a profile (reversible flag; hidden profiles read as
  "not found" to everyone but the owner and staff).
- Links in: account menu ("Your profile"), account page, gallery credits (gallery session),
  arena list owners/co-mods, chat player list.

## Non-goals
Kill/death stats, time played (not recorded today), comments, followers, leaderboards.

## Anti-goals (privacy)
Never expose email, IP, last sign-in, login info, account ids, or which other names share the
pilot's email or no-email group. A profile is per name and stands alone.

## Approaches
1. **Plugin-served page + JSON API (chosen).** `profile.html`/`profile.js` in the plugin's
   wwwroot, `/pilot/*` proxied by Caddy to the plugin, data from `/account/api/profile`. Same stack
   as the account pages; one new Caddy route that `remote-install.sh` adds once.
2. Static site page `site/pilot/` with Caddy try_files — still needs a Caddy rewrite and the data
   API anyway; no gain.
3. `/account/pilot/<name>` — no Caddy change, but the URL was agreed with the gallery session as
   `/pilot/<name>` and is the nicer public one.

## Data
New table `pilot_profiles(account_id PK, bio, avatar_ship, hide_contribs, hide_arenas, hide_ships,
mod_hidden, updated)`; deleted with the name in `DeletePilot`. Public shape built in Core
(`PilotProfile`), so privacy is unit-tested.

## API
- `GET /account/api/profile?name=<n>` → `{ ok, name, since, bio, avatar, show:{contribs,arenas,ships},
  arenas:[name], ships:[{id,name}], mine, staff, hidden }` — 404 `{ok:false}` for unknown or
  moderated-hidden names (unless owner/staff).
- `GET /account/api/profile/ship?name=<n>&id=<id>` → PNG sheet, only for a ship in that pilot's
  slots or their avatar, and only when ships are shown (avatar always allowed).
- `POST /account/api/profile` (owner): `{ bio, avatar, showContribs, showArenas, showShips }`.
- `POST /account/api/profile/moderate` (staff): `{ name, clearBio?, hidden? }`.
- Contributions: the gallery's `GET /account/api/gallery?pilot=<n>&limit=` (gallery owns it); the
  page shows nothing for that section if it 404s.

## Left to the builder
Exact styling (match the account pages), gallery page link once Chris names it.
