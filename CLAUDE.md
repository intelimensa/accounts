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
configs to accounts (see "Staff area" below). Config delivery's server side is built on the
`remote-config` branch (see "Config delivery" below and `docs/config-management.md`); AxoSync's side isn't
yet. Check the files directly if in doubt.

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
- **No subscriptions/payments.** `Accounts.Status` is a simple Active/Revoked/Pending flag set by staff,
  not something driven by webhooks or payment events.

## Data model

Implemented — see `Intelimensa.Accounts/Models/` and `Intelimensa.Accounts/Data/ApplicationDbContext.cs`
rather than trusting this table to stay in sync:

| Table | Purpose |
|---|---|
| `AspNetUsers` (`ApplicationUser : IdentityUser`) | email, password hash (Argon2id), created_at. ASP.NET Core Identity owns this table — there's no separate hand-rolled `Users` table. |
| `Accounts` | user_id (1:1, unique), status (active/revoked), expires_at. Config assignment is per registered unit (`AccountDevice.AssignedConfigId`), not per account. |
| `ClientInstalls` | one row per registered AxoSync installation (not a unit: a *device* here is an Intelimensa unit, `BciDevices`) — its signing and agreement public keys, key thumbprint (unique per account), platform, name, app version, slot, registered/last-seen/revoked. Created only at a password login that carries the keys. |
| `Configs` | key (unique, matches AxoSync's config stems e.g. `ms2`/`ms5`/`biosemi`), display_name, `IsDefault`, `RetiredAt`, and once uploaded the blob's storage key/SHA-256/size and the CEK wrapped under the KEK. |
| `ConfigKeyIssuances` | the audit trail of delivered config keys: account, install, pairing, config, blob SHA-256, build, lease end. |

A fifth table, `RefreshTokens` (user_id, token_hash — SHA-256 of the raw token, never the raw value
itself, created_at, expires_at, revoked_at, replaced_by_token_id for rotation chains), backs the
JWT refresh flow below. A token from a login that registered an install is bound to it (`ClientInstallId`).

Password hashing is `Intelimensa.Accounts/Security/Argon2idPasswordHasher.cs`, an
`IPasswordHasher<ApplicationUser>` registered in `Program.cs` in place of Identity's default
PBKDF2 hasher (cost parameters and the self-describing hash-string format are documented in that
file's header comment). Identity is wired via `AddIdentityCore` (not `AddIdentity`) plus
`.AddSignInManager()`. Password policy favors length over composition rules (`RequiredLength =
12`, no digit/upper/symbol requirement) per current NIST 800-63B guidance, since Argon2id already
defends against offline cracking.

## Config delivery

The design, its reasoning and what is deferred (2FA, watermarking, the emergency lease, the browser) are in
[`docs/config-management.md`](docs/config-management.md); its last section, "As built (server)", lists what
the build settled. Read it before changing any of this. The server side, in short:

- **Client installs** (`Security/ClientInstallRegistry.cs`): AxoSync registers its two P-256 public keys at
  password login (`install` in the login body). At most `Configs:MaxInstallsPerAccount` (6) active installs per
  account (staff override per account), idle ones revoked after `Configs:InstallIdleDays`, the participant emailed
  for each new one and able to remove it on My Account. Tokens from that login are bound to the install.
- **Install proofs** (`Security/InstallProof.cs`, `InstallNonces.cs`, `InstallProofMiddleware.cs`): every request on a
  bound token is signed by the install over method, path, body hash and a server-issued nonce. Logins without an
  install (Quarry, publish scripts) still work and get unbound tokens, which can't fetch configs.
- **Configs** (`ConfigDelivery/`): the same roles as builds. A Publisher uploads ConfigGen's v2 blob plus its random
  CEK (`ConfigGen publish`, via `/api/publishing/configs`; or by hand on `/Staff/Configs`), it waits as the config's
  *pending* file, and Staff publish it (or the Publisher, with `--auto-publish`). `ConfigPublisher` holds the rules for
  both: test-decrypt the pair, store the blob in `IConfigStorage` (`Configs:StoragePath`), wrap the CEK under the KEK
  (`KeyEncryption`). The v2 format (`ConfigBlob`) has no PBKDF2.
- **Delivery** (`Api/ConfigDelivery/`, `ConfigIssuer`): `GET /api/devices/{serial}/config?version=&platform=` wraps the
  CEK to the install's agreement key and the calling build's release secret (`CekWrap`: ECDH + HKDF + AES-GCM) and
  returns it with a signed offline lease (`OfflineLease`, 7 days by default, staff override, never past `ExpiresAt`);
  `GET /api/configs/{key}/blob` serves the blob. Only published builds uploaded with `X-Release-Secret`, and not
  blocked on `/Staff/Releases`, get keys.
- **Switch:** without `Configs:KeyEncryptionKey` and `Configs:LeaseSigningKey` the server runs and delivery is off
  (503). Both are secrets, like `Jwt:SigningKey` (user-secrets locally, environment on the VPS); how to make them is in
  the doc. `Configs:DevelopmentReleaseSecret` (Debug builds, `version=development`) is refused outside Development.
- **Testing:** `scripts/test-server.sh start` turns delivery on with throwaway keys; `dotnet run scripts/config-delivery-smoke.cs`
  then plays AxoSync through the whole flow. The formats are pinned by `testdata/config-delivery-vectors.json`.

**Explicitly out of scope**: preventing an authorized participant from extracting the plaintext
config from their own running app (for example via a memory dump). No key-delivery scheme solves
that. The doc's threat-model section covers the mitigations, and explains why moving the pipeline
onto the device isn't viable on current hardware.

## The JSON API's contract with its clients

`Api/*/…Contracts.cs` are the wire shapes. They are pinned by `testdata/api-vectors.json` (generator `scripts/gen-api-vectors.py`), which `ApiVectorTests` round-trips through these records with ASP.NET's JSON options, and which MindStoneQuarry's client and AxoSync's hand-written client are tested against (copies, kept identical by `./sync-vectors.sh --fix` in the workspace root). **Changing a record or its JSON (a field, a name, an enum, a date format) means changing the generator first,** regenerating, syncing the copies and updating the clients; the tests say which one disagrees. Error bodies differ by family (a bare JSON string for manufacturing and devices, `{"error": …}` for publishing and updates, ProblemDetails for a 503); the fixture records that too.

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
- With `install` in the login body (AxoSync), the tokens are bound to that client install: refresh then needs
  the install's proof, and so does every API call on the access token (`client_install` claim). See "Config delivery".
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

**Sign-up needs approval.** `Register` creates the account `Pending` (`AccountStatus.Pending = 2`, stored as an integer, so
no migration was needed and existing accounts stayed Active). `AccountPolicy.IsUsable` passes only `Active`, so a pending
account can't use `/Download`, the API login or anything else gated on it, and `Account/Status` says it is awaiting approval.
Staff approve on `/Staff` (pending accounts are listed first with an **Approve** button that also takes an optional expiry).
**Email confirmation.** `Register` emails a confirmation link (`Email/EmailConfirmation.cs`; Identity token, 3-day lifespan, link
to `/Account/ConfirmEmail`); `Account/Status` shows the state and a rate-limited resend (one per 2 minutes per user). An
unconfirmed user may still sign in to the web portal (to resend) but is Pending, and staff can't set an account Active
(Approve or the status select) until its address is confirmed, so confirmation is a precondition of approval and access
needs no second check. The migration `ConfirmExistingEmails` marked every pre-existing user confirmed. Mail goes through
`IMailSender`: `SmtpMailSender` (MailKit; `Email:Host/Port/User/Password/FromAddress/FromName`, 587 STARTTLS or 465) when
`Email:Host` is set, otherwise `LoggingMailSender`, which only logs the message and link (development, `scripts/test-server.sh`;
production warns at startup). Links use `App:PublicBaseUrl` (set in `appsettings.Production.json`; behind nginx the request
says `http`). Send through the cPanel host's SMTP, not from the VPS (SPF doesn't list the VPS). `Email:Password` is a secret:
user-secrets locally, `Email__Password` in the systemd unit.
There is deliberately no `Participant` role: roles are baked into the cookie/JWT, `Status`/`ExpiresAt` are read per request.

**Roles and the Administrator.** `Administrator` (policy `Administrator`, folder `/Admin`, seeded at startup) is the only role
that grants or revokes `Staff`, `Manufacturer` and `Publisher`, at `/Admin/Roles`. It can't change its own roles, and the UI
can't grant `Administrator` itself: that stays `scripts/setrole.sh --role Administrator`. Administrator doesn't imply Staff.
A role change updates the user's security stamp, and `Security/SecurityStampCheck` (cookie `OnValidatePrincipal`, on every
request) ends that user's web session at once; a JWT already issued keeps its roles until it expires (15 min). The stock
`SecurityStampValidator` isn't used (it signs out the two-factor schemes, which aren't registered).

