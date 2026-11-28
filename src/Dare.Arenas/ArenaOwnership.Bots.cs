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

    internal static bool IsPending(string name, string password) =>
        Pending.TryGetValue(name, out var pending) && pending == password;

    internal static BotPassBook NewBook() => new(Issue, IsPending, Revoke);

    // Players who logged in on a bot pass. This, not the name, makes a player a bot: a pilot
    // account that holds a registered bot's name logs in as a pilot and stays one.
    private static readonly ConcurrentDictionary<Player, byte> Sessions = new();

    internal static void MarkSession(Player player) => Sessions[player] = 0;

    internal static void ForgetSession(Player player) => Sessions.TryRemove(player, out _);

    internal static bool IsSession(Player player) => Sessions.ContainsKey(player);

    /// <summary>Drops sessions of players no longer in the zone.</summary>
    internal static void Prune(IReadOnlySet<Player> online)
    {
        foreach (var player in Sessions.Keys)
            if (!online.Contains(player)) Sessions.TryRemove(player, out _);
    }
}

public sealed partial class ArenaOwnership
{
    /// <summary>One arena's bot host: a single <c>oss-bot --host</c> process fed lineup messages on its standard input.</summary>
    private sealed class HostRun
    {
        public Process? Process;
        public DateTimeOffset NextStart;
        public DateTimeOffset StopDeadline;
        public int Backoff = 5;
        public BotSeats Seats = new(() => DateTimeOffset.UtcNow, Random.Shared);
        public BotStagger Stagger = new(() => DateTimeOffset.UtcNow, Random.Shared);
        /// <summary>The lineup last written; null after a (re)start so the next one is sent.</summary>
        public string? LastLineup;
        public string Owner = "";
        public IReadOnlyList<ArenaBot> Bots = [];
        /// <summary>What the lineup shows each bot now (Out = absent).</summary>
        public IReadOnlyList<BotPlacement> Shown = [];
        /// <summary>Each listed bot's one-time pass, kept (so the text stays the same) until it is replaced.</summary>
        public readonly BotPassBook Passes = BotPasses.NewBook();
    }

    /// <summary>A bot's casual chat in progress (replies to pilots, exchanges with another bot).</summary>
    private sealed class BotChat
    {
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
    private readonly Dictionary<string, HostRun> _hosts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bots already logged as left out because the host would refuse them (label and reason).</summary>
    private readonly HashSet<string> _hostRefusalsLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BotChat> _botChats = new(StringComparer.OrdinalIgnoreCase);
    private const string BotChatSecretsPath = "conf/bot-chat-secrets.conf";
    private BotBanter? _botBanter;
    private readonly Queue<DateTimeOffset> _botChatRequests = new();
    private DateTimeOffset _nextBotExchange;
    private string? _botProgram;
    private string _botServer = "";

    /// <summary>The player logged in on a bot's one-time pass.</summary>
    internal static bool IsBot(Player player) => BotPasses.IsSession(player);

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
        foreach (var (arena, run) in _hosts)
            FinishHost(arena, run, force: true);
        _hosts.Clear();
        _botChats.Clear();
        _botOccupancy.Clear();
        _botBanter?.Dispose();
        _botBanter = null;
    }

    /// <summary>A bot of a host that is playing in <paramref name="arena"/> right now.</summary>
    private Player? RunningBot(string arena, string name) =>
        _playerData.FindPlayer(name) is { Status: PlayerState.Playing } player &&
        string.Equals(player.Arena?.Name, arena, StringComparison.OrdinalIgnoreCase) ? player : null;

    /// <summary>The bots a host lists (not Out), with their saved settings.</summary>
    private static IEnumerable<ArenaBot> Listed(HostRun run) => run.Bots.Where(bot =>
        run.Shown.Any(p => p.Seat != BotSeat.Out && string.Equals(p.Label, bot.Label, StringComparison.OrdinalIgnoreCase)));

    private BotChat ChatOf(string name)
    {
        if (!_botChats.TryGetValue(name, out var chat)) _botChats[name] = chat = new();
        return chat;
    }

