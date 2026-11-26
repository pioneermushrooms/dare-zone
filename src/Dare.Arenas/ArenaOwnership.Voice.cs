using System.Reflection;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// The voice menu (Tribes-style quick voice lines). The Dare client's V menu sends ?v <code>; the
// line goes out as the sender's public (global phrases) or team (team phrases) chat carrying the
// phrase's sound id (128-255), so every client shows "Name> text" and Dare clients play the clip.
// Spec: docs/superpowers/specs/2026-11-25-voice-menu-design.md
public sealed partial class ArenaOwnership
{
    private VoicePhrases? _voicePhrases;
    private VoiceLimiter? _voiceLimiter;

    private void LoadVoice()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("voice.phrases.json")
                ?? throw new FormatException("voice.phrases.json is not embedded");
            using var reader = new StreamReader(stream);
            _voicePhrases = VoicePhrases.Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Voice menu off: {ex.Message}");
            _voicePhrases = null;
        }
        _voiceLimiter = new VoiceLimiter(() => DateTimeOffset.UtcNow);
    }

    [ConfigHelp<bool>("Voice", "Enabled", ConfigScope.Arena, Default = true,
        Description = "Whether players can use the voice menu (?v) in this arena.")]
    [ConfigHelp<int>("Voice", "Burst", ConfigScope.Arena, Default = 3, Min = 1, Max = 20,
        Description = "Voice lines a player may send within Voice:Window seconds before being silenced.")]
    [ConfigHelp<int>("Voice", "Window", ConfigScope.Arena, Default = 6, Min = 1, Max = 60,
        Description = "Seconds over which Voice:Burst is counted.")]
    [ConfigHelp<int>("Voice", "Silence", ConfigScope.Arena, Default = 10, Min = 0, Max = 300,
        Description = "Seconds a player's voice lines are dropped after going over the limit.")]
    private VoiceLimits ArenaVoiceLimits(Arena arena) => arena.Cfg is not { } cfg
        ? VoiceLimits.Default
        : new VoiceLimits(
            Math.Clamp(_configManager.GetInt(cfg, "Voice", "Burst", 3), 1, 20),
            TimeSpan.FromSeconds(Math.Clamp(_configManager.GetInt(cfg, "Voice", "Window", 6), 1, 60)),
            TimeSpan.FromSeconds(Math.Clamp(_configManager.GetInt(cfg, "Voice", "Silence", 10), 0, 300)));

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<code>",
        Description = """
            Says a voice-menu line (the Dare client's V menu sends these). Global lines go to the
            arena, team lines to your team. Too many in a row silences your voice lines for a bit.
            """)]
    private void Command_v(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is not { } arena || player.Name is null) return;

        if (_voicePhrases is null || _voiceLimiter is null
            || (arena.Cfg is { } cfg && _configManager.GetInt(cfg, "Voice", "Enabled", 1) == 0))
        {
            Reply(player, "Voice is off in this arena.");
            return;
        }

        if (!_voicePhrases.TryGet(parameters.ToString(), out var phrase))
        {
            Reply(player, "Unknown voice command.");
            return;
        }

        var type = phrase.Scope == VoiceScope.Global ? ChatMessageType.Pub : ChatMessageType.Freq;
        if (_chat.GetPlayerChatMask(player).IsRestricted(type))
        {
            Reply(player, "You can't talk right now.");
            return;
        }

        var limits = ArenaVoiceLimits(arena);
        var verdict = _voiceLimiter.Try(player.Name, limits);
        if (verdict.Kind == VoiceVerdictKind.SilencedNow)
        {
            Reply(player, $"Voice silenced for {(int)limits.Silence.TotalSeconds} s - too many voice lines.");
            return;
        }
        if (verdict.Kind == VoiceVerdictKind.Silenced) return;

        var recipients = new HashSet<Player>();
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
                if (p.Status == PlayerState.Playing && p.Arena == arena
                    && (phrase.Scope == VoiceScope.Global || p.Freq == player.Freq))
                    recipients.Add(p);
        }
        finally { _playerData.Unlock(); }

        _chat.SendAnyMessage(recipients, type, (ChatSound)phrase.Sound, player, phrase.Text);
        // The website's chat panel listens to chat callbacks; this line didn't come through Chat.
        Callback_Chat(arena, player, type, (ChatSound)phrase.Sound, null, player.Freq, phrase.Text);
    }
}
