# Voice Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tribes-style voice menu: `V` + letters sends a spoken, named chat line to your team or the arena, server-checked and spam-limited, with clips generated locally by Chatterbox.

**Architecture:** One phrase table (`voice/phrases.json`, dare-zone) drives everything. The Dare.Arenas plugin handles `?v <code>`: looks the code up, applies a per-player limiter, then sends the phrase text as public or team chat *from the sender* with the phrase's sound id (128–255). The Dare client gets a generated Rust copy of the table for its menu, sends `?v <code>`, and plays `voice<id>.wav` for any received chat line carrying a voice id. Clips come from a committed Python tool in `voice/` (generate → audition/pick → export into the client).

**Tech Stack:** C# / .NET (SubspaceServer v4 plugin, xUnit), Rust (oss-client, cpal mixer, `cargo test`), Python 3.11 + Chatterbox + ffmpeg (clip tooling, pytest).

**Spec:** `docs/superpowers/specs/2026-11-25-voice-menu-design.md`

**Repos and branches:**
- `~/dare-zone`, branch `voice-menu` (already created from `main`; spec committed). Tasks 1–4, 8.
- `~/oss-src/oss-client`, new branch `voice-menu` from `dare/main` (`git switch -c voice-menu dare/main`; the local `dare` branch is stale and the name is ambiguous with the remote — always say `dare/main`). Tasks 5–7, 8's export.

## Global Constraints

- Voice sound ids are **128–255**, assigned once per phrase in tree order starting at 128, never reused.
- Scope per phrase: `global` → chat type Pub (2) to the whole arena; `team` → chat type Freq (3) to the sender's freq.
- Spam defaults: **3 voice lines per 6 s**; the 4th silences voice for **10 s**. Notice text, exact: `Voice silenced for 10 s — too many voice lines.` (number = configured silence).
- `arena.conf` `[Voice]`: `Enabled` (1), `Burst` (3), `Window` (6, seconds), `Silence` (10, seconds).
- Menu: `V` opens; leaf **sends and closes in the same frame**; `Backspace` up a level (closes at top); `Esc` closes; closes after **4 s** idle.
- Client setting `[chat] enter_to_chat`, default **yes**.
- Clips: mono, 16-bit PCM WAV, **11 025 Hz**, silence-trimmed, loudness-normalised; file name `voice<id>.wav` in the client's `assets/classic/sound/`.
- Voice: Chatterbox, reference `voice/ref/ralph_low.wav`, `cfg_weight` 0.3, exaggeration by delivery (urgent/hype 1.0, smug 0.9, calm 0.6); **monster** treatment (chain in Task 4).
- Never ship Tribes recordings. No voice audio on the website in v1.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A moderator-shut-up player** (chat mask restricts Pub/Freq) uses `?v` → must be refused like typed chat. Test lives in Task 3's manual check list and the `VoiceCommand` guard (Task 3 Step 3).
2. **Hand-typed `?v GW ` / `?v  gw`** (case, extra spaces) → same as `gw`; **`?v g`** (a menu, not a phrase) → "Unknown voice command". Pinned in Task 1 test `Lookup_normalises_and_rejects_menus`.
3. **Pressing `V` while the chat line is open** (enter-to-chat on) → types `v`, does not open the menu. Pinned in Task 6 test `v_types_when_chat_is_open`.
4. **Missing clip** (client older than the phrase table, or a Continuum-dir override without our files) → text still shows, no panic, one warning. Pinned in Task 7 test `unknown_voice_id_names_a_clip_and_never_panics` + existing `Audio::samples` caching of `None`.
5. **Voice disabled in an arena** (`Voice:Enabled=0`) → private `Voice is off in this arena.`, nothing sent, limiter untouched. Pinned in Task 3 Step 3 guard order and the Task 8 end-to-end list.

---

## dare-zone

### Task 1: Phrase table and its parser/validator

**Files:**
- Create: `voice/phrases.json`
- Create: `src/Dare.Arenas.Core/VoicePhrases.cs`
- Test: `tests/Dare.Arenas.Tests/VoicePhrasesTests.cs`
- Modify: `src/Dare.Arenas/Dare.Arenas.csproj` (embed `..\..\voice\phrases.json` as `EmbeddedResource`, `LogicalName` `voice.phrases.json`)
- Modify: `tests/Dare.Arenas.Tests/Dare.Arenas.Tests.csproj` (copy `..\..\voice\phrases.json` to output as `phrases.json`)

