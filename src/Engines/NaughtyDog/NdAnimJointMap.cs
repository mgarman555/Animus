using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Works out WHICH joint each animation track drives.
///
/// This is the difference between motion that is merely present and motion that is correct.
/// A clip stores its tracks in the clip's own joint order, which is not the skeleton's global
/// joint order — clips routinely animate a subset. Mapping track <c>j</c> onto bone <c>j</c>
/// therefore lands the elbow's rotation on the spine: the character moves, convincingly, and
/// everything is in the wrong place.
///
/// The fix does not need the clip format to be understood. Naughty Dog identifies joints by
/// <see cref="NdStringId"/> (FNV-1a-64 of the name), and we already know every bone name from
/// <c>JOINT_HIERARCHY</c>. So hashing those names and looking for the resulting values in the
/// anim pak finds the clip's joint table directly, and the ORDER they appear in is the
/// attribution. A 64-bit hash matching a specific bone name is that bone: across a few MB of
/// candidate slots and a few hundred bones, the chance of even one coincidental hit is around
/// 1e-11.
///
/// When no such table is found the mapping is left unresolved rather than guessed — see
/// <see cref="Resolve"/>.
/// </summary>
public static class NdAnimJointMap
{
    /// <summary>Shortest run of consecutive joint hashes that counts as a real joint table.</summary>
    private const int MinRunLength = 6;

    /// <summary>Strides to try between consecutive hashes in a joint table.</summary>
    private static readonly int[] Strides = { 8, 16, 4 };

    public sealed class Result
    {
        /// <summary>Bone index per track, in the clip's track order. Empty when unresolved.</summary>
        public int[] TrackToBone { get; init; } = Array.Empty<int>();
        /// <summary>Bone name per track, parallel to <see cref="TrackToBone"/>.</summary>
        public string[] TrackNames { get; init; } = Array.Empty<string>();
        /// <summary>Absolute byte offset the joint table was found at (diagnostics).</summary>
        public int TableOffset { get; init; } = -1;
        /// <summary>Byte stride between hashes in that table.</summary>
        public int Stride { get; init; }
        /// <summary>How the table was found, for the log and the viewer's status line.</summary>
        public string Evidence { get; init; } = string.Empty;

        public bool Resolved => TrackToBone.Length > 0;
    }

    /// <summary>
    /// Find the clip's joint table by matching <see cref="NdStringId"/> hashes of the
    /// skeleton's bone names against the pak's bytes.
    ///
    /// Returns an unresolved result when nothing matches. Callers must NOT fall back to a
    /// positional mapping: a wrong attribution is indistinguishable from working animation
    /// until someone looks closely, which is worse than no animation at all.
    /// </summary>
    public static Result Resolve(byte[] data, SkeletonData? skeleton, string label)
    {
        if (skeleton is not { Bones.Count: > 0 } || data.Length < 64)
            return new Result { Evidence = "no skeleton to match joint hashes against" };

        // hash → bone index. Duplicate bone names keep the first index.
        var byHash = new Dictionary<ulong, int>(skeleton.Bones.Count);
        for (int i = 0; i < skeleton.Bones.Count; i++)
            byHash.TryAdd(NdStringId.Hash(skeleton.Bones[i].Name), i);

        Result? best = null;

        foreach (int stride in Strides)
        {
            // Walk every aligned start once; a run is a maximal sequence of slots, `stride`
            // apart, that all hash to a bone of this skeleton.
            for (int start = 0; start + 8 <= data.Length; start += 4)
            {
                if (!byHash.ContainsKey(BitConverter.ToUInt64(data, start))) continue;

                var bones = new List<int>();
                var seen  = new HashSet<int>();
                int at = start;
                while (at + 8 <= data.Length &&
                       byHash.TryGetValue(BitConverter.ToUInt64(data, at), out int bone))
                {
                    // A joint table names each joint once; a repeat means we have run off the
                    // end of the table into something else that happens to hash.
                    if (!seen.Add(bone)) break;
                    bones.Add(bone);
                    at += stride;
                }

                if (bones.Count < MinRunLength) continue;
                if (best != null && bones.Count <= best.TrackToBone.Length) continue;

                best = new Result
                {
                    TrackToBone = bones.ToArray(),
                    TrackNames  = bones.Select(b => skeleton.Bones[b].Name).ToArray(),
                    TableOffset = start,
                    Stride      = stride,
                    Evidence    = $"{bones.Count} consecutive joint-name hashes at 0x{start:X} " +
                                  $"(stride {stride}) matched bones of '{skeleton.SourceName}'",
                };

                // Skip past what we just consumed; overlapping starts cannot beat it.
                start = at;
            }
        }

        if (best == null)
        {
            Log.Info($"NdAnimJointMap[{label}]: no joint-name-hash table found " +
                     $"({skeleton.Bones.Count} bone names hashed and searched) — " +
                     "track attribution left unresolved rather than guessed");
            return new Result { Evidence = "no joint-name-hash table found in this pak" };
        }

        Log.Info($"NdAnimJointMap[{label}]: {best.Evidence}");
        return best;
    }

    /// <summary>
    /// Self-check: <c>JOINT_HIERARCHY</c>'s name table stores a u64 alongside each name pointer.
    /// If that u64 is the name's StringId64, hashing the names we already parsed reproduces it —
    /// which confirms both the hash function and that we are reading the right field, using only
    /// data already in hand. Returns how many of the skeleton's bones verified.
    /// </summary>
    public static int VerifyAgainstNameTable(byte[] data, int nameTableOffset, SkeletonData skeleton)
    {
        int matched = 0;
        for (int b = 0; b < skeleton.Bones.Count; b++)
        {
            int o = nameTableOffset + b * 16;
            if (o + 8 > data.Length) break;
            if (BitConverter.ToUInt64(data, o) == NdStringId.Hash(skeleton.Bones[b].Name)) matched++;
        }
        return matched;
    }
}
