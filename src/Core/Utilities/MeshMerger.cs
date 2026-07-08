using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Utilities;

/// <summary>
/// Merges multiple MeshAssetData objects into a single combined mesh.
///
/// This is the main tool for characters that ship with separate geometry pieces —
/// head, body, armor, hair, props — where you want one clean FBX for Blender or Unreal.
///
/// How the merge works:
///   For each LOD level (defaulting to LOD 0 if counts differ), vertex buffers are
///   concatenated in order and index buffers are remapped to account for the new vertex
///   offsets. Each original mesh becomes a named SubmeshInfo entry inside the resulting
///   LodData so the boundary information isn't lost. Material slots are preserved and
///   their indices are offset so slot 0 from mesh A doesn't collide with slot 0 from
///   mesh B. If both meshes are skeletal and share bones by name, those bones are unified
///   into one skeleton; bones that only appear in one mesh are kept and marked accordingly.
/// </summary>
public static class MeshMerger
{
    /// <summary>
    /// Merge a list of meshes into a single MeshAssetData.
    /// Returns null if the input list is empty or contains no valid LOD 0 data.
    /// </summary>
    public static MeshAssetData? Merge(
        IReadOnlyList<MeshAssetData> meshes,
        MeshMergeSettings? settings = null)
    {
        settings ??= new MeshMergeSettings();

        // Filter to meshes that actually have geometry
        var valid = meshes
            .Where(m => m.Lods.Count > 0 && m.Lods[0].VertexBuffer != null && m.Lods[0].VertexCount > 0)
            .ToList();

        if (valid.Count == 0) return null;
        if (valid.Count == 1) return valid[0]; // Nothing to merge

        // Figure out how many LOD levels to produce:
        // If all meshes have the same count, produce all of them.
        // If they differ, produce only LOD 0 — safest default.
        var lodCounts = valid.Select(m => m.Lods.Count).Distinct().ToList();
        int numLods = lodCounts.Count == 1 ? lodCounts[0] : 1;

        bool isSkeletal = valid.Any(m => m.IsSkeletal);

        // ── Build the merged skeleton (if applicable) ─────────────────────────
        SkeletonData? mergedSkeleton = null;
        if (isSkeletal && settings.MergeSkeletons)
        {
            mergedSkeleton = MergeSkeletons(valid.Where(m => m.Skeleton != null)
                                                  .Select(m => m.Skeleton!)
                                                  .ToList());
        }

        // ── Merge material slots ──────────────────────────────────────────────
        var mergedMaterials = new List<MaterialSlot>();
        int slotOffset = 0;
        var materialOffsets = new int[valid.Count]; // slot offset per input mesh

        for (int i = 0; i < valid.Count; i++)
        {
            materialOffsets[i] = slotOffset;
            foreach (var slot in valid[i].MaterialSlots)
            {
                mergedMaterials.Add(new MaterialSlot
                {
                    SlotIndex    = slot.SlotIndex + slotOffset,
                    MaterialName = string.IsNullOrEmpty(slot.MaterialName)
                        ? $"Material_{i}_{slot.SlotIndex}"
                        : slot.MaterialName,
                    MaterialPath = slot.MaterialPath
                });
            }
            slotOffset += valid[i].MaterialSlots.Count > 0
                ? valid[i].MaterialSlots.Count
                : 1; // always advance at least 1 so slots stay unique
        }

        // ── Bone remap: each source mesh's local bone order → merged skeleton order ──
        // Skin indices in each source LOD point into that mesh's own Skeleton.Bones list.
        // After MergeSkeletons unifies bones by name, those indices must be rebased.
        Dictionary<string, int>? mergedBoneIndex = null;
        if (mergedSkeleton != null)
        {
            mergedBoneIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < mergedSkeleton.Bones.Count; b++)
                mergedBoneIndex[mergedSkeleton.Bones[b].Name] = b;
        }

        // ── Merge LODs ────────────────────────────────────────────────────────
        var mergedLods = new List<LodData>();

