# Preference slots and game results (design draft)

Status: **draft, nothing implemented.** Agreed in discussion with the AxoSync side; the server is built first, then
AxoSync follows. Two features share one pattern (an account-scoped document that AxoSync reads and writes through the
JSON API, with the contract pinned by a shared fixture):

1. **Preference slots**: five named profiles per account that follow the user across machines. Any signed-in account.
2. **Game results**: one encrypted bundle per game session (settings snapshot + trials + results). Study participants only.

Neither feature gates anything in the app; AxoSync works signed out and offline, falling back to local state.

## 1. Preference slots

### Data model

```
PreferenceSlot
  AccountId   int       FK Account, part of the key
  Slot        int       1..5, part of the key (CHECK constraint)
  Name        string    1..40 chars, trimmed, no control characters
  Payload     string    JSON text (see below), at most 32 KiB
  Version     long      starts at 1, +1 on every successful write; the ETag
  UpdatedAt   datetimeoffset
```

Rows are created lazily. `GET /api/preferences` always returns five entries; a slot that has no row is reported as
`{ slot, name: "Profile N", version: 0, empty: true }`. Staff never edit these; they belong to the user.

### Payload

A sparse override map. **A key that is absent means "use the loaded config's default"**; that is the rule AxoSync has
today and it must survive (a later release can then change a default for users who never touched it).

```json
{ "v": 1, "values": { "KeybindLeft": "A", "TrackBallGameSensitivity": "0.4" } }
```

- Keys are AxoSync's `Preferences.Keys` names; values are strings. The server does not know any key. It checks only: valid
  JSON, an object with `v` (integer >= 1) and `values` (object of string -> string), at most 256 keys, key length <= 64,
  value length <= 1024, whole payload <= 32 KiB. Unknown properties other than these are rejected so the format can't
  silently grow.
- An empty slot (`values: {}`) is "pure config defaults". Clearing a slot is `DELETE`, which removes the row (the name
  resets too).
- `v` is the payload format version, not the key set. Key renames are migrated by the client; the server never rewrites a
  payload.

### Endpoints (JWT bearer, any active account)