    private void AddressBot(Arena arena, Player sender, ReadOnlySpan<char> message)
    {
        if (_botBanter is null || sender.Name is not { } pilot || IsBot(sender)) return;
        if (!_hosts.TryGetValue(arena.Name, out var host) || host.StopDeadline != default || host.Process is not { HasExited: false }) return;
        var text = message.Trim();
        if (text.IsEmpty || text.Length > 240) return;
        if (text.StartsWith("@ai ", StringComparison.OrdinalIgnoreCase)) text = text[4..].TrimStart();
        var now = DateTimeOffset.UtcNow;
        foreach (var bot in Listed(host))
        {
            if (!MentionsBot(text, bot.Label) || RunningBot(arena.Name, bot.Label) is null) continue;
            var run = ChatOf(bot.Label);
            if (run.ChatReply is not null || run.TypedReply is not null || run.NextChat > now) continue;
            var casual = bot.Script.Split('\n').Select(line => line.Trim()).FirstOrDefault(
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
        foreach (var (arena, host) in _hosts)
        {
            if (host.StopDeadline != default || host.Process is not { HasExited: false } ||
                !_botOccupancy.TryGetValue(arena, out var seen) || now - seen >= TimeSpan.FromSeconds(3)) continue;
            var pair = Listed(host).Where(bot => ChatProfile(bot) is not null &&
                    bot.Script.Split('\n').Any(line => line.Trim() == "botchat on") &&
                    RunningBot(arena, bot.Label) is not null)
                .Take(2).ToArray();
            if (pair.Length < 2 || ChatProfile(pair[0]) is not { } first || ChatProfile(pair[1]) is not { } second) continue;
            _nextBotExchange = now.AddMinutes(20);
            var chat = ChatOf(pair[0].Label);
            chat.ExchangePartner = pair[1].Label;
            chat.Exchange = _botBanter.Exchange(first, second);
            break;
        }
    }

    private void SendBotExchange(string name, BotChat run, DateTimeOffset now)
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

    private void SendBotReply(string name, BotChat run, DateTimeOffset now)
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
        // Bots are the players on a bot pass; real people (not bots, not fake players) keep arenas occupied.
        var entries = new List<CensusEntry>();
        var online = new HashSet<Player>();
        _playerData.Lock();
        try
        {
            foreach (var player in _playerData.Players)
            {
                online.Add(player);
                if (player.Name is not { } name) continue;
                entries.Add(new(name, player.Arena?.Name,
                    player.Arena is { } a && string.Equals(a.Name, a.BaseName, StringComparison.OrdinalIgnoreCase),
                    player.Status == PlayerState.Playing, player.Ship != ShipType.Spec, player.Freq,
                    IsBot(player), player.Type == ClientType.Fake));
            }
        }
        finally { _playerData.Unlock(); }
        BotPasses.Prune(online);
        var census = ZoneCensus.Take(entries);
        foreach (var arena in census.Occupied) _botOccupancy[arena] = now;
        var flying = census.Flying;
        var botsInZone = census.BotsInZone;

        int cap = Math.Clamp(_configManager.GetInt(_configManager.Global, ConfigSection, "MaxBots", 30), 0, 100);
        int total = 0;
        var desired = new Dictionary<string, (string Owner, IReadOnlyList<ArenaBot> Bots)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (arena, lastSeen) in _botOccupancy.ToArray())
        {
            if (now - lastSeen > TimeSpan.FromMinutes(1))
            {
                _botOccupancy.Remove(arena);
                continue;
            }
            if (Store.Get(arena) is not { Status: ArenaStatus.Active } owned) continue;
            var bots = Store.LaunchableBots(owned.Name, n => _accounts?.Find(n) is not null, (bot, why) =>
            {
                if (_hostRefusalsLogged.Add(bot.Label + "\n" + why))
                    Log(LogLevel.Warn, $"Bot [{bot.Label}] in [{owned.Name}] not launched: {why}.");
            }).Take(cap - total).ToList();
            if (bots.Count == 0) continue;
            total += bots.Count;
            desired[owned.Name] = (owned.Owner, bots);
        }

        foreach (var (arena, run) in _hosts.ToArray())
        {
            if (desired.ContainsKey(arena) && run.StopDeadline == default) continue;
            // Closing standard input sends the host's bots home; it gets a few seconds before it is killed.
            if (run.Process is { HasExited: false } process && run.StopDeadline == default)
            {
                RevokePasses(run);
                run.StopDeadline = now.AddSeconds(3);
                try { process.StandardInput.Close(); } catch (IOException) { }
                continue;
            }
            if (run.Process is { HasExited: false } && now < run.StopDeadline) continue;
            FinishHost(arena, run, force: true);
            _hosts.Remove(arena);
        }

        foreach (var (arena, (owner, bots)) in desired)
        {
            if (!_hosts.TryGetValue(arena, out var run))
                _hosts[arena] = run = new HostRun();
            if (run.StopDeadline != default) continue; // still stopping; started again once it has gone
            run.Owner = owner;
            run.Bots = bots;

            if (run.Process?.HasExited == true)
            {
                Log(LogLevel.Warn, $"Bot host for [{arena}] exited ({run.Process.ExitCode}); restarting in {run.Backoff} s.");
                FinishHost(arena, run, force: false);
                DelayRestart(run, now);
            }
            if (run.Process is null && run.NextStart <= now) StartHost(arena, run, now);

            // A bot still in the zone from before (an old host's session) counts as out until it has left;
            // then it comes in like any arriving bot, after its own short wait.
            var target = BotDirector.Plan(bots, Store.MatchSizes(arena),
                    flying.TryGetValue(arena, out var freqs) ? freqs : new Dictionary<int, int>())
                .Select(p => botsInZone.Contains(p.Label) && !run.Passes.Holds(p.Label) ? p with { Seat = BotSeat.Out } : p)
                .ToList();
            run.Shown = run.Stagger.Apply(run.Seats.Step(target));
            if (run.Process is not { } host) continue;

            var lineup = new List<(ArenaBot Bot, BotPlacement Placement, string Password)>();
            foreach (var placement in run.Shown)
            {
                if (placement.Seat == BotSeat.Out) continue;
                // null: an old session still in the zone, not listed until it has left
                if (run.Passes.PassFor(placement.Label, botsInZone.Contains(placement.Label), now) is not { } pass) continue;
                lineup.Add((bots.First(b => b.Label == placement.Label), placement, pass));
            }
            run.Passes.KeepOnly(lineup.Select(e => e.Bot.Label).ToList());

            var text = BotLineup.Render(arena, owner, lineup);
            if (text != run.LastLineup)
            {
                try
                {
                    host.StandardInput.Write(text);
                    host.StandardInput.Flush();
                    run.LastLineup = text;
                }
                catch (IOException ex)
                {
                    Log(LogLevel.Warn, $"Could not send the lineup to the bot host for [{arena}]: {ex.Message}");
                }
            }

            foreach (var (bot, placement, _) in lineup)
            {
                if (placement.Seat == BotSeat.In && _playerData.FindPlayer(bot.Label) is { } player && IsBot(player) &&
                    player.Status == PlayerState.Playing && player.Ship != ShipType.Spec && player.Freq != placement.Team &&
                    string.Equals(player.Arena?.Name, arena, StringComparison.OrdinalIgnoreCase))
                    _game.SetFreq(player, (short)placement.Team);
            }
        }

        // casual chat, for bots that are listed
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in _hosts.Values)
            foreach (var bot in Listed(run))
                listed.Add(bot.Label);
        foreach (var name in _botChats.Keys.Where(n => !listed.Contains(n)).ToList()) _botChats.Remove(name);
        foreach (var (name, chat) in _botChats)
        {
            SendBotReply(name, chat, now);
            SendBotExchange(name, chat, now);
        }
    }

    private void StartHost(string arena, HostRun run, DateTimeOffset now)
    {
        try
        {
            var start = new ProcessStartInfo(_botProgram!) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (var arg in new[] { "--host", "--server", _botServer, "--map-cache", Path.GetFullPath("data/bot-maps") })
                start.ArgumentList.Add(arg);
            run.Process = Process.Start(start) ?? throw new InvalidOperationException("Bot host did not start.");
            run.LastLineup = null;
            // a new host's bots all arrive from outside, each after its own wait
            run.Seats = new BotSeats(() => DateTimeOffset.UtcNow, Random.Shared);
            run.Stagger = new BotStagger(() => DateTimeOffset.UtcNow, Random.Shared);
            Log(LogLevel.Info, $"Bot host for [{arena}] started (pid {run.Process.Id}).");
        }
        catch (Exception ex)
        {
            FinishHost(arena, run, force: true);
            DelayRestart(run, now);
            Log(LogLevel.Warn, $"Could not start the bot host for [{arena}]: {ex.Message}");
        }
    }

    private static void DelayRestart(HostRun run, DateTimeOffset now)
    {
        run.NextStart = now.AddSeconds(run.Backoff);
        run.Backoff = Math.Min(run.Backoff * 2, 300);
    }

    private static void RevokePasses(HostRun run)
    {
        run.Passes.Clear();
    }

    private void FinishHost(string arena, HostRun run, bool force)
    {
        // A restarted host's bots log in again on new passes, once their old sessions have left the zone.
        RevokePasses(run);
        run.LastLineup = null;
        if (run.Process is not { } process) return;
        try
        {
            if (force && !process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log(LogLevel.Warn, $"Could not stop the bot host for [{arena}]: {ex.Message}");
        }
        finally
        {
            process.Dispose();
            run.Process = null;
        }
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[create|set|list|match|deploy|stop|remove] [name] [behavior] [ship 1-8] [team N] [priority 1-9] [chat on|off]",
        Description = """
            Anyone: ?bots tells you which pilots in this arena are bots.
            Owners manage the bots of the arena they own (they must be in it). Behaviors: fighter (chases and
            shoots pilots, roams when there are none), patrol (flies its waypoints, or roams),
            sentry (guards a spot), idle (sits still).
              ?bots create Sparring fighter 3   makes a bot and sends it in now (ship 3 = Spider)
              ?bots set Sparring patrol         changes its behavior or ship, keeps its waypoints
              ?bots set Sparring chat on        it answers when a pilot says its name
              ?bots set Sparring team 2         its team (frequency 0-9999)
              ?bots set Sparring priority 8     1-9: higher priority stays in when room is short
              ?bots match 2 4                   field 4 bots on team 2 (?bots match 2 off removes it)
              ?bots list                        your bots, their teams and whether they are flying
              ?bots stop Sparring / ?bots deploy Sparring   take it out / send it back in
              ?bots remove Sparring             deletes it
            Waypoints, greetings and replies are set in the Arena Editor's Bots tab on the website.
            """)]
    private void Command_bots(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var (action, rest) = SplitFirst(parameters);
        bool isOwner = player.Flags.Authenticated && player.Name is not null && player.Arena is { } here &&
            Store.Get(here.BaseName) is { Status: ArenaStatus.Active } mine &&
            Store.SameAccount(mine.Owner, player.Name);
        if (!isOwner)
        {
            SayWhoIsBot(player);
            return;
        }
        var owner = player.Name!;
        var owned = Store.Get(player.Arena!.BaseName)!;
        if (action.Length == 0 || action is "?")
            SayWhoIsBot(player);

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

            case "match":
            {
                if (rest.Trim().Length == 0)
                {
                    var sizes = Store.MatchSizes(owned.Name);
                    Reply(player, sizes.Count == 0
                        ? "Bots: no match sizes set. ?bots match <team> <size> fields that many bots on a team."
                        : "Bots: match sizes: " + string.Join(", ", sizes.Select(kv => $"team {kv.Key}: {kv.Value}")) + ". ?bots match <team> off removes one.");
                    break;
                }
                if (BotCommand.ParseMatch(rest) is not { } match)
                {
                    Reply(player, "Bots: type ?bots match <team 0-9999> <size>, or ?bots match <team> off.");
                    break;
                }
                var result = Store.SetMatchSize(owned.Name, owner, match.Freq, match.Size);
                Reply(player, "Bots: " + (!result.Ok ? result.Message
                    : match.Size is { } n ? $"team {match.Freq} now fields {n} bot{(n == 1 ? "" : "s")}."
                    : $"team {match.Freq} has no match size now."));
                break;
            }

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
                var saved = SaveBot(owned.Name, owner, args.Label, BotCommand.Apply(null, args.Behaviour, args.Ship, args.Chat, args.Team, args.Priority));
                if (!saved.Ok) { Reply(player, "Bots: " + saved.Message); break; }
                var deployed = Store.SetBotDeployment(owned.Name, owner, args.Label, true);
                var bot = Store.Bots(owned.Name).First(b => string.Equals(b.Label, args.Label, StringComparison.OrdinalIgnoreCase));
                Reply(player, $"Bots: created {bot.Label} ({BotCommand.Describe(bot.Script)}). " +
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
                if (existing is not null && args.Behaviour is null && args.Ship is null && args.Chat is null && args.Team is null && args.Priority is null)
                {
                    Reply(player, $"Bots: {existing.Label} is {BotCommand.Describe(existing.Script)}. " +
                        $"Give a behavior (fighter, patrol, sentry, idle), ship 1-8, team N, priority 1-9 or chat on/off, e.g. ?bots set {existing.Label} sentry ship 2");
                    break;
                }
                var result = SaveBot(owned.Name, owner, existing?.Label ?? args.Label,
                    BotCommand.Apply(existing?.Script, args.Behaviour, args.Ship, args.Chat, args.Team, args.Priority));
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
        Reply(player, "Bots: ?bots list | ?bots set <name> <behavior|ship N|team N|priority 1-9|chat on|off> | ?bots stop <name> | ?bots deploy <name> | ?bots remove <name>");
        Reply(player, "Bots: ?bots match <team> <size> | ?bots match <team> off | ?bots match. Bots on a team make room for pilots (higher priority stays longest).");
        Reply(player, "Bots: fighter chases and shoots pilots, patrol flies waypoints, sentry guards a spot, idle sits still. Without waypoints, fighter and patrol roam.");
        if (count == 0) Reply(player, "Bots: this arena has no bots yet.");
        if (BotsUnavailable() is { } why) Reply(player, "Bots: " + why);
    }

    private void SayWhoIsBot(Player player)
    {
        var names = new List<string>();
        if (player.Arena is { } arena)
        {
            _playerData.Lock();
            try
            {
                foreach (var p in _playerData.Players)
                    if (p.Arena == arena && p.Name is { } n && IsBot(p)) names.Add(n);
            }
            finally { _playerData.Unlock(); }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        Reply(player, names.Count == 0 ? "No bots here." : "Bots here: " + string.Join(", ", names));
    }

    /// <summary>Where the match director has a bot now (in, spectating, out), or null when no host shows it.</summary>
    private BotSeat? SeatOf(string arena, string label) =>
        _hosts.TryGetValue(arena, out var run) && run.Shown.FirstOrDefault(p =>
            string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase)) is { } placement ? placement.Seat : null;

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
            var state = !bot.Deployed ? "stopped"
                : (Store.BotProblem(arena, bot.Label, n => _accounts?.Find(n) is not null) is { } problem ? BotCommand.NotLaunched(problem) : null)
                    ?? SeatOf(arena, bot.Label) switch
                    {
                        BotSeat.In => "in",
                        BotSeat.Spectating => "spectating",
                        _ => "out",
                    };
            Reply(player, "Bots: " + BotCommand.ListLine(bot.Label, bot.Script, state));
        }
        var sizes = Store.MatchSizes(arena);
        foreach (var (team, size) in sizes.OrderBy(kv => kv.Key))
        {
            int flying = 0;
            _playerData.Lock();
            try
            {
                foreach (var p in _playerData.Players)
                    if (p.Status == PlayerState.Playing && p.Ship != ShipType.Spec && p.Freq == team &&
                        string.Equals(p.Arena?.Name, arena, StringComparison.OrdinalIgnoreCase) && p.Type != ClientType.Fake && !IsBot(p)) flying++;
            }
            finally { _playerData.Unlock(); }
            Reply(player, $"Bots: team {team}: size {size}, {flying} player{(flying == 1 ? "" : "s")} flying");
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
