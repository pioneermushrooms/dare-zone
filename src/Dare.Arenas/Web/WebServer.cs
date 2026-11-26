using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// Module side of the web pages: start/stop, and the API handlers. Handlers that touch server
// state (config, arenas, players, chat) run on the mainloop via WebServer.OnMain; the store,
// WebAuth and the rate limiter are thread-safe.
// ---------------------------------------------------------------------------------------------
public sealed partial class ArenaOwnership
{
    internal const string ConfigSection = "DareArenas";
    // Loopback only (never "+" or "*": the managed HttpListener would then bind every interface).
    // HttpListener matches prefixes on the Host header, so a request with Host: subspacefoundation.com
    // gets 404 here. Caddy must therefore rewrite Host to the upstream (header_up Host
    // {upstream_hostport}); see deploy/Caddyfile.snippet.
    internal const string DefaultWebListen = "http://127.0.0.1:8088/";
    private const int PublicListMax = 30;

    private WebServer? _web;

    /// <summary>Public base URL used in ?weblogin links (DareArenas:WebBaseUrl), no trailing '/'.</summary>
    internal string WebBaseUrl =>
        (_configManager.GetStr(_configManager.Global, ConfigSection, "WebBaseUrl") is { Length: > 0 } url ? url : DefaultWebBaseUrl).TrimEnd('/');

    private void StartWeb()
    {
        var listen = _configManager.GetStr(_configManager.Global, ConfigSection, "WebListen");
        if (string.IsNullOrWhiteSpace(listen)) listen = DefaultWebListen;
        if (!listen.EndsWith('/')) listen += "/";

        LoadEmailSender();
        try
        {
            _web = new WebServer(this, _mainloop, listen);
            _web.Start();
            Log(LogLevel.Info, $"Web pages listening on {listen} (public links: {WebBaseUrl}/).");
        }
        catch (Exception ex)
        {
            // The in-game commands keep working without the web pages.
            Log(LogLevel.Error, $"Could not start the web listener on {listen}: {ex.Message}");
            _web?.Dispose();
            _web = null;
        }
    }

    private void StopWeb()
    {
        if (_web is null) return;
        _web.Dispose();
        _web = null;
        // Run anything the web threads already queued while the store is still open.
        _mainloop.WaitForMainWorkItemDrain();
    }

    internal ArenaStore WebStore => Store;

    // ---- handlers (mainloop) ------------------------------------------------------------------

