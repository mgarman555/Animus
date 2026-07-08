using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.ModelExporter.Fbx;
using Xunit;

namespace GameAssetExplorer.Tests;

public class FbxAnimationTests
{
    private static SkeletonData TwoBoneSkeleton() => new()
    {
        Bones =
        {
            new BoneInfo { Name = "root", ParentIndex = -1, Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
            new BoneInfo { Name = "child", ParentIndex = 0, Position = new float[] { 0, 5, 0 }, Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 } },
        }
    };

    [Fact]
    public void BuildAnimation_EmitsStackLayerCurveNodesAndCurves()
    {
        var anim = new AnimationAssetData
        {
            Info = new AssetInfo { Name = "Idle", Type = AssetType.Animation },
            FrameRate = 30f,
            FrameCount = 3,
            Tracks =
            {
                new AnimTrack
                {
                    BoneName = "child",
                    PositionKeys = { new float[] { 0, 5, 0 }, new float[] { 0, 6, 0 }, new float[] { 0, 7, 0 } },
                    RotationKeys = { new float[] { 0, 0, 0, 1 }, new float[] { 0, 0, 0.7071f, 0.7071f }, new float[] { 0, 0, 1, 0 } },
                }
            }
        };

        var nodes = FbxSceneBuilder.BuildAnimation(TwoBoneSkeleton(), anim, new ExportSettings());
        var read = FbxBinaryReader.ReadFromBytes(FbxBinaryWriter.WriteToBytes(nodes));
        var objects = read.First(n => n.Name == "Objects");

        Assert.Contains(objects.Children, c => c.Name == "AnimationStack");
        Assert.Contains(objects.Children, c => c.Name == "AnimationLayer");

        // Position + rotation channels = 2 curve nodes, 6 curves.
        Assert.Equal(2, objects.Children.Count(c => c.Name == "AnimationCurveNode"));
        Assert.Equal(6, objects.Children.Count(c => c.Name == "AnimationCurve"));

        // Curves carry 3 keys with KTime scaled per frame.
        var curve = objects.Children.First(c => c.Name == "AnimationCurve");
        var times = Assert.IsType<long[]>(curve.Children.First(k => k.Name == "KeyTime").Properties[0]);
        Assert.Equal(3, times.Length);
        Assert.Equal(0L, times[0]);
        Assert.True(times[2] > times[1] && times[1] > times[0], "key times must be strictly increasing");
    }

    [Fact]
    public void RotationCurves_AreUnrolled_NoBigJumps()
    {
        // A rotation sweeping past 180° about Z would wrap to -180 without unrolling.
        var keys = new List<float[]>();
        for (int f = 0; f <= 8; f++)
        {
            double ang = f * 30.0 * Math.PI / 180.0; // 0..240° about Z
            keys.Add(new[] { 0f, 0f, (float)Math.Sin(ang / 2), (float)Math.Cos(ang / 2) });
        }
        var anim = new AnimationAssetData
        {
            Info = new AssetInfo { Name = "Sweep", Type = AssetType.Animation },
            FrameRate = 30f, FrameCount = keys.Count,
            Tracks = { new AnimTrack { BoneName = "child", RotationKeys = keys } }
        };

        var nodes = FbxSceneBuilder.BuildAnimation(TwoBoneSkeleton(), anim, new ExportSettings());
        var read = FbxBinaryReader.ReadFromBytes(FbxBinaryWriter.WriteToBytes(nodes));
        var objects = read.First(n => n.Name == "Objects");

        // The Z rotation curve is the 3rd curve of the rotation curve node; grab all curves and find
        // the one whose values monotonically climb toward 240 (proof of unrolling).
        var zCurves = objects.Children
            .Where(c => c.Name == "AnimationCurve")
            .Select(c => (float[])c.Children.First(k => k.Name == "KeyValueFloat").Properties[0])
            .ToList();

        var climbing = zCurves.FirstOrDefault(v => v.Length == keys.Count && v[^1] > 200);
        Assert.NotNull(climbing);
        for (int i = 1; i < climbing!.Length; i++)
            Assert.True(Math.Abs(climbing[i] - climbing[i - 1]) < 90,
                "unrolled euler must not jump ~360° between adjacent frames");
    }
}
