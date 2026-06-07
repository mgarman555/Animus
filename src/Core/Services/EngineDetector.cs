namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Inspects a game directory and returns an engine ID + version string.
/// For plugin-backed engines (UE, RAGE, ND, SOTR) returns the plugin's EngineId.
/// For everything else returns a human-readable label (Frostbite, Unity, etc.).
/// </summary>
public static class EngineDetector
{
    public static (string id, string version) Detect(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return ("Unknown", "");

        try
        {
            // RAGE: .rpf archives in root or one level deep (GTA5, RDR2)
            if (HasFiles(gameDir, "*.rpf", maxDepth: 2))
                return ("RAGE", "");

            // NaughtyDog: .psarc archives
            if (HasFiles(gameDir, "*.psarc", maxDepth: 2))
                return ("ND", "");

            // Unreal Engine: find a Paks directory containing .pak files
            var paksDir = FindDir(gameDir, "Paks", maxDepth: 4);
            if (paksDir != null && HasFiles(paksDir, "*.pak", maxDepth: 1))
            {
                bool isUE5 = HasFiles(paksDir, "*.utoc", maxDepth: 1);
                return ("UE", isUE5 ? "5" : "4");
            }

            // Foundation Engine (SOTR): .tiger files
            if (HasFiles(gameDir, "*.tiger", maxDepth: 2))
                return ("SOTR", "");

            // Frostbite: Data/*.cas or cas.cat in root
            var dataDir = Path.Combine(gameDir, "Data");
            if (File.Exists(Path.Combine(gameDir, "cas.cat")) ||
                (Directory.Exists(dataDir) && HasFiles(dataDir, "*.cas", maxDepth: 1)))
                return ("Frostbite", "");

            // Unity: UnityPlayer.dll
            if (File.Exists(Path.Combine(gameDir, "UnityPlayer.dll")))
                return ("Unity", "");

            // Source Engine: .vpk files
            if (HasFiles(gameDir, "*.vpk", maxDepth: 2))
                return ("Source Engine", "");

            // CryEngine
            if (File.Exists(Path.Combine(gameDir, "Bin64", "CrySystem.dll")) ||
                File.Exists(Path.Combine(gameDir, "Bin32", "CrySystem.dll")))
                return ("CryEngine", "");

            // id Tech: .pk4 / .pk3 files
            if (HasFiles(gameDir, "*.pk4", maxDepth: 2) ||
                HasFiles(gameDir, "*.pk3", maxDepth: 2))
                return ("id Tech", "");
        }
        catch { /* directory access errors are non-fatal */ }

        return ("Unknown", "");
    }

    // Returns true if at least one file matching the pattern exists within maxDepth levels.
    // Stops as soon as it finds the first match — does not traverse the whole tree.
    private static bool HasFiles(string dir, string pattern, int maxDepth)
    {
        if (!Directory.Exists(dir) || maxDepth <= 0) return false;

        if (Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any())
            return true;

        if (maxDepth > 1)
        {
            foreach (var sub in SafeEnumDirs(dir))
                if (HasFiles(sub, pattern, maxDepth - 1))
                    return true;
        }

        return false;
    }

    // BFS find of the first directory with a matching name, up to maxDepth levels deep.
    private static string? FindDir(string root, string name, int maxDepth)
    {
        if (maxDepth <= 0) return null;

        foreach (var sub in SafeEnumDirs(root))
        {
            if (string.Equals(Path.GetFileName(sub), name, StringComparison.OrdinalIgnoreCase))
                return sub;

            var found = FindDir(sub, name, maxDepth - 1);
            if (found != null) return found;
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }
}
