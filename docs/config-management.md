# Config file management — design

Status: **agreed design; the server side is built** on the `remote-config` branch (2026-10-25), without 2FA.
AxoSync's client side and ConfigGen's publish mode are not built yet. Watermarking, the emergency offline
lease and the browser are deferred (see "Later"), and so is 2FA, to get delivery working first. Where the
build settled something this doc left open, or departed from it, see "As built (server)" at the end. This doc replaces the
"Config delivery model (planned)" section of `CLAUDE.md`.

## Where we are today

- Config sources are plaintext INI files in `axosync/ConfigGen/Assets/`.
- `ConfigGen` encrypts each one with a **fixed, human-memorable passphrase per config stem**
  (`Shared/ConfigPasswords.cs`). The format is `salt(32) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`,
  with PBKDF2-SHA256 at 600k iterations and AES-256-GCM.
- The encrypted files are **embedded in every AxoSync installer**. A Release user types the
  passphrase into `ConfigSelectionDialog`, and `ConfigLoader.DecryptResource` decrypts the file in
  memory.
- As a result, anyone with the installer and a passphrase can decrypt that config forever.
  Account status, expiry and staff assignment in this service have no effect on that. Rotating
  keys later doesn't help either: installers already handed out stay decryptable with the
  passphrases they shipped with.

## How configs are used

- There is **one general config**. It's the default for typical participants.
- A few **experimental configs** exist, and staff issue them by hand to specific participants.
- Configs **aren't versioned**. A materially different config is simply a new config (a new
  `Config` row with its own key). Replacing a config's file in place only happens for key
  rotation or a fix (see "Rotation, revocation and compromise").
- Configs aren't customized per participant today. That may change, and it would arrive together
  with watermarking (see "Threat model", item 2).

## Goals

1. Only a signed-in, usable account (active and not expired) can decrypt a config, and only when
   it has a registered device that staff have assigned that config to.
2. **A valid token on its own is not enough.** Whoever replays AxoSync's requests with a stolen or
   copied token, or with their own token from a script, gets nothing they can decrypt. Turning a
   delivery into a config takes the private key of a registered install *and* the secret
   compiled into a supported AxoSync release.
3. The app works offline for a bounded period. Revocation and expiry take effect at the next
   online launch, or when the offline lease runs out. Nothing needs cleaning up by hand on
   participants' machines.
4. The plaintext never sits on the server's disk, in its backups, or on the participant's disk.
5. Rotating a config's key is cheap: re-run ConfigGen and replace the file.
6. No password prompt for a signed-in participant.