    /// <summary>Players per arena base name. Mainloop (player data).</summary>
    internal Dictionary<string, int> PlayerCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
                if (p.Arena is not null)
                    counts[p.Arena.BaseName] = counts.GetValueOrDefault(p.Arena.BaseName) + 1;
        }
        finally
        {
            _playerData.Unlock();
        }
        return counts;
    }

    /// <summary>The public feed. Store reads only (thread-safe), so it runs on the web worker.</summary>
    internal object PublicData(IReadOnlyDictionary<string, int> counts)
    {
        var requests = Store.Requests(pendingOnly: false, 100);
        return new
        {
            pending = Store.PendingRequestsOldestFirst(100)
                .Select(r => new { r.Arena, r.Player, r.Pitch, r.At }),
            decisions = requests.Where(r => r.Status != RequestStatus.Pending).Take(PublicListMax)
                .Select(r => new { r.Arena, r.Player, r.Pitch, status = r.Status.ToString(), r.DecidedBy, r.Reason, r.At }),
            arenas = Store.Arenas().Select(a => new
            {
                a.Name, a.Owner, status = a.Status.ToString(), a.Created, a.LastEntered, a.CoMods,
                players = a.Status == ArenaStatus.Active ? counts.GetValueOrDefault(a.Name) : 0,
            }),
            events = Store.Events(null, PublicListMax).Select(e => new { e.At, e.Arena, e.Actor, e.Kind, e.Detail }),
            publicPlayers = PublicCounts.PublicPlayers(counts),
        };
    }

    /// <summary>The owner page's data for <paramref name="arena"/> (role already checked by the caller).</summary>
    internal object ArenaData(string player, IReadOnlyList<(string Arena, string Role)> roles, string arena, string role, ConfigHandle? cfg)
    {
        var owned = Store.OwnedOrDraft(arena);
        var levelFiles = cfg is null ? new List<string>() : LevelFiles.Parse(_configManager.GetStr(cfg, "General", "LevelFiles")).ToList();
        return new
        {
            player,
            arenas = roles.Select(r => new { name = r.Arena, role = r.Role }),
            arena,
            role,
            owner = owned?.Owner,
            status = owned?.Status.ToString(),
            coMods = owned?.CoMods ?? (IReadOnlyList<string>)Array.Empty<string>(),
            lastEntered = owned?.LastEntered,
            @private = Store.IsPrivate(arena),
            map = cfg is null ? null : _configManager.GetStr(cfg, "General", "Map"),
            levelFiles,
            limits = new { maxLevelFiles = MaxLevelFiles, mapBytes = 2 * 1024 * 1024, lvzBytes = 5 * 1024 * 1024, settingChangesPerMinute = SettingChangesPerMinute },
            settings = cfg is null ? null : SettingsForWeb(cfg),
            configMissing = cfg is null,
            maxPerPilot = ArenaStore.MaxPerPilot,
            staff = StaffFlags(player),
            restart = RestartData(arena, player),
        };
    }

    internal object NoArenaData(string player)
    {
        var pending = Store.Requests(pendingOnly: true, 1000)
            .FirstOrDefault(r => string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase));
        return new
        {
            player,
            arenas = Array.Empty<object>(),
            arena = (string?)null,
            role = (string?)null,
            pendingRequest = pending is null ? null : new { pending.Arena, pending.Pitch, pending.At },
            maxPitch = MaxPitchLength,
            maxPerPilot = ArenaStore.MaxPerPilot,
            staff = StaffFlags(player),
        };
    }

    /// <summary>Every owner-editable setting, grouped by section, with the server's config help.</summary>
    private object SettingsForWeb(ConfigHandle cfg)
    {
        var documented = DocumentedArenaSettings();
        var policy = new SettingsPolicy(documented.Values);
        var editable = documented.Values.Where(v => policy.IsEditable(v.Section, v.Key)).ToList();

        var help = new Dictionary<(string, string), IConfigHelpAttribute?>();
        _configHelp.Lock();
        try
        {
            foreach (var (section, key) in editable)
            {
                var helpSection = ShipSections.Contains(section) ? "All" : section;
                help[(section, key)] = _configHelp.TryGetSettingHelp(helpSection, key, out var list) && list.Count > 0
                    ? (list.FirstOrDefault(r => r.Attribute.Scope == ConfigScope.Arena).Attribute ?? list[0].Attribute)
                    : null;
            }
        }
        finally
        {
            _configHelp.Unlock();
        }

        return editable
            .GroupBy(v => v.Section)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                section = g.Key,
                settings = g.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v =>
                {
                    var h = help.GetValueOrDefault((v.Section, v.Key));
                    return new
                    {
                        key = v.Key,
                        value = _configManager.GetStr(cfg, v.Section, v.Key),
                        description = h?.Description is { } d ? string.Join(' ', d.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null,
                        @default = h?.DefaultValue,
                        min = h?.Min,
                        max = h?.Max,
                        range = h?.Range,
                    };
                }),
            });
    }

    /// <summary>Re-checks the role at the moment of writing (it may have changed since the request began).</summary>
    internal bool HasRole(string arena, string player, bool ownerOnly)
    {
        var role = Store.RoleIn(arena, player);
        return role is not null && (!ownerOnly || role == "owner");
    }

    internal Result WebSetting(ConfigHandle cfg, string arena, string player, string sectionKey, string value)
    {
        if (!HasRole(arena, player, ownerOnly: false))
            return new Result(false, "You are no longer an owner or co-mod of this arena.");
        var result = ApplySetting(cfg, arena, player, sectionKey, value);
        if (result.Ok) Log(LogLevel.Info, $"(web) {player} changed a setting of [{arena}]: {result.Message}");
        return result;
    }

    internal Result WebUpload(ConfigHandle cfg, string arena, string player, UploadKind kind, string fileName, byte[] content)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can upload files.");
        var result = StoreUpload(cfg, arena, player, kind, fileName, content);
        Log(LogLevel.Info, $"(web) {player} upload of {kind} '{fileName}' to [{arena}]: {(result.Ok ? "stored" : "refused: " + result.Message)}");
        return result;
    }

    internal Result WebRemoveLvz(ConfigHandle cfg, string arena, string player, string fileName)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can remove files.");
        var result = RemoveLvz(cfg, arena, player, fileName);
        Log(LogLevel.Info, $"(web) {player} remove of lvz '{fileName}' in [{arena}]: {(result.Ok ? "removed" : "refused: " + result.Message)}");
        return result;
    }

    internal Result WebRequest(string player, string arena, string pitch)
    {
        var result = SubmitRequest(player, arena, pitch);
        if (result.Ok && _playerData.FindPlayer(player) is { } online)
            Reply(online, $"You launched an arena on the web: {result.Message}");
        return result;
    }

    /// <summary>Co-mods from the web hub: the owner role is checked again here, at the moment of writing. Mainloop.</summary>
    internal Result WebCoMod(string player, string arena, string action, string name)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can change co-mods.");
        return ChangeCoMod(player, arena, action, name);
    }

    /// <summary>The arena's General:Map value. Mainloop.</summary>
    internal string? MapSetting(ConfigHandle cfg) => _configManager.GetStr(cfg, "General", "Map");

    /// <summary>Recycle from the web (after a map publish); the owner role is checked again here. Mainloop.</summary>
    internal Result WebRecycle(string player, string arena)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can recycle it from the web.");
        var (recycled, notRecycled) = RecycleRunning(arena);
        Log(LogLevel.Info, $"(web) {player} recycled [{arena}]: {recycled.Count} recycled, {notRecycled.Count} could not be.");
        if (notRecycled.Count > 0)
            return new Result(false, $"Could not recycle {string.Join(", ", notRecycled)} right now; type ?recyclearena there.");
        return new Result(true, recycled.Count > 0
            ? "Arena recycled: players inside are back in with the new map."
            : "Nobody is in the arena; the new map loads when someone enters.");
    }

}

/// <summary>
/// The HttpListener for the arena pages. Accepts paths with or without the <c>/arena</c> prefix
/// (Caddy's <c>handle_path /arena/*</c> strips it; local testing keeps it); public links and
/// redirects always use <c>/arena/...</c>. The account pages arrive with their <c>/account</c>
/// prefix kept (Caddy <c>handle /account/*</c>, no stripping) and are routed in AccountPages.cs. Requests are handled on thread-pool threads; anything
/// touching server state is queued to the mainloop with <see cref="IMainloop.QueueMainWorkItem"/>.
/// </summary>
internal sealed partial class WebServer : IDisposable
{
    private const int MaxJsonBytes = 4096;
    private const int MaxBotJsonBytes = 8192;
    private const int MaxConcurrent = 16;
    // A request body must arrive within these, so slow senders can't hold the 16 slots.
    private static readonly TimeSpan JsonBodyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadBodyTimeout = TimeSpan.FromSeconds(120);
    private const string CsrfHeader = "X-Requested-With";
    private const string CsrfValue = "dare-arena";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ArenaOwnership _m;
    private readonly IMainloop _mainloop;
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly Dictionary<string, (byte[] Body, string Type)> _static;
    private volatile bool _stopped;

