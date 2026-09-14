# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is **Intelimensa Accounts** — the account-management and BCI-configuration-distribution
service for [AxoSync](../axosync/) (sibling repo at `C:\_code\intelimensa\axosync`). It's a
standalone ASP.NET Core Razor Pages app, deployed independently of the company's WordPress
marketing site, intended to be reachable at `accounts.intelimensa.com`.

**Current scope**: there is no billing/subscription system. This exists to support a research
cohort using loaned MindStone BCI devices — the job of this service is account access control
and encrypted configuration delivery, not commerce. Don't add subscription/payment concepts
unless the project's scope actually changes.

**Status**: data model, EF Core/Identity plumbing, auth, and staff config-assignment are
implemented — cookie login for the web portal, `POST /api/auth/{login,refresh,logout}` issuing
JWT + refresh tokens for AxoSync (see "Auth flow" below), and a `/Staff` area for assigning
configs to accounts (see "Staff area" below). Config encryption/delivery and device registration
are still just the plan below, not implemented. Treat sections below as the plan to implement
except "Data model", "Auth flow", and "Staff area", which now reflect real code — check the files
directly if in doubt.

## Why this is a separate service (not a WordPress plugin)

The configuration files AxoSync loads encode the actual BCI signal-processing pipeline (filter
parameters, thresholds, calibration recipes) — they're Intelimensa's real IP, more so than the
(obfuscated but fairly generic) compiled binaries. This service is what decides who gets a copy
of that. It's kept fully separate from the WordPress site — different codebase, different
process, different attack surface — rather than built as a WP plugin, specifically because a
CMS plugin ecosystem is a much larger and constantly-changing attack surface than a small,
purpose-built service the team fully controls.

## Architecture

One app, two faces, sharing the same database and business logic:

- **Web portal** (Razor Pages) — humans use this in a browser. Self-service sign-up/login, and a
  read-only "your assigned configuration" status page. Users do **not** pick their own
  configuration here — see below.
- **JSON API** (same project, e.g. under `/api/...` or minimal-API endpoints alongside the Razor
  Pages) — the AxoSync desktop (and eventually mobile) client talks to this to log in, register
  its device, and fetch its encrypted configuration.

Do not split these into separate deployables. At this scale (a bounded cohort, no billing), one
project/one database/one deploy is the right amount of infrastructure.

## Key design decisions

- **Config assignment is staff-only, never user-selected.** A new account starts unconfigured.
  Someone on the Intelimensa side assigns the correct config after matching the account to the
  right participant/device/calibration. The portal shows the assignment; it doesn't let the user
  choose one. This matters because configs are calibration/hardware-specific — a wrong
  self-selected config would silently misconfigure someone's device. Implemented via a `/Staff`
  area — see "Staff area" below.
- **Per-account expiry (`ExpiresAt`).** The loaned-device cohort has a study end date; access
  should lapse on its own rather than depending on someone remembering to revoke every
  participant by hand.
- **No subscriptions/payments.** `Accounts.Status` is a simple active/revoked flag set by staff,
  not something driven by webhooks or payment events.

## Data model

Implemented — see `Intelimensa.Accounts/Models/` and `Intelimensa.Accounts/Data/ApplicationDbContext.cs`
rather than trusting this table to stay in sync:

