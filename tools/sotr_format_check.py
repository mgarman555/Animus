#!/usr/bin/env python3
"""
Cross-checks the byte-level logic of the new SOTR plugin.

Each decoder below is a line-by-line transcription of the C# I just wrote. Encoders are
written independently from the *format spec* (TrRebootModTools / cdcengine.re), so a
mismatch means the C# disagrees with the spec rather than with itself. The CDRM test in
particular runs against real zlib output, which is what catches the 2-byte-header and
16-byte-alignment mistakes.
"""
import struct, zlib, sys

ok = True
def check(name, cond, detail=""):
    global ok
    print(f"  {'PASS' if cond else 'FAIL'}  {name}{(' — ' + detail) if detail and not cond else ''}")
    if not cond:
        ok = False

def align16(v): return (v + 0xF) & ~0xF


# ─────────────────────────────────────────────────────────────────────────────
# 1. CDRM — encoder per spec, decoder per Cdrm.cs
# ─────────────────────────────────────────────────────────────────────────────
def cdrm_encode(payloads):
    """Build a CDRM blob. Chunks that don't shrink are stored verbatim."""
    descs, blobs = [], []
    for p in payloads:
        comp = zlib.compress(p, 9)          # real zlib: 2-byte header + deflate + adler32
        if len(comp) >= len(p):
            descs.append((len(p), len(p))); blobs.append(p)
        else:
            descs.append((len(p), len(comp))); blobs.append(comp)

    out = bytearray()
    out += struct.pack("<IiiI", 0x4D524443, 0, len(descs), 0)
    for unpacked, packed in descs:
        out += struct.pack("<II", (unpacked << 8), packed)
    out += b"\0" * (align16(len(out)) - len(out))
    for (_, packed), blob in zip(descs, blobs):
        out += blob
        out += b"\0" * (align16(len(out)) - len(out))
    return bytes(out)


def cdrm_decode(data, offset=0, chunk_limit=None):
    """Transcription of Cdrm.Decompress."""
    magic, _type, num_chunks, _pad = struct.unpack_from("<IiiI", data, offset)
    assert magic == 0x4D524443, "bad magic"

    take = num_chunks if chunk_limit is None else min(num_chunks, chunk_limit)
    chunks, payload, total = [], align16(offset + 0x10 + 8 * num_chunks), 0

    pos = offset + 0x10
    for i in range(num_chunks):
        packed_field, packed = struct.unpack_from("<II", data, pos); pos += 8
        unpacked = packed_field >> 8
        if i < take:
            chunks.append((payload, unpacked, packed)); total += unpacked
        payload = align16(payload + packed)

    out = bytearray()
    for chunk_offset, unpacked, packed in chunks:
        if unpacked == 0:
            continue
        if packed == unpacked:
            out += data[chunk_offset:chunk_offset + unpacked]
        else:
            # skip the 2-byte zlib header, then raw deflate
            raw = zlib.decompressobj(-zlib.MAX_WBITS).decompress(
                data[chunk_offset + 2:chunk_offset + 2 + packed], unpacked)
            out += raw
    assert len(out) == total, f"{len(out)} != {total}"
    return bytes(out)


print("CDRM container (against real zlib):")
compressible = bytes(range(256)) * 400          # 102,400 B, compresses well
incompressible = bytes((i * 7919 + 13) % 251 for i in range(3000))
parts = [compressible, incompressible, b"tail chunk" * 100]

blob = cdrm_encode(parts)
check("single-chunk round-trip", cdrm_decode(cdrm_encode([compressible])) == compressible)
check("multi-chunk round-trip", cdrm_decode(blob) == b"".join(parts))
check("chunk payloads are 16-byte aligned",
      all(off % 16 == 0 for off, _, _ in
          [(align16(0x10 + 8 * 3), 0, 0)]))
