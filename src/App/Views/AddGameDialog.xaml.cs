using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Engines.UnrealEngine;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Dialog for adding or editing a game configuration.
/// Pass an existing GameConfig to edit it, or null to create a new one.
/// After ShowDialog() returns true, read the Result property.
/// </summary>
public partial class AddGameDialog : Window
{
    private readonly IReadOnlyList<IGameEngine> _engines;
    private GameConfig? _existingConfig;

    /// <summary>The configured game. Populated when the user clicks Save.</summary>
    public GameConfig? Result { get; private set; }

    public AddGameDialog(GameConfig? existing, IReadOnlyList<IGameEngine> engines)
    {
        InitializeComponent();

        _engines        = engines;
        _existingConfig = existing;

        // Populate engine dropdown
        CmbEngine.ItemsSource = engines;

        if (existing != null)
        {
            // Edit mode
            DialogTitle.Text      = "Edit Game";
            TxtDisplayName.Text   = existing.DisplayName;
            TxtGameDir.Text       = existing.GameDirectory;
            TxtAesKey.Text        = existing.AesKey ?? "";

            // Pre-select the engine
            var matchedEngine = engines.FirstOrDefault(e =>
                string.Equals(e.EngineId, existing.EngineId, StringComparison.OrdinalIgnoreCase));
            if (matchedEngine != null)
                CmbEngine.SelectedItem = matchedEngine;
        }
        else
        {
            // Auto-select first engine
            if (engines.Count > 0)
                CmbEngine.SelectedIndex = 0;
        }
    }