        for (int lodIdx = 0; lodIdx < numLods; lodIdx++)
        {
            var lod = MergeLod(valid, lodIdx, mergedBoneIndex, settings);
            if (lod != null) mergedLods.Add(lod);
        }

        if (mergedLods.Count == 0) return null;

        // ── Compute merged bounding box ───────────────────────────────────────
        var bounds = ComputeCombinedBounds(valid);

        // ── Assemble the result ───────────────────────────────────────────────
        var result = new MeshAssetData
        {
            Info = new AssetInfo
            {
                Name         = settings.MergedMeshName,
                Type         = isSkeletal ? AssetType.SkeletalMesh : AssetType.StaticMesh,
                VirtualPath  = $"_merged/{settings.MergedMeshName}",
                EngineClassName = isSkeletal ? "SkeletalMesh" : "StaticMesh"
            },
            IsSkeletal     = isSkeletal,
            Lods           = mergedLods,
            Skeleton       = mergedSkeleton,
            MaterialSlots  = mergedMaterials,
            Bounds         = bounds,
        };

        // Record which source meshes went into this merge
        result.RawProperties["_MergedFrom"] = string.Join(", ", valid.Select(m => m.Info.Name));
        result.RawProperties["_MergedCount"] = valid.Count;
        result.RawProperties["_TotalVertices"] = mergedLods[0].VertexCount;
        result.RawProperties["_TotalTriangles"] = mergedLods[0].TriangleCount;

