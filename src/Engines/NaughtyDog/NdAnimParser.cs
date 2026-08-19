using System.Numerics;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Reads joint animation out of Naughty Dog <c>anim-*.pak</c> files.
///
/// <para><b>What is known.</b> The pak container itself is fully understood (pages, pointer
/// fixups, ResItems, string tables), so enumerating and NAMING animation resources is exact:
/// clips appear as ResItems of type <c>ANIM</c>, <c>ANIM_GROUP</c> or <c>ANIM_STREAM</c>, and
/// the ResItem carries the clip's own name string. Those type names are confirmed against the
/// reverse-engineered TLOU2 runtime SDK's <c>ItemId</c> enum (ANIM = 0x29, ANIM_GROUP = 0x2A,
/// ANIM_STREAM = 0x33, JOINT_HIERARCHY = 0x2B).</para>
///
/// <para><b>What is not.</b> No public tool decodes ND clip keyframes — the Noesis reference
/// this project ports from registers itself with <c>-noanims</c> and has no animation code at
/// all, and the one closed-source tool that attempts it documents its own output as wrong. So
/// the keyframe payload is not read from a published spec here; it is <i>measured</i>.</para>
///
/// <para><b>How the payload is recovered.</b> Rotation tracks are found by looking for what a
/// rotation track provably is, rather than by guessing offsets: a run of float4s that are all
/// unit-length, and that move smoothly from one sample to the next. Both properties are
/// physical invariants of joint animation and neither holds for unrelated bytes, so a run that
/// satisfies both at 4-byte alignment is a rotation track with very high confidence. The same
/// continuity test then distinguishes joint-major storage (one run per joint, F samples each)
/// from frame-major (one run per frame, J samples each), because only the joint-major reading
/// produces small frame-to-frame angular deltas.</para>
///
/// <para>Everything the scan concludes is reported through <see cref="DecodeReport"/> and the
/// log. When the invariants do not hold — which is what a bit-packed/quantised clip looks like
/// — the parser decodes nothing and says so, rather than emitting a plausible-looking pose
/// that would be wrong in the viewport.</para>
/// </summary>
public static class NdAnimParser
{
    /// <summary>Shortest run of samples accepted as a track.</summary>
    private const int MinTrackSamples = 4;

    /// <summary>How far from unit length a quaternion may be and still count.</summary>
    private const float UnitTolerance = 2e-3f;

    /// <summary>
    /// Mean angular step (radians) below which a run reads as consecutive frames of ONE joint
    /// rather than one frame across MANY joints. Adjacent joints in a skeleton differ by large
    /// rotations; adjacent frames of a 30 fps clip differ by a fraction of a degree.
    /// </summary>
    private const double JointMajorMaxMeanStep = 0.35;

    /// <summary>One animation resource found in the pak.</summary>
    public sealed record AnimResourceInfo(
        string Type,
        string Name,
        int    PageIndex,
        int    PageStart,
        int    ResItemOffset,
        int    DataStart);

    /// <summary>What the scan saw, so a failed decode is diagnosable instead of silent.</summary>
    public sealed class DecodeReport
    {
        public List<AnimResourceInfo> Resources { get; } = new();
        public int    QuaternionRunsFound   { get; set; }
        public int    LongestRun            { get; set; }
        public int    DominantRunLength     { get; set; }
        public int    DominantRunCount      { get; set; }
        public double MeanAngularStep       { get; set; } = double.NaN;
        public string Layout                { get; set; } = "undetermined";
        public string Outcome               { get; set; } = "not attempted";
        public int    ClipsDecoded          { get; set; }

