using GameAssetExplorer.Core.Models;
using Microsoft.Win32;
using System.Text.Json;

namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Scans installed game launchers (Steam, Epic, Ubisoft, Rockstar) and returns
/// a GameConfig for each discovered game. Engine is auto-detected from the game directory.
/// </summary>
public static class GameScanner
{
    public static async Task<List<GameConfig>> ScanAllAsync()
    {
        var tasks = new[]
        {
            Task.Run(ScanSteam),
            Task.Run(ScanEpic),
            Task.Run(ScanUbisoft),
            Task.Run(ScanRockstar),
        };

        await Task.WhenAll(tasks);

        return tasks.SelectMany(t => t.Result).ToList();
    }

    // ── Steam ────────────────────────────────────────────────────────────────

    private static List<GameConfig> ScanSteam()
    {
        var games = new List<GameConfig>();

        var steamRoot = ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ??
            ReadRegistry(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath");

        if (steamRoot == null || !Directory.Exists(steamRoot)) return games;

        var libraryPaths = new List<string> { Path.Combine(steamRoot, "steamapps") };

        // libraryfolders.vdf lists extra Steam library locations
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            foreach (var line in SafeReadLines(vdfPath))
            {
                var t = line.Trim();
                if (t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    var path = VdfValue(t);
                    if (path != null)
                        libraryPaths.Add(Path.Combine(path, "steamapps"));
                }
            }
        }

        foreach (var libPath in libraryPaths)
        {
            if (!Directory.Exists(libPath)) continue;

            foreach (var acf in SafeGlob(libPath, "appmanifest_*.acf"))
            {
                var (name, installDir, appId) = ParseAcf(acf);
                if (name == null || installDir == null) continue;

                var fullPath = Path.Combine(libPath, "common", installDir);
                if (!Directory.Exists(fullPath)) continue;
                if (LauncherFilter.IsLauncherOrTool(name, fullPath)) continue;

                var (engineId, engineVersion) = EngineDetector.Detect(fullPath);
                var game = Make(name, fullPath, engineId, engineVersion, "Steam");
                if (appId != null) game.EngineSpecificSettings["SteamAppId"] = appId;
                games.Add(game);
            }
        }

        return games;
    }

    // ── Epic Games Launcher ──────────────────────────────────────────────────

    private static List<GameConfig> ScanEpic()
    {
        var games = new List<GameConfig>();

        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestDir)) return games;

        foreach (var item in SafeGlob(manifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;

                if (!root.TryGetProperty("DisplayName", out var nameProp)) continue;
                if (!root.TryGetProperty("InstallLocation", out var pathProp)) continue;

                var name = nameProp.GetString();
                var path = pathProp.GetString();
                if (name == null || path == null || !Directory.Exists(path)) continue;
                if (LauncherFilter.IsLauncherOrTool(name, path)) continue;

                var (engineId, engineVersion) = EngineDetector.Detect(path);
                games.Add(Make(name, path, engineId, engineVersion, "Epic"));
            }
            catch { }
        }

        return games;
    }

    // ── Ubisoft Connect ──────────────────────────────────────────────────────

    private static List<GameConfig> ScanUbisoft()
    {
        var games = new List<GameConfig>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            if (root == null) return games;

            foreach (var appId in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(appId);
                    var installDir = sub?.GetValue("InstallDir") as string;
                    if (installDir == null || !Directory.Exists(installDir)) continue;

                    // Ubisoft doesn't store display names in this key — use the folder name
                    var name = Path.GetFileName(installDir.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (LauncherFilter.IsLauncherOrTool(name, installDir)) continue;

                    var (engineId, engineVersion) = EngineDetector.Detect(installDir);
                    games.Add(Make(name, installDir, engineId, engineVersion, "Ubisoft"));
                }
                catch { }
            }
        }
        catch { }

        return games;
    }

    // ── Rockstar Games Launcher ──────────────────────────────────────────────

    private static List<GameConfig> ScanRockstar()
    {
        var games = new List<GameConfig>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Rockstar Games");
            if (root == null) return games;

            foreach (var gameName in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(gameName);
                    // Rockstar stores the path under InstallFolder
                    var installFolder = sub?.GetValue("InstallFolder") as string;
                    if (installFolder == null || !Directory.Exists(installFolder)) continue;
                    if (LauncherFilter.IsLauncherOrTool(gameName, installFolder)) continue;

                    var (engineId, engineVersion) = EngineDetector.Detect(installFolder);
                    games.Add(Make(gameName, installFolder, engineId, engineVersion, "Rockstar"));
                }
                catch { }
            }
        }
        catch { }

        return games;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GameConfig Make(
        string name, string dir, string engineId, string engineVersion, string source) =>
        new()
        {
            DisplayName    = name,
            GameDirectory  = dir,
            EngineId       = engineId,
            EngineVersion  = engineVersion,
            Source         = source,
        };

    private static string? ReadRegistry(string keyPath, string valueName)
    {
        try { return Registry.GetValue(keyPath, valueName, null) as string; }
        catch { return null; }
    }

    // Reads a VDF key-value line like:  "path"    "C:\\SteamLibrary"
    // Returns the value string, or null if the line doesn't match.
    private static string? VdfValue(string line)
    {
        var parts = line.Split('"');
        if (parts.Length < 4) return null;
        return parts[3].Replace("\\\\", "\\");
    }

    private static (string? name, string? dir, string? appId) ParseAcf(string path)
    {
        string? name = null, dir = null, appId = null;
        foreach (var line in SafeReadLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith("\"name\"", StringComparison.OrdinalIgnoreCase))
                name ??= VdfValue(t);
            else if (t.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                dir ??= VdfValue(t);
            else if (t.StartsWith("\"appid\"", StringComparison.OrdinalIgnoreCase))
                appId ??= VdfValue(t);
            if (name != null && dir != null && appId != null) break;
        }
        return (name, dir, appId);
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeGlob(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return []; }
    }
}
