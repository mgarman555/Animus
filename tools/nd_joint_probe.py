"""
nd_joint_probe.py — dump + validate a Naughty Dog JOINT_HIERARCHY against a REAL pak.

This is the ground-truth verifier for NdJointHierarchyParser.cs. Point it at a joint=True
pak (a "-base.pak", where nd_pak_probe.py reports JOINT=True) and it will:
  1. resolve the pak header / pages / pointer-fixup table (same as NdPakReader),
  2. locate JOINT_HIERARCHY and dump its header region as a u32 grid + resolved pointers,
  3. apply the ported layout (nodeCount@+20, matsOffset@+32, namesOffset@+56) and read the
     names / parents / transforms arrays, printing samples so the offsets can be confirmed.

Layout reference: alphaZomega fmt_nd_pak.py + nd_pak.bt (github.com/alphazolam).
  names      : nodeCount x 16B { u64 hash, u64 ptr->ASCII } (pointer @ +8)
  transforms : 48B { float scale[4], float quat[4] xyzw, float pos[4] }  (pos = LOCAL)
  parents    : 16B boneParentInfo { i32 groupID, i32 parentID, i32 childID, i32 chainID }

Usage:  python3 tools/nd_joint_probe.py <path-to-base.pak> [more.pak ...]
"""
import struct, sys, os, math

def u16(d, o): return struct.unpack_from("<H", d, o)[0]
def u32(d, o): return struct.unpack_from("<I", d, o)[0]
def i32(d, o): return struct.unpack_from("<i", d, o)[0]
def u64(d, o): return struct.unpack_from("<Q", d, o)[0]
def f32(d, o): return struct.unpack_from("<f", d, o)[0]

def cstr(d, o, m=64):
    if o < 0 or o >= len(d): return ""
    e = o
    while e < len(d) and e - o < m and d[e] != 0: e += 1
    try: return d[o:e].decode("ascii")
    except: return d[o:e].decode("latin-1", "replace")

def is_joint_name(s):
    if not (1 <= len(s) <= 63): return False
    ok = all(c.isalnum() or c in "_-:." for c in s)
    return ok and any(c.isalpha() for c in s)

