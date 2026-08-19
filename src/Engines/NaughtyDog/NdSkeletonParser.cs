using System.Numerics;
using GameAssetExplorer.Core.Animation;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Parses the <c>JOINT_HIERARCHY</c> resource of a Naughty Dog .pak into a
/// <see cref="SkeletonData"/> — bone names, parent links and the parent-local bind pose.
///
/// Navigation is a port of fmt_nd_pak.py's <c>PakFile.readPak</c> joint block, cross-checked
/// against the <c>nd_pak.bt</c> 010 template for the real field names.
///
/// The resource payload starts at <c>payloadStart = pageStart + resItemOffset +
/// ResItemPaddingSz</c> (48 on TLOU2), same as every other resource — the <c>+20</c> below is
/// simply the field offset of the joint count within it, not a header the resource prepends:
///
///   payloadStart+0x00  u32   version
///   payloadStart+0x10  u32   numJSegments
///   payloadStart+0x14  u32   nodeCount       ← what the reference calls boneCount
///   payloadStart+0x18  u32   boneCount1      (skipped)
///   payloadStart+0x1C  u32   boneCount2      (skipped)
///   payloadStart+0x20  ptr   matsOffset      → the transform table
///   payloadStart+0x28  ptr   skeletonFlipDataOffset  (a real pointer; never dereferenced here)
///   payloadStart+0x30  ptr   jsInfoOffset            (ditto)
///   payloadStart+0x38  ptr   namesOffset
///
/// so with jointBase = payloadStart + 0x14 the fields below read at +0, +12, +20, +28, +36.
///
///   xformsOffset+16  u16 nodeCount   +18 u16 xformCount   +20 u16 uknCount
///   xformsOffset+32  u32 headerSize                        +60 u32 hierarchyOffset
///   xformsOffset+40  u32 uknFloatsOffs → nodeCount × 3×4 float matrices — a SECOND matrix
///                    table this parser does not read. Worth probing if the bind pose ever
///                    disagrees with the mesh.
///
///   transforms  @ xformsOffset + headerSize, stride 48:
///                 +0  float3 scale  (+4 pad)
///                 +16 float4 rotation quaternion
///                 +32 float3 position (+4 pad)
///   parenting   @ xformsOffset + hierarchyOffset + hashesSize, stride 16:
///                 int GroupID, int ParentID, int ChildID, int ChainID
///                 (hashesSize = u32 at xformsOffset + hierarchyOffset + 20)
///   names       @ namesOffset, stride 16: [u64 hash][u64 nameOffset relative to pageStart]
///
/// Only bones whose ancestor chain terminates at bone 0 ("the boneMap") get an entry in the
/// transform table; helper/straggler joints borrow their chain's transform so that bone
/// INDICES stay dense and aligned with the skin table's 10-bit indices.
///
/// Two things the reference leaves ambiguous are resolved by measurement rather than
/// assumption — see <see cref="QuaternionConvention"/> and <see cref="Score"/>.
/// </summary>
public static class NdSkeletonParser
{
    private const int XFORM_STRIDE  = 48;
    private const int PARENT_STRIDE = 16;
    private const int NAME_STRIDE   = 16;

    /// <summary>
    /// Which way the stored bind quaternion has to be read.
    ///
    /// fmt_nd_pak.py builds the bone matrix from <c>NoeQuat(...).transpose()</c>. Noesis's
    /// quaternion→matrix helper works in row-vector convention, so whether that
    /// <c>.transpose()</c> cancels out or not depends on an internal we cannot read from the
    /// Python source. Rather than guess, the parser builds the skeleton BOTH ways and keeps
    /// whichever one actually agrees with the data (see <see cref="Score"/>).
    /// </summary>
    public enum QuaternionConvention
    {
        /// <summary>Use the quaternion exactly as stored.</summary>
        AsStored,
        /// <summary>Conjugate it (negate x, y, z) — the literal reading of the reference.</summary>
        Conjugated,
    }

    /// <summary>How well a parsed skeleton agrees with the mesh it is supposed to deform.</summary>
    public readonly record struct Score(
        double MeanInfluenceDistance,   // lower is better; NaN when no skin data was supplied
        double ExtentDiagonal,          // world bbox diagonal of the joints
        double MeshExtentDiagonal,      // world bbox diagonal of the mesh, or 0
        bool   HasSkinEvidence)
    {
        /// <summary>
        /// Single comparable number — smaller wins. With skin data this is the mean distance
        /// from a vertex to the weighted centre of the joints that drive it, which is a direct
        /// physical measurement and beats any heuristic. Without it we fall back to
        /// "the skeleton should be roughly the size of the mesh, not orders of magnitude off".
        /// </summary>
        public double Value => HasSkinEvidence
            ? MeanInfluenceDistance
            : (MeshExtentDiagonal > 1e-6 ? Math.Abs(ExtentDiagonal - MeshExtentDiagonal) : ExtentDiagonal);

        public override string ToString() => HasSkinEvidence
            ? $"meanInfluenceDist={MeanInfluenceDistance:F4} extent={ExtentDiagonal:F3}"
            : $"extent={ExtentDiagonal:F3} (meshExtent={MeshExtentDiagonal:F3})";
    }

