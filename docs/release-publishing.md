# Publishing AxoSync releases and updating clients

How a build gets from the release script to participants' machines, and how AxoSync decides it
can trust it. The server side is implemented (`Publishing/`, `Api/Updates/`, `Pages/Staff/Releases`);
the AxoSync updater and the `publish.sh` rewrite live in the axosync repo.

## Trust model

AxoSync updates itself with an installer fetched from this server, and the Windows installer runs
elevated. TLS plus the SHA-256 on the page only proves the file wasn't corrupted; if the VPS were
compromised, an attacker could replace the installer for every client. So every build is **signed
offline** and the client verifies the signature against a public key compiled into AxoSync:

- The private key lives on the publisher's machine only. It is never uploaded to this server.
- The server stores and relays each artifact's signature and, when `Releases:SigningPublicKey`
  is set, rejects uploads whose signature doesn't verify (a typo guard, not the security boundary:
  the client check is).
- A compromised server can withhold updates but can't make clients install anything it made up.
- Clients must also refuse to install a version that isn't higher than the running one, otherwise
  a malicious server could replay an old, validly signed, vulnerable build.

## Signature contract (server, publish script and updater must agree)

ECDSA, curve P-256, SHA-256, signature DER-encoded then base64. It signs this exact UTF-8 text,
each line ending in `\n` (including the last):

```
intelimensa-axosync-release-v1
<version>            e.g. 1.4.2 (no build metadata)
<platform>           WindowsX64 | MacOSArm64 | MacOSX64 | LinuxX64
<sha256>             lowercase hex of the installer file
<size in bytes>
```

`Publishing/ReleaseSigning.cs` defines it. Change the format by bumping the `v1` tag and
changing all three places together.

**The shared fixture** `testdata/release-signing-vectors.json` pins the contract: valid signatures for every
platform plus tampered fields, wrong tag/line endings, a wrong signer and bad encodings, each with the verdict
every implementation must give. It is checked by `Intelimensa.Accounts.Tests` (`dotnet test`), by AxoSync's
`UpdateTrustVectorTests` (a byte-identical copy) and by `publish.sh --selftest`. A format change starts here:
edit `scripts/gen-signing-vectors.py` for the new tag, regenerate (`python3 -I scripts/gen-signing-vectors.py`),
then run `./sync-vectors.sh --fix` in the workspace root to copy it to axosync and commit in each repo. The
test key in it is a throwaway: never add it to a trusted-key list.

Key setup, once:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out release-signing.pem          # keep private, back up
openssl pkey -in release-signing.pem -pubout -outform DER | base64 | tr -d '\n'   # public key for the server and AxoSync
```

Signing a file (this is what the publish script does):

```bash
printf 'intelimensa-axosync-release-v1\n%s\n%s\n%s\n%s\n' "$VERSION" "$PLATFORM" "$SHA256" "$SIZE" > stmt.txt
openssl dgst -sha256 -sign release-signing.pem -out sig.bin stmt.txt && base64 < sig.bin | tr -d '\n'
```

Works with both macOS's LibreSSL and OpenSSL 3+. Losing the private key means shipping a new
public key inside AxoSync, which existing installs can't be updated to without a manual install.

## API

All JWT (`Authorization: Bearer ...` from `POST /api/auth/login`). Enum values are names.

**AxoSync** (`ReleaseClient` policy: any account that is still active and unexpired, re-checked on
every call, so revoking an account stops updates at once):

- `GET /api/releases/latest?platform=MacOSArm64&current=1.4.2[&includePrerelease=true]` returns
  `{updateAvailable, mandatory, version, notes, minimumVersion, publishedAt, artifact:{fileName,
  sizeBytes, sha256, signature, downloadUrl}}`. `current` may carry a `+sha` suffix. The newest
  published SemVer that has a build for the platform wins (pre-releases only when asked). With a
  signing key configured, unsigned builds are never offered. `mandatory` is true when `current` is
  below the offered release's `minimumVersion`.
- `GET /api/releases/{version}/{platform}/file` streams the installer (Published only; range
  requests supported; ETag is the SHA-256).

**Publisher** (`Publisher` role, grant with `scripts/setrole.sh`; account re-checked per call):

- `PUT /api/publishing/releases/{version}` body `{notes, minimumVersion}` creates the draft, or
  updates a draft's notes and minimum version. Idempotent so the script can be re-run. A release
  that is no longer a draft returns 409.
- `PUT /api/publishing/releases/{version}/artifacts/{platform}?fileName=...` body is the raw
  installer, header `X-Release-Signature` the base64 signature. Drafts only. Replaces a previous
  upload for that platform.
- `POST /api/publishing/releases/{version}/publish` and `.../withdraw`. Publishing needs at least
  one artifact, and every artifact signed while a key is configured.
- `GET /api/publishing/releases/{version}` shows the release and its artifacts.

Errors are `{ "error": "..." }` with 400 (invalid), 404, 409 (wrong state), 401 (no or unusable
account) or 403 (missing role).

## Rules worth knowing

- **Builds are immutable once published.** A fix is a new version. Uploads and removals are
  drafts-only, on the staff page as well as the API.
- **Withdraw rolls the "latest" back.** Clients already on the withdrawn version aren't downgraded.
- **`minimumVersion`** is the lever for forced updates: set it on a release and clients below it
  get `mandatory: true`. Leave it empty for an optional update. It can't exceed the release's own
  version.
- **The staff page** (`/Staff/Releases`) can still upload by hand; with a key configured it needs
  the signature pasted in too.