Not a goal: stopping an authorized participant from extracting the plaintext from their own
running process. No delivery scheme can, as long as the pipeline runs on their PC (see "Threat
model"). The aim is that extraction takes reverse-engineering a protected release, not a `curl`.

## Overview: four layers

| Layer | What it stops |
|---|---|
| 1. Requests are signed by a key that belongs to the install | A token copied off a disk or out of traffic is useless without the install's private key |
| 2. Installs are limited per account and visible | A participant who scripts their own client has to register a new install, which they're told about and staff can see |
| 3. The delivered key is wrapped to the install's key **and** to a per-release secret | A delivery is only usable by that install running a supported AxoSync build |
| 4. Encrypted configs, offline leases, decryption in memory only | Plaintext never on disk or on the server; offline use bounded |

Layer 3 is the one that answers "only AxoSync knows what to do with it". Layers 1 and 2 make sure
a stolen token can't stand in for a registered install.

## 1. Client installs (the `ClientInstalls` table)

One row per **client install**: one registered installation of AxoSync (one instance slot on one
machine or app install). The table exists today as the unused `Device` model and is renamed
`ClientInstall` before anything writes to it. In this service a
*device* is an Intelimensa unit with a provisioned serial number and registration code
(`BciDevice`); a *client install* is a copy of AxoSync.

### Keys

On first sign-in, AxoSync generates **two P-256 keypairs** for the install: a signing key (ECDSA,
proves requests come from this install) and a key-agreement key (ECDH, receives wrapped content
keys). Two keys, not one, because platform keystores fix a key's purpose at creation and because
reusing one key for signing and ECDH is poor practice.

Where the private keys live, best available per platform:

| Platform | Store | Can the participant copy the key? |
|---|---|---|
| Windows with TPM 2.0 | CNG, Microsoft Platform Crypto Provider (TPM-backed, non-exportable) | No, it can't leave the machine; it can only be *used* there |
| Windows without TPM | DPAPI (`FileSecretStore`) | Yes, as that user |
| macOS | `0600` file (`FileSecretStore`) today; Secure Enclave once builds carry a stable Developer ID signature | Yes today; no with the Secure Enclave |
| iOS / iPadOS | Secure Enclave via Keychain | No |
| Android | Android Keystore (hardware-backed where available) | No |
| Browser | WebCrypto, non-extractable key in IndexedDB (sign-in only; the browser gets no configs, see section 2) | Not directly, but the page runs on the participant's machine |

The choice of P-256 is deliberate: it is the curve every hardware store above supports, and the
one the release signing already uses.

### How a row is created

At login, AxoSync sends its install's public keys with the credentials:

```json
POST /api/auth/login
{ "email": "…", "password": "…",
  "install": { "signingKey": "<SPKI base64>", "agreementKey": "<SPKI base64>",
               "platform": "MacOS", "name": "MacBook Air", "appVersion": "0.1.0", "slot": 1 } }
```

with an install proof (below) signed by `signingKey`, which shows the client holds the private
key. The server then:

1. checks the password and account as today;
2. if the account has two-factor authentication on, asks for the code first (see "Two-factor
   authentication"), so a new install can only be registered with both factors;
3. looks for a non-revoked `ClientInstalls` row of this account with the same signing-key thumbprint. If
   there is one (the participant signed out and back in), it reuses it, and no slot is used;
4. otherwise counts the account's active installs (not revoked, seen within the idle window). At
   the limit it refuses with `409 {"error":"install_limit"}`, and AxoSync tells the participant to
   remove an old install on the website. Below it, it creates the row;
5. issues the tokens bound to that row: the refresh-token row gets `ClientInstallId`, and the access JWT
   carries the install's ID and key thumbprint (a `cnf` claim, as in RFC 9449 / DPoP);
6. records the registration and emails the participant ("AxoSync was signed in on a new
   computer: MacBook Air, macOS").

Nothing else creates rows. Registration happens only together with a password login, so a
stolen refresh token can't add an install.

### Install proofs (every authenticated request)

Every request that uses a token (`refresh`, the config endpoints, update checks) carries a header
`X-Install-Proof`: the install's ECDSA signature, DER then base64, over this UTF-8 text, each
line ending in `\n`, in the same style as the release statement:

```
intelimensa-install-proof-v1
<METHOD>
<path and query>
<lowercase hex SHA-256 of the body, or of the empty string>
<nonce>
```

The **nonce comes from the server**, not the client's clock, so a wrong clock can't break sign-in.
Every `/api` response carries a fresh `X-Install-Nonce`, a stateless HMAC of a timestamp under a server
key, valid for 5 minutes, and `GET /api/auth/nonce` hands one out on its own. The client sends the nonce
it signed in its own `X-Install-Nonce` request header. A request with a missing or stale nonce gets `401`
(`install_proof_required` / `install_nonce_stale`) with a new nonce, and the client retries once. (The same exchange as DPoP's `use_dpop_nonce`.)

The server checks that the proof verifies against the signing key of the install the token is
bound to, and that the install isn't revoked. That check runs on every call, like the existing
per-call account check, so removing an install takes effect at once.

### Limits and visibility

- **Default limit: 6 active installs per account** (`Configs:MaxInstallsPerAccount`). That's one
  for each target a participant might reasonably use (desktop PC, MacBook, Android phone, iOS
  tablet, browser) plus one spare for moving to a new machine. Staff can override it for one
  account (`Account.MaxInstallsOverride`, nullable).
- **Idle expiry:** an install not seen for 60 days stops counting and is revoked
  (`Configs:InstallIdleDays`). A reinstall or a lost key therefore resolves itself, and nobody has
  to clean up dead installs by hand.
- **The participant's account page lists their installs** (name, platform, last seen) with a
  "Remove" button, which revokes the row and its refresh tokens.
- **Staff alert** when one account registers 3 or more installs within 24 hours. That catches a
  scripted client sooner than a cap does.
- Each instance slot is its own install, so two people on one PC use two installs, each on their
  own account.

## 2. Per-release secrets

Each AxoSync **build** (one version on one platform) carries a random 32-byte secret, compiled
in. The server holds a copy, and mixes it into every key it wraps for that build (section 4), so
a delivery is useless to anything but that build's code.

### How a secret is made and reaches the server

1. The build script (`osx/release.sh`, `windows/release.ps1`) generates the secret, compiles it in
   and writes it next to the installer (`<installer>.release-secret`).
2. The publish script (`all/publish.sh`, `windows/publish.ps1`) uploads it with the artifact:
   `PUT /api/publishing/releases/{version}/artifacts/{platform}`, new header
   `X-Release-Secret`. It is Publisher-only and drafts-only, the same as the installer upload,
   then the script deletes the file.
3. The server stores it **wrapped under the KEK** on the `ReleaseArtifact` row. It never leaves
   the server again.

This is separate from release signing, which is unchanged: the YubiKey signs the installer's
statement (`release-publishing.md`), and the per-release secret is neither a signature nor part of
the signed statement.

### Where the secret lives in the build

In a handful of methods that Reactor virtualizes (NecroBit and code virtualization, already on
in both `.nrproj` files), together with the code that uses it: the key unwrap and the blob
decryption (section 4), written in AxoSync itself instead of calling `AesGcm` and the platform
HKDF. A library call is a public, unprotected function that anyone can hook (Harmony and the
like) to read the key as it goes in. The same code inside a virtualized method can't be hooked
that way. A config is a few KB, so speed doesn't matter.

- **Verify on the output, not the settings.** Reactor virtualizes the methods it is told to. After
  each release build, open the obfuscated assembly in ILSpy or dnSpy and confirm those methods'
  bodies are VM stubs, on both platforms.
- **What stays hookable:** the decrypted text still goes to TINS's `INI.TryParse`, and the ECDH
  step goes to the platform keystore when the key is hardware-backed (that only yields the shared
  secret, which is useless without the release secret). TINS.Core isn't protected and won't be,
  because it runs almost every hot path of the app. A later option is to move the config parser
  into AxoSync and protect it there.
- **The browser head gets no configs.** It runs as WebAssembly, where Reactor doesn't apply and
  every line of our code (pipeline included) is readable in the browser. The browser is tabled
  until the pipeline no longer runs on the participant's machine: either `msv4`-class devices run
  it on the unit, or there is enough compute to run it in the cloud. In both cases the config
  never reaches the browser at all.

### Retiring a release's secret

A release's secret can be **blocked** (`ReleaseArtifact.ConfigDeliveryBlockedAt`), because the
release is too old or its secret is suspected leaked. The server then issues no keys to that
build, and AxoSync tells the participant to update. Combined with `minimumVersion` this is the
forced-upgrade path. **The update check must never depend on a config being loaded**, so that a
blocked or broken release can always update itself. A test should enforce this.

Debug builds carry no release secret. They keep the local `ConfigPasswords` path, and a
development server may accept one fixed development secret (`Configs:DevelopmentReleaseSecret`),
which production never sets.

## 3. Producing and publishing a config

Configs follow the same roles as AxoSync builds: a **Publisher** uploads, the upload waits as a draft
(here: the config's *pending* file), and **Staff** publish it. A Publisher may publish its own upload
only by asking for it explicitly (`--auto-publish`, as with the release scripts).

`ConfigGen publish <key>...` (axosync repo) does it, for each named catalog entry:

- merges `ui-defaults.ini` into the source, as the plain ConfigGen run does;
- generates a **fresh random content key (CEK)**, 32 bytes;
- encrypts the INI in a **new blob format, v2**: `"IMCF" ‖ version(1)=2 ‖ nonce(12) ‖ tag(16) ‖
  ciphertext`, AES-256-GCM with the CEK used directly and the 5-byte header as associated data. The v1
  format's PBKDF2 at 600k iterations stretched a human passphrase; a random 256-bit key needs none;
- signs in with a Publisher account (as `publish.sh` does) and calls the publisher API
  (`/api/publishing/configs`): `PUT /{key}` creates or renames the config, `PUT /{key}/file` uploads the
  blob with its key in the `X-Config-Key` header, and with `--auto-publish`, `POST /{key}/publish` makes it
  live. **The key never touches the disk**, and ConfigGen checks the server's SHA-256 against its own.

`ConfigGen publish --offline <dir>` writes `<key>.cfg` and `<key>.key` instead, for upload by hand on
`/Staff/Configs`; delete the `.key` file afterwards.

Every upload, through the API or the page, goes through the same rules (`ConfigDelivery/ConfigPublisher.cs`).
The server:

- validates the blob header and length;
- **test-decrypts the blob with the supplied key** and checks the result is text with INI sections, so a
  mismatched pair is rejected at upload rather than on a participant's machine. The plaintext is only
  held in memory and then discarded;
- stores the blob through `IConfigStorage`, under its SHA-256, outside the app directory;
- stores the CEK **wrapped under the server KEK**, so a leaked DB or backup alone yields no keys;
- keeps both as the config's **pending** file, replacing any earlier pending one. Participants keep
  getting the live file.

Staff review it on `/Staff/Configs` and **Publish** it (it replaces the live file, which rotates the
config's key; clients pick it up at their next online launch) or **Discard** it. A config with no
published file, or a retired one (`RetiredAt`), isn't delivered or newly assigned.

Once this ships, `ConfigPasswords.cs` and the configs embedded in the installer go away from
Release builds.

## 4. Assignment, delivery and decryption

### Assignment

A participant registers a unit with its serial number and registration code
(`POST /api/devices/register`, as today). Staff then assign a config to that `AccountDevice`:

- the form **pre-selects the default config** (the one `Config` row marked `IsDefault`), and staff
  confirm it with one click or pick an experimental config;
- nothing is assigned automatically; until staff confirm, the participant's app gets no config;
- at most one config is the default.

### Delivery (every online launch)

1. AxoSync restores its session (`refresh`, with an install proof) and reads the connected unit's
   serial (`dcGetIdentity`).
2. `GET /api/devices/{serial}/config` (token plus install proof). The server checks that:
   - the account is usable and the install isn't revoked;
   - an active `AccountDevice` exists for this serial;
   - it has an assigned config with an uploaded file, not retired;
   - the calling build (version and platform from the request) has a release secret that isn't
     blocked.

   It then wraps the CEK for this install and this build:

   ```
   shared  = ECDH(fresh server ephemeral key, install agreementKey)
   kek     = HKDF-SHA256(ikm = shared, salt = release secret,
                         info = "intelimensa-cek-wrap-v1" ‖ install id ‖ config key ‖ blob sha256)
   wrapped = AES-256-GCM(kek, CEK)
   ```

   and responds (`Cache-Control: no-store`):

   ```json
   { "configKey": "ms5", "sha256": "…",
     "wrappedCek": { "ephemeralKey": "<SPKI base64>", "nonce": "…", "ciphertext": "…", "tag": "…" },
     "lease": { … }, "leaseSignature": "…" }
   ```

   Every issuance is logged with the account, install, unit, config, blob SHA-256, build and time.
3. `GET /api/configs/{key}/blob` returns the ciphertext. Same entitlement, but cacheable
   (ETag = SHA-256), because the blob is useless without the key. AxoSync downloads it only when
   the SHA-256 differs from its cached copy.
4. AxoSync does ECDH with its agreement key, then (inside the protected methods) HKDF, unwraps
   the CEK, decrypts the blob and hands the INI text to the parser. **Nothing decrypted is
   written to disk.**

A token and a nonce replayed from a script without the install's private key fails the proof. A
script that has its own registered install gets a wrapped key it can't open without the release
secret.

### Offline use: leases

Offline use is required (participants can't be assumed to have a reliable connection). It is
built as a bounded **offline lease**, renewed whenever the app is online.

The server signs a lease (ECDSA P-256, DER signature, over a text statement in the style of the
release statement; `ConfigDelivery/OfflineLease.cs`), with a server lease-signing key whose public key
AxoSync embeds:

```json
{ "accountId": 42, "installId": "…", "serial": "MS05A0K3F9Q7", "configKey": "ms5",
  "sha256": "…", "issuedAt": "…", "notAfter": "…" }
```

- `notAfter` = min(now + lease length, account `ExpiresAt`).
- **Default 7 days** (`Configs:OfflineLeaseDays`); staff can set a longer lease for one account
  (`Account.OfflineLeaseDaysOverride`), capped at **30 days** (`Configs:MaxOfflineLeaseDays`).
  It is set on the account rather than per install, since connectivity depends on the
  participant's circumstances.

AxoSync caches the blob (freely), and the **wrapped** CEK plus the lease plus a "latest time seen"
high-water mark. The wrapped CEK is still useless without the install's agreement key and the
release secret, so the cache doesn't need a separate protection layer. It goes in `ISecretStore`
anyway.

- **Online launch:** fetch a fresh wrapped key and lease (and the blob if its SHA-256 changed),
  then overwrite the cache.
- **Offline launch:** use the cache only if the lease signature is valid, `now < notAfter`, the
  clock hasn't moved backwards past the high-water mark (with some tolerance), the lease's
  `installId` is this install and its `serial` is the connected unit.
- **Otherwise:** AxoSync explains that it needs to go online.
- **After an update**, the cached key is wrapped under the *previous* build's secret, which the new
  build doesn't have. So before installing, AxoSync fetches the new build's key:
  - Once an update is staged (`UpdateService`, online by definition), AxoSync asks
    `GET /api/devices/{serial}/config?version=<new version>&platform=<platform>` and caches that
    wrapped key and lease next to the current ones, keyed by build. The new build picks its own
    on its first launch, so an install-on-exit followed by an offline launch still works.
  - The server answers only for builds that are published and not blocked. Asking on behalf of
    another build gives nothing away: unwrapping still needs that build's secret.
  - If the fetch fails, the update installs anyway, and the new build needs one online launch.
    It never blocks an update.

Revocation and expiry take effect at the next online launch, or when the lease runs out.

## Two-factor authentication

Install registration happens only at password login, so a second factor there protects both
the account and the "only registered installs get configs" rule.

**Who has it**
- **Optional for every account.** A participant turns it on from their account page.
- **Required for privileged roles:** Staff, Admin, Manufacturer and Publisher. An account holding
  one of these roles without 2FA is sent to enrollment at its next web login, and its API tokens
  carry no privileged role claims until it has enrolled.

**The method:** time-based codes from an authenticator app (TOTP, RFC 6238), plus ten single-use
recovery codes. Both are built into ASP.NET Core Identity: `AddDefaultTokenProviders()`, which
`Program.cs` already calls, registers the authenticator provider, and `UserManager` generates and
checks recovery codes (stored hashed). No SMS (weak, and costs money per message), and no emailed
codes (email is the account-reset channel, so it can't also be the second factor).

**Website**
- Enrollment page: shows a QR code and the key in text (an `otpauth://` URI), asks for one code to
  confirm, then shows the recovery codes once.
- Login: the existing `Pages/Account/Login` gets a second step for the code or a recovery code.
- Account page: disable (needs a current code), regenerate recovery codes.
- Enabling 2FA offers to sign out every other install, in case the password was already known to
  someone.

**API (AxoSync, Quarry, the publish scripts)**

Login becomes two steps when the account has 2FA:

```json
POST /api/auth/login          { "email": "…", "password": "…", "install": { … } }
→ 200 { "twoFactorRequired": true, "challenge": "<opaque>", "expiresAt": "…" }

POST /api/auth/login/2fa      { "challenge": "<opaque>", "code": "123456" }
                           or { "challenge": "<opaque>", "recoveryCode": "…" }
→ 200 TokenResponse (as today)
```

- The challenge is short-lived (5 minutes) and single-use. It is bound to the user, their security
  stamp (so a password change voids it) and the install key that signed the first step; the
  second step must carry an install proof from the same key.
- Failed codes count toward Identity's lockout, like failed passwords.
- **Refresh never asks for a code.** A refresh token belongs to an install that already passed
  2FA, and is bound to that install's key, so the install itself acts as "remember this device".
  That is what keeps 2FA from becoming a prompt at every launch.

**Clients**
- AxoSync: `LoginDialog` gets a code step.
- Quarry: its login gets a code step (Manufacturer accounts must have 2FA).
- `publish.sh` / `publish.ps1`: prompt for the code (Publisher accounts must have 2FA). Both are
  already interactive (YubiKey PIN and touch), so this adds one prompt per release. The Mac drives
  remote Windows releases (`all/publish.sh`), so the code is typed there.
- `api-vectors.json` gets the two-step login examples, with its copies in Quarry and AxoSync.

**Lost authenticator and lost recovery codes**
- Staff can reset a participant's 2FA from the account row, after confirming who is asking (for
  example from the participant agreement's contact details). The reset is logged and the
  participant is emailed.
- Resetting a Staff or Admin account's 2FA needs a different Staff or Admin account.

**Later:** passkeys (WebAuthn) for the website. Check what ASP.NET Core Identity in .NET 10 offers
before building our own. They don't fit the desktop app's login as easily, which is why TOTP comes
first.

## Failure modes and recovery

| Failure | Effect | Recovery |
|---|---|---|
| Install key lost (reinstall, wiped AppData, new PC, profile reset, Mac migration) | Signed out | Sign in again; a new install is registered. The old one idles out after 60 days, or the participant removes it. |
| Install limit reached | "Too many installs" at sign-in | Participant removes an old install on the website; staff can raise the limit for one account. |
| Offline longer than the lease | AxoSync asks to go online | By design; staff can lengthen the lease for one account. |
| Unit doesn't report a serial | No config can be bound to it | Two such units are in use today; they're to be replaced before the config-less build ships (see "Rollout"). |
| Participant's clock wrong | Proofs unaffected (server nonce); the lease clock check may complain | Tolerance on the lease check; an online launch corrects it. |
| VPS down | Participants with a valid lease don't notice; new or expired ones can't load a config | Restore from backup (DB and blobs; keys come from the password-manager copies). An emergency offline lease is a possible later addition (see "Later"). |
| DB or backup leaked on its own | No usable keys (CEKs wrapped under the KEK; release secrets too) | Rotate anyway: ConfigGen publish, re-upload. |
| VPS taken over | KEK, CEKs, blobs and release secrets exposed: assume every current config leaked | New KEK and lease-signing key, new CEKs, new release, revoke all tokens. The leaked content stays leaked; only watermarking would trace it. |
| Lease-signing key leaked | Leases can be forged, but a lease only gates a wrapped key the forger would still have to open | Ship a release with a new public key. |
| A release's secret extracted | That build's protection is gone | Block its config delivery; participants on it update (`minimumVersion`). Windows copies not installed by Inno need a manual reinstall. |
| A release with a crypto or protocol bug | Nobody on it can load a config | Publish a fix; the update path never depends on a config. The server keeps serving the old wire format until no supported build uses it. |
| Malware running as the participant (no hardware key store) | Can use the install's key and token | Same exposure as today; removing the install on the website kills it. Hardware-backed keys (TPM, Secure Enclave, Keystore) stop the key from being copied away. |
| Keys or tokens in logs | Leaked through support zips | Never log keys, tokens, proofs or wrapped keys; redact `Authorization`. A test should enforce it. |

## Rollout

**Branches:** the work happens on a branch named `remote-config` in each repo it touches
(`accounts` and `axosync`; `firmware` too if Quarry's login changes for 2FA), and each is merged
into its repo's `main` when that step is ready.

1. Server: install registration and proofs, then the config endpoints, release-secret upload and
   leases, behind a flag. AxoSync keeps the password path.
2. AxoSync: install keys and proofs in `AccountsClient`; then config fetch, falling back to the
   password dialog when the server can't deliver. Watch the issuance log for failures from real
   participants.
3. A release without embedded configs or `ConfigPasswords`, once **every active participant has
   received a config through the server and every unit in use reports a serial** (the two units
   without one are replaced first). Pull the old installers from distribution.

The password fallback in step 2 is the transition's recovery path. It is also why the old
installers stay as weak as they are today until step 3.

## Contracts

The new wire formats get a shared fixture like release signing, owned here:
`testdata/config-delivery-vectors.json` (install proofs, the CEK wrap, the v2 blob and the offline lease),
generated by `scripts/gen-config-delivery-vectors.py`. That script implements the crypto in plain Python,
independently of .NET, checks itself against published test vectors, and writes the same file on every
run. It has no copies yet: AxoSync's and ConfigGen's will be added to `vectors.manifest` when their side is
built. The new endpoints' JSON goes into `api-vectors.json` at the same time, since that fixture's copies
live in AxoSync and Quarry.

## Data model changes

| Change | Notes |
|---|---|
| `Device` renamed `ClientInstall` (table `ClientInstalls`, enum `ClientPlatform`): `SigningKey`, `AgreementKey`, `KeyThumbprint`, `Name`, `Slot`, `LastSeenAt`, `RevokedAt`; add `Browser` to the platforms | Replaces the single `PublicKey` placeholder. Nothing writes the table today, so the rename is free now. |
| `RefreshToken.ClientInstallId` | Binds each refresh token to one install. |
| 2FA: none | Identity's own columns hold it (`TwoFactorEnabled`, and the authenticator key and recovery codes in `AspNetUserTokens`). |
| `Account.MaxInstallsOverride` (int, nullable) | Null means `Configs:MaxInstallsPerAccount` (6). |
| `ReleaseArtifact`: `WrappedReleaseSecret`, `KekId`, `ConfigDeliveryBlockedAt` | One secret per build. |
| `Config`: add `BlobStorageKey`, `BlobSha256`, `BlobSize`, `WrappedCek`, `KekId`, `UploadedAt`, `UploadedByUserId` (nullable until the first upload); `IsDefault`, `RetiredAt`; drop `CurrentVersion` | One encrypted file per config. |
| `Account.OfflineLeaseDaysOverride` (int, nullable) | Capped at `Configs:MaxOfflineLeaseDays`. |
| New `ConfigKeyIssuances` table (or reuse `BciDeviceEvent`) | Who received which key, on which install and build, when. |

## Rotation, revocation and compromise

- **Suspected key leak, or a fix to a config:** run ConfigGen publish (fresh CEK) and upload to the
  same `Config` row. The new SHA-256 makes clients fetch the new file and key at their next
  online launch. A participant still offline keeps the old file until their lease runs out.
- **A materially different config:** a new `Config` row; reassign participants; retire the old row.
- **Participant revoked or expired:** no new keys or leases; access ends at the next online launch
  or when the lease expires. One who extracted the key from memory keeps that config's current
  content, which is why rotation exists.
- **Install removed:** its tokens and proofs stop working at once; its cached lease stops at the
  next online launch or expiry.
- **Release secret suspected leaked:** block that build's config delivery; ship a new build.
- **KEK compromise:** new KEK, re-wrap all CEKs and release secrets. Server-side only.

## Server key management

Long-lived server secrets (the JWT signing key is the same kind):

- **The KEK** wraps CEKs and release secrets. A single secret in configuration
  (`Configs:KeyEncryptionKey`, base64, with `Configs:KeyEncryptionKeyId`), loaded like
  `Jwt:SigningKey`: .NET User Secrets in Development, an environment variable or file on the VPS.
  Without it (or without the lease key) the app still starts, and config delivery is off: the config
  endpoints answer `503 config_delivery_unavailable` and the staff page says so. That is the rollout's
  switch. ASP.NET Data Protection was rejected: its key ring would be one more thing to protect and back up.
- **The lease-signing private key** (`Configs:LeaseSigningKey`, base64 PKCS#8 DER of a P-256 key). The staff
  Configs page shows its public half, which AxoSync embeds.
- **The nonce key** for install proofs is derived from `Jwt:SigningKey` (HKDF), so it is not one more
  secret. Losing it only invalidates nonces in flight.

Making them (once per server; never commit them):

```bash
openssl rand -base64 32                                            # Configs:KeyEncryptionKey
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -outform DER | base64
                                                                    # Configs:LeaseSigningKey
```

On the VPS they are files or environment variables readable only by the service user, outside the
rsync-deployed app directory and **outside whatever the backup job copies**. Anyone who takes
the VPS gets them along with the DB; the KEK defends against the DB or a backup leaking on its
own, which is the more likely accident.

**Losing them is recoverable.** Without the KEK: a new KEK, re-run ConfigGen publish and
re-upload, and ship a build (its release secret is then wrapped under the new KEK). The
lease-signing key is replaceable too, but needs an AxoSync release with the new public key, which
is why it deserves a backup.

**Backup copies** go in a password manager with end-to-end encryption (Proton Pass), in an unshared
vault, on an account with a strong unique password and 2FA (ideally a hardware key). Its recovery
phrase is written down and stored physically.

**Where the real exposure is**, ranked by what an attacker would gain:

1. **The plaintext INI sources** in `axosync/ConfigGen/Assets/`, in git on every clone, the git
   host and the history. See "Decisions so far".
2. **The VPS.** It holds the KEK, the DB, the blobs and the signing keys. Keep SSH key-only, the OS
   patched, services minimal.
3. **Staff accounts.** Any Staff account can upload and assign configs, which is why privileged
   roles require 2FA (see "Two-factor authentication").
4. **The password manager**, holding the backup copies.

## Threat model: extraction by a participant

A participant who controls the machine AxoSync runs on can get the plaintext config, for example
by dumping process memory, hooking the parser, or reverse-engineering the protected methods to
recover the release secret. No key-delivery scheme fixes this while the PC needs the plaintext to
run the pipeline. What we can do, cheapest first:

1. **Limit the blast radius** (this design): each participant gets only their assigned config;
   each config has its own key; deliveries are bound to an install and a build; every issuance is
   audited.
2. **Deterrence and traceability.** Participant agreements that cover the software and configs.
   Later, **watermarking**: ConfigGen generates offline a pool of copies of each config, each with
   its own CEK and a recorded code (tiny, behaviour-neutral changes to non-round float
   parameters, plus cosmetic marks), and the server hands each unit an unused copy on first
   issuance. The server still never holds plaintext. A replay check over recorded sessions must
   show each copy's output stays within a set bound of the base config's, since this is a
   research cohort. A `ConfigGen identify` command ranks the likely source of a leaked copy.
3. **Raise the cost** (this design, section 2): Reactor virtualization of the code that holds the
   release secret and decrypts, with its own crypto rather than hookable library calls. Later: the
   config parser moved into AxoSync and protected too; certificate pinning.
4. **Move the IP off the PC.** The only structural fix: the pipeline and its config run on the
   device, behind the MCU's debug lock, and the PC never sees them. `msv4` (nRF54L15) is built
   that way, but it is far from being the shipping product: the pipeline's CPU budget on it
   hasn't been met and the full build hasn't been tested. Until it ships, the PC design above is
   the protection. On the current hardware (MS-V2, PIC32MX, no code protection) a config stored on
   the unit would be easier to extract than from the PC, so that's not an option.

## Later

- **Emergency offline lease.** For VPS downtime: the participant's AxoSync shows an activation
  request (the install's public key, account, unit, build), staff produce a short signed lease
  and wrapped key with an offline ConfigGen command from backups (the KEK and lease-signing key
  from the password manager, the wrapped CEK from the latest DB backup), and email it back. The
  file is only usable by that install. Tabled; it needs those secrets on a staff machine, so it
  should stay rare.
- **Watermarking**, as above.
- **Protected config parser** in AxoSync.
- **Platform attestation** on iOS and Android (App Attest, Play Integrity), which can prove a
  genuine app is calling. Desktop has no equivalent.

## Decisions so far

- **Offline use is required**, through signed leases: 7 days by default, up to 30 for one account
  on request.
- **One general default config** plus experimental configs issued by hand; staff confirm every
  assignment.
- **Configs aren't versioned.** A different config is a new `Config` row.
- **Deliveries are bound to an install and a build** (layers 1–3); a valid token alone yields
  nothing usable.
- **Install limit: 6 per account by default**, one per target platform plus one spare, with a
  per-account override; idle installs expire after 60 days.
- **Per-release secrets are protected with Reactor virtualization**, with AxoSync's own crypto
  inside the protected methods. No native component. TINS.Core stays unprotected (hot paths).
- **The two units without a serial** are replaced before the config-less build ships.
- **2FA (TOTP plus recovery codes) is optional for every account and required for privileged
  roles** (Staff, Admin, Manufacturer, Publisher). Refresh never asks for a code.
- **The browser head gets no configs**, until the pipeline runs on the unit (`msv4`) or in the cloud.
- **`Device` is renamed `ClientInstall`**: in this service a device is an Intelimensa unit, and a
  client install is a registered copy of AxoSync.
- **A staged update fetches the new build's wrapped key before installing**, so offline use
  survives an update; a failed fetch never blocks the update.
- **The KEK is a single secret in configuration** with a key ID; backups in Proton Pass; operational
  copies on the VPS outside the app directory and the backup set.
- **The pipeline stays on the PC** until `msv4` is proven.
- **Plaintext sources stay in the AxoSync repo for now**, while one person has access. Before anyone
  else gets access, move them out *and* rewrite the history (it holds every past config plus
  `ConfigPasswords.cs`). Until then: 2FA (ideally a hardware key) on the GitHub account(s) with
  access, the repo private, and no third-party GitHub Apps or Actions with read access.

## Open questions

1. **Browser and cloud pipeline:** tabled until `msv4` ships or cloud compute is available.

## As built (server)

The server side is on `accounts`' `remote-config` branch. Where it settled something open above, or
departed from it:

- **Unbound tokens still exist.** A login without `install` (Quarry, the publish scripts, AxoSync builds
  from before this) gets tokens bound to no install. They work as before for everything except configs:
  the config endpoints need a bound token (policy `InstallClient`, else 403) and its proof.
- **Proofs** are checked by `Security/InstallProofMiddleware` for every `/api` request on a bound token,
  after authorization; login and refresh check their own (`Api/Auth/AuthEndpoints.cs`). The login body
  carries both public keys, and the whole body is covered by the proof, so there is no separate
  signature over the agreement key.
- **A removed install stays removed.** Its signing key can't register again (400 `install_invalid`); the
  client makes new keys and signs in as a new install.
- **Builds:** only a *published* release's artifact with a release secret, not blocked, gets keys; a draft
  doesn't. `version=development` uses `Configs:DevelopmentReleaseSecret`, which the server refuses to start
  with outside Development. The secret travels in `X-Release-Secret` on the artifact upload (or the staff
  Releases form); the response's `hasReleaseSecret` says whether the build will receive configs.
- **Errors** are `{"error": code}`: `install_proof_required`, `install_nonce_stale`, `install_proof_invalid`,
  `install_revoked` (401); `install_invalid` (400) and `install_limit` (409) at login;
  `version_and_platform_required` (400), `build_not_supported` (403), `not_registered`, `not_assigned`,
  `no_config` (404) and `config_delivery_unavailable` (503) from the config endpoints.
- **The staff alert** for many new installs is a warning in the server log, not an email.
- **Testing:** `scripts/test-server.sh start` turns delivery on with throwaway keys, and
  `dotnet run scripts/config-delivery-smoke.cs` drives the whole flow against it as AxoSync and ConfigGen
  would (about 50 checks). The format tests are `ConfigDeliveryVectorTests`.
