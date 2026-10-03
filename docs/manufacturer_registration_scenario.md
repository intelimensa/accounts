# Manufacturer scenario: putting a unit on the device list

How a manufactured BCI unit gets onto the device list (`BciDevices`) so a participant can later
register it. This is done by the **manufacturing station app, MindStoneQuarry**, signed in as a
user with the `Manufacturer` role. Staff and participants never create units this way. The app's
design is in the firmware repo (`MindStoneQuarry-design.md`); the server side described here is
built, the app is not.

## Steps

1. **Sign in to Quarry** with the station's own account (one per station). **[server built]**
2. **Pick device type, region and the firmware version** being flashed, and connect the unit.
   The server has no default region: Quarry may pre-select one but always sends it, and a missing or
   unknown region is refused. `GET /api/manufacturing/options` lists the accepted product codes,
   regions and rekey reasons.
3. **Reserve.** Quarry calls `POST /api/manufacturing/units/reserve`. **[server built]**
   - The server allocates the next serial (canonical form `PPPPRVAAAAAC`, e.g. `MSV2G01SATCF`,
     shown on the label as `MSV2-G01S-ATCF`; see below) and generates
     a random registration code (e.g. `066N9-6CWEA`).
   - The unit now exists as `Reserved`: it can't be registered by anyone yet.
   - The plaintext code is returned **once**; the server stores only its hash.
4. **Set the USB serial and flash.** **[app not built]** Quarry runs the FTDI tool to set the
   UART-USB bridge's serial number to the serial, then the MPLAB flash utility to program the
   application image plus the per-unit identity record (serial and code). The programmer erases all
   program memory, so the identity is written on every flash. See the firmware repo's
   `DEVICE_IDENTITY.md`.
5. **Read the identity back** from the running unit (`dcGetIdentity`) and compare it, and the USB
   serial, to what was reserved. **[app not built]**
6. **Confirm.** Quarry calls `POST /api/manufacturing/units/confirm` with the code it read back.
   **[server built]** If it matches the server's hash, the unit becomes `Manufactured` and is
   registerable.
7. **Print the label** with the serial and code, and ship the unit. **[app not built]** The label
   is a fallback for manual entry; participants normally never type either value.

## When things go wrong

- **Flash or readback fails:** re-flash and re-verify, or `void` the reservation. Voided serials
  are never reused.
- **Quarry loses the code after reserving:** `rekey` the serial for a fresh code, or `void` it.
- **Lost or leaked label, corrupted identity page, or hardware rework:** `rekey` it with a required
  reason (`Relabel`, `Reflash` or `Rework`, plus an optional note) and the firmware version you
  intend to flash. The old code stops working immediately and the unit goes back to `Reserved`
  until it's re-flashed and confirmed. Participants already paired to it stay paired, but new
  participants can't register it while it's `Reserved`, so don't leave a reworked unit
  unconfirmed. A unit with participants paired can't be voided.
- **Firmware reflashed but the same identity put back:** a programmer flash erases the identity, so
  Quarry can read it from the running unit before erasing and write it back afterwards. Then there's
  no rekey and no new label; just record it with the `firmware` endpoint. If the identity can't be
  read first (blank, corrupt, older firmware), rekey instead.
- **MS-V3 units (bootloader + app):** besides the firmware version, Quarry sends the bootloader's
  version (`bootloaderVersion`, read from the factory image) on reserve, confirm and rekey. It's
  recorded on the unit and in its history; omit it for units without a bootloader (MS-V2). An app
  update over USB doesn't touch the bootloader, so it's recorded with the `firmware` endpoint and no
  `bootloaderVersion`; after a programmer flash that restored the same identity, send the version
  that was written.
- **History:** every reserve, confirm, void, rekey and firmware write is recorded per unit (who,
  when, reason, firmware and bootloader versions before and after; never the code).
- **Confirm retried after a network error:** safe, confirm is idempotent.

## Serial format: dashless, with a display form

Serials are stored, sent and compared as 12 plain characters (`MSV2G01SATCF`). Dashes
(`MSV2-G01S-ATCF`) are added only for human-facing text such as labels and staff pages, and the
API returns both (`serialNumber`, `serialNumberLabel`). They're kept out of the stored form because
some operating systems rewrite punctuation in a USB serial string (macOS turns each non-alphanumeric
character into an underscore), which breaks matching a port to its serial. The server accepts a
serial in label form, with underscores or in lowercase and resolves it to the stored one; staff
entries and CSV rows in the current format are stored dashless too. Legacy free-form serials are
left as they are.

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
server, so registering proves you read the unit rather than merely knew its serial. It proves
access to the unit, not tamper resistance: someone with a programmer and the unit can read its
memory, including the code.
