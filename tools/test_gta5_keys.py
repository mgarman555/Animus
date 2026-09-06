"""Tests the key-extraction machinery without needing a real GTA5.exe.

The real digests can only be satisfied by real key bytes, which are not here and never will be.
What is testable is the machinery: that the aligned scan finds planted blobs at the right offsets,
that it handles blobs straddling a work-block boundary, that it reports honestly when something is
missing, and that a saved key set round-trips byte for byte into the layout the C# reader expects.
"""

import hashlib
import os
import struct
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gta5_keys as gk  # noqa: E402
import gta5_key_hashes as KH  # noqa: E402

ok = True


def check(label, cond, detail=""):
    global ok
    ok &= bool(cond)
    print(f"  {'PASS' if cond else 'FAIL'}  {label}" + (f"   {detail}" if detail and not cond else ""))


def main():
    print("Digest tables")
    counts = {label: (len(d), n) for label, d, _l, n in KH.GROUPS}
    for label, (got, want) in counts.items():
        check(f"{label} has {want} digests", got == want, f"got {got}")
    all_d = [d for _, ds, _, _ in KH.GROUPS for d in ds]
    check("all digests are 20 bytes", all(len(d) == 20 for d in all_d))
    # Duplicates are real, not a parsing artefact: GTA V reuses decrypt tables, so 272 table
    # slots come from 84 distinct blobs. The scan has to fill every slot from a shared blob.
    check("375 key slots in total", len(all_d) == 375, f"got {len(all_d)}")
    check("only 187 distinct blobs need locating", len(set(all_d)) == 187, f"got {len(set(all_d))}")
    tables = [d for l, d, _l2, _n in KH.GROUPS if l == "ng_tables"][0]
    check("the 272 table slots are drawn from 84 distinct blobs", len(set(tables)) == 84,
          f"got {len(set(tables))}")

    print("\nAligned scan over a synthetic executable")
    tmp = tempfile.mkdtemp()
    exe = os.path.join(tmp, "fake.exe")

    # Plant four blobs: one early, one at an odd-but-aligned offset, one straddling the 1 MiB
    # work-block boundary, and one near the end of the file.
    size = 3 * gk.BLOCK + 4096
    body = bytearray(os.urandom(size))
    planted = {
        0x800:                  os.urandom(0x20),
        0x51A38:                os.urandom(0x100),
        gk.BLOCK - 0x40:        os.urandom(0x110),   # crosses the block boundary
        size - 0x400 - 8:       os.urandom(0x400),
    }
    for off, blob in planted.items():
        assert off % gk.ALIGN == 0
        body[off:off + len(blob)] = blob
    open(exe, "wb").write(bytes(body))

    wanted_groups = []
    for i, (off, blob) in enumerate(sorted(planted.items(), key=lambda kv: len(kv[1]))):
        wanted_groups.append((f"g{i}", [hashlib.sha1(blob).digest()], len(blob), 1))

    real_groups = KH.GROUPS
    try:
        KH.GROUPS = wanted_groups
        found = gk._search(exe, jobs=2)
    finally:
        KH.GROUPS = real_groups

    by_len = {len(b): off for off, b in planted.items()}
    for length, off in by_len.items():
        got = found.get((length, 0))
        check(f"blob of {length} bytes found at 0x{off:X}", got == off, f"got {got}")

    straddle = gk.BLOCK - 0x40
    check("a blob straddling the work-block boundary is still found",
          found.get((0x110, 0)) == straddle, f"got {found.get((0x110, 0))}")

    print("\nA blob shared by several slots fills all of them")
    shared = os.urandom(0x400)
    body2 = bytearray(os.urandom(gk.BLOCK))
    body2[0x1000:0x1000 + len(shared)] = shared
    exe2 = os.path.join(tmp, "shared.exe")
    open(exe2, "wb").write(bytes(body2))
    KH_s = KH.GROUPS
    try:
        dg = hashlib.sha1(shared).digest()
        KH.GROUPS = [("dupes", [dg, dg, dg], 0x400, 3)]   # one blob, three slots
        f2 = gk._search(exe2, jobs=2)
    finally:
        KH.GROUPS = KH_s
    check("one blob fills all three slots that share its digest",
          all(f2.get((0x400, i)) == 0x1000 for i in range(3)), f"got {f2}")

    print("\nMissing-material reporting")
    KH_saved = KH.GROUPS
    try:
        KH.GROUPS = [("nonexistent", [hashlib.sha1(b"not in the file").digest()], 0x20, 1)]
        try:
            gk.Keys.from_exe(exe, jobs=2, progress=lambda m: None)
            check("an exe with no key material raises rather than returning junk", False)
        except gk.KeyError_ as e:
            check("an exe with no key material raises rather than returning junk",
                  "could not locate" in str(e))
    finally:
        KH.GROUPS = KH_saved

    print("\nSave / load round trip")
    aes = os.urandom(gk.AES_KEY_SIZE)
    lut = os.urandom(gk.LUT_SIZE)
    ng = [os.urandom(gk.NG_KEY_SIZE) for _ in range(101)]
    tables = [[[(r * 16 + c) * 256 + k for k in range(256)] for c in range(16)] for r in range(17)]
    keys = gk.Keys(aes, ng, tables, lut, source="synthetic")

    out = os.path.join(tmp, "Keys")
    keys.save(out)

    sizes = {n: os.path.getsize(os.path.join(out, n)) for n in gk.FILES.values()}
    check("gtav_aes_key.dat is 32 bytes", sizes["gtav_aes_key.dat"] == 32, str(sizes))
    check("gtav_hash_lut.dat is 256 bytes", sizes["gtav_hash_lut.dat"] == 256, str(sizes))
    check("gtav_ng_key.dat is 101 x 272 = 27472 bytes",
          sizes["gtav_ng_key.dat"] == 101 * 272, str(sizes))
    check("gtav_ng_decrypt_tables.dat is 17x16x256x4 = 278528 bytes",
          sizes["gtav_ng_decrypt_tables.dat"] == 17 * 16 * 256 * 4, str(sizes))

    back = gk.Keys.from_directory(out)
    check("aes survives", back.aes == aes)
    check("lut survives", back.lut == lut)
    check("all 101 ng keys survive", back.ng_keys == ng)
    check("decrypt tables survive with correct grouping", back.tables == tables)

    print("\nThe C# reader expects exactly these sizes")
    cs = open(os.path.join(HERE, "..", "src", "Engines", "RageEngine", "GtaKeys.cs")).read()
    for const, want in (("AesKeySize   = 32", 32), ("NgKeyCount   = 101", 101),
                        ("NgKeySize    = 272", 272), ("HashLutSize  = 256", 256)):
        check(f"C# declares {const}", const in cs)

    print("\nA short key file is rejected rather than half-loaded")
    with open(os.path.join(out, "gtav_hash_lut.dat"), "wb") as f:
        f.write(b"\x00" * 255)
    try:
        gk.Keys.from_directory(out)
        check("a truncated key file raises", False)
    except gk.KeyError_ as e:
        check("a truncated key file raises", "255 bytes, expected 256" in str(e), str(e))

    print("\nALL PASS" if ok else "\nFAILURES ABOVE")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
