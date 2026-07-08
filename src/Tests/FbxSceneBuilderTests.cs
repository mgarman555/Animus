using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.ModelExporter.Fbx;
using Xunit;

namespace GameAssetExplorer.Tests;

/// <summary>
/// Builds an FBX scene from a real MeshAssetData and parses it back, asserting the geometry,
/// normals, UVs, materials and connections survive. This is the in-sandbox stand-in for a Blender
/// import (which must still be eyeballed on the Windows PC).
/// </summary>
public class FbxSceneBuilderTests
{
    private static MeshAssetData TwoSubmeshQuad()
    {
        // 6 verts = two triangles, split into two submeshes so material/connection wiring is exercised.
        var vb = new byte[6 * 12];
        (float, float, float)[] p =
        {
            (0,0,0), (1,0,0), (0,1,0),   // submesh 0
            (0,0,1), (1,0,1), (0,1,1),   // submesh 1
        };
        for (int v = 0; v < 6; v++)
        {
            BitConverter.TryWriteBytes(vb.AsSpan(v * 12 + 0, 4), p[v].Item1);
            BitConverter.TryWriteBytes(vb.AsSpan(v * 12 + 4, 4), p[v].Item2);
            BitConverter.TryWriteBytes(vb.AsSpan(v * 12 + 8, 4), p[v].Item3);
        }
        var ib = new byte[6 * 4];
        for (int i = 0; i < 6; i++) BitConverter.TryWriteBytes(ib.AsSpan(i * 4, 4), i);

        var uv = new byte[6 * 8]; // zeros are fine — just exercises the UV path

        return new MeshAssetData
        {
            Info = new AssetInfo { Name = "Quad", Type = AssetType.StaticMesh },
            MaterialSlots =
            {
                new MaterialSlot { SlotIndex = 0, MaterialName = "MatA" },
                new MaterialSlot { SlotIndex = 1, MaterialName = "MatB" },
            },
            Lods =
            {
                new LodData
                {
                    LodIndex = 0, VertexCount = 6, TriangleCount = 2,
                    VertexBuffer = vb, IndexBuffer = ib, UvBuffer = uv,
                    Submeshes =
                    {
                        new SubmeshInfo { Name = "part0", VertexStart = 0, VertexCount = 3, IndexStart = 0, IndexCount = 3 },
                        new SubmeshInfo { Name = "part1", VertexStart = 3, VertexCount = 3, IndexStart = 3, IndexCount = 3 },
                    }
                }
            }
        };
    }

    [Fact]
    public void Build_StaticMesh_ProducesParseableSceneWithGeometryAndMaterials()
    {
        var mesh = TwoSubmeshQuad();
        var nodes = FbxSceneBuilder.Build(mesh, mesh.Lods[0], new ExportSettings { ApplyBlenderBoneCorrection = false, ModelScaleFactor = 1f });
        var bytes = FbxBinaryWriter.WriteToBytes(nodes);
        var read = FbxBinaryReader.ReadFromBytes(bytes);

        var names = read.Select(n => n.Name).ToList();
        Assert.Contains("FBXHeaderExtension", names);
        Assert.Contains("GlobalSettings", names);
        Assert.Contains("Definitions", names);
        Assert.Contains("Objects", names);
        Assert.Contains("Connections", names);

        var objects = read.First(n => n.Name == "Objects");
        var geos = objects.Children.Where(c => c.Name == "Geometry").ToList();
        var models = objects.Children.Where(c => c.Name == "Model").ToList();
        var mats = objects.Children.Where(c => c.Name == "Material").ToList();
        Assert.Equal(2, geos.Count);
        Assert.Equal(2, models.Count);
        Assert.Equal(2, mats.Count);

        // Each geometry has 3 verts (9 doubles) and a polygon index with a negative terminator.
        foreach (var g in geos)
        {
            var verts = Assert.IsType<double[]>(g.Children.First(c => c.Name == "Vertices").Properties[0]);
            Assert.Equal(9, verts.Length);
            var poly = Assert.IsType<int[]>(g.Children.First(c => c.Name == "PolygonVertexIndex").Properties[0]);
            Assert.Equal(3, poly.Length);
            Assert.True(poly[2] < 0, "last polygon index must be one's-complemented");
            Assert.Contains(g.Children, c => c.Name == "LayerElementNormal");
            Assert.Contains(g.Children, c => c.Name == "LayerElementUV");
        }

        // Material names carried through.
        var matNames = mats.Select(m => (string)m.Properties[1]).ToList();
        Assert.Contains("Material::MatA", matNames);
        Assert.Contains("Material::MatB", matNames);

        // Connections: model→0, geom→model, mat→model for each submesh = 6 total.
        var conns = read.First(n => n.Name == "Connections");
        Assert.Equal(6, conns.Children.Count(c => c.Name == "C"));
    }

