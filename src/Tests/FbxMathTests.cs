using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.ModelExporter.Fbx;
using Xunit;

namespace GameAssetExplorer.Tests;

/// <summary>
/// The skinning math is the riskiest part of the FBX path (matrix conventions, quat→euler order,
/// bind matrices). These assert internal consistency: euler decomposition reproduces the source
/// quaternion's rotation, and global bind matrices compose down the hierarchy. Blender still gets
/// the final say on Windows, but any convention slip shows up here first.
/// </summary>
public class FbxMathTests
{
    // Reference quaternion → 3×3 rotation, evaluated by rotating each basis vector.
    private static (double x, double y, double z) RotateByQuat(double qx, double qy, double qz, double qw, double vx, double vy, double vz)
    {
        // v' = q * v * q^-1 for a unit quaternion.
        double ix = qw * vx + qy * vz - qz * vy;
        double iy = qw * vy + qz * vx - qx * vz;
        double iz = qw * vz + qx * vy - qy * vx;
        double iw = -qx * vx - qy * vy - qz * vz;
        double rx = ix * qw + iw * -qx + iy * -qz - iz * -qy;
        double ry = iy * qw + iw * -qy + iz * -qx - ix * -qz;
        double rz = iz * qw + iw * -qz + ix * -qy - iy * -qx;
        return (rx, ry, rz);
    }

    private static BoneInfo Bone(string name, int parent, float[] pos, float[] quat) =>
        new() { Name = name, ParentIndex = parent, Position = pos, Rotation = quat, Scale = new float[] { 1, 1, 1 } };

    [Theory]
    [InlineData(0, 0, 0, 1)]                    // identity
    [InlineData(0, 0, 0.70710678, 0.70710678)] // 90° about Z
    [InlineData(0.70710678, 0, 0, 0.70710678)] // 90° about X
    [InlineData(0.5, 0.5, 0.5, 0.5)]           // 120° about (1,1,1)
    public void QuatToEuler_ReproducesRotation(double qx, double qy, double qz, double qw)
    {
        var (ex, ey, ez) = Mat4.QuatToEulerXyzDeg(qx, qy, qz, qw);
        var r = Mat4.RotationEulerXyzDeg(ex, ey, ez);

        // Compare how each basis vector transforms under the euler matrix vs the source quaternion.
        foreach (var (vx, vy, vz) in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0) })
        {
            double mx = r.M[0] * vx + r.M[4] * vy + r.M[8] * vz;
            double my = r.M[1] * vx + r.M[5] * vy + r.M[9] * vz;
            double mz = r.M[2] * vx + r.M[6] * vy + r.M[10] * vz;
            var (ex2, ey2, ez2) = RotateByQuat(qx, qy, qz, qw, vx, vy, vz);
            Assert.Equal(ex2, mx, 5);
            Assert.Equal(ey2, my, 5);
            Assert.Equal(ez2, mz, 5);
        }
    }

    [Fact]
    public void GlobalBind_ComposesDownHierarchy()
    {
        // root at origin (identity), child translated +Y by 10 in local space.
        var root = Bone("root", -1, new float[] { 0, 0, 0 }, new float[] { 0, 0, 0, 1 });
        var child = Bone("child", 0, new float[] { 0, 10, 0 }, new float[] { 0, 0, 0, 1 });

        var rootLocal = Mat4.LocalFromBone(root);
        var childLocal = Mat4.LocalFromBone(child);
        var childGlobal = rootLocal * childLocal;

        var (x, y, z) = childGlobal.TranslationPart();
        Assert.Equal(0, x, 5);
        Assert.Equal(10, y, 5);
        Assert.Equal(0, z, 5);
    }

    [Fact]
    public void Inverse_TimesOriginal_IsIdentity()
    {
        var b = Bone("b", -1, new float[] { 3, -2, 5 }, new float[] { 0, 0, 0.70710678f, 0.70710678f });
        var m = Mat4.LocalFromBone(b);
        var id = m * m.Inverse();
        for (int i = 0; i < 16; i++)
        {
            double expected = (i % 5 == 0) ? 1.0 : 0.0; // diagonal elements at 0,5,10,15
            Assert.Equal(expected, id.M[i], 5);
        }
    }
}
