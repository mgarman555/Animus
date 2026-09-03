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
        // boneRemap[i] maps valid[i]'s bone indices into the merged skeleton's index space.
        // Null means "this mesh contributed no skeleton", in which case its skin table (if
        // any) cannot be carried across and is dropped rather than silently mis-indexed.
        SkeletonData? mergedSkeleton = null;
        var boneRemap = new int[]?[valid.Count];

        if (isSkeletal && settings.MergeSkeletons)
        {
            var withSkeletons = valid.Select((m, i) => (Mesh: m, Index: i))
                                     .Where(x => x.Mesh.Skeleton != null)
                                     .ToList();

            mergedSkeleton = MergeSkeletons(
                withSkeletons.Select(x => x.Mesh.Skeleton!).ToList(),
                out var remaps);

            for (int k = 0; k < withSkeletons.Count; k++)
                boneRemap[withSkeletons[k].Index] = remaps[k];
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
            var lod = MergeLod(valid, boneRemap, lodIdx, settings);
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
        int[]?[] boneRemap,
        int lodIndex,
        MeshMergeSettings settings)
    {
        // Keep each source LOD paired with the mesh it came from. Filtering the two lists
        // separately (as this used to) desynchronises them the moment one mesh is dropped,
        // which mislabels submeshes and would mis-assign bone remaps.
        var sources = new List<(MeshAssetData Mesh, LodData Lod, int[]? Remap)>();
        for (int i = 0; i < meshes.Count; i++)
        {
            var m = meshes[i];
            var lod = lodIndex < m.Lods.Count ? m.Lods[lodIndex] : m.Lods[0];
            if (lod.VertexBuffer == null || lod.VertexCount <= 0) continue;
            sources.Add((m, lod, i < boneRemap.Length ? boneRemap[i] : null));
        }
        if (sources.Count == 0) return null;

        // Each vertex is 12 bytes (float32 × 3)
        const int VERT_STRIDE = 12;
        const int IDX_STRIDE  = 4;  // int32 per index

        int totalVerts   = sources.Sum(s => s.Lod.VertexCount);
        int totalIndices = sources.Sum(s => s.Lod.IndexBuffer?.Length / IDX_STRIDE ?? 0);

        var mergedVerts   = new byte[totalVerts   * VERT_STRIDE];
        var mergedIndices = new byte[totalIndices  * IDX_STRIDE];

        // UV buffer is optional — only include if every source lod has one
        bool hasUv = sources.All(s => s.Lod.UvBuffer != null);
        const int UV_STRIDE = 8; // float32 U + float32 V
        var mergedUvs = hasUv ? new byte[totalVerts * UV_STRIDE] : null;

        // Skin rows are fixed-width, so the merged width is the widest contributor's. A part
        // without a skin table keeps weight 0 and stays where it was authored.
        int mergedInfluences = sources.Where(s => s.Lod.Skin != null && s.Remap != null)
                                      .Select(s => s.Lod.Skin!.InfluencesPerVertex)
                                      .DefaultIfEmpty(0).Max();
        ushort[]? mergedBoneIdx = null;
        float[]?  mergedBoneWt  = null;
        int mergedMaxBone = -1;
        if (mergedInfluences > 0)
        {
            mergedBoneIdx = new ushort[totalVerts * mergedInfluences];
            mergedBoneWt  = new float[totalVerts * mergedInfluences];
        }

        var submeshes   = new List<SubmeshInfo>();
        int vertexCursor = 0;
        int indexCursor  = 0;

        foreach (var (mesh, src, remap) in sources)
        {
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

            // Carry skin weights across, translating bone indices into the merged skeleton's
            // index space. Without this a merged character has weights pointing at whatever
            // bone happens to occupy that slot in the union skeleton.
            if (mergedInfluences > 0 && mergedBoneIdx != null && mergedBoneWt != null
                && src.Skin is { } skin && remap != null)
            {
                int srcInf = Math.Max(skin.InfluencesPerVertex, 1);
                int copy   = Math.Min(srcInf, mergedInfluences);
                for (int v = 0; v < srcVerts && v < skin.VertexCount; v++)
                {
                    int dstRow = (vertexCursor + v) * mergedInfluences;
                    int srcRow = v * srcInf;
                    for (int k = 0; k < copy; k++)
                    {
                        int b = skin.BoneIndices[srcRow + k];
                        int mapped = b < remap.Length ? remap[b] : -1;
                        if (mapped < 0) continue;
                        mergedBoneIdx[dstRow + k] = (ushort)mapped;
                        mergedBoneWt [dstRow + k] = skin.BoneWeights[srcRow + k];
                        if (mapped > mergedMaxBone) mergedMaxBone = mapped;
                    }
                }
            }

            // Preserve the source submesh boundaries (and with them each part's material and
            // texture assignment) instead of flattening a whole mesh into one slot.
            var srcSubs = src.Submeshes.Count > 0
                ? src.Submeshes
                : new List<SubmeshInfo> { new() { Name = mesh.Info.Name, VertexStart = 0,
                    VertexCount = srcVerts, IndexStart = 0, IndexCount = srcIndices } };

            foreach (var sub in srcSubs)
            {
                submeshes.Add(new SubmeshInfo
                {
                    Name        = src.Submeshes.Count > 0 ? $"{mesh.Info.Name}|{sub.Name}" : sub.Name,
                    VertexStart = vertexCursor + sub.VertexStart,
                    VertexCount = sub.VertexCount,
                    IndexStart  = indexCursor  + sub.IndexStart,
                    IndexCount  = sub.IndexCount,
                    MaterialName         = sub.MaterialName,
                    DiffuseTexturePath   = sub.DiffuseTexturePath,
                    NormalTexturePath    = sub.NormalTexturePath,
                    DiffuseTextureData   = sub.DiffuseTextureData,
                    DiffuseTextureWidth  = sub.DiffuseTextureWidth,
                    DiffuseTextureHeight = sub.DiffuseTextureHeight,
                    DiffuseTextureFormat = sub.DiffuseTextureFormat,
                });
            }

            vertexCursor += srcVerts;
            indexCursor  += srcIndices;
        }

        return new LodData
        {
            LodIndex      = lodIndex,
            ScreenSize    = sources[0].Lod.ScreenSize,
            VertexCount   = totalVerts,
            TriangleCount = totalIndices / 3,
            VertexBuffer  = mergedVerts,
            IndexBuffer   = mergedIndices,
            UvBuffer      = mergedUvs,
            Submeshes     = submeshes,
            Skin          = mergedInfluences > 0 && mergedBoneIdx != null && mergedBoneWt != null
                ? new SkinBinding
                {
                    InfluencesPerVertex = mergedInfluences,
                    VertexCount         = totalVerts,
                    BoneIndices         = mergedBoneIdx,
                    BoneWeights         = mergedBoneWt,
                    MaxBoneIndex        = mergedMaxBone,
                }
                : null,
        };
    }

    // ─── Skeleton merging ─────────────────────────────────────────────────────

    /// <summary>
    /// Union the input skeletons by bone name, first occurrence winning, and hand back a
    /// per-input table mapping that input's bone indices onto the merged skeleton's.
    ///
    /// Two things this must get right, and previously did not:
    ///   • bones are DEEP-COPIED. The originals are still referenced by their source meshes,
    ///     so rewriting a parent index in place would corrupt those meshes.
    ///   • parent indices are translated through the remap. A bone's parent index is only
    ///     meaningful in its own skeleton's index space.
    ///
    /// In the common TLOU2 case every part pak resolves to the same shared <c>*-skel.pak</c>,
    /// so the remap comes out as the identity and nothing moves — which is exactly right.
    /// </summary>
    private static SkeletonData MergeSkeletons(
        IReadOnlyList<SkeletonData> skeletons, out List<int[]> remaps)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var merged      = new SkeletonData();
        remaps          = new List<int[]>(skeletons.Count);

        // Pass 1: place bones and record where each source bone landed.
        foreach (var skel in skeletons)
        {
            var map = new int[skel.Bones.Count];
            for (int i = 0; i < skel.Bones.Count; i++)
            {
                var bone = skel.Bones[i];
                string key = string.IsNullOrEmpty(bone.Name) ? $"__bone_{i}" : bone.Name;

                if (indexByName.TryGetValue(key, out int existing))
                {
                    map[i] = existing;
                    continue;
                }

                map[i] = merged.Bones.Count;
                indexByName[key] = map[i];
                merged.Bones.Add(new BoneInfo
                {
                    Name             = bone.Name,
                    ParentIndex      = bone.ParentIndex,   // still source-space; fixed in pass 2
                    Position         = (float[])bone.Position.Clone(),
                    Rotation         = (float[])bone.Rotation.Clone(),
                    Scale            = (float[])bone.Scale.Clone(),
                    GroupIndex       = bone.GroupIndex,
                    ChildIndex       = bone.ChildIndex,
                    ChainIndex       = bone.ChainIndex,
                    HasBindTransform = bone.HasBindTransform,
                });
            }
            remaps.Add(map);
        }

        // Pass 2: translate parent links, using the remap of whichever skeleton contributed
        // each merged bone.
        var fixedUp = new bool[merged.Bones.Count];
        for (int s = 0; s < skeletons.Count; s++)
        {
            var skel = skeletons[s];
            var map  = remaps[s];
            for (int i = 0; i < skel.Bones.Count; i++)
            {
                int dst = map[i];
                if (fixedUp[dst]) continue;
                fixedUp[dst] = true;

                int p = skel.Bones[i].ParentIndex;
                merged.Bones[dst].ParentIndex =
                    p >= 0 && p < map.Length && map[p] != dst ? map[p] : -1;
            }
        }

        merged.SourceName = string.Join(" + ",
            skeletons.Select(k => k.SourceName).Where(n => !string.IsNullOrEmpty(n)).Distinct());

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
