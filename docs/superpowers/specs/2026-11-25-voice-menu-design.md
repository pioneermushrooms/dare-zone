# Voice menu (VGS-style quick voice commands) — design

Date: 2026-11-25. Repos: `~/dare-zone` (server plugin, phrase table, voice tooling) and
`~/oss-src/oss-client` (Dare client, desktop + browser build).

## Why

Mid-fight you can't type. Chris wants a Tribes 2 style voice menu: tap **V**, then two or three
letters, and your team hears a spoken call ("He's red!", "Break left!", "Bombs!") and sees it in
chat with your name. Mostly **team coordination**; **banter** (Woohoo!, taunts, good game) matters
too. The voices must sound like Tribes: **aggressive, manly, monstrous** — not polite TTS.

Success: a pilot chasing someone fires `V T R` without looking away, and every teammate hears
"He's red!" and reads `Chris> He's red!` within a frame or two of normal chat.

## Goals

- Key-sequence menu in the Dare client: `V` opens it, letters walk the tree, the leaf sends.
- Each phrase has a fixed scope: **global** (whole arena) or **team** (sender's freq only).
- Everyone who receives the line hears the clip and sees the text with the sender's name.
- Server-enforced spam limit with a short forced silence.
- **Enter starts chat**, so bare letters are free for the menu.
- ~90 phrases, one voice ("ralph", monster treatment), clips generated locally with Chatterbox.

## Non-goals (v1)

- Multiple voice packs / per-player voice choice (design leaves room; see "Later").
- Character animations (Tribes had them; SubSpace ships don't animate).
- Playing voice audio on the website's chat panel (text shows there as normal chat).
- Custom per-arena phrase sets.

## Anti-goals

- Voice spam ruining an arena — the limit must hold even against a modified client.
- Forgeable voice lines (a client making someone "say" arbitrary text with a voice clip).
- Soft, friendly, assistant-sounding voices.
- Breaking Continuum players: they must still see readable text and hear nothing wrong.

## Constraints and facts found

- SubSpace chat packets (C2S 0x06, S2C 0x07) already carry a **sound byte**. SubspaceServer's
  `IChat` can send a message with a `ChatSound`. Continuum maps 1–26 to its built-in bong sounds
  and 100–104 to music controls. The Dare client bundles the bongs but **ignores** the sound byte
  on received chat today.
- The Dare client has **no chat mode**: a key that types a character goes to the chat line, and
  game bindings yield to the chat line once it has text (`game/input.rs`, `yields_to_chat`).
- SubspaceServer chat has its own flood control (`FloodLimit`/`FloodShutup`); voice gets a
  separate, stricter limit in our plugin.
- The client decodes `.wav`/`.wa2` (`oss-assets`), mono, resampled at load. Same code in the
  browser (wasm) build.
- The Tribes recordings are owned by their publishers; we ship only our own generated clips.

## Approaches considered

1. **Plain chat line + sound byte from the client.** Simplest, but the server can't easily
   rate-limit it and a modified client can attach any clip to any text. Rejected.
2. **Hidden server command (chosen — Claude's call, Chris handed over the how).** Client sends
   `?v <code>`; the Dare.Arenas plugin looks the code up in its phrase table, checks the spam limit,
   and sends the phrase text with the phrase's sound id to the arena or the sender's team as a chat
   line from the sender. Unforgeable, limit can't be dodged, Continuum players get text.
3. **Client-only text matching.** Clients match plain text to clips. Fragile, Dare-to-Dare only.
   Rejected.

Voice generator: Chatterbox (local, MIT, free) picked over ElevenLabs/OpenAI (paid, keys). Spike
on 2026-11-25 in `spikes/voice`: default voice rejected as too soft; cloning a pitched-down macOS
"Ralph" reference with exaggeration 1.0, then the **monster** treatment, was approved by Chris.

## Decided design

### The menu (client)

- `V` opens the menu when the chat line is closed. A small panel beside the chat log lists the
  current level as `KEY  Label` lines, Tribes-style. Letters walk down. Pressing a leaf sends the
  line and **closes the menu immediately** — same frame, no confirmation, back to flying; the next
  voice line starts again from `V`. (Chris, 2026-11-25.)
- `Backspace` goes up a level; `Esc` closes; the panel closes itself after 4 s idle (Claude's call).
- While the menu is open, its letters don't drive game bindings; flying keys (arrows, Ctrl, Tab,
  etc.) keep working.
- The menu shows whether you're currently voice-silenced, with the seconds left (Claude's call).

### Phrase tree (approved 2026-11-25; scope G = everyone, T = team)

```
V  G Global (G)   Y Yes  N No  H Hi  B Bye  O Oops  Q Quiet!  W Woohoo!
                  C Compliment ▸ A Awesome!  S Great shot!  N Nice move!  G Good game.
                  R Respond    ▸ A Anytime.  T Thanks!  W Wait.  D Don't know.
                  T Taunt      ▸ A Awww…  B Is that the best you can do?  W When will you learn?
   A Attack (T)   A Attack!  F Get their flags!  B Hit their base!  C Chase him!  G Go go go!
   D Defend (T)   B Defend the base!  F Defend our flags!  C Cover the carrier!  M Cover me!
   F Flag (T)     F I have the flags!  T Take the flags!  E They have our flags!  S Flags secure.
   B Base (T)     S Base secure.  E Enemy in the base!  C Base is clear.  R Retake the base!
   N Need (T)     H Help!  C Need cover!  E Need an escort!  A Attach to me!  T Can I attach?
   T Target (T)   A Target acquired.  M Fire on my target!  R He's red!  W He warped!  K Got him!
   W Warn (T)     E Enemies incoming!  B Bombs!  C Cloaker!  M Mines!  I Behind you!
   M Move (T)     L Break left!  R Break right!  F Fall back!  P Push!  S Spread out!  G Group up!
   S Self (T)     O On it.  A I'm attacking.  D I'm defending.  R I'm red, backing off!
   C Command (T)  A Acknowledged.  D Declined.  C Done.
   P Ball (T)     P Pass!  S Shoot!  I I'm open!  D Defend the goal!
   V Very quick (T) Y Yes  N No  H Help!  S Sorry  T Thanks  W Wait
```

Codes are the letters after V, lower-case (`gw`, `gtb`, `tr`, `vy`). Spoken text is the label as
written. Very-quick lines are team-only versions of common globals (Tribes did the same).

### One phrase table

`voice/phrases.json` in dare-zone is the single source: `code`, `sound` (id), `scope`
(`global`/`team`), `label`, `text`, `delivery` (calm/urgent/hype/smug — sets generation
intensity). The plugin embeds it; the generator reads it; the client gets a copy alongside the
clips (`assets/voice/phrases.json`). Drift only affects which clip plays, never what text is shown.

### Sound ids

Voice ids live at **128–255** (one byte; clear of Continuum's 1–26 and 100–104). Assigned once
per phrase, never reused. Builder must check what Continuum does with an unknown id (expected:
silence). If it plays anything, the plugin sends sound 0 to non-Dare clients.

### Server (Dare.Arenas plugin)

- New command `?v <code>` (any player in an arena; specs included — Claude's call, Tribes let
  observers talk). Unknown code → private "Unknown voice command".
- Global phrase → public chat to the arena from the sender with the sound id. Team phrase → team
  chat to the sender's freq with the sound id. Uses existing chat types so every client,
  Continuum included, shows `Name> text` in the right colour.
- Spam limit (Claude's call, configurable): at most **3 voice lines per 6 s**; the 4th silences
  voice for **10 s**, told privately: "Voice silenced for 10 s — too many voice lines." Voice
  attempts during the silence don't extend it and are dropped without a second notice; the
  client's menu shows the countdown instead. Normal typed chat isn't affected.
- `arena.conf` `[Voice]`: `Enabled` (default 1), `Burst` (3), `Window` (6 s), `Silence` (10 s).
- The command line is never echoed to other players.

### Client (oss-client)

- **Enter to chat**: new setting `[chat] enter_to_chat`, default **yes**. On: letters do nothing
  until Enter opens the chat line; Enter sends; Esc cancels. Off: today's Continuum behaviour,
  and the voice menu is reached only by a rebindable key (default none). (Claude's call: a
  setting, because veterans and `?command` users have years of muscle memory.)
- Sending: the leaf sends `?v <code>` as a public chat command.
- Receiving: a chat line whose sound byte is 128–255 plays `assets/voice/<id>.wav` (non-positional,
  through the normal mixer and volume) and is drawn like any chat line. While at it, play the
  classic 1–26 bongs too, as Continuum does (small, same code path).
- Muting: the existing audio mute/volume applies. A separate "voice volume" is left to the builder.

### Clips

- Voice: Chatterbox, reference `spikes/voice/ref/ralph_low.wav` (macOS "Ralph", pitched to 0.82),
  `exaggeration` from `delivery` (urgent/hype 1.0, smug 0.9, calm 0.6 — tune on audition),
  `cfg_weight` 0.3.
- Treatment: **monster** — pitch ×0.72, tanh soft-clip + exciter, atan soft-clip, short echo,
  radio band-pass and compression (exact chain in `spikes/voice/gen2.py`).
- Format: mono 16-bit WAV at 11 025 Hz (radio band tops out at 3.6 kHz, so nothing is lost);
  ~22 KB/s, ~2 MB for all ~90. Trim leading/trailing silence; normalise loudness across clips.
- Pipeline: `voice/` tooling in dare-zone (committed: script, reference, phrases.json; not the
  venv/model). Generates N takes per phrase, auto-rejects takes far longer than the text warrants
  (Chatterbox rambles at high exaggeration), writes an audition page where Chris picks one take per
  phrase, then exports the picks into the client's `assets/voice/`.
- Chris auditions and picks; regenerating a single phrase must be one command.

### Website

The home page chat panel (ChatBridge) shows voice lines as ordinary chat text. No audio there in v1.

## Left to the builder

- Exact panel layout, colours and font (match the existing chat/HUD look).
- How the client marks a voice line in the log, if at all.
- Whether to add a separate voice-volume setting.
- Which existing default bindings, if any, use bare letters and need moving with enter-to-chat on.
- Test strategy per repo (plugin: spam limiter + table lookup unit tests; client: menu state
  machine and enter-to-chat unit tests; one end-to-end check in a local zone with two clients).

## Later

- Voice packs: a per-player voice setting stored with the pilot's account; the plugin would pick
  the id range per voice (e.g. 128–255 split, or a second byte in the text). Bioderm-style
  "monster" and a cleaner "ralph radio" are the obvious first two.
- Ball-arena and flag-arena phrase sets toggled by arena type.
