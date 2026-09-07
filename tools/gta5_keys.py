"""GTA V key material: pull it straight out of your own gta5.exe, or load a saved dump.

CodeWalker ships no keys and neither does this. The AES key, the 101 NG subkeys, the 272 NG
decrypt tables and the 256-byte hash LUT all live inside the game executable, and the way to find
them is to scan the exe for a block whose SHA-1 matches a known digest. Those digests are in
gta5_key_hashes.py; the key bytes only ever come from your copy of the game.

Method and digests are from CodeWalker's GTAKeys.cs / HashSearch (MIT, (c) 2015 Neodymium).
See third_party/NOTICE-CodeWalker.md.

Use
---
    from gta5_keys import Keys
    keys = Keys.from_exe(r"D:\\Games\\GTAV\\GTA5.exe")     # ~30s, scans the whole exe
    keys.save(r"%APPDATA%\\GameAssetExplorer\\RageKeys")   # so it is a one-time cost
    keys = Keys.from_directory(r"...\\RageKeys")           # instant, subsequently

The saved layout is byte-identical to what CodeWalker's key dump writes, so either source works
and so does the C# in src/Engines/RageEngine/GtaKeys.cs.
"""

import hashlib
import mmap
import os
import struct
import sys
from concurrent.futures import ProcessPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gta5_key_hashes as KH  # noqa: E402

ALIGN = 8              # keys are 8-byte aligned in the executable
BLOCK = 1 << 20        # scan granularity, one MiB per work unit

AES_KEY_SIZE  = 0x20
LUT_SIZE      = 0x100
NG_KEY_SIZE   = 0x110
NG_TABLE_SIZE = 0x400

FILES = {
    "aes_key":   "gtav_aes_key.dat",
    "ng_keys":   "gtav_ng_key.dat",
    "ng_tables": "gtav_ng_decrypt_tables.dat",
    "hash_lut":  "gtav_hash_lut.dat",
}


class KeyError_(Exception):
    pass


# ── the scan ──────────────────────────────────────────────────────────────────

_MM = None      # per-worker mmap, set by the initializer


def _init_worker(path):
    global _MM
    f = open(path, "rb")
    _MM = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)


def _scan_block(args):
    """Hash every aligned offset in one block, at each of the blob lengths we are looking for."""
    start, end, wanted = args
    mm = _MM
    n = len(mm)
    found = {}
    sha1 = hashlib.sha1
    for pos in range(start, min(end, n), ALIGN):
        for length, digests in wanted.items():
            if pos + length > n:
                continue
            idxs = digests.get(sha1(mm[pos:pos + length]).digest())
            if idxs is not None:
                # One blob can serve several slots: GTA V reuses decrypt tables, so its 272
                # table slots are drawn from only 84 distinct blobs, one of them 15 times over.
                # Keying this by a single index would leave 188 slots permanently unfilled and
                # make a perfectly good executable look like it was missing key material.
                for i in idxs:
                    found[(length, i)] = pos
    return found


def _search(path, jobs=None, progress=None):
    """Returns {(blob_length, digest_index): file_offset} for everything located."""
    wanted = {}
    for label, digests, length, _count in KH.GROUPS:
        table = wanted.setdefault(length, {})
        for i, d in enumerate(digests):
            table.setdefault(d, []).append(i)

    distinct = sum(len(t) for t in wanted.values())
    total_slots = sum(count for _l, _d, _len, count in KH.GROUPS)

    size = os.path.getsize(path)
    blocks = [(s, s + BLOCK, wanted) for s in range(0, size, BLOCK)]
    jobs = jobs or min(os.cpu_count() or 4, 16)

    found, done = {}, 0
    with ProcessPoolExecutor(max_workers=jobs, initializer=_init_worker, initargs=(path,)) as ex:
        for part in ex.map(_scan_block, blocks, chunksize=4):
            found.update(part)
            done += 1
            if progress and done % 8 == 0:
                progress(f"  scanned {done * BLOCK / (1 << 20):.0f} / {size / (1 << 20):.0f} MiB, "
                         f"{len(found)} / {total_slots} slots filled "
                         f"(from {distinct} distinct blobs)")
    return found


# ── the key set ───────────────────────────────────────────────────────────────

