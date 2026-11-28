# Pilot Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Public `/pilot/<name>` pages, owner-editable, staff-moderated, linked from the site.

**Architecture:** Core `AccountStore.Profiles.cs` stores per-name profile settings and builds the
public view (privacy tested there). The plugin's `ProfileApi.cs` serves JSON + ship sheets under
`/account/api/profile*` and `profile.html` for `/pilot/*`. Caddy proxies `/pilot/*` (snippet part
added by `remote-install.sh`).

**Tech Stack:** C# / .NET 9, SQLite (Microsoft.Data.Sqlite), xUnit, vanilla JS.

**Spec:** `docs/superpowers/specs/2026-11-26-pilot-profiles-design.md`

## Global Constraints
- Bio ≤ 280 chars, plain text, control chars stripped, trimmed.
- Never return email, IP, last_login, account id, or sibling names.
- Profile is per exact name; owner = signed-in account with that id.
- Hidden-by-staff and unknown names answer identically (404, "No pilot by that name.").

## Review Focus
- Name with spaces/odd casing in URL (`/pilot/Big%20Chris`, `/pilot/big chris`) → finds the pilot, shows stored casing.
- Avatar/ship id belonging to another pilot → refused (store) and 404 (sheet).
- Deleted pilot name → profile row deleted; name re-claimed later starts blank.
- Bio with HTML → rendered as text (textContent), stored as given minus control chars.
- Hidden ships + avatar set → avatar sheet still served, slot ships not.

---

### Task 1: Profile store (Core)
**Files:** Create `src/Dare.Arenas.Core/AccountStore.Profiles.cs`; modify `AccountStore.cs` (table), `AccountStore.Pilots.cs` (delete row); test `tests/Dare.Arenas.Tests/PilotProfileTests.cs`.
**Produces:**
- `record PilotProfileSettings(string Bio, long? AvatarShip, bool ShowContribs, bool ShowArenas, bool ShowShips, bool ModHidden)`
- `record PublicPilot(string Name, DateTimeOffset Since, string Bio, long? Avatar, bool ShowContribs, bool ShowArenas, bool ShowShips, IReadOnlyList<ShipyardShip> Ships, bool Hidden)`
- `AccountStore.ProfileSettings(long accountId) -> PilotProfileSettings` (defaults: "", null, true×3, false)
- `AccountStore.SaveProfile(long accountId, string? bio, long? avatarShip, bool showContribs, bool showArenas, bool showShips) -> Result`
- `AccountStore.ModerateProfile(string name, bool? clearBio, bool? hidden) -> Result`
- `AccountStore.PublicPilot(string name, bool privileged) -> PublicPilot?` (null: unknown/inactive, or ModHidden && !privileged; Ships empty when !ShowShips)
- `AccountStore.ProfileShipSheet(string name, long shipId) -> byte[]?`
- [ ] Tests: defaults; save+read; bio >280 refused; avatar not theirs refused; public view has no sibling names; moderation hide → null unless privileged; clearBio; delete pilot drops row; ship sheet only slot/avatar ships, avatar even when ships hidden.
- [ ] Implement, `dotnet test --filter PilotProfile` green, commit.

### Task 2: Web API + route
**Files:** Create `src/Dare.Arenas/Web/ProfileApi.cs`; modify `AccountPages.cs` (dispatch `/profile*`, static files), `WebServer.cs` (`/pilot/*` → `profile.html`), `deploy/Caddyfile.snippet`, `deploy/remote-install.sh` (pilot part), `deploy/test-remote-install.sh`.
- GET `/account/api/profile?name=` adds `arenas` (public, non-private `OwnedArenas`, on main thread), `mine`, `staff` (`IsWebStaff`, account sessions).
- [ ] Build; remote-install test scenario shows one `handle /pilot/*`; commit.

### Task 3: Page + editing
**Files:** Create `wwwroot/profile.html`, `wwwroot/profile.js`; modify `wwwroot/style.css`, `wwwroot/account.html`/`account.js` (link "Your public profile").
- [ ] Signed out view, owner edit panel, staff moderation buttons; commit.

### Task 4: Links in
**Files:** `site/index.html` pilot menu ("Your profile"), `site/chat.js` player list, `wwwroot/app.js` arena owners/co-mods.
- [ ] Commit.

### Task 5: Verify + ship
- [ ] Full `dotnet test`; run plugin locally, check desktop + 375px; merge origin/main; push main.
