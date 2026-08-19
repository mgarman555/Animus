using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Decodes a submesh's vertex→bone skin table.
///
/// The <c>SubMeshDesc</c> carries a pointer to a small skin descriptor:
///
///   skinDesc+0x00  u32  unknown
///   skinDesc+0x04  u32  maxInfluencesPerVertex   (fmt_nd_pak: "numWeights", ≤ 12)
///   skinDesc+0x08  u32  unknown
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
/// WHERE the skin pointer lives inside the descriptor is the one thing that cannot be taken
/// from the reference. fmt_nd_pak walks a 176-byte TLOU2 SubMeshDesc and finds it at +0x58,
/// but the real PC layout is 192 bytes with the vertex/index counts shifted +8 — the extra
/// eight bytes are inserted somewhere between the material pointer (+0x48, confirmed
/// unshifted) and the count block (+0x88, confirmed shifted). So the skin pointer is at
/// either +0x58 or +0x60. Rather than pick one, <see cref="TryParse"/> validates both against
/// the actual bytes and uses whichever produces a self-consistent table.
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

        foreach (int candidate in SkinPointerCandidates)
        {
            int skinDesc = resolvePtr(smdAddr + candidate);
            if (skinDesc < 0 || skinDesc + 32 > data.Length) continue;

            var parsed = TryDecode(data, resolvePtr, skinDesc, numVerts, candidate);
            if (parsed != null) return parsed;
        }

        return null;
    }

    private static SubmeshSkin? TryDecode(byte[] data, Func<int, int> resolvePtr,
                                          int skinDesc, int numVerts, int pointerOffset)
    {
        int declaredMax = (int)R32(data, skinDesc + 4);
        if (declaredMax < 1 || declaredMax > MaxInfluences) return null;

        int mapAddr     = resolvePtr(skinDesc + 0x10);
        int weightsAddr = resolvePtr(skinDesc + 0x18);
        if (mapAddr < 0 || weightsAddr < 0) return null;

        long mapBytes = (long)numVerts * 8;
        if (mapAddr + mapBytes > data.Length) return null;
        if (weightsAddr >= data.Length) return null;

        // ── Validate the index map before trusting any of it ────────────────
        // Per-vertex byte offsets must be 4-aligned (one u32 per influence), must stay inside
        // the file, and counts must respect the descriptor's own maximum. A wrong candidate
        // pointer lands on unrelated bytes and trips one of these almost immediately.
        int  maxCount = 0;
        long maxEnd   = 0;

        for (int v = 0; v < numVerts; v++)
        {
            int o = mapAddr + v * 8;
            uint count = R32(data, o);
            uint boff  = R32(data, o + 4);

            if (count == 0 || count > (uint)declaredMax) return null;
            if ((boff & 3) != 0) return null;

            long end = (long)weightsAddr + boff + count * 4L;
            if (end > data.Length) return null;

            if (count > maxCount) maxCount = (int)count;
            if (end > maxEnd) maxEnd = end;
        }

        // ── Decode ───────────────────────────────────────────────────────────
        int influences = Math.Min(Math.Max(maxCount, 1), MaxInfluences);
        var indices = new ushort[(long)numVerts * influences <= int.MaxValue ? numVerts * influences : 0];
        if (indices.Length == 0) return null;
        var weights = new float[numVerts * influences];

        int maxBone = -1;

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

            // Normalise so every row sums to 1. Doing it from the raw sum rather than a
            // fixed 2^22 divisor means we do not depend on the encoder's exact scale, and
            // rows that quantised slightly off still come out watertight.
            if (rawSum > 0f)
                for (int k = 0; k < written; k++) weights[dst + k] /= rawSum;
            else
                for (int k = 0; k < written; k++) weights[dst + k] = 0f;
        }

        // A table where every vertex is rigidly bound to bone 0 is what a misread pointer
        // that happens to survive the range checks looks like. Real character skins are not
        // that.
        if (maxBone <= 0) return null;

        return new SubmeshSkin
        {
            Influences    = influences,
            VertexCount   = numVerts,
            BoneIndices   = indices,
            BoneWeights   = weights,
            MaxBoneIndex  = maxBone,
            PointerOffset = pointerOffset,
        };
    }

    /// <summary>
    /// Log which descriptor offset won, once per asset, so a layout change in a future game
    /// build shows up in the log instead of silently producing an unskinned mesh.
    /// </summary>
    public static void LogLayout(string label, IEnumerable<SubmeshSkin> skins)
    {
        var byOffset = skins.GroupBy(s => s.PointerOffset)
                            .Select(g => $"+0x{g.Key:X2}×{g.Count()}")
                            .ToArray();
        if (byOffset.Length > 0)
            Log.Info($"NdSkinParser[{label}]: skin pointer resolved at {string.Join(", ", byOffset)}");
    }

    private static uint R32(byte[] d, int o) =>
        o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, o) : 0u;
}
