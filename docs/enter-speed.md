# Getting from Enter to the arena: timings

Measured 2026-11-26 in the desktop app's built-in browser (Chrome, WebGPU) on a home connection
of roughly 0.5–0.7 MB/s down and 130–200 ms ping to the zone. Guest pilot, public arena
(`smallmape.lvl`, 54 KB, 27.9 KB as the zone sends it). Times are from the page's own
`performance.now()`, the resource timings and the game's console lines.

Client changes: dare-client branch `enter-speed` (16bcfa8, e99558c), on `bots-menu-play`.
Site: `?v=16` (this branch).

## Stage by stage

| Stage | Before (v15) | After (v16) |
|---|---|---|
| Page load (load event) | 0.74 s | 0.4–0.6 s |
| Game download starts | on Enter | as soon as the page has loaded |
| Game download size | 15.1 MB raw, **9.35 MB** on the wire | 8.0 MB raw, **2.75 MB** on the wire |
| Game download time (uncached) | **14.2 s**, after Enter | 4.7–6.2 s, in the background before Enter |
| Compile + start (WebGPU, surface) | ~0.03 s | ~0.01 s (compiled while downloading) |
| Gateway (QUIC) + VIE handshake | 0.87 s | 0.15–0.4 s (link opened on Enter; first packet queued, not dropped) |
| Zone login | 0.70 s | 0.6 s (about 3 round trips; unchanged) |
| News + map, first visit | 4.7 s | 5.0 s (unchanged: zone send rate, see below) |
| News + map, return visit | 4.7 s (browser couldn't cache) | **0.4 s** (IndexedDB cache) |
| Sounds | in the game download (5.7 MB) | fetched after arrival in the arena |
| **Enter → flying, first visit** | **21.2 s** (+ time in the sign-in box) | **~6.5 s** if the page was open ~5 s first; ~10 s if Enter is pressed the moment the page loads |
| **Enter → flying, return visit** | ~7 s (map downloaded every time) | **1.6 s** (1.26 s from "Fly as a guest") |

## What changed

1. **Background download.** `start.mjs` starts fetching and compiling the game
   (`WebAssembly.compileStreaming`) once the page has loaded. Enter only shows the progress
   that's left.
2. **Sounds out of the WebAssembly.** 5.7 MB of 16-bit PCM barely compresses, so it was over half
   the compressed download. `build-client.sh` now writes `pkg/sounds.pack`; the page fetches it
   once the pilot is in the arena and hands each file to the game (`provide_asset`). Sounds play
   from then on. `OSS_BUNDLE_SOUNDS=1` builds them in as before.
3. **Map, LVZ and news cached in IndexedDB.** On wasm, oss-session's `MapCache` keeps files in
   memory, filled from IndexedDB before start, and passes new downloads to
   `globalThis.dareCacheSave`. The zone's checksum still decides whether a cached copy is used.
4. **Gateway connection opened on Enter**, while the sign-in box and download finish. Packets
   the game sends before the link is open are queued (they used to be dropped, which cost a
   resend of the connect packet).
5. **The sign-in box ends the wait itself** instead of waiting for the dialog's `close` event,
   which Chrome can hold back until it next draws the page.

## What's left

- **First-visit map download (5 s)** is the zone's bandwidth limiter, not the browser:
  SubspaceServer's `BandwidthDefault` starts every connection at `Net:LimitInitial` = 5000 B/s and
  only grows it per ack. `[Net] LimitInitial = 40000` was added to the live `conf/global.conf` on
  2026-11-26 (backup: `/root/global.conf.pre-limitinitial`). It is read only when the module
  loads, so it takes effect at the next zone restart; then re-time a first visit, expected under
  a second for this map. It applies to every client, native ones included.
- **Pressing Enter the moment the page opens** still waits for the rest of the 2.75 MB. Further
  cuts: graphics (1.6 MB compressed) as a separate pack fetched in parallel; `opt-level = "s"` or
  `wasm-opt` on the code (check the game's frame times first); brotli or zstd-19 precompressed
  files (`file_server { precompressed }`) save about 10% more but need every deploy to upload the
  matching `.br`/`.zst`, or a stale one is served.
- **Caching:** `/play/` files are `Cache-Control: no-cache` with ETags, so return visits cost one
  304 round trip per file. Content-hashed names with `immutable` would remove those (~0.2 s).
- **Login** (0.6 s, three round trips through the gateway) is the zone's handshake; not touched.

## subspacereloaded.com, for comparison (read only, not copied)

Its game is 3.5 MB of WebAssembly (0.99 MB brotli) with graphics in a separate 1 MB pack (0.34 MB
on the wire) and sounds as a FLAC pack loaded later. Everything downloads when the page opens, from
content-hashed files cached `immutable` for a year, and the game runs a lobby behind the Play
button, so Play has nothing left to fetch. Page to game ready: about 2.6 s. Its server and
protocol are its own, so its map and login steps don't compare with a SubSpace zone's.