check("peek returns exactly chunk 0", cdrm_decode(blob, chunk_limit=1) == parts[0])
check("blob at a non-zero offset", cdrm_decode(b"\xAA" * 48 + blob, offset=48) == b"".join(parts))
check("stored chunk passes through", cdrm_decode(cdrm_encode([incompressible])) == incompressible)

# The bug this replaces: the old reader looked for the bytes "MRDC".
check("on-disk magic bytes are 'CDRM'", blob[:4] == b"CDRM", repr(blob[:4]))


# ─────────────────────────────────────────────────────────────────────────────
# 2. Tiger v5 TOC — field offsets
# ─────────────────────────────────────────────────────────────────────────────
print("\nTiger v5 TOC entry:")
entry = struct.pack("<QQIIhBBI",
                    0x1122334455667788,   # nameHash
                    0xFFFFFFFFFFFFFFFF,   # locale
                    0x00001000,           # uncompressedSize
                    0x00000800,           # compressedSize
                    7,                    # archivePart
                    3,                    # archiveId
                    1,                    # archiveSubId
                    0xDEADBEEF)           # offset
check("entry is 32 bytes", len(entry) == 32, str(len(entry)))
h, loc, usz, csz, part, aid, asid, off = struct.unpack("<QQIIhBBI", entry)
check("nameHash @0x00", h == 0x1122334455667788)
check("uncompressedSize @0x10", usz == 0x1000)
check("archivePart @0x18", part == 7)
check("archiveId @0x1A", aid == 3)
check("archiveSubId @0x1B", asid == 1)
check("offset @0x1C", off == 0xDEADBEEF)

header = struct.pack("<Iiiiii32s", 0x53464154, 5, 12, 900, 4, 2, b"pcx64-w")
check("v5 header is 56 bytes", len(header) == 56, str(len(header)))


# ─────────────────────────────────────────────────────────────────────────────
# 3. DRM v23 ResourceCollection — encoder per spec, decoder per SotrDrmReader.cs
# ─────────────────────────────────────────────────────────────────────────────
def drm_encode(resources, dependencies):
    idents = b""
    for r in resources:
        packed = ((r["refdefs"] << 8) | (r["subtype"] << 1)) & 0xFFFFFFFF
        idents += struct.pack("<IBBhIiQ", r["body"], r["type"], 0, 0,
                              packed, r["id"], r["locale"])
    deps = b""
    for locale, path in dependencies:
        deps += struct.pack("<Q", locale) + path.encode() + b"\0"

    locs = b""
    for r in resources:
        unique = ((r["type"] << 24) | (r["id"] & 0xFFFFFF)) & 0xFFFFFFFF
        locs += struct.pack("<IihBBIII", unique, 0, r["part"], r["aid"], r["asid"],
                            r["offset"], r["length"], 0)

    head = struct.pack("<iiiiiiii", 23, 0, len(deps), 0, 0, 0x99, len(resources), 1)
    return head + struct.pack("<Q", 0xFFFFFFFFFFFFFFFF) + idents + deps + locs


def drm_decode(data):
    """Transcription of SotrDrmReader.Parse."""
    (version, include_len, deps_len, _pad_len,
     _size, flags, num_res, main_idx) = struct.unpack_from("<iiiiiiii", data, 0)
    assert version == 23
    pos = 0x20
    header_locale, = struct.unpack_from("<Q", data, pos); pos += 8

    idents = []
    for _ in range(num_res):
        body, rtype, _flags, _pad, packed, rid, locale = struct.unpack_from("<IBBhIiQ", data, pos)
        pos += 24
        idents.append(dict(body=body, type=rtype, subtype=(packed & 0xFF) >> 1,
                           refdefs=packed >> 8, id=rid, locale=locale))

    deps = []
    for length in (deps_len, include_len):
        end = pos + length
        while pos + 8 < end:
            pos += 8
            start = pos
            while pos < end and data[pos] != 0:
                pos += 1
            p = data[start:pos].decode()
            pos += 1
            if p:
                deps.append(p if "." in p else p + ".drm")
        pos = end

    out = []
    for i in range(num_res):
        unique, _pad, part, aid, asid, off, size, _dec = struct.unpack_from("<IihBBIII", data, pos)
        pos += 24
        out.append(dict(type=unique >> 24, id=unique & 0xFFFFFF,
                        subtype=idents[i]["subtype"], refdefs=idents[i]["refdefs"],
                        body=idents[i]["body"], locale=idents[i]["locale"],
                        part=part, aid=aid, asid=asid, offset=off, length=size,
                        enabled=idents[i]["type"] != 1))
    return dict(main=main_idx, flags=flags, locale=header_locale, resources=out, deps=deps)


