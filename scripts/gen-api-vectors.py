#!/usr/bin/env python3
"""Regenerate testdata/api-vectors.json, the shared fixture for the accounts server's JSON API.

The server (Intelimensa.Accounts/Api: AuthContracts.cs, ManufacturingContracts.cs, ReleaseContracts.cs) and its clients
describe the same JSON in their own code, and nothing makes them agree:
  - MindStoneQuarry, Intelimensa.Quarry.Core/Accounts: AccountsModels.cs and AccountsClient.cs (/api/auth, /api/manufacturing)
  - AxoSync, AxoSync/Accounts/AccountsClient.cs (/api/auth), AxoSync/Updates/UpdateChecker.cs (/api/releases/latest) and
    AxoSync/ConfigDelivery/ConfigDeliveryClient.cs (/api/devices: registration, config delivery), which write and read the JSON
    by hand because release builds are obfuscated
  - ConfigGen's publish command (/api/publishing/configs; not tested against this file yet)
  - axosync/workflows/osx/publish.sh (the publishing API; not covered here)

Each example is JSON as it goes over the wire: a `request` is what a client sends and the server must accept, a `response`
is what the server sends and a client must read. The server's responses are written the way ASP.NET writes them (camelCase,
nulls kept, enums as numbers, dates as ISO 8601 with +00:00), so the server's own test can round-trip them exactly.
`type` names the contract record; each consumer maps it to its own type.

The update-check example uses the signature of a case in release-signing-vectors.json (its test key), so a client that
verifies signatures can accept it. The install and config-delivery examples use the keys, wrap and lease of
config-delivery-vectors.json, so they open with its keys. The output is deterministic. Usage: scripts/gen-api-vectors.py [OUTPUT.json]
Then run sync-vectors.sh in the workspace root. Needs: python3.
"""
import base64
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "testdata", "api-vectors.json")
SIGNING = os.path.join(HERE, "..", "testdata", "release-signing-vectors.json")
DELIVERY = os.path.join(HERE, "..", "testdata", "config-delivery-vectors.json")


