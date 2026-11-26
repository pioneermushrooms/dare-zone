using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

internal static class BotPasses
{
    private static readonly ConcurrentDictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);

    internal static string Issue(string name)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        Pending[name] = password;
        return password;
    }

    internal static bool Redeem(string name, string password) =>
        Pending.TryRemove(new KeyValuePair<string, string>(name, password));

    internal static void Revoke(string name) => Pending.TryRemove(name, out _);
}

public sealed partial class ArenaOwnership
{
    private sealed class BotRun
    {
        public Process? Process;
        public DateTimeOffset NextStart;
        public DateTimeOffset StopDeadline;
        public int Backoff = 5;
        public Task<string?>? ChatReply;
        public DateTimeOffset NextChat;
        public string? ChatPilot;
        public string? ChatMessage;
        public string? ChatContext;
        public string? TypedReply;
        public DateTimeOffset ReplyAt;
        public DateTimeOffset ChatStarted;
        public Task<string[]>? Exchange;
        public string? ExchangePartner;
        public string? ExchangeFirst;
        public string? ExchangeFollowup;
        public DateTimeOffset FollowupAt;
    }

    private readonly Dictionary<string, DateTimeOffset> _botOccupancy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Arena, ArenaBot Bot, int Slot, string Owner), BotRun> _bots = new();
    private const string BotChatSecretsPath = "conf/bot-chat-secrets.conf";
    private BotBanter? _botBanter;
    private readonly Queue<DateTimeOffset> _botChatRequests = new();
    private DateTimeOffset _nextBotExchange;
    private string? _botProgram;
    private string _botServer = "";

    internal static bool IsBot(string name) => name.EndsWith(ArenaStore.BotSuffix, StringComparison.OrdinalIgnoreCase);

    internal Result SaveBot(string arena, string owner, string label, string? script)
    {
        if (script is not null && _accounts?.Find(label.Trim()) is not null)
            return new(false, "That label belongs to a pilot account.");
        return Store.SaveBot(arena, owner, label, script);
    }

    private void StartBots()
    {
        var bundled = Path.Combine(Path.GetDirectoryName(typeof(ArenaOwnership).Assembly.Location)!, "oss-bot");
        _botProgram = _configManager.GetStr(_configManager.Global, ConfigSection, "BotProgram")
            ?? (File.Exists(bundled) ? bundled : null);
        if (string.IsNullOrWhiteSpace(_botProgram)) return;
        var port = _configManager.GetInt(_configManager.Global, "Listen", "Port", 5000);
        _botServer = _configManager.GetStr(_configManager.Global, ConfigSection, "BotServer") ?? $"127.0.0.1:{port}";
        try
        {
            if (File.Exists(BotChatSecretsPath))
            {
                var config = File.ReadAllText(BotChatSecretsPath);
                var key = SecretsFile.Get(config, "OpenAI", "ApiKey");
                var model = SecretsFile.Get(config, "OpenAI", "Model") ?? "gpt-4.1-mini";
                if (!string.IsNullOrWhiteSpace(key))
                    _botBanter = new BotBanter(key, model, message => Log(LogLevel.Warn, message));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Warn, $"Could not read casual bot chat configuration ({ex.GetType().Name}).");
        }
        _mainloopTimer.SetTimer(TickBots, 1000, 1000, null);
        Log(LogLevel.Info, "Arena bot supervisor enabled.");
    }

    private void StopBots()
    {
        _mainloopTimer.ClearTimer(TickBots, null);
        foreach (var entry in _bots)
            FinishBot(entry.Key.Bot.Label + ArenaStore.BotSuffix, entry.Value, force: true);
        _bots.Clear();
        _botOccupancy.Clear();
        _botBanter?.Dispose();
        _botBanter = null;
    }

    private void AddressBot(Arena arena, Player sender, ReadOnlySpan<char> message)
    {
        if (_botBanter is null || sender.Name is not { } pilot || IsBot(pilot)) return;
        var text = message.Trim();
        if (text.IsEmpty || text.Length > 240) return;
        if (text.StartsWith("@ai ", StringComparison.OrdinalIgnoreCase)) text = text[4..].TrimStart();
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, run) in _bots)
        {
            if (run.Process is not { HasExited: false } || run.StopDeadline != default ||
                !string.Equals(key.Arena, arena.Name, StringComparison.OrdinalIgnoreCase) ||
                !MentionsBot(text, key.Bot.Label) ||
                run.ChatReply is not null || run.TypedReply is not null || run.NextChat > now) continue;
            var casual = key.Bot.Script.Split('\n').Select(line => line.Trim()).FirstOrDefault(
                line => line.StartsWith("casual ", StringComparison.Ordinal));
            if (casual is null) return;
            while (_botChatRequests.TryPeek(out var at) && now - at >= TimeSpan.FromHours(1))
                _botChatRequests.Dequeue();
            if (_botChatRequests.Count >= 12) return;
            var fields = casual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var previous = string.Equals(run.ChatPilot, pilot, StringComparison.OrdinalIgnoreCase) &&
                now < run.NextChat.AddMinutes(3) ? run.ChatContext : null;
            _botChatRequests.Enqueue(now);
            int[] cooldown = [60, 45, 30, 20, 12];
            run.ChatStarted = now;
            run.NextChat = now.AddSeconds(cooldown[int.Parse(fields[1]) - 1]);
            run.ChatPilot = pilot;
            run.ChatMessage = text.ToString();
            run.ChatReply = _botBanter.Reply(fields[2], run.ChatMessage, previous);
            return;
        }
    }

    private static bool MentionsBot(ReadOnlySpan<char> text, string label)
    {
        for (int start = 0; start <= text.Length - label.Length; start++)
        {
            int end = start + label.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1])) &&
                (end == text.Length || !char.IsLetterOrDigit(text[end])) &&
                text[start..end].Equals(label, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? ChatProfile(ArenaBot bot) => bot.Script.Split('\n')
        .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("casual ", StringComparison.Ordinal))
        ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];

    private void StartBotExchange(DateTimeOffset now)
    {
        if (_botBanter is null || now < _nextBotExchange) return;
        var ready = _bots.Where(entry => entry.Value.Process is { HasExited: false } &&
            entry.Value.StopDeadline == default &&
            ChatProfile(entry.Key.Bot) is not null &&
            entry.Key.Bot.Script.Split('\n').Any(line => line.Trim() == "botchat on") &&
            _botOccupancy.TryGetValue(entry.Key.Arena, out var seen) && now - seen < TimeSpan.FromSeconds(3) &&
            _playerData.FindPlayer(entry.Key.Bot.Label + ArenaStore.BotSuffix) is { Status: PlayerState.Playing })
            .GroupBy(entry => entry.Key.Arena, StringComparer.OrdinalIgnoreCase);
        foreach (var arena in ready)
        {
            var pair = arena.Take(2).ToArray();
            if (pair.Length < 2 || ChatProfile(pair[0].Key.Bot) is not { } first ||
                ChatProfile(pair[1].Key.Bot) is not { } second) continue;
            _nextBotExchange = now.AddMinutes(20);
            pair[0].Value.ExchangePartner = pair[1].Key.Bot.Label + ArenaStore.BotSuffix;
            pair[0].Value.Exchange = _botBanter.Exchange(first, second);
            break;
        }
    }

    private void SendBotExchange(string name, BotRun run, DateTimeOffset now)
    {
        if (run.ExchangePartner is not { } partner ||
            _playerData.FindPlayer(name) is not { Status: PlayerState.Playing, Arena: { } arena } bot ||
            _playerData.FindPlayer(partner) is not { Status: PlayerState.Playing } other || other.Arena != arena)
        {
            run.Exchange = null;
            run.ExchangeFirst = null;
            run.ExchangeFollowup = null;
            return;
        }
        if (run.Exchange is { IsCompleted: true } task)
        {
            run.Exchange = null;
            if (task.IsCompletedSuccessfully && task.Result is [var first, var second])
            {
                run.ExchangeFirst = first;
                run.ExchangeFollowup = second;
                run.FollowupAt = now + TypingDelay(first);
            }
        }
        if (run.FollowupAt > now) return;
        if (run.ExchangeFirst is { } opening)
        {
            run.ExchangeFirst = null;
            SendBotLine(arena, bot, opening);
            run.FollowupAt = now + TypingDelay(run.ExchangeFollowup!);
            return;
        }
        if (run.ExchangeFollowup is { } line)
        {
            run.ExchangeFollowup = null;
            SendBotLine(arena, other, line);
        }
    }

    private static TimeSpan TypingDelay(string text) =>
        TimeSpan.FromMilliseconds(Math.Clamp(300 + text.Length * 18 + Random.Shared.Next(60, 241), 500, 2600));

    private void SendBotReply(string name, BotRun run, DateTimeOffset now)
    {
        if (run.ChatReply is { IsCompleted: true } task)
        {
            run.ChatReply = null;
            run.ChatContext = null;
            if (task.IsCompletedSuccessfully && task.Result is { } response)
            {
                run.TypedReply = response;
                run.ReplyAt = run.ChatStarted + TypingDelay(response);
            }
        }
        if (run.TypedReply is not { } line || run.ReplyAt > now) return;
        run.TypedReply = null;
        if (_playerData.FindPlayer(name) is not { Status: PlayerState.Playing, Arena: { } arena } bot ||
            run.ChatPilot is null || _playerData.FindPlayer(run.ChatPilot)?.Arena != arena) return;
        SendBotLine(arena, bot, line);
        run.ChatContext = $"Earlier message: {run.ChatMessage}\nYour reply: {line}";
    }

    private void SendBotLine(Arena arena, Player bot, string line)
    {
        var recipients = new HashSet<Player>();
        _playerData.Lock();
        try
        {
            foreach (var player in _playerData.Players)
                if (player.Status == PlayerState.Playing && player.Arena == arena) recipients.Add(player);
        }
        finally { _playerData.Unlock(); }
        _chat.SendAnyMessage(recipients, ChatMessageType.Pub, default, bot, line);
        Callback_Chat(arena, bot, ChatMessageType.Pub, default, null, -1, line);
    }

    private bool TickBots()
    {
        try { ReconcileBots(); StartBotExchange(DateTimeOffset.UtcNow); }
        catch (Exception ex) { Log(LogLevel.Error, $"Bot supervisor: {ex.Message}"); }
        return true;
    }

    private void ReconcileBots()
    {
        var now = DateTimeOffset.UtcNow;
        _playerData.Lock();
        try
        {
            foreach (var player in _playerData.Players)
                if (player.Status == PlayerState.Playing && player.Type is ClientType.VIE or ClientType.Continuum &&
                    player.Arena is { } arena &&
                    player.Name is { } name && !IsBot(name) &&
                    string.Equals(arena.Name, arena.BaseName, StringComparison.OrdinalIgnoreCase))
                    _botOccupancy[arena.Name] = now;
        }
        finally { _playerData.Unlock(); }

        int cap = Math.Clamp(_configManager.GetInt(_configManager.Global, ConfigSection, "MaxBots", 30), 0, 100);
        var desired = new HashSet<(string Arena, ArenaBot Bot, int Slot, string Owner)>();
        foreach (var (arena, lastSeen) in _botOccupancy.ToArray())
        {
            if (now - lastSeen > TimeSpan.FromMinutes(1))
            {
                _botOccupancy.Remove(arena);
                continue;
            }
            if (Store.Get(arena) is not { Status: ArenaStatus.Active } owned) continue;
            int slot = 0;
            foreach (var bot in Store.Bots(owned.Name))
                if (bot.Deployed && desired.Count < cap) desired.Add((owned.Name, bot, slot++, owned.Owner));
        }

        foreach (var (key, run) in _bots.ToArray())
        {
            var name = key.Bot.Label + ArenaStore.BotSuffix;
            if (desired.Contains(key) && run.StopDeadline == default)
            {
                if (run.Process?.HasExited == true)
                {
                    FinishBot(name, run, force: false);
                    DelayRestart(run, now);
                }
                else if (run.Process is not null && _playerData.FindPlayer(name) is { Status: PlayerState.Playing } player &&
                    player.Ship != ShipType.Spec && player.Freq != 100 + key.Slot &&
                    string.Equals(player.Arena?.Name, key.Arena, StringComparison.OrdinalIgnoreCase))
                {
                    _game.SetFreq(player, (short)(100 + key.Slot));
                }
                SendBotReply(name, run, now);
                SendBotExchange(name, run, now);
                continue;
            }

            if (run.Process is { HasExited: false } process && run.StopDeadline == default)
            {
                BotPasses.Revoke(name);
                run.StopDeadline = now.AddSeconds(3);
                process.StandardInput.Close();
                continue;
            }
            if (run.Process is { HasExited: false } && now < run.StopDeadline) continue;
            FinishBot(name, run, force: true);
            _bots.Remove(key);
        }

        foreach (var key in desired)
        {
            if (!_bots.TryGetValue(key, out var run)) _bots[key] = run = new();
            var name = key.Bot.Label + ArenaStore.BotSuffix;
            if (run.Process is not null || run.NextStart > now ||
                _bots.Any(entry => entry.Value.Process is not null && string.Equals(
                    entry.Key.Bot.Label, key.Bot.Label, StringComparison.OrdinalIgnoreCase)) ||
                _bots.Values.Count(r => r.Process is not null) >= cap || _playerData.FindPlayer(name) is not null)
                continue;
            try
            {
                var script = BotLaunchScript(key.Bot.Script);
                var start = new ProcessStartInfo(_botProgram!) { UseShellExecute = false, RedirectStandardInput = true };
                foreach (var arg in new[] { "--server", _botServer, "--script", "-", "--map-cache", Path.GetFullPath("data/bot-maps") }) start.ArgumentList.Add(arg);
                var pass = BotPasses.Issue(name);
                run.Process = Process.Start(start) ?? throw new InvalidOperationException("Bot process did not start.");
                run.Process.StandardInput.WriteLine($"name {name}\npassword {pass}\narena {key.Arena}\nship 1\npeers [bot]\ncontroller {key.Owner}\n{script}\nend");
                run.Process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                FinishBot(name, run, force: true);
                DelayRestart(run, now);
                Log(LogLevel.Warn, $"Could not launch bot '{name}': {ex.Message}");
            }
        }
    }

    private static string BotLaunchScript(string script)
    {
        var lines = script.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToList();
        lines.RemoveAll(line => line == "botchat on" || line.StartsWith("announce ", StringComparison.Ordinal));
        var casual = lines.FindIndex(line => line.StartsWith("casual ", StringComparison.Ordinal));
        if (casual < 0) return string.Join('\n', lines);

        var fields = lines[casual].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lines.RemoveAt(casual);
        var greeting = fields[2] switch
        {
            "dry" => "Oh, hello {name}.",
            "competitive" => "You're up, {name}.",
            _ => "Hey {name}, good to see you!"
        };
        if (!lines.Any(line => line.StartsWith("greet ", StringComparison.Ordinal)))
            lines.Add("greet " + greeting);
        return string.Join('\n', lines);
    }

    private static void DelayRestart(BotRun run, DateTimeOffset now)
    {
        run.NextStart = now.AddSeconds(run.Backoff);
        run.Backoff = Math.Min(run.Backoff * 2, 300);
    }

    private void FinishBot(string name, BotRun run, bool force)
    {
        BotPasses.Revoke(name);
        if (run.Process is not { } process) return;
        try
        {
            if (force && !process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log(LogLevel.Warn, $"Could not stop bot '{name}': {ex.Message}");
        }
        finally
        {
            process.Dispose();
            run.Process = null;
        }
    }

    private void Command_bots(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.Flags.Authenticated || player.Name is not { } owner || player.Arena is not { } arena ||
            Store.Get(arena.BaseName) is not { Status: ArenaStatus.Active } owned ||
            !string.Equals(owned.Owner, owner, StringComparison.OrdinalIgnoreCase))
        {
            Reply(player, "Only the active arena's owner can manage bots.");
            return;
        }

        var (action, rest) = SplitFirst(parameters);
        switch (action.ToLowerInvariant())
        {
            case "":
            case "list":
                var bots = Store.Bots(owned.Name);
                if (bots.Count == 0) Reply(player, "No bots configured.");
                foreach (var bot in bots)
                    Reply(player, bot.Label + ArenaStore.BotSuffix + (bot.Deployed ? " (deployed)" : " (stopped)"));
                break;
            case "save":
                var (mode, label) = SplitFirst(rest);
                var existing = Store.Bots(owned.Name).FirstOrDefault(b =>
                    string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase));
                var settings = (existing?.Script ?? "").Split('\n')
                    .Where(line => !line.TrimStart().StartsWith("behaviour ", StringComparison.Ordinal));
                var script = string.Join('\n', settings.Prepend($"behaviour {mode.ToLowerInvariant()}")).TrimEnd();
                Reply(player, SaveBot(owned.Name, owner, label, script).Message);
                break;
            case "deploy":
            case "stop":
                Reply(player, Store.SetBotDeployment(owned.Name, owner, rest, action.Equals("deploy", StringComparison.OrdinalIgnoreCase)).Message);
                break;
            case "remove":
                Reply(player, SaveBot(owned.Name, owner, rest, null).Message);
                break;
            default:
                Reply(player, "?bots list | ?bots save <mode> <label> | ?bots deploy <label> | ?bots stop <label> | ?bots remove <label>");
                break;
        }
    }
}