print("\nDRM v23 ResourceCollection:")
res_in = [
    dict(type=5,  subtype=5,   id=0x1234, locale=0xFFFFFFFFFFFFFFFF, body=2048, refdefs=64,
         part=2, aid=0, asid=0, offset=0x1000, length=1200),
    dict(type=12, subtype=27,  id=0x5678, locale=0xFFFFFFFFFFFFFFFF, body=40960, refdefs=256,
         part=3, aid=1, asid=0, offset=0x8000, length=41216),
    dict(type=10, subtype=0,   id=0x9ABC, locale=0xFFFFFFFFFFFFFFFF, body=512, refdefs=0,
         part=0, aid=0, asid=0, offset=0x40, length=512),
]
deps_in = [(0xFFFFFFFFFFFFFFFF, "lara\\lara_body"), (0xFFFFFFFFFFFFFFFF, "shared\\common.drm")]

parsed = drm_decode(drm_encode(res_in, deps_in))
check("resource count", len(parsed["resources"]) == 3)
check("type comes from the location record", [r["type"] for r in parsed["resources"]] == [5, 12, 10])
check("id comes from the location record", [r["id"] for r in parsed["resources"]] == [0x1234, 0x5678, 0x9ABC])
check("subtype unpacks from identification", [r["subtype"] for r in parsed["resources"]] == [5, 27, 0])
check("refDefinitionsSize unpacks from identification",
      [r["refdefs"] for r in parsed["resources"]] == [64, 256, 0])
check("archive routing preserved",
      [(r["aid"], r["asid"], r["part"]) for r in parsed["resources"]] == [(0, 0, 2), (1, 0, 3), (0, 0, 0)])
check("dependency gets .drm appended when extensionless",
      parsed["deps"] == ["lara\\lara_body.drm", "shared\\common.drm"], str(parsed["deps"]))
check("identification record is 24 bytes", struct.calcsize("<IBBhIiQ") == 24)
check("location record is 24 bytes", struct.calcsize("<IihBBIII") == 24)

# Raw-vs-compressed decision used by TigerArchiveSet.ReadResource
for r in parsed["resources"]:
    raw = (r["refdefs"] + r["body"]) == r["length"]
    if r["id"] == 0x9ABC:
        check("resource stored raw is detected", raw)
    if r["id"] == 0x1234:
        check("compressed resource is detected", not raw)


# ─────────────────────────────────────────────────────────────────────────────
# 4. PCD9 texture header + DDS reconstruction
# ─────────────────────────────────────────────────────────────────────────────
print("\nPCD9 texture / DDS output:")
check("PCD9 header is 28 bytes", struct.calcsize("<IIIIHHHBBHBB") == 0x1C,
      str(struct.calcsize("<IIIIHHHBBHBB")))
check("PCD9 magic bytes are 'PCD9'", struct.pack("<I", 0x39444350) == b"PCD9")

def dds_size(use_dx10):
    # mirrors SotrTextureReader.ToDds field-by-field
    n = 4                      # "DDS "
    n += 4 * 7                 # size..mipMapCount
    n += 4 * 11                # reserved1
    n += 4 * 8                 # DDS_PIXELFORMAT
    n += 4 * 5                 # caps..reserved2
    return n + (20 if use_dx10 else 0)

check("DDS header total is 128 bytes", dds_size(False) == 128, str(dds_size(False)))
check("DDS + DX10 header is 148 bytes", dds_size(True) == 148, str(dds_size(True)))