| Method | Path | Notes |
|---|---|---|
| GET | `/api/preferences` | list: `[{ slot, name, version, updatedAt, empty }]` x5. No payloads. |
| GET | `/api/preferences/{slot}` | the slot with its payload; `ETag: "<version>"`. 404 only for a slot number outside 1..5. |
| PUT | `/api/preferences/{slot}` | replace name + payload. **`If-Match` is required** (`"0"` = create, only if no row). 412 when the version differs, with the current slot in the body so the client can merge without another GET. Returns the new slot. |
| PATCH | `/api/preferences/{slot}/name` | rename only (also needs `If-Match`; doesn't touch the payload). |
| DELETE | `/api/preferences/{slot}` | `If-Match` required. 204; also fine if the row never existed. |

Whole-document replace on purpose: with a sparse map a deleted key is just an absent key, so no patch/tombstone
semantics are needed. Concurrency is optimistic (`Version`), not locked. Two machines on one account is the normal case, so
412 is expected and the client resolves it with a key-level merge against the last version it synced (client side; the
server just reports the conflict).

Errors follow the existing convention (bare JSON string for 400/404, ProblemDetails where `Results.Problem` is used).
Accounts that are revoked or expired are refused as everywhere else.

### Not on the server

The *active* slot of a running instance (instances can run side by side) and everything machine-bound (serial port, theme,
and so on) stay on the machine. Which keys go in a slot is AxoSync's decision, not this server's: see the AxoSync design.

### Client behaviour the server relies on (for context)

- First sign-in with a local `preferences.ini` and slot 1 empty: the account-scoped keys migrate to slot 1.
- Signing in to an account whose slots are filled while a modified local file exists: AxoSync offers to upload the file to
  a chosen slot and/or replace the local settings with an existing slot.
- Writes are debounced and flushed on exit; offline edits are kept in a local cache and pushed on the next connection.

## 2. Game results

### Privacy rules

- Accepted only for an account with an **active** `StudyParticipation` (same gate as `/api/telemetry`); everything else gets
  403. The client also skips collection when `IsStudyParticipant` is false, but the server is the authority.
- Withdrawal is prospective, as for telemetry: existing rows are kept, new uploads get 403. A client holding queued
  bundles drops them on that 403.
- The bundle contains the config's **identity only** (key name and a hash of the config file), never any config contents.
- Nothing readable is written on the participant's computer: AxoSync keeps queued bundles only as the encrypted envelopes
  below.

### Envelope (what the client stores and uploads)

The client encrypts to a public key compiled into AxoSync (a list, like the update-signing keys; any listed key may be
used). Only this server holds the private keys.

Scheme `v1`: ECDH on P-256 with a fresh ephemeral key per bundle, HKDF-SHA256, AES-256-GCM.

```json
{
  "v": 1,
  "kid": "gr-2026-1",
  "epk": "<base64, 65-byte uncompressed P-256 point>",
  "nonce": "<base64, 12 bytes>",
  "ct": "<base64, ciphertext followed by the 16-byte GCM tag>"
}
```

- `shared` = the 32-byte X coordinate of ECDH(ephemeral private, server public).
- `key` = HKDF-SHA256(ikm = shared, salt = empty, info = UTF-8 `"intelimensa-axosync-game-result-v1\n" + kid`, length 32).
- Plaintext = the UTF-8 bytes of the bundle JSON (below).
- **AAD** (authenticated, not secret), UTF-8, every line ending in `\n`:

```
intelimensa-axosync-game-result-v1
<user>               the JWT `sub` of the account that produced it
<sessionId>          the bundle's session GUID, lowercase, hyphenated
<gameType>           e.g. trackball | selector
```

The server rebuilds the AAD from the authenticated user and the request's `sessionId` / `gameType` fields, so an envelope
can't be replayed under another account, session or game type. A format change bumps the `v1` tag and changes the client,
the server and the fixture together (the same discipline as release signing).

The envelope does **not** prove who sent it (anyone can encrypt to a public key); the authenticated upload does.

### Bundle (plaintext, schema `v1`)

```json
{
  "schema": 1,
  "sessionId": "5b0c6d0e-2d0f-4c52-9e3d-2a3f6f8a9b11",
  "gameType": "trackball",
  "startedAt": "2026-10-21T09:14:03+00:00",
  "endedAt": "2026-10-21T09:31:47+00:00",
  "app": { "version": "0.1.0-alpha.4" },
  "config": { "key": "ms5", "sha256": "<hex of the shipped config file>" },
  "unit": { "serial": "MSV3G0123456" },
  "slot": { "number": 2, "name": "Morning" },
  "settings": { "TrackBallGameSensitivity": "0.4" },
  "environment": { "baselineStrategy": "DynamicMAD" },
  "trials": { "header": ["..."], "rows": [["..."]] },
  "results": { "performance": [["..."]], "bandwidth": [["..."]], "markers": [["..."]] },
  "summary": { "difficultyLevel": 3, "accuracy": 0.82, "successfulCommands": 41 }
}
```

- `settings` is the **resolved** value of each setting of that game (after clamping), not just the user's overrides, so a
  result is interpretable without knowing the config defaults of that day.
- `slot` is informational (the name may change later); the snapshot in `settings` is the truth.
- `summary` carries the headline numbers the server indexes. Everything else is stored as an opaque JSON blob.
- The exact `trials` columns come from the games (`Trial.Header`); the server doesn't interpret them in v1.
- Size cap after decryption: 1 MiB; the request body cap: 2 MiB.

### Data model

```
GameResult
  Id               long
  AccountId        int       FK Account
  AccountDeviceId  Guid?     FK AccountDevice; resolved from unit.serial when the unit is paired to this account
  SessionId        Guid      client-generated; UNIQUE (AccountId, SessionId) = idempotency
  GameType         string
  StartedAt, EndedAt   datetimeoffset
  ConfigKey        string    indexed
  ConfigSha256     string
  AppVersion       string
  DifficultyLevel  int?      from summary
  Accuracy         double?
  SuccessfulCommands int?
  Bundle           string    the decrypted JSON text
  SchemaVersion    int
  KeyId            string    which server key opened it
  ReceivedAt       datetimeoffset
```

`TelemetryEvent` stays as is. It could later be folded into this, but that is not part of this change.

### Endpoint

`POST /api/game-results` (JWT bearer). Body: `{ "sessionId", "gameType", "envelope": { ... } }`.

| Result | When |
|---|---|
| 201 | stored |
| 200 | `(account, sessionId)` already stored: the upload was a retry; the client deletes its queued copy |
| 400 | malformed body, unknown `v`/`kid`, decryption/authentication failure (wrong account, tampered, wrong AAD), plaintext not valid for `schema`, size caps |
| 401 | not authenticated |
| 403 | account has no active study participation, or is revoked/expired |
| 413 | body over the cap |

A 400 for a bad envelope tells the client the entry can never succeed; it drops it (and logs). 403 also drops it
(consent). Anything 5xx or a network failure keeps the entry queued.

### Key management

- A key pair per `kid`: `openssl ecparam -name prime256v1 -genkey -noout` (the private key never leaves the server and
  the operator's backup). The public key goes into AxoSync (`GameResultKeys.txt`-style, like `UpdateSigningKeys.txt`).
- Server configuration: `GameResults:Keys` is a list of `{ Kid, PrivateKeyPem }`, supplied from the environment/secret
  store, never committed. The newest is the one clients should use; older ones stay so queued bundles still open. Removing
  a key makes its bundles unreadable, so retire one only when no client can still hold such a bundle.
- If no key is configured the endpoint returns 503 rather than accepting data it cannot open.
- At rest the bundle is plaintext in the database (the indexed columns need it anyway); protecting the database is a
  deployment matter (see `deployment-vps.md`).

### Retention and the participant's view (open)

Not decided: how long results are kept, whether a participant can see/download/delete their own results in the portal, and
what "withdrawal" means for old results beyond "no new uploads". These need an answer before this goes live with real
participants.

## 3. Contract fixtures

Following the workspace rule, a contract change starts with its fixture in the owner (this repo):

- **`testdata/api-vectors.json`** (generator `scripts/gen-api-vectors.py`) gets the new wire examples:
  `PreferenceSlotList` (response), `PreferenceSlot` (response), `PutPreferenceSlotRequest`, the 412 body, `GameResultRequest`,
  and the new error families. Round-tripped by `ApiVectorTests`; AxoSync's hand-written JSON checks against its copy.
- **`testdata/game-result-envelope-vectors.json`** (new; generator `scripts/gen-game-result-vectors.py`, deterministic with
  fixed ephemeral keys and nonces): throwaway server key pair, a few valid envelopes (with their AAD and plaintext), and
  negative cases (wrong AAD user/session/game type, tampered `ct`/`epk`/`nonce`, unknown `kid`/`v`, truncated tag). The
  server's decryptor and AxoSync's encryptor both test against it. The test key is a throwaway and must never be trusted
  anywhere. Add it to `vectors.manifest` with a copy in `axosync` (on the branch that gets the client code).
- A payload-validation table (accepted and rejected slot payloads) can live in the server tests only, since the client
  never needs to reject on those rules.

## 4. Build order (server)

1. Fixtures first: extend `gen-api-vectors.py`, add `gen-game-result-vectors.py`; run `sync-vectors.sh`.
2. `PreferenceSlot` entity + migration; `Api/Preferences/` contracts and endpoints; tests (`If-Match` semantics, lazy
   rows, limits, revoked account).
3. `GameResult` entity + migration; `Api/GameResults/` contracts, `EnvelopeDecryptor`, endpoint; tests (gate on
   participation, idempotency, AAD mismatch, unknown key, withdrawal).
4. Key configuration and the operator notes in `deployment-vps.md`; update `CLAUDE.md`'s API/data-model sections.
5. Then AxoSync: slot cache/sync and migration flows, then the outbox and the games' bundle builder.

## 5. Open points

- **AAD user identity.** The client doesn't receive an account id today. This draft uses the JWT `sub` claim (a label the
  client can read from its own access token; the server checks it against the authenticated user). Alternative: add an id to
  `TokenResponse` (a contract change).
- **Unit linkage.** `AccountDevice` pairing is not yet used by AxoSync (it doesn't call the devices endpoints), so the
  client sends only the serial from the unit's identity reply and the server resolves the pairing. Needs confirming that a
  serial always maps to at most one pairing per account.
- **Retention and participant access** (above).
- **Mobile/browser heads** have no file system for the client outbox; out of scope here.
