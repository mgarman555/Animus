#!/usr/bin/env python3
"""
nd_anim_probe.py — measure the animation payload inside Naughty Dog TLOU2 `anim-*.pak` files.

Why this exists
---------------
The .pak CONTAINER is fully understood (pages, pointer fixups, ResItems, strings), so listing
and naming animation resources is exact. The KEYFRAME payload is not: no public tool decodes
Naughty Dog clip data. The Noesis plugin this project ports from registers itself with
`-noanims` and contains no animation code at all, and the one closed-source tool that emits
anims documents its own output as unreliable.

So this script does not pretend to know the format. It measures the file and prints the
evidence needed to pin the format down:

  * every ResItem, with its type and name (clip names come out of this for free)
  * the raw header bytes of each ANIM / ANIM_GROUP / ANIM_STREAM resource, with each 8-byte
    slot annotated as a resolvable pointer / plausible float / small int
  * runs of unit-length float32 quaternions — an uncompressed rotation track is provably a run
    of unit quaternions that move smoothly, and neither property holds for unrelated bytes
  * the same test for 16-bit "smallest three" packings, the usual compressed alternative
  * a byte-entropy sketch per region, which separates packed bitstreams from plain tables

Run it against a real anim pak and paste the output back; the C# decoder in
`src/Engines/NaughtyDog/NdAnimParser.cs` is structured so that closing the gap is a matter of
adding the measured layout, not rewriting the pipeline.

Usage:
    python tools/nd_anim_probe.py <anim-something.pak> [more.pak ...]
    python tools/nd_anim_probe.py --dir <folder-with-anim-paks>
"""

import math
import os
import struct
import sys
from collections import Counter

ANIM_TYPES = {"ANIM", "ANIM_GROUP", "ANIM_STREAM"}
CINEMATIC_TYPES = {"CINEMATIC_1", "CIN_SEQUENCE_1", "CUTSCENE_DATA", "CAMERA_TABLE_1"}

MIN_RUN = 4            # samples; four consecutive unit quaternions by chance is negligible
UNIT_TOL = 2e-3
JOINT_MAJOR_MAX_STEP = 0.35   # radians; see NdAnimParser for the reasoning


def u16(d, o): return struct.unpack_from("<H", d, o)[0]
def u32(d, o): return struct.unpack_from("<I", d, o)[0]
def i32(d, o): return struct.unpack_from("<i", d, o)[0]
def u64(d, o): return struct.unpack_from("<Q", d, o)[0]
def f32(d, o): return struct.unpack_from("<f", d, o)[0]


def cstr(d, o, limit=200):
    if o < 0 or o >= len(d):
        return ""
    e = o
    while e < len(d) and d[e] != 0 and e - o < limit:
        e += 1
    try:
        return d[o:e].decode("ascii")
    except UnicodeDecodeError:
        return d[o:e].decode("latin-1", "replace")


class Pak:
    """Just enough of the container to walk resources and resolve pointers."""

    def __init__(self, data):
        self.d = data
        self.pages = []
        self.fixups = {}       # absolute address of a pointer field -> target page index
        self.items = []        # (type, name, pageIndex, pageStart, resItemOffset)
        self.is_tlou2 = False
        self.pad = 32

    def read(self):
        d = self.d
        if len(d) < 0x20:
            return False
        magic = u32(d, 0)
        if magic not in (2681, 2685, 68217, 68221, 2147486329):
            print(f"  magic {magic} (0x{magic:X}) is not a Naughty Dog pak")
            return False

        login_idx, login_off = u32(d, 8), u32(d, 0x0C)
        page_ct, page_tab = u32(d, 0x10), u32(d, 0x14)
        fixup_off = u32(d, 0x1C)

        for i in range(page_ct):
            o = page_tab + i * 12
            if o + 12 > len(d):
                break
            self.pages.append((u32(d, o), u32(d, o + 4), u32(d, o + 8)))

        fix_data, fix_ct = u32(d, fixup_off + 4), u32(d, fixup_off + 8)
        for i in range(fix_ct):
            o = fix_data + i * 8
            if o + 8 > len(d):
                break
            src, dst, poff = u16(d, o), u16(d, o + 2), u32(d, o + 4)
            if src < len(self.pages) and dst < len(self.pages):
                self.fixups[self.pages[src][0] + poff] = dst

        if login_idx < len(self.pages):
            ls = self.pages[login_idx][0] + login_off
            if ls + 36 <= len(d) and u32(d, ls + 32) == 74565:
                self.is_tlou2 = True
        if magic in (2685, 68221):
            self.is_tlou2 = True
        self.pad = 48 if self.is_tlou2 else 32

        for p, (fo, _sz, _fl) in enumerate(self.pages):
            if fo + 20 > len(d):
                continue
            n = u16(d, fo + 18)
            cur = fo + 20
            for _ in range(n):
                if cur + 16 > len(d):
                    break
                ri = u32(d, cur + 8)
                cur += 16
                item = fo + ri
                if item + 16 > len(d):
                    continue
                self.items.append((cstr(d, fo + u64(d, item + 8)),
                                   cstr(d, fo + u64(d, item)), p, fo, ri))
        return True

    def resolve(self, addr):
        """Absolute target of the pointer field at `addr`, or None."""
        if addr < 0 or addr + 8 > len(self.d):
            return None
        raw = u64(self.d, addr)
        page = self.fixups.get(addr)
        if page is None or page >= len(self.pages):
            return None
        target = raw + self.pages[page][0]
        return target if 0 <= target < len(self.d) else None