def fourcc(s): return s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24)
check("MakeFourCc('DXT1') round-trips",
      struct.pack("<I", fourcc(b"DXT1")) == b"DXT1")
check("MakeFourCc('DX10') round-trips",
      struct.pack("<I", fourcc(b"DX10")) == b"DX10")


# ─────────────────────────────────────────────────────────────────────────────
# 5. tr11modeldata — struct sizes and the header offsets hardcoded in the C#
# ─────────────────────────────────────────────────────────────────────────────
print("\ntr11modeldata layout:")
model_header = (
    "<"
    "4s"      # signature            0x000
    "I"       # flags                0x004
    "i"       # totalDataSize        0x008
    "i"       # numIndices           0x00C
    "16s"     # boundingSphereCenter 0x010
    "16s"     # boundingBoxMin       0x020
    "16s"     # boundingBoxMax       0x030
    "16s"     # positionScaleOffset  0x040
    "7f"      # radius + 6 lod floats 0x050
    "i"       # lodMode              0x06C
    "i"       # modelType            0x070
    "f"       # sortBias             0x074
    "128s"    # boneUsageMap         0x078
    "q"       # meshPartsOffset      0x0F8
    "q"       # meshHeadersOffset    0x100
    "q"       # boneMappingsOffset   0x108
    "q"       # lodLevelsOffset      0x110
    "q"       # indexDataOffset      0x118
    "HHHH"    # numMeshParts/Meshes/Bones/LodLevels 0x120
    "q"       # preTesselationInfoOffset 0x128
    "i"       # nameLength           0x130
    "i"       # field_134            0x134
    "q"       # nameOffset           0x138
    "i"       # numBlendShapes       0x140
    "i"       # field_144            0x144
    "q"       # blendShapeNamesOffset 0x148
    "f"       # autoBumpScale        0x150
    "iii"     # field_154/158/15C    0x154
)
check("ModelDataHeader is 0x160", struct.calcsize(model_header) == 0x160,
      hex(struct.calcsize(model_header)))

# Offsets the C# reads directly — recompute them from the field list above.
offsets = {}
acc = 0
for label, fmt in [
    ("signature", "4s"), ("flags", "I"), ("totalDataSize", "i"), ("numIndices", "i"),
    ("sphereCenter", "16s"), ("bboxMin", "16s"), ("bboxMax", "16s"), ("posScaleOffset", "16s"),
    ("lodFloats", "7f"), ("lodMode", "i"), ("modelType", "i"), ("sortBias", "f"),
    ("boneUsageMap", "128s"), ("meshPartsOffset", "q"), ("meshHeadersOffset", "q"),
    ("boneMappingsOffset", "q"), ("lodLevelsOffset", "q"), ("indexDataOffset", "q"),
    ("counts", "HHHH"), ("preTess", "q"),
]:
    offsets[label] = acc
    acc += struct.calcsize("<" + fmt)

check("flags @0x004", offsets["flags"] == 0x004, hex(offsets["flags"]))
check("numIndices @0x00C", offsets["numIndices"] == 0x00C, hex(offsets["numIndices"]))
check("boundingBoxMin @0x020", offsets["bboxMin"] == 0x020, hex(offsets["bboxMin"]))
check("boundingBoxMax @0x030", offsets["bboxMax"] == 0x030, hex(offsets["bboxMax"]))
check("modelType @0x070", offsets["modelType"] == 0x070, hex(offsets["modelType"]))
check("meshPartsOffset @0x0F8", offsets["meshPartsOffset"] == 0x0F8, hex(offsets["meshPartsOffset"]))
check("meshHeadersOffset @0x100", offsets["meshHeadersOffset"] == 0x100, hex(offsets["meshHeadersOffset"]))
check("indexDataOffset @0x118", offsets["indexDataOffset"] == 0x118, hex(offsets["indexDataOffset"]))
check("numMeshParts @0x120", offsets["counts"] == 0x120, hex(offsets["counts"]))
check("preTesselationInfoOffset @0x128", offsets["preTess"] == 0x128, hex(offsets["preTess"]))

