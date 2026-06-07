using System.Text.Json.Serialization;

namespace GameAssetExplorer.Core.Models;

/// <summary>
/// Everything we store about a configured game.
/// Saved to disk so your library persists between sessions.
/// </summary>
public class GameConfig
{
    /// <summary>Unique ID for this config entry (generated on first save)</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name shown in the home screen library, e.g. "Star Wars Jedi: Survivor"</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Full path to the game's root install directory</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>Engine identifier, e.g. "UE4", "RAGE", "DECIMA"</summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>Specific engine version string, e.g. "4.27"</summary>
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>Whether this game appears as active/enabled in the home screen</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Path to the game's cover art image (optional, user can set manually)</summary>
    public string? CoverImagePath { get; set; }

    /// <summary>
    /// AES encryption key for Unreal Engine games that use encrypted .pak files.
    /// Jedi Survivor, Fortnite, and most modern UE titles require this.
    /// Format: "0x" followed by 64 hex characters.
    /// </summary>
    public string? AesKey { get; set; }

    /// <summary>
    /// Additional AES keys for games that use multiple encryption keys
    /// (newer UE5 games with multiple .utoc containers often need this)
    /// </summary>
    public List<string> AdditionalAesKeys { get; set; } = new();

    /// <summary>When this game was added to the library</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the user opened this game in the tool</summary>
    public DateTime? LastOpened { get; set; }

    /// <summary>
    /// User notes — free text field. Useful for storing things like
    /// "needs AES key rotation" or custom info about the game's structure.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Engine-specific settings that don't fit the common fields above.
    /// Each engine plugin can define and read its own keys here.
    /// </summary>
    public Dictionary<string, string> EngineSpecificSettings { get; set; } = new();

    /// <summary>Where this entry came from: "Steam", "Epic", "Ubisoft", "Rockstar", or "Manual"</summary>
    public string Source { get; set; } = "Manual";
}
