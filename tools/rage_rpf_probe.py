#!/usr/bin/env python3
"""
RPF7 (GTA V, PC) archive probe — an independent reimplementation of the container format,
written to be diffed against the C# in src/Engines/RageEngine/.

This exists for the same reason nd_pak_probe.py does: the C# cannot be compiled or run outside
Windows, so correctness gets established against real bytes in Python first and the C# is checked
against it. If the two disagree, one of them is wrong and you know before you spend a day in a
debugger.

Usage
-----
  python tools/rage_rpf_probe.py "D:/Games/GTAV/x64m.rpf" --keys "C:/CodeWalker/Keys"
  python tools/rage_rpf_probe.py "D:/Games/GTAV" --keys ... --scan-dir      # every .rpf under a folder
  python tools/rage_rpf_probe.py "D:/Games/GTAV/x64m.rpf" --keys ... --find vb_        # filter paths
  python tools/rage_rpf_probe.py "D:/Games/GTAV/x64m.rpf" --keys ... --extract <vpath> --out file.bin

Keys
----
Four files, all four required, from a CodeWalker key dump:
  gtav_aes_key.dat             32 bytes
  gtav_ng_key.dat              101 * 272   = 27,472 bytes
  gtav_ng_decrypt_tables.dat   17*16*256*4 = 278,528 bytes
  gtav_hash_lut.dat            256 bytes

The LUT is the one people leave out. NG subkey selection uses a LUT-substituted hash, not joaat,
so without it every table of contents decrypts to noise and there is no other symptom.
"""

import argparse
import os
import struct
import sys
import zlib
from collections import Counter

RPF7_MAGIC = 0x52504637
SECTOR = 512

ENC_NONE = 0
ENC_OPEN = 0x4E45504F
ENC_AES  = 0x0FFFFFF9
ENC_NG   = 0x0FEFFFFF

ENC_NAMES = {ENC_NONE: "NONE", ENC_OPEN: "OPEN", ENC_AES: "AES", ENC_NG: "NG"}


# ── keys ──────────────────────────────────────────────────────────────────────

class Keys:
    def __init__(self, directory):
        def rd(name, expect):
            path = os.path.join(directory, name)
            with open(path, "rb") as f:
                data = f.read()
            if len(data) != expect:
                raise SystemExit(f"{name} is {len(data)} bytes, expected {expect}")
            return data

        self.aes = rd("gtav_aes_key.dat", 32)
        ng = rd("gtav_ng_key.dat", 101 * 272)
        self.ng_keys = [ng[i * 272:(i + 1) * 272] for i in range(101)]
        tab = rd("gtav_ng_decrypt_tables.dat", 17 * 16 * 256 * 4)
        flat = struct.unpack_from("<%dI" % (17 * 16 * 256), tab, 0)
        self.tables = [[list(flat[(r * 16 + c) * 256:(r * 16 + c) * 256 + 256])
                        for c in range(16)] for r in range(17)]
        self.lut = rd("gtav_hash_lut.dat", 256)


def gta5_hash(text, lut):
    """LUT-substituted hash used ONLY to pick an NG subkey. Input is NOT lowercased."""
    result = 0
    for ch in text:
        temp = (1025 * (lut[ord(ch) & 0xFF] + result)) & 0xFFFFFFFF
        result = ((temp >> 6) ^ temp) & 0xFFFFFFFF
    nine = (9 * result) & 0xFFFFFFFF
    return (32769 * ((nine >> 11) ^ nine)) & 0xFFFFFFFF