def build(path):
    d = open(path, "rb").read()
    print(f"\n=== {os.path.basename(path)}  ({len(d):,} bytes) ===")
    magic = u32(d, 0)
    loginIdx = u32(d, 8); loginOff = u32(d, 0x0C)
    pageCt = u32(d, 0x10); pPageTab = u32(d, 0x14); fixupOff = u32(d, 0x1C)
    pages = [(u32(d, pPageTab + i*12), u32(d, pPageTab + i*12 + 4), u32(d, pPageTab + i*12 + 8))
             for i in range(pageCt)]
    game = "Unknown"
    if loginIdx < len(pages):
        ls = pages[loginIdx][0] + loginOff
        if ls + 36 <= len(d) and u32(d, ls + 32) == 74565: game = "TLOU2"
        elif magic in (2685, 68221): game = "TLOUP1"
        else: game = "U4/TLL"
    pad = 48 if game in ("TLOU2", "TLOUP1") else 32
    print(f"  magic=0x{magic:X} game={game} pages={len(pages)} resItemPad={pad}")

    # pointer-fixup table: source abs addr -> target page index
    fixDataOff = u32(d, fixupOff + 4); numFix = u32(d, fixupOff + 8)
    fixup = {}
    for i in range(numFix):
        o = fixDataOff + i*8
        p1 = u16(d, o); p2 = u16(d, o + 2); po = u32(d, o + 4)
        if p1 < len(pages) and p2 < len(pages):
            fixup[po + pages[p1][0]] = p2
    print(f"  fixups={len(fixup)}")

    def rptr(addr):
        """Resolve a u64 pointer at addr via the fixup table -> absolute offset, or None."""
        if addr + 8 > len(d): return None
        val = u64(d, addr)
        if addr in fixup and fixup[addr] < len(pages):
            return val + pages[fixup[addr]][0]
        return None

    # find JOINT_HIERARCHY
    joint = None
    for p, (fo, sz, fl) in enumerate(pages):
        if fo + 20 > len(d): continue
        numPH = u16(d, fo + 18); cur = fo + 20
        for _ in range(numPH):
            if cur + 16 > len(d): break
            rio = u32(d, cur + 8); cur += 16
            ia = rio + fo
            if ia + 16 > len(d): continue
            itype = cstr(d, fo + u64(d, ia + 8))
            if itype == "JOINT_HIERARCHY":
                joint = (p, rio, fo); break
        if joint: break
    if not joint:
        print("  JOINT_HIERARCHY: none in this pak (character/clothing pak? joints live in -base.pak)")
        return

    p, rio, fo = joint
    base = fo + rio + pad
    print(f"  JOINT_HIERARCHY @ page {p}  base=0x{base:X}")

    # header u32 grid
    print("  header u32 grid (offset: value  /hex):")
    for off in range(0, 96, 4):
        if base + off + 4 > len(d): break
        v = u32(d, base + off)
        tag = {20: " <- nodeCount?", 24: " boneCount1?", 28: " boneCount2?",
               32: " matsOffset(ptr)", 56: " namesOffset(ptr)"}.get(off, "")
        print(f"    +{off:<3} {v:<12} 0x{v:08X}{tag}")

    # resolved header pointers
    print("  resolved header pointers:")
    for off, name in [(32, "matsOffset"), (40, "skeletonFlipData"), (48, "jsInfo"),
                      (56, "namesOffset"), (64, "riggingGroupsNames"), (72, "riggingGroups")]:
        r = rptr(base + off)
        print(f"    +{off:<3} {name:<20} -> {'0x%X' % r if r else 'None'}")

    n = u32(d, base + 20)
    print(f"  nodeCount(+20) = {n}")
    if not (1 <= n <= 4096):
        print("  !! nodeCount out of range — offsets may differ for this game/version; see grid above")
        return

    # names
    namesPtr = rptr(base + 56)
    names = []
    if namesPtr:
        for i in range(n):
            e = namesPtr + i*16
            pn = rptr(e + 8)
            names.append(cstr(d, pn) if pn else "<no-ptr>")
    good = sum(1 for s in names if is_joint_name(s))
    print(f"  names @ {'0x%X' % namesPtr if namesPtr else 'None'}: {good}/{n} joint-like")
    print(f"    first: {names[:8]}")
    if n > 8: print(f"    last:  {names[-4:]}")

    # transforms: scan a few start offsets in the mats block for unit quaternions
    matsPtr = rptr(base + 32)
    xstart = None
    if matsPtr:
        for hdr in range(0, 68, 4):
            s = matsPtr + hdr
            if s + n*48 > len(d): break
            okq = True
            for i in range(n):
                o = s + i*48 + 16
                q = (f32(d,o), f32(d,o+4), f32(d,o+8), f32(d,o+12))
                if any(math.isnan(x) for x in q): okq = False; break
                L = math.sqrt(sum(x*x for x in q))
                if not (0.97 <= L <= 1.03): okq = False; break
            if okq: xstart = s; break
    if xstart is not None:
        print(f"  transforms @ 0x{xstart:X} (mats+{xstart-matsPtr}) — unit quats OK, stride 48")
        for i in range(min(n, 5)):
            o = xstart + i*48
            sc = (f32(d,o), f32(d,o+4), f32(d,o+8))
            q  = (f32(d,o+16), f32(d,o+20), f32(d,o+24), f32(d,o+28))
            po = (f32(d,o+32), f32(d,o+36), f32(d,o+40))
            nm = names[i] if i < len(names) else "?"
            print(f"    [{i}] {nm:<20} pos=({po[0]:+.3f},{po[1]:+.3f},{po[2]:+.3f}) "
                  f"quat=({q[0]:+.3f},{q[1]:+.3f},{q[2]:+.3f},{q[3]:+.3f}) scale=({sc[0]:.3f},{sc[1]:.3f},{sc[2]:.3f})")
    else:
        print(f"  transforms: no 48B-stride unit-quat run found in mats block (matsPtr={'0x%X'%matsPtr if matsPtr else None})")

    # parents: probe pointer targets + inline for a 16B-stride parentID tree
    def try_tree(addr):
        if addr < 0 or addr + n*16 > len(d): return None
        par = []; root = child = False
        for i in range(n):
            pv = i32(d, addr + i*16 + 4)
            if pv < -1 or pv >= n or pv == i: return None
            root |= pv < 0; child |= pv >= 0
            par.append(pv)
        if not (root and child): return None
        for i in range(n):  # acyclic
            steps = 0; c = i
            while c >= 0:
                c = par[c]; steps += 1
                if steps > n: return None
        return par
    paddr, parents = None, None
    for slot in sorted(a for a in fixup if base <= a < base + 4096):
        t = rptr(slot)
        if t is not None:
            par = try_tree(t)
            if par: paddr, parents = t, par; break
    if parents is None:
        a = base
        while a <= min(len(d) - n*16, base + 4096):
            par = try_tree(a)
            if par: paddr, parents = a, par; break
            a += 4
    if parents is not None:
        roots = sum(1 for x in parents if x < 0)
        print(f"  parents @ 0x{paddr:X}: valid tree, {roots} root(s)")
        print(f"    first parentIDs: {parents[:12]}")
    else:
        print("  parents: no valid 16B-stride parentID tree found near header")

    ok = (namesPtr and good == n and parents is not None)
    print(f"  RESULT: {'PARSE OK — skeleton would be accepted' if ok else 'incomplete — see dump above'}")

for t in (sys.argv[1:] or []):
    try: build(t)
    except Exception as e:
        import traceback; print(f"  ERROR {t}: {e}"); traceback.print_exc()
if not sys.argv[1:]:
    print(__doc__)