class Keys:
    def __init__(self, aes, ng_keys, tables, lut, source=""):
        self.aes = aes
        self.ng_keys = ng_keys
        self.tables = tables
        self.lut = lut
        self.source = source
        self._check()

    def _check(self):
        if len(self.aes) != AES_KEY_SIZE:
            raise KeyError_(f"AES key is {len(self.aes)} bytes, expected {AES_KEY_SIZE}")
        if len(self.lut) != LUT_SIZE:
            raise KeyError_(f"hash LUT is {len(self.lut)} bytes, expected {LUT_SIZE}")
        if len(self.ng_keys) != 101 or any(len(k) != NG_KEY_SIZE for k in self.ng_keys):
            raise KeyError_("NG key set is malformed")
        if len(self.tables) != 17 or any(len(r) != 16 for r in self.tables):
            raise KeyError_("NG decrypt tables are malformed")

    # ── sources ───────────────────────────────────────────────────────────────

    @classmethod
    def from_exe(cls, exe_path, jobs=None, progress=print):
        if not os.path.isfile(exe_path):
            raise KeyError_(f"not a file: {exe_path}")

        size = os.path.getsize(exe_path)
        progress(f"scanning {os.path.basename(exe_path)} ({size / (1 << 20):.0f} MiB) for key material…")
        found = _search(exe_path, jobs=jobs, progress=progress)

        missing = []
        for label, digests, length, count in KH.GROUPS:
            got = sum(1 for i in range(count) if (length, i) in found)
            if got != count:
                missing.append(f"{label} ({got}/{count})")
        if missing:
            raise KeyError_(
                "could not locate all key material in this executable: " + ", ".join(missing)
                + ".\nThat usually means the exe is a different edition or version than the digests "
                  "cover, or it is packed. Try the other edition's executable, or use a CodeWalker "
                  "key dump with --keys instead.")

        with open(exe_path, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)

            def blob(length, idx):
                off = found[(length, idx)]
                return bytes(mm[off:off + length])

            aes = blob(AES_KEY_SIZE, 0)
            lut = blob(LUT_SIZE, 0)
            ng_keys = [blob(NG_KEY_SIZE, i) for i in range(101)]

            # 272 flat tables regroup as 17 rounds x 16 columns, each 256 uint32.
            flat = [blob(NG_TABLE_SIZE, i) for i in range(272)]
            tables = []
            for r in range(17):
                row = []
                for c in range(16):
                    raw = flat[r * 16 + c]
                    row.append(list(struct.unpack("<256I", raw)))
                tables.append(row)
            mm.close()

        progress(f"all {sum(c for _l, _d, _len, c in KH.GROUPS)} key slots filled.")
        return cls(aes, ng_keys, tables, lut, source=exe_path)

    @classmethod
    def from_directory(cls, directory):
        def rd(key, expect):
            p = os.path.join(directory, FILES[key])
            if not os.path.isfile(p):
                raise KeyError_(f"missing {FILES[key]} in {directory}")
            data = open(p, "rb").read()
            if len(data) != expect:
                raise KeyError_(f"{FILES[key]} is {len(data)} bytes, expected {expect}")
            return data

        aes = rd("aes_key", AES_KEY_SIZE)
        lut = rd("hash_lut", LUT_SIZE)
        ngr = rd("ng_keys", 101 * NG_KEY_SIZE)
        tbr = rd("ng_tables", 17 * 16 * 256 * 4)

        ng_keys = [ngr[i * NG_KEY_SIZE:(i + 1) * NG_KEY_SIZE] for i in range(101)]
        flat = struct.unpack("<%dI" % (17 * 16 * 256), tbr)
        tables = [[list(flat[(r * 16 + c) * 256:(r * 16 + c) * 256 + 256]) for c in range(16)]
                  for r in range(17)]
        return cls(aes, ng_keys, tables, lut, source=directory)

    @classmethod
    def load(cls, keys_dir=None, exe=None, progress=print):
        """Directory if given, else the exe, else the usual saved locations."""
        if keys_dir:
            return cls.from_directory(keys_dir)
        if exe:
            return cls.from_exe(exe, progress=progress)
        for d in (os.path.join(os.environ.get("APPDATA", ""), "GameAssetExplorer", "RageKeys"),
                  os.path.join(os.getcwd(), "Keys")):
            if d and os.path.isdir(d):
                try:
                    return cls.from_directory(d)
                except KeyError_:
                    pass
        raise KeyError_("no key set found. Pass --exe <path to GTA5.exe> to extract one, "
                        "or --keys <dir> to load a saved dump.")

    # ── output ────────────────────────────────────────────────────────────────

    def save(self, directory):
        """Writes the four files in CodeWalker's layout, which the C# reader also expects."""
        os.makedirs(directory, exist_ok=True)
        with open(os.path.join(directory, FILES["aes_key"]), "wb") as f:
            f.write(self.aes)
        with open(os.path.join(directory, FILES["hash_lut"]), "wb") as f:
            f.write(self.lut)
        with open(os.path.join(directory, FILES["ng_keys"]), "wb") as f:
            for k in self.ng_keys:
                f.write(k)
        with open(os.path.join(directory, FILES["ng_tables"]), "wb") as f:
            for row in self.tables:
                for col in row:
                    f.write(struct.pack("<256I", *col))
        return directory


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="Extract GTA V key material from your own GTA5.exe.")
    ap.add_argument("exe", help="path to GTA5.exe (or GTA5_Enhanced.exe)")
    ap.add_argument("--out", help="directory to write the four .dat files into")
    ap.add_argument("--jobs", type=int, help="parallel workers (default: CPU count)")
    a = ap.parse_args()

    keys = Keys.from_exe(a.exe, jobs=a.jobs)
    out = a.out or os.path.join(os.environ.get("APPDATA", os.getcwd()), "GameAssetExplorer", "RageKeys")
    keys.save(out)
    print(f"wrote {', '.join(FILES.values())} to {out}")
