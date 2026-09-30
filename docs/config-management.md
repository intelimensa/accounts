# Config file management — design

Status: **agreed design, not built**. The only parts that exist today are the `Configs` metadata
table and per-device assignment (`AccountDevice.AssignedConfigId`, set by staff at
`/Staff/DeviceAssignments`). This doc replaces the "Config delivery model (planned)" section of
`CLAUDE.md`.

## Where we are today

- Config sources are plaintext INI files in `axosync/ConfigGen/Assets/`.
- `ConfigGen` encrypts each one with a **fixed, human-memorable passphrase per config stem**
  (`Shared/ConfigPasswords.cs`). The format is `salt(32) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`,
  with PBKDF2-SHA256 at 600k iterations and AES-256-GCM.
- The encrypted files are **embedded in every AxoSync installer**. A Release user types the
  passphrase into `ConfigSelectionDialog`, and `ConfigLoader.DecryptResource` decrypts the file in
  memory.
- As a result, anyone with the installer and a passphrase can decrypt that config forever.
  Account status, expiry and staff assignment in this service have no effect on that.

## How configs are used

- There is **one general config**. It's the default for typical participants.
- A few **experimental configs** exist, and staff issue them by hand to specific participants.
- Configs **aren't versioned**. A materially different config is simply a new config (a new
  `Config` row with its own key). Replacing a config's file in place only happens for key
  rotation or a fix (see "Rotation, revocation and compromise").
- Configs aren't customized per participant today. That may change, and it would arrive together
  with watermarking (see "Threat model", item 2), which is a separate, pending discussion.

## Goals

1. Only a signed-in, usable account (active and not expired) can decrypt a config, and only when
   it has a registered device that staff have assigned that config to.
2. The app works offline for a bounded period. Revocation and expiry take effect at the next
   online launch, or when the offline lease runs out. Nothing needs cleaning up by hand on
   participants' machines.
3. The plaintext never sits on the server's disk, in its backups, or on the participant's disk.
4. Reuse AxoSync's existing encrypted format and `DecryptResource` unchanged.
5. Rotating a config's key is cheap: re-run ConfigGen and replace the file.

Not a goal: stopping an authorized participant from extracting the plaintext from their own
running process. That is out of scope, as stated in `CLAUDE.md`.

## Proposed flow

### 1. Producing an encrypted config (staff, offline)

ConfigGen gets a "publish" mode. For each catalog entry, it:

- generates a **fresh random content key (CEK)**: 32 random bytes, written out as a base64 string;
- encrypts the INI file with the **existing format and code path**, passing the CEK string as the
  "password";
- writes out two files: `<stem>.cfg` (the ciphertext blob) and `<stem>.key` (the CEK).

Feeding a random 256-bit string through PBKDF2 at 600k iterations adds nothing to security, since
the key already has full entropy. It costs about 100–300 ms per decrypt. In return, the format
stays byte-identical and the client's crypto code doesn't change, which is a good trade. If we
later want to drop the KDF cost, we can add a format version byte, but we don't need to now.

Once this ships, `ConfigPasswords.cs` and the configs embedded in the installer go away. AxoSync
ships with **no** configs.

### 2. Uploading (staff, `/Staff/Configs`)

- Staff upload `<stem>.cfg` together with `<stem>.key` to a `Config` row. Uploading to a row that
  already has a file replaces it (see "Rotation, revocation and compromise").
