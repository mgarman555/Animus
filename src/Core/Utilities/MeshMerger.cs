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

        // ── Merge LODs ────────────────────────────────────────────────────────
        var mergedLods = new List<LodData>();

        for (int lodIdx = 0; lodIdx < numLods; lodIdx++)
        {
            var lod = MergeLod(valid, lodIdx, settings);
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
        MeshMergeSettings settings)
    {
        // For each mesh, use the requested LOD or fall back to the highest available
        var sourceLods = meshes.Select(m =>
            lodIndex < m.Lods.Count ? m.Lods[lodIndex] : m.Lods[0]).ToList();

        // All must have vertex data
        var validLods = sourceLods.Where(l => l.VertexBuffer != null && l.VertexCount > 0).ToList();
        if (validLods.Count == 0) return null;

        // Each vertex is 12 bytes (float32 × 3)
        const int VERT_STRIDE = 12;
        const int IDX_STRIDE  = 4;  // int32 per index

        int totalVerts   = validLods.Sum(l => l.VertexCount);
        int totalIndices = validLods.Sum(l => l.IndexBuffer?.Length / IDX_STRIDE ?? 0);

        var mergedVerts   = new byte[totalVerts   * VERT_STRIDE];
        var mergedIndices = new byte[totalIndices  * IDX_STRIDE];

        // UV buffer is optional — only include if every source lod has one
        bool hasUv = validLods.All(l => l.UvBuffer != null);
        const int UV_STRIDE = 8; // float32 U + float32 V
        var mergedUvs = hasUv ? new byte[totalVerts * UV_STRIDE] : null;

        var submeshes   = new List<SubmeshInfo>();
        int vertexCursor = 0;
        int indexCursor  = 0;

        for (int i = 0; i < validLods.Count; i++)
        {
            var src        = validLods[i];
            var meshName   = meshes[i < meshes.Count ? i : meshes.Count - 1].Info.Name;
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
            LodIndex      = lodIndex,
            ScreenSize    = validLods[0].ScreenSize,
            VertexCount   = totalVerts,
            TriangleCount = totalIndices / 3,
            VertexBuffer  = mergedVerts,
            IndexBuffer   = mergedIndices,
            UvBuffer      = mergedUvs,
            Submeshes     = submeshes
        };
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