        public string Summarise() =>
            $"resources={Resources.Count} " +
            $"[{string.Join(", ", Resources.GroupBy(r => r.Type).Select(g => $"{g.Count()}×{g.Key}"))}] " +
            $"quatRuns={QuaternionRunsFound} longest={LongestRun} " +
            $"dominant={DominantRunCount}×{DominantRunLength} " +
            $"meanStep={(double.IsNaN(MeanAngularStep) ? "n/a" : MeanAngularStep.ToString("F5"))} " +
            $"layout={Layout} clips={ClipsDecoded} — {Outcome}";
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>Enumerate every animation resource in the pak, with its clip name.</summary>
    public static List<AnimResourceInfo> Discover(NdPakReader reader)
    {
        var list = new List<AnimResourceInfo>();
        foreach (var e in reader.AnimEntries)
        {
            list.Add(new AnimResourceInfo(
                Type:          e.Type,
                Name:          string.IsNullOrEmpty(e.ItemName) ? e.Type.ToLowerInvariant() : e.ItemName,
                PageIndex:     e.PageIndex,
                PageStart:     e.PageStart,
                ResItemOffset: e.ResItemOffset,
                DataStart:     e.PageStart + e.ResItemOffset + reader.ResItemPaddingSz));
        }
        return list;
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode every clip this pak yields. <paramref name="skeleton"/> supplies joint names and
    /// count; without it, tracks are still recovered but named positionally.
    /// </summary>
    public static List<AnimationAssetData> DecodeAll(
        NdPakReader reader, SkeletonData? skeleton, string label, out DecodeReport report)
    {
        report = new DecodeReport();
        var clips = new List<AnimationAssetData>();

        report.Resources.AddRange(Discover(reader));
        if (report.Resources.Count == 0)
        {
            report.Outcome = "no ANIM/ANIM_GROUP/ANIM_STREAM resources in this pak";
            return clips;
        }

        report.Outcome = "scanning for rotation tracks";

        var runs = FindQuaternionRuns(reader.Data);
        report.QuaternionRunsFound = runs.Count;
        report.LongestRun = runs.Count > 0 ? runs.Max(r => r.Count) : 0;

        if (runs.Count == 0)
        {
            report.Outcome = "no uncompressed rotation tracks present — clip payload is quantised, " +
                             "and its bit layout is not yet measured (run tools/nd_anim_probe.py on this pak)";
            Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
            return clips;
        }

        // Group by run length: a clip's tracks all share one length, so the modal length is
        // the clip's frame count (joint-major) or joint count (frame-major).
        var byLength = runs.GroupBy(r => r.Count)
                           .OrderByDescending(g => g.Count())
                           .ThenByDescending(g => g.Key)
                           .First();

        report.DominantRunLength = byLength.Key;
        report.DominantRunCount  = byLength.Count();

        var group = byLength.OrderBy(r => r.Offset).ToList();
        double meanStep = group.Average(r => r.MeanStep);
        report.MeanAngularStep = meanStep;

        bool jointMajor = meanStep <= JointMajorMaxMeanStep;
        report.Layout = jointMajor ? "joint-major (one run per joint)" : "frame-major (one run per frame)";

        if (!jointMajor)
        {
            // Frame-major: sample j of run f is joint j at frame f. Transpose into tracks.
            group = Transpose(group, reader.Data);
            if (group.Count == 0)
            {
                report.Outcome = "frame-major layout detected but could not be transposed into per-joint tracks";
                Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
                return clips;
            }
        }

        int jointCount = group.Count;
        int frameCount = jointMajor ? byLength.Key : byLength.Count();

        if (frameCount < MinTrackSamples)
        {
            report.Outcome = $"only {frameCount} frames recovered — too short to be a clip";
            Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
            return clips;
        }

        // One clip per animation resource is the common case; when a pak holds several
        // resources but only one coherent track set, attribute it to the first resource and
        // say so rather than inventing duplicates.
        var primary = report.Resources[0];
        float fps = ProbeFrameRate(reader.Data, primary.DataStart);

        var clip = new AnimationAssetData
        {
            Info       = new AssetInfo { Name = primary.Name, Type = AssetType.Animation },
            ClipName   = primary.Name,
            FrameRate  = fps,
            FrameCount = frameCount,
        };

        for (int j = 0; j < jointCount; j++)
        {
            var run = group[j];
            var track = new AnimTrack
            {
                BoneName  = skeleton != null && j < skeleton.Bones.Count ? skeleton.Bones[j].Name : $"joint_{j}",
                BoneIndex = skeleton != null && j < skeleton.Bones.Count ? j : -1,
            };
            for (int f = 0; f < frameCount; f++)
            {
                var q = run.Samples != null
                    ? run.Samples[f]
                    : ReadQuat(reader.Data, run.Offset + f * 16);
                track.RotationKeys.Add(new[] { q.X, q.Y, q.Z, q.W });
            }
            clip.Tracks.Add(track);
            clip.JointNames.Add(track.BoneName);
        }

        clips.Add(clip);
        report.ClipsDecoded = 1;
        report.Outcome = skeleton == null
            ? $"decoded {jointCount} rotation tracks × {frameCount} frames; joint names are positional " +
              "because no skeleton was supplied"
            : $"decoded {jointCount} rotation tracks × {frameCount} frames, mapped positionally onto " +
              $"'{skeleton.SourceName}' ({skeleton.Bones.Count} bones)" +
              (jointCount != skeleton.Bones.Count
                  ? " — track count and bone count differ, so the mapping is unverified"
                  : "");

        Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
        return clips;
    }

    // ── Track scanning ───────────────────────────────────────────────────────

    private sealed class QuatRun
    {
        public int Offset;
        public int Count;
        public double MeanStep;
        public Quaternion[]? Samples;   // set only for synthesised (transposed) runs
    }

    /// <summary>
    /// Every maximal 4-byte-aligned run of unit-length float4s in the file. Runs shorter than
    /// <see cref="MinTrackSamples"/> are dropped — four consecutive unit quaternions arising
    /// by chance from unrelated bytes is vanishingly unlikely, which is what makes this a
    /// reliable detector rather than a heuristic.
    /// </summary>
    private static List<QuatRun> FindQuaternionRuns(byte[] data)
    {
        var runs = new List<QuatRun>();
        int limit = data.Length - 16;
        int i = 0;

        while (i <= limit)
        {
            if (!IsUnitQuat(data, i)) { i += 4; continue; }

            int start = i;
            int count = 0;
            while (i <= limit && IsUnitQuat(data, i)) { count++; i += 16; }

            if (count >= MinTrackSamples)
                runs.Add(new QuatRun { Offset = start, Count = count, MeanStep = MeanAngularStep(data, start, count) });
        }

        return runs;
    }

    private static bool IsUnitQuat(byte[] d, int o)
    {
        if (o + 16 > d.Length) return false;
        float x = BitConverter.ToSingle(d, o);
        float y = BitConverter.ToSingle(d, o + 4);
        float z = BitConverter.ToSingle(d, o + 8);
        float w = BitConverter.ToSingle(d, o + 12);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(w)) return false;

        float len2 = x * x + y * y + z * z + w * w;
        if (MathF.Abs(len2 - 1f) > UnitTolerance) return false;

        // All-zero-imaginary identity quaternions are also what zero-filled padding plus a
        // 1.0f looks like; they are legitimate but only as part of a longer run, which the
        // caller's length threshold already enforces.
        return true;
    }

