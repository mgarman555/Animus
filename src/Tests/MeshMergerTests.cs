using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Utilities;
using Xunit;

namespace GameAssetExplorer.Tests;

/// <summary>
/// Verifies MeshMerger carries the Phase 0 skin/normal channels through a merge and rebases
/// skin bone indices into the unified skeleton. No mocks — builds real MeshAssetData in memory.
/// </summary>
public class MeshMergerTests
{
    // Two triangles, two skeletal meshes with per-mesh bone orders that must be unified.
    private static MeshAssetData MakeSkinnedTri(string name, string[] boneNames)
    {
        // 3 verts, 1 triangle, 1 influence/vertex — each vertex bound to a different local bone.
        var verts = new byte[3 * 12];
        for (int v = 0; v < 3; v++)
            for (int c = 0; c < 3; c++)
                BitConverter.TryWriteBytes(verts.AsSpan((v * 3 + c) * 4, 4), (float)(v + c));

        var idx = new byte[3 * 4];
        for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(idx.AsSpan(i * 4, 4), i);

        var uv = new byte[3 * 8];
        var normals = new float[3 * 3];
        for (int i = 0; i < normals.Length; i++) normals[i] = 1f;

        // local bone indices 0,1,2 — one per vertex, weight 1.
        var boneIdx = new ushort[] { 0, 1, 2 };
        var boneWt = new float[] { 1f, 1f, 1f };

        return new MeshAssetData
        {
            Info = new AssetInfo { Name = name, Type = AssetType.SkeletalMesh },
            IsSkeletal = true,
            Skeleton = new SkeletonData
            {
                Bones = boneNames.Select(n => new BoneInfo { Name = n }).ToList()
            },
            Lods =
            {
                new LodData
                {
                    LodIndex = 0, VertexCount = 3, TriangleCount = 1,
                    VertexBuffer = verts, IndexBuffer = idx, UvBuffer = uv,
                    Normals = normals,
                    BoneIndices = boneIdx, BoneWeights = boneWt, InfluencesPerVertex = 1,
                }
            }
        };
    }

    [Fact]
    public void Merge_CarriesNormalsAndSkin_AndRebasesBoneIndices()
    {
        // Mesh A bones: root, hip, spine.  Mesh B bones: root, arm, hand.
        // Shared "root" unifies; merged order = [root, hip, spine, arm, hand].
        var a = MakeSkinnedTri("A", new[] { "root", "hip", "spine" });
        var b = MakeSkinnedTri("B", new[] { "root", "arm", "hand" });

        var merged = MeshMerger.Merge(new[] { a, b });
        Assert.NotNull(merged);

        var lod = merged!.Lods[0];
        Assert.Equal(6, lod.VertexCount);

        // Channels carried through
        Assert.NotNull(lod.Normals);
        Assert.Equal(6 * 3, lod.Normals!.Length);
        Assert.NotNull(lod.BoneIndices);
        Assert.NotNull(lod.BoneWeights);
        Assert.Equal(1, lod.InfluencesPerVertex);

        // Merged skeleton unified by name
        Assert.Equal(5, merged.Skeleton!.Bones.Count);
        int idxRoot = merged.Skeleton.Bones.FindIndex(x => x.Name == "root");
        int idxHip = merged.Skeleton.Bones.FindIndex(x => x.Name == "hip");
        int idxArm = merged.Skeleton.Bones.FindIndex(x => x.Name == "arm");
        int idxHand = merged.Skeleton.Bones.FindIndex(x => x.Name == "hand");

        // Mesh A verts (0,1,2) were local bones root(0),hip(1),spine(2)
        Assert.Equal(idxRoot, lod.BoneIndices![0]);
        Assert.Equal(idxHip, lod.BoneIndices[1]);
        // Mesh B verts (3,4,5) were local root(0),arm(1),hand(2) — rebased, NOT left as 0,1,2
        Assert.Equal(idxRoot, lod.BoneIndices[3]);
        Assert.Equal(idxArm, lod.BoneIndices[4]);
        Assert.Equal(idxHand, lod.BoneIndices[5]);

        // Weights preserved
        Assert.All(lod.BoneWeights!, w => Assert.Equal(1f, w));
    }

    [Fact]
    public void Merge_IndicesRemappedByVertexOffset()
    {
        var a = MakeSkinnedTri("A", new[] { "root", "hip", "spine" });
        var b = MakeSkinnedTri("B", new[] { "root", "arm", "hand" });

        var merged = MeshMerger.Merge(new[] { a, b })!;
        var lod = merged.Lods[0];

        // Mesh B's triangle indices (0,1,2) should be offset by 3 in the merged buffer.
        int i3 = BitConverter.ToInt32(lod.IndexBuffer!, 3 * 4);
        int i5 = BitConverter.ToInt32(lod.IndexBuffer!, 5 * 4);
        Assert.Equal(3, i3);
        Assert.Equal(5, i5);
    }
}
