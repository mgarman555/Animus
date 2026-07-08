using GameAssetExplorer.Exporters.ModelExporter.Fbx;
using Xunit;

namespace GameAssetExplorer.Tests;

/// <summary>
/// Round-trips the FBX binary writer through the reader. This proves the low-level record/property
/// encoding is internally consistent; cross-checking against a genuine Blender-exported FBX (parsed
/// by the same reader) is done separately with a file dropped into test-assets/.
/// </summary>
public class FbxBinaryRoundTripTests
{
    [Fact]
    public void RoundTrip_ScalarProperties()
    {
        var root = new FbxNode("Root");
        root.Add("Ints", (int)7, (long)9_000_000_000L, (short)3);
        root.Add("Floats", 1.5f, 2.5);
        root.Add("Flags", true, false);
        root.Add("Text", "Model::Cal_Body");

        var bytes = FbxBinaryWriter.WriteToBytes(new[] { root });
        var read = FbxBinaryReader.ReadFromBytes(bytes);

        Assert.Single(read);
        var r = read[0];
        Assert.Equal("Root", r.Name);
        Assert.Equal(4, r.Children.Count);

        var ints = r.Children[0];
        Assert.Equal(7, Assert.IsType<int>(ints.Properties[0]));
        Assert.Equal(9_000_000_000L, Assert.IsType<long>(ints.Properties[1]));
        Assert.Equal((short)3, Assert.IsType<short>(ints.Properties[2]));

        var floats = r.Children[1];
        Assert.Equal(1.5f, Assert.IsType<float>(floats.Properties[0]));
        Assert.Equal(2.5, Assert.IsType<double>(floats.Properties[1]));

        var flags = r.Children[2];
        Assert.True(Assert.IsType<bool>(flags.Properties[0]));
        Assert.False(Assert.IsType<bool>(flags.Properties[1]));

        var text = r.Children[3];
        Assert.Equal("Model::Cal_Body", Assert.IsType<string>(text.Properties[0]));
    }

    [Fact]
    public void RoundTrip_ArrayProperties_ZlibCompressed()
    {
        // Large-ish arrays so zlib actually engages.
        var verts = new double[300];
        for (int i = 0; i < verts.Length; i++) verts[i] = i * 0.5;
        var indices = new int[150];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        var geo = new FbxNode("Geometry");
        geo.Add("Vertices").Prop(verts);
        geo.Add("PolygonVertexIndex").Prop(indices);
        geo.Add("Floats").Prop(new float[] { 0.1f, 0.2f, 0.3f });

        var bytes = FbxBinaryWriter.WriteToBytes(new[] { geo });
        var read = FbxBinaryReader.ReadFromBytes(bytes);

        var g = read[0];
        var rv = Assert.IsType<double[]>(g.Children[0].Properties[0]);
        Assert.Equal(verts.Length, rv.Length);
        Assert.Equal(verts, rv);

        var ri = Assert.IsType<int[]>(g.Children[1].Properties[0]);
        Assert.Equal(indices, ri);

        var rf = Assert.IsType<float[]>(g.Children[2].Properties[0]);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, rf);
    }

    [Fact]
    public void RoundTrip_NestedTreeAndEmptyCompound()
    {
        var objects = new FbxNode("Objects");
        var model = objects.Add("Model", 123L, "Model::Thing", "Mesh");
        model.Add("Version", 232);
        objects.Add("EmptyCompound"); // no props, no children — exercises the terminator rule

        var bytes = FbxBinaryWriter.WriteToBytes(new[] { objects });
        var read = FbxBinaryReader.ReadFromBytes(bytes);

        var o = read[0];
        Assert.Equal("Objects", o.Name);
        Assert.Equal(2, o.Children.Count);
        Assert.Equal("Model", o.Children[0].Name);
        Assert.Equal(123L, o.Children[0].Properties[0]);
        Assert.Equal("Version", o.Children[0].Children[0].Name);
        Assert.Equal("EmptyCompound", o.Children[1].Name);
        Assert.Empty(o.Children[1].Children);
    }

    [Fact]
    public void Writer_ProducesValidMagicAndVersion()
    {
        var bytes = FbxBinaryWriter.WriteToBytes(new[] { new FbxNode("X", 1) });
        // "Kaydara FBX Binary  " then 0x00 0x1A 0x00, then u32 7400.
        var magic = System.Text.Encoding.ASCII.GetString(bytes, 0, 20);
        Assert.Equal("Kaydara FBX Binary  ", magic);
        Assert.Equal(0x1A, bytes[21]);
        uint version = BitConverter.ToUInt32(bytes, 23);
        Assert.Equal(7400u, version);
    }
}
