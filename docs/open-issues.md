# Open issues

Known problems and loose ends, roughly in order of urgency. Written 2026-10-17. Delete an entry once
it's handled.

## Quarry (firmware repo) is behind the server

The design docs (`DEVICE_IDENTITY.md`, `MindStoneQuarry-design.md`) were updated for "no printed
code" and held serials, but Quarry's code wasn't:

- **`RekeyReason.Relabel`** is still in `quarry/src/Intelimensa.Quarry.Core/Accounts/AccountsModels.cs`
  (and in the tests' fake `/options` responses). The server now refuses `Relabel` with a `400`.
- **`WorkflowStep.PrintLabel`** (`Workflow/UnitSession.cs`, `ProvisioningWorkflow.cs`, `SecretCode.cs`)
  assumes the code may be printed. It never is: the step should go, and the code should be dropped
  right after `confirm`.
- **No held-serial path.** The normal flow is now: the operator enters the serial printed on the
  casing, and Quarry calls `rekey` (the server records the reason as `Initial`), then flashes and
  confirms. Quarry only knows `reserve` for new units.

## Production and operations

- **No off-server backups.** The VPS plan has no backup add-on, and both the nightly backup (if the
  cron job in `deployment-vps.md` step 13 is set up) and the deploy script's pre-deploy backups sit
  on the same disk as the database. Decide where off-server copies go.
- **The release signing key is a test key.** Production uses the YubiKey's PIV slot 82 key,
  certificate "AxoSync release signing (test)". Moving to a real key means changing
  `Releases__SigningPublicKey` (or `Releases__SigningPublicKeys__N`) on the server *and* the key compiled into AxoSync, at the same time.
- **Pre-deploy backups are never pruned** (`/var/www/accounts/backups/accounts.pre-deploy-*.db`,
  one per deploy).
- **Product codes not in use.** `Manufacturing:ProductCodes` maps `msv1` and `msv2` as well as
  `msv3`, but the firmware docs say MS-V1/MS-V2 units in circulation have no identity and `MSV3` is
  the only product code in use. If no new MS-V1/V2 units will ever be made, remove those two
  entries so nobody can mint `MSV1`/`MSV2` serials by mistake. Confirm before real serials are
  printed.
- **No uptime monitoring** (see the runbook's "Open questions").

## Device data model

- **Units don't record which catalog build they were flashed with.** `BciDevice` and
  `BciDeviceEvent` keep version strings only. "Which units carry withdrawn build X" is a string
  match, and the rule "only catalog builds can be recorded" lives in the `/Manufacturing` page
  (`ResolveBuildAsync`), not in `UnitProvisioning`, so the API's `reserve`/`confirm`/`rekey`/`firmware`
  still accept any version text. Fix: a nullable `FirmwareBuildId` on the event (and the unit's
  current build), resolved inside `UnitProvisioning`, with the API taking a build id.
- **"Held" isn't an explicit state.** It's inferred (`Reserved` with no code hash), so it can't be
  filtered in a database query, and the hold event is a `Reserved` event told apart only by its note
  text. `Reserved` also covers "awaiting first confirm" and "rework pending". Fix: a `Held` status
  and a `Held` event type.
- **Voiding is permanent and allowed on held serials.** A voided serial can never be held or
  issued again, so a mis-click scraps a casing that's already printed. Either refuse void for held
  serials, or allow un-voiding a unit that was never confirmed.
- **The `/Manufacturing` page can't record a firmware write that keeps the identity** (the API's
  `/units/firmware` has no page equivalent). Without Quarry, reflashing a manufactured unit by hand
  means a rekey.
- **Smaller ones:**
  - `reserve` writes `CurrentFirmwareVersion` before anything is flashed, unlike `rekey` and the
    bootloader version, which are recorded as *intended* and applied at `confirm`.
  - `ProducedAt` is set at reserve/hold and overwritten at the first confirm, so for an unconfirmed
    unit it's a placeholder. It duplicates `ManufacturedAt` except for legacy units.
  - The region exists only inside the serial string; a column would be needed for per-region
    queries.
  - `FirmwareKind` and `FirmwareBuildStatus` have no explicit numeric values but are stored as
    integers, so reordering either enum would silently change existing rows.

## Firmware catalog

- **A bootloader-only hex passes as an MS-V1/MS-V2 build.** It reads as a `Single` release image,
  so it's accepted if someone picks `msv1`/`msv2` as the device type (noted in `CLAUDE.md`).