def main():
    with open(SIGNING, encoding="ascii") as f:
        signing = json.load(f)
    signed = next(c for c in signing["cases"] if c["name"] == "valid-macos-arm64")
    # config delivery's keys, wrap and lease, so the two fixtures describe the same delivery
    with open(DELIVERY, encoding="ascii") as f:
        delivery = json.load(f)
    wrap = delivery["cekWrap"]
    lease = next(c for c in delivery["offlineLease"]["cases"] if c["name"] == "valid")
    blob = delivery["configBlob"]

    examples = []

    def example(name, type_, direction, body, note="", **extra):
        examples.append({"name": name, "type": type_, "direction": direction, "note": note, "json": body, **extra})

    # ---- /api/auth ----------------------------------------------------------------------------------------------
    example("login-request", "LoginRequest", "request",
            {"email": "station@example.com", "password": "correct-horse-battery"})
    example("refresh-request", "RefreshRequest", "request", {"refreshToken": "r3fr3sh-t0k3n-0001"},
            "also the body of /api/auth/logout")
    example("token-response", "TokenResponse", "response", {
        "accessToken": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln",
        "accessTokenExpiresAt": "2026-10-18T10:15:00+00:00",
        "refreshToken": "r3fr3sh-t0k3n-0002",
        "refreshTokenExpiresAt": "2026-11-17T10:00:00+00:00",
        "isStudyParticipant": False})
    example("token-response-study-participant", "TokenResponse", "response", {
        "accessToken": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIyIn0.c2ln",
        "accessTokenExpiresAt": "2026-10-18T10:15:00+00:00",
        "refreshToken": "r3fr3sh-t0k3n-0003",
        "refreshTokenExpiresAt": "2026-11-17T10:00:00+00:00",
        "isStudyParticipant": True})

    example("login-request-with-install", "LoginRequest", "request", {
        "email": "participant@example.com", "password": "correct-horse-battery",
        "install": {"signingKey": delivery["installProof"]["key"]["publicKey"],
                    "agreementKey": wrap["agreementKey"]["publicKey"],
                    "platform": "MacOS", "name": "MacBook Air", "appVersion": "0.2.0", "slot": 1}},
        "AxoSync registers its install's two P-256 keys (base64 SPKI); the platform is a name. The request carries an install "
        "proof by signingKey (config-delivery-vectors.json, installProof). Tokens issued are bound to the install.")

    # ---- /api/devices ----------------------------------------------------------------------------------------------
    example("register-device-request", "RegisterDeviceRequest", "request",
            {"serialNumber": "MSV3G01SATKX", "registrationCode": "7KQ49XT2AM"},
            "the code read from the unit; a pairing the account already has needs none")
    example("register-device-response", "RegisterDeviceResponse", "response", {
        "accountDeviceId": "3f2c8a10-5d4e-4b6a-9e21-7c0d1b2a3f45", "bciDeviceId": "8a6e4d2c-1b3f-4e5a-9c7d-0f1e2d3c4b5a",
        "serialNumber": "MSV3G01SATKX", "serialNumberLabel": "MSV3-G01S-ATKX", "deviceType": "msv3",
        "assignedConfigId": None, "registeredAt": "2026-10-25T12:00:00+00:00"},
        "assignedConfigId stays null until staff assign a config")
    example("config-delivery-response", "ConfigDeliveryResponse", "response", {
        "configKey": wrap["configKey"], "displayName": "MindStone 5", "blobSha256": wrap["blobSha256"],
        "blobSize": len(base64.b64decode(blob["blob"])), "blobUrl": "/api/configs/ms5/blob",
        "wrappedCek": wrap["wrapped"],
        "lease": {"accountId": lease["terms"]["accountId"], "clientInstallId": lease["terms"]["clientInstallId"],
                  "serialNumber": lease["terms"]["serialNumber"], "configKey": lease["terms"]["configKey"],
                  "blobSha256": lease["terms"]["blobSha256"], "issuedAt": lease["terms"]["issuedAt"],
                  "notAfter": lease["terms"]["notAfter"]},
        "leaseSignature": lease["signature"]},
        "GET /api/devices/{serial}/config: the wrap and lease of config-delivery-vectors.json (they open with its keys). The "
        "lease's times are strings exactly as signed, not dates to reformat.")

    # ---- /api/publishing/configs (ConfigGen) ----------------------------------------------------------------------
    example("config-draft-request", "ConfigDraftRequest", "request", {"displayName": "MindStone 5"},
            "PUT /api/publishing/configs/{key}: creates the config, or renames it")
    example("published-config-response", "PublishedConfigResponse", "response", {
        "key": "ms5", "displayName": "MindStone 5", "isDefault": True, "retired": False,
        "live": {"sha256": wrap["blobSha256"], "sizeBytes": len(base64.b64decode(blob["blob"])),
                 "uploadedAt": "2026-10-20T09:00:00+00:00"},
        "pending": {"sha256": "5d41402abc4b2a76b9719d911017c592ae8bd2e4d6a8a2b8f1b0d0c7e1f2a3b4", "sizeBytes": 23530,
                    "uploadedAt": "2026-10-25T12:00:00+00:00"}},
        "after ConfigGen uploaded a new file: it waits as pending until Staff (or --auto-publish) publish it; ConfigGen checks "
        "pending.sha256 against its own")
    example("published-config-response-new", "PublishedConfigResponse", "response", {
        "key": "ms5", "displayName": "MindStone 5", "isDefault": False, "retired": False, "live": None, "pending": None},
        "a config with no file yet")

    # ---- /api/manufacturing ---------------------------------------------------------------------------------------
    example("options-response", "ManufacturingOptionsResponse", "response", {
        "products": [{"deviceType": "msv1", "productCode": "MSV1"}, {"deviceType": "msv3", "productCode": "MSV3"}],
        "regions": [{"code": "G", "description": "Global (no regional variant)"}],
        "rekeyReasons": ["Reflash", "Rework"]},
        "Initial is the server's own choice for a held serial and is never offered; Relabel was retired")
    example("reserve-request-ms-v3", "ReserveUnitRequest", "request",
            {"deviceType": "msv3", "region": "G", "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87"})
    example("reserve-request-no-bootloader", "ReserveUnitRequest", "request",
            {"deviceType": "msv1", "region": "G", "firmwareVersion": "0.5+f34ea83"},
            "a unit without a bootloader leaves the field out")
    example("issued-unit-response", "IssuedUnitResponse", "response", {
        "serialNumber": "MSV3G01SATKX", "serialNumberLabel": "MSV3-G01S-ATKX",
        "registrationCode": "7KQ49XT2AM", "registrationCodeLabel": "7KQ49-XT2AM",
        "deviceType": "msv3", "reservedAt": "2026-10-18T09:30:00+00:00"},
        "returned by reserve and rekey; the only time the plaintext code is exposed")
    example("confirm-request", "ConfirmUnitRequest", "request", {
        "serialNumber": "MSV3G01SATKX", "registrationCode": "7KQ49XT2AM",
        "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87"})
    example("void-request", "UnitRequest", "request", {"serialNumber": "MSV3G01SATKX"})
    example("rekey-request", "RekeyUnitRequest", "request", {
        "serialNumber": "MSV3G01SATKX", "reason": "Reflash", "note": "identity lost in a programmer flash",
        "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87"},
        "the reason is a name, not a number")
    example("rekey-request-no-note", "RekeyUnitRequest", "request", {
        "serialNumber": "MSV3G01SATKX", "reason": "Rework", "note": None, "firmwareVersion": "0.30+9183eec"})
    example("firmware-write-request", "FirmwareWriteRequest", "request", {
        "serialNumber": "MSV3G01SATKX", "firmwareVersion": "0.31+aaaaaaa", "note": None})
    example("unit-response-reserved", "UnitResponse", "response", {
        "serialNumber": "MSV3G01SATKX", "serialNumberLabel": "MSV3-G01S-ATKX", "deviceType": "msv3",
        "status": 0, "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87",
        "manufacturedAt": None, "firmwareUpdatedAt": None},
        "status is a number: 0 Reserved, 1 Manufactured, 2 Voided")
    example("unit-response-manufactured", "UnitResponse", "response", {
        "serialNumber": "MSV3G01SATKX", "serialNumberLabel": "MSV3-G01S-ATKX", "deviceType": "msv3",
        "status": 1, "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87",
        "manufacturedAt": "2026-10-18T10:00:00+00:00", "firmwareUpdatedAt": "2026-10-18T10:00:00+00:00"})
    example("unit-response-ms-v2", "UnitResponse", "response", {
        "serialNumber": "MSV2G0000018", "serialNumberLabel": "MSV2-G000-0018", "deviceType": "msv2",
        "status": 1, "firmwareVersion": "0.5+f34ea83", "bootloaderVersion": None,
        "manufacturedAt": "2026-10-18T10:00:00+00:00", "firmwareUpdatedAt": "2026-10-18T10:00:00+00:00"},
        "no bootloader: the field is null")
    example("unit-response-voided", "UnitResponse", "response", {
        "serialNumber": "MSV3G01SATKX", "serialNumberLabel": "MSV3-G01S-ATKX", "deviceType": "msv3",
        "status": 2, "firmwareVersion": "0.30+9183eec", "bootloaderVersion": "0.53+797af87",
        "manufacturedAt": "2026-10-18T10:00:00+00:00", "firmwareUpdatedAt": "2026-10-18T10:00:00+00:00"})
    example("unit-response-held-serial", "UnitResponse", "response", {
        "serialNumber": "MSV3G0000018", "serialNumberLabel": "MSV3-G000-0018", "deviceType": "msv3",
        "status": 0, "firmwareVersion": None, "bootloaderVersion": None,
        "manufacturedAt": None, "firmwareUpdatedAt": None},
        "a held serial has no code and no firmware version yet: the version is null")

    # ---- /api/releases/latest (an update check) -------------------------------------------------------------------
    artifact = {"fileName": "AxoSyncSetup-1.4.2.dmg", "sizeBytes": signed["sizeBytes"], "sha256": signed["sha256"],
                "signature": signed["signature"], "downloadUrl": "api/releases/1.4.2/MacOSArm64/file"}
    context = {"platform": "MacOSArm64", "trustedPublicKey": signing["publicKey"]}
    example("update-up-to-date", "UpdateCheckResponse", "response", {
        "updateAvailable": False, "mandatory": False, "version": None, "notes": None, "minimumVersion": None,
        "publishedAt": None, "artifact": None},
        "a caller that is current, or has no build for its platform", context={**context, "current": "1.4.2"},
        expect={"status": "UpToDate"})
    example("update-available", "UpdateCheckResponse", "response", {
        "updateAvailable": True, "mandatory": False, "version": "1.4.2", "notes": "Fixes the cursor drift.",
        "minimumVersion": None, "publishedAt": "2026-10-17T12:00:00+00:00", "artifact": artifact},
        "signed with the key of release-signing-vectors.json", context={**context, "current": "1.4.1"},
        expect={"status": "Available", "version": "1.4.2", "mandatory": False, "notes": "Fixes the cursor drift.",
                "fileName": artifact["fileName"], "sizeBytes": artifact["sizeBytes"], "sha256": artifact["sha256"]})
    example("update-available-mandatory", "UpdateCheckResponse", "response", {
        "updateAvailable": True, "mandatory": True, "version": "1.4.2", "notes": None,
        "minimumVersion": "1.4.0", "publishedAt": "2026-10-17T12:00:00+00:00", "artifact": artifact},
        "the caller is below minimumVersion", context={**context, "current": "1.3.0"},
        expect={"status": "Available", "version": "1.4.2", "mandatory": True, "notes": None,
                "fileName": artifact["fileName"], "sizeBytes": artifact["sizeBytes"], "sha256": artifact["sha256"]})
    example("update-offer-not-newer", "UpdateCheckResponse", "response", {
        "updateAvailable": True, "mandatory": False, "version": "1.4.2", "notes": None, "minimumVersion": None,
        "publishedAt": "2026-10-17T12:00:00+00:00", "artifact": artifact},
        "a client refuses an offer that isn't newer than what it runs (a replayed old build)",
        context={**context, "current": "1.4.2"}, expect={"status": "Untrusted"})
    example("update-offer-unsigned", "UpdateCheckResponse", "response", {
        "updateAvailable": True, "mandatory": False, "version": "1.4.2", "notes": None, "minimumVersion": None,
        "publishedAt": "2026-10-17T12:00:00+00:00", "artifact": {**artifact, "signature": None}},
        "a build without a signature is never installed", context={**context, "current": "1.4.1"},
        expect={"status": "Untrusted"})
    example("update-offer-other-server-download", "UpdateCheckResponse", "response", {
        "updateAvailable": True, "mandatory": False, "version": "1.4.2", "notes": None, "minimumVersion": None,
        "publishedAt": "2026-10-17T12:00:00+00:00",
        "artifact": {**artifact, "downloadUrl": "https://evil.example.net/AxoSyncSetup-1.4.2.dmg"}},
        "the bearer token must never go to another server", context={**context, "current": "1.4.1"},
        expect={"status": "Untrusted"})

    # JSON a client must still read: other date spellings, extra fields, a different order
    tolerated = [
        {"name": "token-response-z-dates-extra-field-any-order", "type": "TokenResponse",
         "json": {"isStudyParticipant": False, "refreshTokenExpiresAt": "2026-11-17T10:00:00.1234567Z",
                  "refreshToken": "r3fr3sh-t0k3n-0004", "accessTokenExpiresAt": "2026-10-18T10:15:00Z",
                  "accessToken": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "somethingNew": {"added": "later"}},
         "expect": {"accessToken": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "refreshToken": "r3fr3sh-t0k3n-0004",
                    "accessTokenExpiresAt": "2026-10-18T10:15:00+00:00"}},
    ]

    # what the server answers with when it refuses (the body shape differs by endpoint family)
    errors = [
        {"family": "manufacturing, devices, telemetry", "status": 404, "bodyJson": "Unknown serial number.",
         "note": "a bare JSON string; ReadAsync in Quarry's AccountsClient takes it as the message"},
        {"family": "manufacturing", "status": 503, "bodyJson": {"status": 503, "title": "Service Unavailable", "detail": "Try again shortly."},
         "note": "ProblemDetails, for the few cases that use Results.Problem"},
        {"family": "releases and publishing", "status": 400, "bodyJson": {"error": "platform is required."},
         "note": "an object with `error`; publish.sh reads it"},
        {"family": "install proofs and config delivery", "status": 401, "bodyJson": {"error": "install_nonce_stale"},
         "note": "an object with a machine-readable `error` code (docs/config-management.md, 'As built'); AxoSync retries once on "
                 "install_nonce_stale / install_proof_required with the fresh nonce in the response's X-Install-Nonce header"},
    ]

    doc = {
        "contract": "intelimensa-accounts-api-v1",
        "description": "Shared fixture for the accounts server's JSON API (auth, manufacturing, update check, device registration, "
                       "config delivery and config publishing). Owner: accounts repo "
                       "(testdata/api-vectors.json); copies elsewhere must be byte-identical (sync-vectors.sh). `examples`: JSON on the "
                       "wire. The server must read every `request` into its record and write every `response` back out identically "
                       "(nulls kept); a client must write every `request` it sends as given (optional fields may be left out) and read "
                       "every `response`. Update-check examples carry a `context` (the client's platform, version and trusted key) "
                       "and the `expect`ed result of AxoSync's check. `tolerated`: JSON a client must still read. `errors`: bodies a "
                       "client may meet. `rekeyReasons`: the reasons a station may send; anything else is refused.",
        "rekeyReasons": ["Reflash", "Rework"],
        "examples": examples,
        "tolerated": tolerated,
        "errors": errors,
    }
    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    with open(OUT, "w", newline="\n") as f:
        json.dump(doc, f, indent=2, ensure_ascii=True)
        f.write("\n")
    print(f"wrote {os.path.normpath(OUT)}: {len(examples)} examples, {len(tolerated)} tolerated, {len(errors)} errors")


if __name__ == "__main__":
    main()
