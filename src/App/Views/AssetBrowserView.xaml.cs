using GameAssetExplorer.App.ViewModels;
using GameAssetExplorer.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace GameAssetExplorer.App.Views;

public partial class AssetBrowserView : UserControl
{
    private AssetBrowserViewModel? _vm;

    public AssetBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _vm = DataContext as AssetBrowserViewModel;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.InitializeAsync();
    }

    // ── Left Panel: Folder Tree Navigation ───────────────────────────────────

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm == null || e.NewValue is not AssetTreeNode node) return;

        if (node.IsFolder)
            _vm.SetVisibleAssets(CollectLeafAssets(node));
        else if (node.Asset != null)
            _vm.SetVisibleAssets(new List<AssetInfo> { node.Asset });
    }

    private static List<AssetInfo> CollectLeafAssets(AssetTreeNode node)
    {
        var results = new List<AssetInfo>();
        Collect(node, results);
        return results;

        static void Collect(AssetTreeNode n, List<AssetInfo> acc)
        {
            if (!n.IsFolder && n.Asset != null) acc.Add(n.Asset);
            foreach (var child in n.Children) Collect(child, acc);
        }
    }

    // ── Center Panel: Single-click → info, Double-click → viewer ─────────────

    private async void OnAssetGroupTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm == null) return;
        if (e.NewValue is not AssetInfo asset) return;

        _vm.SelectedAsset = asset;
        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        RefreshPreviewPanel();
        // No automatic viewer — double-click required
    }

    // Double-click in the center type-grouped list.
    private async void OnAssetTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedAsset is not AssetInfo asset) return;
        e.Handled = true;
        await OpenViewerForSelectedAsync(asset);
    }

    // Double-click a file leaf in the left folder tree opens the same viewer.
    private async void OnFileTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (FileTree.SelectedItem is not AssetTreeNode node || node.IsFolder || node.Asset == null)
            return; // folders keep their default expand/collapse behaviour
        e.Handled = true;
        _vm.SelectedAsset = node.Asset;
        await OpenViewerForSelectedAsync(node.Asset);
    }

    /// <summary>
    /// Load the asset (if needed) and open the right window based on the ACTUALLY DECODED type:
    /// a mesh → 3D viewer, a texture → image preview, anything else → the inspector. Routing on the
    /// decoded type (not the listing-time heuristic) means a mesh mis-classified as Unknown still
    /// opens the 3D viewer.
    /// </summary>
    private async Task OpenViewerForSelectedAsync(AssetInfo asset)
    {
        if (_vm == null) return;

        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        RefreshPreviewPanel();

        switch (_vm.PreviewedAsset)
        {
            case TextureAssetData tex:
                OpenTexturePreview(tex);
                break;
            case MeshAssetData mesh:
                OpenMeshViewer(mesh, asset);
                break;
            default:
                OpenInspector(_vm.PreviewedAsset, asset);
                break;
        }
    }

    private void OpenMeshViewer(AssetData? data, AssetInfo info)
    {
        var win = new SkeletalMeshViewerWindow(data, info)
        {
            Owner = Window.GetWindow(this)
        };
        win.Show();
    }

    private void OpenTexturePreview(TextureAssetData tex)
    {
        var win = new TexturePreviewWindow(tex)
        {
            Owner = Window.GetWindow(this)
        };
        win.Show();
    }

    private void OpenInspector(AssetData? data, AssetInfo info)
    {
        var win = new AssetInspectorWindow(data, info)
        {
            Owner = Window.GetWindow(this)
        };
        win.Show();
    }

    // ── Right Panel: Preview & Properties ────────────────────────────────────

    private void RefreshPreviewPanel()
    {
        if (_vm?.PreviewedAsset == null)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PropertiesPanel.Children.Clear();
            return;
        }

        var asset = _vm.PreviewedAsset;

        // ── Image preview ──────────────────────────────────────────────────
        PreviewImage.Visibility = Visibility.Collapsed;

        if (asset is TextureAssetData tex)
        {
            var bmp = TextureDecodeHelper.TryDecode(tex);
            if (bmp != null)
            {
                PreviewImage.Source     = bmp;
                PreviewImage.Visibility = Visibility.Visible;
            }
        }
        else
        {
            var ext = Path.GetExtension(asset.Info.VirtualPath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga")
            {
                if (asset.RawProperties?.TryGetValue("_FilePath", out var pathObj) == true
                    && pathObj is string filePath && File.Exists(filePath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource   = new Uri(filePath, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        PreviewImage.Source     = bmp;
                        PreviewImage.Visibility = Visibility.Visible;
                    }
                    catch { }
                }
            }
        }

        // ── Info / properties list ─────────────────────────────────────────
        PropertiesPanel.Children.Clear();
        AddProp("Name",      asset.Info.Name);
        AddProp("Type",      asset.Info.Type.ToString());
        AddProp("Path",      asset.Info.VirtualPath);
        AddProp("Archive",   Path.GetFileName(asset.Info.ArchivePath));
        AddPropBytes("Compressed",   asset.Info.CompressedSize);
        AddPropBytes("Uncompressed", asset.Info.UncompressedSize);
        AddProp("Class",     asset.Info.EngineClassName);
        AddProp("Encrypted", asset.Info.IsEncrypted ? "Yes" : "No");

        switch (asset)
        {
            case TextureAssetData t:
                AddSeparator();
                AddProp("Dimensions",    $"{t.Width} × {t.Height}");
                AddProp("Format",        t.SourceFormat);
                AddProp("sRGB",          t.IsSrgb ? "Yes" : "No");
                AddProp("Texture Group", t.TextureGroup);
                AddProp("Mip Levels",    t.Mips.Count.ToString());
                break;

            case MeshAssetData m:
                AddSeparator();
                AddProp("Mesh Type", m.Info.Type == AssetType.Level ? "Level"
                                   : m.IsSkeletal ? "Skeletal" : "Static");
                AddProp("LOD Count",      m.Lods.Count.ToString());
                AddProp("Bone Count",     m.Skeleton?.Bones.Count.ToString() ?? "—");
                AddProp("Material Slots", m.MaterialSlots.Count.ToString());
                foreach (var slot in m.MaterialSlots)
                    AddProp($"  Slot {slot.SlotIndex}", slot.MaterialName);
                if (m.Lods.Count > 0)
                {
                    AddSeparator();
                    for (int i = 0; i < m.Lods.Count; i++)
                        AddProp($"LOD {i}", $"{m.Lods[i].VertexCount:N0}v  {m.Lods[i].TriangleCount:N0}t");
                }
                break;

            case AnimationAssetData a:
                AddSeparator();
                AddProp("Frame Rate",  $"{a.FrameRate:F1} fps");
                AddProp("Frames",      a.FrameCount.ToString());
                AddProp("Duration",    $"{a.Duration:F2} s");
                AddProp("Track Count", a.Tracks.Count.ToString());
                break;

            case AudioAssetData au:
                AddSeparator();
                AddProp("Duration",    $"{au.Duration:F2} s");
                AddProp("Sample Rate", $"{au.SampleRate} Hz");
                AddProp("Channels",    au.Channels.ToString());
                AddProp("Format",      au.SourceFormat);
                break;
        }

        if (asset.RawProperties != null)
        {
            // Group keys: Texture[N] entries first (sorted), then everything else.
            // Keys starting with '_' are internal and not displayed.
            // Keys ending in ".path" or ".hash" are sub-fields; indent them slightly.
            var pubKeys = asset.RawProperties
                .Where(kv => !kv.Key.StartsWith('_') && kv.Value != null)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pubKeys.Count > 0)
            {
                AddSeparator();
                string? lastGroup = null;
                foreach (var (k, v) in pubKeys)
                {
                    // Detect group boundary: first segment before '[' or '.'
                    string groupKey = k.Contains('[') ? k[..k.IndexOf('[')] : (k.Contains('.') ? k[..k.IndexOf('.')] : k);
                    if (lastGroup != null && groupKey != lastGroup
                        && !k.Contains('.') && !k.Contains('['))
                        AddSeparator();
                    lastGroup = groupKey;

                    // Sub-fields (.path, .hash) get an indented label
                    string label = k.Contains('.')
                        ? "  " + k[(k.LastIndexOf('.') + 1)..]
                        : k;

                    AddProp(label, v!.ToString());
                }
            }
        }
    }

    // ── Right-click Export Context Menu ──────────────────────────────────────

    private async void OnExportAssetMenuClick(object sender, RoutedEventArgs e)
    {
        var asset = GetAssetFromMenuSender(sender);
        if (asset == null || _vm == null) return;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select export output folder",
            SelectedPath = _vm.ExportOutputPath
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _vm.SelectedAsset   = asset;
        _vm.ExportOutputPath = dlg.SelectedPath;
        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        await _vm.ExportCurrentAssetCommand.ExecuteAsync(dlg.SelectedPath);
    }

    private async void OnExportTextureMenuClick(object sender, RoutedEventArgs e)
    {
        var asset = GetAssetFromMenuSender(sender);
        if (asset == null || _vm == null || asset.Type != AssetType.Texture) return;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select export output folder",
            SelectedPath = _vm.ExportOutputPath
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _vm.SelectedAsset    = asset;
        _vm.ExportOutputPath = dlg.SelectedPath;
        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        await _vm.ExportCurrentAssetCommand.ExecuteAsync(dlg.SelectedPath);
    }

    private async void OnExportMeshMenuClick(object sender, RoutedEventArgs e)
    {
        var asset = GetAssetFromMenuSender(sender);
        if (asset == null || _vm == null) return;
        if (asset.Type is not (AssetType.SkeletalMesh or AssetType.StaticMesh)) return;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select export output folder",
            SelectedPath = _vm.ExportOutputPath
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _vm.SelectedAsset    = asset;
        _vm.ExportOutputPath = dlg.SelectedPath;
        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        await _vm.ExportCurrentAssetCommand.ExecuteAsync(dlg.SelectedPath);
    }

    private async void OnExportWithMetaMenuClick(object sender, RoutedEventArgs e)
    {
        var asset = GetAssetFromMenuSender(sender);
        if (asset == null || _vm == null) return;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select export output folder",
            SelectedPath = _vm.ExportOutputPath
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _vm.SelectedAsset              = asset;
        _vm.ExportOutputPath           = dlg.SelectedPath;
        _vm.ExportSettings.ExportMetadataJson = true;
        await _vm.PreviewSelectedAssetCommand.ExecuteAsync(null);
        await _vm.ExportCurrentAssetCommand.ExecuteAsync(dlg.SelectedPath);
    }

    // ── Merge Queue Handlers ─────────────────────────────────────────────────

    private void OnAddToMergeQueueMenuClick(object sender, RoutedEventArgs e)
    {
        var asset = GetAssetFromMenuSender(sender);
        if (asset == null || _vm == null) return;
        _vm.AddToMergeQueueCommand.Execute(asset);
    }

    private void OnRemoveFromMergeQueueClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // The button's Tag is bound to the AssetInfo
        if (sender is System.Windows.Controls.Button btn && btn.Tag is AssetInfo asset)
            _vm.RemoveFromMergeQueueCommand.Execute(asset);
    }

    private async void OnExportMergedClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select folder for merged OBJ export",
            SelectedPath = _vm.ExportOutputPath
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _vm.ExportOutputPath = dlg.SelectedPath;
        await _vm.ExportMergedCommand.ExecuteAsync(dlg.SelectedPath);
    }

    private static AssetInfo? GetAssetFromMenuSender(object sender)
    {
        if (sender is MenuItem item &&
            item.Parent is ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement fe)
            return fe.DataContext as AssetInfo;
        return null;
    }

    // ── Properties Panel Helpers ─────────────────────────────────────────────

    private void AddProp(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text       = label,
            FontSize   = 11,
            Foreground = (Brush)FindResource("TextSub"),
        };
        var val = new TextBlock
        {
            Text         = value,
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(val, 1);
        row.Children.Add(lbl);
        row.Children.Add(val);
        PropertiesPanel.Children.Add(row);
    }

    private void AddPropBytes(string label, long bytes)
    {
        string display = bytes switch
        {
            < 1024               => $"{bytes} B",
            < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _                    => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
        AddProp(label, display);
    }

    private void AddSeparator()
    {
        PropertiesPanel.Children.Add(new Separator
        {
            Margin     = new Thickness(0, 6, 0, 6),
            Background = (Brush)FindResource("Border")
        });
    }
}