**The first Administrator (and any role, without one) is granted by hand**: `scripts/setrole.sh --account <email> --role Administrator --enabled`,
or the SQL below, once per person:

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

## Device manufacturing & registration codes

Implemented server-side; the station app (MindStoneQuarry) and firmware side live in the
`firmware` repo (`MindStoneQuarry-design.md`, `DEVICE_IDENTITY.md`). Flow docs: `docs/manufacturer_registration_scenario.md`,
`docs/user_registration_scenario.md`.

- **`BciDevice` lifecycle** (`BciDeviceStatus`): `Reserved` (code issued, not confirmed) to
  `Manufactured` (registerable) or `Voided`. `RegistrationCodeHash` is a base64 SHA-256 of the
  normalized code (`Security/RegistrationCode.cs`); the plaintext is only ever returned by the
  manufacturing API's `reserve`/`rekey`. Legacy/backfilled units (staff form, CSV) are `Manufactured`
  with a null hash.
- **Manufacturing API** (`Api/Manufacturing/`, `/api/manufacturing/units/{reserve,confirm,void,rekey,firmware}` plus `GET /api/manufacturing/options`):
  JWT, gated by the `Manufacturer` role/policy (separate from `Staff`, seeded at startup, granted by
  manual SQL like Staff). Account status is re-checked on every call. Serials are stored and sent **dashless**,
  `PPPPRVAAAAAC` (`Manufacturing/SerialNumber.cs`, pinned against Quarry's copy by `testdata/serial-number-vectors.json`: change the format there first, `scripts/gen-serial-vectors.py`, then `./sync-vectors.sh --fix` in the workspace root); `SerialNumber.ToDisplay` gives the human-facing
  `PPPP-RVAA-AAAC` (API responses carry both as `serialNumber`/`serialNumberLabel`) and `TryNormalize`
  accepts label form/underscores/lowercase, which `BciDeviceLookup.FindBySerialAsync` uses for every
  serial input (exact match first, so legacy free-form serials still work). Layout: 4-char product, region/variant char, format-version char (`0`),
  5-char base-36 sequence (random step of 1..`MaxSequenceStep` per unit, starting at
  `SequenceStart`, so counts aren't obvious), Luhn mod-36 check character. Product codes come from
  `Manufacturing:ProductCodes` and allowed regions from `Manufacturing:RegionCodes` (validated at
  startup). The device type is the firmware family, not a config key: `msv1`/`msv2` (standalone
  firmware) and `msv3` (bootloader that takes several application versions) map to `MSV1`/`MSV2`/`MSV3`.
  Which config a unit uses is a separate per-unit staff assignment.
- **Unit history and firmware rules**: every manufacturing call appends a `BciDeviceEvent` (never a
  code). `rekey` needs a `reason` (`RekeyReason`: Reflash/Rework, parsed from a string; value 1 was `Relabel`, retired, don't reuse it), an
  optional note and the *intended* `firmwareVersion`; it works on Reserved and Manufactured units and
  returns the unit to Reserved. Firmware fields are applied at `confirm` (or `/firmware`, for
  firmware writes that leave the registration code unchanged): `LastFirmwareUpdatedAt` always
  moves, `CurrentFirmwareVersion` only if it differs. `ManufacturedAt`/`ProducedAt` are set on the first
  confirm only. **Bootloader version** (MS-V3 units have a bootloader plus an app): an optional
  `bootloaderVersion` rides along with `firmwareVersion` on `reserve`/`confirm`/`rekey`/`/firmware`,
  stored as `BciDevice.BootloaderVersion` and in the event history; given replaces, omitted leaves it
  unchanged (never cleared; null for MS-V2). Like firmware it's applied at `confirm`/`/firmware`, only
  recorded as *intended* at `reserve`/`rekey`; an app update over USB omits it. `void` is refused if any participant is paired. Region has no server default.
- **Manual provisioning page** (`/Manufacturing`, `Pages/Manufacturing/`): reserve (batch of up to 50), confirm, void and
  rekey from the browser, for flashing units without Quarry. Gated by the `Manufacturer` role (policy
  `ManufacturerPortal`, cookie auth; Staff can't use it) and re-checks the account is usable per request. It runs the
  same `Manufacturing/UnitProvisioning` service as the API (reserve/confirm/void/rekey logic lives there; the
  endpoints only map results to HTTP). Fresh registration codes are rendered once in the response (no-store, with
  CSV download and print), never kept in TempData or the DB. Confirming by hand trusts the operator typed the
  code read back from the unit; only Quarry reads it off the device itself.
  **The registration code is never printed** (no label, no insert card): it lives in the unit's identity record and as
  a hash here, and the code table/CSV on this page is only for the operator writing it into the unit.
  **Allocate serials in advance** (device type, region, count up to 200; `UnitProvisioning.AllocateHeldSerialAsync`)
  is the normal start of a unit's life: the serial is 3D-printed on the casing, which takes time, so serials are
  allocated first and held, and the code is issued by rekey when the board is flashed.
  **Hold serials** takes serials typed in by hand (one per line, up to 200) for a serial that already exists
  outside the server: `UnitProvisioning.HoldSerialAsync` reserves *only the serial* so the allocator can never issue it to
  another unit. The unit is `Reserved` with **no registration code and no firmware/bootloader version**
  (`BciDevice.IsHeld`; `CurrentFirmwareVersion` is nullable for this). It can't be confirmed (the message says to rekey)
  or registered by a participant. Flash time: **rekey** it, which issues its first code (the server records the reason
  as `RekeyReason.Initial` for a held serial whatever was sent, and refuses `Initial` for any other unit), then flash and
  confirm. A valid serial (check character), a configured product and region, and never issued (voided included) are
  required. Page-only, not in the API; each serial succeeds or fails on its own. The allocator continues from the
  highest serial per product+region, so a hand-picked one never collides.
- **JWTs now carry role claims** (`TokenService.CreateAccessToken(user, roles)`), baked in at
  login/refresh like cookie roles.
- **Registration** (`POST /api/devices/register`): a *new* pairing needs the unit to be
  `Manufactured` and, while `Devices:RequireRegistrationCode` is true (default; reversible, codes are
  generated regardless), the correct code. Unknown serial / unit not registerable / missing or wrong
  code all return the same generic 404. Re-registering an existing pairing needs no code. Failures
  are capped at 10/hour per user (`RegistrationFailureLimiter`, in-memory).
- **Not built**: the station app, AxoSync reading the identity, a participant/AxoSync-facing firmware-update endpoint (the manufacturing one is built), batch
  reserve for offline factories, any UI to grant `Manufacturer`.

## Firmware catalog

Implemented server-side — Quarry flashes only builds from this catalog, never an arbitrary file on disk.
Firmware developers use their own flash tools; only production-ready builds are submitted here.

- **Versions are read from the file, never typed.** `Firmware/FirmwareImage.cs` (`Describe`) is a port of
  Quarry's `FirmwareHex.Describe` plus the MS-V3 app-header check (firmware repo,
  `quarry/.../Identity/FirmwareHex.cs`, `Update/AppImage.cs`); `Firmware/IntelHex.cs` is the matching reader. **If the image layout
  or version format changes, change both copies.** `testdata/firmware-image-vectors.json` pins what they must agree on
  (owned by the firmware repo, `quarry/tests/`; `FirmwareImageVectorTests` runs it here): the catalog may be stricter than
  Quarry, never looser. (The copies had drifted before this fixture: the port still read the 16 KB-bootloader, header-format-1
  layout, so no current MS-V3 image would have been accepted.) An image is `Single` (MS-V1/V2: the one `MAJOR.HEIGHT+hash`
  string found in flash), `MsV3Factory` (bootloader + app: app version from the CRC-checked header, bootloader
  version from the bootloader flash range) or `MsV3App` (app only: the header).
- **Data**: `FirmwareBuilds` (`DeviceType` = the `msv1`/`msv2`/`msv3` firmware family, `Kind`
  Application/Factory, `Version` = the app's, `BootloaderVersion` (Factory only), `Status` Draft/Published/Withdrawn,
  file name, size, server-computed SHA-256, `StorageKey`). Unique on (DeviceType, Kind, Version, BootloaderVersion), as two
  filtered unique indexes (SQLite treats NULLs as distinct), so a factory image with a new bootloader and the same app
  version is its own build; its file is stored under an extra `{bootloaderVersion}/` folder. Builds are
  immutable: a fix is a new version, there is no replace. **Application** = MS-V1/V2's whole firmware, or an MS-V3
  app on its own (USB update). **Factory** = MS-V3 bootloader + app, what a programmer writes to a new unit. There is
  no bootloader-only kind: a bootloader alone looks like any single image to the programmer (Quarry refuses it, see
  `MindStoneQuarry-design.md` "Firmware version"). `FlashableByProgrammer` = Application for msv1/msv2, Factory for msv3.
- **Upload rules** (`/Staff/Firmware`, device type + file, nothing else typed): the image must read cleanly
  (HEX checksums, EOF record, MS-V3 header CRCs), carry exactly one version, and be a **release** version
  (`MAJOR.HEIGHT+hash`: `.dirty` and `.testN` builds are refused, as Quarry would refuse them); msv3 takes only
  MsV3Factory/MsV3App, msv1/msv2 only Single (a Factory needs a readable bootloader version too). **Caveat:** a
  bootloader-only hex reads as a `Single` release image, so it passes for msv1/msv2 if someone picks that type.
  16 MiB cap. Publish/Withdraw, delete never-published drafts.
- **Storage**: `IFirmwareStorage` / `LocalFirmwareStorage` (`Storage/`), `Firmware:StoragePath` (default
  `firmware-files/`, gitignored; not `firmware/`, which collides with the `Firmware/` source folder on macOS/Windows),
  same rules as release storage (outside the web root and the rsync-deployed dir).
- **API** (`Manufacturer` role, account re-checked per call): `GET /api/manufacturing/firmware?deviceType=msv3`
  lists Published builds (id, kind, version, bootloaderVersion, notes, file name, size, sha256);
  `GET /api/manufacturing/firmware/{id}/file` streams the hex (ETag = SHA-256). Integrity is TLS + SHA-256; no build
  signing. The `/Manufacturing` page only offers (and the server only accepts) published, programmer-flashable builds
  for the unit's device type, and records the build's version (and bootloader version) on the unit.
- **Not built**: the API's `confirm`/`rekey`/`/firmware` still take free-text version strings, not a catalog build id.
  Quarry-side caching for offline factories is Quarry's job.

## Releases & downloads

Implemented — AxoSync installers are published through the API (or by hand) and fetched by
participants (login-gated download page) and by AxoSync's own updater. Full contract, signing scheme
and API reference: [`docs/release-publishing.md`](docs/release-publishing.md) — read it before
touching any of this.

- **Data**: `Releases` (unique `Version`, `Notes`, `MinimumVersion`, `Status` Draft/Published/Withdrawn,
  `PublishedAt`) and `ReleaseArtifacts` (one per release+platform: file name, size, server-computed
  SHA-256, `Signature`, `StorageKey`). Platforms are `ReleasePlatform` (`WindowsX64`, `MacOSArm64`,
  `MacOSX64`, `LinuxX64`).
- **Storage**: `IReleaseStorage` / `LocalReleaseStorage` (`Storage/`) writes to
  `Releases:StoragePath` (default `releases/` under the content root, gitignored) as
  `{version}/{platform}/{file}`. It's never served as a static file, and in production it must live
  outside the rsync-deployed app dir (see `docs/deployment-vps.md`). Object storage later = a new
  `IReleaseStorage` implementation, nothing else changes.
- **One rule set**: `Publishing/ReleasePublisher` holds the publishing rules (SemVer version format,
  drafts-only uploads, signature check, no publishing an empty or unsigned release). Both
  `/Staff/Releases` and the publisher API go through it; don't re-implement them in a page or
  endpoint. `Publishing/ReleaseVersion` is the SemVer comparer (`1.4.0-beta.1 < 1.4.0`).
- **Signed builds**: each artifact carries an offline ECDSA P-256 signature over version, platform,
  SHA-256 and size. The private key never touches this server; the public key is
  `Releases:SigningPublicKey` (or several, `Releases:SigningPublicKeys`; any one signature is enough) here and compiled into AxoSync, which does the real verification. Unset
  = no enforcement (dev only).
- **Publishing**: the `Publisher` role (JWT, seeded at startup, granted by `scripts/setrole.sh` like
  Staff/Manufacturer) drives `/api/publishing/...`: draft, upload artifact + signature, publish /
  withdraw. The staff page `/Staff/Releases` does the same by hand. Published releases are immutable
  (a fix is a new version); a published release can be withdrawn but not returned to draft.
- **Update check** (`Api/Updates/`, any usable account, JWT): `GET /api/releases/latest?platform=&current=`
  returns the highest published SemVer with a build for that platform (pre-releases only on request),
  plus `mandatory` when `current` is below that release's `MinimumVersion`; `GET /api/releases/{version}/{platform}/file`
  streams it. Account usability is re-checked per call.
- **Downloading by hand**: `/Download` requires login *and* `AccountPolicy.IsUsable` (active, not
  expired). Shows the latest published build per platform (with SHA-256) plus older versions; a
  platform with no published build is greyed out. Files stream from `?handler=File&version=&platform=`
  and only for Published releases. It is linked (navbar "Downloads", a button on the home page, a row on My Account) only for
  accounts that pass `AccountPolicy.IsUsable`, via the per-request `Security/AccountAccess`; that only decides what to show,
  the page and endpoints still check the policy themselves.
- **Gotcha**: `.gitignore` has the Visual Studio template line `[Rr]eleases/`, which (macOS is
  case-insensitive) silently ignores any new *folder* named `Releases`. That's why the update API
  lives in `Api/Updates/`. Don't create a folder with that name.

## Build Commands

The solution file is `.slnx` (the newer XML-based format), not `.sln` — there is no `.sln` file
in this repo.

```bash
dotnet build Intelimensa.Accounts.slnx
dotnet run --project Intelimensa.Accounts/Intelimensa.Accounts.csproj
```

```bash
dotnet test Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj
```

`Intelimensa.Accounts.Tests` is small so far: it runs `ReleaseSigning` against the fixture shared with AxoSync and
`publish.sh` and `publish.ps1` (`testdata/release-signing-vectors.json`, owned by this repo; see `docs/release-publishing.md`).

### Dev server

`scripts/dev-server.sh start|stop|restart|status|logs` runs the real dev app: the real `accounts.db`, `releases/` and
`firmware-files/`, the user-secrets JWT key, and the `http` launch profile's port (5295), read from
`launchSettings.json`. `start` refuses while EF migrations are pending; `start --migrate` backs the database up first
(`Intelimensa.Accounts/accounts.pre-migrate-<time>.db`, gitignored) and applies them. It clears any leaked
`ConnectionStrings__*`/`*__StoragePath`/`Jwt__*` variables from the shell, and `stop` only ever stops a process that is
verifiably this repo's app (by pidfile, or by the dev port if the pidfile was lost); anything else on the port is
reported and left alone. It never deletes data.

### Deploying to production

`scripts/deploy.sh plan|deploy|rollback|status` deploys to the VPS (`docs/deployment-vps.md`, step 12). The app never
migrates itself at startup, so the script does it: it applies only the migrations the server hasn't run, with the
service stopped, after a `sqlite3 .backup`, using `sqlite3 -bail` and restoring the backup if the script fails. The new
build is uploaded beside the running one and switched in by a rename; the old one is kept as `app.prev` for `rollback`
(which also restores the pre-deploy database when that deploy changed the schema, losing anything written since).
`deploy` and `rollback` change production: run them only when the user asks for that deploy. `plan` and `status` are
read-only. Don't hand-run `dotnet ef migrations script` against the production database.

### Test server

For manual or scripted testing against a running app, use `scripts/test-server.sh start|stop|status|logs`
instead of starting servers by hand. It builds, migrates a throwaway database, seeds `test@example.com` /
`correct-horse-battery` (Staff + Manufacturer) and starts on a free port (from 5399), with its database, release
storage, firmware storage and config storage all inside one temp directory (`$TMPDIR/intelimensa-accounts-test`) that `stop`
deletes (only if it carries the script's marker file). It never touches `accounts.db`, `releases/` or
`firmware-files/`. Never point a test server at the dev storage folders: uploads there are real dev data.

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

## Shared contract fixtures (workspace)

This repo is one of the sibling repos of the Intelimensa workspace (`github.com/intelimensa/code-workspace`, private; normally the
parent folder of this one). Where two repos implement one contract, a JSON fixture pins it: one repo **owns** the fixture, the
others keep a byte-identical **copy**, and each side tests itself against it. The workspace repo holds the list
(`vectors.manifest`), `sync-vectors.sh` (copies each owner's file over its copies) and `check-contracts.sh` (runs all of it, about
10 s). Its root `CLAUDE.md` has the overview.

| Fixture | Here | Role | Generator / the other side |
|---|---|---|---|
| Release signing | `testdata/release-signing-vectors.json` | owner | `scripts/gen-signing-vectors.py`; copy in `axosync` (branch `remote-account` only) |
| Serial numbers | `testdata/serial-number-vectors.json` | owner | `scripts/gen-serial-vectors.py`; copy in `firmware/quarry/tests/testdata/` |
| JSON API | `testdata/api-vectors.json` | owner | `scripts/gen-api-vectors.py`; copies in `firmware/quarry/tests/testdata/` and `axosync` (`remote-account` only) |
| Config delivery | `testdata/config-delivery-vectors.json` | owner | `scripts/gen-config-delivery-vectors.py` (plain-Python crypto, deterministic); copy in `axosync/ConfigGen/testdata/` (`ConfigGen selftest`; branch `remote-config`) |
| Firmware images | `testdata/firmware-image-vectors.json` | copy | owner: `firmware/quarry/tests/` |
| Unit identity | `testdata/identity-vectors.json` | copy | owner: `firmware/testdata/` |

They are checked by `Intelimensa.Accounts.Tests`.

- **Never edit a copy.** Change the owner's generator, regenerate, run `../sync-vectors.sh --fix` (it lives in the workspace root,
  the folder above this one), and commit the changed copy in each repo it touched.
- Run `../check-contracts.sh` before pushing a change that touches one of these contracts.
- This repo alone can't run those scripts. If the workspace isn't next to it, clone it and run its `onboard.sh`, which clones the
  other repos into place.