    // ── Event Handlers ───────────────────────────────────────────────────────

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description  = "Select the game's root install directory",
            SelectedPath = TxtGameDir.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtGameDir.Text = dialog.SelectedPath;
            TryAutoDetectEngine(dialog.SelectedPath);
            AutoSelectPresetFromDirectory();
        }
    }

    /// <summary>
    /// FModel-style game auto-detect: if the chosen directory matches a known UE preset
    /// (e.g. a Jedi game), select it so the correct EGame serialization is applied on save.
    /// No-op when editing an existing config or when the engine isn't UE.
    /// </summary>
    private void AutoSelectPresetFromDirectory()
    {
        if (_existingConfig != null) return;
        if (CmbEngine.SelectedItem is not IGameEngine eng || eng.EngineId != "UE") return;
        if (CmbPreset.ItemsSource == null) return;

        var detected = UeGamePresets.GuessFromDirectory(TxtGameDir.Text);
        if (detected != null)
            CmbPreset.SelectedItem = detected.DisplayName; // fires OnPresetSelectionChanged → sets version + EGame
    }

    private void TryAutoDetectEngine(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        IGameEngine? best       = null;
        float        bestScore  = 0.3f;

        foreach (var engine in _engines)
        {
            try
            {
                var score = engine.DetectEngine(path);
                if (score > bestScore) { bestScore = score; best = engine; }
            }
            catch { /* skip engines that throw on this directory */ }
        }

        if (best != null)
        {
            DetectHintText.Text    = $"Detected: {best.EngineName}";
            DetectHint.Visibility  = Visibility.Visible;
            BtnUseDetected.Tag     = best;
            BtnUseDetected.Visibility = Visibility.Visible;
        }
        else
        {
            DetectHintText.Text   = "Could not auto-detect engine — select manually below.";
            DetectHint.Visibility = Visibility.Visible;
            BtnUseDetected.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUseDetectedEngine(object sender, RoutedEventArgs e)
    {
        if (BtnUseDetected.Tag is IGameEngine engine)
            CmbEngine.SelectedItem = engine;
    }

    private bool _suppressPresetChange;

    private void OnEngineSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbEngine.SelectedItem is not IGameEngine engine) return;

        // Populate version dropdown
        CmbVersion.ItemsSource   = engine.SupportedVersions;
        CmbVersion.SelectedIndex = engine.SupportedVersions.Count - 1; // Default to latest

        // UE-only: show AES section + the FModel-style game preset picker
        bool isUe = engine.EngineId == "UE";
        AesSection.Visibility    = isUe ? Visibility.Visible : Visibility.Collapsed;
        PresetSection.Visibility = isUe ? Visibility.Visible : Visibility.Collapsed;

        if (isUe)
            PopulatePresetDropdown();

        // Pre-select version from existing config if editing
        if (_existingConfig != null && !string.IsNullOrEmpty(_existingConfig.EngineVersion))
        {
            var versions = engine.SupportedVersions.ToList();
            var idx = versions.IndexOf(_existingConfig.EngineVersion);
            if (idx >= 0) CmbVersion.SelectedIndex = idx;
        }

        // Pre-select preset if editing a known game
        if (isUe && _existingConfig != null)
        {
            var preset = UeGamePresets.GuessFromConfig(_existingConfig);
            _suppressPresetChange = true;
            CmbPreset.SelectedItem = preset?.DisplayName ?? "Custom (set version manually)";
            UpdatePresetHint(preset);
            _suppressPresetChange = false;
        }

        // New game: auto-detect the preset from the chosen directory (FModel-style)
        if (isUe && _existingConfig == null)
            AutoSelectPresetFromDirectory();
    }

    private void PopulatePresetDropdown()
    {
        var items = new List<string> { "Custom (set version manually)" };
        items.AddRange(UeGamePresets.All.Select(p => p.DisplayName));
        CmbPreset.ItemsSource = items;
        CmbPreset.SelectedIndex = 0;
    }

    private void OnPresetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressPresetChange) return;

        var preset = UeGamePresets.Find(CmbPreset.SelectedItem as string);
        UpdatePresetHint(preset);

        if (preset == null) return;

        // Auto-set the engine version dropdown
        var versions = (CmbVersion.ItemsSource as IEnumerable<string>)?.ToList();
        if (versions != null)
        {
            var idx = versions.IndexOf(preset.EngineVersion);
            if (idx >= 0) CmbVersion.SelectedIndex = idx;
        }
    }

    private void UpdatePresetHint(UeGamePresets.Preset? preset)
    {
        if (preset == null)
        {
            LblPresetHint.Text = "Use this if your game isn't in the list, or you're testing a custom build.";
            return;
        }
        var parts = new List<string>
        {
            $"Will mount as {preset.EGameValue} (UE {preset.EngineVersion} + custom serialization).",
        };
        if (preset.RequiresAesKey) parts.Add("Requires AES key.");
        if (!string.IsNullOrEmpty(preset.Hint)) parts.Add(preset.Hint!);
        LblPresetHint.Text = string.Join("  ", parts);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // ── Validation ─────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(TxtDisplayName.Text))
        {
            MessageBox.Show("Please enter a display name.", "Required Field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtDisplayName.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtGameDir.Text) || !Directory.Exists(TxtGameDir.Text))
        {
            MessageBox.Show("Please select a valid game directory.", "Invalid Path",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CmbEngine.SelectedItem is not IGameEngine selectedEngine)
        {
            MessageBox.Show("Please select an engine.", "Required Field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Build Result ───────────────────────────────────────────────
        var config = _existingConfig ?? new GameConfig();

        config.DisplayName    = TxtDisplayName.Text.Trim();
        config.GameDirectory  = TxtGameDir.Text.Trim();
        config.EngineId       = selectedEngine.EngineId;
        config.EngineVersion  = CmbVersion.SelectedItem?.ToString() ?? "";
        config.AesKey         = string.IsNullOrWhiteSpace(TxtAesKey.Text) ? null : TxtAesKey.Text.Trim();

        // Persist (or clear) the EGame preset override
        var preset = selectedEngine.EngineId == "UE"
            ? UeGamePresets.Find(CmbPreset.SelectedItem as string)
            : null;
        if (preset != null)
            config.EngineSpecificSettings["EGame"] = preset.EGameValue;
        else
            config.EngineSpecificSettings.Remove("EGame");

        Result       = config;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
