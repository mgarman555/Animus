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
        public int    TranslationTracksBound{ get; set; }
        /// <summary>How the clip's joint table was located, or why it wasn't.</summary>
        public string JointMapEvidence      { get; set; } = "";

        /// <summary>One line per resource that yielded a clip, so a pak of many is legible.</summary>
        public List<string> ClipOutcomes { get; } = new();

        /// <summary>
        /// True when no resource yielded a clip from its own byte range and the parser fell
        /// back to one whole-pak sweep. Worth surfacing: the resulting clip is attributed to
        /// the first resource, which is a guess about naming that the per-resource path does
        /// not have to make.
        /// </summary>
        public bool WholeFileFallback { get; set; }

        public string Summarise() =>
            $"resources={Resources.Count} " +
            $"[{string.Join(", ", Resources.GroupBy(r => r.Type).Select(g => $"{g.Count()}×{g.Key}"))}] " +
            $"quatRuns={QuaternionRunsFound} longest={LongestRun} " +
            $"dominant={DominantRunCount}×{DominantRunLength} " +
            $"meanStep={(double.IsNaN(MeanAngularStep) ? "n/a" : MeanAngularStep.ToString("F5"))} " +
            $"packedRuns={PackedRunsFound}({PackedFormat}) " +
            $"transRuns={TranslationRunsFound} transBound={TranslationTracksBound} " +
            $"jointMap=[{JointMapEvidence}] " +
            $"layout={Layout} clips={ClipsDecoded}{(WholeFileFallback ? " (whole-pak fallback)" : "")} " +
            $"— {Outcome}";
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

        // Each resource owns the bytes from its own payload start up to the next resource's,
        // and is scanned on its own. Sweeping the whole pak in one pass instead — which is
        // what this did before — pools every clip's runs together, groups them by modal
        // length, and emits ONE clip holding a mixture of tracks from several: a clip that
        // does not exist in the game, with every other clip in the pak silently dropped.
        foreach (var (res, from, to) in ResourceSpans(report.Resources, reader.Data.Length))
        {
            var clip = DecodeSpan(reader.Data, res, from, to, skeleton, label, report);
            if (clip != null) clips.Add(clip);
        }

        if (clips.Count == 0)
        {
            // A resource can describe a clip whose payload does not sit inside its own span —
            // an ANIM_GROUP indexing data elsewhere in the pak is the case to expect. One
            // whole-pak sweep keeps those working instead of regressing them to nothing; the
            // clip is attributed to the first resource, and the report flags that it is a
            // fallback so the naming is not mistaken for something the parser established.
            var whole = DecodeSpan(reader.Data, report.Resources[0], 0, reader.Data.Length,
                                   skeleton, label, report);
            if (whole != null)
            {
                clips.Add(whole);
                report.WholeFileFallback = true;
            }
        }

        report.ClipsDecoded = clips.Count;

        if (clips.Count == 0)
        {
            report.Outcome = "no rotation tracks found in any resource, uncompressed or in the " +
                             "usual quantised packings — this pak's bit layout is not yet measured " +
                             "(run tools/nd_anim_probe.py on it and feed the result back)";
        }
        else
        {
            int attributed = clips.Count(c => c.JointMappingResolved);
            report.Outcome =
                $"decoded {clips.Count} clip(s) from {report.Resources.Count} resource(s); " +
                (attributed == clips.Count
                    ? $"all attributed to named joints of '{skeleton?.SourceName}'"
                    : attributed == 0
                        ? "none attributed to named joints — the rotations are left unattached " +
                          "rather than mapped positionally, because a wrong attribution plays as " +
                          "convincing motion with every joint in the wrong place, which is harder " +
                          "to spot than no motion"
                        : $"{attributed} attributed to named joints, {clips.Count - attributed} left " +
                          "unattached rather than mapped positionally") +
                (report.ClipOutcomes.Count > 0 ? " | " + string.Join(" | ", report.ClipOutcomes) : "");
        }

        Log.Info($"NdAnimParser[{label}]: {report.Summarise()}");
        return clips;
    }

    /// <summary>A span shorter than this cannot hold even one minimal track.</summary>
    private const int MinSpanBytes = 16 * MinTrackSamples;

    /// <summary>
    /// The byte range each resource's payload occupies: from its own payload start to the
    /// next resource's. Ranges too short to hold a track are dropped, which is what an
    /// ANIM_GROUP header sitting immediately before the clip it indexes looks like.
    /// </summary>
    private static List<(AnimResourceInfo Res, int From, int To)> ResourceSpans(
        List<AnimResourceInfo> resources, int dataLength)
    {
        var ordered = resources.OrderBy(r => r.DataStart).ToList();
        var spans   = new List<(AnimResourceInfo, int, int)>();

        for (int i = 0; i < ordered.Count; i++)
        {
            int from = Math.Clamp(ordered[i].DataStart, 0, dataLength);
            int to   = i + 1 < ordered.Count
                ? Math.Clamp(ordered[i + 1].DataStart, from, dataLength)
                : dataLength;

            if (to - from >= MinSpanBytes) spans.Add((ordered[i], from, to));
        }

        return spans;
    }

    /// <summary>
    /// Recover one clip from one resource's byte range, or null when the range holds no
    /// rotation tracks. Everything the scan concludes is accumulated into
    /// <paramref name="report"/> so a pak of many clips stays diagnosable.
    /// </summary>
    private static AnimationAssetData? DecodeSpan(
        byte[] data, AnimResourceInfo res, int from, int to,
        SkeletonData? skeleton, string label, DecodeReport report)
    {
        var runs = FindQuaternionRuns(data, from, to);
        report.QuaternionRunsFound += runs.Count;

        if (runs.Count == 0)
        {
            // Nothing stored as plain float quaternions. Before giving up, try the packing
            // joint animation is usually quantised with: a 2-bit index naming the dropped
            // (largest) component, then three signed fixed-point components. Those decode to
            // unit quaternions by construction, so smoothness across samples is the only
            // evidence — which is why the run has to be longer and steadier to count.
            runs = FindPackedRuns(data, from, to, out string packedFormat);
            report.PackedRunsFound += runs.Count;
            if (runs.Count > 0) report.PackedFormat = packedFormat;
            if (runs.Count == 0) return null;
        }

        report.LongestRun = Math.Max(report.LongestRun, runs.Max(r => r.Count));

        // Group by run length: a clip's tracks all share one length, so the modal length is
        // the clip's frame count (joint-major) or joint count (frame-major).
        var byLength = runs.GroupBy(r => r.Count)
                           .OrderByDescending(g => g.Count())
                           .ThenByDescending(g => g.Key)
                           .First();

        // Byte ranges the rotation runs occupy, captured before any transpose. The translation
        // scan skips them: a stream of float4 quaternions re-read as float3s yields small
        // finite values that step smoothly, so without this the rotation data would be
        // rediscovered as bogus position tracks.
        var rotationBytes = byLength.Select(r => (Start: r.Offset, End: r.Offset + r.ByteLength))
                                    .OrderBy(x => x.Start)
                                    .ToList();

        var group = byLength.OrderBy(r => r.Offset).ToList();
        double meanStep = group.Average(r => r.MeanStep);
        bool jointMajor = meanStep <= JointMajorMaxMeanStep;

        if (!jointMajor)
        {
            // Frame-major: sample j of run f is joint j at frame f. Transpose into tracks.
            group = Transpose(group, data);
            if (group.Count == 0) return null;
        }

        int jointCount = group.Count;
        int frameCount = jointMajor ? byLength.Key : byLength.Count();
        if (frameCount < MinTrackSamples) return null;

        // The single-valued report fields describe the biggest clip in the pak, so the
        // viewer's status line stays meaningful when there are many.
        if (jointCount * frameCount > report.DominantRunCount * report.DominantRunLength)
        {
            report.DominantRunLength = byLength.Key;
            report.DominantRunCount  = byLength.Count();
            report.MeanAngularStep   = meanStep;
            report.Layout = jointMajor
                ? "joint-major (one run per joint)"
                : "frame-major (one run per frame)";
        }

        // WHICH joint each track drives, resolved by matching the skeleton's bone-name hashes
        // against THIS resource's bytes. Mapping track j onto bone j instead would put the
        // elbow's rotation on the spine — motion that plays convincingly with everything in
        // the wrong place. Resolving within the span also stops a pak of many clips from
        // handing every clip the first one's joint table.
        var jointMap = NdAnimJointMap.Resolve(data, from, to, skeleton, $"{label}:{res.Name}");
        if (jointMap.Resolved || string.IsNullOrEmpty(report.JointMapEvidence))
            report.JointMapEvidence = jointMap.Evidence;

        var clip = new AnimationAssetData
        {
            Info       = new AssetInfo { Name = res.Name, Type = AssetType.Animation },
            ClipName   = res.Name,
            FrameRate  = ProbeFrameRate(data, res.DataStart),
            FrameCount = frameCount,
        };

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
                    : ReadQuat(data, run.Offset + f * 16);
                track.RotationKeys.Add(new[] { q.X, q.Y, q.Z, q.W });
            }
            clip.Tracks.Add(track);
            clip.JointNames.Add(track.BoneName);
        }

        int bound = clip.Tracks.Count(t => t.BoneIndex >= 0);
        clip.JointMappingResolved = bound > 0;

        int moved = AttachTranslations(data, from, to, clip, frameCount, rotationBytes,
                                       jointMap, report);

        report.ClipOutcomes.Add(
            $"'{res.Name}' {jointCount}×{frameCount}" +
            (bound == jointCount ? "" : $" ({bound} attributed)") +
            (moved > 0 ? $" +{moved} pos" : ""));

        return clip;
    }

    // ── Track scanning ───────────────────────────────────────────────────────

    private sealed class QuatRun
    {
        public int Offset;
        public int Count;
        /// <summary>Bytes the run occupies, so the translation scan can skip over it.</summary>
        public int ByteLength;
        public double MeanStep;
        public Quaternion[]? Samples;   // set only for synthesised (transposed) runs
    }

    /// <summary>
    /// Every maximal 4-byte-aligned run of unit-length float4s in <c>[from, to)</c>. Runs
    /// shorter than <see cref="MinTrackSamples"/> are dropped — four consecutive unit
    /// quaternions arising by chance from unrelated bytes is vanishingly unlikely, which is
    /// what makes this a reliable detector rather than a heuristic.
    /// </summary>
    private static List<QuatRun> FindQuaternionRuns(byte[] data, int from, int to)
    {
        var runs = new List<QuatRun>();
        int limit = Math.Min(to, data.Length) - 16;
        int i = Math.Max(from, 0);

        while (i <= limit)
        {
            if (!IsUnitQuat(data, i)) { i += 4; continue; }

            int start = i;
            int count = 0;
            while (i <= limit && IsUnitQuat(data, i)) { count++; i += 16; }

            if (count >= MinTrackSamples)
                runs.Add(new QuatRun
                {
                    Offset     = start,
                    Count      = count,
                    ByteLength = count * 16,
                    MeanStep   = MeanAngularStep(data, start, count),
                });
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
    private static List<QuatRun> FindPackedRuns(byte[] data, int from, int to, out string formatName)
    {
        var best = new List<QuatRun>();
        formatName = "none";

        foreach (var fmt in PackedFormats)
        {
            if (to - from > PackedScanByteCap)
                Log.Info($"NdAnimParser: packed scan ({fmt.Name}) covers the first " +
                         $"{PackedScanByteCap / (1 << 20)} MB of this {(to - from) / (1 << 20)} MB span");
            var found = ScanPacked(data, fmt, from, to);
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
    /// How much of a span the packed scan will sweep. Finding the packing only needs a
    /// representative slice, and sweeping every alignment of a several-hundred-megabyte anim
    /// pak three times over is not worth the wait. When the cap bites it is logged, never
    /// silently applied.
    /// </summary>
    private const int PackedScanByteCap = 64 << 20;

    private static List<QuatRun> ScanPacked(byte[] data, PackedFormat fmt, int from, int to)
    {
        var runs = new List<QuatRun>();
        int stride = fmt.ByteStride;
        int begin = Math.Max(from, 0);
        int scanEnd = Math.Min(Math.Min(to, data.Length), begin + PackedScanByteCap);
        int limit = scanEnd - stride * MinPackedSamples;
        if (limit <= begin) return runs;

        // Step by the stride so a run is only found at its true alignment; the outer walk
        // advances 4 bytes at a time so every plausible alignment is still visited.
        for (int start = begin; start < limit; start += 4)
        {
            int count = 0;
            double stepSum = 0;
            Quaternion prev = default;
            var samples = new List<Quaternion>();

            for (int k = 0; ; k++)
            {
                int o = start + k * stride;
                if (o + stride > scanEnd) break;
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
                    Offset     = start,
                    Count      = count,
                    ByteLength = count * stride,
                    MeanStep   = stepSum / (count - 1),
                    Samples    = samples.ToArray(),
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
    /// Recover this clip's position tracks and attach them to the joints they drive; returns
    /// how many were attached.
    ///
    /// <para>Counting these without decoding them used to be the only honest option: an
    /// unattributed position track cannot be assigned to a bone, and guessing slides the wrong
    /// joint. <see cref="NdAnimJointMap"/> now establishes the clip's joint ORDER from name
    /// hashes, which makes two cases attributable without guessing:</para>
    ///
    /// <list type="bullet">
    /// <item>as many position runs as rotation tracks — the clip stores both per joint in the
    /// same order, so run <c>j</c> drives whatever bone rotation track <c>j</c> drives;</item>
    /// <item>exactly one — root motion, driving the joint the clip's table names first.</item>
    /// </list>
    ///
    /// <para>Any other count is counted, reported and left unattached. A partial subset carries
    /// nothing that says WHICH joints it covers, and attaching it in order would translate the
    /// wrong bones — which reads as a character sliding along the floor, sourced from a joint
    /// that never moved.</para>
    /// </summary>
    private static int AttachTranslations(
        byte[] data, int from, int to, AnimationAssetData clip, int frameCount,
        List<(int Start, int End)> rotationBytes, NdAnimJointMap.Result jointMap,
        DecodeReport report)
    {
        // A handful of samples that happen to drift smoothly is not evidence of a track.
        if (frameCount < MinTranslationSamples) return 0;

        var runs = FindTranslationRuns(data, from, to, frameCount, rotationBytes);
        report.TranslationRunsFound += runs.Count;

        if (runs.Count == 0 || !jointMap.Resolved) return 0;

        int attached = 0;

        if (runs.Count == clip.Tracks.Count)
        {
            for (int j = 0; j < runs.Count; j++)
            {
                if (clip.Tracks[j].BoneIndex < 0) continue;
                AddPositionKeys(clip.Tracks[j], runs[j].Samples);
                attached++;
            }
        }
        else if (runs.Count == 1 && clip.Tracks.Count > 0 && clip.Tracks[0].BoneIndex >= 0)
        {
            // Root motion. Track 0 is the joint the clip's own table names first, which is the
            // root in ND rigs — the same ordering the rotation attribution already relies on.
            AddPositionKeys(clip.Tracks[0], runs[0].Samples);
            attached = 1;
        }

        report.TranslationTracksBound += attached;
        return attached;
    }

    private static void AddPositionKeys(AnimTrack track, Vector3[] samples)
    {
        foreach (var v in samples) track.PositionKeys.Add(new[] { v.X, v.Y, v.Z });
    }

    private sealed class VecRun
    {
        public int Offset;
        public Vector3[] Samples = Array.Empty<Vector3>();
    }

    /// <summary>
    /// Runs of float3s that behave like a position track: finite, at a physically plausible
    /// magnitude for a character rig, and moving smoothly. A track for THIS clip holds exactly
    /// one sample per frame, so only runs of exactly <paramref name="frameCount"/> count —
    /// which is both the right constraint and a free check on the frame count itself.
    /// </summary>
    private static List<VecRun> FindTranslationRuns(
        byte[] data, int from, int to, int frameCount, List<(int Start, int End)> rotationBytes)
    {
        const float MaxCoord = 100f;      // metres; a character rig lives well inside this
        const float MaxStep  = 0.5f;      // metres between consecutive frames

        var runs  = new List<VecRun>();
        int begin = Math.Max(from, 0);
        int end   = Math.Min(to, data.Length);
        int limit = end - 12;

        for (int start = begin; start <= limit; start += 4)
        {
            if (InAnyRange(rotationBytes, start)) continue;

            var samples = new List<Vector3>();
            Vector3 prev = default;
            bool endedOnContent = false;   // the run stopped because the DATA stopped qualifying

            for (int k = 0; ; k++)
            {
                int o = start + k * 12;
                if (o + 12 > end) break;                              // ran out of span
                if (InAnyRange(rotationBytes, o)) { endedOnContent = true; break; }

                float x = BitConverter.ToSingle(data, o);
                float y = BitConverter.ToSingle(data, o + 4);
                float z = BitConverter.ToSingle(data, o + 8);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { endedOnContent = true; break; }
                if (MathF.Abs(x) > MaxCoord || MathF.Abs(y) > MaxCoord || MathF.Abs(z) > MaxCoord) { endedOnContent = true; break; }

                var v = new Vector3(x, y, z);
                if (k > 0 && (v - prev).Length() > MaxStep) { endedOnContent = true; break; }
                prev = v;
                samples.Add(v);

                // No point reading past a full clip's worth: a longer run is not this track.
                if (samples.Count > frameCount) { endedOnContent = true; break; }
            }

            // Two ways a span's unused tail imitates a track, both rejected here.
            //
            // Zero-filled padding is finite, in range and perfectly smooth, so it satisfies
            // every test above. A run that never moves is therefore not accepted: it cannot be
            // told apart from padding, and attaching it would change no pose anyway, since a
            // joint that holds still is already where the bind pose put it.
            //
            // And a run is only a track if it stopped because the DATA stopped qualifying.
            // One that merely hit the end of the span is an artifact of where the span happens
            // to end — a tail of padding leaves exactly one clip's worth before the boundary
            // often enough to matter, and the neighbouring resource's header supplies just
            // enough variation to get it past the constant test.
            if (samples.Count == frameCount && endedOnContent && !IsConstant(samples))
            {
                runs.Add(new VecRun { Offset = start, Samples = samples.ToArray() });
                start += samples.Count * 12 - 4;
            }
        }

        return runs;
    }

    /// <summary>Does this run hold the same position at every frame?</summary>
    private static bool IsConstant(List<Vector3> samples)
    {
        for (int i = 1; i < samples.Count; i++)
            if (samples[i] != samples[0]) return false;
        return true;
    }

    /// <summary>Is <paramref name="pos"/> inside one of these sorted, non-overlapping ranges?</summary>
    private static bool InAnyRange(List<(int Start, int End)> ranges, int pos)
    {
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (pos < ranges[mid].Start) hi = mid - 1;
            else if (pos >= ranges[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
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
