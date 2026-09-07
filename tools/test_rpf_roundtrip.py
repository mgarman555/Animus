"""Build a synthetic RPF7 archive byte for byte, then read it back with the probe.

Exercises the parts of the container that are pure bit-twiddling and therefore easy to get
silently wrong: the packed binary entry, the 3-byte resource fields and the 0x80 discriminator
bit, the name table, the directory tree walk, raw DEFLATE, and a nested archive with its own
StartPos.
"""
import io, os, struct, sys, zlib
sys.path.insert(0, "/home/user/Animus/tools")
import rage_rpf_probe as probe

SECTOR = 512

def pad(b, n=SECTOR):
    return b + b"\x00" * (-len(b) % n)

def build(entries_spec, names_blob, payload, start_sector_data):
    """entries_spec: list of packed 16-byte entries."""
    hdr = struct.pack("<4I", probe.RPF7_MAGIC, len(entries_spec), len(names_blob), probe.ENC_OPEN)
    toc = hdr + b"".join(entries_spec) + names_blob
    toc = pad(toc, start_sector_data * SECTOR)
    return toc + payload

def dir_entry(name_off, index, count):
    return struct.pack("<4I", name_off, 0x7FFFFF00, index, count)

def bin_entry(name_off, size, offset, uncompressed, enc=0):
    buf = (name_off & 0xFFFF) | ((size & 0xFFFFFF) << 16) | ((offset & 0xFFFFFF) << 40)
    return struct.pack("<QII", buf, uncompressed, enc)

def res_entry(name_off, size, offset, sys_flags, gfx_flags):
    b = bytearray(16)
    struct.pack_into("<H", b, 0, name_off)
    b[2] = size & 0xFF; b[3] = (size >> 8) & 0xFF; b[4] = (size >> 16) & 0xFF
    b[5] = offset & 0xFF; b[6] = (offset >> 8) & 0xFF
    b[7] = ((offset >> 16) & 0xFF) | 0x80        # discriminator: marks this entry a resource
    struct.pack_into("<II", b, 8, sys_flags, gfx_flags)
    return bytes(b)

# names table
names = b"\x00"                       # root dir has an empty name at offset 0
def add(n):
    global names
    off = len(names)
    names += n.encode() + b"\x00"
    return off

off_levels  = add("levels")
off_hello   = add("hello.txt")
off_drawable= add("test.ydr")
off_nested  = add("child.rpf")

# ---- payloads ----
hello_raw  = b"the pier deck sits at z=12.9\n" * 40
hello_defl = zlib.compressobj(9, zlib.DEFLATED, -15)
hello_comp = hello_defl.compress(hello_raw) + hello_defl.flush()

ydr_raw  = bytes(range(256)) * 20
ydr_defl = zlib.compressobj(9, zlib.DEFLATED, -15)
ydr_comp = b"RSC7" + b"\x00" * 12 + ydr_defl.compress(ydr_raw) + ydr_defl.flush()   # 16-byte header

# ---- nested child archive (self-contained, sits at its own sector) ----
child_names = b"\x00" + b"inner.txt\x00"
inner_raw = b"nested archives are where all the map data lives\n" * 10
c = zlib.compressobj(9, zlib.DEFLATED, -15)
inner_comp = c.compress(inner_raw) + c.flush()
child_data_sector = 1
child = build(
    [dir_entry(0, 1, 1), bin_entry(1, len(inner_comp), child_data_sector, len(inner_raw))],
    child_names, pad(inner_comp), child_data_sector)

# ---- root archive layout ----
# sector 1: hello.txt, sector 2: test.ydr, sector 3+: child.rpf
data = pad(hello_comp) + pad(ydr_comp) + pad(child)
entries = [
    dir_entry(0, 1, 2),                                            # 0 root: children 1..2
    dir_entry(off_levels, 3, 2),                                   # 1 levels/: children 3..4
    bin_entry(off_nested, len(child), 3, len(child)),              # 2 child.rpf
    bin_entry(off_hello, len(hello_comp), 1, len(hello_raw)),      # 3 levels/hello.txt
    res_entry(off_drawable, len(ydr_comp), 2, 0x10000001, 0x20000002),  # 4 levels/test.ydr
]
archive = build(entries, names, data, 1)

import tempfile
path = os.path.join(tempfile.mkdtemp(), "synthetic.rpf")
with open(path, "wb") as f:
    f.write(archive)

# ---- read it back ----
class K: pass
keys = K(); keys.aes = b"\x00"*32; keys.ng_keys = [b"\x00"*272]*101
keys.tables = [[[0]*256]*16]*17; keys.lut = bytes(256)

fh = open(path, "rb")
root = probe.Archive(fh, "synthetic.rpf", "synthetic.rpf", 0, os.path.getsize(path), keys)
root.scan_children()

ok = True
def check(label, got, want):
    """Prints a short value; a mismatch prints both in full so the failure is diagnosable."""
    global ok
    good = got == want
    ok &= good
    brief = got if isinstance(got, (int, bool)) else f"{len(got)} item(s)/byte(s)"
    if good:
        print(f"  PASS  {label}: {brief}")
    else:
        print(f"  FAIL  {label}\n        got:      {got!r}\n        expected: {want!r}")

paths = sorted(e.path for e in root.files)
check("root file paths", paths,
      ["synthetic.rpf\\child.rpf", "synthetic.rpf\\levels\\hello.txt", "synthetic.rpf\\levels\\test.ydr"])
check("nested archive found", len(root.children), 1)
check("nested file path", [e.path for e in root.children[0].files], ["synthetic.rpf\\child.rpf\\inner.txt"])

hello = next(e for e in root.files if e.name == "hello.txt")
check("binary entry kind", hello.kind, "bin")
check("deflated binary extract", root.extract(hello), hello_raw)

ydr = next(e for e in root.files if e.name == "test.ydr")
check("resource entry kind", ydr.kind, "res")
check("resource extract skips RSC7 header", root.extract(ydr), ydr_raw)
check("resource version nibbles", ((ydr.sys_flags >> 28) & 0xF) << 4 | ((ydr.gfx_flags >> 28) & 0xF), 0x12)

inner = root.children[0].files[0]
check("nested extract via child StartPos", root.children[0].extract(inner), inner_raw)
check("child StartPos", root.children[0].start, 3 * SECTOR)

# offsets past the 24-bit boundary still discriminate correctly
big = bin_entry(0, 0x00FFFF, 0x7FFFFF, 0x00FFFF)
h2 = struct.unpack_from("<I", big, 4)[0]
check("binary entry with max offset still reads as binary", (h2 & 0x80000000) == 0, True)

fh.close()
print("\nALL PASS" if ok else "\nFAILURES ABOVE")
sys.exit(0 if ok else 1)