| Table | Purpose |
|---|---|
| `AspNetUsers` (`ApplicationUser : IdentityUser`) | email, password hash (Argon2id), created_at. ASP.NET Core Identity owns this table — there's no separate hand-rolled `Users` table. |
| `Accounts` | user_id (1:1, unique), assigned_config_id (nullable), status (active/revoked), expires_at |
| `Devices` | one row per registered client machine — id (guid), account_id, public_key (nullable), platform, registered_at, last_seen_at, revoked |
| `Configs` | key (unique, matches AxoSync's config stems e.g. `ms2`/`ms5`/`biosemi`), display_name, current_version. No blob/CEK storage yet — that's a config-delivery-phase decision, not a data-model one. |

A fifth table, `RefreshTokens` (user_id, token_hash — SHA-256 of the raw token, never the raw value
itself, created_at, expires_at, revoked_at, replaced_by_token_id for rotation chains), backs the
JWT refresh flow below. Not yet tied to a `Device` row — that binding is future work once device
registration exists.

Password hashing is `Intelimensa.Accounts/Security/Argon2idPasswordHasher.cs`, an
`IPasswordHasher<ApplicationUser>` registered in `Program.cs` in place of Identity's default
PBKDF2 hasher (cost parameters and the self-describing hash-string format are documented in that
file's header comment). Identity is wired via `AddIdentityCore` (not `AddIdentity`) plus
`.AddSignInManager()`. Password policy favors length over composition rules (`RequiredLength =
12`, no digit/upper/symbol requirement) per current NIST 800-63B guidance, since Argon2id already
defends against offline cracking.

## Config delivery model (planned)

Configs are already AES-GCM encrypted (PBKDF2, 600k iterations) by AxoSync's own `ConfigGen` — see
`axosync/CLAUDE.md`'s "Configuration System" section, and the actual implementation in
`axosync/ConfigGen/Program.cs` (encrypt) / `axosync/AxoSync/Utilities/ConfigLoader.cs` (decrypt,
`DecryptResource`) for the exact format: `salt(32) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`,
PBKDF2-SHA256/600k/32-byte salt, AES-256-GCM/12-byte nonce/16-byte tag. Don't reinvent that
encryption scheme here — see `axosync/notes/accounts-service-integration.md` for the full
client-side design doc (written from this side, for AxoSync's agent to implement against once
this service's API exists) covering how the CEK below reuses this same binary layout.

The delivery design (envelope encryption, so the raw content-encryption key is never itself
transmitted in the clear):

1. Server generates/holds a random Content Encryption Key (CEK) per config (version).
2. The config payload is encrypted with the CEK using the existing AES-GCM format.
3. On login, over TLS, the server hands the client the CEK it needs (gated on that account being
   active, not expired, and entitled to that config) — either directly (baseline: trust TLS +
   authenticated session) or wrapped to a device-specific public key if a stronger transit
   guarantee is later warranted.
4. The client re-wraps whatever it persists locally (the decrypted config, or the CEK) using the
   host OS's secure-storage primitive before writing anything to disk, so a cached copy is tied
   to that specific device/user and isn't portable by copying the file elsewhere:
   - Windows (only platform actively shipping today): DPAPI (`ProtectedData`,
     `DataProtectionScope.CurrentUser`).
   - iOS/Android (near-future targets, not yet built): Keychain Services / Android Keystore
     respectively, behind the same abstraction shape — see `axosync/CLAUDE.md`'s platform
     abstraction convention (`SerialCommService`, `ISystemControlModule`) for the pattern this
     should follow when those heads exist.
   - No browser target is planned for AxoSync, so no browser-side secure storage is needed here.
5. Client caches the decrypted config for offline use, re-validating (refreshing) opportunistically
   when online; a refresh-token expiry drives an offline grace period rather than requiring the
   app to be online on every launch.

**Explicitly out of scope for this design**: preventing an authorized, licensed user from
extracting the plaintext config once their own copy of the app has decrypted it to run the
pipeline (e.g. via a memory dump on their own machine). No key-delivery or at-rest scheme solves
that — it's a runtime-hardening / anti-tamper / watermarking problem, tracked separately, not
something this service's API design can fix.

## Auth flow

Implemented — `Intelimensa.Accounts/Api/Auth/AuthEndpoints.cs` (`MapAuthEndpoints`) and
`Intelimensa.Accounts/Security/TokenService.cs`. Both the web portal (cookie auth) and the JSON
API (JWT) go through the same `UserManager`/`SignInManager<ApplicationUser>` password-verification
path, per the "one app, two faces, sharing the same ... business logic" architecture above.

- `POST /api/auth/login` (email + password) → short-lived JWT access token (15 min default,
  `Jwt:AccessTokenLifetimeMinutes`) + a longer-lived opaque refresh token (30 days default,
  `Jwt:RefreshTokenLifetimeDays`). Gated on `Account.Status == Active` and not expired — a
  revoked/expired account can't obtain a session at all, not just config access.
- `POST /api/auth/refresh` — rotates the refresh token (issues a replacement, revokes the one
  presented) and returns a fresh access token. A reused/revoked/expired refresh token is rejected.
- `POST /api/auth/logout` — revokes a refresh token.
- The refresh token is what the desktop client persists (DPAPI-wrapped locally per the config
  delivery model above) — its presence/validity is the mechanism behind the offline grace period.

**JWT signing key**: read from configuration (`Jwt:SigningKey`, base64), never committed. In
Development it comes from .NET User Secrets — one-time setup after cloning:

```bash
cd Intelimensa.Accounts
dotnet user-secrets set "Jwt:SigningKey" "<base64 value, e.g. from: openssl rand -base64 64>"
```

The app throws a clear startup error if `Jwt:SigningKey` is missing rather than falling back to a
default. `Jwt:Issuer`/`Jwt:Audience`/the two lifetime settings are non-secret and live in
`appsettings.json`.

Web portal pages live under `Pages/Account/` (`Login`, `Register`, `Logout`, `Status` — the "your
assigned configuration" status page from the architecture section above). The whole `/Account`
folder requires auth except `Login`/`Register` (`AuthorizeFolder`/`AllowAnonymousToPage` in
`Program.cs`). Note the `Models.Account` / `Pages.Account.*` namespace collision this creates —
code under `Pages/Account/` that needs the `Account` entity type imports it via
`using AccountEntity = Intelimensa.Accounts.Models.Account;` (a bare `Account` reference inside
the `Intelimensa.Accounts.Pages.Account` namespace resolves to the enclosing namespace itself,
not the model class — `CS0118`).

## Staff area

Implemented — `Pages/Staff/Index.cshtml` (assign a config / set status / set expiry per account,
one combined form per row) and `Pages/Staff/Configs.cshtml` (create/list `Config` rows). Gated by
a `"Staff"` ASP.NET Core Identity role and a matching `"Staff"` authorization policy
(`RequireRole("Staff")`), applied to the whole folder via
`options.Conventions.AuthorizeFolder("/Staff", "Staff")` in `Program.cs`. The `Staff` role itself
is seeded idempotently on every startup (`RoleManager.RoleExistsAsync`/`CreateAsync` right after
`builder.Build()`) so it always exists, even against a fresh DB.

**There is no UI to grant the `Staff` role** — with zero staff users there's nothing to gate a
"manage staff" page behind yet, so promotion is a manual one-time SQL step per person:

```sql
INSERT INTO AspNetUserRoles (UserId, RoleId)
SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
WHERE u.Email = 'someone@example.com' AND r.Name = 'Staff';
```

**Role claims are baked into the auth cookie at sign-in** (standard ASP.NET Core Identity
behavior) — a user promoted to `Staff` while already logged in won't see the effect until they log
out and back in. This tripped up manual testing once; don't mistake it for the grant not working.

`Pages/Staff/` sits in namespace `Intelimensa.Accounts.Pages.Staff` — no model type is named
`Staff`, so unlike `Pages/Account/` this doesn't hit the `CS0118` collision described above.

## Build Commands

The solution file is `.slnx` (the newer XML-based format), not `.sln` — there is no `.sln` file
in this repo.

```bash
dotnet build Intelimensa.Accounts.slnx
dotnet run --project Intelimensa.Accounts/Intelimensa.Accounts.csproj
```

No test project exists yet.

### EF Core

`dotnet-ef` is a local tool (`.config/dotnet-tools.json`) — run `dotnet tool restore` once after
cloning, then use it from `Intelimensa.Accounts/`:

```bash
dotnet ef migrations add <Name>
dotnet ef database update
```

The DB is SQLite (`accounts.db`, gitignored) via `ConnectionStrings:DefaultConnection` in
`appsettings.json`.

## Conventions

- Namespace: `Intelimensa.Accounts.*`, matching AxoSync's `Intelimensa.AxoSync.*` convention.
- This repo is independent of `axosync/` (separate git repo, separate solution) but should stay
  consistent with it in naming and in the encrypted-config format it produces/consumes.
