# Foundation accounts — design

Date: 2026-11-23 · Status: draft for approval · Builds on: arena ownership (merged, live)

## Intent

Make Foundation's logins professional: one account per player, claimed on the website, that
works in game and on the site with the same name and password, backed by a verified
**recovery email**. No guests: every name in the zone belongs to a real account, so spam is
contained and every score belongs to someone.

Standalone now (no ISOJ dependency), with the door open to linking an ISOJ name later.

This is project 1 of 3. Project 2 is the visual site redesign; project 3 is the map editor
on the site. Both come after and use these accounts for sign-in.

## Goals

1. **Sign up on the site:** name + password + recovery email → a 6-digit one-time code is
   emailed → entering it creates the account and claims the name in game and on the site.
2. **One login everywhere:** name + password in Continuum, oss-client and the website.
3. **Recovery:** "Forgot password" emails a one-time code; the player sets a new password.
4. **No guests:** a name with no account cannot enter the zone. Continuum players are told
   where to claim it.
5. **Site sign-in to your arena:** the arena owner pages use the account session (no more
   need for `?weblogin`, which stays as a convenience).
6. **Migration:** names already claimed in the zone (e.g. `dare`) keep working with their
   current password and are asked to add a recovery email on first site sign-in.
7. **ISOJ-ready:** each account has an optional linked ISOJ name field, unused for now.

## Non-goals

- The site redesign and the map editor (projects 2 and 3).
- ISOJ linking itself, billing/biller protocol.
- Two-factor login, social logins, paid features.
- Changing scores/stats storage (scores stay keyed by name, which is now always an account).

## Anti-goals

- Storing or emailing passwords in plain text; logging them.
- Revealing whether a name or email is registered through error messages or timing
  ("If that account exists, we sent a code").
- Letting anyone hold a name without a verified email.
- Locking out the zone's own staff if the email service is down: existing accounts must
  still log in to the game without email.

## Constraints

- Lives in the existing `Dare.Arenas` plugin and its SQLite store (`data/dare-arenas.db`),
  on SubspaceServer v4.0.0. Rules go in `Dare.Arenas.Core`, unit-tested.
- Game clients send only name + password. The plugin implements the server's `IAuth`
  (replacing the `AuthFile` module in `Modules.config`); `AuthBan` stays in the chain.
- Refusals: Continuum gets `AuthCode.CustomText` ("This name isn't claimed. Claim it at
  subspacefoundation.com"); VIE clients get the closest standard code (builder to confirm
  which one oss-client shows sensibly, e.g. `NoPermission`).
- Names follow the server's existing rules (printable ASCII, the zone's length limit,
  case-insensitive uniqueness, the existing `Obscene` filter if loaded).
- Passwords: min 8 characters; hashed with PBKDF2-SHA256 (≥ 200,000 iterations, per-account
  salt) via `System.Security.Cryptography`; constant-time comparison.
- Email codes: 6 digits, valid 10 minutes, one use, max 5 wrong tries then void.
- Rate limits: code sends 1/min and 5/hour per email, 10/hour per IP; sign-ups 3/hour per
  IP; game logins: 10 wrong passwords per name per 10 minutes → refuse that name for 10
  minutes, applied to everyone including staff (logged; staff can still sign in on the site to reset). *(Controller call.)*
- Email delivery: **Resend** HTTP API from the server; API key in a server-only secret file
  (`/opt/subspace/zone/conf/dare-secrets.conf`, mode 600, never in the repo); sender
  `Foundation <no-reply@subspacefoundation.com>`; Chris adds Resend's DNS records (SPF,
  DKIM) in Cloudflare.
- Web: same plugin `HttpListener`; new pages under `/account/` (Caddy routes `/account/*`
  to the plugin like `/arena/*`); session cookie as today (`HttpOnly; Secure;
  SameSite=Lax`), same-origin checks on every POST.

## Approaches considered

- **A. Accounts inside the Foundation plugin (chosen).** One database, one set of rules; the
  plugin answers game logins and serves the account pages; arena pages share the session.
- **B. Separate web app writing the zone's password file.** Two systems to keep in sync;
  fragile file writes.
- **C. Hosted login service (Supabase/Clerk).** Polished, but every game login depends on an
  outside service, and tying it to game names is awkward.

Chris picked A.

## Design

### Data (new tables in the existing store)

- `accounts`: id, name (unique, case-insensitive, original casing kept), password_hash,
  password_salt, hash_iterations, recovery_email (normalized lower-case), email_verified,
  created, last_login, status (`active` | `locked`), isoj_name (nullable),
  legacy_hash (nullable — imported zone password, cleared after first successful login
  re-hashes it).
- `email_codes`: id, purpose (`signup` | `recovery` | `add-email`), email, account_id
  (nullable for signup), code_hash, expires, attempts, used.
- `pending_signups`: name, email, password_hash/salt (hashed at entry), expires — so the
  password is never held in clear while waiting for the code. A name is only reserved once
  the code is verified (first verified wins; pending entries expire in 10 minutes).
- `login_failures`: name, window start, count (game) — for the game lockout rule.

### Game login (IAuth)

Unknown name → refuse (custom text / standard code). Known + `status=active` + password
matches (PBKDF2, or the legacy zone hash for imported names, then upgraded) → OK with the
name authenticated (so the arena rules that need an authenticated name keep working).
Wrong password → `BadPassword` and count toward the lockout. Work happens off the mainloop
(hashing is slow) and `Done()` is called back on the mainloop as `IAuthRequest` requires.
In-game `?passwd`: removed from the default group's caps; players change passwords on the
site.

### Site pages (`/account/…`)

`signup` → `verify` (enter code) → done (signed in); `login`; `forgot` → `reset` (code +
new password); `/account` (profile: name, recovery email with change via code, change
password, "Linked ISOJ name: not linked", sign out). Arena owner pages accept the account
session. Pages are plain and functional in this project (styled like the current site);
the big visual redesign is project 2.

### Migration

On first deploy, import every `[Users]` entry from `conf/passwd.conf` as an account with
`legacy_hash`, no email, `email_verified = false`. These log in to the game as before. On
first site sign-in they must add a verified recovery email before anything else. All `[Users]`
entries are imported, including test names from the deploy checks; the deploy prints the list so
Chris can remove the test ones. *(Controller call.)* Keep a
backup of `passwd.conf`; `AuthFile` is removed from `Modules.config`.

### Email

A small `EmailSender` interface in Core with a Resend implementation in the plugin and a
fake for tests. Messages: plain text + minimal HTML, the code in large type, no links that
carry the code. If Resend is unreachable, the page says "couldn't send the code, try again
in a minute"; game logins are unaffected.

## Testing

- Core unit tests: name rules, password hashing/verification (incl. legacy), code
  generation/expiry/attempt limits, every rate limit, sign-up race (two people, same name),
  account-existence non-disclosure (same responses), migration import parsing.
- Local integration (local zone, port 6000, fake email sender that writes codes to the log):
  sign up on the site → log in to the game with that name/password (oss-bot) → wrong
  password refused and lockout after 10 → unknown name refused → forgot/reset works →
  imported legacy name logs in and is re-hashed → arena owner page works with the account
  session.
- Live: after deploy, Chris signs up a test account end to end with a real email.

## Left to the builder

Exact page wording and layout (within the current site style), table schemas, the VIE
refusal code choice (verify what oss-client displays), Resend request details, and how the
legacy zone hash format is verified (read `AuthFile` in v4.0.0).
