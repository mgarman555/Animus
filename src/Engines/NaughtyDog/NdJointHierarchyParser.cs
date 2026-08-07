using System.Numerics;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Parses a TLOU2 <c>JOINT_HIERARCHY</c> ResItem into <see cref="SkeletonData"/> so the viewer's
/// Armature toggle/export and the metadata sidecar get real bones.
///
/// Why structure-discovery instead of hard-coded offsets: the rest of the ND engine is a faithful
/// port of <c>fmt_nd_pak.py</c>, but that script's joint-hierarchy section isn't reproduced here and
/// the documented Ellie character paks carry no joints (<c>JOINT_HIERARCHY</c> lives in base-skeleton
/// paks). Rather than ship guessed byte offsets, this locates the three parallel arrays a ND joint
/// hierarchy always exposes — <b>names</b> (fixup pointers to ASCII), <b>parent indices</b> (int32
/// tree), and <b>bind transforms</b> (4×4 float matrices) — by their signatures, then accepts the
/// result only if names resolve to joint-like ASCII AND the parents form a valid acyclic tree of the
/// same length. Anything short of that returns null, so a wrong guess never becomes a bogus skeleton.
/// This is the same "probe candidates, validate, log" pattern used by <see cref="GeoContext"/>'s
/// m_papTransform reader and the viewer's texture auto-detect.
///
/// Verification note: this has not been run against a real <c>joint=True</c> pak in this environment
/// (Linux, no .NET, and the on-hand Ellie paks are joint-less). The rich Log output is the
/// diagnostic — run the app against a base-skeleton pak on Windows and confirm the discovered
/// name/parent/transform arrays before trusting exported bind poses.
/// </summary>
public static class NdJointHierarchyParser
{
    // Search window past the resource header where the parallel arrays' pointers live.
    private const int HeaderSpan = 1024;
    private const int MinJoints  = 2;
    private const int MaxJoints  = 4096;

