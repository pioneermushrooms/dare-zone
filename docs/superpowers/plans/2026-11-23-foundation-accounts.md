# Foundation Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One email-backed Foundation account per player, claimed on the site, used for game and site login; no guests.

**Architecture:** Rules and storage in `Dare.Arenas.Core` (unit-tested): validation, password hashing (PBKDF2 + legacy zone hash), an `AccountStore` on the existing SQLite file, email-code flows and limits. The `Dare.Arenas` plugin adds an `IAuth` module that answers game logins from the store, a Resend email sender, and `/account/*` pages on its existing web server; the arena pages accept the account session.

**Tech Stack:** C# / .NET 9, SubspaceServer v4.0.0, Microsoft.Data.Sqlite 9.0.3 (server's copy), System.Security.Cryptography, System.Net.Http (Resend REST API), xUnit, Caddy.

**Spec:** `docs/superpowers/specs/2026-11-23-foundation-accounts-design.md`

## Global Constraints

- Names: 3–20 characters; letters, digits, space, `_`, `-`, `.`; must start with a letter; no leading/trailing/double spaces; unique case-insensitively (stored with original casing). (Legacy zone hash uses the lower-cased name truncated to 23.)
- Passwords: 8–31 characters (Continuum sends at most 31), printable ASCII.
- Password hashing: PBKDF2-SHA256, 210,000 iterations, 16-byte random salt, 32-byte output; constant-time compare (`CryptographicOperations.FixedTimeEquals`).
- Legacy hash (imported `passwd.conf` `[Users]`): SHA-512 over a 56-byte buffer = lower-cased name (≤23 chars, Latin-1) in bytes 0–23 + password (≤31 chars) in bytes 24–55, zero-padded; Base64 (no line breaks). On a successful legacy login, re-hash with PBKDF2 and clear the legacy hash.
- Recovery email: trimmed, lower-cased, ≤ 254 chars, one `@`, a dot in the domain part; one account per email.
- Codes: 6 digits from `RandomNumberGenerator.GetInt32(0, 1_000_000)`, zero-padded; stored as SHA-256 hash; valid 10 minutes; one use; 5 wrong tries voids the code.
- Limits: code sends 1 / minute and 5 / hour per email, 10 / hour per IP; sign-up starts 3 / hour per IP; game logins: 10 wrong passwords per name within 10 minutes → refuse that name for 10 minutes (everyone, staff included).
- Non-disclosure: sign-up, recovery and email-change responses are identical whether or not a name/email exists ("If that's right, we sent a code to …").
- Email: Resend REST (`POST https://api.resend.com/emails`, bearer key); key read from `conf/dare-secrets.conf` (`[Resend] ApiKey = …`, file mode 600, never in the repo); from `Foundation <no-reply@mail.subspacefoundation.com>`; local/testing uses a log-only sender.
- Game refusals: Continuum → `AuthCode.CustomText` "This name isn't claimed. Claim it at subspacefoundation.com" (unknown name) / "Too many wrong passwords. Try again in 10 minutes." (lockout); VIE clients → `AuthCode.NoPermission` for both; wrong password → `AuthCode.BadPassword`.
- Web: `/account/*` on the plugin's `HttpListener` (Caddy `handle_path /account/*` → `127.0.0.1:8088` with the account handler prefix), session cookie `dare_session` (`HttpOnly; Secure; SameSite=Lax; Path=/`), same-origin + JSON content-type checks on every POST, HTML-encode all user text.
- Plugin stays on the v4.0.0 server; `AuthFile` is replaced in `Modules.config`; `AuthBan` remains.

## Review Focus

- Names differing only by case (`Dare` vs `dare`) or with spaces (`Big Chris`) — same account in game and on site. → Task 2 test `same_account_any_case`.
- Two people verifying sign-up codes for the same name at once — exactly one gets it, the other a clear "name taken". → Task 2 test `signup_race_first_verified_wins`.
- Email typed with different case / spaces (`Me@X.com `) — treated as the same address everywhere. → Task 1 test `email_normalized`.
- Imported name with a password longer than 31 chars or name longer than 23 — legacy verification matches the zone's old truncation. → Task 1 test `legacy_hash_truncates_like_zone`.
- Email service down during sign-up or recovery — friendly error, no half-created account, code not consumed. → Task 4 test `send_failure_creates_nothing`.

---

### Task 1: Account rules and password hashing

**Files:** Create `src/Dare.Arenas.Core/AccountRules.cs`, `src/Dare.Arenas.Core/PasswordHasher.cs`; tests `tests/Dare.Arenas.Tests/AccountRulesTests.cs`, `PasswordHasherTests.cs`.

