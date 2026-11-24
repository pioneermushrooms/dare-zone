# Foundation roadmap (2026-11-24)

Live today: zone **Foundation** (5.161.238.245:5000, SubspaceServer v4.0.0 + Dare.Arenas plugin),
site **subspacefoundation.com** (Caddy), player-owned arenas (request → staff approve → owner
tools in game + web), **Foundation accounts** (site sign-up with recovery email + emailed code via
Resend, same name/password in game, no guests). Code: `~/dare-zone` (branches: `main`,
`accounts` not yet merged). Client: `~/oss-src/oss-client` branch `dare` (Dare.app).
Process note: reviews only on login/permission/file-write code; cheaper model by default; one
end-to-end check per milestone; fresh session per project.

## 1. Clear login errors (small, next)
- Server: unclaimed name already gets Continuum custom text; add the same wording for lockout and
  a hint on bad password where the protocol allows (`AuthCode.CustomText` is Continuum-only).
- Dare client (oss-client fork): map refusal codes to plain messages + action:
  unknown/NoPermission → "This name isn't registered — Register at subspacefoundation.com/account/signup" (button opens browser);
  BadPassword → "Wrong password — Forgot it? subspacefoundation.com/account/forgot";
  locked → "Too many tries — wait 10 minutes".

## 2. Staff create arenas directly (small)
- New staff command `?createarena <name> [owner]` (smod+/sysop): creates from template, makes the
  creator (or named claimed account) owner, logs a public event — no request/approve round trip.
- Same as a button on the web staff view.
- Typical sysop commands that already exist on the server: `?setgroup`/`?rmgroup`, `?listmod`,
  `?kick`, `?z` (zone message), `?a` (arena message), `?warn`, `?lock`/`?unlock`, `?specall`,
  `?setship`/`?setfreq`, `?flagreset`, `?recyclearena`, `?recyclezone`, `?shutdown`,
  `?makearena`, `?geta`/`?seta` (arena settings), `?getg`/`?setg` (global), `?reloadconf`,
  `?getfile`/`?putfile`, `?lsmod`/`?insmod`/`?rmmod`, `?lastlog`. Ours: `?arenaadmin`,
  `?approvearena`, `?declinearena`, `?arenarequests`, `?arenalog`.

## 3. Website modernization (medium)
- Visual redesign: big imagery (ship art, skins, map renders), few words, one clear message.
- Player hub after sign-in: my account, my arena (settings, uploads, co-mods), create/request.
- Staff view: requests, arenas, create arena, events.
- Zone list: every directory zone listed equally (live from directory servers).

## 4. Map editor on the site (medium)
- Productize the Chart Room spike (`~/dare-zone/spikes/map-editor`): signed-in owners open their
  arena's map, edit, Publish → existing upload API → `?recyclearena`.

## 5. Dare client (separate project, oss-client fork)
- Splash screen: full-size window, modern layout (keep skins support), zone list sorted by name or
  population — NOT by ping (it reshuffles).
- In-game issues seen: laggy movement, sprites in wrong places → investigate interpolation /
  prediction and packet handling (compare with Continuum on the same zone; record with
  `--record` and replay).

## Housekeeping
- Merge `accounts` into `main`.
- Deferred minors: see `.superpowers` ledgers (copied to `~/oss-src/dare-previews/`).
- Clean live test data (test arenas/requests) when Chris decides.
