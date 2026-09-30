# Manufacturer scenario: putting a unit on the device list

How a manufactured BCI unit gets onto the device list (`BciDevices`) so a participant can later
register it. This is done by the **manufacturing station app, MindStoneQuarry**, signed in as a
user with the `Manufacturer` role. Staff and participants never create units this way. The app's
design is in the firmware repo (`MindStoneQuarry-design.md`); the server side described here is
built, the app is not.

## Steps

1. **Sign in to Quarry** with the station's own account (one per station). **[server built]**
2. **Pick device type, region and the firmware version** being flashed, and connect the unit.
3. **Reserve.** Quarry calls `POST /api/manufacturing/units/reserve`. **[server built]**
   - The server allocates the next serial (`PPPP-RVAA-AAAC`, e.g. `MSV2-G01S-ATCF`) and generates
     a random registration code (e.g. `066N9-6CWEA`).
   - The unit now exists as `Reserved`: it can't be registered by anyone yet.
   - The plaintext code is returned **once**; the server stores only its hash.
4. **Flash** the application image plus the per-unit identity record (serial and code), with
   code-protect on. **[app not built]** See the firmware repo's `DEVICE_IDENTITY.md`.
5. **Read the identity back** from the running unit (`dcGetIdentity`) and compare. **[app not built]**
6. **Confirm.** Quarry calls `POST /api/manufacturing/units/confirm` with the code it read back.
   **[server built]** If it matches the server's hash, the unit becomes `Manufactured` and is
   registerable.
7. **Print the label** with the serial and code, and ship the unit. **[app not built]** The label
   is a fallback for manual entry; participants normally never type either value.

## When things go wrong

- **Flash or readback fails:** re-flash and re-verify, or `void` the reservation. Voided serials
  are never reused.
- **Quarry loses the code after reserving:** `rekey` the serial for a fresh code, or `void` it.
- **Lost or leaked label on a shipped unit:** `rekey` it. The old code stops working immediately,
  the unit is re-flashed and confirmed again, and participants already paired to it are unaffected.
- **Confirm retried after a network error:** safe, confirm is idempotent.

## Legacy and manual entry

`/Staff/Devices` still lets staff add units one at a time or by CSV, but these are **backfill
only**: they're marked `Manufactured` with no registration code, so **they can't be newly
registered while code checking is on** (`Devices:RequireRegistrationCode`, default true). Use the
manufacturing flow for real units.

## Setting up a station

1. The station account signs up at the portal like any user, then staff grant the role (no UI yet):

   ```sql
   INSERT INTO AspNetUserRoles (UserId, RoleId)
   SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
   WHERE u.Email = 'station-01@example.com' AND r.Name = 'Manufacturer';
   ```

2. Revoking a station: set its account to revoked in `/Staff`. Manufacturing calls re-check the
   account on every request, so it takes effect immediately.
3. Product codes (4 characters, e.g. `MSV2`) come from `Manufacturing:ProductCodes` and the allowed
   region/variant characters from `Manufacturing:RegionCodes`, both in `appsettings.json` (currently
   placeholders). A device type or region not in those maps can't be manufactured, and bad entries
   stop the server from starting.

## Firmware updates

AxoSync updates `CurrentFirmwareVersion` / `LastFirmwareUpdatedAt` itself when it performs a
firmware upgrade, so manufacturing only supplies the initial version. **[endpoint not built]**

## Why the code exists

The serial alone is guessable and appears in many places (staff pages, the participant's status
page, paperwork). The registration code exists only on the unit, its label and as a hash on the
server, so registering proves you read the unit rather than merely knew its serial. It does not
defend against someone copying a unit's memory; only readback protection does that.