    /// <summary>
    /// Parse the pak's joint hierarchy. When <paramref name="mesh"/> is supplied and already
    /// carries skin weights, both quaternion conventions are tried and the one that best
    /// explains the mesh is returned; otherwise <see cref="QuaternionConvention.Conjugated"/>
    /// (the literal reading of the reference) is used.
    /// </summary>
    public static SkeletonData? TryParse(NdPakReader reader, string label, MeshAssetData? mesh = null)
    {
        try
        {
            var conjugated = ParseWith(reader, label, QuaternionConvention.Conjugated);
            if (conjugated == null) return null;

            var asStored = ParseWith(reader, label, QuaternionConvention.AsStored);
            if (asStored == null) return conjugated;

            var scoreConj  = Evaluate(conjugated, mesh);
            var scoreStore = Evaluate(asStored,   mesh);

            // Conjugated is the reading proven by the reference's own import/export round-trip,
            // so it wins ties; the probe only overrides it when the mesh's own weights say
            // otherwise, which would mean this build stores quaternions the other way round.
            bool useStored = scoreStore.Value < scoreConj.Value;
            Log.Info($"NdSkeletonParser[{label}]: bind-quaternion probe — " +
                     $"conjugated {{{scoreConj}}} vs as-stored {{{scoreStore}}} → " +
                     $"using {(useStored ? "as-stored (probe overrode the reference default)" : "conjugated")}" +
                     (scoreConj.HasSkinEvidence ? " (measured against skin weights)" : " (heuristic — no skin data)"));

            return useStored ? asStored : conjugated;
        }
        catch (Exception ex)
        {
            Log.Warn($"NdSkeletonParser[{label}]: failed — {ex.Message}");
            return null;
        }
    }

    // ── Core walk ────────────────────────────────────────────────────────────

    private static SkeletonData? ParseWith(NdPakReader reader, string label, QuaternionConvention conv)
    {
        if (reader.JointEntry is not { } joint) return null;

        var data      = reader.Data;
        int pageStart = joint.PageStart;
        int jointBase = pageStart + joint.ResItemOffset + 20 + reader.ResItemPaddingSz;

        if (jointBase + 44 > data.Length) return null;

        int boneCount = (int)R32(data, jointBase);
        if (boneCount <= 0 || boneCount > 8192)
        {
            Log.Warn($"NdSkeletonParser[{label}]: implausible boneCount {boneCount}");
            return null;
        }

        var xformsPtr = reader.ReadPointerFixup(jointBase + 12);
        var namesPtr  = reader.ReadPointerFixup(jointBase + 36);
        if (xformsPtr is not > 0 || namesPtr is not > 0)
        {
            Log.Warn($"NdSkeletonParser[{label}]: joint pointers unresolved " +
                     $"(xforms={xformsPtr?.ToString() ?? "null"}, names={namesPtr?.ToString() ?? "null"})");
            return null;
        }

        int xforms = (int)xformsPtr.Value;
        int names  = (int)namesPtr.Value;
        if (xforms + 64 > data.Length) return null;

        int xformCount      = R16(data, xforms + 18);
        int headerSize      = (int)R32(data, xforms + 32);
        int hierarchyOffset = (int)R32(data, xforms + 60);

        if (xformCount <= 0 || xformCount > boneCount) xformCount = Math.Min(boneCount, Math.Max(xformCount, 0));
        if (headerSize <= 0 || xforms + headerSize > data.Length) return null;

        // ── Transform table (parent-local scale / rotation / translation) ────
        int transformsStart = xforms + headerSize;
        var localTrs = new (Vector3 S, Quaternion R, Vector3 T)[xformCount];
        int nonUnitScales = 0;

        for (int i = 0; i < xformCount; i++)
        {
            int o = transformsStart + i * XFORM_STRIDE;
            if (o + XFORM_STRIDE > data.Length) { xformCount = i; Array.Resize(ref localTrs, i); break; }

            var s = new Vector3(F32(data, o), F32(data, o + 4), F32(data, o + 8));
            var q = new Quaternion(F32(data, o + 16), F32(data, o + 20), F32(data, o + 24), F32(data, o + 28));
            var t = new Vector3(F32(data, o + 32), F32(data, o + 36), F32(data, o + 40));

            if (conv == QuaternionConvention.Conjugated) q = Quaternion.Conjugate(q);
            q = q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

            // The reference reads bind scale and then throws it away, and its geometry output is
            // known-good, so scale is kept here only when it is a sane, usable value. Anything
            // degenerate or wild is dropped to identity rather than allowed to wreck the pose.
            bool scaleUsable = float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z)
                            && s.X > 1e-4f && s.Y > 1e-4f && s.Z > 1e-4f
                            && s.X < 100f  && s.Y < 100f  && s.Z < 100f;
            if (!scaleUsable) s = Vector3.One;
            else if (MathF.Abs(s.X - 1) > 1e-3f || MathF.Abs(s.Y - 1) > 1e-3f || MathF.Abs(s.Z - 1) > 1e-3f)
                nonUnitScales++;

            localTrs[i] = (s, q, t);
        }

