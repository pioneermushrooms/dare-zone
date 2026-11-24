# Dare client netcode review (2026-11-23)

Code-first review of "laggy movement / sprites in wrong places". Code read:
`oss-client` (`dare`), `oss-core` (`oss-net`, `oss-sim`, `oss-proto`), and the server's
position handling in SubspaceServer v4 (`Core/Modules/Game.cs`).

**Status (2026-11-23):** findings 1–4 fixed, plus two found while fixing (see "Also fixed").
Branch `netcode-precision` in both `oss-core` and `oss-client`, uncommitted. All tests, clippy
and fmt clean. Not yet checked in a live game.

## Findings, most likely cause first

### 1. Other ships are slowed to base top speed between updates — CONFIRMED
`oss-core/crates/oss-sim/src/world.rs` `step_ship` (top-speed block, ~L636–656) runs for
remote ships too. A remote ship has no input and its starting stats, so every tick its
velocity is clamped to `ship.stats.speed`. Anyone with speed upgrades or afterburners is
slowed locally, falls behind, then gets pulled forward by the next update (200 ms glide, or a
jump at ≥ 4 tiles). That's rubber-banding, which matches "laggy movement".
Probe test (since removed): a remote Warbird reported at 4020 was clamped to 2010 (base) after
one tick.
**Fix:** remote ships should coast on the reported velocity (walls/bounce still apply; skip
thrust and the top-speed clamp, or clamp only to the absolute max speed).

### 2. The sender's latency is ignored, so ships are drawn behind where they are — CONFIRMED
The server stamps forwarded 0x28/0x05 packets with its own receive time and puts the sender's
upstream latency in a separate field (`Game.cs` ~L1453, ~L1493: `Time = gtc`,
`C2SLatency = latency`). The client parses `c2s_latency` (`oss-proto/src/position.rs`) but
`oss-session/src/lib.rs` (~L1295–1340) uses only `extend_ticks(now, p.time)`. So every
remote state is treated as `c2s_latency` ticks fresher than it is (commonly 5–15 ticks =
50–150 ms). Ships sit behind their true position, and shots look like they come from behind
the ship.
**Fix:** `server_ticks = extend_ticks(now, time).wrapping_sub(c2s_latency)`.

### 3. Remote shots start late and from the stale spot
`oss-client/src/game/sandbox.rs` ~L1437 calls `set_remote_state` then `fire_remote`.
On a small correction `set_remote_state` leaves `ship.pos` at the old spot and only queues a
glide, so `fire_remote` (`oss-sim/src/remote.rs`) spawns from the uncorrected position.
Projectiles also aren't advanced by the packet's age: a shot fired 100 ms ago starts at the
muzzle now.
**Fix:** spawn from the extrapolated report position, then advance each new projectile
`age` ticks.

### 4. Clock sync trusts every single sample
`oss-core/crates/oss-net/src/connection.rs` `on_sync_response` (~L1030) replaces
`clock_offset` with each 5 s sample: no smoothing, no preference for low-RTT samples. One
jittery round trip moves the whole clock by the jitter, so all remote ages shift together and
every ship nudges at once. The world clock slews 1 tick per 10 ticks, or hard-jumps above 200.
**Fix:** keep the lowest-RTT sample over the last N syncs, or move toward it gradually.
Minor: the `|offset| <= 10 → 0` deadband is applied to the absolute offset, not the
change. It's harmless in practice, since the offset is large, but it should apply to the change.

### 5. Smaller / unverified
- Weapon fire is sent reliably (`oss-session/src/lib.rs` ~L1882). The server forwards
  positions unreliably and only accepts a packet newer than the last
  (`Game.cs` ~L1031). A retransmitted weapon packet arrives late and may be dropped or
  forwarded late. Unverified; compare with sending unreliably.
- A pending glide is cancelled on wall bounce (`world.rs` ~L711), leaving the rest of the
  correction until the next update.
- Remote ships are extrapolated to server-now in `pump_network`, then stepped again for the
  pending ticks in the same frame. That's a few ms ahead at most, so negligible.

## Also fixed
- **Late shots were dropped.** The client threw away any update older than the latest one,
  even one carrying a weapon. The server forwards those (a retransmitted or reordered weapon
  packet), so the shot was lost. Now it fires from its own report and age without moving the
  ship (`SessionEvent::PlayerFired`).
- **Own updates stamped with frame time.** A frame that catches up several ticks stamped every
  shot in it with the frame's wall time. Each update now carries the moment its tick stands
  for (`Session::send_position_at`), so the server's latency figure and everyone's catch-up are
  exact.
- Weapon packets stay reliable: the server forwards late weapon packets with their true
  latency, and receivers now catch them up, so reliability costs no precision and loses no shots.
- Left as is: glide cancelled on wall bounce. The next update corrects it within ~100 ms, and
  continuing the glide would push into the wall.

## Not a problem
- Out-of-order/duplicate drops (`update_position`), 16-bit timestamp extension,
  own-position stamping, and the position-flood guard all look correct.

## Cheap logging to confirm after fixes (no video)
Per remote update, at debug: `pid, age, c2s_latency, correction px, snapped?`. Per sync:
`rtt, offset, offset change`. A short session on Foundation with 2+ real players gives
before/after numbers.

## Suggested order
1 and 2 first (small, testable, likely most of the visible problem), then 3, then 4.
Fixes for 1, 3 and 4 land in `oss-core`; 2 lands in `oss-session`.

## Round 2: always track the true position (2026-11-23)
Goal: every client draws every ship as close to where it really is as possible, at all
times, because hits are decided on the victim's client against the victim's true position.
- **No more 200 ms glide.** A report moves the remote ship's position to the truth at once
  (carried forward by age, walls included). Collisions, its shots and radar use that
  immediately. Only the picture eases over from where it was shown, so there is no flicker:
  28% of the gap closes each tick (half in ~20 ms, 95% in ~90 ms, gone at < 0.5 px). Jumps at
  ≥ 4 tiles, spawns and warps. `Ship::draw_offset`, `Ship::drawn()`, `world::ease_offset`.
- **Extra update when your controls change.** The client sends a position update the tick
  you start or stop thrusting, afterburning or turning, at most one per 20 ms, on top of the
  timed ones. Every other client corrects its guess about your ship right away instead of up
  to `SendPositionDelay` later. The server forwards every position update in range and has
  no rate limit on them (`Game.cs` position handler). `Sandbox::report_motion_change`.
- **Measurement.** Every 10 s online the client logs one `tracking:` line: report count,
  jumps, prediction error (median/p95/max px) split into coasting vs accelerating, and
  drawn-vs-true error. Per-report detail at debug level (`RUST_LOG=debug`).
- **Not done yet: predicting acceleration.** Wait for the `tracking:` numbers. If
  "accelerating" error stays large even with the extra updates, predicting thrust from
  consecutive reports is the next step.
