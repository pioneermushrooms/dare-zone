using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace Dare.Arenas;

/// <summary>
/// Game login against Foundation accounts (<see cref="AccountStore"/>), replacing the server's
/// <c>AuthFile</c> module. Its own module (not part of <see cref="ArenaOwnership"/>) because
/// <c>AuthBan</c> wraps whichever <see cref="IAuth"/> is registered when AuthBan loads, so this
/// line must sit in Modules.config where AuthFile was, before AuthBan.
/// <para>
/// On load, imports conf/passwd.conf <c>[Users]</c> once (the store keeps a marker). Logins are
/// checked on the thread pool (PBKDF2 is slow) and answered on the mainloop. Only a right password
/// for an active account is let in, authenticated, with the account's stored name casing.
/// </para>
/// <para>
/// Guests: the name <c>guest</c> (any password) flies unauthenticated under a fresh quirky space
/// name each time (<see cref="GuestNames"/>, e.g. "Wobbly Nebula"), one nobody online or on file
/// has. An unauthenticated player can't own arenas or take roles.
/// </para>
/// </summary>
[ModuleInfo("Foundation accounts: game login (IAuth) from the account store; imports passwd.conf [Users] once.")]
public sealed class FoundationAuth : IModule, IAuth
{
    private const string LogName = "Dare.Accounts";

    private readonly IMainloop _mainloop;
    private readonly ILogManager _logManager;
    private readonly IPlayerData _playerData;
#if DEBUG
    private readonly ICommandManager _commandManager;
    private readonly IChat _chat;
#endif

    private AccountStore? _store;
    private InterfaceRegistrationToken<IAuth>? _authToken;

    // Players whose login is being checked. A disconnect drops the entry: the server pools
    // IAuthRequest objects, so a late answer for a gone player must not touch the request.
    private readonly object _pendingLock = new();
    private readonly Dictionary<Player, object> _pending = new();

    public FoundationAuth(
        IMainloop mainloop,
        ILogManager logManager,
        IPlayerData playerData
#if DEBUG
        , ICommandManager commandManager,
        IChat chat
#endif
        )
    {
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
#if DEBUG
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
#endif
    }