**Interfaces:**
- Produces: `public static class AccountRules { bool TryName(string input, out string name, out string error); bool TryPassword(string input, out string error); bool TryEmail(string input, out string normalized, out string error); }`
- Produces: `public static class PasswordHasher { (string Hash, string Salt, int Iterations) Hash(string password); bool Verify(string password, string hash, string salt, int iterations); string LegacyHash(string name, string password); bool VerifyLegacy(string name, string password, string legacyHash); }` (hash/salt Base64).
- [ ] **Step 1: Failing tests:** valid/invalid names per constraints (incl. `" dare"`, `"a  b"`, `"1abc"`, 21 chars); passwords 7/8/31/32 chars and non-ASCII; `email_normalized` (`" Me@X.com "` → `me@x.com`), bad emails (`a@b`, `@x.com`, two `@`); hash round-trip, wrong password false, two hashes of same password differ (salt); `legacy_hash_truncates_like_zone`: `LegacyHash("Dare", pw)` equals a known vector computed with the zone formula (compute the expected value in the test from the formula independently, e.g. build the 56-byte buffer and SHA512 in the test) and long name/password truncation gives the same hash as the truncated inputs.
- [ ] **Step 2–4:** run FAIL → implement → run PASS. **Step 5: Commit** `core: account rules and password hashing`.

### Task 2: Account store

**Files:** Create `src/Dare.Arenas.Core/AccountStore.cs`, `AccountModels.cs`; test `AccountStoreTests.cs`.

**Interfaces:**
- Produces (models): `public enum LoginResult { Ok, UnknownName, BadPassword, LockedOut, Disabled }`; `public sealed record Account(long Id, string Name, string? Email, bool EmailVerified, DateTimeOffset Created, DateTimeOffset? LastLogin, string? IsojName)`; `public enum CodePurpose { Signup, Recovery, AddEmail }`.
- Produces: `public sealed class AccountStore : IDisposable` ctor `(string dbPath, Func<DateTimeOffset> clock)` (same db file as `ArenaStore`, own connection, `BEGIN IMMEDIATE` per mutation) with:
  `Result StartSignup(string name, string password, string email, out string code)` (validates via `AccountRules`; refuses taken name/email silently-equivalent: returns Ok with `code` empty when the name or email is taken, so callers respond identically and send nothing),
  `Result VerifySignup(string email, string code, out Account? account)`,
  `LoginResult CheckLogin(string name, string password, out Account? account)` (handles legacy + rehash + lockout counting + `LastLogin`),
  `Result StartRecovery(string email, out string code, out string? name)`, `Result CompleteRecovery(string email, string code, string newPassword)`,
  `Result StartAddEmail(long accountId, string email, out string code)`, `Result VerifyAddEmail(long accountId, string code)`,
  `Result ChangePassword(long accountId, string current, string next)`,
  `int ImportLegacy(IEnumerable<(string Name, string Hash)> users)` (skips existing names, `lock`/`any` entries),
  `Account? Find(string name)`, `Account? Get(long id)`.
  Uses `Result` from `Models.cs`. Codes returned only so the caller can email them; only hashes are stored.
- [ ] **Step 1: Failing tests** (temp db, fake clock): sign-up → verify creates account, login Ok; `same_account_any_case`; `signup_race_first_verified_wins` (two pending sign-ups same name via two store instances; first verify Ok, second fails "name taken"); taken email/name → Ok with empty code; code expiry 10 min, single use, 5 wrong tries void; lockout: 10 bad passwords → `LockedOut` for 10 min, then Ok with right password; legacy import → login with old password Ok and hash upgraded (second login works with PBKDF2 path); recovery changes password and old one fails; add-email for imported account; change password needs current.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5: Commit** `core: account store`.

### Task 3: Game login (IAuth) and in-game changes

**Files:** Create `src/Dare.Arenas/FoundationAuth.cs`; modify `src/Dare.Arenas/ArenaOwnership.cs` (register/unregister), `deploy/` (`Modules.config` edit in `apply-local.sh`/remote install: replace `SS.Core.Modules.AuthFile` line with nothing — `FoundationAuth` registers `IAuth`; remove `cmd_passwd` from the default group; `default-caps` unchanged otherwise).