    public static SkeletonData? TryParse(NdPakReader r, string label)
    {
        if (r.JointEntry is not { } je) return null;

        try
        {
            var data = r.Data;
            int baseOff = je.PageStart + je.ResItemOffset + r.ResItemPaddingSz;
            if (baseOff < 0 || baseOff + 32 > data.Length)
            {
                Log.Warn($"NdJoint[{label}]: JOINT_HIERARCHY resource base OOB (0x{baseOff:X})");
                return null;
            }

            // ── 1. Name table: an array of consecutive u64 fixup pointers → ASCII joint names ──
            var names = FindNameTable(r, baseOff, out int nameArrayAddr);
            if (names == null)
            {
                Log.Info($"NdJoint[{label}]: no joint-name pointer array found near 0x{baseOff:X} — skeleton not parsed");
                return null;
            }
            int n = names.Count;
            Log.Info($"NdJoint[{label}]: name table @0x{nameArrayAddr:X} → {n} joints  (e.g. {string.Join(", ", names.Take(4))}{(n > 4 ? ", …" : "")})");

            // ── 2. Parent indices: an int32[n] run that forms a valid tree ──────────────────────
            int[]? parents = FindParentArray(r, baseOff, n, out int parentArrayAddr);
            if (parents == null)
            {
                Log.Info($"NdJoint[{label}]: name table found but no valid int32 parent tree of length {n} — skeleton rejected");
                return null;
            }
            Log.Info($"NdJoint[{label}]: parent tree @0x{parentArrayAddr:X} validated ({parents.Count(p => p < 0)} root(s))");

            // ── 3. Bind transforms (optional): 4×4 float matrices → per-joint position/rotation ──
            var (positions, rotations, scales, xformAddr) = FindTransforms(r, baseOff, n);
            if (xformAddr != 0)
                Log.Info($"NdJoint[{label}]: bind transforms @0x{xformAddr:X} decoded for {n} joints");
            else
                Log.Info($"NdJoint[{label}]: no bind-transform array found — bones placed at origin (hierarchy only)");

            // ── Build ───────────────────────────────────────────────────────────────────────────
            var skel = new SkeletonData();
            for (int i = 0; i < n; i++)
            {
                skel.Bones.Add(new BoneInfo
                {
                    Name        = names[i],
                    ParentIndex = parents[i],
                    Position    = positions?[i] ?? new float[3],
                    Rotation    = rotations?[i] ?? new float[] { 0, 0, 0, 1 },
                    Scale       = scales?[i]    ?? new float[] { 1, 1, 1 },
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

    // ── Name table discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// A ND joint hierarchy references its joint names through an array of u64 fixup pointers, each
    /// resolving to an ASCII string. Scan every fixup whose source sits in the header window and,
    /// for each, walk consecutive 8-byte slots as long as they stay fixups pointing at joint-like
    /// ASCII. The longest such run is the name table.
    /// </summary>
    private static List<string>? FindNameTable(NdPakReader r, int baseOff, out int arrayAddr)
    {
        arrayAddr = 0;
        List<string>? best = null;

        foreach (int slot0 in FixupSourcesIn(r, baseOff, baseOff + HeaderSpan))
        {
            // slot0 is a header pointer; follow it to a candidate array base, then read the array.
            var arrBase = r.ReadPointerFixup(slot0);
            if (arrBase is null) continue;
            var run = ReadNamePointerRun(r, (int)arrBase.Value);
            if (run.Count >= MinJoints && run.Count <= MaxJoints && (best == null || run.Count > best.Count))
            {
                best = run;
                arrayAddr = (int)arrBase.Value;
            }
        }

        return best;
    }

    /// <summary>Read consecutive u64 fixup slots at <paramref name="addr"/> while each resolves to a joint-like ASCII string.</summary>
    private static List<string> ReadNamePointerRun(NdPakReader r, int addr)
    {
        var names = new List<string>();
        for (int k = 0; ; k++)
        {
            int slot = addr + k * 8;
            if (slot + 8 > r.Data.Length) break;
            if (!r.PointerFixups.ContainsKey(slot)) break;      // array ended
            var target = r.ReadPointerFixup(slot);
            if (target is null) break;
            string s = ReadString(r.Data, (int)target.Value, 64);
            if (!IsJointName(s)) break;
            names.Add(s);
            if (names.Count > MaxJoints) break;
        }
        return names;
    }

    // ── Parent array discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Find an int32[n] run that is all in [-1, n) and describes an acyclic tree with at least one
    /// root. ND arrays usually live behind a fixup pointer, so probe every header-pointer target
    /// first, then fall back to an inline scan of the header window. Returns the parents or null.
    /// </summary>
    private static int[]? FindParentArray(NdPakReader r, int baseOff, int n, out int arrayAddr)
    {
        arrayAddr = 0;

        // Pointer-referenced arrays (the common ND layout)
        foreach (int slot in FixupSourcesIn(r, baseOff, baseOff + HeaderSpan))
        {
            var target = r.ReadPointerFixup(slot);
            if (target is null) continue;
            if (TryReadParentTree(r.Data, (int)target.Value, n, out var parents))
            {
                arrayAddr = (int)target.Value;
                return parents;
            }
        }

        // Inline fallback: some counts/indices sit directly in the header window
        int scanEnd = Math.Min(r.Data.Length - n * 4, baseOff + HeaderSpan);
        for (int addr = baseOff; addr <= scanEnd; addr += 4)
        {
            if (TryReadParentTree(r.Data, addr, n, out var parents))
            {
                arrayAddr = addr;
                return parents;
            }
        }
        return null;
    }

    private static bool TryReadParentTree(byte[] data, int addr, int n, out int[] parents)
    {
        parents = new int[n];
        if (addr < 0 || addr + n * 4 > data.Length) return false;
        bool hasRoot = false, hasChild = false;
        for (int i = 0; i < n; i++)
        {
            int p = BitConverter.ToInt32(data, addr + i * 4);
            if (p < -1 || p >= n || p == i) return false;
            if (p < 0) hasRoot = true; else hasChild = true;
            parents[i] = p;
        }
        // A real skeleton has root(s) AND parented bones — reject an all-flat run (every value -1),
        // which is a common coincidental match on zero/padding regions.
        return hasRoot && hasChild && IsAcyclicTree(parents);
    }

    private static bool IsAcyclicTree(int[] parents)
    {
        for (int i = 0; i < parents.Length; i++)
        {
            int steps = 0, cur = i;
            while (cur >= 0)
            {
                cur = parents[cur];
                if (++steps > parents.Length) return false; // cycle
            }
        }
        return true;
    }

    // ── Transform discovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Locate an array of n 4×4 float matrices (64 bytes each) referenced from the header and
    /// decode per-joint translation (reliable) plus rotation/scale when the 3×3 basis is
    /// near-orthonormal. Returns zeroed arrays' address 0 if nothing convincing is found.
    /// </summary>
    private static (float[][]? pos, float[][]? rot, float[][]? scale, int addr) FindTransforms(
        NdPakReader r, int baseOff, int n)
    {
        foreach (int slot in FixupSourcesIn(r, baseOff, baseOff + HeaderSpan))
        {
            var arrBase = r.ReadPointerFixup(slot);
            if (arrBase is null) continue;
            int a = (int)arrBase.Value;
            if (a < 0 || a + n * 64 > r.Data.Length) continue;
            if (!AllFinite(r.Data, a, n * 16)) continue;

            var pos   = new float[n][];
            var rot   = new float[n][];
            var scale = new float[n][];
            bool sane = true;
            for (int i = 0; i < n; i++)
            {
                var m = ReadMatrix(r.Data, a + i * 64);
                // Row-major affine: translation in the last row (m41..m43).
                pos[i] = new[] { m[12], m[13], m[14] };
                DecomposeBasis(m, out rot[i], out scale[i]);
                if (Math.Abs(pos[i][0]) > 1e6 || Math.Abs(pos[i][1]) > 1e6 || Math.Abs(pos[i][2]) > 1e6)
                    { sane = false; break; }
            }
            if (sane) return (pos, rot, scale, a);
        }
        return (null, null, null, 0);
    }

    private static float[] ReadMatrix(byte[] d, int o)
    {
        var m = new float[16];
        for (int i = 0; i < 16; i++) m[i] = BitConverter.ToSingle(d, o + i * 4);
        return m;
    }

    /// <summary>
    /// Decompose the upper 3×3 into scale + rotation quaternion when it is near-orthonormal after
    /// scale removal; otherwise fall back to identity rotation / unit scale so a questionable basis
    /// never emits a bogus quaternion into exports.
    /// </summary>
    private static void DecomposeBasis(float[] m, out float[] rot, out float[] scale)
    {
        rot   = new float[] { 0, 0, 0, 1 };
        scale = new float[] { 1, 1, 1 };

        var c0 = new Vector3(m[0], m[1], m[2]);
        var c1 = new Vector3(m[4], m[5], m[6]);
        var c2 = new Vector3(m[8], m[9], m[10]);
        float s0 = c0.Length(), s1 = c1.Length(), s2 = c2.Length();
        if (s0 < 1e-6f || s1 < 1e-6f || s2 < 1e-6f) return;

        var mat = new Matrix4x4(
            c0.X / s0, c0.Y / s0, c0.Z / s0, 0,
            c1.X / s1, c1.Y / s1, c1.Z / s1, 0,
            c2.X / s2, c2.Y / s2, c2.Z / s2, 0,
            0, 0, 0, 1);

        // Orthonormality check: rows should be mutually perpendicular unit vectors.
        var r0 = new Vector3(mat.M11, mat.M12, mat.M13);
        var r1 = new Vector3(mat.M21, mat.M22, mat.M23);
        var r2 = new Vector3(mat.M31, mat.M32, mat.M33);
        if (Math.Abs(Vector3.Dot(r0, r1)) > 0.02f ||
            Math.Abs(Vector3.Dot(r0, r2)) > 0.02f ||
            Math.Abs(Vector3.Dot(r1, r2)) > 0.02f)
            return; // not a clean rotation — keep identity

        var q = Quaternion.CreateFromRotationMatrix(mat);
        rot   = new[] { q.X, q.Y, q.Z, q.W };
        scale = new[] { s0, s1, s2 };
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    /// <summary>Fixup source addresses within [lo, hi), ascending — the header's pointer fields.</summary>
    private static IEnumerable<int> FixupSourcesIn(NdPakReader r, int lo, int hi) =>
        r.PointerFixups.Keys.Where(a => a >= lo && a < hi).OrderBy(a => a);

    private static bool AllFinite(byte[] d, int floatOff, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float f = BitConverter.ToSingle(d, floatOff + i * 4);
            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
        }
        return true;
    }

    private static string ReadString(byte[] d, int o, int max)
    {
        if (o < 0 || o >= d.Length) return string.Empty;
        int end = o;
        while (end < d.Length && end - o < max && d[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(d, o, end - o);
    }

    /// <summary>
    /// Joint names in ND paks look like "root", "spine_a", "l_hand", "headb" — lowercase-ish ASCII,
    /// letters plus digits/underscores/colons, no spaces or control bytes. This is the discriminator
    /// that keeps the name-table scan from locking onto random pointer arrays.
    /// </summary>
    private static bool IsJointName(string s)
    {
        if (s.Length < 2 || s.Length > 63) return false;
        bool hasLetter = false;
        foreach (char c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
            if (c is (>= '0' and <= '9') or '_' or '-' or ':' or '.') continue;
            return false; // space, punctuation, or control byte → not a joint name
        }
        return hasLetter;
    }
}