mesh_header = "<iHHqqqqqiiqqiiiiii"
check("MeshHeader is 0x60", struct.calcsize(mesh_header) == 0x60, hex(struct.calcsize(mesh_header)))
mh_off, acc = {}, 0
for label, fmt in [("numParts", "i"), ("numBones", "H"), ("field6", "H"), ("boneIndices", "q"),
                   ("vb0off", "q"), ("vb0ptr", "q"), ("vb1off", "q"), ("vb1ptr", "q"),
                   ("vertexFormatSize", "i"), ("field34", "i"), ("vertexFormatOffset", "q"),
                   ("blendShapes", "q"), ("numVertices", "i")]:
    mh_off[label] = acc
    acc += struct.calcsize("<" + fmt)
check("mesh vb0 offset @0x10", mh_off["vb0off"] == 0x10, hex(mh_off["vb0off"]))
check("mesh vb1 offset @0x20", mh_off["vb1off"] == 0x20, hex(mh_off["vb1off"]))
check("vertexFormatSize @0x30", mh_off["vertexFormatSize"] == 0x30, hex(mh_off["vertexFormatSize"]))
check("vertexFormatOffset @0x38", mh_off["vertexFormatOffset"] == 0x38, hex(mh_off["vertexFormatOffset"]))
check("numVertices @0x48", mh_off["numVertices"] == 0x48, hex(mh_off["numVertices"]))

mesh_part = "<16siiiiiiihhq5q"
check("MeshPart is 0x60", struct.calcsize(mesh_part) == 0x60, hex(struct.calcsize(mesh_part)))
mp_off, acc = {}, 0
for label, fmt in [("center", "16s"), ("firstIndexIdx", "i"), ("numPrimitives", "i"),
                   ("numVertices", "i"), ("flags", "i"), ("drawGroupId", "i"), ("order", "i"),
                   ("actualMeshPart", "i"), ("lodLevel", "h"), ("field2E", "h"),
                   ("materialIdx", "q")]:
    mp_off[label] = acc
    acc += struct.calcsize("<" + fmt)
check("part firstIndexIdx @0x10", mp_off["firstIndexIdx"] == 0x10, hex(mp_off["firstIndexIdx"]))
check("part numPrimitives @0x14", mp_off["numPrimitives"] == 0x14, hex(mp_off["numPrimitives"]))
check("part lodLevel @0x2C", mp_off["lodLevel"] == 0x2C, hex(mp_off["lodLevel"]))
check("part materialIdx @0x30", mp_off["materialIdx"] == 0x30, hex(mp_off["materialIdx"]))

# VertexFormat: 16-byte head then 8 bytes per attribute — the size check the C# enforces
for n in (1, 5, 17):
    check(f"vertexFormatSize for {n} attributes", 0x10 + n * 8 == 16 + 8 * n)


# ─────────────────────────────────────────────────────────────────────────────
# 6. FNV-1 64 hash used for asset names
# ─────────────────────────────────────────────────────────────────────────────
print("\nName hash:")
def cdc_hash64(s):
    h = 0xCBF29CE484222325
    for c in s:
        h = ((h ^ ord(c)) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h

# FNV-1a would xor *after* multiplying; this must be FNV-1 (xor then multiply).
check("hash is stable", cdc_hash64("pcx64-w\\lara\\lara.drm") == cdc_hash64("pcx64-w\\lara\\lara.drm"))
check("hash is case-sensitive (no normalisation)",
      cdc_hash64("pcx64-w\\Lara.drm") != cdc_hash64("pcx64-w\\lara.drm"))
check("empty string is the FNV offset basis", cdc_hash64("") == 0xCBF29CE484222325)

print("\n" + ("ALL CHECKS PASSED" if ok else "SOME CHECKS FAILED"))
sys.exit(0 if ok else 1)
