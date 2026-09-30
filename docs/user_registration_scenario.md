# User scenario: registering a device to your account

What a study participant does to link a loaned BCI unit to their Intelimensa account. Prerequisite:
the manufacturer flow in `manufacturer_registration_scenario.md` has put the unit on the device
list.

> **Status legend:** **[built]** exists today; **[planned]** is the registration-code design, not
> yet implemented.

## Steps

1. **Create an account** at the web portal (`/Account/Register`) and sign in. **[built]** A new
   account starts with no configuration assigned. Access lapses on the account's expiry date or if
   staff revoke it.
2. **Receive the unit.** The label or insert card carries the **serial number** and a
   **registration code**, kept as a fallback; normally the participant never needs them.
   **[code is planned]**
3. **Start AxoSync with the unit connected and sign in.** **[built]** AxoSync logs in through
   `POST /api/auth/login`.
4. **Confirm the registration when AxoSync asks.** **[planned]** AxoSync reads the unit's serial
   and registration code over the device link (`dcGetIdentity`, see the firmware repo's
   `DEVICE_IDENTITY.md`) and shows "Register unit MS2-000123 to your account?". The participant
   confirms; nothing is typed. AxoSync then calls `POST /api/devices/register` with both values.
   - The confirmation guards against registering the wrong unit by accident. The code, not the
     dialog, is the security check.
   - The server checks the account is active and not expired, then that the serial exists and
     (once built) that the code matches. Unknown serial and wrong code return the same generic
     error so serials can't be probed.
   - On success the unit is linked to the account. Repeating the registration for the same
     account and unit is harmless and needs no code.
   - Repeated failures are rate limited per account. **[planned]**
   - **Fallback:** if the unit can't be read (for example over BLE without a bonded link and
     button press), AxoSync offers manual entry of the serial and the code from the label.
   - **Units shipped before the firmware update** (4 loaned units) have no code and are being
     replaced once the firmware is stable. Their existing account pairings keep working, but no
     new account can register them: a unit with no stored code hash is not registerable.
5. **Wait for staff to assign a configuration.** **[built]** Staff match the account, unit and
   calibration, then assign a config at `/Staff/DeviceAssignments`. Participants can't choose one.
6. **Check status** at `/Account/Status`. **[built]** It lists registered units and each unit's
   assigned config, or "config not yet assigned".

## Things that can go wrong

- **"Unknown device serial" / registration failed:** retype the serial and code. If it still
  fails, contact staff; the unit may not be on the list, or the label code may need regenerating.
- **Account expired or revoked:** registration is refused entirely until staff act.
- **Unit passed to another participant:** the new holder registers it under their own account.
  Registering never removes anyone else's link; staff revoke the previous pairing.
