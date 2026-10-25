#!/usr/bin/env python3
"""Regenerate testdata/config-delivery-vectors.json, the shared fixture for config delivery's wire formats.

docs/config-management.md describes them; the server implements them in Intelimensa.Accounts (Security/InstallProof.cs,
ConfigDelivery/CekWrap.cs, ConfigBlob.cs, OfflineLease.cs), AxoSync will implement the client side, and ConfigGen writes
the blobs. Each tests itself against this file:
  - install proofs: the statement an install signs for each request, with valid and tampered signatures
  - the CEK wrap: ECDH P-256 + HKDF-SHA256 (salted with the build's release secret) + AES-256-GCM, with the
    intermediate values, and inputs that must fail to open it
  - the v2 config blob: "IMCF" || 2 || nonce || tag || AES-256-GCM ciphertext, with broken blobs
  - the offline lease: the signed statement, valid and tampered

Every key, nonce and signature comes from fixed seeds (ECDSA with RFC 6979 deterministic nonces), so the output is the
same on every run. The keys are throwaway test keys: never configure or embed one.

The crypto is written out here in plain Python (P-256, AES, GCM, HKDF) rather than taken from a library: it needs nothing
but python3, and it is an implementation independent of .NET's, which is what a fixture should be. It checks itself
against published test vectors before writing anything.

Usage: scripts/gen-config-delivery-vectors.py [OUTPUT.json]
Then run sync-vectors.sh in the workspace root once the file has copies. Needs: python3.
"""
import base64
import hashlib
import hmac
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "testdata", "config-delivery-vectors.json")

# ---- P-256 --------------------------------------------------------------------------------------------------------------

P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
A = P - 3
B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B
N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
G = (0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296,
     0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5)
SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d03010703420004")


def point_add(p1, p2):
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    (x1, y1), (x2, y2) = p1, p2
    if x1 == x2 and (y1 + y2) % P == 0:
        return None
    if p1 == p2:
        lam = (3 * x1 * x1 + A) * pow(2 * y1, -1, P) % P
    else:
        lam = (y2 - y1) * pow(x2 - x1, -1, P) % P
    x3 = (lam * lam - x1 - x2) % P
    return x3, (lam * (x1 - x3) - y1) % P


def point_mul(k, point):
    result = None
    while k:
        if k & 1:
            result = point_add(result, point)
        point = point_add(point, point)
        k >>= 1
    return result


def on_curve(point):
    x, y = point
    return (y * y - (x * x * x + A * x + B)) % P == 0


def i2b(value, size=32):
    return value.to_bytes(size, "big")


class Key:
    """A P-256 key from a fixed seed."""

    def __init__(self, seed):
        self.d = int.from_bytes(hashlib.sha256(seed.encode()).digest(), "big") % (N - 1) + 1
        self.q = point_mul(self.d, G)
        self.spki = SPKI_PREFIX + i2b(self.q[0]) + i2b(self.q[1])

    def json(self, private=True):
        out = {"publicKey": b64(self.spki)}
        if private:
            out = {"privateKey": i2b(self.d).hex(), **out}
        return out


def rfc6979_k(d, h):
    """RFC 6979 section 3.2, SHA-256, for a 256-bit group order (no truncation needed)."""
    x, h1 = i2b(d), i2b(int.from_bytes(h, "big") % N)
    v, k = b"\x01" * 32, b"\x00" * 32
    k = hmac.new(k, v + b"\x00" + x + h1, hashlib.sha256).digest()
    v = hmac.new(k, v, hashlib.sha256).digest()
    k = hmac.new(k, v + b"\x01" + x + h1, hashlib.sha256).digest()
    v = hmac.new(k, v, hashlib.sha256).digest()
    while True:
        v = hmac.new(k, v, hashlib.sha256).digest()
        candidate = int.from_bytes(v, "big")
        if 1 <= candidate < N:
            return candidate
        k = hmac.new(k, v + b"\x00", hashlib.sha256).digest()
        v = hmac.new(k, v, hashlib.sha256).digest()


def ecdsa_sign(key, message):
    h = hashlib.sha256(message).digest()
    z = int.from_bytes(h, "big")
    k = rfc6979_k(key.d, h)
    r = point_mul(k, G)[0] % N
    s = pow(k, -1, N) * (z + r * key.d) % N
    assert r and s
    return r, s


