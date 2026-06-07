namespace GameAssetExplorer.Engines.UnrealEngine;

/// <summary>
/// Curated FModel-style presets for popular Unreal Engine games that need a custom
/// <c>EGame</c> value (specialised serialization quirks per title) rather than a
/// generic engine version. Picking one of these in the AddGameDialog auto-populates
/// the right EngineVersion + <c>EngineSpecificSettings["EGame"]</c> so the game
/// mounts cleanly.
///
/// Mirrors the presets list FModel ships with its GameSelector — kept here so we
/// can extend it without touching UI code.
/// </summary>
public static class UeGamePresets
{
    public record Preset(
        string DisplayName,
        string EngineVersion,    // base UE version for the version dropdown
        string EGameValue,       // CUE4Parse EGame enum name, e.g. "GAME_StarWarsJediSurvivor"
        bool   RequiresAesKey,
        string? Hint);

    /// <summary>Curated list of presets. Order shown in the picker.</summary>
    public static readonly IReadOnlyList<Preset> All = new[]
    {
        // Star Wars Jedi series — Respawn UE with custom serialization
        new Preset("Star Wars Jedi: Survivor",     "4.26", "GAME_StarWarsJediSurvivor",     true,
            "Requires AES key. Path: …\\SwGame\\Content\\Paks"),
        new Preset("Star Wars Jedi: Fallen Order", "4.21", "GAME_StarWarsJediFallenOrder", true,
            "Requires AES key. Path: …\\SwGame\\Content\\Paks"),

        // Hellblade
        new Preset("Senua's Saga: Hellblade II",   "5.3",  "GAME_HellbladeSenuasSaga",      false,
            "UE 5.3. Hellblade 2."),

        // Other widely-used presets with custom CUE4Parse enums
        new Preset("Hogwarts Legacy",              "4.27", "GAME_HogwartsLegacy",           true,  null),
        new Preset("Fortnite",                     "5.4",  "GAME_UE5_4",                    true,
            "Use the current Fortnite AES key (rotates each season)."),
        new Preset("Marvel Rivals",                "5.3",  "GAME_MarvelRivals",             true,  null),
        new Preset("Black Myth: Wukong",           "5.0",  "GAME_BlackMythWukong",          false, null),
        new Preset("Ark: Survival Ascended",       "5.2",  "GAME_ArkSurvivalAscended",      false, null),
        new Preset("Stellar Blade",                "4.26", "GAME_StellarBlade",             false, null),
        new Preset("Final Fantasy VII Rebirth",    "4.26", "GAME_FinalFantasy7Rebirth",     false, null),
        new Preset("Tower of Fantasy",             "4.26", "GAME_TowerOfFantasy",           false, null),
        new Preset("Lies of P",                    "4.27", "GAME_LiesOfP",                  false, null),
        new Preset("Remnant II",                   "5.0",  "GAME_RemnantII",                false, null),
        new Preset("Tekken 8",                     "5.1",  "GAME_Tekken8",                  false, null),
        new Preset("Mortal Kombat 1",              "4.27", "GAME_MortalKombat1",            false, null),
        new Preset("Street Fighter 6",             "4.27", "GAME_StreetFighter6",           false, null),
    };

    /// <summary>Lookup a preset by its display name. Returns null for "Custom".</summary>
    public static Preset? Find(string? displayName) =>
        string.IsNullOrEmpty(displayName) ? null
            : All.FirstOrDefault(p => string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Try to guess the best preset for a config that's already saved, based on its
    /// stored EGame override (or null if it was a plain version-only config).</summary>
    public static Preset? GuessFromConfig(Core.Models.GameConfig config)
    {
        if (config.EngineSpecificSettings.TryGetValue("EGame", out var eg) && !string.IsNullOrEmpty(eg))
            return All.FirstOrDefault(p => p.EGameValue == eg);
        return null;
    }
}
