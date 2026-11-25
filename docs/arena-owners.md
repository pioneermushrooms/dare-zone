# Owning an arena on Foundation

On the Foundation zone (`5.161.238.245:5000`, Continuum or oss-client) any player can ask for an
arena of their own. If staff say yes, the arena is yours: you choose its settings, its map and
its graphics, and you can pick up to five co-mods to help run it. Everything is public: who asked
for what, who decided, and why.

## First: you need a password on your name

Arena requests and arena powers only work for a **logged-in, password-protected name**. Anyone
could type your name, so the zone only trusts a name once it has been checked against a password.

Foundation has its own accounts (there is no billing server). Every player needs one: the zone
only lets in names that have been claimed, and arena requests and powers only work for a logged-in
name. To claim yours:

1. Go to https://subspacefoundation.com/account/signup and enter your name, a password (8 to 31
   characters) and a recovery email. We email you a 6-digit code; enter it and the name is yours.
   It's first come, first served.
2. In game, log in with that same name and password. That makes you logged in, which you need to
   request or own an arena or to use `?weblogin`.
3. Forgot your password? Use https://subspacefoundation.com/account/forgot: we email a code to your
   recovery email and you choose a new password. You can also change it, and your email, while signed
   in on the site.

**If you played here before:** your old name and password still work in game. The first time you
sign in on the site (https://subspacefoundation.com/account/) with your old name and password, you
will be asked to add a recovery email (we send a code to confirm it). Until you do, you can't
recover the account if you forget the password.

If a login says "This name isn't claimed", this is the step you are missing. Passwords are changed on the site.

## Making an arena

Type, in any arena:

    ?makearena <name> [what the arena is for]

for example `?makearena skyrace Fast racing laps for warbirds`. Or use *Make an arena* in your
hub on the site. There is no approval: the arena is yours at once.

- The name is 3 to 15 characters of lowercase letters and digits. It must start with a letter
  and must not end in a digit. Names already in use (and `public`, `2v2pub`, `3v3pub`, `4v4pub`,
  `duel`) are taken.
- The description is optional, up to 120 characters. Everyone can read it.
- You can own one arena at a time. `?requestarena` still works and does the same thing.

Then go to your arena with `?go <name>`. Staff can still suspend an arena that breaks the rules.

## What owners and co-mods can do

In **your own arena** (the powers do not work anywhere else):

- **Owner:** change settings (`?arenaset` or the web page), upload the map and graphics, pick
  co-mods, and the moderator tools below.
- **Co-mods** (up to 5): change settings and use the moderator tools. They cannot upload files
  or change who the co-mods are.
- **Moderator tools** (both): arena messages (`?a`), the game timer, locking or recycling the
  arena, flags and balls, setting players' ships and teams, prizes, spectating, and chat masks.
  Kicks, bans and zone-wide messages stay with zone staff.

## Commands

For everyone:

| Command | What it does |
| --- | --- |
| `?makearena <name> [pitch]` | Make an arena of your own, right away (needs a password, see above). |
| `?arenarequests` | Lists the newest arenas (and any requests from before arenas launched without approval). |
| `?arenalog [name]` | The last 10 public events (requests, decisions, co-mod changes, …), for all arenas or one. |
| `?myarena` | Shows the arena you own: status, co-mods, when someone last entered it. |
| `?weblogin` | Sends you a one-time link to the arena web page (valid 10 minutes, works once). |

Owners and co-mods, in their own arena:

| Command | What it does |
| --- | --- |
| `?arenaset <Section:Key> <value>` | Change one setting, e.g. `?arenaset Warbird:BulletSpeed 3000`. |
| `?arenaget <Section:Key>` | Show the current value of one setting. |

Owners only, in their own arena:

| Command | What it does |
| --- | --- |
| `?comod` | Lists your co-mods. |
| `?comod add <player>` / `?comod remove <player>` | Adds or removes a co-mod (public in `?arenalog`). A new co-mod must have an account (claimed at subspacefoundation.com/account/signup). |
| `?arenamap <file.lvl>` | Uploads a map from your Continuum folder; it becomes the arena's map. |
| `?putlvz <file.lvz>` | Uploads a graphics file from your Continuum folder and adds it to the arena. Uploading a file with the same name as one already there replaces it. |
| `?rmlvz <file.lvz>` | Removes a graphics file from the arena (and deletes it). |

Staff:

| Command | What it does |
| --- | --- |
| `?approvearena <name>` | Approves a request: creates the arena and makes the asker its owner (moderators and up). |
| `?declinearena <name> <reason>` | Declines a request; the reason (one line, up to 120 characters) is public (moderators and up). Nobody can approve or decline their own request. |
| `?createarena <name> [owner]` | Creates an arena at once, without a request; you (or the named player, who needs a Foundation account) own it. Public in `?arenalog` (super moderators and sysops). |
| `?arenaadmin suspend <name> <reason>` | Closes an arena to players (super moderators and sysops). |
| `?arenaadmin revive <name>` | Reopens a suspended or archived arena. |
| `?arenaadmin transfer <name> <player>` | Gives the arena to another player (a name with a Foundation account). |
| `?arenaadmin archive <name>` | Closes the arena at once; its folder is stored away at the next server restart (nothing is deleted). |

To take an arena or its powers away from an owner, use `?arenaadmin` (suspend, transfer or
archive). Removing the owner's group with `?rmgroup` does not last: the module grants it again
the next time they enter their arena.

Staff who own or co-mod an arena keep their own staff group there; their staff group already has
the owner commands, which still work only in the arena they own (or co-mod).

Uploads with `?arenamap`/`?putlvz` need Continuum (it sends the file). With any other client, use
the web page.

## The web page

Sign in at <https://subspacefoundation.com/account/login> (or type `?weblogin` in game for a
one-time link, valid 10 minutes). Your hub is <https://subspacefoundation.com/arena/me>.

