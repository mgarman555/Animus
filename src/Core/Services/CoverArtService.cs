using GameAssetExplorer.Core.Models;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Downloads game cover art from Steam's CDN and caches it locally.
/// Works via two strategies:
///   1. Name-based match against a built-in dictionary of known Steam App IDs.
///   2. Steam library path detection — finds the .acf file next to the game directory.
/// Downloaded images are saved to %AppData%\GameAssetExplorer\covers\{gameId}.jpg
/// </summary>
public class CoverArtService
{
    private readonly string _cacheDir;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Steam library poster: portrait 600×900 — proper "game library tile" art, fits portrait cards
    private const string SteamLibraryPosterUrl = "https://cdn.akamai.steamstatic.com/steam/apps/{0}/library_600x900.jpg";
    // Fallback for older games that don't have a library poster — landscape 460×215
    private const string SteamHeaderUrl = "https://cdn.akamai.steamstatic.com/steam/apps/{0}/header.jpg";

    /// <summary>
    /// Hardcoded Steam App IDs for popular games supported by the tool.
    /// Keys are lower-case, comparison is OrdinalIgnoreCase.
    /// </summary>
    private static readonly Dictionary<string, int> KnownIds =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Naughty Dog ──────────────────────────────────────────────────
        ["the last of us part i"]               = 1888930,
        ["the last of us part 1"]               = 1888930,
        ["the last of us part i remastered"]    = 1888930,
        ["tlou1"]                               = 1888930,
        ["tlou part i"]                         = 1888930,
        ["tlou part 1"]                         = 1888930,
        ["the last of us part ii"]              = 2531310,
        ["the last of us part 2"]               = 2531310,
        ["the last of us part ii remastered"]   = 2531310,
        ["tlou2"]                               = 2531310,
        ["tlou part ii"]                        = 2531310,
        ["tlou part 2"]                         = 2531310,
        ["the last of us 2"]                    = 2531310,
        ["the last of us ii"]                   = 2531310,
        ["uncharted 4: a thief's end"]          = 1659420,
        ["uncharted 4"]                         = 1659420,
        ["uncharted legacy of thieves"]         = 1659420,
        ["uncharted: legacy of thieves collection"] = 1659420,
        // ── Rockstar ─────────────────────────────────────────────────────
        ["grand theft auto v"]                  = 271590,
        ["grand theft auto 5"]                  = 271590,
        ["gta v"]                               = 271590,
        ["gta5"]                                = 271590,
        ["gta 5"]                               = 271590,
        ["red dead redemption 2"]               = 1174180,
        ["rdr2"]                                = 1174180,
        ["rdr 2"]                               = 1174180,
        // ── EA / Respawn ─────────────────────────────────────────────────
        ["star wars jedi: survivor"]            = 1701240,
        ["star wars jedi survivor"]             = 1701240,
        ["jedi survivor"]                       = 1701240,
        ["star wars jedi: fallen order"]        = 1172380,
        ["star wars jedi fallen order"]         = 1172380,
        ["jedi fallen order"]                   = 1172380,
        // ── Various UE titles ─────────────────────────────────────────────
        ["hogwarts legacy"]                     = 990080,
        ["cyberpunk 2077"]                      = 1091500,
        ["elden ring"]                          = 1245620,
        ["batman: arkham knight"]               = 208650,
        ["batman arkham knight"]                = 208650,
        ["fortnite"]                            = 1677740,
        ["marvel's spider-man remastered"]      = 1817070,
        ["spider-man remastered"]               = 1817070,
        ["spider-man: miles morales"]           = 1817070,
        ["spider-man miles morales"]            = 1817070,
        ["god of war"]                          = 1593500,
        ["god of war ragnarök"]                 = 2322010,
        ["god of war ragnarok"]                 = 2322010,
        ["horizon zero dawn"]                   = 1151640,
        ["horizon forbidden west"]              = 2420110,
        ["horizon forbidden west complete edition"] = 2420110,
        ["remnant ii"]                          = 1477830,
        ["remnant 2"]                           = 1477830,
        ["ark: survival evolved"]               = 346110,
        ["ark survival evolved"]                = 346110,
        ["ark: survival ascended"]              = 2399830,
        ["ark survival ascended"]               = 2399830,
        ["atlas fallen"]                        = 1541640,
        ["lies of p"]                           = 1627720,
        ["mortal kombat 1"]                     = 1971870,
        ["tekken 8"]                            = 1778820,
        ["street fighter 6"]                    = 1826630,
        ["atomic heart"]                        = 668580,
        ["the lord of the rings: gollum"]       = 1336960,
        ["lords of the fallen"]                 = 1501750,
        ["alan wake 2"]                         = 2050650,
        ["alan wake ii"]                        = 2050650,
        ["immortals of aveum"]                  = 1888160,
        ["black myth: wukong"]                  = 2358720,
        ["black myth wukong"]                   = 2358720,
    };

    public CoverArtService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>
    /// One-time migration: deletes any cover JPGs from before the portrait-poster switch
    /// so the next refresh downloads the new artwork. Idempotent — does nothing once the
    /// marker file is present.
    /// </summary>
    public static void MigrateCacheIfNeeded(string cacheDir)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            var marker = Path.Combine(cacheDir, "_v2_portrait.marker");
            if (File.Exists(marker)) return;

            int deleted = 0;
            foreach (var f in Directory.GetFiles(cacheDir, "*.jpg"))
            {
                try { File.Delete(f); deleted++; } catch { }
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            Log.Info($"CoverArt: migrated cache to v2 (portrait posters) — deleted {deleted} old file(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"CoverArt: cache migration failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Attempt to download cover art for this game config.
    /// Returns the local file path on success, or null if no art could be found.
    /// The caller should update GameConfig.CoverImagePath with the returned path and save.
    /// </summary>
    public async Task<string?> DownloadCoverAsync(GameConfig config)
    {
        // Already have a local file
        string cachePath = Path.Combine(_cacheDir, $"{config.Id}.jpg");
        if (File.Exists(cachePath))
        {
            Log.Info($"CoverArt: cached for {config.DisplayName} -> {cachePath}");
            return cachePath;
        }

        int? appId = FindSteamAppId(config);
        if (appId == null)
        {
            Log.Info($"CoverArt: no Steam AppID for '{config.DisplayName}' (source={config.Source}) — skipping");
            return null;
        }

        // Try the portrait library poster first; fall back to the landscape header
        var urls = new[]
        {
            string.Format(SteamLibraryPosterUrl, appId.Value),
            string.Format(SteamHeaderUrl,        appId.Value),
        };

        foreach (var url in urls)
        {
            try
            {
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Info($"CoverArt: {url} → HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 1024)
                {
                    Log.Info($"CoverArt: {url} → {bytes.Length} bytes (too small, skipping)");
                    continue;
                }

                await File.WriteAllBytesAsync(cachePath, bytes);
                Log.Info($"CoverArt: downloaded {config.DisplayName} (AppID {appId}) from {url} ({bytes.Length} bytes)");
                return cachePath;
            }
            catch (Exception ex)
            {
                Log.Warn($"CoverArt: {url} failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    // ── App ID resolution ────────────────────────────────────────────────────

    private static int? FindSteamAppId(GameConfig config)
    {
        // Strategy 0: AppID captured from Steam .acf manifest during library scan
        if (config.EngineSpecificSettings.TryGetValue("SteamAppId", out var stored)
            && int.TryParse(stored, out var storedId))
            return storedId;

        // Strategy 1: exact name match (case-insensitive)
        var name = config.DisplayName.Trim();
        if (KnownIds.TryGetValue(name, out var id)) return id;

        // Strategy 2: substring match (e.g. "My TLOU2 Game" contains "tlou2")
        foreach (var (key, val) in KnownIds)
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
                return val;
        }

        // Strategy 3: detect from Steam library path structure
        return FindAppIdFromSteamPath(config.GameDirectory);
    }

    private static int? FindAppIdFromSteamPath(string gamePath)
    {
        try
        {
            // Steam games live at: .../steamapps/common/{GameName}/
            // We walk up until we find a "common" folder whose parent looks like "steamapps"
            var dir = new DirectoryInfo(gamePath.TrimEnd('\\', '/'));
            while (dir != null &&
                   !string.Equals(dir.Name, "common", StringComparison.OrdinalIgnoreCase))
                dir = dir.Parent;

            if (dir?.Parent == null) return null;
            var steamappsDir = dir.Parent;  // e.g. …/steamapps

            foreach (var acf in steamappsDir.GetFiles("appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf.FullName);

                var appIdMatch    = Regex.Match(text, @"""appid""\s+""(\d+)""");
                var installMatch  = Regex.Match(text, @"""installdir""\s+""([^""]+)""");
                if (!appIdMatch.Success || !installMatch.Success) continue;

                string installDir = Path.Combine(
                    steamappsDir.FullName, "common", installMatch.Groups[1].Value);

                if (string.Equals(
                        installDir.TrimEnd('\\', '/'),
                        gamePath.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(appIdMatch.Groups[1].Value, out int appId))
                        return appId;
                }
            }
        }
        catch { /* Steam path detection is best-effort */ }
        return null;
    }
}