**Interfaces:**
- Produces:
  - `public enum VoiceScope { Global, Team }`
  - `public sealed record VoicePhrase(string Code, byte Sound, VoiceScope Scope, string Label, string Text, string Delivery);`
  - `public sealed record VoiceMenu(string Code, string Label);`
  - `public sealed class VoicePhrases { static VoicePhrases Parse(string json); IReadOnlyList<VoiceMenu> Menus; IReadOnlyList<VoicePhrase> Phrases; bool TryGet(string typed, out VoicePhrase phrase); }` — `Parse` throws `FormatException` naming the offending code when validation fails.

JSON shape (exact keys):

```json
{ "menus":   [ { "code": "g",  "label": "Global" }, { "code": "gc", "label": "Compliment" } ],
  "phrases": [ { "code": "gy", "sound": 128, "scope": "global", "label": "Yes", "text": "Yes!", "delivery": "calm" } ] }
```

Fill it from the spec's tree, in the spec's order, top-level menus `g a d f b n t w m s c p v` and the sub-menus `gc gr gt`. Sounds from 128 upward in that order. Label = the words shown in the menu; text = the words spoken and shown in chat (same unless the label is abbreviated). Delivery: `urgent` for warnings, needs, moves, target calls, attack/defend orders; `hype` for Woohoo, I have the flags, Got him, Go go go; `smug` for taunts; `calm` for the rest.

- [ ] **Step 1: Write the failing tests**

```csharp
static VoicePhrases Real() => VoicePhrases.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "phrases.json")));

[Fact] public void Real_table_parses_and_matches_spec_examples()
{
    var t = Real();
    Assert.True(t.TryGet("gw", out var w));  Assert.Equal(("Woohoo!", VoiceScope.Global), (w.Text, w.Scope));
    Assert.True(t.TryGet("tr", out var r));  Assert.Equal(("He's red!", VoiceScope.Team), (r.Text, r.Scope));
    Assert.True(t.TryGet("ml", out var l));  Assert.Equal("Break left!", l.Text);
    Assert.True(t.TryGet("gtb", out var b)); Assert.Equal("Is that the best you can do?", b.Text);
    Assert.All(t.Phrases, p => Assert.InRange(p.Sound, 128, 255));
    Assert.Equal(t.Phrases.Count, t.Phrases.Select(p => p.Sound).Distinct().Count());
    Assert.Equal(128, t.Phrases[0].Sound);
}

[Fact] public void Every_phrase_sits_under_a_menu_and_no_code_is_both()
{
    var t = Real();
    var menus = t.Menus.Select(m => m.Code).ToHashSet();
    Assert.All(t.Phrases, p => Assert.Contains(p.Code[..^1], menus));
    Assert.All(t.Menus.Where(m => m.Code.Length > 1), m => Assert.Contains(m.Code[..^1], menus));
    Assert.Empty(t.Phrases.Select(p => p.Code).Intersect(menus));
}

[Fact] public void Lookup_normalises_and_rejects_menus()
{
    var t = Real();
    Assert.True(t.TryGet("  GW ", out var p)); Assert.Equal("gw", p.Code);
    Assert.False(t.TryGet("g", out _));
    Assert.False(t.TryGet("zz", out _));
    Assert.False(t.TryGet("", out _));
}

[Theory]
[InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":127,"scope":"global","label":"Y","text":"Y","delivery":"calm"}]}""")] // id out of range
[InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":128,"scope":"both","label":"Y","text":"Y","delivery":"calm"}]}""")]  // bad scope
[InlineData("""{"menus":[],"phrases":[{"code":"gy","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"calm"}]}""")]                          // orphan
[InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"calm"},{"code":"gn","sound":128,"scope":"global","label":"N","text":"N","delivery":"calm"}]}""")] // duplicate id
public void Parse_rejects_bad_tables(string json) => Assert.Throws<FormatException>(() => VoicePhrases.Parse(json));
```

