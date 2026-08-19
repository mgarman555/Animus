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
    /// <summary>Shortest run of samples accepted as an uncompressed track.</summary>
    private const int MinTrackSamples = 4;

    /// <summary>
    /// Compressed quaternions are unit-length by construction, so smoothness is the ONLY
    /// evidence available and the run has to be longer before it counts.
    /// </summary>
    private const int MinPackedSamples = 12;

    /// <summary>Mean angular step (radians) a packed run must stay under to read as a track.</summary>
    private const double PackedMaxMeanStep = 0.05;

    /// <summary>Shortest run of samples accepted as a translation track.</summary>
    private const int MinTranslationSamples = 12;

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
        public int    PackedRunsFound       { get; set; }
        public string PackedFormat          { get; set; } = "none";
        public int    TranslationRunsFound  { get; set; }
        /// <summary>How the clip's joint table was located, or why it wasn't.</summary>
        public string JointMapEvidence      { get; set; } = "";

        public string Summarise() =>
            $"resources={Resources.Count} " +
            $"[{string.Join(", ", Resources.GroupBy(r => r.Type).Select(g => $"{g.Count()}×{g.Key}"))}] " +
            $"quatRuns={QuaternionRunsFound} longest={LongestRun} " +
            $"dominant={DominantRunCount}×{DominantRunLength} " +
            $"meanStep={(double.IsNaN(MeanAngularStep) ? "n/a" : MeanAngularStep.ToString("F5"))} " +
            $"packedRuns={PackedRunsFound}({PackedFormat}) transRuns={TranslationRunsFound} " +
            $"jointMap=[{JointMapEvidence}] " +
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
            // Nothing stored as plain float quaternions. Before giving up, try the packing
            // joint animation is usually quantised with: a 2-bit index naming the dropped
            // (largest) component, then three signed fixed-point components. Those decode to
            // unit quaternions by construction, so smoothness across samples is the only
            // evidence — which is why the run has to be longer and steadier to count.
            runs = FindPackedRuns(reader.Data, out string packedFormat);
            report.PackedRunsFound = runs.Count;
            report.PackedFormat    = packedFormat;

            if (runs.Count == 0)
            {
                report.Outcome = "no rotation tracks found, uncompressed or in the usual quantised " +
                                 "packings — this clip's bit layout is not yet measured " +
                                 "(run tools/nd_anim_probe.py on this pak and feed the result back)";
                Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
                return clips;
            }
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

        report.TranslationRunsFound = CountTranslationRuns(reader.Data, frameCount);

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

        // WHICH joint each track drives, resolved by matching the skeleton's bone-name hashes
        // against the pak. Mapping track j onto bone j instead would put the elbow's rotation
        // on the spine — motion that plays convincingly with everything in the wrong place.
        var jointMap = NdAnimJointMap.Resolve(reader.Data, skeleton, label);
        report.JointMapEvidence = jointMap.Evidence;

        for (int j = 0; j < jointCount; j++)
        {
            var run = group[j];

            int boneIndex = jointMap.Resolved && j < jointMap.TrackToBone.Length
                ? jointMap.TrackToBone[j]
                : -1;

            var track = new AnimTrack
            {
                // An unresolved track keeps a positional placeholder NAME so it is still
                // listed, but BoneIndex stays -1 so the sampler binds nothing and the
                // character holds its bind pose instead of moving wrongly.
                BoneName  = boneIndex >= 0 && skeleton != null
                    ? skeleton.Bones[boneIndex].Name
                    : $"joint_{j}",
                BoneIndex = boneIndex,
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
        int bound = clip.Tracks.Count(t => t.BoneIndex >= 0);
        clip.JointMappingResolved = bound > 0;

        report.Outcome = bound == 0
            ? $"decoded {jointCount} rotation tracks × {frameCount} frames, but could not work out " +
              $"which joint each drives ({jointMap.Evidence}). The rotations are left unattached " +
              "rather than mapped positionally — a wrong attribution plays as convincing motion " +
              "with every joint in the wrong place, which is harder to spot than no motion."
            : $"decoded {jointCount} rotation tracks × {frameCount} frames; {bound} attributed to " +
              $"named joints of '{skeleton?.SourceName}' via {jointMap.Evidence}" +
              (bound < jointCount ? $" ({jointCount - bound} track(s) unmatched)" : "");

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

    // ── Quantised rotation tracks ────────────────────────────────────────────

    /// <summary>One "smallest three" packing to try.</summary>
    private readonly record struct PackedFormat(string Name, int ByteStride, int ComponentBits);

    /// <summary>
    /// The packings joint animation is realistically quantised with. Each stores a 2-bit index
    /// naming the component that was dropped (always the largest, so the remaining three are
    /// each within ±1/√2) followed by three signed fixed-point components.
    /// </summary>
    private static readonly PackedFormat[] PackedFormats =
    {
        new("48-bit (3×15)", 6, 15),
        new("64-bit (3×20)", 8, 20),
        new("32-bit (3×10)", 4, 10),
    };

    /// <summary>
    /// Scan for continuous runs of packed quaternions, trying each candidate packing and
    /// keeping the one that yields the most track-like data. Returns runs whose samples are
    /// already decoded, so the caller treats them exactly like uncompressed ones.
    /// </summary>
    private static List<QuatRun> FindPackedRuns(byte[] data, out string formatName)
    {
        var best = new List<QuatRun>();
        formatName = "none";

        foreach (var fmt in PackedFormats)
        {
            if (data.Length > PackedScanByteCap)
                Log.Info($"NdAnimParser: packed scan ({fmt.Name}) covers the first " +
                         $"{PackedScanByteCap / (1 << 20)} MB of {data.Length / (1 << 20)} MB");
            var found = ScanPacked(data, fmt);
            // Prefer the packing that explains the most samples, not merely the most runs —
            // a format that misreads produces many short runs, not a few long ones.
            if (found.Sum(r => r.Count) > best.Sum(r => r.Count))
            {
                best = found;
                formatName = fmt.Name;
            }
        }

        return best;
    }

    /// <summary>
    /// How much of a pak the packed scan will sweep. Finding the packing only needs a
    /// representative slice, and sweeping every alignment of a several-hundred-megabyte anim
    /// pak three times over is not worth the wait. When the cap bites it is logged, never
    /// silently applied.
    /// </summary>
    private const int PackedScanByteCap = 64 << 20;

    private static List<QuatRun> ScanPacked(byte[] data, PackedFormat fmt)
    {
        var runs = new List<QuatRun>();
        int stride = fmt.ByteStride;
        int scanEnd = Math.Min(data.Length, PackedScanByteCap);
        int limit = scanEnd - stride * MinPackedSamples;
        if (limit <= 0) return runs;

        // Step by the stride so a run is only found at its true alignment; the outer walk
        // advances 4 bytes at a time so every plausible alignment is still visited.
        for (int start = 0; start < limit; start += 4)
        {
            int count = 0;
            double stepSum = 0;
            Quaternion prev = default;
            var samples = new List<Quaternion>();

            for (int k = 0; ; k++)
            {
                int o = start + k * stride;
                if (o + stride > data.Length) break;
                var q = UnpackSmallestThree(data, o, fmt);
                if (q is null) break;

                if (k > 0)
                {
                    double step = AngleBetween(prev, q.Value);
                    // A single discontinuity ends the run rather than averaging away.
                    if (step > PackedMaxMeanStep * 4) break;
                    stepSum += step;
                }
                prev = q.Value;
                samples.Add(q.Value);
                count++;
            }

            if (count >= MinPackedSamples && stepSum / (count - 1) <= PackedMaxMeanStep)
            {
                runs.Add(new QuatRun
                {
                    Offset   = start,
                    Count    = count,
                    MeanStep = stepSum / (count - 1),
                    Samples  = samples.ToArray(),
                });
                start += count * stride - 4;   // skip past what we just consumed
            }
        }

        return runs;
    }

    /// <summary>
    /// Decode one packed quaternion, or null when the bits cannot represent one (the three
    /// stored components summing past unit length is the giveaway, and it is what rules out
    /// most unrelated bytes).
    /// </summary>
    private static Quaternion? UnpackSmallestThree(byte[] d, int o, PackedFormat fmt)
    {
        if (o + fmt.ByteStride > d.Length) return null;

        ulong raw = 0;
        for (int i = 0; i < fmt.ByteStride; i++) raw |= (ulong)d[o + i] << (8 * i);

        int dropped = (int)(raw & 0x3);
        ulong mask = (1UL << fmt.ComponentBits) - 1;
        Span<float> comps = stackalloc float[3];
        int shift = 2;
        float sumSq = 0;

        for (int i = 0; i < 3; i++)
        {
            ulong v = (raw >> shift) & mask;
            shift += fmt.ComponentBits;
            float c = (float)((double)v / mask) * 2f - 1f;
            comps[i] = c;
            sumSq += c * c;
        }

        if (sumSq > 1f) return null;
        float missing = MathF.Sqrt(1f - sumSq);

        return dropped switch
        {
            0 => new Quaternion(missing,  comps[0], comps[1], comps[2]),
            1 => new Quaternion(comps[0], missing,  comps[1], comps[2]),
            2 => new Quaternion(comps[0], comps[1], missing,  comps[2]),
            _ => new Quaternion(comps[0], comps[1], comps[2], missing),
        };
    }

    // ── Translation tracks ───────────────────────────────────────────────────

    /// <summary>
    /// Count runs of float3s that behave like a translation track: finite, at a physically
    /// plausible magnitude for a character rig, and moving smoothly. Reported rather than
    /// decoded — until a clip's joint ordering is known, an unattributed position track cannot
    /// be assigned to a bone, and guessing would move the wrong joint.
    /// </summary>
    private static int CountTranslationRuns(byte[] data, int expectedFrames)
    {
        const float MaxCoord = 100f;      // metres; a character rig lives well inside this
        const float MaxStep  = 0.5f;      // metres between consecutive frames
        int runs = 0;
        int limit = data.Length - 12;

        for (int start = 0; start < limit; start += 4)
        {
            int count = 0;
            Vector3 prev = default;

            for (int k = 0; ; k++)
            {
                int o = start + k * 12;
                if (o + 12 > data.Length) break;
                float x = BitConverter.ToSingle(data, o);
                float y = BitConverter.ToSingle(data, o + 4);
                float z = BitConverter.ToSingle(data, o + 8);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) break;
                if (MathF.Abs(x) > MaxCoord || MathF.Abs(y) > MaxCoord || MathF.Abs(z) > MaxCoord) break;

                var v = new Vector3(x, y, z);
                if (k > 0 && (v - prev).Length() > MaxStep) break;
                prev = v;
                count++;
            }

            if (count >= Math.Max(MinTranslationSamples, Math.Min(expectedFrames, 64)))
            {
                runs++;
                start += count * 12 - 4;
            }
        }

        return runs;
    }

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