def joaat(text):
    """Jenkins one-at-a-time over the lowercased string. Keys archetype names to files."""
    h = 0
    for ch in text.lower():
        h = (h + ord(ch)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h ^= h >> 6
    h = (h + (h << 3)) & 0xFFFFFFFF
    h ^= h >> 11
    h = (h + (h << 15)) & 0xFFFFFFFF
    return h


# NG round column mappings. Rounds 0, 1 and 16 use A; rounds 2..15 use B.
ROUND_A = ((0, 1, 2, 3), (4, 5, 6, 7), (8, 9, 10, 11), (12, 13, 14, 15))
ROUND_B = ((0, 7, 10, 13), (1, 4, 11, 14), (2, 5, 8, 15), (3, 6, 9, 12))


def _ng_round(block, subkey, table, mapping):
    out = bytearray(16)
    for i, cols in enumerate(mapping):
        x = subkey[i]
        for c in cols:
            x ^= table[c][block[c]]
        struct.pack_into("<I", out, i * 4, x & 0xFFFFFFFF)
    return out


def decrypt_ng_block(block, subkeys, tables):
    b = _ng_round(block, subkeys[0], tables[0], ROUND_A)
    b = _ng_round(b, subkeys[1], tables[1], ROUND_A)
    for k in range(2, 16):
        b = _ng_round(b, subkeys[k], tables[k], ROUND_B)
    return _ng_round(b, subkeys[16], tables[16], ROUND_A)


def decrypt_ng(data, name, length, keys):
    idx = (gta5_hash(name, keys.lut) + length + (101 - 40)) % 0x65
    key = keys.ng_keys[idx]
    words = struct.unpack("<68I", key)
    subkeys = [words[i * 4:(i + 1) * 4] for i in range(17)]

    out = bytearray(data)
    for b in range(len(data) // 16):
        out[b * 16:(b + 1) * 16] = decrypt_ng_block(data[b * 16:(b + 1) * 16], subkeys, keys.tables)
    return bytes(out)   # any trailing partial block passes through in plaintext


def decrypt_aes(data, key):
    try:
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    except ImportError:
        raise SystemExit("AES archive needs `pip install cryptography` (GTA V is almost all NG, "
                         "so you may not hit this at all)")
    aligned = len(data) - len(data) % 16
    dec = Cipher(algorithms.AES(key), modes.ECB()).decryptor()
    return dec.update(data[:aligned]) + dec.finalize() + data[aligned:]


# ── entries ───────────────────────────────────────────────────────────────────

class Entry:
    __slots__ = ("kind", "name", "path", "offset", "size", "uncompressed",
                 "encrypted", "sys_flags", "gfx_flags", "index", "count")


def size_from_flags(flags):
    s = (((flags >> 27) & 1) << 0) + (((flags >> 26) & 1) << 1) + \
        (((flags >> 25) & 1) << 2) + (((flags >> 24) & 1) << 3) + \
        (((flags >> 17) & 0x7F) << 4) + (((flags >> 11) & 0x3F) << 5) + \
        (((flags >> 7) & 0xF) << 6) + (((flags >> 5) & 3) << 7) + \
        (((flags >> 4) & 1) << 8)
    return (0x200 << (flags & 0xF)) * s


class Archive:
    def __init__(self, fh, name, path, start, size, keys):
        self.fh, self.name, self.path = fh, name, path
        self.start, self.size, self.keys = start, size, keys
        self.entries = []
        self.files = []
        self.children = []
        self.encryption = None
        self._read_header()

    def _read_header(self):
        fh = self.fh
        fh.seek(self.start)
        version, count, names_len, enc = struct.unpack("<4I", fh.read(16))
        if version != RPF7_MAGIC:
            raise ValueError(f"not RPF7 (version 0x{version:08X}, expected 0x{RPF7_MAGIC:08X})")
        self.encryption = enc

        edata = fh.read(count * 16)
        ndata = fh.read(names_len)

        if enc in (ENC_NONE, ENC_OPEN):
            pass
        elif enc == ENC_AES:
            edata = decrypt_aes(edata, self.keys.aes)
            ndata = decrypt_aes(ndata, self.keys.aes)
        else:   # NG, or unknown treated as NG (OpenIV-modified archives land here)
            edata = decrypt_ng(edata, self.name, self.size, self.keys)
            ndata = decrypt_ng(ndata, self.name, self.size, self.keys)

        for i in range(count):
            raw = edata[i * 16:(i + 1) * 16]
            h1, h2 = struct.unpack_from("<2I", raw, 0)
            e = Entry()

            if h2 == 0x7FFFFF00:
                e.kind = "dir"
                name_off = h1
                e.index, e.count = struct.unpack_from("<2I", raw, 8)
            elif (h2 & 0x80000000) == 0:
                e.kind = "bin"
                buf = struct.unpack_from("<Q", raw, 0)[0]
                name_off = buf & 0xFFFF
                e.size = (buf >> 16) & 0xFFFFFF
                e.offset = (buf >> 40) & 0xFFFFFF
                e.uncompressed, enc_type = struct.unpack_from("<2I", raw, 8)
                e.encrypted = enc_type == 1
            else:
                e.kind = "res"
                name_off = struct.unpack_from("<H", raw, 0)[0]
                e.size = raw[2] | (raw[3] << 8) | (raw[4] << 16)
                e.offset = (raw[5] | (raw[6] << 8) | (raw[7] << 16)) & 0x7FFFFF
                e.sys_flags, e.gfx_flags = struct.unpack_from("<2I", raw, 8)
                if e.size == 0xFFFFFF:
                    # sentinel: real size is smeared across the first 16 payload bytes
                    save = fh.tell()
                    fh.seek(self.start + e.offset * SECTOR)
                    head = fh.read(16)
                    fh.seek(save)
                    e.size = head[7] | (head[14] << 8) | (head[5] << 16) | (head[2] << 24)

            end = ndata.find(b"\x00", name_off)
            e.name = ndata[name_off:end if end >= 0 else name_off].decode("ascii", "replace")
            if e.kind == "res":
                e.encrypted = e.name.lower().endswith(".ysc")
            self.entries.append(e)

        self._build_paths()

    def _build_paths(self):
        if not self.entries or self.entries[0].kind != "dir":
            raise ValueError("entry 0 is not the root directory")
        self.entries[0].path = self.path
        stack = [self.entries[0]]
        while stack:
            d = stack.pop()
            lo, hi = d.index, d.index + d.count
            if hi > len(self.entries):
                continue
            for e in self.entries[lo:hi]:
                e.path = d.path + "\\" + e.name.lower()
                if e.kind == "dir":
                    stack.append(e)
                else:
                    self.files.append(e)

    def scan_children(self):
        for e in self.files:
            if e.kind != "bin" or not e.name.lower().endswith(".rpf"):
                continue
            size = e.size if e.size else e.uncompressed
            child = Archive(self.fh, e.name, e.path,
                            self.start + e.offset * SECTOR, size, self.keys)
            child.scan_children()
            self.children.append(child)

    def walk(self):
        yield self
        for c in self.children:
            yield from c.walk()

    def extract(self, e):
        self.fh.seek(self.start + e.offset * SECTOR)
        if e.kind == "bin":
            length = e.size if e.size else e.uncompressed
            data = self.fh.read(length)
            if e.encrypted:
                data = (decrypt_aes(data, self.keys.aes) if self.encryption == ENC_AES
                        else decrypt_ng(data, e.name, e.uncompressed, self.keys))
            if e.size:                       # non-zero on-disk size means deflated
                data = inflate(data) or data
            return data

        # resource: skip the 16-byte RSC7 header, the flags are already in the TOC
        self.fh.seek(16, os.SEEK_CUR)
        data = self.fh.read(e.size - 16)
        if e.encrypted:
            data = (decrypt_aes(data, self.keys.aes) if self.encryption == ENC_AES
                    else decrypt_ng(data, e.name, e.size, self.keys))
        return inflate(data) or data


def inflate(data):
    try:
        return zlib.decompress(data, -15)    # raw DEFLATE, no zlib header
    except zlib.error:
        return None


# ── reporting ─────────────────────────────────────────────────────────────────

def probe(path, keys, find=None, extract=None, out=None, quiet=False):
    with open(path, "rb") as fh:
        root = Archive(fh, os.path.basename(path), os.path.basename(path).lower(),
                       0, os.path.getsize(path), keys)
        root.scan_children()

        archives = list(root.walk())
        files = [(a, e) for a in archives for e in a.files]
        enc = Counter(ENC_NAMES.get(a.encryption, f"0x{a.encryption:08X}") for a in archives)
        ext = Counter(os.path.splitext(e.name.lower())[1] for _, e in files)
        kind = Counter(e.kind for _, e in files)

        print(f"\n=== {path}")
        print(f"  archives (incl. nested) : {len(archives):,}")
        print(f"  files                   : {len(files):,}   ({kind['res']:,} resource, {kind['bin']:,} binary)")
        print(f"  encryption              : " + ", ".join(f"{k}={v}" for k, v in enc.most_common()))
        print(f"  top extensions          : " + ", ".join(f"{k or '(none)'}={v:,}" for k, v in ext.most_common(12)))

        if find:
            hits = [(a, e) for a, e in files if find.lower() in e.path.lower()]
            print(f"\n  {len(hits):,} path(s) matching {find!r}" + ("" if quiet else ", first 40:"))
            if not quiet:
                for a, e in hits[:40]:
                    size = e.size if e.size else (e.uncompressed if e.kind == "bin" else 0)
                    print(f"    {e.path}   [{e.kind} {size:,}B]")

        if extract:
            match = [(a, e) for a, e in files if e.path.lower().endswith(extract.lower())]
            if not match:
                print(f"\n  !! nothing matching {extract!r}")
                return 1
            a, e = match[0]
            data = a.extract(e)
            print(f"\n  extracted {e.path}: {len(data):,} bytes")
            if e.kind == "res":
                print(f"    resource version {((e.sys_flags >> 28) & 0xF) << 4 | ((e.gfx_flags >> 28) & 0xF)}, "
                      f"system {size_from_flags(e.sys_flags):,}B + graphics {size_from_flags(e.gfx_flags):,}B")
            print(f"    first 16 bytes: {data[:16].hex(' ')}")
            if out:
                with open(out, "wb") as f:
                    f.write(data)
                print(f"    written to {out}")

        return 0


def main():
    ap = argparse.ArgumentParser(description="Probe RPF7 (GTA V) archives.")
    ap.add_argument("target", help=".rpf file, or a directory with --scan-dir")
    ap.add_argument("--keys", required=True, help="directory holding the four gtav_*.dat key files")
    ap.add_argument("--scan-dir", action="store_true", help="probe every .rpf under target")
    ap.add_argument("--find", help="print file paths containing this substring")
    ap.add_argument("--extract", help="extract the first file whose path ends with this")
    ap.add_argument("--out", help="write --extract output here")
    ap.add_argument("--quiet", action="store_true", help="counts only, no path listings")
    args = ap.parse_args()

    keys = Keys(args.keys)

    if args.scan_dir:
        targets = []
        for dirpath, _, names in os.walk(args.target):
            targets += [os.path.join(dirpath, n) for n in names if n.lower().endswith(".rpf")]
        targets.sort()
        print(f"{len(targets)} root archive(s) under {args.target}")
        total_files = 0
        failed = []
        for t in targets:
            try:
                with open(t, "rb") as fh:
                    root = Archive(fh, os.path.basename(t), os.path.basename(t).lower(),
                                   0, os.path.getsize(t), keys)
                    root.scan_children()
                    n = sum(len(a.files) for a in root.walk())
                    total_files += n
                    print(f"  {os.path.basename(t):<24} {n:>9,} files  "
                          f"{ENC_NAMES.get(root.encryption, hex(root.encryption))}")
            except Exception as ex:
                failed.append((t, ex))
                print(f"  {os.path.basename(t):<24} FAILED: {ex}")
        print(f"\nTOTAL {total_files:,} files, {len(failed)} archive(s) failed")
        return 0 if total_files > 0 else 1

    return probe(args.target, keys, args.find, args.extract, args.out, args.quiet)


if __name__ == "__main__":
    sys.exit(main())