- [ ] **Step 2: Run to see them fail** — `dotnet test tests/Dare.Arenas.Tests --filter VoicePhrasesTests` → FAIL (type not defined).
- [ ] **Step 3: Write `voice/phrases.json` and implement `VoicePhrases`** with `System.Text.Json`; codes are lower-case `[a-z]+`; also reject duplicate codes, empty text, unknown delivery (`calm|urgent|hype|smug`).
- [ ] **Step 4: Run** — same command → PASS.
- [ ] **Step 5: Commit** — `git add voice/phrases.json src/Dare.Arenas.Core/VoicePhrases.cs tests/... src/Dare.Arenas/Dare.Arenas.csproj && git commit -m "voice: phrase table and validator"`

### Task 2: Voice limiter

**Files:**
- Create: `src/Dare.Arenas.Core/VoiceLimiter.cs`
- Test: `tests/Dare.Arenas.Tests/VoiceLimiterTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct VoiceLimits(int Burst, TimeSpan Window, TimeSpan Silence);` with `public static VoiceLimits Default => new(3, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10));`
  - `public enum VoiceVerdictKind { Allowed, SilencedNow, Silenced }`
  - `public readonly record struct VoiceVerdict(VoiceVerdictKind Kind, int SecondsLeft);`
  - `public sealed class VoiceLimiter { VoiceLimiter(Func<DateTimeOffset> clock); VoiceVerdict Try(string key, VoiceLimits limits); }` — thread-safe; drops keys with nothing in the window and no silence on each `Try` (as `RateLimiter` does), so departed players cost nothing; `SilencedNow` is returned only for the attempt that trips the silence (that's the one the plugin announces); attempts during silence return `Silenced` and don't extend it or count.

- [ ] **Step 1: Write the failing tests** (clock field like `RateLimiterTests`)

```csharp
[Fact] public void Three_in_six_seconds_then_the_fourth_silences_for_ten()
{
    var l = new VoiceLimiter(() => _now);
    for (var i = 0; i < 3; i++) Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
    Assert.Equal(new VoiceVerdict(VoiceVerdictKind.SilencedNow, 10), l.Try("a", VoiceLimits.Default));
    _now = _now.AddSeconds(4);
    Assert.Equal(new VoiceVerdict(VoiceVerdictKind.Silenced, 6), l.Try("a", VoiceLimits.Default));
    _now = _now.AddSeconds(6.1);
    Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
}
[Fact] public void Spaced_out_lines_never_silence() { /* 20 tries 2.1 s apart → all Allowed */ }
[Fact] public void Attempts_while_silenced_do_not_extend_it() { /* trip, then try every second; Allowed again at exactly 10 s after the trip */ }
[Fact] public void Players_are_independent_and_limits_are_per_call() { /* "a" tripped, "b" Allowed; Burst 1 trips on the 2nd */ }
[Fact] public void Seconds_left_rounds_up() { /* 9.2 s left → 10 */ }
```

- [ ] **Step 2: Run** — `dotnet test tests/Dare.Arenas.Tests --filter VoiceLimiterTests` → FAIL.
- [ ] **Step 3: Implement** — per key: queue of allowed times within `Window`, plus `silencedUntil`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `git commit -m "voice: per-player voice limiter with forced silence"`

### Task 3: `?v` command in the plugin

**Files:**
- Create: `src/Dare.Arenas/ArenaOwnership.Voice.cs` (partial of `ArenaOwnership`)
- Modify: `src/Dare.Arenas/Commands.cs` (`Add("v", Command_v);` under players)
- Modify: `src/Dare.Arenas/ArenaOwnership.cs` (load phrases and create the limiter in `Load`)
- Modify: `deploy/default-caps.txt` (add `cmd_v`)
- Modify: `deploy/arenas/_template/arena.conf` (commented `[Voice]` block with the four keys and defaults)
- Modify: `docs/arena-owners.md` (one short paragraph: what `[Voice]` does)

**Interfaces:**
- Consumes: `VoicePhrases.TryGet`, `VoiceLimiter.Try`, `VoiceLimits` (Tasks 1–2); `IChat.SendAnyMessage(HashSet<Player>, ChatMessageType, ChatSound, Player? from, string)`, `IChat.GetPlayerChatMask(Player)`, `IConfigManager.GetInt(arena.Cfg, "Voice", key, default)`, `IPlayerData.Lock/Players/Unlock`.
- Produces: in-game `?v <code>`; the exact notice strings below (the client parses the silence one).

- [ ] **Step 1: Load the table** — in `Load`, read the `voice.phrases.json` manifest resource and `VoicePhrases.Parse` it; on failure log Error and leave voice off (the module still loads). Create `_voiceLimiter = new VoiceLimiter(() => DateTimeOffset.UtcNow)`.
- [ ] **Step 2: `[CommandHelp]`** — Targets None, Args `<code>`, Description: "Says a voice-menu line (the Dare client's V menu sends these). Global lines go to the arena, team lines to your team."
- [ ] **Step 3: Implement `Command_v(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)`**, guards in this order, each replying privately and returning:
  1. not in an arena → nothing;
  2. table failed to load or `Voice:Enabled` is 0 → `Voice is off in this arena.`;
  3. chat mask restricts `Pub` (global) or `Freq` (team) → `You can't talk right now.`;
  4. unknown code → `Unknown voice command.`;
  5. limiter: `SilencedNow` → `Voice silenced for {Silence} s — too many voice lines.`; `Silenced` → nothing (silently dropped);
  6. send: recipients = players in the arena with `Status == Playing` (global) or additionally `Freq == player.Freq` (team), **including the sender**; `SendAnyMessage(set, Pub|Freq, (ChatSound)phrase.Sound, player, phrase.Text)`.
  Limits from `arena.Cfg` `[Voice]` `Burst`/`Window`/`Silence` (clamp Burst 1–20, Window 1–60, Silence 0–300).
- [ ] **Step 4: Build** — `dotnet build Dare.sln -c Release` → 0 errors, and `dotnet test` → all pass.
- [ ] **Step 5: Local check** with the local zone (`deploy/apply-local.sh` as for earlier features) and the existing Dare client typing commands by hand: `?v gw` shows `Name> Woohoo!` publicly; `?v tr` reaches only your freq; 4 quick `?v gw` → the notice, then silence for 10 s; `?v g` → unknown; arena with `Voice:Enabled=0` → off notice.
- [ ] **Step 6: Commit** — `git commit -m "voice: ?v command — table lookup, spam limit, team/arena routing with sound id"`

### Task 4: Clip tooling (`voice/`)

**Files:**
- Create: `voice/README.md` (setup + the three commands below; note the ~3 GB model download and 1 GB venv, both outside git)
- Create: `voice/requirements.txt` (`chatterbox-tts`, `setuptools<81` — Chatterbox's watermarker needs `pkg_resources`)
- Create: `voice/ref/ralph_low.wav` (copy from `spikes/voice/ref/ralph_low.wav`)
- Create: `voice/voicetool.py` (pure helpers), `voice/generate.py`, `voice/audition.py`, `voice/export.py`
- Test: `voice/test_voicetool.py`
- Modify: `.gitignore` (`voice/.venv/`, `voice/takes/`, `voice/picks.json` is **committed**, not ignored)

**Interfaces:**
- Consumes: `voice/phrases.json` (Task 1).
- Produces:
  - `voice/takes/<code>_<n>.wav` raw takes; `voice/picks.json` `{ "<code>": <n> }`.
  - `export.py --client ~/oss-src/oss-client` writes `<client>/assets/classic/sound/voice<id>.wav` for every picked phrase **and** `<client>/crates/oss-client/src/game/voice_table.rs` (generated; consumed by Task 5).
  - `voicetool.py`: `too_long(text: str, seconds: float) -> bool`, `exaggeration(delivery: str) -> float`, `MONSTER: str` (ffmpeg filter chain), `rust_table(phrases: dict) -> str`.

- [ ] **Step 1: Write failing tests**

```python
def test_too_long_scales_with_words():
    assert not too_long("Bombs!", 0.9)
    assert too_long("Bombs!", 2.5)
    assert not too_long("Is that the best you can do?", 1.6)
    assert too_long("Is that the best you can do?", 4.1)

def test_exaggeration_by_delivery():
    assert (exaggeration("urgent"), exaggeration("hype"), exaggeration("smug"), exaggeration("calm")) == (1.0, 1.0, 0.9, 0.6)

def test_rust_table_lists_menus_and_phrases():
    src = rust_table({"menus": [{"code": "g", "label": "Global"}],
                      "phrases": [{"code": "gw", "sound": 135, "scope": "global", "label": "Woohoo!", "text": "Woohoo!", "delivery": "hype"}]})
    assert 'VoiceMenuDef { code: "g", label: "Global" }' in src
    assert 'VoicePhraseDef { code: "gw", sound: 135, team: false, label: "Woohoo!" }' in src
    assert src.startswith("// Generated by dare-zone voice/export.py")
```

`too_long` rule: allowed seconds = `0.6 + 0.28 * words`, ×1.5 slack.
`MONSTER` (from the approved spike, applied to the 24 kHz raw take):
`asetrate=24000*0.72,aresample=24000,atempo=1.3889,asoftclip=type=tanh,aexciter=amount=2:drive=6,asoftclip=type=atan,aecho=0.8:0.6:18:0.25,highpass=f=250,lowpass=f=3600,acompressor=threshold=-18dB:ratio=4:attack=5:release=60,volume=1.6`
Export appends `silenceremove=start_periods=1:start_threshold=-45dB:stop_periods=1:stop_threshold=-45dB,loudnorm=I=-16:TP=-1.5` and writes `-ac 1 -ar 11025 -c:a pcm_s16le`.

- [ ] **Step 2: Run** — `cd voice && ../spikes/voice/.venv/bin/python -m pytest -q test_voicetool.py` (or the new venv) → FAIL.
- [ ] **Step 3: Implement `voicetool.py`**, then the scripts:
  - `generate.py [--takes 4] [--only gw,tr]` — loads Chatterbox once (`mps` if available, `PYTORCH_ENABLE_MPS_FALLBACK=1`), seeds per take, writes raw takes, regenerates a take (new seed, up to 3 tries) when `too_long`. `--only` is the "regenerate one phrase" command.
  - `audition.py` — serves on `127.0.0.1:28417` a page with, per phrase, each take rendered through `MONSTER` (cached as `takes/<code>_<n>_m.wav`), radio buttons, and Save → `POST /picks` writes `picks.json`. Pre-selects existing picks.
  - `export.py --client <path>` — refuses if any phrase lacks a pick (lists them); writes clips and `voice_table.rs`.
- [ ] **Step 4: Run tests** → PASS. Smoke: `generate.py --takes 2 --only gw,tr` then `audition.py` loads and saves.
- [ ] **Step 5: Commit** — `git commit -m "voice: Chatterbox clip tooling — generate, audition/pick, export"`

## oss-client

### Task 5: Voice menu state machine

**Files:**
- Create: `crates/oss-client/src/game/voice.rs`
- Create: `crates/oss-client/src/game/voice_table.rs` (hand-written placeholder with the same shape as the generated file and a few real entries — `g`, `gw`, `t`, `tr`, `m`, `ml`, `gt`, `gtb` with their real sound ids from `phrases.json` — replaced by Task 8's export)
- Modify: `crates/oss-client/src/game/mod.rs` (`pub mod voice; mod voice_table;`)

**Interfaces:**
- Produces:
  - `pub struct VoiceMenuDef { pub code: &'static str, pub label: &'static str }`, `pub struct VoicePhraseDef { pub code: &'static str, pub sound: u8, pub team: bool, pub label: &'static str }`, `pub static MENUS: &[VoiceMenuDef]`, `pub static PHRASES: &[VoicePhraseDef]` (in `voice_table.rs`).
  - `pub enum VoiceStep { Open, Moved, Send(&'static str), Closed, Ignored }`
  - `pub struct VoiceMenu` with `pub fn new() -> Self`, `pub fn is_open(&self) -> bool`, `pub fn press(&mut self, c: char, now: f32) -> VoiceStep` (handles `v` to open when closed), `pub fn back(&mut self, now: f32) -> VoiceStep`, `pub fn close(&mut self)`, `pub fn tick(&mut self, now: f32)` (closes after 4.0 s idle), `pub fn entries(&self) -> Vec<(char, &'static str, bool /*is submenu*/)>` for the current level, `pub fn path(&self) -> &str`.
  - `pub fn clip_name(sound: u8) -> Option<String>` — `128..=255` → `voice<id>`, `1..=26` → `bong<id>`, else `None`.

- [ ] **Step 1: Failing tests** (in `voice.rs` `#[cfg(test)]`)

```rust
#[test] fn v_g_w_sends_woohoo_and_closes_at_once() {
    let mut m = VoiceMenu::new();
    assert_eq!(m.press('v', 0.0), VoiceStep::Open);
    assert_eq!(m.press('g', 0.1), VoiceStep::Moved);
    assert_eq!(m.press('w', 0.2), VoiceStep::Send("gw"));
    assert!(!m.is_open());
}
#[test] fn letters_are_case_insensitive_and_unknown_ones_ignored() { /* 'V','T','x' → Ignored, still at "t"; 'R' → Send("tr") */ }
#[test] fn backspace_goes_up_then_closes() { /* v g t → path "gt"; back → "g"; back → ""; back → Closed */ }
#[test] fn idle_four_seconds_closes() { /* open at 0.0; tick(3.9) open; tick(4.0) closed; a press at 3.0 resets the timer */ }
#[test] fn entries_list_the_current_level() { /* after v: contains ('g', "Global", true) and ('t', "Target", true); after v t: ('r', "He's red!", false) */ }
#[test] fn clip_names() { assert_eq!(clip_name(135).as_deref(), Some("voice135")); assert_eq!(clip_name(3).as_deref(), Some("bong3")); assert_eq!(clip_name(0), None); assert_eq!(clip_name(100), None); }
```

- [ ] **Step 2: Run** — `cargo test -p oss-client voice::` → FAIL.
- [ ] **Step 3: Implement** — current path string; a key is valid if `path+key` is a menu code or phrase code.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `git commit -m "voice: V-menu state machine and phrase table"`

### Task 6: Enter-to-chat and menu input wiring

**Files:**
- Modify: `crates/oss-client/src/config.rs` (`[chat] enter_to_chat = yes|no`, field `enter_to_chat: bool` default `true`, written in the saved config like `[audio]`)
- Modify: `crates/oss-client/src/game/chat.rs` (`ChatLog` gains `pub open: bool`; `open()`, `cancel()`; `submit()` also closes)
- Modify: `crates/oss-client/src/game/sandbox.rs` (input loop ~lines 1056–1083, `set_typing` ~749)
- Modify: `crates/oss-client/src/game/settings_ui.rs` (a toggle "Enter to chat" next to the other chat options, if that screen lists chat options; else Options' general list)

**Interfaces:**
- Consumes: `VoiceMenu`, `VoiceStep` (Task 5).
- Produces: `Sandbox::voice: VoiceMenu`; `Sandbox::send_voice(&mut self, code: &str)` = `send_chat_line(&format!("?v {code}"))`.

Behaviour when `enter_to_chat` is on (spec): closed chat line ignores character strokes; first Enter opens it (empty line visible with cursor), typing goes in, Enter sends and closes, Esc cancels and closes; Enter on an empty open line just closes. While the line is closed, a character stroke goes to `voice.press`; `Send(code)` → `send_voice(code)`. `Backspace` → `voice.back`, `Esc` → `voice.close` when the menu is open (Esc must not also open the game menu that frame). `input.set_typing(self.menu || self.chat.open || self.voice.is_open())` so letter-bound game keys yield while the menu is open; arrows/Ctrl/Tab keep flying. Call `voice.tick(self.clock)` each frame. When off: today's behaviour untouched, and the menu is never opened by letters.

- [ ] **Step 1: Failing tests**

```rust
// config.rs
#[test] fn enter_to_chat_defaults_on_and_round_trips() { /* default true; parse "[chat]\nenter_to_chat = no\n" → false; save then parse → same */ }
// chat.rs
#[test] fn enter_opens_and_submit_closes() { /* open(); type "hi"; submit() == "hi"; !open */ }
#[test] fn cancel_clears_and_closes() { }
// sandbox.rs (existing test helpers drive strokes into a Sandbox without a net)
#[test] fn v_types_when_chat_is_open() { /* enter_to_chat on; Enter, 'v' → chat.input == "v", voice closed */ }
#[test] fn v_opens_voice_menu_when_chat_is_closed() { /* 'v' → voice.is_open(), chat.input empty */ }
#[test] fn legacy_mode_types_letters_straight_into_chat() { /* enter_to_chat off; 'v' → chat.input == "v", voice closed */ }
```

If `sandbox.rs` has no stroke-driving test helper, put the decision in a pure function `fn route_char(enter_to_chat: bool, chat_open: bool, voice_open: bool) -> CharRoute { Chat, Voice, Drop }` in `voice.rs` and test that instead; the sandbox calls it.

- [ ] **Step 2: Run** — `cargo test -p oss-client` → new tests FAIL.
- [ ] **Step 3: Implement** as described.
- [ ] **Step 4: Run** → PASS; `cargo clippy -p oss-client -- -D warnings` clean.
- [ ] **Step 5: Commit** — `git commit -m "chat: Enter to chat (default on); letters drive the V menu"`

### Task 7: Hearing voice lines, panel and silence countdown

**Files:**
- Modify: `crates/oss-client/src/game/sandbox.rs` (`SessionEvent::Chat` arm ~1555: keep `sound`; draw the panel next to the chat log ~2741)
- Modify: `crates/oss-client/src/game/voice.rs` (`pub fn silence_from_notice(text: &str) -> Option<u32>`; `VoiceMenu::silenced_until: f32`)
- Modify: `assets/classic/README.md` (one line: `voice*.wav` are ours, generated for the Dare voice menu, not Continuum's)

**Interfaces:**
- Consumes: `clip_name` (Task 5); `Sandbox::sound(name, gain, None)`.

Received chat with `sound != 0`: `if let Some(n) = clip_name(sound) { self.sound(&n, 1.0, None) }`, then the line as usual (no extra marking in v1). Arena message matching `silence_from_notice` (`^Voice silenced for (\d+) s`) sets `voice.silenced_until = clock + n`. Panel: while open, a box just above the chat log, one row per entry `KEY  Label` (submenus end in `▸`), header = path in caps (`V G`), and while silenced a yellow row `Silenced — Ns`. Font/colours from the existing chat drawing.

- [ ] **Step 1: Failing tests**

```rust
#[test] fn silence_notice_parses() { assert_eq!(silence_from_notice("Voice silenced for 10 s — too many voice lines."), Some(10)); assert_eq!(silence_from_notice("Voice is off in this arena."), None); }
#[test] fn unknown_voice_id_names_a_clip_and_never_panics() { for id in 0..=255u8 { let _ = clip_name(id); } assert_eq!(clip_name(254).as_deref(), Some("voice254")); }
```

- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS; play the sandbox with a hand-copied `voice135.wav` from the spike to hear it once.
- [ ] **Step 5: Commit** — `git commit -m "voice: play voice and classic chat sounds; V-menu panel; silence countdown"`

## Both repos

### Task 8: Real clips, export, end-to-end

- [ ] **Step 1: Generate** — `cd ~/dare-zone/voice && PYTORCH_ENABLE_MPS_FALLBACK=1 .venv/bin/python generate.py --takes 4` (≈74 phrases × 4; tens of minutes on the M5).
- [ ] **Step 2: Chris auditions** — `audition.py`, open `http://localhost:28417`, pick one take per phrase, Save. Re-run `generate.py --only <codes>` for any phrase with no good take. **This step waits on Chris.**
- [ ] **Step 3: Export** — `export.py --client ~/oss-src/oss-client`; check ~74 files of ~10–40 KB each, total ≲ 3 MB, and `cargo test -p oss-client` still passes against the generated `voice_table.rs`.
- [ ] **Step 4: Commit both** — dare-zone: `voice/picks.json` ("voice: picked takes"); oss-client: clips + `voice_table.rs` ("voice: clips (ralph, monster) and generated table").
- [ ] **Step 5: End-to-end, local zone, two Dare clients on different freqs**:
  - V G W → both hear Woohoo and see `Name> Woohoo!`; V T R → only the sender's freq hears/sees it;
  - menu closes the same frame as the send; Backspace/Esc/4 s idle behave;
  - 4 lines in quick succession → notice, countdown on the panel, nothing sent for 10 s;
  - Enter opens chat, `v` types, Enter sends; `enter_to_chat = no` restores the old typing;
  - `?shutup`-ed player can't use voice; `Voice:Enabled=0` arena says it's off;
  - the browser build (same assets dir) plays a clip, if the web build on its branch bundles `assets/classic/sound` — note the result either way.
  - Continuum: if a Continuum client is available, check that a voice id plays nothing; if not available, record it as unverified in the PR.
- [ ] **Step 6:** Note in `docs/roadmap.md` under a new "Voice menu" item what shipped and what's later (voice packs).
