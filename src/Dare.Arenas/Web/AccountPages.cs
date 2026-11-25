using System.Diagnostics;
using System.Net;
using System.Text;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// Foundation account pages (/account/...): sign-up with an emailed code, sign-in, recovery,
// recovery-email change, password change. Everything here runs on the web worker threads: the
// AccountStore, WebAuth, the rate limiters and the email sender are thread-safe, and nothing
// touches server (mainloop) state.
// ---------------------------------------------------------------------------------------------
public sealed partial class ArenaOwnership
{
    internal const string SecretsPath = "conf/dare-secrets.conf";

    private static readonly Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    // Send limits (Global Constraints): code sends 1/min and 5/h per email, 10/h per IP;
    // sign-up starts 3/h per IP. Keys: send:email:<email>, send:ip:<ip>, signup:ip:<ip>.
    internal readonly RateLimiter SendPerEmailMinute = new(1, TimeSpan.FromMinutes(1), Clock);
    internal readonly RateLimiter SendPerEmailHour = new(5, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter SendPerIp = new(10, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter SignupPerIp = new(3, TimeSpan.FromHours(1), Clock);

    // Web sign-in and reset: 20 attempts per 10 minutes per IP (each costs a PBKDF2 run), and a web-only
    // lockout per (name, IP) that is separate from the game's lockout.
    internal readonly RateLimiter LoginPerIp = new(20, TimeSpan.FromMinutes(10), Clock);
    internal readonly RateLimiter ResetPerIp = new(20, TimeSpan.FromMinutes(10), Clock);
    internal readonly WebLoginGuard WebLogins = new(Clock);

    private IEmailSender? _email;

    internal IEmailSender Email => _email ?? throw new InvalidOperationException("Email sender is not set up.");
    internal AccountStore Accounts => _accounts ?? throw new InvalidOperationException("Account store is not open.");

    /// <summary>
    /// Resend when <c>conf/dare-secrets.conf</c> has <c>[Resend] ApiKey</c>. Without a key: DEBUG
    /// builds use the log-only sender (local testing); Release builds use a sender that always fails
    /// (the pages show the generic answer, nothing is logged but the failure) and log an Error once,
    /// so a codes-in-the-log leak is impossible. Plain file read at load; warns when the file is
    /// readable by everyone. Never logs the key.
    /// </summary>
    private void LoadEmailSender()
    {
        string? key = null, from = null;
        try
        {
            if (File.Exists(SecretsPath))
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(SecretsPath) & UnixFileMode.OtherRead) != 0)
                    Log(LogLevel.Warn, $"{SecretsPath} is readable by everyone; chmod 600 it.");
                var text = File.ReadAllText(SecretsPath);
                key = SecretsFile.Get(text, "Resend", "ApiKey");
                from = SecretsFile.Get(text, "Resend", "From");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not read {SecretsPath}: {ex.GetType().Name}.");
        }

        if (key is not null)
        {
            _email = new ResendEmailSender(key, from, Log);
            Log(LogLevel.Info, "Account emails are sent with Resend.");
        }
        else
        {
#if DEBUG
            _email = new LogEmailSender(Log);
            Log(LogLevel.Warn, $"Real email is OFF: no [Resend] ApiKey in {SecretsPath}. Account codes are written to this log instead (DEBUG build, local testing only).");
#else
            _email = new UnavailableEmailSender();
            Log(LogLevel.Error, $"No [Resend] ApiKey in {SecretsPath}: account emails cannot be sent, so sign-up, recovery and email changes will not work until the key is set (set-resend-key.sh).");
#endif
        }
    }
}

internal sealed partial class WebServer
{
    internal const string AccountPrefix = "/account";

    private const string TooMany = "Too many requests. Try again later.";
    private const string WaitAMinute = "Please wait a minute before asking for another code.";
    private const string Busy = "Server busy, try again.";
    private const string WrongLogin = "Wrong name or password.";
    private const string LockedOut = "Too many wrong passwords. Try again in 10 minutes.";
    private const string NeedEmail = "Add a recovery email first.";
    private const string NotSignedIn = "Not signed in.";

