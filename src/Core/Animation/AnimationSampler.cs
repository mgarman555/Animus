using System.Numerics;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Animation;

/// <summary>
/// Samples an <see cref="AnimationAssetData"/> clip at an arbitrary time and produces the
/// parent-local pose for every bone of a target skeleton.
///
/// Channels the clip does not animate fall back to the bind pose, so a clip that only moves
/// the upper body leaves the legs where the skeleton put them instead of collapsing them to
/// the origin. Additive clips are layered on top of the bind pose rather than replacing it.
/// </summary>
public sealed class AnimationSampler
{
    private readonly SkeletonData _skeleton;
    private readonly AnimationAssetData _clip;

    /// <summary>Tracks re-indexed to the skeleton; null slots = bone not animated.</summary>
    private readonly AnimTrack?[] _trackByBone;

    /// <summary>Bind-pose TRS cached per bone so sampling allocates nothing per frame.</summary>
    private readonly Vector3[]    _bindPos;
    private readonly Quaternion[] _bindRot;
    private readonly Vector3[]    _bindScale;

    /// <summary>How many of the clip's tracks found a bone on this skeleton.</summary>
    public int BoundTrackCount { get; }

    /// <summary>Track names that had no counterpart on the skeleton (diagnostics).</summary>
    public IReadOnlyList<string> UnboundTracks { get; }