    public WebServer(ArenaOwnership module, IMainloop mainloop, string prefix)
    {
        _m = module;
        _mainloop = mainloop;
        _listener.Prefixes.Add(prefix);
        _static = LoadStatic();
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        _stopped = true;
        try { _listener.Close(); } catch { /* already closed */ }
    }

    private async Task AcceptLoop()
    {
        var failures = 0;
        while (!_stopped)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (_stopped)
            {
                break;
            }
            catch (Exception ex)
            {
                // Back off on a persistent error (100 ms doubling to 30 s) and log only when the
                // delay grows, so a broken listener can't flood the log.
                failures++;
                var delay = Math.Min(100 * (1 << Math.Min(failures - 1, 9)), 30_000);
                if (failures <= 10 || delay < 30_000 || failures % 100 == 0)
                    _m.Log(LogLevel.Warn, $"Web accept failed ({failures} in a row; retrying in {delay} ms): {ex.Message}");
                await Task.Delay(delay).ConfigureAwait(false);
                continue;
            }

            failures = 0;
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        if (!_slots.Wait(0))
        {
            TryWrite(ctx, Text(503, "Busy; try again shortly."));
            return;
        }

        try
        {
            Reply reply;
            try
            {
                reply = await RouteAsync(ctx).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The body did not arrive in time: answer 408 and close the connection (closing it
                // also ends the abandoned read).
                _m.Log(LogLevel.Info, $"Web {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: request body too slow; connection closed.");
                try { ctx.Response.KeepAlive = false; } catch { }
                reply = JsonReply(408, new { ok = false, message = "The request took too long to arrive." });
            }
            catch (Exception ex)
            {
                _m.Log(LogLevel.Error, $"Web {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} failed: {ex}");
                reply = JsonReply(500, new { ok = false, message = "Server error; staff can see the details in the log." });
            }
            TryWrite(ctx, reply);
        }
        finally
        {
            _slots.Release();
        }
    }

    // ---- routing ------------------------------------------------------------------------------

    private async Task<Reply> RouteAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = WebPaths.Normalize(req.Url?.AbsolutePath);
        var method = req.HttpMethod;

        if (IsAccountPath(path))
            return await AccountRouteAsync(req, path).ConfigureAwait(false);

        switch (path)
        {
            case "/": return Get(method, () => Static("index.html"));
            case "/me": return Get(method, () => Static("arena.html"));
            case "/staff": return Get(method, () => Static("staff.html"));
            case "/staff.js": return Get(method, () => Static("staff.js"));
            case "/api/staff": return method == "GET" ? await StaffDataAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/staff/approve":
                return method == "POST" ? await StaffActionAsync(req, (p, b) => _m.WebStaffApprove(p, Str(b, "arena") ?? "", Str(b, "player"))).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/staff/decline":
                return method == "POST" ? await StaffActionAsync(req, (p, b) => _m.WebStaffDecline(p, Str(b, "arena") ?? "", Str(b, "reason") ?? "", Str(b, "player"))).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/staff/arena":
                return method == "POST" ? await StaffActionAsync(req, (p, b) => _m.WebStaffArena(p, Str(b, "arena") ?? "", Str(b, "action") ?? "", Str(b, "reason") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/staff/create":
                return method == "POST" ? await StaffActionAsync(req, (p, b) => _m.WebStaffCreate(p, Str(b, "arena") ?? "", Str(b, "owner"))).ConfigureAwait(false) : MethodNotAllowed();
            case "/app.js": return Get(method, () => Static("app.js"));
            case "/style.css": return Get(method, () => Static("style.css"));
            case "/login": return method == "GET" ? Login(req) : MethodNotAllowed();
            case "/api/zones": return Get(method, ZonesReply);
            case "/api/public":
                return method == "GET" ? await PublicAsync().ConfigureAwait(false) : MethodNotAllowed();
            case "/api/me": return method == "GET" ? await MeAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/settings": return method == "POST" ? await SettingsAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/upload": return method == "POST" ? await UploadAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/rmlvz": return method == "POST" ? await RemoveLvzAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/request": return method == "POST" ? await RequestAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/drafts/mine": return method == "GET" ? await DraftsMineAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/drafts":
                return method == "POST" ? await DraftActionAsync(req, (p, b) => _m.CreateDraftFor(p, Str(b, "name") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/drafts/invite":
                return method == "POST" ? await DraftActionAsync(req, (p, b) => _m.InviteFor(p, Str(b, "name") ?? "", Str(b, "player") ?? "", Str(b, "add") != "false")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/drafts/request":
                return method == "POST" ? await DraftActionAsync(req, (p, b) => _m.RequestPublicFor(p, Str(b, "name") ?? "", Str(b, "pitch") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/drafts/publish":
                return method == "POST" ? await DraftActionAsync(req, (p, b) => _m.PublishFor(p, Str(b, "name") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            case "/map": return Get(method, () => Static("map.html"));
            case "/editor.js": return Get(method, () => Static("editor.js"));
            case "/lvl.js": return Get(method, () => Static("lvl.js"));
            case "/lvlgen.js": return Get(method, () => Static("lvlgen.js"));
            case "/api/map": return method == "GET" ? await MapAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/recycle": return method == "POST" ? await RecycleAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/restart": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) =>
                _m.WebRestart(p, a, Str(b, "message"), RestartAt(b), RestartEvery(b))).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/restart/cancel": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) => _m.WebCancelRestart(p, a)).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/visibility": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) => _m.WebSetPrivate(p, a, b)).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/discard": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) => _m.WebDiscard(p, a, b)).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/bots": return method == "GET" ? ListBots(req) : MethodNotAllowed();
            case "/api/bots/save": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) =>
                _m.SaveBot(a, p, Str(b, "label") ?? "", Str(b, "script") ?? ""), MaxBotJsonBytes).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/bots/deploy":
            case "/api/bots/stop":
                return method == "POST" ? await OwnerActionAsync(req, (p, a, b) =>
                    _m.WebStore.SetBotDeployment(a, p, Str(b, "label") ?? "", path == "/api/bots/deploy")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/bots/remove": return method == "POST" ? await OwnerActionAsync(req, (p, a, b) =>
                _m.SaveBot(a, p, Str(b, "label") ?? "", null)).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/comod": return method == "POST" ? await CoModAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/maps": return method == "GET" ? SharedMaps() : MethodNotAllowed();
            case "/api/maps/file": return method == "GET" ? await SharedMapAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/arenas":
                return method == "GET" ? JsonReply(200, new { ok = true, arenas = await OnMain(() => _m.ZoneArenas()).ConfigureAwait(false) }) : MethodNotAllowed();
            case "/api/chat": return method == "GET" ? await ChatAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/chat/join": return method == "POST" ? await ChatJoinAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/chat/say": return method == "POST" ? await ChatSayAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/chat/leave": return method == "POST" ? await ChatLeaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/requests/withdraw":
                return method == "POST" ? await DraftActionAsync(req, (p, b) => _m.WithdrawFor(p, Str(b, "arena") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            default: return Text(404, "Not found.");
        }
    }

    // ---- public feed cache: unauthenticated, so at most one rebuild per PublicCacheTtl ----------

    private static readonly TimeSpan PublicCacheTtl = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _publicBuild = new(1, 1);
    private volatile Tuple<DateTimeOffset, byte[]>? _publicCache;

    private async Task<Reply> PublicAsync()
    {
        var cached = _publicCache;
        if (cached is not null && DateTimeOffset.UtcNow - cached.Item1 < PublicCacheTtl)
            return JsonBytes(200, cached.Item2);

        await _publicBuild.WaitAsync().ConfigureAwait(false); // single flight
        try
        {
            cached = _publicCache;
            if (cached is not null && DateTimeOffset.UtcNow - cached.Item1 < PublicCacheTtl)
                return JsonBytes(200, cached.Item2);

            var counts = await OnMain(() => _m.PlayerCounts()).ConfigureAwait(false); // the only mainloop part
            var body = JsonSerializer.SerializeToUtf8Bytes(_m.PublicData(counts), Json);
            _publicCache = Tuple.Create(DateTimeOffset.UtcNow, body);
            return JsonBytes(200, body);
        }
        finally
        {
            _publicBuild.Release();
        }
    }

    /// <summary>
    /// The live zone list (public; served from the module's cache, never fetched per request).
    /// Addresses stay server-side: the page only needs to know which zones play in the browser.
    /// </summary>
    private Reply ZonesReply()
    {
        var z = _m.Zones();
        return JsonReply(200, new
        {
            zones = z.Zones.Select(x => new { x.Name, x.Description, x.Players, browser = ArenaOwnership.PlaysInBrowser(x.Address) }),
            at = z.At == DateTimeOffset.MinValue ? (DateTimeOffset?)null : z.At,
            answered = z.Answered,
            asked = z.Asked,
        });
    }

    private static Reply JsonBytes(int status, byte[] body) => new(status, "application/json; charset=utf-8", body);

    private Reply Get(string method, Func<Reply> f) => method is "GET" or "HEAD" ? f() : MethodNotAllowed();

    private Reply Login(HttpListenerRequest req)
    {
        var token = req.QueryString["t"];
        var session = string.IsNullOrEmpty(token) ? null : _m.WebAuth.Redeem(token);
        if (session is null)
            return Html(400, "<p>This sign-in link is invalid, used or expired. Type <b>?weblogin</b> in game for a new one.</p>"
                             + "<p><a href=\"/arena/\">Arena list</a></p>");

        _m.Log(LogLevel.Info, $"Web sign-in for {_m.WebAuth.PlayerFor(session)}.");
        return new Reply(303, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Signed in."),
            [("Set-Cookie", WebPaths.SessionCookie(session, secure: true)), ("Location", WebPaths.Prefix + "/me")]);
    }

    /// <summary>
    /// 401 without a session (a ?weblogin session or an account sign-in, both keyed by name); 403
    /// for writes without the anti-CSRF
    /// header or with an Origin that isn't this site.
    /// </summary>
    private Reply? Gate(HttpListenerRequest req, bool write, out string player)
    {
        player = "";
        var cookie = req.Cookies[WebPaths.SessionCookieName]?.Value;
        var session = string.IsNullOrEmpty(cookie) ? null : _m.WebAuth.SessionFor(cookie);
        if (session is null)
            return JsonReply(401, new { ok = false, message = "Not signed in. Sign in at /account/login, or type ?weblogin in game for a sign-in link." });
        if (session.Value.AccountId is { } id)
        {
            var account = _m.Accounts.Get(id);
            if (account is null)
                return JsonReply(401, new { ok = false, message = "Not signed in." });
        }
        if (write && req.Headers[CsrfHeader] != CsrfValue)
            return JsonReply(403, new { ok = false, message = "Missing request header." });
        if (write && req.Headers["Origin"] is { } origin && !WebPaths.IsAllowedOrigin(origin, _m.WebBaseUrl))
            return JsonReply(403, new { ok = false, message = "Request refused (not from this site)." });
        player = session.Value.Player;
        return null;
    }

    /// <summary>Picks the arena the request is about: <c>?arena=</c> if given, else the player's first role.</summary>
    private static (string Arena, string Role)? Pick(IReadOnlyList<(string Arena, string Role)> roles, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted))
            return roles.Count > 0 ? roles[0] : null;
        foreach (var r in roles)
            if (string.Equals(r.Arena, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    private async Task<Reply> MeAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;

        var roles = _m.WebStore.RolesFor(player);
        var wanted = req.QueryString["arena"];
        var pick = Pick(roles, wanted);
        if (pick is null)
        {
            if (!string.IsNullOrWhiteSpace(wanted))
                return JsonReply(403, new { ok = false, message = "You are not an owner or co-mod of that arena." });
            return JsonReply(200, await OnMain(() => _m.NoArenaData(player)).ConfigureAwait(false));
        }

        var (arena, role) = pick.Value;
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        try
        {
            return JsonReply(200, await OnMain(() => _m.ArenaData(player, roles, arena, role, cfg)).ConfigureAwait(false));
        }
        finally
        {
            if (cfg is not null) _m.CloseArenaConfig(cfg);
        }
    }

    private async Task<Reply> SettingsAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var section = Str(body, "section");
        var key = Str(body, "key");
        var value = Str(body, "value");

        var pick = Pick(_m.WebStore.RolesFor(player), Str(body, "arena"));
        if (pick is null)
            return JsonReply(403, new { ok = false, message = "Only this arena's owner or co-mods can change its settings." });
        if (section is null || key is null || value is null)
            return JsonReply(400, new { ok = false, message = "Send {\"section\", \"key\", \"value\"}." });

        var arena = pick.Value.Arena;
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null)
            return JsonReply(409, new { ok = false, message = "Your arena's settings file is missing; ask staff." });
        try
        {
            var result = await OnMain(() => _m.WebSetting(cfg, arena, player, $"{section}:{key}", value)).ConfigureAwait(false);
            return ResultReply(result);
        }
        finally
        {
            _m.CloseArenaConfig(cfg);
        }
    }

    private async Task<Reply> UploadAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var kind = req.QueryString["kind"]?.ToLowerInvariant() switch
        {
            "map" => UploadKind.Map,
            "lvz" => UploadKind.Lvz,
            _ => (UploadKind?)null,
        };
        if (kind is null)
            return JsonReply(400, new { ok = false, message = "kind must be map or lvz." });

        var pick = Pick(_m.WebStore.RolesFor(player), req.QueryString["arena"]);
        if (pick is null || pick.Value.Role != "owner")
            return JsonReply(403, new { ok = false, message = "Only the arena's owner can upload files." });
        var arena = pick.Value.Arena;

        var name = req.QueryString["name"]?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (kind == UploadKind.Lvz)
                return JsonReply(400, new { ok = false, message = "name is required for graphics (.lvz) uploads." });
            name = arena + ".lvl";
        }

        // Refuse oversized bodies before reading them into memory.
        var limit = kind == UploadKind.Map ? 2 * 1024 * 1024 : 5 * 1024 * 1024;
        if (req.ContentLength64 > limit)
            return JsonReply(413, new { ok = false, message = kind == UploadKind.Map ? "Map files must be 2 MB or smaller." : "Graphics files must be 5 MB or smaller." });
        var content = await ReadBodyAsync(req, limit, UploadBodyTimeout).ConfigureAwait(false);
        if (content is null)
            return JsonReply(413, new { ok = false, message = kind == UploadKind.Map ? "Map files must be 2 MB or smaller." : "Graphics files must be 5 MB or smaller." });

        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null)
            return JsonReply(409, new { ok = false, message = "Your arena's settings file is missing; ask staff." });
        try
        {
            var k = kind.Value;
            var result = await OnMain(() => _m.WebUpload(cfg, arena, player, k, name, content)).ConfigureAwait(false);
            return ResultReply(result);
        }
        finally
        {
            _m.CloseArenaConfig(cfg);
        }
    }

