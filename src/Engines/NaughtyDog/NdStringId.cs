namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Naughty Dog's <c>StringId64</c> — the 64-bit name hash the engine uses in place of strings
/// for resource types, joint names and similar identifiers.
///
/// It is plain FNV-1a-64: basis <c>0xCBF29CE484222325</c>, prime <c>0x100000001B3</c>, over the
/// raw ASCII bytes of the name. Verified against every entry of the reference plugin's
/// published type-string table (JOINT_HIERARCHY, GEOMETRY_1, VRAM_DESC, ANIM_GROUP,
/// MATERIAL_TABLE_1, PAK_LOGIN_TABLE, TEXTURE_TABLE, SPAWNER_GROUP) — 8/8 exact.
///
/// This matters well beyond naming: it makes hashes in a pak <b>matchable back to names we
/// already know</b>. A 64-bit hash that equals the hash of a specific bone name is that bone
/// to any useful certainty, which turns "which joint does this track drive?" from a guess into
/// a lookup.
/// </summary>
public static class NdStringId
{
    private const ulong Basis = 0xCBF29CE484222325UL;
    private const ulong Prime = 0x100000001B3UL;

    public static ulong Hash(string name)
    {
        ulong h = Basis;
        foreach (char c in name)
        {
            h ^= (byte)c;
            h *= Prime;
        }
        return h;
    }

    /// <summary>Hash → name for a known set of names, for reversing hashes found in a pak.</summary>
    public static Dictionary<ulong, string> BuildTable(IEnumerable<string> names)
    {
        var map = new Dictionary<ulong, string>();
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n)) map.TryAdd(Hash(n), n);
        return map;
    }
}