        // ── Parenting table ──────────────────────────────────────────────────
        // hashesSize is the size of the joint-name-hash block that sits between the
        // hierarchy header and the parent quadruples.
        int hierBase = xforms + hierarchyOffset;
        if (hierBase + 24 > data.Length) return null;
        int hashesSize    = (int)R32(data, hierBase + 20);
        int parentingStart = hierBase + hashesSize;

        if (parentingStart < 0 || parentingStart + boneCount * PARENT_STRIDE > data.Length)
        {
            Log.Warn($"NdSkeletonParser[{label}]: parenting table out of range " +
                     $"(start=0x{parentingStart:X}, need {boneCount * PARENT_STRIDE} bytes)");
            return null;
        }

        var parents = new (int Group, int Parent, int Child, int Chain)[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            int o = parentingStart + b * PARENT_STRIDE;
            parents[b] = (I32(data, o), I32(data, o + 4), I32(data, o + 8), I32(data, o + 12));
        }

        // ── Name table ───────────────────────────────────────────────────────
        var boneNames = new string[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            int o = names + b * NAME_STRIDE;
            if (o + NAME_STRIDE > data.Length) { boneNames[b] = $"bone_{b}"; continue; }
            long nameOff = BitConverter.ToInt64(data, o + 8);
            int abs = pageStart + (int)nameOff;
            string n = ReadString(data, abs);
            boneNames[b] = string.IsNullOrEmpty(n) ? $"bone_{b}" : n;
        }

        // ── boneMap: which bones own a row of the transform table ────────────
        // A bone qualifies when walking its parent chain lands on bone 0.
        var boneMap = new List<int>(boneCount);
        for (int b = 0; b < boneCount; b++)
            if (RootOf(parents, b) == 0) boneMap.Add(b);

        var mapPosition = new Dictionary<int, int>(boneMap.Count);
        for (int k = 0; k < boneMap.Count; k++) mapPosition[boneMap[k]] = k;

        // ── Assemble ─────────────────────────────────────────────────────────
        var skeleton = new SkeletonData { SourceName = label };
        int headbIndex = Array.IndexOf(boneNames, "headb");

        for (int b = 0; b < boneCount; b++)
        {
            var bone = new BoneInfo
            {
                Name        = boneNames[b],
                ParentIndex = parents[b].Parent,
                GroupIndex  = parents[b].Group,
                ChildIndex  = parents[b].Child,
                ChainIndex  = parents[b].Chain,
            };

            if (mapPosition.TryGetValue(b, out int row) && row < localTrs.Length)
            {
                Apply(bone, localTrs[row]);
            }
            else
            {
                // Helper / straggler joint: no row of its own. Borrow the transform of the
                // chain it belongs to so it sits somewhere sane instead of at the origin.
                bone.HasBindTransform = false;
                // Index the transform array by the chain bone's RANK in boneMap. (The Noesis
                // reference indexes it by the raw global joint id here, which reads past the
                // end of the array for any chain id beyond the boneMap length.)
                int chain = parents[b].Chain;
                if (chain >= 0 && mapPosition.TryGetValue(chain, out int chainRow) && chainRow < localTrs.Length)
                    Apply(bone, localTrs[chainRow]);
                else
                    Apply(bone, (Vector3.One, Quaternion.Identity, Vector3.Zero));

                // Unparented helpers would otherwise become extra roots and drift away from
                // the character. Eyelash groups belong on the head; everything else on root.
                if (bone.ParentIndex == -1)
                    bone.ParentIndex = (bone.Name == "eyelash_grp" && headbIndex >= 0) ? headbIndex : 0;
            }

            if (bone.ParentIndex == b) bone.ParentIndex = -1;   // self-parent guard
            if (bone.ParentIndex >= boneCount) bone.ParentIndex = -1;

            skeleton.Bones.Add(bone);
        }