- The server:
  - validates the blob layout (at least 60 bytes);
  - **test-decrypts the blob with the supplied key** and checks that the result parses as INI. A
    mismatched pair is then rejected at upload time rather than on a participant's machine. The
    plaintext is only held in memory and then discarded;
  - computes the blob's SHA-256;
  - stores the blob through an `IConfigStorage` outside the app directory. `IConfigStorage` has
    the same shape as `IReleaseStorage`;
  - stores the CEK in the DB **wrapped under a server key-encryption key (KEK)** (see "Server key
    management"). A leaked DB or backup alone then yields no usable keys.
- Staff delete the `.key` file from their machine after uploading. The server is the only
  long-lived holder of CEKs.
- A config with no uploaded file can't be delivered. A retired config (`RetiredAt` set) isn't
  delivered, and can't be newly assigned.

### 3. Assignment

A participant registers a device with its serial number and registration code. Staff then assign
a config to that `AccountDevice`, as they do today.

- The staff assignment form **pre-selects the default config** (the one `Config` row marked
  `IsDefault`). Staff confirm it with one click, or pick an experimental config instead.
- Nothing is assigned automatically. Until staff confirm, the participant's app gets no config.
  This keeps "staff match the participant, unit and calibration" true.
- At most one config is the default. Marking another config as the default unmarks the previous
  one.

### 4. Delivery and decryption (AxoSync, on every online sign-in)

1. AxoSync signs in (`/api/auth/login` or `refresh`). It knows which BCI device is connected from
   the device's serial.
2. `GET /api/devices/{serial}/config` (JWT) checks all of these:
   - the account is usable;
   - an active `AccountDevice` exists for this serial;
   - that pairing has an assigned config that has an uploaded file and isn't retired.

   If all checks pass, the response is the config's metadata, the key and a signed offline lease
   (section 5):

   ```json
   { "configKey": "ms5", "sha256": "…", "cek": "<base64>", "lease": { … }, "leaseSignature": "…" }
   ```

   The response carries `Cache-Control: no-store`. Every issuance is logged with the account,
   device, config, blob SHA-256 and time.
3. `GET /api/configs/{key}/blob` returns the ciphertext.
   - It's gated on the same entitlement.
   - It's a separate request that can be cached (ETag = SHA-256), because the blob is useless
     without the key.
   - AxoSync re-downloads the blob only when the SHA-256 differs from its cached copy.
4. AxoSync calls `DecryptResource(blob, cek)`, parses the INI and runs the pipeline from memory.
   **Nothing decrypted is written to disk.** The CEK is persisted only inside the DPAPI-wrapped
   offline lease (section 5).

**Why the client decrypts rather than the server sending plaintext:** trust is the same either
way, because the server holds the key in both cases. But with client-side decryption:
- the existing client code path stays unchanged;
- the blob can be cached and transferred separately from the key;
- the only secret in each response is 32 bytes behind `no-store`, and the blob never needs
  special handling.

### 5. Local storage and offline use

**Requirement: the app must work offline.** We can't assume participants have a reliable
connection. Offline use is therefore built as a bounded **offline lease**, renewed whenever the app
is online.

**What the server issues.** Alongside the CEK, the server issues a signed lease:

```json
{ "accountId": 42, "serial": "MS05A0K3F9Q7", "configKey": "ms5", "sha256": "…",
  "issuedAt": "…", "notAfter": "…" }
```

- The lease is signed with Ed25519 or ECDSA using a server lease-signing key. AxoSync embeds the
  matching public key.
- `notAfter` = min(now + lease length, account `ExpiresAt`).
- **The lease length defaults to 7 days** (`Configs:OfflineLeaseDays`).
- Staff can set a longer lease for one account (`Account.OfflineLeaseDaysOverride`, nullable),
  for example when a participant contacts support with a plausible reason such as fieldwork or
  travel.
- The override is capped at **30 days** (`Configs:MaxOfflineLeaseDays`), so a typo can't grant a
  year.
- The override is set on the account rather than per device, because connectivity depends on the
  participant's circumstances.

**What AxoSync persists.**

- The encrypted blob, which can be cached freely.
- The CEK and the lease, wrapped together with DPAPI (`CurrentUser`), so they can't be copied to
  another user or machine.
- A "latest time seen" high-water mark, kept inside the same DPAPI-wrapped record.

**At launch.**

- **Online:** refresh the token and fetch a fresh lease and CEK. Download the blob too if its
  SHA-256 changed. Then overwrite the cache.
- **Offline:** use the cached CEK only if all of these hold:
  - the lease signature is valid;
  - `now < notAfter`;
  - the clock hasn't moved backwards past the high-water mark (with some tolerance);
  - the connected BCI device's serial matches the lease's `serial`.
- **Otherwise:** AxoSync explains that it needs to go online.

**Revocation and expiry.** Revocation takes effect at the next online launch, or at the latest
when the lease expires. The lease length trades offline tolerance against how long a revoked
participant keeps access, so extensions should be granted with that in mind.

**Plaintext stays off disk.** The plaintext is still never written to disk. It's re-derived from
the blob and CEK on every launch.

**What this does and doesn't protect.**
- DPAPI stops casual copying to another machine or another Windows user.
- It does **not** protect against the participant themselves: any process running as that user
  can unprotect the record.
- The expiry and clock checks also run on a machine the participant controls.

Offline leases therefore keep honest users honest and limit accidental spread. They are not a
defense against a determined participant (see "Threat model" below).

Mobile versions of the app would store the same record in the iOS Keychain or Android Keystore.

## Data model changes

| Change | Notes |
|---|---|
| `Config`: add `BlobStorageKey`, `BlobSha256`, `BlobSize`, `WrappedCek`, `KekId`, `UploadedAt`, `UploadedByUserId` (all nullable until the first upload) | One encrypted file per config. `KekId` identifies which KEK wrapped the CEK, so the KEK can be rotated later. |
| `Config`: add `IsDefault` (bool, at most one true) and `RetiredAt` (nullable) | `IsDefault` sets the default pre-selection. `RetiredAt` stops a config being delivered or newly assigned. |
| `Config`: drop `CurrentVersion` | Configs aren't versioned. |
| `Account.OfflineLeaseDaysOverride` (int, nullable) | Staff set it via the `/Staff` account row. Null means the default. Capped at `Configs:MaxOfflineLeaseDays`. |
| New `ConfigKeyIssuances` table (or reuse `BciDeviceEvent`) | Audit record of who received which key, and when. |

## Rotation, revocation and compromise

- **Suspected key leak, or a fix to a config:**
  1. Run ConfigGen publish, which creates a fresh CEK.
  2. Upload the result to the same `Config` row, replacing the old file.
  3. The new blob SHA-256 makes clients fetch the new file and key at their next online sign-in.

  The old key is no longer issued. A participant still offline keeps using the old file until
  their lease runs out.
- **A materially different config:** create a new `Config` row and reassign the participants who
  should get it. Retire the old row once nobody needs it.
- **Participant revoked or expired:** no new key or lease is issued. Their access ends at the next
  online launch, or when their current lease expires. A participant who already extracted the key
  from memory keeps that config's current content, which is why rotation exists.
- **KEK compromise:** generate a new KEK and re-wrap all CEKs under it. This is a server-side
  maintenance command, with no participant impact.

## Server key management

The server holds two long-lived secrets. (The existing JWT signing key is the same kind of
secret.)

- **The KEK**, which wraps CEKs in the database. It's a **single secret in configuration**
  (`Configs:KeyEncryptionKey`, base64, with a `Configs:KeyEncryptionKeyId`), loaded like
  `Jwt:SigningKey`:
  - from .NET User Secrets in Development;
  - from an environment variable or file on the VPS.

  The app fails at startup if the KEK is missing. ASP.NET Data Protection was rejected: its key
  ring would be one more thing to protect and back up, and its automatic rotation adds little
  when each config already has its own key.
- **The lease-signing private key.**

**Where they live operationally.**
- On the VPS, as files or environment variables readable only by the service user.
- Outside the rsync-deployed app directory, and **outside whatever the backup job copies**.

The running server must be able to read them, so anyone who gains control of the VPS gets them
along with the DB. The KEK does not defend against that. It defends against the DB or a backup
leaking on its own, which is the more likely accident.

**Losing the KEK is recoverable.**
- The plaintext sources remain in the AxoSync repo. If the KEK is lost, generate a new one,
  re-run ConfigGen publish and re-upload. Participants get the new files at their next online
  sign-in.
- The lease-signing key is also replaceable. But replacing it needs an AxoSync release with the
  new public key, which is why that key deserves a backup.

Neither is a crown jewel whose loss is catastrophic, so the backup copy doesn't need elaborate
protection.

**Backup copy.** A password manager with end-to-end encryption, such as Proton Pass, is a
reasonable place for these, provided that:

- the Proton account has a strong unique password and 2FA, ideally a hardware key;
- the secrets sit in a vault that isn't shared.

A paper copy of the secrets isn't needed, given the recoverability above. What should be written
down and stored physically is the Proton account's own recovery phrase.

**Where the real exposure is.** Ranked by what an attacker would gain:

1. **The plaintext INI sources** in `axosync/ConfigGen/Assets/`. They're in git, so they're on
   every clone, on the git host, and in the history. This outranks everything in this service.
   See "Decisions so far" for when to move them.
2. **The VPS.** It holds the KEK, the DB, the blobs and the signing key, and so every config. Keep
   SSH key-only, the OS patched, and a minimal set of services running.
3. **Staff accounts.** Any Staff account can upload configs and assign them. See the Staff 2FA
   open question.
4. **The password manager**, holding the backup copies.

## Threat model: extraction by a participant

A participant who controls the machine AxoSync runs on can get the plaintext config. Some ways:

- dumping process memory, or attaching a debugger and reading the parsed INI;
- patching or hooking the .NET assembly (dnSpy, Harmony) to log the output of `DecryptResource`.
  Obfuscation slows this down but doesn't stop it;
- unprotecting the DPAPI record as the same user. This yields the CEK and makes the offline lease
  checks moot;
- intercepting TLS with their own root CA. Certificate pinning raises the bar, but it can be
  patched out.

No key-delivery scheme fixes this. As long as the PC app needs the plaintext to run the pipeline,
the PC's owner can obtain it. What we can do, cheapest first:

1. **Limit the blast radius** (already in this design).
   - Each participant receives only their assigned config.
   - Each config has its own key, and replacing a config's file rotates that key.
   - Every key issuance is audited.
2. **Deterrence and traceability.**
   - Participant agreements that cover the software and configs.
   - Optionally, per-issuance **watermarking**: the server derives a unique, harmless variant of
     the config for each device, so a leaked copy identifies its source. This requires the server
     to produce per-device ciphertext: it holds the plaintext (encrypted under the KEK) and
     encrypts on issuance, instead of storing ConfigGen's output directly. The same change would
     support future per-participant calibration, so the two may arrive together.
   - Watermarking is a separate, pending discussion.
3. **Raise the cost.** These are speed bumps, worth adding only if cheap:
   - Native AOT for the parts of AxoSync that handle the config (much harder to patch than IL);
   - anti-debug checks;
   - avoiding a single obvious decrypt-and-parse chokepoint;
   - certificate pinning.
4. **Move the IP off the PC.** This is the only structural fix. The sensitive parts of the
   pipeline (filters, thresholds, calibration) would run in the MindStone firmware. The config
   would be delivered to the device encrypted to a device key held in the MCU (readout-protected,
   or in a secure element if the hardware has one), and the PC app would never see it. Extraction
   would then require attacking hardware.

   **Not viable on current hardware.**
   - The shipping MS-V2 uses a PIC32MX250F128D: 128 KB flash, 32 KB RAM, no FPU, no secure
     storage.
   - Code-protect is deliberately off (see `firmware/DEVICE_IDENTITY.md`), so a hobbyist
     programmer reads the whole flash. A config stored there would be *easier* to extract than
     from the PC app.
   - The chip also can't run the pipeline.

   **Storing configs on the device without running the pipeline there is strictly worse.** The PC
   still needs the plaintext, so that exposure remains, and a flash dump becomes a second
   exposure. Only running the pipeline on the device removes the PC exposure.

   **What a future hardware revision would need:**
   - an MCU with robust readout protection plus isolated key storage (e.g. an nRF53/nRF54-class
     part with TrustZone and a key management unit), or an external secure element;
   - enough compute for the pipeline;
   - a signed firmware-update path that doesn't depend on reading flash back.

   Even then, the risk shifts to lab-grade attacks (fault injection) rather than disappearing. On
   loaned units that come back at the end of the study, those attacks are also more likely to be
   detected.

   **Compute and battery cost.**
   - The full pipeline takes about 16 ms per run on a modern i7 with vectorized native code
     (FFTW/PocketFFT, OpenBLAS).
   - A Cortex-M33 like the STM32H5 (250 MHz, single-precision FPU) is plausibly one to two orders
     of magnitude slower on that workload. Unless the pipeline is restructured for embedded use,
     a run could take longer than the interval between runs.
   - Even if it fit, running the MCU flat out would draw roughly as much as the EEG front end and
     the radio combined. That means roughly halving battery life on a head-worn device, where a
     bigger battery isn't an option.

   These are estimates; profile before deciding.

   **Partial split.** Only the cheap decision layer (thresholds, calibration, classifier weights)
   would run on the device, with the PC sending features over BLE. This avoids the compute
   problem, but:
   - it adds BLE round-trip latency;
   - it exposes the decision layer to black-box queries. A simple (e.g. linear) decision function
     can be reconstructed from a modest number of them.

   **Conclusion.** On-device processing isn't worth it for IP protection alone. Revisit it only if
   the product wants it for other reasons (a standalone device, latency), and with a profile of
   which stages dominate the 16 ms.

For the current cohort (loaned devices, identifiable participants under agreements), the plan is:
- item 1 now;
- item 2's agreements now, and watermarking if and when it's agreed;
- item 3 opportunistically;
- item 4 evaluated separately with the firmware work.

## What changes in AxoSync

- **ConfigGen:** add the publish mode (fresh CEK, `.cfg` + `.key` output), and stop writing into
  `AxoSync/Assets/config`.
- **AxoSync:**
  - remove the embedded configs and the password dialog from Release builds;
  - add a config-fetch step after sign-in and device detection;
  - add a blob cache keyed by SHA-256;
  - add the DPAPI-wrapped offline lease with its checks;
  - embed the lease-verification public key.

  `DecryptResource` stays as it is.
- **Debug builds** may keep a local-file path for development convenience, still excluded from
  Release.

## Decisions so far

- **Offline use is required.** It's handled by signed offline leases (section 5).
- **The offline lease lasts 7 days by default.** Staff can extend it for one account on request,
  up to 30 days (`Configs:MaxOfflineLeaseDays`).
- **There is one general default config**, plus experimental configs issued by hand. The staff
  assignment form pre-selects the default, and staff confirm it. Nothing is assigned
  automatically.
- **Configs aren't versioned.** A different config is a new `Config` row. Replacing a file in
  place is only for key rotation or fixes. There is no version pinning.
- **The KEK is a single secret in configuration**, loaded like the JWT signing key, and it carries
  a key ID for later rotation.
- **Backup copies of server secrets** go in the password manager (Proton Pass, with 2FA). The
  operational copies live on the VPS, outside the app directory and the backup set.
- **The pipeline stays on the PC.**
  - On-device processing isn't feasible on the MS-V2 (PIC32), and on an STM32H5-class part it
    would cost too much battery for IP protection alone.
  - Revisit when choosing the MCU for the next hardware revision, and only if the product wants
    on-device processing for its own sake.
  - The nRF52840 port would need `APPROTECT` and has no key management unit, so an
    nRF53/nRF54-class part or a secure element is the likelier candidate.
- **Plaintext sources stay in the AxoSync repo for now**, while one person has access. Before
  anyone else gets access to that repo, move them out *and* rewrite its history, since the history
  holds every past config plus `ConfigPasswords.cs`. Until then:
  - the GitHub account(s) with access keep 2FA, ideally with a hardware key;
  - the repo stays private;
  - no third-party GitHub Apps or Actions get read access to the repo.

## Open questions

1. **Staff 2FA:**
   - Not required while Staff is limited to the co-founders.
   - Revisit when the service is deployed to the VPS. It's cheap there: authenticator-app codes
     (TOTP) are built into ASP.NET Core Identity.
   - It would apply to the Staff and Manufacturer roles only.
2. **Watermarking and per-participant configs:** pending a separate discussion. If adopted, the
   server encrypts per device on issuance, and the upload flow changes (see "Threat model",
   item 2).
