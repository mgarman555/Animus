using GameAssetExplorer.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Generic asset inspector — opens for any asset type that doesn't have a dedicated viewer
/// (animations, audio, materials, blueprints, levels, unknown). Shows the same property list
/// the right-pane info panel renders, but in its own window.
/// </summary>
public partial class AssetInspectorWindow : Window
{
    public AssetInspectorWindow(AssetData? data, AssetInfo info)
    {
        InitializeComponent();

        Title          = $"Inspector — {info.Name}";
        LblName.Text   = info.Name;
        LblPath.Text   = info.VirtualPath;
        LblIcon.Text   = IconForType(info.Type);

        AddProp("Name",          info.Name);
        AddProp("Type",          info.Type.ToString());
        AddProp("Path",          info.VirtualPath);
        AddProp("Archive",       Path.GetFileName(info.ArchivePath));
        AddPropBytes("Compressed",   info.CompressedSize);
        AddPropBytes("Uncompressed", info.UncompressedSize);
        AddProp("Class",         info.EngineClassName);
        AddProp("Encrypted",     info.IsEncrypted ? "Yes" : "No");

        switch (data)
        {
            case AnimationAssetData a:
                AddSeparator();
                AddProp("Frame Rate",  $"{a.FrameRate:F1} fps");
                AddProp("Frames",      a.FrameCount.ToString());
                AddProp("Duration",    $"{a.Duration:F2} s");
                AddProp("Track Count", a.Tracks.Count.ToString());
                if (!string.IsNullOrEmpty(a.SkeletonPath))
                    AddProp("Skeleton", a.SkeletonPath);
                break;

            case AudioAssetData au:
                AddSeparator();
                AddProp("Duration",    $"{au.Duration:F2} s");
                AddProp("Sample Rate", $"{au.SampleRate} Hz");
                AddProp("Channels",    au.Channels.ToString());
                AddProp("Format",      au.SourceFormat);
                break;

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
                AddProp("Mesh Type",      m.IsSkeletal ? "Skeletal" : "Static");
                AddProp("LOD Count",      m.Lods.Count.ToString());
                AddProp("Bone Count",     m.Skeleton?.Bones.Count.ToString() ?? "—");
                AddProp("Material Slots", m.MaterialSlots.Count.ToString());
                if (m.Lods.Count > 0)
                {
                    AddSeparator();
                    for (int i = 0; i < m.Lods.Count; i++)
                        AddProp($"LOD {i}", $"{m.Lods[i].VertexCount:N0}v  {m.Lods[i].TriangleCount:N0}t");
                }
                break;
        }

        if (data?.RawProperties is { Count: > 0 })
        {
            var pubKeys = data.RawProperties
                .Where(kv => !kv.Key.StartsWith('_') && kv.Value != null)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pubKeys.Count > 0)
            {
                AddSeparator();
                foreach (var (k, v) in pubKeys)
                {
                    string label = k.Contains('.')
                        ? "  " + k[(k.LastIndexOf('.') + 1)..]
                        : k;
                    AddProp(label, v!.ToString());
                }
            }
        }
    }

    private static string IconForType(AssetType t) => t switch
    {
        AssetType.Texture      => "▦",
        AssetType.SkeletalMesh => "🦴",
        AssetType.StaticMesh   => "◈",
        AssetType.Animation    => "🏃",
        AssetType.Audio        => "🎵",
        AssetType.Material     => "◑",
        AssetType.Blueprint    => "📋",
        AssetType.Level        => "🗺",
        AssetType.Cinematic    => "🎬",
        _                      => "📦",
    };

    private void AddProp(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text       = label,
            FontSize   = 12,
            Foreground = (Brush)FindResource("TextSub"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var val = new TextBlock
        {
            Text         = value,
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(val, 1);
        row.Children.Add(lbl);
        row.Children.Add(val);
        PropsPanel.Children.Add(row);
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
        PropsPanel.Children.Add(new Separator
        {
            Margin     = new Thickness(0, 8, 0, 8),
            Background = (Brush)FindResource("Border"),
        });
    }
}
