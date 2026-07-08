#!/usr/bin/env python3
"""
fbx_probe.py — independent reader/dumper for FBX 7.x BINARY files.

Purpose: verify the C# FbxBinaryWriter output against a from-scratch implementation of the FBX
binary spec (https://code.blender.org/2013/08/fbx-binary-file-format-specification/), and confirm
we parse genuine Blender/SDK-exported FBX the same way. It is deliberately dependency-free
(stdlib only) so it runs anywhere Python does.

Usage:
    python tools/fbx_probe.py path/to/file.fbx            # dump the node tree (truncated arrays)
    python tools/fbx_probe.py path/to/file.fbx --full     # don't truncate array previews
    python tools/fbx_probe.py path/to/file.fbx --check    # structural sanity checks, exit code

The dump prints each node's name, property type codes, and child count so the tree can be eyeballed
against what the exporter intended (Geometry/Model/Material/Deformer/AnimationCurve, Connections...).
"""
import struct
import sys
import zlib

MAGIC = b"Kaydara FBX Binary  "


class Node:
    __slots__ = ("name", "props", "prop_codes", "children")

    def __init__(self, name):
        self.name = name
        self.props = []
        self.prop_codes = []
        self.children = []


def _read_header(f):
    magic = f.read(23)
    if magic[:20] != MAGIC:
        raise ValueError("Not an FBX binary file (bad magic)")
    (version,) = struct.unpack("<I", f.read(4))
    return version


def _read_property(f):
    code = f.read(1).decode("ascii")
    if code == "Y":
        return code, struct.unpack("<h", f.read(2))[0]
    if code == "C":
        return code, struct.unpack("<b", f.read(1))[0] != 0
    if code == "I":
        return code, struct.unpack("<i", f.read(4))[0]
    if code == "F":
        return code, struct.unpack("<f", f.read(4))[0]
    if code == "D":
        return code, struct.unpack("<d", f.read(8))[0]
    if code == "L":
        return code, struct.unpack("<q", f.read(8))[0]
    if code == "S" or code == "R":
        (length,) = struct.unpack("<I", f.read(4))
        data = f.read(length)
        if code == "S":
            return code, data.decode("utf-8", "replace")
        return code, data
    if code in ("f", "d", "i", "l", "b"):
        return code, _read_array(f, code)
    raise ValueError(f"Unknown property type code {code!r}")


def _read_array(f, code):
    count, encoding, comp_len = struct.unpack("<III", f.read(12))
    payload = f.read(comp_len)
    raw = zlib.decompress(payload) if encoding == 1 else payload
    fmt = {"f": "f", "d": "d", "i": "i", "l": "q", "b": "b"}[code]
    return list(struct.unpack(f"<{count}{fmt}", raw))


def _read_node(f, wide):
    hdr = f.read(8 if not wide else 24)  # placeholder; re-read precisely below
    f.seek(-len(hdr), 1)
    if wide:
        end_offset, num_props, prop_len = struct.unpack("<QQQ", f.read(24))
    else:
        end_offset, num_props, prop_len = struct.unpack("<III", f.read(12))
    name_len = f.read(1)[0]

    if end_offset == 0 and num_props == 0 and prop_len == 0 and name_len == 0:
        return None  # null terminator

    name = f.read(name_len).decode("ascii", "replace")
    node = Node(name)
    for _ in range(num_props):
        code, val = _read_property(f)
        node.prop_codes.append(code)
        node.props.append(val)

    while f.tell() < end_offset:
        child = _read_node(f, wide)
        if child is None:
            break
        node.children.append(child)

    f.seek(end_offset)
    return node


def parse(path):
    with open(path, "rb") as f:
        version = _read_header(f)
        wide = version >= 7500
        nodes = []
        while True:
            node = _read_node(f, wide)
            if node is None:
                break
            nodes.append(node)
    return version, nodes


def _fmt_prop(code, val, full):
    if isinstance(val, list):
        preview = val if full or len(val) <= 6 else val[:6] + ["..."]
        return f"[{code}×{len(val)}] {preview}"
    if isinstance(val, bytes):
        return f"[{code}] <{len(val)} bytes>"
    return f"[{code}] {val!r}"


def dump(nodes, full=False, depth=0):
    for n in nodes:
        props = ", ".join(_fmt_prop(c, v, full) for c, v in zip(n.prop_codes, n.props))
        print("  " * depth + f"{n.name}: {props}" + (f"  ({len(n.children)} children)" if n.children else ""))
        dump(n.children, full, depth + 1)


def find(nodes, name):
    out = []
    for n in nodes:
        if n.name == name:
            out.append(n)
        out.extend(find(n.children, name))
    return out


def check(version, nodes):
    """Minimal structural sanity for a mesh export; returns (ok, messages)."""
    msgs = [f"FBX version {version}"]
    ok = True
    tops = {n.name for n in nodes}
    for req in ("FBXHeaderExtension", "GlobalSettings", "Definitions", "Objects", "Connections"):
        present = req in tops
        msgs.append(f"  top-level {req}: {'ok' if present else 'MISSING'}")
        ok &= present or req in ("Definitions",)  # Definitions optional but expected

    geos = find(nodes, "Geometry")
    conns = find(nodes, "Connections")
    msgs.append(f"  Geometry nodes: {len(geos)}")
    for g in geos:
        verts = [c for c in g.children if c.name == "Vertices"]
        idx = [c for c in g.children if c.name == "PolygonVertexIndex"]
        nv = len(verts[0].props[0]) // 3 if verts and verts[0].props else 0
        ni = len(idx[0].props[0]) if idx and idx[0].props else 0
        msgs.append(f"    Geometry: {nv} verts, {ni} polygon-vertex indices")
        if nv == 0 or ni == 0:
            ok = False
    if conns:
        total = sum(len(c.children) for c in conns)
        msgs.append(f"  Connections: {total}")
    return ok, msgs


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    path = argv[1]
    full = "--full" in argv
    do_check = "--check" in argv
    version, nodes = parse(path)
    if do_check:
        ok, msgs = check(version, nodes)
        print("\n".join(msgs))
        print("RESULT:", "OK" if ok else "PROBLEMS FOUND")
        return 0 if ok else 1
    print(f"FBX version {version}, {len(nodes)} top-level nodes")
    dump(nodes, full)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