    bool IModule.Load(IComponentBroker broker)
    {
        try
        {
            var dir = Path.GetDirectoryName(ArenaOwnership.DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _store = new AccountStore(ArenaOwnership.DbPath, () => DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open account store '{ArenaOwnership.DbPath}': {ex.Message}");
            return false;
        }

        ImportLegacyUsers(_store);

        NewPlayerCallback.Register(broker, Callback_NewPlayer);
        _authToken = broker.RegisterInterface<IAuth>(this);
#if DEBUG
        _commandManager.AddCommand("acctcreate", Command_acctcreate);
        _commandManager.AddUnlogged("acctcreate");
        Log(LogLevel.Warn, "DEBUG build: ?acctcreate is available (local testing only).");
#endif
        Log(LogLevel.Info, $"Loaded; game logins use the account store '{Path.GetFullPath(ArenaOwnership.DbPath)}'.");
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        if (broker.UnregisterInterface(ref _authToken) != 0)
            return false;
#if DEBUG
        _commandManager.RemoveCommand("acctcreate", Command_acctcreate);
        _commandManager.RemoveUnlogged("acctcreate");
#endif
        NewPlayerCallback.Unregister(broker, Callback_NewPlayer);
        lock (_pendingLock) _pending.Clear();
        _store?.Dispose();
        _store = null;
        return true;
    }

    // ---- passwd.conf import -------------------------------------------------------

    /// <summary>
    /// Imports passwd.conf <c>[Users]</c> as legacy accounts, once: real hashes become active
    /// accounts, <c>lock</c> entries 'locked' and <c>any</c> entries 'reserved'; staff.conf names
    /// (any section) with no passwd.conf hash become 'reserved', so nobody can sign up as staff.
    /// Called only when there is something to import: an empty import would set the store's marker
    /// and block the real one later.
    /// </summary>
    private void ImportLegacyUsers(AccountStore store)
    {
        string? text = ReadConf(ArenaOwnership.PasswdFile, out var path);
        string? staffText = ReadConf("staff.conf", out _);

        var users = ClaimedName.AllUsers(text ?? "");
        var staff = StaffConf.Names(staffText);
        if (users.Count == 0 && staff.Count == 0) return;

        try
        {
            var before = users.Select(u => u.Name).Concat(staff).Where(n => store.Find(n) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var count = store.ImportLegacy(users.Select(u => (u.Name, u.Value)), staff);
            if (count == 0) return; // imported before (marker) or every name already has an account
            var created = before.Select(n => store.Find(n)).Where(a => a is not null).Select(a => a!).ToList();
            Log(LogLevel.Info, $"Imported {count} legacy account(s) from {path ?? ArenaOwnership.PasswdFile}: "
                + string.Join(", ", created.Where(a => a.Status == "active").Select(a => a.Name)));
            foreach (var a in created.Where(a => a.Status != "active"))
                Log(LogLevel.Warn, $"Name '{a.Name}' is {a.Status}: it cannot be signed up or logged into until staff release it.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Legacy import from {path} failed: {ex.Message}");
        }
    }

    private string? ReadConf(string file, out string? path)
    {
        foreach (var candidate in new[] { Path.Combine("conf", file), file })
        {
            try
            {
                if (File.Exists(candidate)) { path = candidate; return File.ReadAllText(candidate); }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, $"Could not read {candidate} for the legacy import: {ex.Message}");
            }
        }
        path = null;
        return null;
    }

    // ---- IAuth ------------------------------------------------------------------------

    void IAuth.Authenticate(IAuthRequest authRequest)
    {
        var player = authRequest.Player;
        var store = _store;
        if (player is null || store is null || authRequest.LoginBytes.Length < LoginPacket.VIELength)
        {
            Refuse(authRequest, player?.Type == ClientType.Continuum, null);
            authRequest.Done();
            return;
        }

        ref readonly LoginPacket lp = ref authRequest.LoginPacket;
        var name = Decode(((ReadOnlySpan<byte>)lp.Name).SliceNullTerminated()).Trim();
        var password = Decode(((ReadOnlySpan<byte>)lp.Password).SliceNullTerminated());
        var continuum = player.Type == ClientType.Continuum;

        var result = authRequest.Result;
        result.Authenticated = false;
        result.DemoData = false;
        result.SetName(name);
        result.SetSendName(name);
        result.SetSquad("");

        if (name.Length == 0)
        {
            result.Code = AuthCode.BadName;
            authRequest.Done();
            return;
        }

        // a guest, unless an old account really is named "guest"
        if (string.Equals(name, AccountRules.GuestLogin, StringComparison.OrdinalIgnoreCase) && store.Find(name) is null)
        {
            LetGuestIn(authRequest, store, password);
            return;
        }

        // the zone's own bots (arena bots and the tutorial's): a one-time pass from the bot supervisor
        if (store.IsBotLogin(name) || (TutorialBots.Holds(name) && store.Find(name) is null))
        {
            result.Authenticated = BotPasses.Redeem(name, password);
            if (result.Authenticated) BotPasses.MarkSession(player);
            result.Code = result.Authenticated ? AuthCode.OK : AuthCode.BadPassword;
            authRequest.Done();
            return;
        }

        var token = new object();
        lock (_pendingLock) _pending[player] = token;

        _ = Task.Run(() =>
        {
            LoginResult? outcome;
            Account? account = null;
            try
            {
                // a game pass from the site's Play button (single use, one minute, this account
                // only); anything else is checked as the password
                outcome = store.RedeemGamePass(name, password, out account)
                    ? LoginResult.Ok
                    : store.CheckLogin(name, password, out account);
            }
            catch (Exception ex)
            {
                outcome = null;
                Log(LogLevel.Error, $"Login check for '{name}' failed: {ex.Message}");
            }

            var done = new Completion(authRequest, player, token, name, continuum, outcome, account);
            if (!_mainloop.QueueMainWorkItem(Complete, done))
            {
                Log(LogLevel.Error, $"Could not queue the login answer for '{name}'; dropping it.");
                lock (_pendingLock) { if (_pending.TryGetValue(player, out var t) && t == token) _pending.Remove(player); }
            }
        });
    }

    private sealed record Completion(IAuthRequest Request, Player Player, object Token, string TypedName,
        bool Continuum, LoginResult? Outcome, Account? Account);

    /// <summary>Mainloop: fills the result and hands it back, unless the player left meanwhile.</summary>
    private void Complete(Completion c)
    {
        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(c.Player, out var t) || t != c.Token)
            {
                Log(LogLevel.Info, $"Login answer for '{c.TypedName}' dropped (player disconnected).");
                return;
            }
            _pending.Remove(c.Player);
        }

        var result = c.Request.Result;
        if (c.Outcome == LoginResult.Ok && c.Account is { } account)
        {
            result.Authenticated = true;
            result.Code = AuthCode.OK;
            result.SetName(account.Name);
            result.SetSendName(account.Name);
            Log(LogLevel.Info, $"Login ok: '{account.Name}'{(c.TypedName != account.Name ? $" (typed '{c.TypedName}')" : "")}.");
        }
        else
        {
            Refuse(c.Request, c.Continuum, c.Outcome == LoginResult.Ok ? null : c.Outcome);
            Log(LogLevel.Info, $"Login refused for '{c.TypedName}': {c.Outcome?.ToString() ?? "error"}"
                + (c.Outcome == LoginResult.LockedOut ? " (too many wrong passwords; name locked for 10 minutes)." : "."));
        }
        c.Request.Done();
    }

