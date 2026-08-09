#!/usr/bin/env python3
r"""
Shadow of the Tomb Raider archive probe — validates the format assumptions the C# SOTR
plugin is built on, against a real game install, with no .NET build required.

This is the same "prove it in Python against real bytes first" approach that was used for the
TLOU2 paks (see tools/nd_pak_probe.py). Every reader below is a deliberate transcription of
the corresponding C# in src/Engines/SotrEngine/, so a failure here is a failure there, and the
report tells you exactly which layer broke.

Stdlib only — no numpy, no pillow, nothing to install.

Usage (Windows):
    python tools\sotr_probe.py "C:\Users\madie\Documents\Shadow of the Tomb Raider"

    # also write sample assets you can open in Blender / an image viewer:
    python tools\sotr_probe.py "C:\...\Shadow of the Tomb Raider" --extract out_sotr

Useful flags:
    --extract DIR   write sample .dds textures and .obj meshes to DIR
    --collections N how many .drm collections to parse   (default 400, 0 = all)
    --samples N     how many textures/models to decode   (default 8)
    --hashlist PATH override the hash->path dictionary location
    --verbose       print per-item detail instead of just the summary
"""

import argparse
import os
import struct
import sys
import zlib
from collections import Counter, defaultdict

# ── report plumbing ───────────────────────────────────────────────────────────

PASS, FAIL, WARN = "PASS", "FAIL", "WARN"
_results = []


def record(status, name, detail=""):
    _results.append((status, name, detail))
    mark = {PASS: "  PASS", FAIL: "  FAIL", WARN: "  WARN"}[status]
    print(f"{mark}  {name}" + (f" — {detail}" if detail else ""))


def section(title):
    print()
    print(title)
    print("-" * len(title))


def u8(b, o):  return b[o]
def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def i16(b, o): return struct.unpack_from("<h", b, o)[0]
def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def i32(b, o): return struct.unpack_from("<i", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]
def f32(b, o): return struct.unpack_from("<f", b, o)[0]


def align16(v):
    return (v + 0xF) & ~0xF


# ── CDRM (mirrors src/Engines/SotrEngine/Cdrm.cs) ─────────────────────────────

CDRM_MAGIC = 0x4D524443          # on-disk bytes 'C','D','R','M'


def cdrm_has_magic(fh, offset):
    if offset < 0:
        return False
    fh.seek(offset)
    head = fh.read(4)
    return len(head) == 4 and struct.unpack("<I", head)[0] == CDRM_MAGIC


def cdrm_decompress(fh, offset, chunk_limit=None):
    fh.seek(offset)
    header = fh.read(16)
    if len(header) < 16:
        raise ValueError("truncated CDRM header")

    magic, _type, num_chunks, _pad = struct.unpack("<IiiI", header)
    if magic != CDRM_MAGIC:
        raise ValueError("not a CDRM blob")
    if num_chunks < 0 or num_chunks > 0x100000:
        raise ValueError(f"chunk count out of range ({num_chunks})")

    table = fh.read(8 * num_chunks)
    if len(table) < 8 * num_chunks:
        raise ValueError("truncated CDRM chunk table")

    take = num_chunks if chunk_limit is None else min(num_chunks, chunk_limit)
    payload = align16(offset + 0x10 + 8 * num_chunks)
    chunks = []

    for i in range(num_chunks):
        packed_field, packed = struct.unpack_from("<II", table, i * 8)
        unpacked = packed_field >> 8
        if i < take:
            chunks.append((payload, unpacked, packed))
        payload = align16(payload + packed)

    out = bytearray()
    for chunk_offset, unpacked, packed in chunks:
        if unpacked == 0:
            continue
        fh.seek(chunk_offset)
        if packed == unpacked:
            out += fh.read(unpacked)
        else:
            fh.seek(chunk_offset + 2)          # skip the 2-byte zlib header
            raw = fh.read(packed)
            out += zlib.decompressobj(-zlib.MAX_WBITS).decompress(raw, unpacked)
    return bytes(out)


# ── TAFS v5 archive (mirrors TigerReader.cs) ──────────────────────────────────

TAFS_MAGIC = 0x53464154