# ── measurements ─────────────────────────────────────────────────────────────

def quat_runs(d):
    """Maximal 4-byte-aligned runs of unit-length float4s. Returns (offset, count, meanStep)."""
    runs = []
    limit = len(d) - 16
    i = 0
    while i <= limit:
        if not _is_unit(d, i):
            i += 4
            continue
        start, count = i, 0
        while i <= limit and _is_unit(d, i):
            count += 1
            i += 16
        if count >= MIN_RUN:
            runs.append((start, count, _mean_step(d, start, count)))
    return runs


def _is_unit(d, o):
    try:
        x, y, z, w = struct.unpack_from("<4f", d, o)
    except struct.error:
        return False
    for v in (x, y, z, w):
        if v != v or v in (float("inf"), float("-inf")):
            return False
    return abs(x * x + y * y + z * z + w * w - 1.0) <= UNIT_TOL


def _mean_step(d, off, count):
    if count < 2:
        return 0.0
    total = 0.0
    prev = struct.unpack_from("<4f", d, off)
    for k in range(1, count):
        cur = struct.unpack_from("<4f", d, off + k * 16)
        dot = abs(sum(a * b for a, b in zip(prev, cur)))
        total += 2.0 * math.acos(min(1.0, dot))
        prev = cur
    return total / (count - 1)


def smallest_three_runs(d, bits=16):
    """
    Probe the classic compressed-quaternion packing: a 2-bit index naming the dropped
    (largest) component, then three signed fixed-point components. Reconstructed quaternions
    are unit by construction, so the discriminator here is TEMPORAL CONTINUITY only.
    """
    stride = 8 if bits == 16 else 4
    best = []
    limit = len(d) - stride * MIN_RUN
    step = stride
    o = 0
    while o < limit:
        vals = []
        ok = True
        for k in range(MIN_RUN * 2):
            q = _unpack_smallest_three(d, o + k * stride, bits)
            if q is None:
                ok = False
                break
            vals.append(q)
        if ok:
            steps = []
            for k in range(1, len(vals)):
                dot = abs(sum(a * b for a, b in zip(vals[k - 1], vals[k])))
                steps.append(2.0 * math.acos(min(1.0, dot)))
            mean = sum(steps) / len(steps)
            if mean < 0.05:          # far smoother than random data ever is
                best.append((o, len(vals), mean))
                o += stride * len(vals)
                continue
        o += step
    return best


def _unpack_smallest_three(d, o, bits):
    if o + 8 > len(d):
        return None
    raw = u64(d, o) if bits == 16 else u32(d, o)
    idx = raw & 0x3
    shift = 2
    comps = []
    mask = (1 << bits) - 1
    for _ in range(3):
        v = (raw >> shift) & mask
        shift += bits
        comps.append((v / mask) * 2.0 - 1.0)
    s = sum(c * c for c in comps)
    if s > 1.0:
        return None
    missing = math.sqrt(1.0 - s)
    q = comps[:]
    q.insert(idx, missing)
    return q


