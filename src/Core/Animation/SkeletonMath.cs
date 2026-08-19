using System.Numerics;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Animation;

/// <summary>
/// Bind-pose maths shared by the viewport, the exporters and the animation sampler.
///
/// Convention used everywhere in this codebase:
///   • <see cref="BoneInfo"/> holds a PARENT-LOCAL TRS.
///   • World bind of bone i = worldBind[parent(i)] · localBind(i)   (column-vector maths,
///     i.e. <c>Matrix4x4.CreateScale · CreateFromQuaternion · CreateTranslation</c> composed
///     with System.Numerics' row-major storage, then multiplied parent-last).
///   • The mesh's vertex positions are authored in the SAME space as the world bind pose,
///     so the skinning matrix for bone i at pose P is  <c>worldPose(i) · worldBind(i)⁻¹</c>
///     and the identity pose reproduces the mesh exactly.
///
/// System.Numerics stores matrices row-major and <c>Vector3.Transform(v, M)</c> computes
/// <c>v · M</c>. Composing as <c>A * B</c> therefore applies A first, then B — which is why
/// a child's world matrix is <c>local * parentWorld</c> and not the other way round.
/// </summary>
public static class SkeletonMath
{
    /// <summary>Compose a bone's parent-local TRS into a matrix.</summary>
    public static Matrix4x4 LocalMatrix(BoneInfo b)
    {
        var s = new Vector3(b.Scale[0], b.Scale[1], b.Scale[2]);
        if (s.X == 0 && s.Y == 0 && s.Z == 0) s = Vector3.One;

        var q = new Quaternion(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3]);
        if (q.LengthSquared() < 1e-12f) q = Quaternion.Identity;
        else q = Quaternion.Normalize(q);

        var t = new Vector3(b.Position[0], b.Position[1], b.Position[2]);

        return Matrix4x4.CreateScale(s)
             * Matrix4x4.CreateFromQuaternion(q)
             * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>
    /// Accumulate parent-local bind transforms into world space. Bones may appear in any
    /// order: parents are resolved on demand, and a cycle (or a forward reference that
    /// cannot be resolved) degrades to the bone's local matrix rather than looping forever.
    /// </summary>
    public static Matrix4x4[] ComputeWorldBind(SkeletonData skeleton)
    {
        var bones = skeleton.Bones;
        var world = new Matrix4x4[bones.Count];
        var done  = new bool[bones.Count];

        for (int i = 0; i < bones.Count; i++)
            Resolve(i, bones, world, done, depth: 0);

        return world;
    }

    private static void Resolve(int i, List<BoneInfo> bones, Matrix4x4[] world, bool[] done, int depth)
    {
        if (done[i]) return;
        // Mark first: a parent cycle then reads the local matrix instead of recursing forever.
        done[i] = true;

        var local = LocalMatrix(bones[i]);
        int p = bones[i].ParentIndex;

        if (p >= 0 && p < bones.Count && p != i && depth < 256)
        {
            Resolve(p, bones, world, done, depth + 1);
            world[i] = local * world[p];
        }
        else
        {
            world[i] = local;
        }
    }

    /// <summary>
    /// Inverse of each world bind matrix. A singular matrix (degenerate helper joint)
    /// falls back to identity so it contributes the rest pose instead of NaNs.
    /// </summary>
    public static Matrix4x4[] ComputeInverseBind(Matrix4x4[] worldBind)
    {
        var inv = new Matrix4x4[worldBind.Length];
        for (int i = 0; i < worldBind.Length; i++)
            inv[i] = Matrix4x4.Invert(worldBind[i], out var m) ? m : Matrix4x4.Identity;
        return inv;
    }

    /// <summary>
    /// Accumulate an arbitrary set of parent-local pose matrices into world space, using the
    /// parent links of <paramref name="skeleton"/>. <paramref name="localPose"/> must be
    /// indexed the same as <see cref="SkeletonData.Bones"/>.
    /// </summary>
    public static Matrix4x4[] ComputeWorldPose(SkeletonData skeleton, Matrix4x4[] localPose)
    {
        var bones = skeleton.Bones;
        int n = Math.Min(bones.Count, localPose.Length);
        var world = new Matrix4x4[n];
        var done  = new bool[n];

        for (int i = 0; i < n; i++)
            ResolvePose(i, bones, localPose, world, done, 0);

        return world;
    }

    private static void ResolvePose(int i, List<BoneInfo> bones, Matrix4x4[] local,
                                    Matrix4x4[] world, bool[] done, int depth)
    {
        if (done[i]) return;
        done[i] = true;

        int p = bones[i].ParentIndex;
        if (p >= 0 && p < world.Length && p != i && depth < 256)
        {
            ResolvePose(p, bones, local, world, done, depth + 1);
            world[i] = local[i] * world[p];
        }
        else
        {
            world[i] = local[i];
        }
    }

    /// <summary>
    /// The palette handed to the skinner: <c>worldPose[i] · inverseBind[i]</c>. Bones the pose
    /// doesn't cover keep the bind pose (identity skinning matrix).
    /// </summary>
    public static Matrix4x4[] BuildSkinningPalette(Matrix4x4[] worldPose, Matrix4x4[] inverseBind)
    {
        int n = Math.Max(worldPose.Length, inverseBind.Length);
        var palette = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            if (i < worldPose.Length && i < inverseBind.Length)
                palette[i] = inverseBind[i] * worldPose[i];
            else
                palette[i] = Matrix4x4.Identity;
        }
        return palette;
    }

    /// <summary>World-space bind position of every bone (the joint pivots the viewer draws).</summary>
    public static Vector3[] BindPositions(SkeletonData skeleton)
    {
        var world = ComputeWorldBind(skeleton);
        var pts = new Vector3[world.Length];
        for (int i = 0; i < world.Length; i++)
            pts[i] = world[i].Translation;
        return pts;
    }
}
