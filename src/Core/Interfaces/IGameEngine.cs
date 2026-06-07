using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Interfaces;

/// <summary>
/// The contract every engine plugin must implement.
/// When we add RAGE, Decima, or any other engine, it gets its own class that fulfills this interface.
/// The App layer never talks directly to engine code — it always goes through here.
/// </summary>
public interface IGameEngine
{
    /// <summary>Human-readable engine name, e.g. "Unreal Engine 4", "RAGE Engine"</summary>
    string EngineName { get; }

    /// <summary>Short identifier used internally, e.g. "UE4", "RAGE", "DECIMA"</summary>
    string EngineId { get; }

    /// <summary>
    /// Supported game engine versions, e.g. ["4.27", "5.0", "5.1"] for UE.
    /// Used to populate the version dropdown when configuring a game.
    /// </summary>
    IReadOnlyList<string> SupportedVersions { get; }

    /// <summary>
    /// File extensions this engine uses for its archives/packages.
    /// Used during auto-detection (e.g. ".pak", ".utoc" for UE; ".rpf" for RAGE).
    /// </summary>
    IReadOnlyList<string> ArchiveExtensions { get; }

    /// <summary>
    /// Called when the user selects a game to open.
    /// Should load the archive manifest so we can list files without reading every single one.
    /// </summary>
    Task<bool> MountGameAsync(GameConfig config, IProgress<string>? progress = null);

    /// <summary>Unloads everything and frees memory. Called when user closes a game.</summary>
    Task UnmountGameAsync();

    /// <summary>Whether a game is currently mounted and ready to browse.</summary>
    bool IsMounted { get; }

    /// <summary>
    /// Returns all assets in the game as a flat list.
    /// Assets are lazy — calling this doesn't read any asset data, just the file tree.
    /// </summary>
    Task<IReadOnlyList<AssetInfo>> GetAllAssetsAsync();

    /// <summary>
    /// Get assets under a specific virtual path (e.g. "/Game/Characters/").
    /// Used for the tree view in the asset browser.
    /// </summary>
    Task<IReadOnlyList<AssetInfo>> GetAssetsAtPathAsync(string virtualPath);

    /// <summary>
    /// Load the full data for a single asset (texture pixels, mesh data, etc.).
    /// This is where actual file reading happens.
    /// </summary>
    Task<AssetData> LoadAssetAsync(AssetInfo asset);

    /// <summary>
    /// Quick auto-detect: look at the game directory and say whether this engine
    /// is the right one for it. Returns a confidence score 0.0-1.0.
    /// </summary>
    float DetectEngine(string gameDirectory);
}
