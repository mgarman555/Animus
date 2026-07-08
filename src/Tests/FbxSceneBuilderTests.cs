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
