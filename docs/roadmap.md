# Foundation roadmap (2026-11-24)

Live today: zone **Foundation** (5.161.238.245:5000, SubspaceServer v4.0.0 + Dare.Arenas plugin),
site **subspacefoundation.com** (Caddy), player-owned arenas (request → staff approve → owner
tools in game + web), **Foundation accounts** (site sign-up with recovery email + emailed code via
Resend, same name/password in game, no guests). Code: `~/dare-zone` (`main`; `accounts` merged). Client: `~/oss-src/oss-client` branch `dare` (Dare.app).
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
- Netcode precision pass done 2026-11-23 (branch `netcode-precision` in `oss-core` and
  `oss-client`, committed, not pushed or merged, not yet live-tested): see
  `docs/netcode-review.md`. Next: rebuild Dare.app, play with 2+ players, read the
  `tracking:` log lines.
- Splash screen: full-size window, modern layout (keep skins support), zone list sorted by name or
  population — NOT by ping (it reshuffles).
- In-game issues seen: laggy movement, sprites in wrong places → **code-first netcode review**,
  not screen recording (watching playback burns credits and solo play can't reproduce real
  multiplayer conditions). Read and check, against the protocol and the server's handling in
  SubspaceServer v4:
  - clock sync (time sync request/response, server-time offset, drift correction);
  - position packet handling (timestamp → age of update, out-of-order/duplicate drops);
  - remote ship extrapolation/interpolation (velocity × elapsed time, snapping vs smoothing);
  - local prediction and correction when the server disagrees;
  - weapon/projectile spawn timing relative to the shooter's timestamp.
- Back the review with cheap numeric logging (ping, clock offset, correction distance per
  update), not video. Use `--record`/replay only for a targeted repro once code review points
  at a suspect.

## 6. Voice menu (built 2026-11-26, not merged)
- Tribes-style quick voice lines: V + letters, 73 phrases (team/global), server `?v` with spam
  limit and arena switch `Voice:Enabled`; Enter-to-chat in the client (default on).
- Branches: dare-zone `voice-menu`; client `voice-menu` (worktree `~/oss-src/voice-menu`).
  Clips: Chatterbox, ralph voice + monster treatment, tooling in `voice/`.
- Spec/plan: `docs/superpowers/specs/2026-11-25-voice-menu-design.md`,
  `docs/superpowers/plans/2026-11-25-voice-menu.md`. Tried in game by Chris 2026-11-26: good.
- Before deploy: check real Continuum ignores ids 128+ (zone already sends it text-only);
  `?shutup` path untested. Later: voice packs, a bindable voice key with Enter-to-chat off,
  deferred minors in `.superpowers/sdd/2026-11-25-voice-menu/progress.md`.

## Housekeeping
- Deferred minors: see `.superpowers` ledgers (copied to `~/oss-src/dare-previews/`).
- Clean live test data (test arenas/requests) when Chris decides.
