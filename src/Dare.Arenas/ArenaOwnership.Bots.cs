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
    // A running bot is keyed by its saved settings, not its place in the arena's list, so adding
    // or removing one bot does not restart the others; each one's team (100 + its place) is kept apart.
    private readonly Dictionary<(string Arena, ArenaBot Bot, string Owner), BotRun> _bots = new();
    private readonly Dictionary<(string Arena, ArenaBot Bot, string Owner), int> _botSlots = new();
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
        var desired = new HashSet<(string Arena, ArenaBot Bot, string Owner)>();
        _botSlots.Clear();
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
                if (bot.Deployed && desired.Count < cap)
                {
                    desired.Add((owned.Name, bot, owned.Owner));
                    _botSlots[(owned.Name, bot, owned.Owner)] = slot++;
                }
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
                    player.Ship != ShipType.Spec && _botSlots.TryGetValue(key, out int slot) && player.Freq != 100 + slot &&
                    string.Equals(player.Arena?.Name, key.Arena, StringComparison.OrdinalIgnoreCase))
                {
                    _game.SetFreq(player, (short)(100 + slot));
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

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[create|set|list|deploy|stop|remove] [name] [behavior] [ship 1-8] [chat on|off]",
        Description = """
            Arena bots for the arena you own (you must be in it). Behaviors: fighter (chases and
            shoots pilots, roams when there are none), patrol (flies its waypoints, or roams),
            sentry (guards a spot), idle (sits still).
              ?bots create Sparring fighter 3   makes a bot and sends it in now (ship 3 = Spider)
              ?bots set Sparring patrol         changes its behavior or ship, keeps its waypoints
              ?bots set Sparring chat on        it answers when a pilot says its name
              ?bots list                        your bots and whether they are flying
              ?bots stop Sparring / ?bots deploy Sparring   take it out / send it back in
              ?bots remove Sparring             deletes it
            Waypoints, greetings and replies are set in the Arena Editor's Bots tab on the website.
            """)]
    private void Command_bots(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!player.Flags.Authenticated || player.Name is not { } owner || player.Arena is not { } arena ||
            Store.Get(arena.BaseName) is not { Status: ArenaStatus.Active } owned ||
            !string.Equals(owned.Owner, owner, StringComparison.OrdinalIgnoreCase))
        {
            Reply(player, "Bots: only the owner of this arena can manage its bots, from inside the arena.");
            return;
        }

        var (action, rest) = SplitFirst(parameters);
        var bots = Store.Bots(owned.Name);
        ArenaBot? Find(string label) => bots.FirstOrDefault(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase));
        bool NeedName(string label, string usage)
        {
            if (label.Length != 0) return true;
            Reply(player, "Bots: which bot? " + usage + (bots.Count == 0 ? "" : " Yours: " + string.Join(", ", bots.Select(b => b.Label)) + "."));
            return false;
        }
        bool Known(string label)
        {
            if (Find(label) is not null) return true;
            Reply(player, bots.Count == 0
                ? $"Bots: there is no bot called {label}. Make one with ?bots create {label} fighter"
                : $"Bots: there is no bot called {label}. Yours: {string.Join(", ", bots.Select(b => b.Label))}.");
            return false;
        }

        switch (action.ToLowerInvariant())
        {
            case "":
            case "help":
            case "?":
                BotHelp(player, bots.Count);
                break;

            case "list":
                ListBots(player, owned.Name);
                break;

            case "create":
            case "new":
            case "add":
            {
                var args = BotCommand.Parse(rest);
                if (!NeedName(args.Label, "Type ?bots create <name> [behavior] [ship 1-8], e.g. ?bots create Sparring fighter 3")) break;
                if (Find(args.Label) is { } taken)
                {
                    Reply(player, $"Bots: {taken.Label} already exists. Change it with ?bots set {taken.Label} <behavior> [ship 1-8].");
                    break;
                }
                var saved = SaveBot(owned.Name, owner, args.Label, BotCommand.Apply(null, args.Behaviour, args.Ship, args.Chat));
                if (!saved.Ok) { Reply(player, "Bots: " + saved.Message); break; }
                var deployed = Store.SetBotDeployment(owned.Name, owner, args.Label, true);
                var bot = Store.Bots(owned.Name).First(b => string.Equals(b.Label, args.Label, StringComparison.OrdinalIgnoreCase));
                Reply(player, $"Bots: created {bot.Label}{ArenaStore.BotSuffix} ({BotCommand.Describe(bot.Script)}). " +
                    (deployed.Ok ? JoinNote() : deployed.Message));
                if (!bot.Script.Contains("casual ", StringComparison.Ordinal))
                    Reply(player, $"Bots: it doesn't talk yet. ?bots set {bot.Label} chat on lets it answer when a pilot says its name.");
                if (!bot.Script.Contains("point ", StringComparison.Ordinal) && !bot.Script.Contains("behaviour idle", StringComparison.Ordinal) &&
                    !bot.Script.Contains("behaviour sentry", StringComparison.Ordinal))
                    Reply(player, "Bots: with no waypoints it roams the map. Set a route on the map in the Arena Editor's Bots tab.");
                break;
            }

            case "set":
            case "edit":
            case "save":
            {
                var args = BotCommand.Parse(rest);
                if (!NeedName(args.Label, "Type ?bots set <name> <behavior> [ship 1-8], e.g. ?bots set Sparring patrol")) break;
                var existing = Find(args.Label);
                if (existing is null && !action.Equals("save", StringComparison.OrdinalIgnoreCase))
                {
                    Known(args.Label);
                    break;
                }
                if (existing is not null && args.Behaviour is null && args.Ship is null && args.Chat is null)
                {
                    Reply(player, $"Bots: {existing.Label} is {BotCommand.Describe(existing.Script)}. " +
                        $"Give a behavior (fighter, patrol, sentry, idle), ship 1-8 or chat on/off, e.g. ?bots set {existing.Label} sentry ship 2");
                    break;
                }
                var result = SaveBot(owned.Name, owner, existing?.Label ?? args.Label,
                    BotCommand.Apply(existing?.Script, args.Behaviour, args.Ship, args.Chat));
                if (!result.Ok) { Reply(player, "Bots: " + result.Message); break; }
                var after = Store.Bots(owned.Name).First(b => string.Equals(b.Label, existing?.Label ?? args.Label, StringComparison.OrdinalIgnoreCase));
                Reply(player, $"Bots: {after.Label} is now {BotCommand.Describe(after.Script)}." +
                    (after.Deployed ? " It rejoins with the change in a few seconds." : $" It is stopped; ?bots deploy {after.Label} sends it in."));
                break;
            }

            case "deploy":
            case "start":
            case "go":
            case "stop":
            case "pause":
            {
                var label = BotCommand.Label(rest);
                bool deploy = action.ToLowerInvariant() is "deploy" or "start" or "go";
                if (!NeedName(label, deploy ? "Type ?bots deploy <name>." : "Type ?bots stop <name>.") || !Known(label)) break;
                var result = Store.SetBotDeployment(owned.Name, owner, label, deploy);
                Reply(player, "Bots: " + (result.Ok
                    ? deploy ? $"{Find(label)!.Label} deployed. " + JoinNote() : $"{Find(label)!.Label} stopped. It leaves within a few seconds; its settings are kept."
                    : result.Message));
                break;
            }

            case "remove":
            case "delete":
            {
                var label = BotCommand.Label(rest);
                if (!NeedName(label, "Type ?bots remove <name>.") || !Known(label)) break;
                var result = SaveBot(owned.Name, owner, Find(label)!.Label, null);
                Reply(player, "Bots: " + (result.Ok ? $"{Find(label)!.Label} removed." : result.Message));
                break;
            }

            default:
                Reply(player, $"Bots: I don't know \"{action}\".");
                BotHelp(player, bots.Count);
                break;
        }
    }

    private void BotHelp(Player player, int count)
    {
        Reply(player, "Bots: ?bots create <name> [fighter|patrol|sentry|idle] [ship 1-8] [chat on]   e.g. ?bots create Sparring fighter 3 chat on");
        Reply(player, "Bots: ?bots list | ?bots set <name> <behavior|ship N|chat on|off> | ?bots stop <name> | ?bots deploy <name> | ?bots remove <name>");
        Reply(player, "Bots: fighter chases and shoots pilots, patrol flies waypoints, sentry guards a spot, idle sits still. Without waypoints, fighter and patrol roam.");
        if (count == 0) Reply(player, "Bots: this arena has no bots yet.");
        if (BotsUnavailable() is { } why) Reply(player, "Bots: " + why);
    }

    private void ListBots(Player player, string arena)
    {
        var bots = Store.Bots(arena);
        if (bots.Count == 0)
        {
            Reply(player, "Bots: this arena has no bots yet. Make one with ?bots create Sparring fighter");
            return;
        }
        foreach (var bot in bots)
        {
            var flying = _playerData.FindPlayer(bot.Label + ArenaStore.BotSuffix) is { Status: PlayerState.Playing } p &&
                string.Equals(p.Arena?.Name, arena, StringComparison.OrdinalIgnoreCase);
            var state = !bot.Deployed ? "stopped" : flying ? "deployed, flying" : "deployed, joining";
            Reply(player, $"Bots: {bot.Label}{ArenaStore.BotSuffix}  {BotCommand.Describe(bot.Script)}  [{state}]");
        }
        if (BotsUnavailable() is { } why) Reply(player, "Bots: " + why);
    }

    /// <summary>Why deployed bots cannot fly in this zone right now, or null when they can.</summary>
    private string? BotsUnavailable()
    {
        if (string.IsNullOrWhiteSpace(_botProgram))
            return "this zone has no bot program installed, so bots are saved but cannot fly yet. Zone staff need to install oss-bot.";
        if (_configManager.GetInt(_configManager.Global, ConfigSection, "MaxBots", 30) <= 0)
            return "bots are switched off zone-wide (MaxBots = 0), so they are saved but cannot fly.";
        return null;
    }

    private string JoinNote() => BotsUnavailable() is { } why
        ? "But " + why
        : "It joins in a few seconds while a pilot is in the arena.";
}
