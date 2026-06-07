using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace GameAssetExplorer.App.ViewModels;

// Args for errors that should surface as a dialog (not just a status-bar blip)
public record LoadErrorArgs(string Title, string Message);

/// <summary>
/// ViewModel for the home screen — the game library.
/// Manages the list of configured games and handles opening them.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly ConfigManager  _config;
    private readonly PluginLoader   _plugins;
    private readonly CoverArtService _coverArt;

    [ObservableProperty]
    private ObservableCollection<GameLibraryEntry> games = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenGameCommand))]
    private GameLibraryEntry? selectedGame;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusMessage = "Ready.";

    // ── Events ──────────────────────────────────────────────────────────────
    /// <summary>Fired after a game is successfully mounted — navigate to the asset browser.</summary>
    public event EventHandler<GameOpenedEventArgs>? GameOpened;

    /// <summary>Fired when the user clicks Add Game — the View opens the dialog.</summary>
    public event EventHandler? AddGameRequested;

    /// <summary>Fired when loading fails with a user-visible message for a dialog.</summary>
    public event EventHandler<LoadErrorArgs>? LoadError;

    public HomeViewModel(ConfigManager config, PluginLoader plugins)
    {
        _config  = config;
        _plugins = plugins;

        var coversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameAssetExplorer", "covers");
        _coverArt = new CoverArtService(coversDir);

        // Drop any launcher/runtime entries that slipped in on previous scans
        _config.PruneLaunchers();

        RefreshGameList();
        _ = ScanAndMergeAsync();        // background library scan — don't await
        _ = FetchMissingCoversAsync();  // background — don't await
    }

    private void RefreshGameList()
    {
        Games = new ObservableCollection<GameLibraryEntry>(
            _config.Games.Select(g => new GameLibraryEntry(g)));
    }

    /// <summary>
    /// Background task: scan all installed game launchers and merge new games into the library.
    /// Skips games already present (matched by directory path). Runs once per startup.
    /// </summary>
    private async Task ScanAndMergeAsync()
    {
        try
        {
            StatusMessage = "Scanning installed games…";
            Log.Info("Scan: starting library scan");
            var discovered = await GameAssetExplorer.Core.Services.GameScanner.ScanAllAsync();
            Log.Info($"Scan: discovered {discovered.Count} games across launchers");

            int added = 0;
            foreach (var config in discovered)
            {
                if (_config.AddGame(config))
                {
                    added++;
                    Log.Info($"Scan: added '{config.DisplayName}' ({config.Source}, engine={config.EngineId} {config.EngineVersion}) — {config.GameDirectory}");
                }
            }

            if (added > 0)
            {
                App.Current.Dispatcher.Invoke(() =>
                {
                    RefreshGameList();
                    _ = FetchMissingCoversAsync();
                });
            }

            StatusMessage = added > 0 ? $"Found {added} new game(s)." : "Library up to date.";
            Log.Info($"Scan: complete ({added} added, {discovered.Count - added} duplicates skipped)");
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            StatusMessage = $"Library scan failed: {ex.Message}";
        }
    }

    /// <summary>Background task: fetch cover art for any game that doesn't have one yet.</summary>
    private async Task FetchMissingCoversAsync()
    {
        foreach (var entry in Games.ToList())  // snapshot to avoid collection-changed issues
        {
            if (!string.IsNullOrEmpty(entry.Config.CoverImagePath) &&
                File.Exists(entry.Config.CoverImagePath))
                continue;  // already have it

            try
            {
                var path = await _coverArt.DownloadCoverAsync(entry.Config);
                if (path != null)
                {
                    entry.Config.CoverImagePath = path;
                    _config.UpdateGame(entry.Config);
                    entry.NotifyCoverChanged();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Cover fetch error for {entry.Config.DisplayName}: {ex.Message}");
            }
        }
    }

    /// <summary>Open the add/configure game dialog (the View handles opening the Window)</summary>
    [RelayCommand]
    private void AddGame() => AddGameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Select a game card (single click)</summary>
    [RelayCommand]
    private void SelectGame(GameLibraryEntry entry)
    {
        if (SelectedGame != null) SelectedGame.IsSelected = false;
        SelectedGame = entry;
        if (entry != null) entry.IsSelected = true;
    }

    /// <summary>Open the selected game in the asset browser</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGame))]
    private async Task OpenGame()
    {
        if (SelectedGame == null) return;

        var gameName = SelectedGame.DisplayName;
        var cfg = SelectedGame.Config;
        IsLoading = true;
        StatusMessage = $"Loading {gameName}…";
        Log.Info($"Mount: opening '{gameName}' (engine={cfg.EngineId} {cfg.EngineVersion}, source={cfg.Source}, dir={cfg.GameDirectory})");

        try
        {
            var engine = _plugins.GetEngineById(SelectedGame.Config.EngineId);
            if (engine == null)
            {
                var msg = $"No engine plugin found for engine ID \"{SelectedGame.Config.EngineId}\".\n\n" +
                          "Try editing the game and re-selecting the engine.";
                Log.Warn($"Mount: no plugin for engine ID '{cfg.EngineId}' — game won't open");
                StatusMessage = msg;
                LoadError?.Invoke(this, new LoadErrorArgs("Engine Not Found", msg));
                return;
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);
            bool success;
            try
            {
                success = await engine.MountGameAsync(SelectedGame.Config, progress);
            }
            catch (NotSupportedException ex)
            {
                // e.g. DSAR format — give a specific, actionable message
                StatusMessage = $"Cannot open {gameName}: {ex.Message}";
                LoadError?.Invoke(this, new LoadErrorArgs(
                    $"Cannot Open — {gameName}",
                    ex.Message));
                return;
            }

            if (success)
            {
                _config.RecordGameOpened(SelectedGame.Config.Id);
                StatusMessage = $"{gameName} loaded.";
                Log.Info($"Mount: '{gameName}' mounted successfully via {engine.EngineId}");
                GameOpened?.Invoke(this, new GameOpenedEventArgs
                {
                    Engine = engine,
                    Config = SelectedGame.Config
                });
            }
            else
            {
                Log.Warn($"Mount: '{gameName}' returned success=false from {engine.EngineId}.MountGameAsync");
                string detail = engine.EngineId switch
                {
                    "UE"   => "No .pak/.utoc files found. Check the game directory and, if the game uses encryption, make sure an AES key is entered.",
                    "ND"   => "No .psarc or Naughty Dog .pak files found.\n\n" +
                              "For TLOU2: run your Python .psarc → .pak extractor first, then point the game directory at the extracted folder.",
                    "RAGE" => "No .rpf files found. Check the game directory.",
                    "SOTR" => "No .tiger files found. Point the game directory at the Shadow of the Tomb Raider install folder (the one containing bigfile.000.tiger).",
                    _      => "Check that the game directory is correct."
                };
                var fullMsg = $"Could not load any assets for \"{gameName}\".\n\n{detail}";
                StatusMessage = $"Failed to load {gameName}.";
                LoadError?.Invoke(this, new LoadErrorArgs($"Failed to Open — {gameName}", fullMsg));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Mount: unexpected error opening '{gameName}'", ex);
            var msg = $"An unexpected error occurred while loading {gameName}:\n\n{ex.Message}";
            StatusMessage = $"Error: {ex.Message}";
            LoadError?.Invoke(this, new LoadErrorArgs("Unexpected Error", msg));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanOpenGame() => SelectedGame != null && !IsLoading;

    // ── Public helpers called from code-behind ───────────────────────────────

    /// <summary>All engine plugins registered at startup — populates the dialog's engine dropdown.</summary>
    public IReadOnlyList<IGameEngine> AvailableEngines => _plugins.Engines;

    /// <summary>
    /// Called by HomeView code-behind after the Add Game dialog is confirmed.
    /// Returns false if a game with that directory already exists (duplicate).
    /// </summary>
    public bool AddGameFromDialog(GameConfig config)
    {
        if (!_config.AddGame(config))
            return false;  // duplicate — caller shows the error

        RefreshGameList();
        _ = FetchMissingCoversAsync();  // try to get cover art for the new game
        return true;
    }

    /// <summary>Called by HomeView code-behind after the Edit Game dialog is confirmed.</summary>
    public void UpdateGameFromDialog(GameConfig config)
    {
        _config.UpdateGame(config);
        RefreshGameList();
    }

    /// <summary>Remove a game from the library.</summary>
    [RelayCommand]
    private void RemoveGame(GameLibraryEntry entry)
    {
        _config.RemoveGame(entry.Config.Id);
        if (SelectedGame?.Config.Id == entry.Config.Id) SelectedGame = null;
        RefreshGameList();
    }

    /// <summary>Toggle a game's enabled/disabled state from the home screen toggle switch</summary>
    [RelayCommand]
    private void ToggleGameEnabled(GameLibraryEntry entry)
    {
        entry.Config.IsEnabled = !entry.Config.IsEnabled;
        entry.NotifyEnabledChanged();
        _config.UpdateGame(entry.Config);
    }

    /// <summary>Auto-detect the engine for a game directory</summary>
    [RelayCommand]
    private void AutoDetectEngine(string gameDirectory)
    {
        var engine = _plugins.AutoDetectEngine(gameDirectory);
        StatusMessage = engine != null
            ? $"Detected engine: {engine.EngineName}"
            : "Could not auto-detect engine. Please select manually.";
    }
}

/// <summary>
/// Wraps a GameConfig for display in the home screen grid.
/// Extends ObservableObject so individual card properties can notify the UI.
/// </summary>
public class GameLibraryEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public GameLibraryEntry(GameConfig config) => Config = config;

    public GameConfig Config { get; }

    public string DisplayName    => Config.DisplayName;
    public string EngineName     => Config.EngineId;
    public bool   IsEnabled      => Config.IsEnabled;

    /// <summary>Shows engine name + version + source, e.g. "Unreal Engine 5  ·  Steam"</summary>
    public string SubtitleDisplay
    {
        get
        {
            var engine = Config.EngineId switch
            {
                "UE"   => "Unreal Engine",
                "RAGE" => "RAGE Engine",
                "ND"   => "NaughtyDog Engine",
                "SOTR" => "Foundation Engine",
                null or "" => "Unknown Engine",
                var s  => s,   // Frostbite, Unity, Source Engine, etc.
            };

            if (!string.IsNullOrEmpty(Config.EngineVersion))
                engine += $" {Config.EngineVersion}";

            var source = Config.Source;
            if (!string.IsNullOrEmpty(source) && source != "Manual")
                engine += $"  ·  {source}";

            return engine;
        }
    }

    public string LastOpenedDisplay => Config.LastOpened.HasValue
        ? Config.LastOpened.Value.ToLocalTime().ToString("MMM d, yyyy")
        : "Never opened";

    /// <summary>
    /// Path to the downloaded cover image, or null if none available.
    /// Null causes the card to show the letter-gradient placeholder.
    /// </summary>
    public string? CoverImagePath =>
        string.IsNullOrEmpty(Config.CoverImagePath) ? null : Config.CoverImagePath;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Called by ToggleGameEnabled so the card checkbox refreshes without rebuilding the list.</summary>
    public void NotifyEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));

    /// <summary>Called by HomeViewModel after cover art is downloaded to refresh the card image.</summary>
    public void NotifyCoverChanged() => OnPropertyChanged(nameof(CoverImagePath));
}
