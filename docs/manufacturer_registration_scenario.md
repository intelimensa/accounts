# Manufacturer scenario: adding devices to the device list

How a manufactured BCI unit gets onto the device list (`BciDevices`) so a participant can later
register it. Staff (users with the `Staff` role) do this; participants never touch the list.

> **Status legend:** steps marked **[built]** exist today. Steps marked **[planned]** are the
> registration-code design, which is agreed but not yet implemented.

## Steps

1. **Assemble a CSV** with one row per unit produced. **[built]**

   ```csv
   serial_number,device_type,produced_at,firmware_version
   MS2-000123,ms2,2026-09-28,1.4.0
   MS2-000124,ms2,2026-09-28,1.4.0
   ```

   - `device_type` uses the config-key vocabulary (`ms2`, `ms5`, `biosemi`).
   - `produced_at` is `yyyy-MM-dd`. Columns may be in any order; the header row is required.
   - Serial numbers must be unique, both within the file and against units already on the list.

2. **Sign in as Staff** and open `/Staff/Devices`. **[built]**
3. **Upload the CSV** under "Import from CSV". **[built]** The import is all-or-nothing: if any row
   is invalid or a serial already exists, nothing is imported and each problem is listed. Fix the
   file and upload again.
4. **Download the registration codes** shown after a successful import. **[planned]**
   - The server generates one random code per unit (10 characters, unambiguous alphabet, e.g.
     `7KQ4-M9XT2`) and stores only a hash of it.
   - The codes are displayed **once**, as a downloadable `serial_number,registration_code` CSV.
     They can't be recovered afterwards.
5. **Print each code on the unit's label or insert card** and ship the unit. **[planned]** The
   code is proof the registrant physically has the unit.

## Later changes

- **Firmware:** AxoSync updates `CurrentFirmwareVersion` / `LastFirmwareUpdatedAt` itself when it
  performs a firmware upgrade, so manufacturing only supplies the initial version. **[planned]**
- **Typo in the list:** there's no edit or delete yet. Add an edit/delete page for units with no
  registrations. **[not built]**
- **Lost or leaked label:** staff regenerate the code, which replaces the stored hash. The old
  code stops working; existing registrations are unaffected. **[planned]**

## Why the code exists

The serial alone is guessable (serials are usually sequential). Without a second secret, any
active account could register any serial. Config assignment is still staff-gated, so the risk is
limited to a stray registration, but the code closes it cheaply while keeping self-service.
