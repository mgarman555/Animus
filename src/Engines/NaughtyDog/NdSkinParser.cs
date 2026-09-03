using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Decodes a submesh's vertex→bone skin table.
///
/// The <c>SubMeshDesc</c> carries a pointer to a small skin descriptor:
///
///   skinDesc+0x00  u32  unknown
///   skinDesc+0x04  u32  totalInfluences          — the count for the WHOLE submesh, summed
///                                                  over every vertex; NOT a per-vertex cap
///   skinDesc+0x08  u32  unknown  (TLOUP1: >0 ⇒ uncompressed float weights)
///   skinDesc+0x0C  u32  unknown
///   skinDesc+0x10  ptr  index map                (numVerts × { u32 count, u32 byteOffset })
///   skinDesc+0x18  ptr  weight blob
///
/// Each vertex's influences live at <c>weights + byteOffset</c>, packed as one u32 per
/// influence: bits 0..21 are the weight, bits 22..31 the bone index. (fmt_nd_pak reads this
/// as <c>readBits(22)</c> then <c>readBits(10)</c>; Noesis's bit reader is LSB-first and
/// re-aligns to a byte on every seek, so the pair is exactly a little-endian u32 and the
/// whole blob is 4-byte aligned.)
///
/// The `+0x04` field being a submesh TOTAL rather than a per-vertex maximum is proved by the
/// reference's writer, which emits it as <c>runningOffset/4</c> where <c>runningOffset</c>
/// advances 4 bytes for every non-zero influence across every vertex
/// (fmt_nd_pak.py L3118-3133, writing to <c>mapOffsetAddr-12</c> — which is this field).
/// Reading it as a per-vertex cap and rejecting anything over 12 would throw out every real
/// submesh and silently load the character unskinned.
///
/// WHERE the skin pointer lives inside the descriptor is the one thing that cannot be taken
/// from the reference. fmt_nd_pak walks a 176-byte TLOU2 SubMeshDesc and finds it at +0x58,
/// but the real PC layout is 192 bytes with the vertex/index counts shifted +8 — the extra
/// eight bytes are inserted somewhere between the material pointer (+0x48, confirmed
/// unshifted) and the count block (+0x88, confirmed shifted). So the skin pointer is at
/// either +0x58 or +0x60. <see cref="TryParse"/> scores BOTH against the actual bytes and
/// takes the better one; a candidate only gets that far if the pak's pointer-fixup table has
/// an entry at that address, which already rules out reading a non-pointer field as one.
/// </summary>
public static class NdSkinParser
{
    /// <summary>Hard ceiling from the format — fmt_nd_pak reads at most 12 influences.</summary>
    public const int MaxInfluences = 12;

    /// <summary>Candidate offsets of the skin-data pointer inside the 192-byte SubMeshDesc.</summary>
    public static readonly int[] SkinPointerCandidates = { 0x58, 0x60 };

    /// <summary>One submesh's decoded influences, flat: vertex v slot k at <c>v * Influences + k</c>.</summary>
    public sealed class SubmeshSkin
    {
        public int      Influences   { get; init; }
        public int      VertexCount  { get; init; }
        public ushort[] BoneIndices  { get; init; } = Array.Empty<ushort>();
        public float[]  BoneWeights  { get; init; } = Array.Empty<float>();
        public int      MaxBoneIndex { get; init; } = -1;
        /// <summary>Which SubMeshDesc offset the skin pointer was found at (diagnostics).</summary>
        public int      PointerOffset { get; init; }
        /// <summary>
        /// How many corroborating invariants this candidate satisfied beyond the structural
        /// minimum — used to choose between +0x58 and +0x60 when both parse.
        /// </summary>
        public int      Confidence    { get; init; }
        /// <summary>Human-readable list of which corroborations held (diagnostics).</summary>
        public string   Evidence      { get; init; } = string.Empty;
    }

    /// <summary>
    /// Decode the skin table for the submesh whose descriptor starts at <paramref name="smdAddr"/>.
    /// <paramref name="resolvePtr"/> resolves a pointer-fixup at an absolute byte address to an
    /// absolute target address, or -1 when the address holds no fixup.
    /// Returns null when the submesh is unskinned or no candidate layout validated.
    /// </summary>
    public static SubmeshSkin? TryParse(byte[] data, Func<int, int> resolvePtr,
                                        int smdAddr, int numVerts, string label)
    {
        if (numVerts <= 0) return null;

        SubmeshSkin? best = null;

        foreach (int candidate in SkinPointerCandidates)
        {
            int skinDesc = resolvePtr(smdAddr + candidate);
            if (skinDesc < 0 || skinDesc + 32 > data.Length) continue;

            var parsed = TryDecode(data, resolvePtr, skinDesc, numVerts, candidate);
            if (parsed == null) continue;

            // Both offsets can carry a real pointer, so take the better-corroborated table
            // rather than whichever was tried first.
            if (best == null || parsed.Confidence > best.Confidence) best = parsed;
        }

        return best;
    }