def ecdsa_verify(q, message, r, s):
    if not (1 <= r < N and 1 <= s < N):
        return False
    z = int.from_bytes(hashlib.sha256(message).digest(), "big")
    w = pow(s, -1, N)
    point = point_add(point_mul(z * w % N, G), point_mul(r * w % N, q))
    return point is not None and point[0] % N == r


def der_int(value):
    raw = value.to_bytes((value.bit_length() + 7) // 8 or 1, "big")
    if raw[0] & 0x80:
        raw = b"\x00" + raw
    return b"\x02" + bytes([len(raw)]) + raw


def der_signature(r, s):
    body = der_int(r) + der_int(s)
    return b"\x30" + bytes([len(body)]) + body


def sign_der(key, text):
    return b64(der_signature(*ecdsa_sign(key, text.encode())))


def sign_raw(key, text):
    r, s = ecdsa_sign(key, text.encode())
    return b64(i2b(r) + i2b(s))


def ecdh(private_key, public_key):
    return i2b(point_mul(private_key.d, public_key.q)[0])


# ---- AES-256 and GCM ---------------------------------------------------------------------------------------------------

def gf_mul8(a, b):
    out = 0
    while b:
        if b & 1:
            out ^= a
        a = ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else a << 1
        b >>= 1
    return out


def make_sbox():
    sbox = []
    for i in range(256):
        inv = next((j for j in range(1, 256) if gf_mul8(i, j) == 1), 0) if i else 0
        s = inv
        for shift in range(1, 5):
            s ^= ((inv << shift) | (inv >> (8 - shift))) & 0xFF
        sbox.append(s ^ 0x63)
    return sbox


SBOX = make_sbox()


def aes256_expand(key):
    assert len(key) == 32
    words = [list(key[i:i + 4]) for i in range(0, 32, 4)]
    rcon = 1
    for i in range(8, 60):
        temp = list(words[i - 1])
        if i % 8 == 0:
            temp = [SBOX[b] for b in temp[1:] + temp[:1]]
            temp[0] ^= rcon
            rcon = gf_mul8(rcon, 2)
        elif i % 8 == 4:
            temp = [SBOX[b] for b in temp]
        words.append([a ^ b for a, b in zip(words[i - 8], temp)])
    return [sum(words[r * 4:r * 4 + 4], []) for r in range(15)]


def aes256_encrypt_block(round_keys, block):
    state = [b ^ k for b, k in zip(block, round_keys[0])]
    for rnd in range(1, 15):
        state = [SBOX[b] for b in state]
        state = [state[(r + 4 * ((c + r) % 4))] for c in range(4) for r in range(4)]
        if rnd != 14:
            mixed = []
            for c in range(4):
                col = state[4 * c:4 * c + 4]
                mixed += [gf_mul8(col[0], 2) ^ gf_mul8(col[1], 3) ^ col[2] ^ col[3],
                          col[0] ^ gf_mul8(col[1], 2) ^ gf_mul8(col[2], 3) ^ col[3],
                          col[0] ^ col[1] ^ gf_mul8(col[2], 2) ^ gf_mul8(col[3], 3),
                          gf_mul8(col[0], 3) ^ col[1] ^ col[2] ^ gf_mul8(col[3], 2)]
            state = mixed
        state = [b ^ k for b, k in zip(state, round_keys[rnd])]
    return bytes(state)


def gf128_mul(x, y):
    r = 0xE1 << 120
    z, v = 0, y
    for i in range(127, -1, -1):
        if (x >> i) & 1:
            z ^= v
        v = (v >> 1) ^ r if v & 1 else v >> 1
    return z


def ghash(h, data):
    y = 0
    for i in range(0, len(data), 16):
        y = gf128_mul(y ^ int.from_bytes(data[i:i + 16], "big"), h)
    return y


def pad16(data):
    return data + b"\x00" * (-len(data) % 16)


def aes_gcm_encrypt(key, nonce, plaintext, aad=b""):
    assert len(nonce) == 12
    keys = aes256_expand(key)
    h = int.from_bytes(aes256_encrypt_block(keys, b"\x00" * 16), "big")
    j0 = nonce + b"\x00\x00\x00\x01"
    ciphertext = bytearray()
    for i in range(0, len(plaintext), 16):
        counter = nonce + ((2 + i // 16) & 0xFFFFFFFF).to_bytes(4, "big")
        stream = aes256_encrypt_block(keys, counter)
        ciphertext += bytes(a ^ b for a, b in zip(plaintext[i:i + 16], stream))
    ciphertext = bytes(ciphertext)
    lengths = (len(aad) * 8).to_bytes(8, "big") + (len(ciphertext) * 8).to_bytes(8, "big")
    s = ghash(h, pad16(aad) + pad16(ciphertext) + lengths)
    tag = i2b(s ^ int.from_bytes(aes256_encrypt_block(keys, j0), "big"), 16)
    return ciphertext, tag


def aes_gcm_decrypt(key, nonce, ciphertext, tag, aad=b""):
    """None when the tag doesn't match (CTR mode is its own inverse, so re-encrypting gives the plaintext)."""
    plaintext, _ = aes_gcm_encrypt(key, nonce, ciphertext, aad)
    _, expected = aes_gcm_encrypt(key, nonce, plaintext, aad)
    return plaintext if hmac.compare_digest(expected, tag) else None


# ---- HKDF (RFC 5869) ---------------------------------------------------------------------------------------------------

def hkdf_sha256(ikm, salt, info, length=32):
    prk = hmac.new(salt or b"\x00" * 32, ikm, hashlib.sha256).digest()
    out, block, counter = b"", b"", 1
    while len(out) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        out += block
        counter += 1
    return out[:length]


# ---- self-checks against published vectors ----------------------------------------------------------------------------

def self_check():
    # FIPS-197 appendix C.3
    assert aes256_encrypt_block(aes256_expand(bytes(range(32))), bytes.fromhex("00112233445566778899aabbccddeeff")) \
        == bytes.fromhex("8ea2b7ca516745bfeafc49904b496089"), "AES-256"
    # GCM specification (McGrew and Viega), test cases 13 and 14
    assert aes_gcm_encrypt(bytes(32), bytes(12), b"")[1] == bytes.fromhex("530f8afbc74536b9a963b4f1c4cb738b"), "GCM 13"
    c, t = aes_gcm_encrypt(bytes(32), bytes(12), bytes(16))
    assert (c, t) == (bytes.fromhex("cea7403d4d606b6e074ec5d3baf39d18"),
                      bytes.fromhex("d0d1c8a799996bf0265b98b5d48ab919")), "GCM 14"
    # RFC 5869 test case 1 (its output length, 42)
    assert hkdf_sha256(bytes([0x0B] * 22), bytes(range(13)), bytes(range(0xF0, 0xFA)), 42).hex() == (
        "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"), "HKDF"
    # RFC 6979 appendix A.2.5, P-256 with SHA-256, message "sample"
    key = Key("unused")
    key.d = 0xC9AFA9D845BA75166B5C215767B1D6934E50C3DB36E89B127B8A622B120F6721
    key.q = point_mul(key.d, G)
    assert key.q[0] == 0x60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6, "P-256 public key"
    r, s = ecdsa_sign(key, b"sample")
    assert (r, s) == (0xEFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716,
                      0xF7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8), "RFC 6979"
    assert ecdsa_verify(key.q, b"sample", r, s)
    a, b = Key("ecdh-a"), Key("ecdh-b")
    assert ecdh(a, b) == ecdh(b, a) and on_curve(a.q)


# ---- the fixture ---------------------------------------------------------------------------------------------------------

def b64(data):
    return base64.b64encode(data).decode()


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def seeded_bytes(seed, size):
    return hashlib.sha256(seed.encode()).digest()[:size]


PROOF_TAG = "intelimensa-install-proof-v1"
WRAP_TAG = "intelimensa-cek-wrap-v1"
LEASE_TAG = "intelimensa-config-lease-v1"
BLOB_MAGIC = b"IMCF"


def proof_statement(method, path, body, nonce):
    return f"{PROOF_TAG}\n{method}\n{path}\n{sha256_hex(body.encode())}\n{nonce}\n"


def install_proofs():
    key, other = Key("install-signing"), Key("install-signing-other")
    nonce = "AAAAAGkAbcDuPZf0bKkZ2V3nQ8xTwYg"
    install_body = json.dumps({
        "email": "participant@example.com", "password": "correct-horse-battery",
        "install": {"signingKey": b64(key.spki), "agreementKey": b64(Key("install-agreement").spki),
                    "platform": "MacOS", "name": "MacBook Air", "appVersion": "0.2.0", "slot": 1}},
        separators=(",", ":"))
    requests = {
        "get-config": ("GET", "/api/devices/MSV3G01SATKX/config?version=0.2.0&platform=MacOSArm64", ""),
        "post-refresh": ("POST", "/api/auth/refresh", '{"refreshToken":"r3fr3sh-t0k3n-0002"}'),
        "post-login": ("POST", "/api/auth/login", install_body),
    }

    cases = []

    def case(name, request, signature, public_key, valid, note=""):
        method, path, body, case_nonce = request
        cases.append({"name": name, "note": note, "method": method, "pathAndQuery": path, "body": body,
                      "bodySha256": sha256_hex(body.encode()), "nonce": case_nonce,
                      "statement": proof_statement(method, path, body, case_nonce),
                      "publicKey": b64(public_key.spki), "signature": signature, "valid": valid})

    for name, (method, path, body) in requests.items():
        request = (method, path, body, nonce)
        case("valid-" + name, request, sign_der(key, proof_statement(*request)), key, True)

    method, path, body = requests["get-config"]
    signed = sign_der(key, proof_statement(method, path, body, nonce))
    case("tampered-method", ("POST", path, body, nonce), signed, key, False)
    case("tampered-path", (method, path.replace("MSV3G01SATKX", "MSV3G01SATKY"), body, nonce), signed, key, False)
    case("tampered-query", (method, path.replace("0.2.0", "0.1.0"), body, nonce), signed, key, False)
    case("tampered-nonce", (method, path, body, nonce[:-1] + "h"), signed, key, False)
    case("wrong-key", (method, path, body, nonce), signed, other, False, "signed by the install, checked against another")
    case("raw-signature", (method, path, body, nonce), sign_raw(key, proof_statement(method, path, body, nonce)), key, False,
         "r||s (IEEE P1363) instead of DER: refused, only DER is accepted")
    method, path, body = requests["post-refresh"]
    case("tampered-body", (method, path, body.replace("0002", "0003"), nonce),
         sign_der(key, proof_statement(method, path, body, nonce)), key, False)

    return {"tag": PROOF_TAG, "key": key.json(), "otherKey": other.json(private=False), "cases": cases}


def wrap_info(install_id, config_key, blob_sha256):
    return f"{WRAP_TAG}\n{install_id}\n{config_key}\n{blob_sha256}\n"


def cek_wrap(blob_sha256, cek):
    agreement, ephemeral = Key("install-agreement"), Key("server-ephemeral")
    secret = seeded_bytes("release-secret-0.2.0-MacOSArm64", 32)
    install_id, config_key = "6f1c2a9e-4b7d-4e2a-9c3b-1d5e8f7a6b40", "ms5"
    nonce = seeded_bytes("wrap-nonce", 12)

    def wrap(secret_, install_id_, config_key_, sha):
        shared = ecdh(ephemeral, agreement)
        kek = hkdf_sha256(shared, secret_, wrap_info(install_id_, config_key_, sha).encode())
        return shared, kek, aes_gcm_encrypt(kek, nonce, cek)

    shared, kek, (ciphertext, tag) = wrap(secret, install_id, config_key, blob_sha256)

    def opens(secret_, install_id_, config_key_, sha):
        kek_ = hkdf_sha256(ecdh(agreement, ephemeral), secret_, wrap_info(install_id_, config_key_, sha).encode())
        return aes_gcm_decrypt(kek_, nonce, ciphertext, tag) == cek

    attempts = [
        ("as-issued", secret, install_id, config_key, blob_sha256, ""),
        ("other-build-secret", seeded_bytes("release-secret-0.1.0-MacOSArm64", 32), install_id, config_key, blob_sha256,
         "another build's release secret"),
        ("other-install", secret, "0b7e5d21-93a4-4c6f-8e1d-2f4a6c8e0b13", config_key, blob_sha256, ""),
        ("other-config", secret, install_id, "ms2", blob_sha256, ""),
        ("other-blob", secret, install_id, config_key, sha256_hex(b"another blob"), ""),
    ]
    assert opens(*attempts[0][1:5]) and not any(opens(*a[1:5]) for a in attempts[1:])

    return {
        "tag": WRAP_TAG,
        "agreementKey": agreement.json(), "ephemeralKey": ephemeral.json(),
        "releaseSecret": b64(secret), "clientInstallId": install_id, "configKey": config_key, "blobSha256": blob_sha256,
        "cek": b64(cek), "nonce": b64(nonce),
        "info": wrap_info(install_id, config_key, blob_sha256), "sharedSecret": shared.hex(), "kek": kek.hex(),
        "wrapped": {"ephemeralKey": b64(ephemeral.spki), "nonce": b64(nonce), "ciphertext": b64(ciphertext), "tag": b64(tag)},
        "attempts": [{"name": n, "note": note, "releaseSecret": b64(s), "clientInstallId": i, "configKey": c, "blobSha256": h,
                      "opens": idx == 0} for idx, (n, s, i, c, h, note) in enumerate(attempts)],
    }


def config_blob():
    cek = seeded_bytes("config-cek-ms5", 32)
    nonce = seeded_bytes("blob-nonce", 12)
    plaintext = ("[CONEXUS_BCI_CONFIGURATION]\nSECTIONS = INPUT\n\n[INPUT]\n"
                 "POLLING_PERIOD = 0.25 ;a made-up config, not a real one\n")
    header = BLOB_MAGIC + bytes([2])
    ciphertext, tag = aes_gcm_encrypt(cek, nonce, plaintext.encode(), header)
    blob = header + nonce + tag + ciphertext

    def broken(name, data, note=""):
        return {"name": name, "note": note, "blob": b64(data)}

    flipped = bytearray(blob)
    flipped[-1] ^= 0x01
    bad_tag = bytearray(blob)
    bad_tag[5 + 12] ^= 0x01
    invalid = [
        broken("bad-magic", b"IMCX" + blob[4:]),
        broken("version-1", BLOB_MAGIC + bytes([1]) + blob[5:], "the header is authenticated: changing the version breaks the tag"),
        broken("truncated", blob[:5 + 12 + 15], "shorter than header + nonce + tag"),
        broken("tampered-ciphertext", bytes(flipped)),
        broken("tampered-tag", bytes(bad_tag)),
    ]
    return cek, {
        "magic": BLOB_MAGIC.decode(), "version": 2,
        "cek": b64(cek), "nonce": b64(nonce), "plaintext": plaintext, "blob": b64(blob), "blobSha256": sha256_hex(blob),
        "otherCek": b64(seeded_bytes("config-cek-other", 32)),
        "invalid": invalid,
    }


def lease_statement(t):
    return (f"{LEASE_TAG}\n{t['accountId']}\n{t['clientInstallId']}\n{t['serialNumber']}\n{t['configKey']}\n"
            f"{t['blobSha256']}\n{t['issuedAt']}\n{t['notAfter']}\n")


def offline_lease(blob_sha256):
    key, other = Key("lease-signing"), Key("lease-signing-other")
    terms = {"accountId": 42, "clientInstallId": "6f1c2a9e-4b7d-4e2a-9c3b-1d5e8f7a6b40", "serialNumber": "MSV3G01SATKX",
             "configKey": "ms5", "blobSha256": blob_sha256,
             "issuedAt": "2026-10-25T12:00:00Z", "notAfter": "2026-11-01T12:00:00Z"}
    statement = lease_statement(terms)
    signature = sign_der(key, statement)
    longer = dict(terms, notAfter="2026-12-01T12:00:00Z")
    other_serial = dict(terms, serialNumber="MSV3G01SATKY")
    cases = [
        {"name": "valid", "terms": terms, "statement": statement, "publicKey": b64(key.spki), "signature": signature, "valid": True},
        {"name": "extended-not-after", "terms": longer, "statement": lease_statement(longer), "publicKey": b64(key.spki),
         "signature": signature, "valid": False},
        {"name": "other-unit", "terms": other_serial, "statement": lease_statement(other_serial), "publicKey": b64(key.spki),
         "signature": signature, "valid": False},
        {"name": "wrong-key", "terms": terms, "statement": statement, "publicKey": b64(other.spki), "signature": signature,
         "valid": False},
    ]
    return {"tag": LEASE_TAG, "timeFormat": "yyyy-MM-ddTHH:mm:ssZ (UTC, whole seconds)", "key": key.json(), "cases": cases}


def main():
    self_check()
    cek, blob = config_blob()
    doc = {
        "description": "Config delivery's wire formats (accounts/docs/config-management.md). Owned by the accounts repo, "
                       "generated by scripts/gen-config-delivery-vectors.py; copies must be byte-identical. Every key here is "
                       "a throwaway test key: never configure or embed one.",
        "installProof": install_proofs(),
        "cekWrap": cek_wrap(blob["blobSha256"], cek),
        "configBlob": blob,
        "offlineLease": offline_lease(blob["blobSha256"]),
    }
    with open(OUT, "w", encoding="ascii", newline="\n") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"wrote {os.path.relpath(OUT)}")


if __name__ == "__main__":
    main()
