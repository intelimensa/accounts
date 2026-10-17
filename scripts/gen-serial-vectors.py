#!/usr/bin/env python3
"""Regenerate testdata/serial-number-vectors.json, the shared fixture for the unit serial-number format.

A serial is 12 characters, PPPPRVAAAAAC: product (4), region (1), format version ('0'), a 5-character
base-36 sequence and a Luhn mod-36 check character over the 11 before it. Two implementations must agree on it:
this repo's Manufacturing/SerialNumber.cs (authoritative: it allocates serials) and MindStoneQuarry's
Identity/SerialNumber.cs (which lets the station reject a mistyped serial before asking the server). The format
is described in firmware/DEVICE_IDENTITY.md; the firmware itself never validates a serial.

The expected values come from the plain Luhn mod-N algorithm written out below, not from either implementation.
The output is deterministic. Usage: scripts/gen-serial-vectors.py [OUTPUT.json]
Then run sync-vectors.sh in the workspace root to copy the file to the other repos.
Needs: python3.
"""
import json
import os
import sys

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "testdata", "serial-number-vectors.json")
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
MAX_SEQUENCE = 36 ** 5 - 1


def check_character(body):
    """Luhn mod N (N = 36), right to left, doubling the rightmost character first."""
    n, factor, total = 36, 2, 0
    for ch in reversed(body):
        addend = factor * ALPHABET.index(ch)
        factor = 1 if factor == 2 else 2
        total += addend // n + addend % n
    return ALPHABET[(n - total % n) % n]


def sequence_text(number):
    out = ""
    for _ in range(5):
        out = ALPHABET[number % 36] + out
        number //= 36
    return out


def serial(product, region, number):
    body = f"{product}{region}0{sequence_text(number)}"
    return body + check_character(body)


def display(s):
    return f"{s[:4]}-{s[4:8]}-{s[8:]}"


def main():
    # parts that must give exactly this serial
    parts = [
        ("MSV2", "G", 0), ("MSV2", "G", 1), ("MSV3", "G", 17), ("MSV1", "G", 35), ("MSV1", "G", 36),
        ("MSV2", "G", 3_000_000), ("MSV3", "G", 3_000_008), ("MSV3", "G", 12_345_678),
        ("MSV2", "A", 1), ("MSV3", "0", 1), ("AB12", "Z", 7),
        ("MSV2", "G", MAX_SEQUENCE - 1), ("MSV2", "G", MAX_SEQUENCE),
    ]
    valid = []
    for product, region, number in parts:
        s = serial(product, region, number)
        valid.append({"product": product, "region": region, "sequence": number, "sequenceText": sequence_text(number),
                      "serial": s, "display": display(s)})
    # every check character must be exercised, '0' included (it is the case where the formula's modulo wraps)
    seen = {v["serial"][-1] for v in valid}
    n = 100
    while len(seen) < 36 and n < 100_000:
        s = serial("MSV3", "G", n)
        if s[-1] not in seen:
            seen.add(s[-1])
            valid.append({"product": "MSV3", "region": "G", "sequence": n, "sequenceText": sequence_text(n),
                          "serial": s, "display": display(s)})
        n += 1
    assert len(seen) == 36

    good = valid[0]["serial"]
    wrong_check = good[:-1] + ALPHABET[(ALPHABET.index(good[-1]) + 1) % 36]
    transposed = serial("MSV3", "G", 3_000_000)
    transposed = transposed[:6] + transposed[7] + transposed[6] + transposed[8:]  # swap two adjacent characters

    # input that must normalise to a canonical serial (upper case; '-', '_' and whitespace removed), or to nothing
    s = valid[5]["serial"]
    normalize = [
        {"input": s, "canonical": s},
        {"input": s.lower(), "canonical": s},
        {"input": display(s), "canonical": s},
        {"input": display(s).lower(), "canonical": s},
        {"input": s[:4] + "_" + s[4:], "canonical": s},
        {"input": "  " + display(s) + "\t", "canonical": s},
        {"input": s[:4] + " " + s[4:8] + " " + s[8:], "canonical": s},
        {"input": "", "canonical": None},
        {"input": "   ", "canonical": None},
        {"input": "-----", "canonical": None},
        {"input": s[:-1], "canonical": None},
        {"input": s + "0", "canonical": None},
        {"input": wrong_check, "canonical": None},
        {"input": transposed, "canonical": None},
        {"input": s[:5] + "1" + s[6:], "canonical": None},                    # format version other than '0'
        {"input": s[:3] + "!" + s[4:], "canonical": None},
        {"input": s[:6] + "ı" + s[7:], "canonical": None},                # dotless i: must not fold to an ASCII 'I'
        {"input": s[:6] + "ſ" + s[7:], "canonical": None},                # long s
        {"input": s[:6] + "K" + s[7:], "canonical": None},                # Kelvin sign
        {"input": s[:6] + "０" + s[7:], "canonical": None},                # full-width digit zero
        {"input": s[:6] + "٠" + s[7:], "canonical": None},                # Arabic-indic digit zero
    ]
    # canonical-looking strings that are not valid serials
    invalid = [x["input"] for x in normalize if x["canonical"] is None and x["input"] == x["input"].strip() and x["input"].strip("-")]
    # (an input that merely normalises to None because it is blank is covered above)

    # a serial that is not in the canonical format is shown unchanged (legacy and staff-entered serials)
    unchanged = ["LEGACY-123", "MSV2-G01S-ATCF-X", "abc123", "MSV2G01SATC", display(s), s.lower()]

    doc = {
        "contract": "intelimensa-serial-number-v1",
        "description": "Shared fixture for the unit serial-number format. Owner: accounts repo (testdata/serial-number-vectors.json); "
                       "copies elsewhere must be byte-identical (sync-vectors.sh). Format: PPPPRVAAAAAC, product, region, version '0', "
                       "5-character base-36 sequence, Luhn mod-36 check character (firmware/DEVICE_IDENTITY.md). "
                       "valid: building from the parts must give `serial`, which must be valid and display as `display`, and `sequence` "
                       "encodes to `sequenceText`. normalize: normalising `input` gives `canonical`, or fails when it is null. "
                       "invalid: none of these is a valid canonical serial. displayUnchanged: not canonical, so shown as given. "
                       "Expected values come from the plain Luhn mod-N algorithm in scripts/gen-serial-vectors.py.",
        "maxSequence": MAX_SEQUENCE,
        "valid": valid,
        "normalize": normalize,
        "invalid": invalid,
        "displayUnchanged": unchanged,
    }
    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    with open(OUT, "w", newline="\n") as f:
        json.dump(doc, f, indent=2, ensure_ascii=True)
        f.write("\n")
    print(f"wrote {os.path.normpath(OUT)}: {len(valid)} valid, {len(normalize)} normalize, {len(invalid)} invalid")


if __name__ == "__main__":
    main()