    private static SubmeshSkin? TryDecode(byte[] data, Func<int, int> resolvePtr,
                                          int skinDesc, int numVerts, int pointerOffset)
    {
        int mapAddr     = resolvePtr(skinDesc + 0x10);
        int weightsAddr = resolvePtr(skinDesc + 0x18);
        if (mapAddr < 0 || weightsAddr < 0) return null;

        long mapBytes = (long)numVerts * 8;
        if (mapAddr + mapBytes > data.Length) return null;
        if (weightsAddr >= data.Length) return null;

        // ── Structural validation ────────────────────────────────────────────
        // These hold regardless of what the +0x04 field turns out to mean, so a table that
        // passes them is decodable even if that field is something else in some build.
        // A wrong candidate pointer lands on unrelated bytes and trips one almost immediately.
        int  maxCount    = 0;
        long summedCount = 0;
        long blobEnd     = 0;
        long prevEnd     = 0;
        bool sequential  = true;

        for (int v = 0; v < numVerts; v++)
        {
            int o = mapAddr + v * 8;
            uint count = R32(data, o);
            uint boff  = R32(data, o + 4);

            // A vertex with no influences is legal (a stray unweighted vertex in an otherwise
            // skinned submesh); it becomes a zero-weight row and the skinner leaves it at rest.
            // A count past the format ceiling is not legal and rejects the candidate.
            if (count > MaxInfluences) return null;
            if ((boff & 3) != 0) return null;
            if (count == 0) continue;

            long end = (long)weightsAddr + boff + count * 4L;
            if (end > data.Length) return null;

            // Rows must not overlap; the reference's writer emits them back to back.
            if (boff < prevEnd - weightsAddr) return null;
            if (boff != prevEnd - weightsAddr && v > 0) sequential = false;
            prevEnd = end;

            summedCount += count;
            if (count > maxCount) maxCount = (int)count;
            if (end > blobEnd) blobEnd = end;
        }

        // ── Decode ───────────────────────────────────────────────────────────
        int influences = Math.Min(Math.Max(maxCount, 1), MaxInfluences);
        long cells = (long)numVerts * influences;
        if (cells <= 0 || cells > int.MaxValue / 4) return null;

        var indices = new ushort[cells];
        var weights = new float[cells];

        int  maxBone = -1;
        double rawRowSum = 0;
        int rowsCounted = 0;

        for (int v = 0; v < numVerts; v++)
        {
            int o = mapAddr + v * 8;
            int count = (int)R32(data, o);
            int boff  = (int)R32(data, o + 4);
            int src   = weightsAddr + boff;
            int dst   = v * influences;

            float rawSum = 0f;
            int written = 0;

            for (int k = 0; k < count && k < influences; k++)
            {
                uint packed = R32(data, src + k * 4);
                uint raw    = packed & 0x3FFFFFu;          // bits 0..21
                int  bone   = (int)((packed >> 22) & 0x3FFu); // bits 22..31

                indices[dst + written] = (ushort)bone;
                weights[dst + written] = raw;
                rawSum += raw;
                if (bone > maxBone) maxBone = bone;
                written++;
            }

            rawRowSum += rawSum;
            rowsCounted++;

            // Normalise so every row sums to 1. Doing it from the raw sum rather than the
            // encoder's fixed 4194303 divisor means a row that quantised slightly off still
            // comes out watertight.
            if (rawSum > 0f)
                for (int k = 0; k < written; k++) weights[dst + k] /= rawSum;
            else
                for (int k = 0; k < written; k++) weights[dst + k] = 0f;
        }

        // A table where every vertex is rigidly bound to bone 0 is what a misread pointer that
        // happens to survive the range checks looks like. Real character skins are not that.
        // 1024 is the ceiling of the 10-bit index field.
        if (maxBone <= 0 || maxBone >= 1024) return null;

        // ── Corroboration ────────────────────────────────────────────────────
        // Beyond the structural minimum, these confirm the read. They are scored rather than
        // enforced so that one surprising field cannot cost us a mesh that decodes fine.
        int confidence = 0;
        var evidence = new List<string>(4);

        // The +0x04 field should be the submesh's total influence count (see the class docs).
        uint declaredTotal = R32(data, skinDesc + 4);
        if (declaredTotal == summedCount) { confidence += 2; evidence.Add("total matches +0x04"); }

        // The weight blob should be exactly consumed by those influences.
        if (blobEnd - weightsAddr == summedCount * 4) { confidence++; evidence.Add("blob exactly consumed"); }

        if (sequential) { confidence++; evidence.Add("rows sequential"); }

        // Weights really are 22-bit fixed point normalised against 2²²−1.
        if (rowsCounted > 0)
        {
            double mean = rawRowSum / rowsCounted;
            if (Math.Abs(mean - 4194303.0) / 4194303.0 < 0.01)
            { confidence++; evidence.Add("rows sum to 2²²−1"); }
        }

        return new SubmeshSkin
        {
            Influences    = influences,
            VertexCount   = numVerts,
            BoneIndices   = indices,
            BoneWeights   = weights,
            MaxBoneIndex  = maxBone,
            PointerOffset = pointerOffset,
            Confidence    = confidence,
            Evidence      = string.Join(", ", evidence),
        };
    }

    /// <summary>
    /// Log which descriptor offset won, once per asset, so a layout change in a future game
    /// build shows up in the log instead of silently producing an unskinned mesh.
    /// </summary>
    public static void LogLayout(string label, IEnumerable<SubmeshSkin> skins)
    {
        var list = skins.ToList();
        var byOffset = list.GroupBy(s => s.PointerOffset)
                           .Select(g => $"+0x{g.Key:X2}×{g.Count()}")
                           .ToArray();
        if (byOffset.Length == 0) return;

        string corroboration = list
            .GroupBy(s => s.Evidence)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()}× [{(g.Key.Length == 0 ? "structural only" : g.Key)}]")
            .First();

        Log.Info($"NdSkinParser[{label}]: skin pointer resolved at {string.Join(", ", byOffset)}; " +
                 $"corroboration {corroboration}");

        if (byOffset.Length > 1)
            Log.Warn($"NdSkinParser[{label}]: submeshes disagree on the skin-pointer offset — " +
                     "the 192-byte SubMeshDesc layout may differ from what is assumed.");
    }

    private static uint R32(byte[] d, int o) =>
        o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, o) : 0u;
}
