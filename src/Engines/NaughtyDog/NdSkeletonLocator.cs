using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Finds the skeleton that belongs to a TLOU2 character pak.
///
/// Character actors are split across several paks — <c>ellie-head.pak</c>,
/// <c>ellie-body.pak</c>, <c>ellie-arms.pak</c> — and none of them carries a
/// <c>JOINT_HIERARCHY</c>. The joints live once, in a shared
/// <c>&lt;world&gt;/actor97/&lt;name&gt;-skel.pak</c>, and every part pak's skin table indexes
/// into it. Without resolving that link a part pak has weights pointing at bones that do not
/// exist locally, which is exactly the state this project was in before.
///
/// Resolution order:
///   1. the pak's own JOINT_HIERARCHY, if it has one;
///   2. progressively shorter name stems — <c>ellie-head</c> → <c>ellie-head-skel.pak</c> →
///      <c>ellie-skel.pak</c> — preferring a match in the same directory, then the same
///      actor folder, then anywhere in the mounted game;
///   3. any skeleton whose bone count covers the mesh's highest referenced bone index,
///      ranked by how much of the name it shares.
///
/// NPCs work the same way; they just resolve to a shared rig (<c>base-male-skel.pak</c>,
/// <c>base-female-skel.pak</c>, <c>base-teen-skel.pak</c>, …) through step 3.
/// </summary>
public sealed class NdSkeletonLocator
{
    private const string SkelSuffix = "-skel";

    private readonly IReadOnlyList<NdPakEntry> _index;
    private readonly Dictionary<string, SkeletonData?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public NdSkeletonLocator(IReadOnlyList<NdPakEntry> pakIndex) => _index = pakIndex;

    /// <summary>Every <c>*-skel.pak</c> in the mounted game, for the viewer's manual override list.</summary>
    public IReadOnlyList<NdPakEntry> AllSkeletonPaks =>
        _index.Where(e => IsSkeletonPak(e.VirtualPath)).OrderBy(e => e.VirtualPath, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Load and parse a specific skeleton pak by file path (used by the manual override).</summary>
    public SkeletonData? LoadFrom(string filePath, MeshAssetData? mesh = null)
    {
        if (_cache.TryGetValue(filePath, out var cached)) return cached;

        SkeletonData? skel = null;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var reader = new NdPakReader(bytes);
            if (reader.ReadHeader() && reader.JointEntry != null)
                skel = NdSkeletonParser.TryParse(reader, Path.GetFileNameWithoutExtension(filePath), mesh);
        }
        catch (Exception ex)
        {
            Log.Warn($"NdSkeletonLocator: could not read {Path.GetFileName(filePath)} — {ex.Message}");
        }

        _cache[filePath] = skel;
        return skel;
    }

