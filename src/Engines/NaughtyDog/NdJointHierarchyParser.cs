using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Parses a TLOU2 <c>JOINT_HIERARCHY</c> ResItem into <see cref="SkeletonData"/> so the viewer's
/// Armature toggle/export and the metadata sidecar get real bones.
///
/// Ported from alphaZomega's <c>fmt_nd_pak.py</c> and the <c>nd_pak.bt</c> 010-Editor template
/// (github.com/alphazolam) — the same references the rest of this engine follows. Layout, from the
/// resource data base (<c>pageStart + resItemOffset + ResItemPaddingSz</c>, i.e. the geometry base
/// formula):
///   +0  version, +16 numJSegments, <b>+20 nodeCount</b>, +24 boneCount1, +28 boneCount2,
///   <b>+32 matsOffset</b> (ptr → transforms), +40 skeletonFlipData, +48 jsInfo,
///   <b>+56 namesOffset</b> (ptr → names), +64 riggingGroupsNames, +72 riggingGroups, …
///   • names   : nodeCount × 16 bytes = { u64 hash, u64 ptr→ASCII } (pointer at +8)
///   • transforms: 48 bytes each = { float scale[4], float rotation[4] (quat xyzw), float pos[4] } — pos is LOCAL
///   • parents : boneParentInfo 16 bytes each = { i32 groupID, i32 parentID, i32 childID, i32 chainID }
///
/// The header field offsets and the array strides above are the .bt template verbatim. The two
/// sub-offsets the references leave fuzzy — where the transform array begins inside the matsOffset
/// block (there's a small sub-header) and where the parent array sits — are resolved by signature
/// (unit-length quaternions for transforms; a valid 16-byte-stride parent tree). The skeleton is
/// accepted only if names resolve to joint-like ASCII AND parents form a valid acyclic tree of
/// length nodeCount, so a bad read returns null instead of a bogus skeleton.
///
/// NOT yet run against a real joint=True pak here (Linux/no-.NET; the on-hand Ellie paks are
/// joint-less — joints live in sibling "-base.pak" files). The rich NdJoint[…] Log is the
/// diagnostic; eyeball the armature on Windows before trusting exported bind poses.
/// </summary>
public static class NdJointHierarchyParser
{
    // Header field offsets from the resource data base (nd_pak.bt _JOINT_HIERARCHY).
    private const int OffNodeCount   = 20;
    private const int OffMatsPtr     = 32;
    private const int OffNamesPtr    = 56;

    private const int NameStride     = 16;   // { u64 hash, u64 ptr }
    private const int NamePtrOff     = 8;    // pointer sits after the hash
    private const int XformStride    = 48;   // { scale[4], quat[4], pos[4] }
    private const int ParentStride   = 16;   // { groupID, parentID, childID, chainID }
    private const int ParentIdOff    = 4;    // parentID is the 2nd int32

    private const int MaxJoints      = 4096;
    private const int ScanSpan       = 4096;