def sid64(name):
    """Naughty Dog StringId64 — FNV-1a-64 over raw ASCII.

    Verified 8/8 against the reference plugin's published type-string table
    (JOINT_HIERARCHY, GEOMETRY_1, VRAM_DESC, ANIM_GROUP, MATERIAL_TABLE_1,
    PAK_LOGIN_TABLE, TEXTURE_TABLE, SPAWNER_GROUP).
    """
    h = 0xCBF29CE484222325
    for b in name.encode("ascii", "ignore"):
        h = ((h ^ b) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


def bone_names_from_skel(path):
    """Pull joint names out of a *-skel.pak's JOINT_HIERARCHY name table."""
    d = open(path, "rb").read()
    pak = Pak(d)
    if not pak.read():
        return []
    joint = next((it for it in pak.items if it[0] == "JOINT_HIERARCHY"), None)
    if not joint:
        return []
    _t, _n, page_start, ri_off, _pi = joint
    base = page_start + ri_off + pak.pad + 0x14
    count = u32(d, base)
    if not (0 < count < 8192):
        return []
    names_ptr = pak.fixup(base + 36)
    if names_ptr is None:
        return []
    out = []
    for b in range(count):
        o = names_ptr + b * 16
        if o + 16 > len(d):
            break
        out.append(cstr(d, page_start + u64(d, o + 8)))
    return [n for n in out if n]


def joint_hash_table(d, bone_names):
    """Find the clip's joint table by matching StringId64 hashes of known bone names.

    This is the measurement that turns "which joint does this track drive?" from a guess
    into a lookup: the order the hashes appear IS the clip's track order.
    """
    if not bone_names:
        return None
    by_hash = {}
    for i, n in enumerate(bone_names):
        by_hash.setdefault(sid64(n), i)

    best = None          # (offset, stride, [bone indices])
    for stride in (8, 16, 4):
        off = 0
        while off + 8 <= len(d):
            if u64(d, off) not in by_hash:
                off += 4
                continue
            run, seen, at = [], set(), off
            while at + 8 <= len(d):
                idx = by_hash.get(u64(d, at))
                # A joint table names each joint once; a repeat means the run has left the
                # table and wandered into something that happens to hash.
                if idx is None or idx in seen:
                    break
                seen.add(idx)
                run.append(idx)
                at += stride
            if best is None or len(run) > len(best[2]):
                best = (off, stride, run)
            off = max(at, off + 4)

    # Two or three coincidental hits are noise, not a table. Matches NdAnimJointMap's
    # MinRunLength so the probe and the C# decoder agree on what counts as a find.
    return best if best is not None and len(best[2]) >= 6 else None


def entropy(chunk):
    if not chunk:
        return 0.0
    counts = Counter(chunk)
    n = len(chunk)
    return -sum((c / n) * math.log2(c / n) for c in counts.values())


# ── reporting ────────────────────────────────────────────────────────────────

def annotate_header(pak, base, count=24):
    """Print each 8-byte slot of a resource header with a best-guess interpretation."""
    d = pak.d
    for k in range(count):
        o = base + k * 8
        if o + 8 > len(d):
            break
        raw = u64(d, o)
        lo, hi = u32(d, o), u32(d, o + 4)
        notes = []
        tgt = pak.resolve(o)
        if tgt is not None:
            notes.append(f"ptr→0x{tgt:X}")
            s = cstr(d, tgt, 48)
            if s and all(32 <= ord(c) < 127 for c in s[:8]):
                notes.append(f"str='{s[:48]}'")
        for nm, v in (("lo", lo), ("hi", hi)):
            fv = f32(d, o + (0 if nm == "lo" else 4))
            if fv == fv and abs(fv) > 1e-6 and abs(fv) < 1e6:
                notes.append(f"{nm}f={fv:.4g}")
            if 0 < v < 100000:
                notes.append(f"{nm}={v}")
        print(f"      +0x{k*8:03X}  {raw:016X}  {'  '.join(notes[:5])}")


def probe(path):
    d = open(path, "rb").read()
    print(f"\n=== {os.path.basename(path)}  ({len(d):,} bytes) ===")

    pak = Pak(d)
    if not pak.read():
        return

    print(f"  game={'TLOU2' if pak.is_tlou2 else 'U4/TLL'}  pages={len(pak.pages)}  "
          f"fixups={len(pak.fixups)}  resItems={len(pak.items)}")

    types = Counter(t for t, *_ in pak.items)
    print(f"  types: {', '.join(f'{v}×{k}' for k, v in types.most_common(20))}")

    if BONE_NAMES:
        hit = joint_hash_table(d, BONE_NAMES)
        if hit:
            off, stride, order = hit
            print(f"  JOINT TABLE: {len(order)} joint-name hashes at 0x{off:X} (stride {stride})")
            named = [BONE_NAMES[i] for i in order]
            print(f"    track order: {', '.join(named[:16])}"
                  + (f" … (+{len(named)-16} more)" if len(named) > 16 else ""))
            print("    ^ this is the clip's track -> joint mapping; feed it to NdAnimJointMap")
        else:
            print(f"  JOINT TABLE: none of the {len(BONE_NAMES)} bone-name hashes appear in this pak")
            print("    (try a different -skel.pak, or the joint table may be indices not hashes)")

    anims = [it for it in pak.items if it[0] in ANIM_TYPES]
    cines = [it for it in pak.items if it[0] in CINEMATIC_TYPES]
    print(f"  animation resources={len(anims)}  cinematic resources={len(cines)}")

    for i, (itype, name, pidx, pstart, rioff) in enumerate(anims[:8]):
        base = pstart + rioff + pak.pad
        print(f"\n  --- [{i}] {itype}  name='{name}'  page={pidx} riOff=0x{rioff:X} "
              f"payload=0x{base:X} ---")
        annotate_header(pak, base, 20)

    if len(anims) > 8:
        print(f"\n  (+{len(anims) - 8} more animation resources; names:)")
        for itype, name, *_ in anims[8:64]:
            print(f"      {itype}: {name}")

    print("\n  --- uncompressed rotation tracks (unit float4 runs) ---")
    runs = quat_runs(d)
    if not runs:
        print("      none — the clip payload is quantised, not plain float quaternions")
    else:
        by_len = Counter(c for _o, c, _s in runs)
        print(f"      {len(runs)} run(s); longest={max(c for _o, c, _s in runs)}")
        print(f"      run-length histogram (top 8): {by_len.most_common(8)}")
        dom_len, dom_ct = by_len.most_common(1)[0]
        group = [r for r in runs if r[1] == dom_len]
        mean = sum(s for _o, _c, s in group) / len(group)
        layout = "joint-major (run = one joint over time)" if mean <= JOINT_MAJOR_MAX_STEP \
                 else "frame-major (run = one frame across joints)"
        print(f"      dominant: {dom_ct} run(s) of {dom_len}; mean angular step "
              f"{mean:.5f} rad → {layout}")
        for o, c, st in runs[:6]:
            print(f"        @0x{o:08X}  {c} samples  meanStep={st:.5f}")

    print("\n  --- compressed rotation tracks (smallest-three probe) ---")
    for bits in (16,):
        hits = smallest_three_runs(d, bits)
        if hits:
            print(f"      {bits}-bit: {len(hits)} candidate run(s); first few:")
            for o, c, st in hits[:6]:
                print(f"        @0x{o:08X}  {c} samples  meanStep={st:.5f}")
        else:
            print(f"      {bits}-bit: no continuous runs found")

    print("\n  --- byte entropy by page (8 = incompressible/packed, <6 = tables/strings) ---")
    for p, (fo, sz, _fl) in enumerate(pak.pages[:12]):
        chunk = d[fo:fo + min(sz, 1 << 16)]
        print(f"      page {p:2d} @0x{fo:08X} size={sz:,} entropy={entropy(chunk):.2f}")

    if pak.pages:
        last = pak.pages[-1]
        boundary = last[0] + last[1]
        print(f"\n  after-pages boundary=0x{boundary:X}  trailing bytes={len(d) - boundary:,}")


BONE_NAMES = []


def main():
    global BONE_NAMES
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1

    # --skel <path>: load a skeleton's joint names so the probe can match their
    # StringId64 hashes inside the anim pak and recover the track -> joint mapping.
    if "--skel" in args:
        i = args.index("--skel")
        if i + 1 >= len(args):
            print("--skel needs a path to a *-skel.pak")
            return 1
        skel_path = args[i + 1]
        del args[i:i + 2]
        try:
            BONE_NAMES = bone_names_from_skel(skel_path)
            print(f"loaded {len(BONE_NAMES)} joint names from {os.path.basename(skel_path)}")
        except Exception as e:
            print(f"could not read {skel_path}: {type(e).__name__}: {e}")

    if not args:
        print(__doc__)
        return 1

    targets = []
    if args[0] == "--dir":
        if len(args) < 2:
            print("--dir needs a folder")
            return 1
        for root, _dirs, files in os.walk(args[1]):
            for f in files:
                if f.lower().startswith("anim-") and f.lower().endswith(".pak"):
                    targets.append(os.path.join(root, f))
        targets.sort()
        print(f"found {len(targets)} anim pak(s) under {args[1]}")
        targets = targets[:8]
    else:
        targets = args

    for t in targets:
        try:
            probe(t)
        except Exception as e:      # a probe must never stop the batch
            print(f"  ERROR {t}: {type(e).__name__}: {e}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