    // Sign-up, recovery and email-change answers take at least this long whether or not a code
    // was sent, so response time doesn't tell whether a name/email exists.
    private static readonly TimeSpan SendFloor = TimeSpan.FromMilliseconds(1200);

    // A real PBKDF2 hash to verify against for unknown names, so they take as long as known ones.
    private static readonly Lazy<(string Hash, string Salt, int Iter)> DummyCred = new(() => PasswordHasher.Hash("dummy-password-0"));

    private static readonly Dictionary<string, string> AccountPages = new(StringComparer.Ordinal)
    {
        ["/account"] = "account.html",
        ["/account/"] = "account.html",
        ["/account/signup"] = "account-signup.html",
        ["/account/verify"] = "account-verify.html",
        ["/account/login"] = "account-login.html",
        ["/account/forgot"] = "account-forgot.html",
        ["/account/reset"] = "account-reset.html",
        ["/account/account.js"] = "account.js",
        ["/account/style.css"] = "style.css",
    };

    internal static readonly string[] AccountStaticFiles =
        ["account.html", "account-signup.html", "account-verify.html", "account-login.html", "account-forgot.html", "account-reset.html", "account.js"];

    private static bool IsAccountPath(string path) => path == AccountPrefix || path.StartsWith(AccountPrefix + "/", StringComparison.Ordinal);

    private async Task<Reply> AccountRouteAsync(HttpListenerRequest req, string path)
    {
        var method = req.HttpMethod;
        if (AccountPages.TryGetValue(path, out var file))
            return Get(method, () => Static(file));

        if (!path.StartsWith(AccountPrefix + "/api/", StringComparison.Ordinal))
            return Text(404, "Not found.");
        var api = path[(AccountPrefix.Length + 4)..];

        if (api == "/me")
            return method == "GET" ? AccountMe(req) : MethodNotAllowed();
        if (api == "/pilots" && method == "GET")
            return await PilotsAsync(req).ConfigureAwait(false);
        if (api == "/pilots/settings" && method == "GET")
            return PilotSettings(req);
        if (method != "POST")
            return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused)
            return refused;

        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;