        return result;
    }

    // ─── LOD merging ──────────────────────────────────────────────────────────

    private static LodData? MergeLod(
        IReadOnlyList<MeshAssetData> meshes,
        int lodIndex,
        Dictionary<string, int>? mergedBoneIndex,
        MeshMergeSettings settings)
    {
        // Pair each mesh with the LOD it contributes (requested level, else its highest),
        // keeping only those with real geometry. Pairing (not two parallel lists) keeps each
        // source LOD aligned with its owning mesh — needed to remap skin indices and name submeshes.
        var sources = meshes
            .Select(m => (mesh: m, lod: lodIndex < m.Lods.Count ? m.Lods[lodIndex] : m.Lods[0]))
            .Where(p => p.lod.VertexBuffer != null && p.lod.VertexCount > 0)
            .ToList();
        if (sources.Count == 0) return null;

        // Each vertex is 12 bytes (float32 × 3)
        const int VERT_STRIDE = 12;
        const int IDX_STRIDE  = 4;  // int32 per index

        int totalVerts   = sources.Sum(p => p.lod.VertexCount);
        int totalIndices = sources.Sum(p => p.lod.IndexBuffer?.Length / IDX_STRIDE ?? 0);

        var mergedVerts   = new byte[totalVerts   * VERT_STRIDE];
        var mergedIndices = new byte[totalIndices  * IDX_STRIDE];

        // UV buffer is optional — only include if every source lod has one
        bool hasUv = sources.All(p => p.lod.UvBuffer != null);
        const int UV_STRIDE = 8; // float32 U + float32 V
        var mergedUvs = hasUv ? new byte[totalVerts * UV_STRIDE] : null;

        // Normals/tangents: include only if every source has them (a partial set would misalign)
        bool hasNormals  = sources.All(p => p.lod.Normals  != null);
        bool hasTangents = sources.All(p => p.lod.Tangents != null);
        var mergedNormals  = hasNormals  ? new float[totalVerts * 3] : null;
        var mergedTangents = hasTangents ? new float[totalVerts * 4] : null;

        // Skin: include when a merged skeleton exists and at least one source is skinned.
        // Use a uniform influences-per-vertex = max across sources; pad the rest with weight 0.
        bool hasSkin = mergedBoneIndex != null && sources.Any(p => p.lod.BoneIndices != null && p.lod.BoneWeights != null);
        int influences = hasSkin
            ? sources.Where(p => p.lod.BoneIndices != null).Select(p => p.lod.InfluencesPerVertex).DefaultIfEmpty(4).Max()
            : 4;
        var mergedBoneIdx = hasSkin ? new ushort[totalVerts * influences] : null;
        var mergedBoneWt  = hasSkin ? new float[totalVerts  * influences] : null;

        var submeshes   = new List<SubmeshInfo>();
        int vertexCursor = 0;
        int indexCursor  = 0;

        foreach (var (mesh, src) in sources)
        {
            var meshName   = mesh.Info.Name;
            int srcVerts   = src.VertexCount;
            int srcIndices = src.IndexBuffer?.Length / IDX_STRIDE ?? 0;

            // Copy vertex positions
            if (src.VertexBuffer != null)
            {
                int bytesToCopy = Math.Min(src.VertexBuffer.Length, srcVerts * VERT_STRIDE);
                Buffer.BlockCopy(src.VertexBuffer, 0, mergedVerts, vertexCursor * VERT_STRIDE, bytesToCopy);
            }

            // Copy UVs if present
            if (hasUv && mergedUvs != null && src.UvBuffer != null)
            {
                int uvBytes = Math.Min(src.UvBuffer.Length, srcVerts * UV_STRIDE);
                Buffer.BlockCopy(src.UvBuffer, 0, mergedUvs, vertexCursor * UV_STRIDE, uvBytes);
            }

            // Copy normals / tangents if present
            if (mergedNormals != null && src.Normals != null)
                Array.Copy(src.Normals, 0, mergedNormals, vertexCursor * 3, Math.Min(src.Normals.Length, srcVerts * 3));
            if (mergedTangents != null && src.Tangents != null)
                Array.Copy(src.Tangents, 0, mergedTangents, vertexCursor * 4, Math.Min(src.Tangents.Length, srcVerts * 4));

            // Copy + remap skin influences into the merged skeleton's bone order
            if (hasSkin && mergedBoneIdx != null && mergedBoneWt != null)
                RemapSkin(mesh, src, srcVerts, influences, vertexCursor, mergedBoneIndex!, mergedBoneIdx, mergedBoneWt);

            // Copy and remap indices (offset by vertexCursor)
            if (src.IndexBuffer != null)
            {
                for (int j = 0; j < srcIndices; j++)
                {
                    int srcIdx = BitConverter.ToInt32(src.IndexBuffer, j * IDX_STRIDE);
                    int newIdx = srcIdx + vertexCursor;
                    BitConverter.TryWriteBytes(mergedIndices.AsSpan((indexCursor + j) * IDX_STRIDE, IDX_STRIDE), newIdx);
                }
            }

            // Record submesh boundary
            submeshes.Add(new SubmeshInfo
            {
                Name        = meshName,
                VertexStart = vertexCursor,
                VertexCount = srcVerts,
                IndexStart  = indexCursor,
                IndexCount  = srcIndices
            });

            vertexCursor += srcVerts;
            indexCursor  += srcIndices;
        }

        return new LodData
        {
            LodIndex            = lodIndex,
            ScreenSize          = sources[0].lod.ScreenSize,
            VertexCount         = totalVerts,
            TriangleCount       = totalIndices / 3,
            VertexBuffer        = mergedVerts,
            IndexBuffer         = mergedIndices,
            UvBuffer            = mergedUvs,
            Normals             = mergedNormals,
            Tangents            = mergedTangents,
            BoneIndices         = mergedBoneIdx,
            BoneWeights         = mergedBoneWt,
            InfluencesPerVertex = influences,
            Submeshes           = submeshes
        };
    }

    /// <summary>
    /// Copies one source LOD's skin influences into the merged skin arrays at <paramref name="vertexCursor"/>,
    /// translating each source-local bone index to its position in the merged skeleton (matched by bone name).
    /// Handles differing per-source influence counts by copying up to the merged stride. Sources with no skin
    /// data leave zero-weight (unbound) vertices.
    /// </summary>
    private static void RemapSkin(
        MeshAssetData mesh, LodData src, int srcVerts, int influences, int vertexCursor,
        Dictionary<string, int> mergedBoneIndex, ushort[] outIdx, float[] outWt)
    {
        if (src.BoneIndices == null || src.BoneWeights == null) return; // unbound: leave zeros
        var bones = mesh.Skeleton?.Bones;
        int srcInf = Math.Max(src.InfluencesPerVertex, 1);

        for (int v = 0; v < srcVerts; v++)
        {
            int copy = Math.Min(srcInf, influences);
            for (int k = 0; k < copy; k++)
            {
                int si = v * srcInf + k;
                if (si >= src.BoneIndices.Length || si >= src.BoneWeights.Length) break;

                ushort localBone = src.BoneIndices[si];
                int merged = localBone;
                // Translate local index → name → merged index when we have the source skeleton
                if (bones != null && localBone < bones.Count &&
                    mergedBoneIndex.TryGetValue(bones[localBone].Name, out int mi))
                    merged = mi;

                int di = (vertexCursor + v) * influences + k;
                outIdx[di] = (ushort)merged;
                outWt[di]  = src.BoneWeights[si];
            }
        }
    }

    // ─── Skeleton merging ─────────────────────────────────────────────────────

    private static SkeletonData MergeSkeletons(IReadOnlyList<SkeletonData> skeletons)
    {
        // Union of bones by name. When two skeletons share a bone name, we take the
        // first occurrence (which typically comes from the primary mesh / body).
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged    = new SkeletonData();

        foreach (var skel in skeletons)
        {
            foreach (var bone in skel.Bones)
            {
                if (seenNames.Add(bone.Name))
                    merged.Bones.Add(bone);
            }
        }

        return merged;
    }

    // ─── Bounding box ─────────────────────────────────────────────────────────

    private static BoundingBox ComputeCombinedBounds(IReadOnlyList<MeshAssetData> meshes)
    {
        var bb = new BoundingBox
        {
            Min = new[] { float.MaxValue, float.MaxValue, float.MaxValue },
            Max = new[] { float.MinValue, float.MinValue, float.MinValue }
        };

        foreach (var mesh in meshes)
        {
            var src = mesh.Bounds;
            for (int axis = 0; axis < 3; axis++)
            {
                if (src.Min[axis] < bb.Min[axis]) bb.Min[axis] = src.Min[axis];
                if (src.Max[axis] > bb.Max[axis]) bb.Max[axis] = src.Max[axis];
            }
        }

        // If no valid bounds were set (all zeros), return a zeroed box
        if (bb.Min[0] == float.MaxValue)
        {
            bb.Min = new float[3];
            bb.Max = new float[3];
        }

        return bb;
    }
}

/// <summary>
/// Controls how the merge is performed. Defaults are sensible for a typical
/// character assembly workflow (combine pieces, keep separate materials).
/// </summary>
public class MeshMergeSettings
{
    /// <summary>
    /// Name for the resulting merged asset. Appears in the file name and asset tree.
    /// </summary>
    public string MergedMeshName { get; set; } = "MergedMesh";

    /// <summary>
    /// Whether to merge the bone hierarchies from all skeletal meshes into one skeleton.
    /// When true, bones with the same name from different meshes are unified.
    /// Set to false if you want the skeletons to remain separate (unusual).
    /// </summary>
    public bool MergeSkeletons { get; set; } = true;

    /// <summary>
    /// Which LOD to use from each source mesh when the meshes have different LOD counts.
    /// 0 = always LOD 0 (highest quality). Ignored when all meshes have the same LOD count.
    /// </summary>
    public int FallbackLodIndex { get; set; } = 0;

    /// <summary>
    /// Scale factor to apply to all vertex positions before merging.
    /// Useful if source meshes are from different engines with different unit scales.
    /// 1.0 = no change.
    /// </summary>
    public float UniformScale { get; set; } = 1.0f;
}