class TigerArchive:
    def __init__(self, index_path):
        self.index_path = index_path
        self.parts = {}
        self.entries = []

    def open(self):
        with open(self.index_path, "rb") as fh:
            header = fh.read(56)
            if len(header) < 56:
                raise ValueError("truncated TAFS header")
            (magic, self.version, self.num_parts, self.num_files,
             self.id, self.sub_id) = struct.unpack_from("<Iiiiii", header, 0)
            if magic != TAFS_MAGIC:
                raise ValueError(f"bad magic 0x{magic:08X}")
            if self.version != 5:
                raise ValueError(f"unsupported version {self.version}")
            self.platform = header[0x18:0x38].split(b"\0")[0].decode("ascii", "replace")

            toc = fh.read(32 * self.num_files)

        for i in range(self.num_files):
            (name_hash, locale, usize, csize,
             part, aid, asid, offset) = struct.unpack_from("<QQIIhBBI", toc, i * 32)
            self.entries.append(dict(
                name_hash=name_hash, locale=locale, usize=usize, csize=csize,
                part=part, aid=aid, asid=asid, offset=offset))

    def part_path(self, part):
        suffix = ".000.tiger"
        if self.index_path.lower().endswith(suffix):
            return self.index_path[: -len(suffix)] + f".{part:03d}.tiger"
        return self.index_path

    def part_file(self, part):
        fh = self.parts.get(part)
        if fh is None:
            fh = open(self.part_path(part), "rb")
            self.parts[part] = fh
        return fh

    def read_blob(self, part, offset, length, assume_uncompressed=False, chunk_limit=None):
        fh = self.part_file(part)
        if not assume_uncompressed and cdrm_has_magic(fh, offset):
            return cdrm_decompress(fh, offset, chunk_limit)
        fh.seek(offset)
        return fh.read(length)

    def close(self):
        for fh in self.parts.values():
            fh.close()
        self.parts.clear()


class TigerArchiveSet:
    """Mirrors TigerArchiveSet.cs — routes reads by (archiveId, archiveSubId)."""

    def __init__(self):
        self.archives = []
        self.by_id = {}

    def add(self, archive):
        self.archives.append(archive)
        self.by_id.setdefault((archive.id, archive.sub_id), archive)

    def find(self, aid, asid):
        return self.by_id.get((aid, asid))

    def read_entry(self, entry, listed_in, chunk_limit=None):
        owner = self.find(entry["aid"], entry["asid"]) or listed_in
        return owner.read_blob(entry["part"], entry["offset"], entry["usize"],
                               chunk_limit=chunk_limit)

    def read_resource_body(self, r):
        owner = self.find(r["aid"], r["asid"])
        if owner is None:
            return None
        raw_stored = (r["refdefs"] + r["body"]) == r["length"]
        data = owner.read_blob(r["part"], r["offset"], r["length"],
                               assume_uncompressed=raw_stored)
        skip = min(r["refdefs"], len(data))
        return data[skip:]

    def close(self):
        for a in self.archives:
            a.close()


# ── DRM v23 ResourceCollection (mirrors SotrDrmReader.cs) ─────────────────────

RESOURCE_TYPES = {
    0: "Unknown", 1: "Empty", 2: "Animation", 3: "SpeedTree", 4: "BlendShapeDriver",
    5: "Texture", 6: "SoundBank", 7: "Dtp", 8: "Script", 9: "ShaderLib", 10: "Material",
    11: "GlobalContentReference", 12: "Model", 13: "CollisionModel", 14: "ObjectReference",
    15: "AnimationLib", 16: "Biome", 17: "LocalString",
}
SUBTYPE_MODEL, SUBTYPE_MODELDATA = 26, 27


def parse_drm(data):
    if len(data) < 0x28 or i32(data, 0) != 23:
        return None

    include_len = i32(data, 0x04)
    deps_len    = i32(data, 0x08)
    num_res     = i32(data, 0x18)
    main_idx    = i32(data, 0x1C)

    if num_res < 0 or num_res > 65536:
        return None
    if not (0 <= deps_len <= len(data)) or not (0 <= include_len <= len(data)):
        return None

    pos = 0x20
    header_locale = u64(data, pos); pos += 8

    idents = []
    for _ in range(num_res):
        if pos + 24 > len(data):
            return None
        body   = u32(data, pos)
        rtype  = u8(data, pos + 4)
        packed = u32(data, pos + 8)
        rid    = i32(data, pos + 0x0C)
        locale = u64(data, pos + 0x10)
        pos += 24
        idents.append(dict(body=body, type=rtype,
                           subtype=(packed & 0xFF) >> 1,
                           refdefs=packed >> 8, id=rid, locale=locale))

    deps = []
    for length in (deps_len, include_len):
        end = min(pos + length, len(data))
        while pos + 8 < end:
            pos += 8
            start = pos
            while pos < end and data[pos] != 0:
                pos += 1
            name = data[start:pos].decode("latin-1", "replace")
            pos += 1
            if name:
                deps.append(name if "." in name else name + ".drm")
        pos = end

    resources = []
    for i in range(num_res):
        if pos + 24 > len(data):
            break
        unique = u32(data, pos)
        part   = i16(data, pos + 0x08)
        aid    = u8(data, pos + 0x0A)
        asid   = u8(data, pos + 0x0B)
        off    = u32(data, pos + 0x0C)
        size   = u32(data, pos + 0x10)
        pos += 24
        ident = idents[i]
        resources.append(dict(
            type=unique >> 24, id=unique & 0x00FFFFFF,
            subtype=ident["subtype"], refdefs=ident["refdefs"], body=ident["body"],
            locale=ident["locale"], part=part, aid=aid, asid=asid,
            offset=off, length=size, enabled=ident["type"] != 1))

    return dict(main=main_idx, locale=header_locale, resources=resources, deps=deps)