    public static SkeletonData? TryParse(NdPakReader r, string label)
    {
        if (r.JointEntry is not { } je) return null;

        try
        {
            var d = r.Data;
            int baseOff = je.PageStart + je.ResItemOffset + r.ResItemPaddingSz;
            if (baseOff < 0 || baseOff + 64 > d.Length)
            {
                Log.Warn($"NdJoint[{label}]: resource base OOB (0x{baseOff:X})");
                return null;
            }

            int n = (int)R32(d, baseOff + OffNodeCount);
            if (n < 1 || n > MaxJoints)
            {
                Log.Warn($"NdJoint[{label}]: implausible nodeCount={n} at 0x{baseOff + OffNodeCount:X}");
                return null;
            }

            // ── Names (nodeCount × { u64 hash, u64 ptr→ASCII }) ─────────────────────────────────
            var namesPtr = r.ReadPointerFixup(baseOff + OffNamesPtr);
            var names = namesPtr is { } np ? ReadNames(r, (int)np, n) : null;
            if (names == null)
            {
                Log.Info($"NdJoint[{label}]: names array (namesOffset @0x{baseOff + OffNamesPtr:X}) didn't resolve to {n} joint-like strings — skeleton rejected");
                return null;
            }
            Log.Info($"NdJoint[{label}]: {n} joints — names e.g. {string.Join(", ", names.Take(5))}{(n > 5 ? ", …" : "")}");

            // ── Parents (16-byte boneParentInfo; parentID @ +4) ─────────────────────────────────
            var parents = FindParents(r, baseOff, n, out int parentsAddr);
            if (parents == null)
            {
                Log.Info($"NdJoint[{label}]: no valid parent tree (length {n}) found — skeleton rejected");
                return null;
            }
            Log.Info($"NdJoint[{label}]: parent tree @0x{parentsAddr:X} ({parents.Count(p => p < 0)} root(s))");

            // ── Transforms (48-byte scale/quat/pos; LOCAL) ──────────────────────────────────────
            var xforms = ReadTransforms(r, baseOff, n, out int xformAddr);
            if (xformAddr != 0)
                Log.Info($"NdJoint[{label}]: local transforms @0x{xformAddr:X} decoded");
            else
                Log.Info($"NdJoint[{label}]: transform array not located — bones placed at origin (hierarchy only)");

            var skel = new SkeletonData();
            for (int i = 0; i < n; i++)
            {
                var (pos, rot, scale) = xforms?[i] ?? (new float[3], new float[] { 0, 0, 0, 1 }, new float[] { 1, 1, 1 });
                skel.Bones.Add(new BoneInfo
                {
                    Name        = names[i],
                    ParentIndex = parents[i],
                    Position    = pos,
                    Rotation    = rot,
                    Scale       = scale,
                });
            }
            Log.Info($"NdJoint[{label}]: skeleton built — {skel.Bones.Count} bones");
            return skel;
        }
        catch (Exception ex)
        {
            Log.Warn($"NdJoint[{label}]: parse threw — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── Names ─────────────────────────────────────────────────────────────────────

    private static List<string>? ReadNames(NdPakReader r, int arr, int n)
    {
        if (arr < 0 || arr + n * NameStride > r.Data.Length) return null;
        var names = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            var p = r.ReadPointerFixup(arr + i * NameStride + NamePtrOff);
            if (p is null) return null;
            string s = ReadString(r.Data, (int)p.Value, 64);
            if (!IsJointName(s)) return null;
            names.Add(s);
        }
        return names;
    }

    // ── Parents ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the 16-byte-stride boneParentInfo array (parentID @ +4) of length n whose parentID
    /// column forms a valid acyclic tree. Probe pointer-referenced targets from the header first
    /// (the arrays live in their own pages), then fall back to an inline scan of the header window.
    /// </summary>
    private static int[]? FindParents(NdPakReader r, int baseOff, int n, out int addr)
    {
        addr = 0;
        foreach (int slot in r.PointerFixups.Keys.Where(a => a >= baseOff && a < baseOff + ScanSpan).OrderBy(a => a))
        {
            var t = r.ReadPointerFixup(slot);
            if (t is { } tv && TryParentTree(r.Data, (int)tv, n, out var p)) { addr = (int)tv; return p; }
        }
        int scanEnd = Math.Min(r.Data.Length - n * ParentStride, baseOff + ScanSpan);
        for (int a = baseOff; a <= scanEnd; a += 4)
            if (TryParentTree(r.Data, a, n, out var p)) { addr = a; return p; }
        return null;
    }

    private static bool TryParentTree(byte[] d, int addr, int n, out int[] parents)
    {
        parents = new int[n];
        if (addr < 0 || (long)addr + (long)n * ParentStride > d.Length) return false;
        bool hasRoot = false, hasChild = false;
        for (int i = 0; i < n; i++)
        {
            int p = (int)R32(d, addr + i * ParentStride + ParentIdOff);
            if (p < -1 || p >= n || p == i) return false;
            if (p < 0) hasRoot = true; else hasChild = true;
            parents[i] = p;
        }
        return hasRoot && hasChild && IsAcyclicTree(parents);
    }

    private static bool IsAcyclicTree(int[] parents)
    {
        for (int i = 0; i < parents.Length; i++)
        {
            int steps = 0, cur = i;
            while (cur >= 0) { cur = parents[cur]; if (++steps > parents.Length) return false; }
        }
        return true;
    }

    // ── Transforms ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read n 48-byte transforms (scale[4], quat[4] xyzw, pos[4]) from the matsOffset block. The
    /// block has a small sub-header before the array, so the start is found by scanning a few
    /// offsets for the first where all n quaternions are unit-length (the transform signature).
    /// Positions are local-space, matching the viewer's parent-chain walk.
    /// </summary>
    private static (float[] pos, float[] rot, float[] scale)[]? ReadTransforms(
        NdPakReader r, int baseOff, int n, out int addr)
    {
        addr = 0;
        var matsPtr = r.ReadPointerFixup(baseOff + OffMatsPtr);
        if (matsPtr is null) return null;
        int mats = (int)matsPtr.Value;

        // Try successive start offsets inside the mats block (sub-header is small); accept the first
        // where every quaternion is near unit-length — a strong, self-validating signature.
        for (int hdr = 0; hdr <= 64; hdr += 4)
        {
            int start = mats + hdr;
            if (start < 0 || (long)start + (long)n * XformStride > r.Data.Length) break;
            if (!QuaternionsUnit(r.Data, start, n)) continue;

            var res = new (float[], float[], float[])[n];
            for (int i = 0; i < n; i++)
            {
                int o = start + i * XformStride;
                res[i] = (
                    new[] { F(r.Data, o + 32), F(r.Data, o + 36), F(r.Data, o + 40) },       // pos
                    new[] { F(r.Data, o + 16), F(r.Data, o + 20), F(r.Data, o + 24), F(r.Data, o + 28) }, // quat xyzw
                    new[] { F(r.Data, o + 0),  F(r.Data, o + 4),  F(r.Data, o + 8) });        // scale
            }
            addr = start;
            return res;
        }
        return null;
    }

    private static bool QuaternionsUnit(byte[] d, int start, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int o = start + i * XformStride + 16;
            float x = F(d, o), y = F(d, o + 4), z = F(d, o + 8), w = F(d, o + 12);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(w)) return false;
            double len = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
            if (len < 0.97 || len > 1.03) return false;
        }
        return true;
    }

    // ── Primitives ────────────────────────────────────────────────────────────────

    private static uint  R32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
    private static float F(byte[] d, int o)   => BitConverter.ToSingle(d, o);

    private static string ReadString(byte[] d, int o, int max)
    {
        if (o < 0 || o >= d.Length) return string.Empty;
        int end = o;
        while (end < d.Length && end - o < max && d[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(d, o, end - o);
    }

    /// <summary>
    /// ND joint names read like "root", "spine_a", "l_hand", "headb" — ASCII letters plus
    /// digits/underscore/hyphen/colon/dot, no spaces or control bytes. The discriminator that keeps
    /// the parse from accepting a mis-resolved names pointer.
    /// </summary>
    private static bool IsJointName(string s)
    {
        if (s.Length < 1 || s.Length > 63) return false;
        bool hasLetter = false;
        foreach (char c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
            if (c is (>= '0' and <= '9') or '_' or '-' or ':' or '.') continue;
            return false;
        }
        return hasLetter;
    }
}