**Interfaces:**
- Consumes: `AccountStore.CheckLogin`. Produces: `public sealed class FoundationAuth : IModule, IAuth` (or a component registered by `ArenaOwnership` — implementer chooses, documents) answering `IAuth.Authenticate(IAuthRequest)`: read name/password from `request.LoginPacket`, run `CheckLogin` off the mainloop (`Task.Run`), then on the mainloop (`IMainloop.QueueMainWorkItem`) set `Result` (`Authenticated = true`, name/send-name = account's stored casing on Ok; refusal codes per Global Constraints, CustomText only for Continuum logins) and call `Done()`.
- [ ] **Step 1: Local integration** on the local zone (port 6000): import the local `passwd.conf` users, sign up a test account through `AccountStore` via a small test hook or by calling the web endpoint from Task 4 if built — implementer may add a sysop-only `?acctcreate <name> <pw>` **only in DEBUG builds** for local testing; show: known name + right password logs in (oss-bot), wrong password refused, unknown name refused, lockout after 10, imported legacy name logs in, arena ownership flows still see the name as authenticated.
- [ ] **Step 2: Commit** `plugin: Foundation game login`.

### Task 4: Email and account web pages

**Files:** Create `src/Dare.Arenas.Core/EmailMessages.cs` (+ test `EmailMessagesTests.cs`), `src/Dare.Arenas.Core/IEmailSender.cs`, `src/Dare.Arenas/ResendEmailSender.cs`, `src/Dare.Arenas/LogEmailSender.cs`, `src/Dare.Arenas/Web/AccountPages.cs`, `src/Dare.Arenas/Web/wwwroot/account*.html` (+ shared css/js); modify `src/Dare.Arenas/Web/WebServer.cs`.

**Interfaces:**
- Produces: `public interface IEmailSender { Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct); }`; `public static class EmailMessages { (string Subject, string Text, string Html) Code(CodePurpose purpose, string code, string? name); }` (code in large type, no links containing the code).
- Sender choice: `ResendEmailSender` when `conf/dare-secrets.conf` has `[Resend] ApiKey`, else `LogEmailSender` (logs "email to x: code 123456" at Info — local only; log a Warning at load that real email is off).
- Endpoints (JSON; pages call them): `POST /account/api/signup {name,password,email}` → always `{ok:true,message}`; `POST /account/api/verify {email,code}` → signs in (cookie); `POST /account/api/login {name,password}` (uses `CheckLogin`, same lockout); `POST /account/api/logout`; `POST /account/api/forgot {email}`; `POST /account/api/reset {email,code,password}`; `GET /account/api/me`; `POST /account/api/email {email}` / `POST /account/api/email/verify {code}`; `POST /account/api/password {current,next}`. Pages: `/account/signup`, `/account/verify`, `/account/login`, `/account/forgot`, `/account/reset`, `/account/` (profile; imported accounts without email are forced to add one first; shows "Linked ISOJ name: not linked").
- Arena pages accept the account session (`dare_session` maps to the account name) in addition to `?weblogin` sessions.
- Limits per Global Constraints via `RateLimiter` (keys `send:email`, `send:ip`, `signup:ip`).
- [ ] **Step 1: Failing tests** for `EmailMessages` (each purpose contains the code, no URL contains it) and `send_failure_creates_nothing` (Core-level: a signup whose send fails is cancelled — expose `AccountStore.CancelSignup(email)` or make send part of the flow; implementer picks, test pins: after a failed send, verifying with that code fails and the name is not reserved).
- [ ] **Step 2: Local integration:** with `LogEmailSender`, curl the full sign-up → read code from the log → verify → login → oss-bot game login with the new account → forgot/reset → profile email change; CSRF (foreign Origin) refused; identical responses for existing/non-existing email; arena page works with the account cookie.
- [ ] **Step 3: Commit** `plugin: account pages and email`.

### Task 5: Migration, deploy and docs

**Files:** Modify `deploy/remote-install.sh`, `deploy/deploy.sh`, `deploy/Caddyfile.snippet`, `deploy/test-remote-install.sh`; create `deploy/set-resend-key.sh`; modify `docs/arena-owners.md`, `site/index.html`, `site/arena-owners.html`.

- [ ] **Step 1:** Install path: on first run with `FoundationAuth`, the plugin imports `conf/passwd.conf` `[Users]` once (marker in store) and logs the imported names; remote install backs up `passwd.conf`, removes the `AuthFile` module line (rollback restores it), adds Caddy `handle_path /account/*` with the same `header_up Host` as `/arena/*`.
- [ ] **Step 2:** `deploy/set-resend-key.sh`: run by Chris; `ssh`es to the server and prompts (no echo) for the key, writes `conf/dare-secrets.conf` owned by `subspace`, mode 600, then restarts the zone. Never prints the key.
- [ ] **Step 3:** Docs/site: "Claim your name" link on the homepage → `/account/signup`; owner guide updated (sign in on the site; `?passwd` removed; recovery email).
- [ ] **Step 4:** Harness scenarios for the new install steps pass; full suite passes. **Commit** `deploy: Foundation accounts`.
- [ ] **Step 5 (needs Chris):** deploy, Chris runs `set-resend-key.sh`, live end-to-end sign-up with a real email, game login with it.