- **Everyone** can see <https://subspacefoundation.com/arena/>: every player-owned arena with its
  owner, co-mods and players online, waiting requests, decisions and the public log. The home page
  lists every SubSpace zone, live from the directory servers, A to Z.
- **Without an arena**, the hub's *Make an arena* page launches one (same rules as
  `?makearena`).
- **Owners and co-mods** see their arena: *Settings* (grouped by section, with a description,
  default and range for each).
- **Owners** also get *Map*: **Open the map editor** shows the arena's current map in the browser
  (pencil, rectangle, eraser, undo/redo, zoom and pan; tiles from the map's own tileset, or flat
  colours, or a tileset file of your own that stays in your browser). **Publish** saves it as the
  arena's map and recycles the arena, so players inside come straight back in with the new map.
  You can also upload a `.lvl` made elsewhere. Co-mods can open the editor to look and download,
  not publish. Then *Graphics* (upload `.lvz` files by drag-and-drop,
  remove one with its **Remove** button; a file with the same name replaces the old one) and
  *Crew* (add or remove co-mods; same rules as `?comod`).
- **Staff** get a *Staff* page (<https://subspacefoundation.com/arena/staff>): waiting requests
  with Approve and Decline (moderators and up; a decline needs a public reason), *Create arena*
  (super moderators and sysops, same as `?createarena`), every arena and the event log.
  Suspend, revive, transfer and archive stay in game (`?arenaadmin`).

## Limits

- One arena per player, and one waiting request per player.
- Up to 5 co-mods per arena.
- Map: one `.lvl` per arena, at most 2 MB.
- Graphics: `.lvz` files of at most 5 MB each, at most 10 per arena. Replacing a file keeps its
  slot; `?rmlvz` (or the web page) frees one.
- Settings: at most 20 changes per minute per arena (in game and web together). `Modules`
  settings can't be changed, and the map and graphics lists change only through uploads.
- An arena nobody enters for **30 days** is archived (never while players are in it): it closes
  and its files are stored away at the next server restart, not deleted. Staff can bring it back with `?arenaadmin revive`.
- Every decision (request, approval, decline, staff create, transfer, suspend, revive, archive, co-mod change)
  is public in `?arenalog` and on the web page.