# ── PCD9 texture (mirrors SotrTextureReader.cs) ───────────────────────────────

PCD9_MAGIC = 0x39444350

DXGI_NAMES = {
    28: "RGBA8", 29: "RGBA8", 49: "R8G8", 61: "R8",
    71: "BC1", 72: "BC1", 74: "BC2", 75: "BC2", 77: "BC3", 78: "BC3",
    80: "BC4", 83: "BC5", 87: "BGRA8", 91: "BGRA8", 95: "BC6H", 98: "BC7", 99: "BC7",
}
BLOCK8  = {71, 72, 80}
BLOCK16 = {74, 75, 77, 78, 83, 95, 98, 99}
BPP     = {61: 8, 49: 16, 28: 32, 29: 32, 87: 32, 91: 32}


def surface_size(fmt, w, h):
    if fmt in BLOCK8:
        return ((w + 3) // 4) * ((h + 3) // 4) * 8
    if fmt in BLOCK16:
        return ((w + 3) // 4) * ((h + 3) // 4) * 16
    bpp = BPP.get(fmt, 0)
    return w * h * bpp // 8 if bpp else 0


def parse_pcd9(body):
    if len(body) < 0x1C or u32(body, 0) != PCD9_MAGIC:
        return None
    tex = dict(
        fmt=u32(body, 0x04), size=u32(body, 0x08), highres=u32(body, 0x0C),
        width=u16(body, 0x10), height=u16(body, 0x12), volume=u16(body, 0x14),
        mips=u8(body, 0x17), flags=u16(body, 0x18), tile=u8(body, 0x1B))
    pos = 0x1C
    if tex["flags"] & 0x2000:
        pos += 0x100
    tex["data"] = body[pos: pos + tex["size"]]
    tex["cube"] = bool(tex["flags"] & 0x8000)
    tex["mips"] = max(1, tex["mips"])
    return tex


def split_mips(tex):
    if not tex["data"]:
        return []
    w, h = max(1, tex["width"]), max(1, tex["height"])
    if tex["cube"]:
        return [(w, h, tex["data"])]

    levels = tex["mips"]
    for _ in range(tex["highres"]):
        if levels <= 1:
            break
        w, h, levels = max(1, w // 2), max(1, h // 2), levels - 1

    out, offset = [], 0
    for _ in range(levels):
        if offset >= len(tex["data"]):
            break
        size = surface_size(tex["fmt"], w, h)
        if size <= 0:
            break
        if offset + size > len(tex["data"]):
            size = len(tex["data"]) - offset
        if size <= 0:
            break
        out.append((w, h, tex["data"][offset: offset + size]))
        offset += size
        w, h = max(1, w // 2), max(1, h // 2)
    return out or [(max(1, tex["width"]), max(1, tex["height"]), tex["data"])]


def write_dds(path, tex, mips):
    fmt = tex["fmt"]
    fourcc = {71: b"DXT1", 72: b"DXT1", 74: b"DXT3", 75: b"DXT3",
              77: b"DXT5", 78: b"DXT5"}.get(fmt)
    use_dx10 = fourcc is None

    pf_flags = 0x4
    pf_fourcc = fourcc if fourcc else b"DX10"

    flags = 0x1007 | (0x20000 if len(mips) > 1 else 0) | 0x80000
    caps = 0x1000 | (0x400008 if len(mips) > 1 else 0)

    header = struct.pack(
        "<4sIIIIIII44sIIIIIIIIIIIII",
        b"DDS ", 124, flags, tex["height"], tex["width"],
        len(mips[0][2]), 1, len(mips), b"\0" * 44,
        32, pf_flags, struct.unpack("<I", pf_fourcc)[0], 0, 0, 0, 0, 0,
        caps, 0, 0, 0, 0)

    with open(path, "wb") as fh:
        fh.write(header)
        if use_dx10:
            srgb_to_linear = {29: 28, 72: 71, 75: 74, 78: 77, 99: 98, 91: 87}
            fh.write(struct.pack("<IIIII", srgb_to_linear.get(fmt, fmt), 3, 0, 1, 0))
        for _, _, data in mips:
            fh.write(data)


# ── tr11modeldata (mirrors SotrMeshParser.cs) ─────────────────────────────────

ATTR_POSITION  = 0xD2F7D823
ATTR_NORMAL    = 0x36F5E414
ATTR_TEXCOORD1 = 0x8317902A

# TR11 vertex attribute class -> (component count, byte size, decoder key)
CLASS_TYPE = {
    0:  ("f", 1, 4),  1:  ("f", 2, 8),  2:  ("f", 3, 12), 3:  ("f", 4, 16),
    4:  ("un8", 4, 4), 5: ("un8", 4, 4), 6: ("u8", 4, 4), 7: ("u8", 4, 4), 8: ("u8", 4, 4),
    9:  ("s16", 2, 4), 10: ("s16", 4, 8), 11: ("u16", 4, 8), 12: ("u32", 4, 16),
    13: ("un8", 4, 4), 14: ("sn16", 2, 4), 15: ("sn16", 4, 8),
    16: ("un16", 2, 4), 17: ("un16", 4, 8),
    18: ("u1010102", 4, 4), 19: ("un1010102", 4, 4), 20: ("un1010102", 4, 4),
    21: ("un1010102", 4, 4),
    22: ("un8", 4, 4), 23: ("u8", 4, 4), 24: ("u16", 4, 8),
    25: ("sn16", 2, 4), 26: ("sn16", 4, 8),
}


def read_attr(data, o, kind, count):
    if kind == "f":
        return list(struct.unpack_from("<" + "f" * count, data, o))
    if kind == "un8":
        return [b / 255.0 for b in data[o:o + 4]]
    if kind == "u8":
        return list(data[o:o + 4])
    if kind == "s16":
        return list(struct.unpack_from("<" + "h" * count, data, o))
    if kind == "u16":
        return list(struct.unpack_from("<" + "H" * count, data, o))
    if kind == "u32":
        return list(struct.unpack_from("<" + "I" * count, data, o))
    if kind == "sn16":
        return [v / 32768.0 for v in struct.unpack_from("<" + "h" * count, data, o)]
    if kind == "un16":
        return [v / 65535.0 for v in struct.unpack_from("<" + "H" * count, data, o)]
    if kind in ("u1010102", "un1010102"):
        p = u32(data, o)
        vals = [p & 0x3FF, (p >> 10) & 0x3FF, (p >> 20) & 0x3FF, (p >> 30) & 0x3]
        if kind == "un1010102":
            vals = [vals[0] / 1023.0, vals[1] / 1023.0, vals[2] / 1023.0, vals[3] / 3.0]
        return vals
    return [0.0] * count


def parse_modeldata(body, verbose=False):
    """Returns a dict describing the model, or raises ValueError."""
    if len(body) < 0x160 or body[0:4] != b"Mesh":
        raise ValueError("not a tr11modeldata body (missing 'Mesh' signature)")

    m = dict(
        flags=u32(body, 0x004),
        num_indices=i32(body, 0x00C),
        model_type=i32(body, 0x070),
        mesh_parts_off=u64(body, 0x0F8),
        mesh_headers_off=u64(body, 0x100),
        bone_map_off=u64(body, 0x108),
        lod_levels_off=u64(body, 0x110),
        index_data_off=u64(body, 0x118),
        num_mesh_parts=u16(body, 0x120),
        num_meshes=u16(body, 0x122),
        num_bones=u16(body, 0x124),
        num_lod_levels=u16(body, 0x126),
        pre_tess=u64(body, 0x128),
        name_length=i32(body, 0x130),
        num_blend_shapes=i32(body, 0x140),
        blend_shape_names_off=u64(body, 0x148),
    )
    m["has_blend_shapes"] = bool(m["flags"] & 0x4000)
    m["skinned"] = bool(m["flags"] & 0x1) or m["model_type"] == 1
    m["size"] = len(body)

    # --- the central hypothesis: are the header pointers body-relative offsets? ---
    def plausible(off, need):
        return 0 < off and off + need <= len(body)

    m["offsets_plausible"] = (
        plausible(m["mesh_headers_off"], m["num_meshes"] * 0x60)
        and plausible(m["mesh_parts_off"], m["num_mesh_parts"] * 0x60)
        and plausible(m["index_data_off"], m["num_indices"] * 2)
    )

    # --- what a sequential walk would predict for meshHeaders, for comparison ---
    seq = 0x160 + m["name_length"]
    seq = (seq + 0x1F) & ~0x1F
    seq += m["num_lod_levels"] * 0x40
    seq += m["num_bones"] * 4
    seq = (seq + 0x1F) & ~0x1F
    m["sequential_mesh_headers"] = seq
    m["offsets_match_sequential"] = (seq == m["mesh_headers_off"])

    if not m["offsets_plausible"]:
        raise ValueError(
            f"header offsets not body-relative "
            f"(meshHeaders=0x{m['mesh_headers_off']:X}, size=0x{len(body):X})")

    # --- indices ---
    idx_off = m["index_data_off"]
    indices = struct.unpack_from("<" + "H" * m["num_indices"], body, idx_off)

    # --- meshes ---
    meshes = []
    for mi in range(m["num_meshes"]):
        h = m["mesh_headers_off"] + mi * 0x60
        num_parts = i32(body, h + 0x00)
        vb = [u64(body, h + 0x10), u64(body, h + 0x20)]
        fmt_size = i32(body, h + 0x30)
        fmt_off = u64(body, h + 0x38)
        num_verts = i32(body, h + 0x48)

        mesh = dict(num_parts=num_parts, num_verts=num_verts, positions=None, uvs=None,
                    attrs=[], ok=False, reason="")

        if not (0 < num_verts <= 4_000_000) or not plausible(fmt_off, 0x10):
            mesh["reason"] = "bad vertex count or format offset"
            meshes.append(mesh)
            continue

        num_attrs = u16(body, fmt_off + 0x08)
        strides = [u8(body, fmt_off + 0x0A), u8(body, fmt_off + 0x0B)]

        if not (0 < num_attrs <= 64) or not plausible(fmt_off, 0x10 + num_attrs * 8):
            mesh["reason"] = "bad attribute count"
            meshes.append(mesh)
            continue
        if fmt_size and fmt_size != 0x10 + num_attrs * 8:
            mesh["reason"] = f"vertexFormatSize {fmt_size} != {0x10 + num_attrs * 8}"
            meshes.append(mesh)
            continue

        positions = [(0.0, 0.0, 0.0)] * num_verts
        uvs = [(0.0, 0.0)] * num_verts
        got_pos = False

        for a in range(num_attrs):
            ab = fmt_off + 0x10 + a * 8
            name = u32(body, ab)
            attr_off = i16(body, ab + 4)
            cls = u8(body, ab + 6)
            buf_idx = u8(body, ab + 7)
            mesh["attrs"].append((name, cls, buf_idx, attr_off))

            if name not in (ATTR_POSITION, ATTR_TEXCOORD1):
                continue
            if buf_idx > 1 or strides[buf_idx] == 0:
                continue
            spec = CLASS_TYPE.get(cls)
            if spec is None:
                continue
            kind, count, tsize = spec

            base, stride = vb[buf_idx], strides[buf_idx]
            if not plausible(base, num_verts * stride):
                continue

            for v in range(num_verts):
                at = base + v * stride + attr_off
                if at + tsize > len(body):
                    break
                vals = read_attr(body, at, kind, count)
                if name == ATTR_POSITION:
                    positions[v] = (vals[0], vals[1] if count > 1 else 0.0,
                                    vals[2] if count > 2 else 0.0)
                else:
                    uvs[v] = (vals[0], vals[1] if count > 1 else 0.0)
            if name == ATTR_POSITION:
                got_pos = True

        mesh["positions"] = positions
        mesh["uvs"] = uvs
        mesh["ok"] = got_pos
        if not got_pos:
            mesh["reason"] = "no POSITION attribute decoded"
        meshes.append(mesh)

    # --- mesh parts, assigned to meshes in order ---
    parts = []
    mesh_idx, claimed = 0, 0
    for p in range(m["num_mesh_parts"]):
        while mesh_idx < m["num_meshes"] - 1 and claimed >= meshes[mesh_idx]["num_parts"]:
            mesh_idx += 1
            claimed = 0
        b = m["mesh_parts_off"] + p * 0x60
        part = dict(mesh=mesh_idx,
                    first_index=i32(body, b + 0x10),
                    num_prims=i32(body, b + 0x14),
                    lod=i16(body, b + 0x2C),
                    material=u64(body, b + 0x30))
        claimed += 1
        parts.append(part)

    m["indices"] = indices
    m["meshes"] = meshes
    m["parts"] = parts
    return m


def build_lods(m):
    """Merge mesh parts into LODs the way the C# does; returns {lod: (verts, uvs, tris)}."""
    lods = {}
    for part in m["parts"]:
        if part["num_prims"] <= 0:
            continue
        mesh = m["meshes"][part["mesh"]]
        if not mesh["ok"]:
            continue
        count = part["num_prims"] * 3
        if part["first_index"] < 0 or part["first_index"] + count > len(m["indices"]):
            continue
        span = m["indices"][part["first_index"]: part["first_index"] + count]
        if any(i >= mesh["num_verts"] for i in span):
            continue

        lod = max(0, part["lod"])
        entry = lods.setdefault(lod, dict(verts=[], uvs=[], tris=[], base={}))
        if part["mesh"] not in entry["base"]:
            entry["base"][part["mesh"]] = len(entry["verts"])
            entry["verts"].extend(mesh["positions"])
            entry["uvs"].extend(mesh["uvs"])
        base = entry["base"][part["mesh"]]
        for t in range(part["num_prims"]):
            entry["tris"].append((base + span[t * 3], base + span[t * 3 + 1],
                                  base + span[t * 3 + 2]))
    return lods


def write_obj(path, verts, uvs, tris):
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(f"# exported by tools/sotr_probe.py — {len(verts)} verts, {len(tris)} tris\n")
        for x, y, z in verts:
            fh.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
        for u, v in uvs:
            fh.write(f"vt {u:.6f} {1.0 - v:.6f}\n")
        for a, b, c in tris:
            fh.write(f"f {a+1}/{a+1} {b+1}/{b+1} {c+1}/{c+1}\n")


# ── hash list ─────────────────────────────────────────────────────────────────

def fnv1_64(s):
    h = 0xCBF29CE484222325
    for ch in s:
        h = ((h ^ ord(ch)) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


def load_hash_list(path):
    names = {}
    if not path or not os.path.isfile(path):
        return names
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\r\n")
            if line:
                names[fnv1_64(line)] = line
    return names


def resolve(names, h):
    raw = names.get(h)
    if raw is None:
        return None
    if raw.lower().startswith("pcx64-w\\"):
        raw = raw[len("pcx64-w\\"):]
    return raw.replace("\\", "/")


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Probe a Shadow of the Tomb Raider install.")
    ap.add_argument("game_dir")
    ap.add_argument("--extract", metavar="DIR")
    ap.add_argument("--collections", type=int, default=400)
    ap.add_argument("--samples", type=int, default=8)
    ap.add_argument("--hashlist")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    print("Shadow of the Tomb Raider — archive probe")
    print("=" * 60)
    print(f"Install: {args.game_dir}")

    if not os.path.isdir(args.game_dir):
        print(f"\nFATAL: not a directory: {args.game_dir}")
        return 2

    # ── 1. discovery ──────────────────────────────────────────────────────────
    section("1. Archive discovery")
    index_files = []
    for root, _dirs, files in os.walk(args.game_dir):
        for f in files:
            if f.lower().endswith(".000.tiger"):
                index_files.append(os.path.join(root, f))
    index_files.sort()

    if not index_files:
        record(FAIL, "found *.000.tiger index files",
               "none found — is this the install root?")
        print("\nNothing to probe. Point --game_dir at the folder containing bigfile.000.tiger.")
        return 2
    record(PASS, "found *.000.tiger index files", f"{len(index_files)}")
    for p in index_files:
        print(f"        {os.path.relpath(p, args.game_dir)}")

    # ── 2. TAFS headers + TOC ─────────────────────────────────────────────────
    section("2. TAFS v5 header + TOC")
    aset = TigerArchiveSet()
    total_entries = 0
    for path in index_files:
        a = TigerArchive(path)
        try:
            a.open()
        except Exception as exc:
            record(FAIL, f"open {os.path.basename(path)}", str(exc))
            continue
        aset.add(a)
        total_entries += len(a.entries)
        record(PASS, f"open {os.path.basename(path)}",
               f"v{a.version}, id {a.id}.{a.sub_id}, {a.num_parts} parts, "
               f"{a.num_files:,} files, platform '{a.platform}'")

    if not aset.archives:
        print("\nNo archive opened. Stopping.")
        return 2

    record(PASS, "total TOC entries", f"{total_entries:,}")

    # cross-check that entry offsets land inside their part files
    base = aset.archives[0]
    bad_offsets = 0
    checked = 0
    part_sizes = {}
    for e in base.entries[:20000]:
        p = e["part"]
        if p not in part_sizes:
            try:
                part_sizes[p] = os.path.getsize(base.part_path(p))
            except OSError:
                part_sizes[p] = -1
        size = part_sizes[p]
        if size < 0:
            continue
        checked += 1
        if e["offset"] >= size:
            bad_offsets += 1
    record(PASS if bad_offsets == 0 else FAIL,
           "TOC offsets land inside their part files",
           f"{checked:,} checked, {bad_offsets} out of range")

    missing_parts = [p for p, s in part_sizes.items() if s < 0]
    if missing_parts:
        record(WARN, "referenced part files present",
               f"missing parts: {sorted(missing_parts)[:8]}")
    else:
        record(PASS, "referenced part files present", f"{len(part_sizes)} parts touched")

    # archiveId routing sanity
    routed = Counter((e["aid"], e["asid"]) for e in base.entries)
    unroutable = sum(n for k, n in routed.items() if aset.find(*k) is None)
    record(PASS if unroutable == 0 else WARN,
           "every entry routes to a mounted archive",
           f"{len(routed)} distinct (archiveId,subId); {unroutable:,} entries unroutable")

    # ── 3. name dictionary ────────────────────────────────────────────────────
    section("3. Name dictionary (FNV-1 64)")
    here = os.path.dirname(os.path.abspath(__file__))
    hashlist = args.hashlist or os.path.join(
        here, "..", "src", "Engines", "SotrEngine", "Resources", "SOTR_PC_Release.list")
    names = load_hash_list(hashlist)
    if not names:
        record(WARN, "hash list loaded", f"not found at {hashlist} — names will be hashes")
    else:
        record(PASS, "hash list loaded", f"{len(names):,} paths")
        matched = sum(1 for e in base.entries if e["name_hash"] in names)
        pct = 100.0 * matched / max(1, len(base.entries))
        record(PASS if pct > 50 else FAIL,
               "TOC hashes resolve against the dictionary",
               f"{matched:,}/{len(base.entries):,} ({pct:.1f}%)")
        if pct <= 50:
            print("        A low percentage means the hash function or the path form is wrong.")

    # ── 4. CDRM ───────────────────────────────────────────────────────────────
    section("4. CDRM decompression")
    compressed = raw = failed = 0
    size_ok = size_bad = 0
    sample = base.entries[: max(200, args.samples * 20)]
    for e in sample:
        try:
            fh = base.part_file(e["part"])
        except OSError:
            continue
        try:
            if cdrm_has_magic(fh, e["offset"]):
                compressed += 1
                data = cdrm_decompress(fh, e["offset"])
                if len(data) == e["usize"]:
                    size_ok += 1
                else:
                    size_bad += 1
                    if args.verbose:
                        print(f"        size mismatch: got {len(data)} want {e['usize']}")
            else:
                raw += 1
        except Exception as exc:
            failed += 1
            if args.verbose:
                print(f"        decompress error: {exc}")

    record(PASS, "entries sampled", f"{len(sample)} ({compressed} CDRM, {raw} stored raw)")
    if compressed:
        record(PASS if failed == 0 else FAIL,
               "CDRM blobs inflate without error", f"{failed} failures")
        record(PASS if size_bad == 0 else FAIL,
               "inflated size matches the TOC's uncompressedSize",
               f"{size_ok} match, {size_bad} mismatch")
        print("        (this is the check that would have caught the 'MRDC' magic,")
        print("         the 16-byte header, chunk alignment and the zlib 2-byte skip)")
    else:
        record(WARN, "CDRM exercised", "no compressed entries in the sample")

    # ── 5. DRM collections ────────────────────────────────────────────────────
    section("5. DRM v23 ResourceCollections")
    drm_entries = []
    for a in aset.archives:
        for e in a.entries:
            n = names.get(e["name_hash"])
            if n is not None and n.lower().endswith(".drm"):
                drm_entries.append((e, a))
    if not drm_entries:                       # no dictionary: sniff instead
        for e in base.entries[:5000]:
            try:
                head = aset.read_entry(e, base, chunk_limit=1)
                if len(head) >= 4 and i32(head, 0) == 23:
                    drm_entries.append((e, base))
            except Exception:
                pass

    record(PASS, "collections identified", f"{len(drm_entries):,}")

    limit = len(drm_entries) if args.collections == 0 else min(args.collections, len(drm_entries))
    parsed = failed_parse = 0
    resources = {}
    type_hist = Counter()
    owner_of = {}

    for e, owner in drm_entries[:limit]:
        try:
            data = aset.read_entry(e, owner)
            coll = parse_drm(data)
        except Exception as exc:
            failed_parse += 1
            if args.verbose:
                print(f"        {exc}")
            continue
        if coll is None:
            failed_parse += 1
            continue
        parsed += 1
        for r in coll["resources"]:
            if not r["enabled"] or r["length"] == 0:
                continue
            if r["type"] in (0, 1):
                continue
            key = (r["type"], r["id"], r["locale"])
            if key not in resources:
                resources[key] = r
                owner_of[key] = e["name_hash"]
                type_hist[RESOURCE_TYPES.get(r["type"], r["type"])] += 1

    record(PASS if parsed else FAIL, "collections parsed",
           f"{parsed:,} ok, {failed_parse:,} failed (of {limit:,} tried)")
    record(PASS if resources else FAIL, "unique resources discovered", f"{len(resources):,}")
    for name, n in type_hist.most_common():
        print(f"        {name:<24} {n:,}")

    # ── 6. textures ───────────────────────────────────────────────────────────
    section("6. PCD9 textures")
    tex_res = [r for k, r in resources.items() if r["type"] == 5]
    record(PASS if tex_res else WARN, "texture resources found", f"{len(tex_res):,}")

    decoded = tex_failed = 0
    fmt_hist = Counter()
    mip_exact = mip_short = 0
    samples_written = 0
    outdir = args.extract
    if outdir:
        os.makedirs(outdir, exist_ok=True)

    for r in tex_res[: max(args.samples, 24)]:
        try:
            body = aset.read_resource_body(r)
            tex = parse_pcd9(body) if body else None
        except Exception as exc:
            tex_failed += 1
            if args.verbose:
                print(f"        {exc}")
            continue
        if tex is None:
            tex_failed += 1
            continue
        decoded += 1
        fmt_hist[DXGI_NAMES.get(tex["fmt"], f"DXGI:{tex['fmt']}")] += 1

        mips = split_mips(tex)
        consumed = sum(len(d) for _, _, d in mips)
        if consumed == len(tex["data"]):
            mip_exact += 1
        else:
            mip_short += 1
            if args.verbose:
                print(f"        mip chain consumed {consumed} of {len(tex['data'])} bytes "
                      f"({tex['width']}x{tex['height']} "
                      f"{DXGI_NAMES.get(tex['fmt'], tex['fmt'])} "
                      f"mips={tex['mips']} highres={tex['highres']})")

        if outdir and samples_written < args.samples:
            name = f"Texture_{r['id']}.dds"
            try:
                write_dds(os.path.join(outdir, name), tex, mips)
                samples_written += 1
            except Exception as exc:
                if args.verbose:
                    print(f"        dds write failed: {exc}")

    record(PASS if decoded else FAIL, "PCD9 headers parsed",
           f"{decoded} ok, {tex_failed} failed")
    if decoded:
        record(PASS if mip_short == 0 else WARN,
               "mip chain accounts for the whole payload",
               f"{mip_exact} exact, {mip_short} leftover/short")
        for name, n in fmt_hist.most_common():
            print(f"        {name:<10} {n}")
    if outdir and samples_written:
        record(PASS, "sample .dds written", f"{samples_written} in {outdir}")

    # ── 7. models ─────────────────────────────────────────────────────────────
    section("7. tr11modeldata geometry")
    model_res = [r for k, r in resources.items()
                 if r["type"] == 12 and r["subtype"] == SUBTYPE_MODELDATA]
    record(PASS if model_res else WARN, "modeldata resources found", f"{len(model_res):,}")

    ok = bad = 0
    offsets_ok = offsets_bad = seq_match = 0
    blend_shape_models = 0
    obj_written = 0
    for r in model_res[: max(args.samples, 16)]:
        try:
            body = aset.read_resource_body(r)
            if not body:
                bad += 1
                continue
            m = parse_modeldata(body, args.verbose)
        except ValueError as exc:
            bad += 1
            msg = str(exc)
            if "not body-relative" in msg:
                offsets_bad += 1
            if args.verbose:
                print(f"        Model_{r['id']}: {msg}")
            continue
        except Exception as exc:
            bad += 1
            if args.verbose:
                print(f"        Model_{r['id']}: {exc}")
            continue

        ok += 1
        offsets_ok += 1
        if m["offsets_match_sequential"]:
            seq_match += 1
        if m["has_blend_shapes"]:
            blend_shape_models += 1

        lods = build_lods(m)
        if args.verbose:
            decoded_meshes = sum(1 for x in m["meshes"] if x["ok"])
            print(f"        Model_{r['id']}: {m['num_meshes']} meshes "
                  f"({decoded_meshes} decoded), {m['num_mesh_parts']} parts, "
                  f"{m['num_bones']} bones, blendshapes={m['has_blend_shapes']}, "
                  f"LODs={sorted(lods)}")

        if outdir and lods and obj_written < args.samples:
            lod0 = lods[min(lods)]
            if lod0["verts"] and lod0["tris"]:
                try:
                    write_obj(os.path.join(outdir, f"Model_{r['id']}_lod{min(lods)}.obj"),
                              lod0["verts"], lod0["uvs"], lod0["tris"])
                    obj_written += 1
                except Exception as exc:
                    if args.verbose:
                        print(f"        obj write failed: {exc}")

    record(PASS if ok else FAIL, "models parsed", f"{ok} ok, {bad} failed")
    if ok or offsets_bad:
        total = ok + offsets_bad
        status = PASS if offsets_bad == 0 else FAIL
        record(status, "header pointers ARE body-relative offsets",
               f"{offsets_ok}/{total} models")
        print("        This is the central assumption in SotrMeshParser.cs. If it fails,")
        print("        the pointers are relocated by the refDefinitions block and the")
        print("        parser must walk sequentially instead.")
    if ok:
        record(PASS if seq_match == ok else WARN,
               "offset-driven and sequential agree on meshHeaders",
               f"{seq_match}/{ok} — mismatches are expected for blend-shape models")
        print(f"        models carrying blend shapes: {blend_shape_models}/{ok}")
    if outdir and obj_written:
        record(PASS, "sample .obj written", f"{obj_written} in {outdir}")

    # ── summary ───────────────────────────────────────────────────────────────
    section("Summary")
    counts = Counter(s for s, _, _ in _results)
    print(f"  {counts[PASS]} passed, {counts[WARN]} warnings, {counts[FAIL]} failed")
    if counts[FAIL]:
        print("\n  Failures, in the order worth fixing:")
        for status, name, detail in _results:
            if status == FAIL:
                print(f"    - {name}" + (f" — {detail}" if detail else ""))
        print("\n  Paste this whole report back to Claude and it will fix the C# to match.")
    else:
        print("\n  Every layer validated against real game data. The C# in")
        print("  src/Engines/SotrEngine/ mirrors this exactly, so it should behave the same.")

    aset.close()
    return 1 if counts[FAIL] else 0


if __name__ == "__main__":
    sys.exit(main())