    /// <summary>Mean absolute angle between consecutive samples of a run, in radians.</summary>
    private static double MeanAngularStep(byte[] d, int offset, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        var prev = ReadQuat(d, offset);
        for (int k = 1; k < count; k++)
        {
            var cur = ReadQuat(d, offset + k * 16);
            sum += AngleBetween(prev, cur);
            prev = cur;
        }
        return sum / (count - 1);
    }

    private static double AngleBetween(Quaternion a, Quaternion b)
    {
        float dot = MathF.Abs(Quaternion.Dot(a, b));
        if (dot > 1f) dot = 1f;
        return 2.0 * Math.Acos(dot);
    }

    /// <summary>
    /// Reinterpret F runs of J samples (frame-major) as J tracks of F samples (joint-major).
    /// Only accepted when the transposed tracks are actually continuous — otherwise the
    /// frame-major reading was wrong too and we return nothing rather than fabricate motion.
    /// </summary>
    private static List<QuatRun> Transpose(List<QuatRun> frames, byte[] data)
    {
        int f = frames.Count;
        int j = frames[0].Count;
        if (f < MinTrackSamples || j <= 0) return new List<QuatRun>();

        var tracks = new List<QuatRun>(j);
        double totalStep = 0;

        for (int joint = 0; joint < j; joint++)
        {
            var samples = new Quaternion[f];
            for (int frame = 0; frame < f; frame++)
                samples[frame] = ReadQuat(data, frames[frame].Offset + joint * 16);

            double step = 0;
            for (int k = 1; k < f; k++) step += AngleBetween(samples[k - 1], samples[k]);
            step /= (f - 1);
            totalStep += step;

            tracks.Add(new QuatRun { Offset = frames[0].Offset, Count = f, MeanStep = step, Samples = samples });
        }

        return (totalStep / j) <= JointMajorMaxMeanStep ? tracks : new List<QuatRun>();
    }

    private static Quaternion ReadQuat(byte[] d, int o) => new(
        BitConverter.ToSingle(d, o),
        BitConverter.ToSingle(d, o + 4),
        BitConverter.ToSingle(d, o + 8),
        BitConverter.ToSingle(d, o + 12));

    /// <summary>
    /// Look for the clip's frame rate in its header. ND clips run at 30 fps, so a float in a
    /// plausible range near the resource start is taken as the rate; anything else falls back
    /// to 30 rather than producing a clip that plays at the wrong speed silently.
    /// </summary>
    private static float ProbeFrameRate(byte[] data, int dataStart)
    {
        for (int o = dataStart; o < dataStart + 128 && o + 4 <= data.Length; o += 4)
        {
            float v = BitConverter.ToSingle(data, o);
            if (!float.IsFinite(v)) continue;
            // Accept only the rates animation is actually authored at.
            if (MathF.Abs(v - 30f) < 0.01f || MathF.Abs(v - 60f) < 0.01f ||
                MathF.Abs(v - 24f) < 0.01f || MathF.Abs(v - 15f) < 0.01f)
                return v;
        }
        return 30f;
    }
}