    [Fact]
    public void Build_SkinnedMesh_EmitsSkeletonSkinAndPose()
    {
        // 2 bones; 3 verts, each bound to a bone (vert 0,1 -> bone 0; vert 2 -> bone 1).
        var vb = new byte[3 * 12];
        BitConverter.TryWriteBytes(vb.AsSpan(12, 4), 1f);
        BitConverter.TryWriteBytes(vb.AsSpan(28, 4), 1f);
        var ib = new byte[3 * 4];
        for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(ib.AsSpan(i * 4, 4), i);

        var mesh = new MeshAssetData
        {
            Info = new AssetInfo { Name = "Rigged", Type = AssetType.SkeletalMesh },
            IsSkeletal = true,
            MaterialSlots = { new MaterialSlot { MaterialName = "M" } },
            Skeleton = new SkeletonData
            {
                Bones =
                {
                    new BoneInfo { Name = "root", ParentIndex = -1, Position = new float[] { 0, 0, 0 }, Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
                    new BoneInfo { Name = "child", ParentIndex = 0, Position = new float[] { 0, 5, 0 }, Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
                }
            },
            Lods =
            {
                new LodData
                {
                    LodIndex = 0, VertexCount = 3, TriangleCount = 1,
                    VertexBuffer = vb, IndexBuffer = ib,
                    InfluencesPerVertex = 1,
                    BoneIndices = new ushort[] { 0, 0, 1 },
                    BoneWeights = new float[] { 1f, 1f, 1f },
                    Submeshes = { new SubmeshInfo { Name = "s", VertexStart = 0, VertexCount = 3, IndexStart = 0, IndexCount = 3 } }
                }
            }
        };

        var nodes = FbxSceneBuilder.Build(mesh, mesh.Lods[0], new ExportSettings { ExportSkeleton = true });
        var read = FbxBinaryReader.ReadFromBytes(FbxBinaryWriter.WriteToBytes(nodes));
        var objects = read.First(n => n.Name == "Objects");

        // Two LimbNode bone models + one armature Null.
        var models = objects.Children.Where(c => c.Name == "Model").ToList();
        Assert.Contains(models, m => (string)m.Properties[2] == "Null");
        Assert.Equal(2, models.Count(m => (string)m.Properties[2] == "LimbNode"));

        // Skin + clusters.
        var deformers = objects.Children.Where(c => c.Name == "Deformer").ToList();
        Assert.Contains(deformers, d => (string)d.Properties[2] == "Skin");
        var clusters = deformers.Where(d => (string)d.Properties[2] == "Cluster").ToList();
        Assert.Equal(2, clusters.Count); // one per influencing bone

        // Cluster weights present and TransformLink is a 16-element matrix.
        foreach (var cl in clusters)
        {
            var w = Assert.IsType<double[]>(cl.Children.First(c => c.Name == "Weights").Properties[0]);
            Assert.All(w, x => Assert.Equal(1.0, x, 5));
            var tl = Assert.IsType<double[]>(cl.Children.First(c => c.Name == "TransformLink").Properties[0]);
            Assert.Equal(16, tl.Length);
        }

        // Bind pose lists armature + both bones.
        var pose = objects.Children.First(c => c.Name == "Pose");
        Assert.Equal(3, pose.Children.Count(c => c.Name == "PoseNode"));

        // child bone's global bind translation is +5 in Y (composed from the hierarchy).
        var childCluster = clusters.First(c => ((int[])c.Children.First(k => k.Name == "Indexes").Properties[0]).Contains(2));
        var childLink = (double[])childCluster.Children.First(k => k.Name == "TransformLink").Properties[0];
        Assert.Equal(5.0, childLink[13], 4); // column-major translation Y at index 13
    }

    [Fact]
    public void BuildSkeletonOnly_EmitsBonesAndPoseNoGeometry()
    {
        var skel = new SkeletonData
        {
            Bones =
            {
                new BoneInfo { Name = "root", ParentIndex = -1, Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
                new BoneInfo { Name = "spine", ParentIndex = 0, Position = new float[] { 0, 1, 0 }, Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
            }
        };
        var nodes = FbxSceneBuilder.BuildSkeletonOnly(skel, new ExportSettings());
        var read = FbxBinaryReader.ReadFromBytes(FbxBinaryWriter.WriteToBytes(nodes));
        var objects = read.First(n => n.Name == "Objects");

        Assert.DoesNotContain(objects.Children, c => c.Name == "Geometry");
        Assert.Equal(2, objects.Children.Count(c => c.Name == "Model" && (string)c.Properties[2] == "LimbNode"));
        Assert.Contains(objects.Children, c => c.Name == "Pose");
    }

    [Fact]
    public void Build_DeclaresYUpWhenBoneCorrectionOn_ZUpWhenOff()
    {
        var mesh = TwoSubmeshQuad();

        int UpAxis(bool correction)
        {
            var nodes = FbxSceneBuilder.Build(mesh, mesh.Lods[0], new ExportSettings { ApplyBlenderBoneCorrection = correction });
            var gs = nodes.First(n => n.Name == "GlobalSettings");
            var p70 = gs.Children.First(c => c.Name == "Properties70");
            var up = p70.Children.First(c => c.Name == "P" && (string)c.Properties[0] == "UpAxis");
            return (int)up.Properties[4];
        }

        Assert.Equal(1, UpAxis(true));   // Y-up (data baked)
        Assert.Equal(2, UpAxis(false));  // Z-up (UE native)
    }
}
