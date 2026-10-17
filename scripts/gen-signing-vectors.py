#!/usr/bin/env python3
"""Regenerate testdata/release-signing-vectors.json, the shared fixture for the release-signing contract.

The contract (accounts/docs/release-publishing.md, Publishing/ReleaseSigning.cs) is implemented three times:
this repo, AxoSync's Updates/UpdateTrust.cs and axosync/workflows/osx/publish.sh. Each tests itself against
this file, so the statement text or the signature encoding can't change in one place unnoticed.

ECDSA signatures aren't deterministic, so this file is committed and only regenerated when the contract
changes (a new statement tag, say). A fresh throwaway key is made every run; it signs nothing real and must
never be added to a trusted-key list. After regenerating, run sync-vectors.sh in the workspace root to copy
the file to the other repos.

Usage: scripts/gen-signing-vectors.py
Needs: python3, openssl.
"""
import base64
import hashlib
import json
import os
import subprocess
import tempfile

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "testdata", "release-signing-vectors.json")
TAG = "intelimensa-axosync-release-v1"


def statement(version, platform, sha256, size):
    return f"{TAG}\n{version}\n{platform}\n{sha256}\n{size}\n"


def openssl(*args, data=None):
    return subprocess.run(["openssl", *args], input=data, check=True, capture_output=True).stdout


class Key:
    def __init__(self, directory, name):
        self.path = os.path.join(directory, name + ".pem")
        openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", self.path)
        self.public_b64 = base64.b64encode(openssl("pkey", "-in", self.path, "-pubout", "-outform", "DER")).decode()

    def sign_der(self, text):
        return openssl("dgst", "-sha256", "-sign", self.path, data=text.encode())


def der_to_raw(der):
    """DER SEQUENCE { INTEGER r, INTEGER s } to the fixed-width r||s form (IEEE P1363) that PKCS#11 tokens emit."""
    assert der[0] == 0x30
    i = 2 if der[1] < 0x80 else 2 + (der[1] & 0x7F)
    parts = []
    for _ in range(2):
        assert der[i] == 0x02
        n = der[i + 1]
        parts.append(int.from_bytes(der[i + 2:i + 2 + n], "big").to_bytes(32, "big"))
        i += 2 + n
    return b"".join(parts)


def b64(data):
    return base64.b64encode(data).decode()


def sha(label):
    return hashlib.sha256(label.encode()).hexdigest()


def main():
    with tempfile.TemporaryDirectory() as tmp:
        key, other = Key(tmp, "trusted"), Key(tmp, "other")
        cases = []

        def add(name, note, fields, signed_text, signature, expect_valid, signer="trusted"):
            cases.append({
                "name": name,
                "note": note,
                "version": fields[0],
                "platform": fields[1],
                "sha256": fields[2],
                "sizeBytes": fields[3],
                # the exact bytes the signer signed; equals Statement(fields) unless statementMatchesFields is false
                "signedStatementHex": signed_text.encode().hex(),
                "statementMatchesFields": signed_text == statement(*fields),
                "signature": signature,
                "signedBy": signer,
                "expectValid": expect_valid,
            })

        def good(name, note, fields):
            text = statement(*fields)
            add(name, note, fields, text, b64(key.sign_der(text)), True)
            return cases[-1]

        base = ("1.4.2", "MacOSArm64", sha("installer-1"), 123456789)
        valid = [
            good("valid-macos-arm64", "the shape publish.sh produces", base),
            good("valid-windows-x64", "", ("1.4.2", "WindowsX64", sha("installer-2"), 98765432)),
            good("valid-macos-x64", "", ("2.0.0", "MacOSX64", sha("installer-3"), 1)),
            good("valid-linux-x64", "", ("10.20.30", "LinuxX64", sha("installer-4"), 4096)),
            good("valid-prerelease-version", "includePrerelease clients can be offered these", ("1.5.0-rc.1", "WindowsX64", sha("installer-5"), 5555555)),
            good("valid-size-over-int32", "sizeBytes is a 64-bit value", ("1.4.2", "WindowsX64", sha("installer-6"), 3000000000)),
        ]
        sig = valid[0]["signature"]

        # One field changed at a time, with the signature of the untouched statement.
        v, p, h, s = base
        add("tampered-version", "signature for 1.4.2 presented for 1.4.3", ("1.4.3", p, h, s), statement(*base), sig, False)
        add("tampered-platform", "signature for MacOSArm64 presented for MacOSX64", (v, "MacOSX64", h, s), statement(*base), sig, False)
        add("tampered-sha256", "one hex digit differs", (v, p, h[:-1] + ("0" if h[-1] != "0" else "1"), s), statement(*base), sig, False)
        add("tampered-size", "off by one byte", (v, p, h, s + 1), statement(*base), sig, False)

        # Signed over a statement that differs from the one a verifier builds from the fields.
        add("signed-without-trailing-newline", "the last line must end in a newline too",
            base, statement(*base)[:-1], b64(key.sign_der(statement(*base)[:-1])), False)
        add("signed-with-v2-tag", "a format change must bump the tag and all three places",
            base, statement(*base).replace(TAG, "intelimensa-axosync-release-v2"),
            b64(key.sign_der(statement(*base).replace(TAG, "intelimensa-axosync-release-v2"))), False)
        add("signed-with-crlf", "line endings are \\n only",
            base, statement(*base).replace("\n", "\r\n"), b64(key.sign_der(statement(*base).replace("\n", "\r\n"))), False)

        # Right statement, wrong signer or wrong encoding.
        add("signed-by-untrusted-key", "valid signature, but not by the key in this file's publicKey",
            base, statement(*base), b64(other.sign_der(statement(*base))), False, signer="other")
        der = key.sign_der(statement(*base))
        add("signature-raw-r-s", "ECDSA over the right text but IEEE P1363 r||s, not DER: the token path must convert it",
            base, statement(*base), b64(der_to_raw(der)), False)
        add("signature-not-base64", "", base, statement(*base), "not base64!!", False)
        add("signature-not-der", "valid base64, garbage inside", base, statement(*base), "AAAA", False)
        add("signature-empty", "", base, statement(*base), "", False)

        doc = {
            "contract": TAG,
            "description": "Shared fixture for the AxoSync release-signing contract. Owner: accounts repo "
                           "(testdata/release-signing-vectors.json); copies elsewhere must be byte-identical (sync-vectors.sh). "
                           "A verifier given (version, platform, sha256, sizeBytes, signature) and publicKey must return expectValid. "
                           "Where statementMatchesFields is true, Statement(fields) must also equal signedStatementHex byte for byte. "
                           "The key is a throwaway test key: never add it to a trusted-key list.",
            "algorithm": "ECDSA P-256, SHA-256, DER signature, base64; publicKey is base64 SubjectPublicKeyInfo",
            "publicKey": key.public_b64,
            "otherPublicKey": other.public_b64,
            "cases": cases,
        }
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", newline="\n") as f:
            json.dump(doc, f, indent=2)
            f.write("\n")
        print(f"wrote {os.path.normpath(OUT)}: {len(cases)} cases")


if __name__ == "__main__":
    main()
