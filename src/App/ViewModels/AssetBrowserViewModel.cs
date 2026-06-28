using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Utilities;
using GameAssetExplorer.Engines.NaughtyDog;
using GameAssetExplorer.Engines.SotrEngine;
using GameAssetExplorer.Exporters.MetadataExporter;
using GameAssetExplorer.Exporters.ModelExporter;
using GameAssetExplorer.Exporters.TextureExporter;
using System.Collections.ObjectModel;
using System.IO;

namespace GameAssetExplorer.App.ViewModels;

/// <summary>
/// ViewModel for the asset browser — the main workspace once a game is loaded.
/// Handles browsing the file tree, previewing assets, and running exports.
/// </summary>
public partial class AssetBrowserViewModel : ObservableObject
{
    private readonly IGameEngine _engine;
    private readonly GameConfig _gameConfig;

    // ─── Asset Browser State ──────────────────────────────────────────────────

    [ObservableProperty]
    private List<AssetTreeNode> rootNodes = new();

    [ObservableProperty]
    private List<AssetInfo> filteredAssets = new();

    [ObservableProperty]
    private AssetInfo? selectedAsset;

    [ObservableProperty]
    private string searchFilter = string.Empty;

    [ObservableProperty]
    private AssetType filterType = AssetType.Unknown; // Unknown = show all

    // ─── Game Info ────────────────────────────────────────────────────────────

    public string GameName => _gameConfig.DisplayName;

    // ─── Loading ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool isLoading;

    // ─── Preview State ────────────────────────────────────────────────────────

    [ObservableProperty]
    private AssetData? previewedAsset;

    [ObservableProperty]
    private bool isLoadingPreview;