        if (conv == QuaternionConvention.Conjugated)
            Log.Info($"NdSkeletonParser[{label}]: {boneCount} bones, {xformCount} transforms, " +
                     $"{boneMap.Count} in boneMap, {boneCount - boneMap.Count} helper joints" +
                     (nonUnitScales > 0 ? $", {nonUnitScales} non-unit bind scales" : ""));

        return skeleton;
    }

    private static void Apply(BoneInfo bone, (Vector3 S, Quaternion R, Vector3 T) trs)
    {
        bone.Scale    = new[] { trs.S.X, trs.S.Y, trs.S.Z };
        bone.Rotation = new[] { trs.R.X, trs.R.Y, trs.R.Z, trs.R.W };
        bone.Position = new[] { trs.T.X, trs.T.Y, trs.T.Z };
    }

    /// <summary>Index of the root the bone's parent chain terminates at (cycle-safe).</summary>
    private static int RootOf((int Group, int Parent, int Child, int Chain)[] parents, int b)
    {
        int cur = b;
        for (int guard = 0; guard < parents.Length; guard++)
        {
            int p = parents[cur].Parent;
            if (p < 0 || p >= parents.Length || p == cur) return cur;
            cur = p;
        }
        return cur;
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Measure how well a candidate skeleton explains the mesh.
    ///
    /// The strong signal, when skin weights are present: a skinned vertex sits close to the
    /// joints that drive it, so the mean distance from each sampled vertex to the
    /// weight-blended world-bind position of its influences is small for the right bind pose
    /// and large for a wrong one. No thresholds or hand-tuned constants — the two candidates
    /// are simply compared against each other.
    /// </summary>
    public static Score Evaluate(SkeletonData skeleton, MeshAssetData? mesh)
    {
        var world = SkeletonMath.ComputeWorldBind(skeleton);
        if (world.Length == 0) return new Score(double.NaN, 0, 0, false);

        var jMin = new Vector3(float.MaxValue);
        var jMax = new Vector3(float.MinValue);
        foreach (var m in world)
        {
            var t = m.Translation;
            if (!IsFinite(t)) continue;
            jMin = Vector3.Min(jMin, t);
            jMax = Vector3.Max(jMax, t);
        }
        double jointExtent = jMin.X <= jMax.X ? (jMax - jMin).Length() : 0;

        // Find a LOD that has both positions and a skin table.
        LodData? skinned = mesh?.Lods.FirstOrDefault(l => l.Skin != null && l.VertexBuffer != null);
        var lodForBounds = mesh?.Lods.FirstOrDefault(l => l.VertexBuffer != null);

        double meshExtent = 0;
        if (lodForBounds?.VertexBuffer is { } vb0)
        {
            var mMin = new Vector3(float.MaxValue);
            var mMax = new Vector3(float.MinValue);
            int count = vb0.Length / 12;
            int step  = Math.Max(1, count / 4096);
            for (int v = 0; v < count; v += step)
            {
                var p = ReadVec(vb0, v * 12);
                if (!IsFinite(p)) continue;
                mMin = Vector3.Min(mMin, p);
                mMax = Vector3.Max(mMax, p);
            }
            if (mMin.X <= mMax.X) meshExtent = (mMax - mMin).Length();
        }

        if (skinned == null) return new Score(double.NaN, jointExtent, meshExtent, false);

        // Same measurement the viewer reports as its rest-pose check, so the two can never
        // disagree about whether a mesh and a skeleton belong together.
        var agreement = SkeletonMath.MeasureRestAgreement(skeleton, skinned, world);
        return agreement.SampleCount == 0
            ? new Score(double.NaN, jointExtent, meshExtent, false)
            : new Score(agreement.MeanDistance, jointExtent, meshExtent, true);
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Vector3 ReadVec(byte[] d, int o) =>
        new(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

    private static uint  R32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, o) : 0u;
    private static int   I32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToInt32(d, o)  : -1;
    private static int   R16(byte[] d, int o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, o) : 0;
    private static float F32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToSingle(d, o) : 0f;

    private static string ReadString(byte[] d, int o)
    {
        if (o < 0 || o >= d.Length) return string.Empty;
        int end = o;
        while (end < d.Length && d[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(d, o, end - o);
    }
}
