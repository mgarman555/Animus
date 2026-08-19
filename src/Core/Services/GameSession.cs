using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Services;

/// <summary>
/// The set of games mounted right now.
///
/// The app used to mount exactly one game at a time and unmount it on the way back to the
/// library, which is why there was no way to compare two games or find an asset without first
/// remembering which game it was in. A session keeps several mounted at once so switching is
/// instant and search can span all of them.
///
/// Mounting is expensive (archive indexing runs into hundreds of thousands of entries), so a
/// game already in the session is returned as-is rather than re-mounted.
/// </summary>
public sealed class GameSession
{
    private readonly List<MountedGame> _games = new();

    public IReadOnlyList<MountedGame> Games => _games;

    /// <summary>Raised when a game is added or removed, so the UI can refresh its rail.</summary>
    public event EventHandler? GamesChanged;

    /// <summary>The game whose assets the browser is currently showing.</summary>
    public MountedGame? Active { get; private set; }

    public MountedGame? Find(string gameId) =>
        _games.FirstOrDefault(g => g.Config.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Add an already-mounted engine to the session, or return the existing entry when this
    /// game is mounted already. Becomes the active game either way.
    /// </summary>
    public MountedGame Add(GameConfig config, IGameEngine engine, IReadOnlyList<AssetInfo> assets)
    {
        var existing = Find(config.Id);
        if (existing != null)
        {
            Active = existing;
            return existing;
        }

        var entry = new MountedGame(config, engine, assets);
        _games.Add(entry);
        Active = entry;
        GamesChanged?.Invoke(this, EventArgs.Empty);
        Log.Info($"GameSession: mounted '{config.DisplayName}' ({assets.Count:N0} assets); " +
                 $"{_games.Count} game(s) in session");
        return entry;
    }

    public void SetActive(MountedGame game)
    {
        if (_games.Contains(game)) Active = game;
    }

    /// <summary>Unmount one game and drop it from the session.</summary>
    public async Task RemoveAsync(MountedGame game)
    {
        if (!_games.Remove(game)) return;

        if (ReferenceEquals(Active, game)) Active = _games.FirstOrDefault();

        try { await game.Engine.UnmountGameAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"GameSession: unmount of '{game.Config.DisplayName}' failed — {ex.Message}"); }

        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unmount everything (application shutdown, or returning to an empty library).</summary>
    public async Task ClearAsync()
    {
        foreach (var g in _games.ToList())
        {
            try { await g.Engine.UnmountGameAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"GameSession: unmount of '{g.Config.DisplayName}' failed — {ex.Message}"); }
        }
        _games.Clear();
        Active = null;
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Find assets across every mounted game.
    ///
    /// Matching is on the asset name first and the virtual path second, so typing "ellie"
    /// surfaces the Ellie meshes ahead of everything that merely lives under an ellie/ folder.
    /// Results are capped because a bare letter matches six figures of assets and the caller is
    /// a list box, not a report.
    /// </summary>
    public IReadOnlyList<SearchHit> SearchAll(string query, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();

        string q = query.Trim();
        var hits = new List<SearchHit>();

        foreach (var game in _games)
        {
            foreach (var asset in game.Assets)
            {
                int rank = Rank(asset, q);
                if (rank < 0) continue;
                hits.Add(new SearchHit(game, asset, rank));
                // Keep scanning other games even once one is saturated — a cap that emptied
                // the later games would make the result depend on mount order.
                if (hits.Count > limit * 8) break;
            }
        }

        return hits
            .OrderBy(h => h.Rank)
            .ThenBy(h => h.Asset.Name.Length)
            .ThenBy(h => h.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>Lower is better; -1 means no match.</summary>
    private static int Rank(AssetInfo asset, string q)
    {
        if (asset.Name.Equals(q, StringComparison.OrdinalIgnoreCase))                 return 0;
        if (asset.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))             return 1;
        if (asset.Name.Contains(q, StringComparison.OrdinalIgnoreCase))               return 2;
        if (asset.VirtualPath.Contains(q, StringComparison.OrdinalIgnoreCase))        return 3;
        return -1;
    }
}

/// <summary>One game mounted in the session, with its asset index.</summary>
public sealed class MountedGame
{
    public GameConfig  Config { get; }
    public IGameEngine Engine { get; }
    public IReadOnlyList<AssetInfo> Assets { get; }

    public MountedGame(GameConfig config, IGameEngine engine, IReadOnlyList<AssetInfo> assets)
    {
        Config = config;
        Engine = engine;
        Assets = assets;
    }

    public string DisplayName => Config.DisplayName;
    public int    AssetCount  => Assets.Count;

    /// <summary>Two-letter badge for the game rail, e.g. "TL" for The Last of Us Part II.</summary>
    public string Badge
    {
        get
        {
            var words = Config.DisplayName
                .Split(new[] { ' ', '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0]))
                .ToArray();
            if (words.Length == 0) return "??";
            if (words.Length == 1) return words[0].Length >= 2
                ? words[0][..2].ToUpperInvariant()
                : words[0].ToUpperInvariant();
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        }
    }
}

/// <summary>One cross-game search result.</summary>
public sealed record SearchHit(MountedGame Game, AssetInfo Asset, int Rank);