    // ─── Export Path ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private string exportOutputPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
        "GameAssetExplorer_Exports");

    // ─── Export State ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool isExporting;

    [ObservableProperty]
    private double exportProgress;

    [ObservableProperty]
    private string exportStatus = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public ExportSettings ExportSettings { get; } = new();

    // ─── Merge Queue ─────────────────────────────────────────────────────────

    /// <summary>
    /// Assets queued for a combined export. The user adds pieces here (head, body, armor…)
    /// then exports them all as one merged OBJ/FBX.
    /// </summary>
    public ObservableCollection<AssetInfo> MergeQueue { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportMergedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearMergeQueueCommand))]
    private int mergeQueueCount;

    [ObservableProperty]
    private string mergedMeshName = "MergedCharacter";

    [ObservableProperty]
    private bool isMergePanelVisible;

    /// <summary>Fired when the user clicks the back arrow — navigate back to the home screen.</summary>
    public event EventHandler? BackRequested;

    public AssetBrowserViewModel(IGameEngine engine, GameConfig config)
    {
        _engine = engine;
        _gameConfig = config;

        // Hook into NaughtyDog background texture-dictionary build so it shows in the status bar
        if (engine is NaughtyDogPlugin nd)
        {
            nd.SetBackgroundProgress(new Progress<string>(msg => StatusMessage = msg));
        }

        // Hook into SOTR background type scan — shows progress and rebuilds the tree when done
        if (engine is SotrEnginePlugin sotr)
        {
            sotr.SetBackgroundProgress(new Progress<string>(msg => StatusMessage = msg));
            sotr.TypeScanCompleted += async (_, _) =>
            {
                // Rebuild tree on UI thread so type folders populate correctly
                var assets = await engine.GetAllAssetsAsync();
                _allAssets = assets;
                BuildFileTree(assets);
                BuildAssetGroups(assets);
                StatusMessage = $"{assets.Count:N0} assets — type scan complete.";
            };
        }
    }

    [RelayCommand]
    private async Task GoBack()
    {
        await _engine.UnmountGameAsync();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    // ─── Initialization ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading asset list…";

        try
        {
            var allAssets = await _engine.GetAllAssetsAsync();
            _allAssets = allAssets;

            // Build the tree + type groups off the UI thread (860k-file games would otherwise
            // freeze the window for the whole build), then assign on the UI thread.
            StatusMessage = $"Indexing {allAssets.Count:N0} assets…";
            var built = await Task.Run(() =>
                (Tree: BuildFileTreeCore(allAssets), Groups: BuildAssetGroupsCore(allAssets)));
            RootNodes = built.Tree;
            AssetGroups = built.Groups;
            FilteredAssets = allAssets.ToList();

            if (_engine is NaughtyDogPlugin nd2 && nd2.IsTexDictLoaded)
                StatusMessage = $"{allAssets.Count:N0} assets loaded. Texture dictionary ready ({nd2.TexDictEntryCount:N0} textures).";
            else if (_engine is NaughtyDogPlugin)
                StatusMessage = $"{allAssets.Count:N0} assets loaded. Building texture dictionary (see status bar)…";
            else
                StatusMessage = $"{allAssets.Count:N0} assets loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading assets: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ─── Browsing & Filtering ─────────────────────────────────────────────────

    // Assets currently visible based on folder-tree selection (before search filter)
    private IReadOnlyList<AssetInfo> _folderScope = Array.Empty<AssetInfo>();

    partial void OnSearchFilterChanged(string value) => ApplyFilter();
    partial void OnFilterTypeChanged(AssetType value) => ApplyFilter();

    private void ApplyFilter()
    {
        var scope = _folderScope.Count > 0 ? _folderScope : _allAssets;

        IEnumerable<AssetInfo> result = scope;

        if (FilterType != AssetType.Unknown)
            result = result.Where(a => a.Type == FilterType);

        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            var q = SearchFilter.Trim();
            result = result.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.VirtualPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = result.ToList();
        FilteredAssets = list;
        BuildAssetGroups(list);
    }

    /// <summary>Load and preview the selected asset. Called when user clicks on a file.</summary>
    [RelayCommand]
    private async Task PreviewSelectedAsset()
    {
        if (SelectedAsset == null) return;

        IsLoadingPreview = true;
        PreviewedAsset = null;

        try
        {
            PreviewedAsset = await _engine.LoadAssetAsync(SelectedAsset);
            StatusMessage = $"Loaded: {SelectedAsset.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load asset: {ex.Message}";
        }
        finally
        {
            IsLoadingPreview = false;
        }
    }

    // ─── Export Commands ──────────────────────────────────────────────────────

    /// <summary>Export the currently previewed asset</summary>
    [RelayCommand]
    private async Task ExportCurrentAsset(string outputDirectory)
    {
        if (PreviewedAsset == null) return;
        await ExportAssets(new[] { PreviewedAsset }, outputDirectory);
    }

    /// <summary>Export all selected assets in the browser (for batch export)</summary>
    [RelayCommand]
    private async Task ExportSelected(string outputDirectory)
    {
        // Multiple selection is handled by the view — for now single asset
        if (PreviewedAsset == null) return;
        await ExportAssets(new[] { PreviewedAsset }, outputDirectory);
    }

    // ─── Merge Queue Commands ─────────────────────────────────────────────────

    /// <summary>Add the currently selected or right-clicked asset to the merge queue.</summary>
    [RelayCommand]
    private void AddToMergeQueue(AssetInfo? asset)
    {
        var target = asset ?? SelectedAsset;
        if (target == null) return;
        if (target.Type is not (AssetType.StaticMesh or AssetType.SkeletalMesh))
        {
            StatusMessage = $"Only mesh assets can be added to the merge queue (got {target.Type}).";
            return;
        }
        if (MergeQueue.Contains(target))
        {
            StatusMessage = $"{target.Name} is already in the merge queue.";
            return;
        }

        MergeQueue.Add(target);
        MergeQueueCount = MergeQueue.Count;
        IsMergePanelVisible = true;
        StatusMessage = $"Added {target.Name} to merge queue ({MergeQueue.Count} items).";
    }

    /// <summary>Remove one asset from the merge queue.</summary>
    [RelayCommand]
    private void RemoveFromMergeQueue(AssetInfo? asset)
    {
        if (asset == null) return;
        MergeQueue.Remove(asset);
        MergeQueueCount = MergeQueue.Count;
        if (MergeQueue.Count == 0) IsMergePanelVisible = false;
        StatusMessage = $"Removed {asset.Name} from merge queue ({MergeQueue.Count} remaining).";
    }

    /// <summary>Clear all items from the merge queue.</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnMergeQueue))]
    private void ClearMergeQueue()
    {
        MergeQueue.Clear();
        MergeQueueCount = 0;
        IsMergePanelVisible = false;
        StatusMessage = "Merge queue cleared.";
    }

    /// <summary>Load all queued meshes, merge them, and export as OBJ.</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnMergeQueue))]
    private async Task ExportMerged(string outputDirectory)
    {
        if (MergeQueue.Count == 0) return;

        IsExporting = true;
        ExportProgress = 0;
        ExportStatus = $"Loading {MergeQueue.Count} meshes for merge…";

        try
        {
            // Load all queued assets
            var loadedMeshes = new List<MeshAssetData>();
            for (int i = 0; i < MergeQueue.Count; i++)
            {
                var queuedInfo = MergeQueue[i];
                ExportStatus = $"Loading {queuedInfo.Name} ({i + 1}/{MergeQueue.Count})…";
                ExportProgress = (double)i / MergeQueue.Count * 50.0; // first 50% = loading

                var loaded = await _engine.LoadAssetAsync(queuedInfo);
                if (loaded is MeshAssetData mesh)
                    loadedMeshes.Add(mesh);
                else
                    StatusMessage = $"Warning: {queuedInfo.Name} did not load as a mesh — skipped.";
            }

            if (loadedMeshes.Count == 0)
            {
                ExportStatus = "No valid meshes loaded — merge cancelled.";
                return;
            }

            ExportStatus = $"Merging {loadedMeshes.Count} meshes…";
            ExportProgress = 55;

            // Run the merge
            var mergeSettings = new MeshMergeSettings
            {
                MergedMeshName   = string.IsNullOrWhiteSpace(MergedMeshName) ? "MergedMesh" : MergedMeshName,
                MergeSkeletons   = true,
                UniformScale     = ExportSettings.ModelScaleFactor
            };
            var merged = await Task.Run(() => MeshMerger.Merge(loadedMeshes, mergeSettings));

            if (merged == null)
            {
                ExportStatus = "Merge failed — check that all meshes have geometry data.";
                return;
            }

            ExportStatus = $"Exporting merged mesh ({merged.Lods[0].VertexCount:N0} verts, {merged.Lods[0].TriangleCount:N0} tris)…";
            ExportProgress = 75;

            // Export as OBJ
            var objExporter = new ObjModelExporter();
            var result = await objExporter.ExportAsync(merged, outputDirectory, ExportSettings);

            ExportProgress = 90;

            // JSON metadata sidecar
            if (ExportSettings.ExportMetadataJson)
            {
                var metaExporter = new JsonMetadataExporter();
                await metaExporter.ExportAsync(merged, outputDirectory, ExportSettings);
            }

            ExportProgress = 100;

            if (result.Success)
                ExportStatus = $"Merged export complete → {result.OutputPath}";
            else
                ExportStatus = $"Export failed: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Merge export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanOperateOnMergeQueue() => MergeQueueCount > 0;

    private async Task ExportAssets(IReadOnlyList<AssetData> assets, string outputDirectory)
    {
        IsExporting = true;
        ExportProgress = 0;

        var progress = new Progress<ExportProgress>(p =>
        {
            ExportProgress = p.PercentComplete;
            ExportStatus = $"Exporting {p.CurrentAsset} ({p.Completed}/{p.Total})";
        });

        var exportTasks = new List<Task>();

        // Normalized layout: Output/[Game]/[Category]/[AssetName]/  (model + meta, textures in Textures/).
        // Each asset gets its own folder, so paths are flat within it (no virtual-path mirroring).
        var flat = ExportSettings.Clone();
        flat.PreserveVirtualPaths = false;
        string gameDir = NormalizePart(_gameConfig.DisplayName);

        foreach (var asset in assets)
        {
            string assetDir = Path.Combine(outputDirectory, gameDir,
                CategoryFolder(asset.Info.Type), NormalizePart(asset.Info.Name));
            Directory.CreateDirectory(assetDir);

            switch (asset)
            {
                case TextureAssetData:
                    exportTasks.Add(new PngTextureExporter()
                        .ExportAsync(asset, Path.Combine(assetDir, "Textures"), flat));
                    break;

                case MeshAssetData:
                    IExporter modelExporter = flat.ModelFormat switch
                    {
                        ModelExportFormat.Obj => new ObjModelExporter(),
                        ModelExportFormat.Fbx => new FbxModelExporter(),
                        _                     => new GltfModelExporter(), // glTF: Blender + UE native
                    };
                    exportTasks.Add(modelExporter.ExportAsync(asset, assetDir, flat));
                    break;

                case AudioAssetData audio:
                    exportTasks.Add(WriteRawAudioAsync(audio, assetDir));
                    break;
            }

            // JSON metadata sidecar alongside the asset
            if (flat.ExportMetadataJson)
                exportTasks.Add(new JsonMetadataExporter().ExportAsync(asset, assetDir, flat));
        }

        try
        {
            await Task.WhenAll(exportTasks);
            ExportStatus = $"Export complete. {assets.Count} asset(s) exported to {outputDirectory}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ─── Initialization ───────────────────────────────────────────────────────

    private IReadOnlyList<AssetInfo> _allAssets = Array.Empty<AssetInfo>();

    // ─── Asset Group Building ─────────────────────────────────────────────────

    [ObservableProperty]
    private List<AssetGroupNode> assetGroups = new();

    /// <summary>
    /// Called by the left-panel file tree when the user selects a folder or file.
    /// Rebuilds the center grouped list to show only assets under that node.
    /// </summary>
    public void SetVisibleAssets(IReadOnlyList<AssetInfo> assets)
    {
        _folderScope = assets;
        ApplyFilter();
    }

    // ─── Normalized export helpers ────────────────────────────────────────────

    private static string CategoryFolder(AssetType t) => t switch
    {
        AssetType.Texture                              => "Textures",
        AssetType.StaticMesh or AssetType.SkeletalMesh => "Meshes",
        AssetType.Animation                            => "Animations",
        AssetType.Audio                                => "Audio",
        AssetType.Material                             => "Materials",
        AssetType.Level                                => "Levels",
        _                                              => "Other"
    };

    private static string NormalizePart(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "unnamed" : s;
    }

    private static async Task WriteRawAudioAsync(AudioAssetData audio, string assetDir)
    {
        var data = audio.RawAudioData;
        if (data == null || data.Length == 0) return;
        string ext = (audio.SourceFormat ?? "").ToLowerInvariant() switch
        {
            var f when f.Contains("ogg") => ".ogg",
            var f when f.Contains("wav") => ".wav",
            _ => ".bin"
        };
        await File.WriteAllBytesAsync(Path.Combine(assetDir, NormalizePart(audio.Info.Name) + ext), data);
    }

    private void BuildAssetGroups(IReadOnlyList<AssetInfo> assets) => AssetGroups = BuildAssetGroupsCore(assets);

    private static List<AssetGroupNode> BuildAssetGroupsCore(IReadOnlyList<AssetInfo> assets) =>
        assets
            .GroupBy(a => a.Type)
            .OrderBy(g => TypeDisplayOrder(g.Key))
            .Select(g => new AssetGroupNode
            {
                Type      = g.Key,
                TypeLabel = TypeDisplayLabel(g.Key),
                Assets    = g.OrderBy(a => a.Name).ToList()
            })
            .ToList();

    private static int TypeDisplayOrder(AssetType t) => t switch
    {
        AssetType.Texture      => 0,
        AssetType.SkeletalMesh => 1,
        AssetType.StaticMesh   => 2,
        AssetType.Animation    => 3,
        AssetType.Audio        => 4,
        AssetType.Material     => 5,
        AssetType.Blueprint    => 6,
        AssetType.Level        => 7,
        AssetType.Cinematic    => 8,
        _                      => 99
    };

    private static string TypeDisplayLabel(AssetType t) => t switch
    {
        AssetType.Texture      => "Textures",
        AssetType.SkeletalMesh => "Skeletal Meshes",
        AssetType.StaticMesh   => "Static Meshes",
        AssetType.Animation    => "Animations",
        AssetType.Audio        => "Audio",
        AssetType.Material     => "Materials",
        AssetType.Blueprint    => "Blueprints",
        AssetType.Level        => "Levels",
        AssetType.Cinematic    => "Cinematics",
        _                      => "Other"
    };

    // ─── File Tree Building ───────────────────────────────────────────────────

    private void BuildFileTree(IReadOnlyList<AssetInfo> assets) => RootNodes = BuildFileTreeCore(assets);

    private List<AssetTreeNode> BuildFileTreeCore(IReadOnlyList<AssetInfo> assets)
    {
        var root = new AssetTreeNode { Name = _gameConfig.DisplayName, IsFolder = true };

        foreach (var asset in assets)
        {
            var parts = asset.VirtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            // Walk/create the folder path using a per-folder index for O(1) lookups.
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current.FolderIndex ??= new Dictionary<string, AssetTreeNode>(StringComparer.OrdinalIgnoreCase);
                if (!current.FolderIndex.TryGetValue(parts[i], out var child))
                {
                    child = new AssetTreeNode { Name = parts[i], IsFolder = true };
                    current.Children.Add(child);
                    current.FolderIndex[parts[i]] = child;
                }
                current = child;
            }

            // Add the asset leaf node
            current.Children.Add(new AssetTreeNode
            {
                Name = asset.Name,
                IsFolder = false,
                Asset = asset
            });
        }

        return new List<AssetTreeNode> { root };
    }
}

/// <summary>A node in the left-panel folder tree — either a directory or an asset leaf.</summary>
public class AssetTreeNode
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label shown in the tree.
    /// Strips _unpacked/_packed suffixes and replaces hyphens with spaces so
    /// "world-santa-barbara_unpacked" becomes "world santa barbara".
    /// </summary>
    public string DisplayName
    {
        get
        {
            var n = Name;
            // Strip common extractor suffixes
            foreach (var suffix in new[] { "_unpacked", "_packed", "_extracted" })
            {
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    n = n[..^suffix.Length];
                    break;
                }
            }
            // Replace hyphens/underscores with spaces for readability
            n = n.Replace('-', ' ').Replace('_', ' ');
            // Title-case each word
            if (n.Length > 0)
                n = string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
            return n;
        }
    }

    public bool IsFolder { get; set; }
    public AssetInfo? Asset { get; set; }
    public List<AssetTreeNode> Children { get; set; } = new();
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Build-time only: O(1) child-folder lookup keyed by folder name. Lets BuildFileTree run in
    /// O(n) instead of a linear Children scan per path segment — essential at 860k-file scale.
    /// Not part of the bound view model.
    /// </summary>
    internal Dictionary<string, AssetTreeNode>? FolderIndex;
}

/// <summary>
/// One type-category group shown in the center panel — e.g. "Textures (2 991)".
/// The TreeView expands it to reveal its individual asset entries.
/// </summary>
public class AssetGroupNode
{
    public AssetType       Type      { get; set; }
    public string          TypeLabel { get; set; } = "";
    public List<AssetInfo> Assets    { get; set; } = new();
    public string          CountLabel => $"{Assets.Count:N0}";

    public string HeaderIcon => Type switch
    {
        AssetType.Texture      => "▦",   // checkered square
        AssetType.SkeletalMesh => "🦴",  // bone
        AssetType.StaticMesh   => "◈",   // solid diamond/mesh
        AssetType.Animation    => "🏃",  // running figure
        AssetType.Audio        => "🎵",  // music note
        AssetType.Material     => "◑",   // half-filled sphere
        AssetType.Blueprint    => "📋",  // clipboard
        AssetType.Level        => "🗺",  // map
        AssetType.Cinematic    => "🎬",  // clapperboard
        _                      => "📦"   // box
    };
}