    /// <summary>
    /// Mainloop: lets a guest in, unauthenticated, under a random space name nobody online or on file has. The password
    /// <see cref="TutorialGuests.DonePassword"/> is a Dare client saying this guest already left or finished the tutorial.
    /// </summary>
    private void LetGuestIn(IAuthRequest request, AccountStore store, string password)
    {
        var result = request.Result;
        var picked = GuestNames.PickFree(Random.Shared.Next, n => _playerData.FindPlayer(n) is not null || store.Find(n) is not null ||
            store.BotNameTaken(n) || TutorialBots.Holds(n));
        if (picked is null)
        {
            Refuse(request, request.Player?.Type == ClientType.Continuum, null);
            Log(LogLevel.Warn, "Guest login refused: no free guest name.");
            request.Done();
            return;
        }
        result.Authenticated = false;
        result.Code = AuthCode.OK;
        result.SetName(picked);
        result.SetSendName(picked);
        if (request.Player is { } player) TutorialGuests.Mark(player, password == TutorialGuests.DonePassword);
        Log(LogLevel.Info, $"Guest login: flying as '{picked}'.");
        request.Done();
    }

    private static void Refuse(IAuthRequest request, bool continuum, LoginResult? outcome)
    {
        var result = request.Result;
        result.Authenticated = false;
        var (kind, text) = GameLogin.Refusal(outcome, continuum)!.Value;
        switch (kind)
        {
            case GameRefusal.BadPassword:
                result.Code = AuthCode.BadPassword;
                break;
            case GameRefusal.CustomText:
                result.Code = AuthCode.CustomText;
                var span = (text ?? "").AsSpan();
                result.SetCustomText(span[..Math.Min(span.Length, result.GetMaxCustomTextLength())]);
                break;
            default:
                result.Code = AuthCode.NoPermission;
                break;
        }
    }

    private void Callback_NewPlayer(Player player, bool isNew)
    {
        if (isNew) return;
        lock (_pendingLock) _pending.Remove(player);
        TutorialGuests.Forget(player);
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => StringUtils.DefaultEncoding.GetString(bytes);

    private void Log(LogLevel level, string message) => _logManager.LogM(level, LogName, message);

#if DEBUG
    // ---- DEBUG-only test hook ---------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name> <password>",
        Description = "DEBUG builds only: creates a Foundation account (no email) for local testing.")]
    private void Command_acctcreate(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var store = _store;
        var s = parameters.Trim();
        var i = s.LastIndexOf(' ');
        if (store is null || i <= 0)
        {
            _chat.SendMessage(player, "Usage: ?acctcreate <name> <password>");
            return;
        }
        var name = s[..i].Trim().ToString();
        var password = s[(i + 1)..].ToString();
        var email = $"acct{Random.Shared.Next(1_000_000, 9_999_999)}@local.test";

        _ = Task.Run(() =>
        {
            string reply;
            try
            {
                var started = store.StartSignup(name, password, email, out var code);
                if (!started.Ok) reply = $"acctcreate: {started.Message}";
                else if (code.Length == 0) reply = $"acctcreate: '{name}' already has an account.";
                else
                {
                    var verified = store.VerifySignup(email, code, out var account);
                    reply = verified.Ok && account is not null
                        ? $"acctcreate: account '{account.Name}' created."
                        : $"acctcreate: {verified.Message}";
                }
            }
            catch (Exception ex)
            {
                reply = $"acctcreate failed: {ex.Message}";
            }
            Log(LogLevel.Info, reply);
            _mainloop.QueueMainWorkItem(r => _chat.SendMessage(player, r), reply);
        });
    }
#endif
}
