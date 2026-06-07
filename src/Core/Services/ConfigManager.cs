using GameAssetExplorer.Core.Models;
using System.Text.Json;

namespace GameAssetExplorer.Core.Services;

/// <summary>
/// Handles saving and loading the game library configuration.
/// Config lives in %AppData%/GameAssetExplorer/games.json so it persists between sessions.
/// </summary>
public class ConfigManager
{
    private readonly string _configPath;
    private List<GameConfig> _games = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "GameAssetExplorer");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "games.json");
    }

    /// <summary>All configured games, in display order</summary>
    public IReadOnlyList<GameConfig> Games => _games.AsReadOnly();

    /// <summary>Load game library from disk. Call at app startup.</summary>
    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            _games = new List<GameConfig>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _games = JsonSerializer.Deserialize<List<GameConfig>>(json, JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load games.json", ex);
            _games = new List<GameConfig>();
        }

        // Validate cover image paths — clear any pointing to deleted files so the
        // background cover-fetch will re-download them.
        bool dirty = false;
        foreach (var g in _games)
        {
            if (!string.IsNullOrEmpty(g.CoverImagePath) && !File.Exists(g.CoverImagePath))
            {
                g.CoverImagePath = null;
                dirty = true;
            }
        }
        if (dirty) Save();
    }

    /// <summary>Save the current game library to disk.</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_games, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save games.json", ex);
        }
    }

    /// <summary>
    /// Add a game to the library.
    /// Returns false (and does NOT add) if a game with the same directory already exists.
    /// </summary>
    public bool AddGame(GameConfig config)
    {
        // Prevent duplicate directories (case-insensitive, ignoring trailing slashes)
        static string Normalize(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .ToLowerInvariant();

        var incoming = Normalize(config.GameDirectory);
        if (_games.Any(g => Normalize(g.GameDirectory) == incoming))
            return false;

        _games.Add(config);
        Save();
        return true;
    }

    public void UpdateGame(GameConfig config)
    {
        var index = _games.FindIndex(g => g.Id == config.Id);
        if (index >= 0)
        {
            _games[index] = config;
            Save();
        }
    }

    public void RemoveGame(string gameId)
    {
        _games.RemoveAll(g => g.Id == gameId);
        Save();
    }

    public GameConfig? GetGame(string gameId) => _games.FirstOrDefault(g => g.Id == gameId);

    /// <summary>
    /// Drop entries that look like launcher/runtime installs rather than games
    /// (e.g. Steam Linux Runtime, Rockstar Social Club). Returns the number removed.
    /// </summary>
    public int PruneLaunchers()
    {
        int before = _games.Count;
        _games.RemoveAll(g => LauncherFilter.IsLauncherOrTool(g.DisplayName, g.GameDirectory));
        int removed = before - _games.Count;
        if (removed > 0) Save();
        return removed;
    }

    /// <summary>Mark a game as just-opened (updates LastOpened timestamp)</summary>
    public void RecordGameOpened(string gameId)
    {
        var game = GetGame(gameId);
        if (game != null)
        {
            game.LastOpened = DateTime.UtcNow;
            Save();
        }
    }
}