    /// <summary>Owner only: removes a graphics file (same rules as ?rmlvz). Body {"arena", "name"}.</summary>
    private async Task<Reply> RemoveLvzAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var name = Str(body, "name");

        var pick = Pick(_m.WebStore.RolesFor(player), Str(body, "arena"));
        if (pick is null || pick.Value.Role != "owner")
            return JsonReply(403, new { ok = false, message = "Only the arena's owner can remove files." });
        if (string.IsNullOrWhiteSpace(name))
            return JsonReply(400, new { ok = false, message = "Send {\"arena\", \"name\"}." });

        var arena = pick.Value.Arena;
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null)
            return JsonReply(409, new { ok = false, message = ArenaOwnership.ConfigMissingMessage });
        try
        {
            var result = await OnMain(() => _m.WebRemoveLvz(cfg, arena, player, name)).ConfigureAwait(false);
            return ResultReply(result);
        }
        finally
        {
            _m.CloseArenaConfig(cfg);
        }
    }

    /// <summary>The signed-in pilot's draft (or null), for the /build/ page.</summary>
    private async Task<Reply> DraftsMineAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        var store = _m.WebStore;
        return JsonReply(200, new
        {
            player,
            max = ArenaStore.MaxPerPilot,
            maxInvites = ArenaStore.MaxInvites,
            drafts = store.DraftsOwnedBy(player).Select(d => new
            {
                name = d.Name,
                status = d.Status.ToString(),
                live = d.Live,
                liveStatus = d.Live ? store.Get(d.Name)?.Status.ToString() : null,
                invited = d.Invited,
                coMods = d.CoMods,
            }),
            requests = store.PendingRequestsOldestFirst(200)
                .Where(r => string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase))
                .Select(r => new { arena = r.Arena, pitch = r.Pitch, at = r.At }),
        });
    }

    private async Task<Reply> StaffDataAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        var data = await OnMain(() => _m.StaffData(player)).ConfigureAwait(false);
        return data is null
            ? JsonReply(403, new { ok = false, message = "This page is for zone staff." })
            : JsonReply(200, data);
    }

    /// <summary>A staff POST: null from the action means the name lacks that staff power.</summary>
    private async Task<Reply> StaffActionAsync(HttpListenerRequest req, Func<string, JsonElement?, Result?> action)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var result = await OnMain(() => action(player, body)).ConfigureAwait(false);
        return result is null
            ? JsonReply(403, new { ok = false, message = "Only zone staff can do that." })
            : ResultReply(result);
    }

    /// <summary>A POST from the /build/ page: JSON body, signed-in pilot, run on the mainloop.</summary>
    private async Task<Reply> DraftActionAsync(HttpListenerRequest req, Func<string, JsonElement?, Result> action)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var result = await OnMain(() => action(player, body)).ConfigureAwait(false);
        return ResultReply(result);
    }

    /// <summary>
    /// The arena's current map (General:Map) for the hub and the map editor; owner or co-mod. Read
    /// from the arena's folder or the shared maps/ folder only (MapFile), at most 4 MB.
    /// </summary>
    private async Task<Reply> MapAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;

        var pick = Pick(_m.WebStore.RolesFor(player), req.QueryString["arena"]);
        if (pick is null)
            return JsonReply(403, new { ok = false, message = "You are not an owner or co-mod of that arena." });
        var arena = pick.Value.Arena;

        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null)
            return JsonReply(409, new { ok = false, message = ArenaOwnership.ConfigMissingMessage });
        string? setting;
        try
        {
            setting = await OnMain(() => _m.MapSetting(cfg)).ConfigureAwait(false);
        }
        finally
        {
            _m.CloseArenaConfig(cfg);
        }

        foreach (var path in MapFile.Candidates(ArenaOwnership.ArenasDir, arena, ArenaOwnership.MapsDir, setting))
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            if (info.Length > MapFile.MaxReadBytes || info.Length == 0)
                return JsonReply(409, new { ok = false, message = "The current map can't be opened in the editor (empty or too large)." });
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return new Reply(200, "application/octet-stream", bytes, [("X-Map-Name", Path.GetFileName(path))]);
        }
        return JsonReply(404, new { ok = false, message = "This arena has no map file yet; start from an empty map or upload one." });
    }

    private Reply ListBots(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        var arena = req.QueryString["arena"];
        if (arena is null || _m.WebStore.Get(arena) is not { Status: ArenaStatus.Active } owned ||
            !string.Equals(owned.Owner, player, StringComparison.OrdinalIgnoreCase))
            return JsonReply(403, new { ok = false, message = "Only the active arena's owner can manage bots." });
        return JsonReply(200, new { ok = true, bots = _m.WebStore.Bots(owned.Name) });
    }

    /// <summary>Owner only, body {"arena", ...}: runs <paramref name="act"/> on the mainloop with the body.</summary>
    private async Task<Reply> OwnerActionAsync(HttpListenerRequest req, Func<string, string, JsonElement, Result> act, int maxBytes = MaxJsonBytes)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req, maxBytes).ConfigureAwait(false);
        if (body is null) return error!;
        var pick = Pick(_m.WebStore.RolesFor(player), Str(body, "arena"));
        if (pick is null || pick.Value.Role != "owner")
            return JsonReply(403, new { ok = false, message = "Only the arena's owner can do that." });
        var arena = pick.Value.Arena;
        var json = body.Value;
        return ResultReply(await OnMain(() => act(player, arena, json)).ConfigureAwait(false));
    }

    /// <summary>"at": an ISO 8601 time with an offset (the page sends UTC); missing or unreadable: now.</summary>
    private static DateTimeOffset? RestartAt(JsonElement body) =>
        Str(body, "at") is { Length: > 0 } at && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when.ToUniversalTime() : null;

    /// <summary>"every": seconds between announcements, 0 when missing.</summary>
    private static int RestartEvery(JsonElement body) =>
        body.TryGetProperty("every", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 0;

    /// <summary>Owner only: recycles the arena so a newly published map loads (as ?recyclearena). Body {"arena"}.</summary>
    private async Task<Reply> RecycleAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var pick = Pick(_m.WebStore.RolesFor(player), Str(body, "arena"));
        if (pick is null || pick.Value.Role != "owner")
            return JsonReply(403, new { ok = false, message = "Only the arena's owner can recycle it from the web." });
        var arena = pick.Value.Arena;
        return ResultReply(await OnMain(() => _m.WebRecycle(player, arena)).ConfigureAwait(false));
    }

    /// <summary>Owner only: {"arena", "action": "add"|"remove", "player"}, as ?comod.</summary>
    private async Task<Reply> CoModAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var pick = Pick(_m.WebStore.RolesFor(player), Str(body, "arena"));
        if (pick is null || pick.Value.Role != "owner")
            return JsonReply(403, new { ok = false, message = "Only the arena's owner can change co-mods." });
        var action = (Str(body, "action") ?? "").ToLowerInvariant();
        var name = Str(body, "player") ?? "";
        var arena = pick.Value.Arena;
        var result = await OnMain(() => _m.WebCoMod(player, arena, action, name)).ConfigureAwait(false);
        return ResultReply(result);
    }

    // ---- the home page's chat panel ------------------------------------------------------------

    /// <summary>The signed-in pilot's account (account sessions; ?weblogin sessions by name), or null.</summary>
    private Account? ChatAccount(HttpListenerRequest req)
    {
        var session = req.Cookies[WebPaths.SessionCookieName]?.Value;
        if (string.IsNullOrEmpty(session) || _m.WebAuth.SessionFor(session) is not { } s) return null;
        return s.AccountId is { } id ? _m.Accounts.Get(id) : _m.Accounts.Find(s.Player);
    }

    /// <summary>
    /// GET ?arena=&amp;after=: the arena's chat after id <c>after</c> and who is in it. Anyone gets a public
    /// arena's public chat; a pilot chatting in the arena (a bridge) gets everything their chat
    /// client sees, private and team messages to them included.
    /// </summary>
    private async Task<Reply> ChatAsync(HttpListenerRequest req)
    {
        if (ArenaOwnership.ChatArenaName(req.QueryString["arena"]) is not { } arena)
            return JsonReply(400, new { ok = false, message = "No such arena." });
        long.TryParse(req.QueryString["after"], out var after);
        var bridge = ChatAccount(req) is { } account ? _m.BridgeFor(account.Id) : null;
        var mine = bridge is not null && string.Equals(bridge.Arena, arena, StringComparison.OrdinalIgnoreCase);
        if (!mine && DraftRules.IsDraft(arena))
            return JsonReply(403, new { ok = false, message = "Drafts are private." });
        var (lines, last) = mine ? bridge!.Since(after) : _m.PublicChat(arena, after);
        var players = await OnMain(() => _m.ChatPlayers(arena)).ConfigureAwait(false);
        return JsonReply(200, new
        {
            ok = true,
            arena,
            last,
            mine,
            messages = lines.Select(l => new { l.Id, l.At, kind = l.Kind, from = l.From, text = l.Text }),
            players,
            chat = bridge is null ? null : new { state = bridge.State, arena = bridge.Arena, name = bridge.Pilot, error = bridge.Error, freq = bridge.Freq },
        });
    }

    /// <summary>POST {"arena"}: joins the arena as a chat client, as the signed-in pilot.</summary>
    private async Task<Reply> ChatJoinAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out _) is { } denied) return denied;
        if (ChatAccount(req) is not { } account) return JsonReply(401, new { ok = false, message = "Sign in first." });
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (ArenaOwnership.ChatArenaName(Str(body, "arena")) is not { } arena)
            return JsonReply(400, new { ok = false, message = "No such arena." });
        return ResultReply(await OnMain(() => _m.StartChat(account, arena)).ConfigureAwait(false));
    }

    /// <summary>POST {"text", "to"?}: says something in the arena (privately to "to" when given).</summary>
    private async Task<Reply> ChatSayAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out _) is { } denied) return denied;
        if (ChatAccount(req) is not { } account || _m.BridgeFor(account.Id) is not { } bridge)
            return JsonReply(409, new { ok = false, message = "Press Chat to join the arena first." });
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var why = await bridge.SayAsync(Str(body, "text") ?? "", Str(body, "to")).ConfigureAwait(false);
        return why is null ? JsonReply(200, new { ok = true }) : JsonReply(400, new { ok = false, message = why });
    }

    /// <summary>POST: leaves the arena's chat.</summary>
    private async Task<Reply> ChatLeaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out _) is { } denied) return denied;
        if (ChatAccount(req) is { } account) _m.StopChat(account.Id);
        await Task.CompletedTask.ConfigureAwait(false);
        return JsonReply(200, new { ok = true });
    }

    /// <summary>The server's shared maps (maps/*.lvl), for the map editor's "Start from".</summary>
    private static Reply SharedMaps()
    {
        var names = Directory.Exists(ArenaOwnership.MapsDir)
            ? Directory.EnumerateFiles(ArenaOwnership.MapsDir, "*.lvl")
                .Select(Path.GetFileName).OfType<string>()
                .Where(n => LevelFiles.IsSafeFileName(n) && n.Length <= MapFile.MaxNameLength)
                .Where(n => new FileInfo(Path.Combine(ArenaOwnership.MapsDir, n)) is { LinkTarget: null, Length: > 0 and <= MapFile.MaxReadBytes })
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        return JsonReply(200, new { ok = true, maps = names });
    }

    /// <summary>One shared map's bytes (?name=x.lvl): a plain .lvl name, only from maps/, never a link.</summary>
    private static async Task<Reply> SharedMapAsync(HttpListenerRequest req)
    {
        var name = req.QueryString["name"] ?? "";
        var path = MapFile.Candidates(ArenaOwnership.ArenasDir, "_none_", ArenaOwnership.MapsDir, name)
            .FirstOrDefault(p => string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), Path.GetFullPath(ArenaOwnership.MapsDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));
        if (path is null || new FileInfo(path) is not { Exists: true, Length: > 0 and <= MapFile.MaxReadBytes })
            return JsonReply(404, new { ok = false, message = "No such map." });
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        return new Reply(200, "application/octet-stream", bytes, [("X-Map-Name", Path.GetFileName(path))]);
    }

    private async Task<Reply> RequestAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        var arena = Str(body, "arena") ?? "";
        var pitch = Str(body, "pitch") ?? "";
        var result = await OnMain(() => _m.WebRequest(player, arena, pitch)).ConfigureAwait(false);
        return ResultReply(result);
    }

    // ---- mainloop marshalling -------------------------------------------------------------------

    /// <summary>Runs <paramref name="f"/> on the mainloop thread and returns its result.</summary>
    private Task<T> OnMain<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopped)
        {
            tcs.SetException(new ObjectDisposedException(nameof(WebServer)));
            return tcs.Task;
        }

        var queued = _mainloop.QueueMainWorkItem(static s =>
        {
            var (server, func, t) = s;
            if (server._stopped)
            {
                t.TrySetException(new ObjectDisposedException(nameof(WebServer)));
                return;
            }
            try { t.TrySetResult(func()); }
            catch (Exception ex) { t.TrySetException(ex); }
        }, (this, f, tcs));

        if (!queued) tcs.TrySetException(new InvalidOperationException("The server is shutting down."));
        return tcs.Task;
    }

    // ---- bodies ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes; null if the body is larger. Throws
    /// <see cref="TimeoutException"/> when the whole body has not arrived within <paramref name="timeout"/>
    /// (the caller then aborts the connection, which also ends the abandoned read).
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest req, int limit, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var ms = new MemoryStream(req.ContentLength64 > 0 ? (int)req.ContentLength64 : 0);
        var buffer = new byte[81920];
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException();
            // WaitAsync: the managed HttpListener's stream does not honour cancellation tokens.
            var n = await req.InputStream.ReadAsync(buffer).AsTask().WaitAsync(left).ConfigureAwait(false);
            if (n == 0) break;
            if (ms.Length + n > limit) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static async Task<(JsonElement? Body, Reply? Error)> ReadJsonAsync(HttpListenerRequest req, int maxBytes = MaxJsonBytes)
    {
        if (req.ContentLength64 > maxBytes)
            return (null, JsonReply(413, new { ok = false, message = "Request too large." }));
        if (req.ContentType is null || !req.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return (null, JsonReply(415, new { ok = false, message = "Send JSON (Content-Type: application/json)." }));
        var bytes = await ReadBodyAsync(req, maxBytes, JsonBodyTimeout).ConfigureAwait(false);
        if (bytes is null)
            return (null, JsonReply(413, new { ok = false, message = "Request too large." }));
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, JsonReply(400, new { ok = false, message = "Send a JSON object." }));
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, JsonReply(400, new { ok = false, message = "Invalid JSON." }));
        }
    }

    private static string? Str(JsonElement? body, string name) =>
        body is { } b && b.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                _ => null,
            }
            : null;

    // ---- replies --------------------------------------------------------------------------------

    private sealed record Reply(int Status, string ContentType, byte[] Body, (string Name, string Value)[]? Headers = null);

    private static Reply ResultReply(Result r) => JsonReply(r.Ok ? 200 : 400, new { ok = r.Ok, message = r.Message });

    private static Reply JsonReply(int status, object payload) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, Json));

    private static Reply Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static Reply MethodNotAllowed() => Text(405, "Method not allowed.");

    private static Reply Html(int status, string body) => new(status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
        $"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">"
        + $"<link rel=stylesheet href=\"/arena/style.css\"><title>Dare arenas</title><main class=panel>{body}</main>"));

    private Reply Static(string name) =>
        _static.TryGetValue(name, out var f) ? new Reply(200, f.Type, f.Body) : Text(404, "Not found.");

    private void TryWrite(HttpListenerContext ctx, Reply reply)
    {
        try
        {
            var res = ctx.Response;
            res.StatusCode = reply.Status;
            res.ContentType = reply.ContentType;
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Cache-Control"] = "no-store";
            if (reply.ContentType.StartsWith("text/html", StringComparison.Ordinal))
                res.Headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (reply.Headers is not null)
                foreach (var (n, v) in reply.Headers)
                    res.AppendHeader(n, v);
            res.ContentLength64 = ctx.Request.HttpMethod == "HEAD" ? 0 : reply.Body.Length;
            if (ctx.Request.HttpMethod != "HEAD")
                res.OutputStream.Write(reply.Body);
            res.Close();
        }
        catch (Exception ex)
        {
            if (!_stopped) _m.Log(LogLevel.Warn, $"Web response failed: {ex.Message}");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static Dictionary<string, (byte[], string)> LoadStatic()
    {
        var asm = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var name in new[] { "index.html", "arena.html", "app.js", "style.css", "staff.html", "staff.js", "map.html", "lvl.js", "lvlgen.js", "editor.js" }.Concat(AccountStaticFiles))
        {
            using var s = asm.GetManifestResourceStream("wwwroot/" + name)
                          ?? throw new InvalidOperationException($"embedded resource wwwroot/{name} is missing");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var type = Path.GetExtension(name) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "application/octet-stream",
            };
            map[name] = (ms.ToArray(), type);
        }
        return map;
    }
}