    public AnimationSampler(SkeletonData skeleton, AnimationAssetData clip)
    {
        _skeleton = skeleton;
        _clip     = clip;

        int n = skeleton.Bones.Count;
        _trackByBone = new AnimTrack?[n];
        _bindPos     = new Vector3[n];
        _bindRot     = new Quaternion[n];
        _bindScale   = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            var b = skeleton.Bones[i];
            _bindPos[i]   = new Vector3(b.Position[0], b.Position[1], b.Position[2]);
            var q = new Quaternion(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3]);
            _bindRot[i]   = q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
            var s = new Vector3(b.Scale[0], b.Scale[1], b.Scale[2]);
            _bindScale[i] = (s.X == 0 && s.Y == 0 && s.Z == 0) ? Vector3.One : s;
        }

        var byName = skeleton.NameToIndex();
        var unbound = new List<string>();
        int bound = 0;

        foreach (var track in clip.Tracks)
        {
            // TryGetValue zeroes its out-parameter when the lookup misses, so it must not
            // write straight into `idx` — an unmatched track would silently bind to bone 0.
            int idx = track.BoneIndex;
            if (idx < 0 && !string.IsNullOrEmpty(track.BoneName)
                        && byName.TryGetValue(track.BoneName, out int found))
                idx = found;

            if (idx >= 0 && idx < n)
            {
                track.BoneIndex = idx;
                _trackByBone[idx] = track;
                bound++;
            }
            else
            {
                unbound.Add(track.BoneName);
            }
        }

        BoundTrackCount = bound;
        UnboundTracks   = unbound;
    }

    /// <summary>Clip length in seconds (0 for a single-frame clip).</summary>
    public float Duration => _clip.Duration;

    public int FrameCount => Math.Max(_clip.FrameCount, 1);

    /// <summary>
    /// Parent-local pose matrices at <paramref name="timeSeconds"/>. Writes into
    /// <paramref name="dest"/> when it is the right size, otherwise allocates.
    /// </summary>
    public Matrix4x4[] SampleLocal(float timeSeconds, Matrix4x4[]? dest = null)
    {
        int n = _skeleton.Bones.Count;
        var outMats = (dest != null && dest.Length == n) ? dest : new Matrix4x4[n];

        // Frame position: clips are frame-indexed, so convert once and share across tracks.
        float fps    = _clip.FrameRate > 0.01f ? _clip.FrameRate : 30f;
        float framePos = timeSeconds * fps;
        int   lastFrame = Math.Max(FrameCount - 1, 0);
        if (framePos < 0) framePos = 0;
        if (framePos > lastFrame) framePos = lastFrame;

        int   f0 = (int)MathF.Floor(framePos);
        int   f1 = Math.Min(f0 + 1, lastFrame);
        float t  = framePos - f0;

        for (int i = 0; i < n; i++)
        {
            var track = _trackByBone[i];

            Vector3    pos   = _bindPos[i];
            Quaternion rot   = _bindRot[i];
            Vector3    scale = _bindScale[i];

            if (track != null)
            {
                if (track.PositionKeys.Count > 0)
                {
                    var p = LerpVec(track.PositionKeys, f0, f1, t);
                    pos = _clip.IsAdditive ? _bindPos[i] + p : p;
                }
                if (track.RotationKeys.Count > 0)
                {
                    var r = SlerpQuat(track.RotationKeys, f0, f1, t);
                    rot = _clip.IsAdditive ? Quaternion.Normalize(_bindRot[i] * r) : r;
                }
                if (track.ScaleKeys.Count > 0)
                {
                    var s = LerpVec(track.ScaleKeys, f0, f1, t);
                    scale = _clip.IsAdditive ? _bindScale[i] * s : s;
                }
            }

            outMats[i] = Matrix4x4.CreateScale(scale)
                       * Matrix4x4.CreateFromQuaternion(rot)
                       * Matrix4x4.CreateTranslation(pos);
        }

        return outMats;
    }

    /// <summary>
    /// Skinning palette at <paramref name="timeSeconds"/>: <c>worldPose · inverseBind</c>.
    /// Pass the inverse-bind array in so it is computed once per mesh, not once per frame.
    /// </summary>
    public Matrix4x4[] SamplePalette(float timeSeconds, Matrix4x4[] inverseBind,
                                     Matrix4x4[]? localScratch = null)
    {
        var local = SampleLocal(timeSeconds, localScratch);
        var world = SkeletonMath.ComputeWorldPose(_skeleton, local);
        return SkeletonMath.BuildSkinningPalette(world, inverseBind);
    }

    /// <summary>World-space joint positions at <paramref name="timeSeconds"/> (armature overlay).</summary>
    public Vector3[] SampleJointPositions(float timeSeconds, Matrix4x4[]? localScratch = null)
    {
        var local = SampleLocal(timeSeconds, localScratch);
        var world = SkeletonMath.ComputeWorldPose(_skeleton, local);
        var pts = new Vector3[world.Length];
        for (int i = 0; i < world.Length; i++) pts[i] = world[i].Translation;
        return pts;
    }

    // ── Key interpolation ────────────────────────────────────────────────────
    //
    // A track with one key is constant. Clamping the index rather than wrapping means the
    // final frame holds instead of snapping back to frame 0 — looping is the player's job.

    private static Vector3 LerpVec(List<float[]> keys, int f0, int f1, float t)
    {
        var a = At(keys, f0);
        if (keys.Count == 1) return new Vector3(a[0], a[1], a[2]);
        var b = At(keys, f1);
        return new Vector3(
            a[0] + (b[0] - a[0]) * t,
            a[1] + (b[1] - a[1]) * t,
            a[2] + (b[2] - a[2]) * t);
    }

    private static Quaternion SlerpQuat(List<float[]> keys, int f0, int f1, float t)
    {
        var a = At(keys, f0);
        var qa = Quaternion.Normalize(new Quaternion(a[0], a[1], a[2], a[3]));
        if (keys.Count == 1) return qa;
        var b = At(keys, f1);
        var qb = Quaternion.Normalize(new Quaternion(b[0], b[1], b[2], b[3]));
        return Quaternion.Normalize(Quaternion.Slerp(qa, qb, t));
    }

    private static float[] At(List<float[]> keys, int frame)
    {
        if (keys.Count == 0) return _zero4;
        int i = frame < 0 ? 0 : frame >= keys.Count ? keys.Count - 1 : frame;
        var k = keys[i];
        return k.Length >= 4 ? k : Pad(k);
    }

    private static readonly float[] _zero4 = { 0, 0, 0, 1 };

    private static float[] Pad(float[] k)
    {
        var p = new float[4] { 0, 0, 0, 1 };
        for (int i = 0; i < k.Length && i < 4; i++) p[i] = k[i];
        return p;
    }
}