        // Password hashing is capped globally; a web request that can't get a slot in 5 s gives up.
        using var limit = PasswordHasher.WaitLimit(TimeSpan.FromSeconds(5));
        try
        {
            return await AccountApiAsync(req, api, body).ConfigureAwait(false);
        }
        catch (PasswordHasher.BusyException)
        {
            _m.Log(LogLevel.Warn, $"Account request {api} gave up: all password-hash slots busy.");
            return Bad(Busy, 503);
        }
    }

    private async Task<Reply> AccountApiAsync(HttpListenerRequest req, string api, System.Text.Json.JsonElement? body)
    {
        return api switch
        {
            "/signup" => await SignupAsync(req, body).ConfigureAwait(false),
            "/verify" => Verify(body),
            "/login" => Login(req, body),
            "/logout" => Logout(req),
            "/forgot" => await ForgotAsync(req, body).ConfigureAwait(false),
            "/reset" => Reset(req, body),
            "/email" => await AddEmailAsync(req, body).ConfigureAwait(false),
            "/email/verify" => VerifyEmail(req, body),
            "/password" => ChangePassword(req, body),
            "/gamepass" => GamePass(req),
            "/pilots/switch" => SwitchPilot(req, body),
            "/pilots/add" => AddPilot(req, body),
            "/pilots/settings" => SavePilotSettings(req, body),
            "/pilots/delete" => await DeletePilotAsync(req, body).ConfigureAwait(false),
            _ => Text(404, "Not found."),
        };
    }

    /// <summary>Every account POST: same-origin (Origin = the public site) and the anti-CSRF header. JSON is checked when the body is read.</summary>
    private Reply? AccountPostCheck(HttpListenerRequest req)
    {
        var origin = req.Headers["Origin"];
        if (!WebPaths.IsAllowedOrigin(origin, _m.WebBaseUrl))
        {
            _m.Log(LogLevel.Info, $"Web POST {req.Url?.AbsolutePath} refused: origin '{origin}' is not this site.");
            return JsonReply(403, new { ok = false, message = "Request refused (not from this site)." });
        }
        if (req.Headers[CsrfHeader] != CsrfValue)
            return JsonReply(403, new { ok = false, message = "Missing request header." });
        return null;
    }

    private static string ClientIp(HttpListenerRequest req) => WebPaths.ClientIp(req.RemoteEndPoint?.Address, req.Headers["X-Forwarded-For"]);

    /// <summary>The signed-in account (account sessions only; ?weblogin sessions don't count here).</summary>
    private (string Session, Account Account)? CurrentAccount(HttpListenerRequest req)
    {
        var session = req.Cookies[WebPaths.SessionCookieName]?.Value;
        if (string.IsNullOrEmpty(session)) return null;
        if (_m.WebAuth.SessionFor(session) is not { AccountId: { } id }) return null;
        var account = _m.Accounts.Get(id);
        return account is null ? null : (session, account);
    }

    private static Reply NotSignedInReply() => JsonReply(401, new { ok = false, message = NotSignedIn });
    private static Reply NeedEmailReply() => JsonReply(403, new { ok = false, message = NeedEmail, needsEmail = true });

    private static Reply Ok(string message) => JsonReply(200, new { ok = true, message });

    private static Reply Bad(string message, int status = 400) => JsonReply(status, new { ok = false, message });

    /// <summary>Takes the send limits for one code email to <paramref name="email"/>; null when allowed.</summary>
    private Reply? TakeSendLimits(string email, string ip)
    {
        if (!_m.SendPerEmailMinute.TryTake("send:email:" + email)) return Bad(WaitAMinute, 429);
        if (!_m.SendPerEmailHour.TryTake("send:email:" + email)) return Bad(TooMany, 429);
        if (!_m.SendPerIp.TryTake("send:ip:" + ip)) return Bad(TooMany, 429);
        return null;
    }

    /// <summary>
    /// Emails <paramref name="code"/> (nothing when it is empty: the name/email exists or is unknown,
    /// and the answer is the same). A failed send cancels the code so it can't be used and holds nothing,
    /// and is answered exactly like a success (the failure is only logged).
    /// </summary>
    private async Task<Reply> SendCodeAsync(CodePurpose purpose, string email, string code, string? name, long? accountId, Result started, Stopwatch timer, int pilots = 1)
    {
        Reply reply;
        if (code.Length == 0)
        {
            _m.Log(LogLevel.Info, $"{purpose} code for {EmailMessages.Mask(email)} not sent (name taken, email unknown, or email at its pilot limit).");
            reply = JsonReply(200, new { ok = true, message = started.Message });
        }
        else
        {
            var (subject, text, html) = EmailMessages.Code(purpose, code, name, pilots);
            bool sent;
            try
            {
                sent = await _m.Email.SendAsync(email, subject, text, html, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _m.Log(LogLevel.Warn, $"Email to {EmailMessages.Mask(email)} failed: {ex.GetType().Name}.");
                sent = false;
            }

            if (sent)
            {
                _m.Log(LogLevel.Info, $"Sent {purpose} code to {EmailMessages.Mask(email)}.");
                reply = JsonReply(200, new { ok = true, message = started.Message });
            }
            else
            {
                // Same answer as a success: a distinguishing status would tell registered from unregistered.
                _m.Accounts.CancelPending(purpose, email, accountId);
                _m.Log(LogLevel.Warn, $"{purpose} code for {EmailMessages.Mask(email)} could not be sent; cancelled.");
                reply = JsonReply(200, new { ok = true, message = started.Message });
            }
        }

        var left = SendFloor - timer.Elapsed;
        if (left > TimeSpan.Zero) await Task.Delay(left).ConfigureAwait(false);
        return reply;
    }

    private Reply Signed(Account account, string message)
    {
        var session = _m.WebAuth.IssueSession(account.Name, account.Id);
        return new Reply(200, "application/json; charset=utf-8",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { ok = true, message, name = account.Name, needsEmail = !account.EmailVerified }, Json),
            [("Set-Cookie", WebPaths.SessionCookie(session, secure: true))]);
    }

    // ---- handlers -------------------------------------------------------------------------------

    private async Task<Reply> SignupAsync(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        var name = Str(body, "name") ?? "";
        var password = Str(body, "password") ?? "";
        var email = Str(body, "email") ?? "";
        if (!AccountRules.TryName(name, out var n, out var err)) return Bad(err);
        if (!AccountRules.TryPassword(password, out err)) return Bad(err);
        if (!AccountRules.TryEmail(email, out var e, out err)) return Bad(err);

        var ip = ClientIp(req);
        if (!_m.SignupPerIp.TryTake("signup:ip:" + ip)) return Bad(TooMany, 429);
        if (TakeSendLimits(e, ip) is { } limited) return limited;

        var timer = Stopwatch.StartNew();
        var started = _m.Accounts.StartSignup(name, password, e, out var code);
        if (!started.Ok) return Bad(started.Message);
        return await SendCodeAsync(CodePurpose.Signup, e, code, n, null, started, timer).ConfigureAwait(false);
    }

    private Reply Verify(System.Text.Json.JsonElement? body)
    {
        var email = Str(body, "email") ?? "";
        var code = Str(body, "code") ?? "";
        var r = _m.Accounts.VerifySignup(email, code, out var account);
        if (!r.Ok || account is null) return Bad(r.Message);
        _m.Log(LogLevel.Info, $"Account created on the web: '{account.Name}' ({EmailMessages.Mask(account.Email ?? "")}).");
        return Signed(account, r.Message);
    }

    private Reply Login(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        var name = (Str(body, "name") ?? "").Trim();
        var password = Str(body, "password") ?? "";
        var ip = ClientIp(req);
        if (!_m.LoginPerIp.TryTake("login:ip:" + ip)) return Bad(TooMany, 429);
        if (name.Length == 0 || password.Length == 0) return Bad(WrongLogin);
        var shown = AccountRules.TryName(name, out var valid, out _) ? $"'{valid}'" : "(invalid name)";

        // The web has its own lockout per (name, IP); it never touches the game's lockout.
        if (_m.WebLogins.IsLocked(name, ip))
        {
            _m.Log(LogLevel.Info, $"Web sign-in refused for {shown}: web lockout.");
            return Bad(LockedOut, 403);
        }

        var result = _m.Accounts.CheckWebLogin(name, password, out var account);
        switch (result)
        {
            case LoginResult.Ok when account is not null:
                _m.WebLogins.Success(name, ip);
                _m.Log(LogLevel.Info, $"Web sign-in for account '{account.Name}'{(account.EmailVerified ? "" : " (no recovery email yet)")}.");
                return Signed(account, account.EmailVerified ? "Signed in." : "Signed in. Add a recovery email to continue.");
            case LoginResult.UnknownName or LoginResult.Disabled:
                // as slow as a real check, so the answer time doesn't tell which names exist
                var d = DummyCred.Value;
                PasswordHasher.Verify(password, d.Hash, d.Salt, d.Iter);
                _m.WebLogins.Failure(name, ip);
                _m.Log(LogLevel.Info, $"Web sign-in refused for {shown}: {result}.");
                return Bad(WrongLogin);
            default:
                _m.WebLogins.Failure(name, ip);
                _m.Log(LogLevel.Info, $"Web sign-in refused for {shown}: {result}.");
                return Bad(WrongLogin);
        }
    }

    private Reply Logout(HttpListenerRequest req)
    {
        var session = req.Cookies[WebPaths.SessionCookieName]?.Value;
        if (!string.IsNullOrEmpty(session))
        {
            var who = _m.WebAuth.PlayerFor(session);
            _m.WebAuth.Revoke(session);
            if (who is not null) _m.Log(LogLevel.Info, $"Web sign-out for '{who}'.");
        }
        return new Reply(200, "application/json; charset=utf-8",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { ok = true, message = "Signed out." }, Json),
            [("Set-Cookie", WebPaths.ClearSessionCookie())]);
    }

    private async Task<Reply> ForgotAsync(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        var email = Str(body, "email") ?? "";
        if (!AccountRules.TryEmail(email, out var e, out var err)) return Bad(err);
        if (TakeSendLimits(e, ClientIp(req)) is { } limited) return limited;

        var timer = Stopwatch.StartNew();
        var started = _m.Accounts.StartRecovery(e, out var code, out var name, out var pilots);
        if (!started.Ok) return Bad(started.Message);
        return await SendCodeAsync(CodePurpose.Recovery, e, code, name, null, started, timer, pilots).ConfigureAwait(false);
    }

    private Reply Reset(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (!_m.ResetPerIp.TryTake("reset:ip:" + ClientIp(req))) return Bad(TooMany, 429);
        var email = Str(body, "email") ?? "";
        var r = _m.Accounts.CompleteRecovery(email, Str(body, "code") ?? "", Str(body, "password") ?? "",
            Str(body, "name"), out var changed);
        if (!r.Ok) return Bad(r.Message);
        if (changed is not null)
        {
            _m.WebAuth.RevokeAllFor(changed); // a reset signs out every web session of that pilot only
            _m.Log(LogLevel.Info, $"Password reset by email for '{changed}'.");
        }
        return Ok(r.Message);
    }

    /// <summary>
    /// A single-use game pass for the signed-in pilot (the site's Play button): the game sends it as
    /// the password and the zone takes it once, within a minute (AccountStore.RedeemGamePass).
    /// Account sessions only, behind the same Origin and header checks as every account POST.
    /// </summary>
    private Reply GamePass(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var pass = _m.Accounts.IssueGamePass(cur.Account.Id);
        if (pass is null) return JsonReply(403, new { ok = false, message = "This account can't sign in." });
        _m.Log(LogLevel.Info, $"Game pass issued for '{cur.Account.Name}' ({ClientIp(req)}).");
        // the name's own keys and macros travel with the pass (the game applies them at start)
        return JsonReply(200, new { ok = true, name = cur.Account.Name, pass, settings = _m.Accounts.PilotSettings(cur.Account.Id) });
    }

    // ---- pilot names on one email ------------------------------------------------------------

    /// <summary>The signed-in pilot's names (same confirmed email) and what each owns.</summary>
    private async Task<Reply> PilotsAsync(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var pilots = _m.Accounts.PilotsOf(cur.Account.Id);
        var holdings = await OnMain(() => _m.PilotHoldings(pilots)).ConfigureAwait(false);
        return JsonReply(200, new
        {
            ok = true,
            current = cur.Account.Name,
            emailVerified = cur.Account.EmailVerified,
            max = AccountStore.MaxAccountsPerEmail,
            pilots = holdings,
        });
    }

    /// <summary>One of the signed-in pilot's names' keys and macros (<c>?name=</c>, default the current one).</summary>
    private Reply PilotSettings(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var wanted = req.QueryString["name"];
        var pilot = string.IsNullOrWhiteSpace(wanted) ? cur.Account : _m.Accounts.PilotOf(cur.Account.Id, wanted);
        if (pilot is null) return Bad("That isn't one of your names.");
        return JsonReply(200, new { ok = true, name = pilot.Name, text = _m.Accounts.PilotSettings(pilot.Id) });
    }

    /// <summary>Saves one of the signed-in pilot's names' keys and macros: {"name", "text"}.</summary>
    private Reply SavePilotSettings(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var pilot = _m.Accounts.PilotOf(cur.Account.Id, Str(body, "name") ?? cur.Account.Name);
        if (pilot is null) return Bad("That isn't one of your names.");
        var r = _m.Accounts.SavePilotSettings(pilot.Id, Str(body, "text") ?? "");
        return r.Ok ? Ok(r.Message) : Bad(r.Message);
    }

    /// <summary>Switches the site session to another name on the same confirmed email (no password).</summary>
    private Reply SwitchPilot(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var target = _m.Accounts.PilotOf(cur.Account.Id, Str(body, "name") ?? "");
        if (target is null) return Bad("That isn't one of your names.");
        _m.WebAuth.Revoke(cur.Session);
        _m.Log(LogLevel.Info, $"Web pilot switch: '{cur.Account.Name}' -> '{target.Name}' ({ClientIp(req)}).");
        return Signed(target, $"Flying as {target.Name}.");
    }

    /// <summary>Adds a name to the signed-in pilot's email, with the same password.</summary>
    private Reply AddPilot(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var r = _m.Accounts.AddPilot(cur.Account.Id, Str(body, "name") ?? "", out var added);
        if (!r.Ok || added is null) return Bad(r.Message);
        _m.Log(LogLevel.Info, $"Pilot name '{added.Name}' added by '{cur.Account.Name}' ({ClientIp(req)}).");
        return JsonReply(200, new { ok = true, message = r.Message, name = added.Name });
    }

    /// <summary>
    /// Deletes one of the signed-in pilot's names: {"name", "keep": {"arena": "other name", ...}}
    /// (arenas and drafts not listed are archived). Deleting the name signed in now switches the
    /// session to another of the pilot's names.
    /// </summary>
    private async Task<Reply> DeletePilotAsync(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var name = Str(body, "name") ?? "";
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body is { } b && b.TryGetProperty("keep", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in k.EnumerateObject())
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Name.Length <= 32)
                    keep[p.Name] = p.Value.GetString() ?? "";
        var before = _m.Accounts.PilotsOf(cur.Account.Id);
        Account? deleted = null;
        var r = await OnMain(() => _m.DeletePilotName(cur.Account.Id, name, keep, out deleted)).ConfigureAwait(false);
        if (!r.Ok || deleted is null) return Bad(r.Message);
        // deleted the name signed in now: carry on as another of the pilot's names
        if (deleted.Id == cur.Account.Id && before.FirstOrDefault(a => a.Id != deleted.Id) is { } next
            && _m.Accounts.Get(next.Id) is { } still)
            return Signed(still, r.Message);
        return Ok(r.Message);
    }

    private Reply AccountMe(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var a = cur.Account;
        return JsonReply(200, new
        {
            ok = true,
            name = a.Name,
            email = a.Email,
            emailVerified = a.EmailVerified,
            needsEmail = !a.EmailVerified,
            isojName = a.IsojName,
            created = a.Created,
            lastLogin = a.LastLogin,
        });
    }

    private async Task<Reply> AddEmailAsync(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var email = Str(body, "email") ?? "";
        if (!AccountRules.TryEmail(email, out var e, out var err)) return Bad(err);
        if (TakeSendLimits(e, ClientIp(req)) is { } limited) return limited;

        var timer = Stopwatch.StartNew();
        var started = _m.Accounts.StartAddEmail(cur.Account.Id, e, out var code);
        if (!started.Ok) return Bad(started.Message);
        return await SendCodeAsync(CodePurpose.AddEmail, e, code, cur.Account.Name, cur.Account.Id, started, timer).ConfigureAwait(false);
    }

    private Reply VerifyEmail(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var r = _m.Accounts.VerifyAddEmail(cur.Account.Id, Str(body, "code") ?? "");
        if (r.Ok) _m.Log(LogLevel.Info, $"Recovery email set for '{cur.Account.Name}'.");
        return r.Ok ? Ok(r.Message) : Bad(r.Message);
    }

    private Reply ChangePassword(HttpListenerRequest req, System.Text.Json.JsonElement? body)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        if (!cur.Account.EmailVerified) return NeedEmailReply();
        var r = _m.Accounts.ChangePassword(cur.Account.Id, Str(body, "current") ?? "", Str(body, "next") ?? "");
        if (!r.Ok) return Bad(r.Message);
        _m.WebAuth.RevokeAllFor(cur.Account.Name, except: cur.Session); // other browsers are signed out
        _m.Log(LogLevel.Info, $"Password changed on the web for '{cur.Account.Name}'.");
        return Ok(r.Message);
    }
}