    /// <summary>
    /// Resolve the skeleton for <paramref name="asset"/>. <paramref name="ownReader"/> is the
    /// already-open reader for the asset's own pak, so a self-contained skeleton costs nothing
    /// extra. Returns null when nothing plausible was found.
    /// </summary>
    public SkeletonData? Resolve(AssetInfo asset, NdPakReader? ownReader, MeshAssetData? mesh)
    {
        // 1 ─ the pak's own joints
        if (ownReader?.JointEntry != null)
        {
            var own = NdSkeletonParser.TryParse(ownReader, asset.Name, mesh);
            if (own is { Bones.Count: > 0 })
            {
                Log.Info($"NdSkeletonLocator[{asset.Name}]: using the pak's own JOINT_HIERARCHY ({own.Bones.Count} bones)");
                return own;
            }
        }

        int needBones = MaxBoneIndex(mesh) + 1;
        if (needBones <= 0 && ownReader?.JointEntry == null)
        {
            // Nothing in the mesh references a bone, so there is nothing to resolve.
            return null;
        }

        string assetDir = DirectoryOf(asset.VirtualPath);
        var candidates  = AllSkeletonPaks;
        if (candidates.Count == 0)
        {
            Log.Info($"NdSkeletonLocator[{asset.Name}]: mesh needs {needBones} bones but no *-skel.pak is mounted");
            return null;
        }

        // 2 ─ name stems, longest first
        foreach (string stem in NameStems(asset.Name))
        {
            string wanted = stem + SkelSuffix;
            var hit = candidates
                .Where(c => StemOf(c.VirtualPath).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => DirectoryOf(c.VirtualPath).Equals(assetDir, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.VirtualPath.Length)
                .FirstOrDefault();

            if (hit == null) continue;

            var skel = LoadFrom(hit.FilePath, mesh);
            if (skel is { Bones.Count: > 0 } && skel.Bones.Count >= needBones)
            {
                skel.SourceName = Path.GetFileName(hit.VirtualPath);
                Log.Info($"NdSkeletonLocator[{asset.Name}]: matched '{hit.VirtualPath}' by name stem '{stem}' ({skel.Bones.Count} bones)");
                return skel;
            }
            if (skel is { Bones.Count: > 0 })
                Log.Info($"NdSkeletonLocator[{asset.Name}]: '{hit.VirtualPath}' has only {skel.Bones.Count} bones, mesh needs {needBones} — keeping looking");
        }

        // 3 ─ any rig big enough, best name overlap wins (this is the NPC path)
        var ranked = candidates
            .Select(c => (Entry: c, Shared: SharedPrefixTokens(asset.Name, StemOf(c.VirtualPath))))
            .OrderByDescending(x => x.Shared)
            .ThenBy(x => DirectoryOf(x.Entry.VirtualPath).Equals(assetDir, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Entry.VirtualPath.Length)
            .Take(24);

        foreach (var (entry, shared) in ranked)
        {
            var skel = LoadFrom(entry.FilePath, mesh);
            if (skel is not { Bones.Count: > 0 } || skel.Bones.Count < needBones) continue;

            skel.SourceName = Path.GetFileName(entry.VirtualPath);
            Log.Info($"NdSkeletonLocator[{asset.Name}]: fell back to '{entry.VirtualPath}' " +
                     $"({skel.Bones.Count} bones ≥ {needBones} needed, {shared} shared name token(s))");
            return skel;
        }

        Log.Warn($"NdSkeletonLocator[{asset.Name}]: no skeleton found covering {needBones} bones " +
                 $"(searched {candidates.Count} *-skel.pak files)");
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int MaxBoneIndex(MeshAssetData? mesh)
    {
        if (mesh == null) return -1;
        int max = -1;
        foreach (var lod in mesh.Lods)
            if (lod.Skin is { } s && s.MaxBoneIndex > max) max = s.MaxBoneIndex;
        return max;
    }

    private static bool IsSkeletonPak(string virtualPath)
    {
        string stem = StemOf(virtualPath);
        // "-skel" and the "-skel-t2" / "-skel-l" / "-skel-r" variants seen on animals and vehicles.
        int i = stem.IndexOf(SkelSuffix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        int after = i + SkelSuffix.Length;
        return after == stem.Length || stem[after] == '-';
    }

    private static string StemOf(string virtualPath)
    {
        string name = virtualPath.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    private static string DirectoryOf(string virtualPath)
    {
        string p = virtualPath.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : string.Empty;
    }

    /// <summary>"abby-prisoner-hair" → "abby-prisoner-hair", "abby-prisoner", "abby".</summary>
    private static IEnumerable<string> NameStems(string assetName)
    {
        var parts = assetName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (int keep = parts.Length; keep >= 1; keep--)
            yield return string.Join('-', parts.Take(keep));
    }

    /// <summary>How many leading '-'-separated tokens two names share.</summary>
    private static int SharedPrefixTokens(string a, string b)
    {
        var pa = a.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var pb = b.Split('-', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        while (n < pa.Length && n < pb.Length && pa[n].Equals(pb[n], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }
}
