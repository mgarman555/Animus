namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Recognises non-game entries that show up in launcher scans
/// (Steam runtimes, Social Club, Unreal Engine itself, etc.) so they
/// can be excluded from the library.
/// </summary>
public static class LauncherFilter
{
    // Substring matches against display name OR install directory leaf.
    // All comparisons case-insensitive.
    private static readonly string[] DenylistPatterns =
    {
        // Steam infrastructure
        "steam linux runtime",
        "steamworks common redistributables",
        "steam audio",
        "steamvr",
        "proton ",
        "steamworks shared",

        // Epic infrastructure / authoring tools
        "epic games launcher",
        "epic online services",
        "unreal engine",      // installed engine, not a game
        "twinmotion",
        "realitycapture",
        "fab ue plugin",      // Epic Fab marketplace UE plugin
        "fab plugin",

        // Rockstar
        "social club",
        "rockstar games launcher",
        "rockstar games social club",

        // Ubisoft
        "ubisoft connect",
        "ubisoft game launcher",

        // Steam apps that aren't games
        "wallpaper engine",
    };

    // Exact (case-insensitive) names that should be skipped — used for generic
    // registry-key names like Rockstar's "Launcher" entry.
    private static readonly HashSet<string> ExactDenylist =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher",
        "social club",
        "rgsc",
    };

    public static bool IsLauncherOrTool(string displayName, string gameDirectory)
    {
        var name = displayName?.Trim() ?? "";
        if (ExactDenylist.Contains(name)) return true;

        var nameLower = name.ToLowerInvariant();
        var leaf = Path.GetFileName(gameDirectory?.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "")
            .ToLowerInvariant();

        foreach (var pat in DenylistPatterns)
        {
            if (nameLower.Contains(pat) || leaf.Contains(pat))
                return true;
        }

        return false;
    }
}
