using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameAssetExplorer.Exporters.MetadataExporter;

/// <summary>
/// Exports full asset metadata as a JSON sidecar file.
/// This is the file that the Blender and Unreal Engine addons will read.
///
/// Every exported asset gets a matching .json file with the same base name.
/// e.g. Cal_Body.fbx gets a Cal_Body.json sitting next to it.
///
/// The JSON includes:
///   - Virtual path and asset name
///   - Asset type and engine class name
///   - Compressed/uncompressed sizes
///   - All raw engine properties (materials, LOD distances, bone names, etc.)
///   - Type-specific metadata (texture format, dimensions, skeleton data, etc.)
///   - Export settings used (scale, format, etc.)
///   - Timestamp and source game info
/// </summary>
public class JsonMetadataExporter : IExporter
{
    public string ExporterName => "JSON Metadata Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => Enum.GetValues<AssetType>().ToList();
    public IReadOnlyList<string> OutputExtensions => new[] { ".json" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ExportResult> ExportAsync(
        AssetData assetData,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            var outputPath = BuildOutputPath(assetData.Info, outputDirectory, settings);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!settings.OverwriteExisting && File.Exists(outputPath))
                return new ExportResult { Success = true, OutputPath = outputPath, SourceAsset = assetData.Info };

            var metadata = BuildMetadata(assetData, settings);
            var json = JsonSerializer.Serialize(metadata, JsonOptions);

            await File.WriteAllTextAsync(outputPath, json);

            return new ExportResult
            {
                Success = true,
                OutputPath = outputPath,
                SourceAsset = assetData.Info,
                FileSizeBytes = new FileInfo(outputPath).Length,
                Duration = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new ExportResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                SourceAsset = assetData.Info
            };
        }
    }

    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();

        for (int i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExportAsync(assets[i], outputDirectory, settings));
        }

        return results;
    }

    // ─── Metadata Construction ────────────────────────────────────────────────

    private static AssetMetadataDocument BuildMetadata(AssetData data, ExportSettings settings)
    {
        var doc = new AssetMetadataDocument
        {
            ExportedAt = DateTime.UtcNow,
            ToolVersion = "1.0.0",
            AssetPath = data.Info.VirtualPath,
            AssetName = data.Info.Name,
            AssetType = data.Info.Type.ToString(),
            EngineClassName = data.Info.EngineClassName,
            CompressedSize = data.Info.CompressedSize,
            UncompressedSize = data.Info.UncompressedSize,
            SourceArchive = Path.GetFileName(data.Info.ArchivePath),
            Properties = settings.IncludeAllProperties ? data.RawProperties : null
        };

        // Add type-specific metadata
        switch (data)
        {
            case TextureAssetData tex:
                doc.TextureInfo = new TextureMetadata
                {
                    Width = tex.Width,
                    Height = tex.Height,
                    Format = tex.SourceFormat,
                    TextureGroup = tex.TextureGroup,
                    IsSrgb = tex.IsSrgb,
                    MipCount = tex.Mips.Count
                };
                break;

            case MeshAssetData mesh:
                doc.MeshInfo = new MeshMetadata
                {
                    IsSkeletal = mesh.IsSkeletal,
                    LodCount = mesh.Lods.Count,
                    LodDetails = mesh.Lods.Select(l => new LodMetadata
                    {
                        LodIndex = l.LodIndex,
                        ScreenSize = l.ScreenSize,
                        VertexCount = l.VertexCount,
                        TriangleCount = l.TriangleCount
                    }).ToList(),
                    MaterialSlots = mesh.MaterialSlots.Select(m => new MaterialSlotMetadata
                    {
                        SlotIndex = m.SlotIndex,
                        SlotName = m.MaterialName,
                        MaterialPath = m.MaterialPath
                    }).ToList(),
                    BoneCount = mesh.Skeleton?.Bones.Count ?? 0,
                    BoneNames = mesh.Skeleton?.Bones.Select(b => b.Name).ToList()
                };
                break;

            case AnimationAssetData anim:
                doc.AnimationInfo = new AnimationMetadata
                {
                    FrameRate = anim.FrameRate,
                    FrameCount = anim.FrameCount,
                    Duration = anim.Duration,
                    SkeletonPath = anim.SkeletonPath,
                    TrackCount = anim.Tracks.Count
                };
                break;

            case AudioAssetData audio:
                doc.AudioInfo = new AudioMetadata
                {
                    Duration = audio.Duration,
                    SampleRate = audio.SampleRate,
                    Channels = audio.Channels,
                    Format = audio.SourceFormat
                };
                break;
        }

        return doc;
    }

    private static string BuildOutputPath(AssetInfo info, string outputDir, ExportSettings settings)
    {
        if (settings.PreserveVirtualPaths)
        {
            var virtualPath = info.VirtualPath.TrimStart('/');
            if (virtualPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                virtualPath = virtualPath.Substring(5);

            var relativeDir = Path.GetDirectoryName(virtualPath) ?? "";
            return Path.Combine(outputDir, relativeDir, info.Name + ".json");
        }

        return Path.Combine(outputDir, info.Name + ".json");
    }
}

// ─── JSON Document Shape ──────────────────────────────────────────────────────

public class AssetMetadataDocument
{
    public DateTime ExportedAt { get; set; }
    public string ToolVersion { get; set; } = string.Empty;
    public string AssetPath { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string EngineClassName { get; set; } = string.Empty;
    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public string SourceArchive { get; set; } = string.Empty;
    public TextureMetadata? TextureInfo { get; set; }
    public MeshMetadata? MeshInfo { get; set; }
    public AnimationMetadata? AnimationInfo { get; set; }
    public AudioMetadata? AudioInfo { get; set; }
    public Dictionary<string, object?>? Properties { get; set; }
}

public class TextureMetadata
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = string.Empty;
    public string TextureGroup { get; set; } = string.Empty;
    public bool IsSrgb { get; set; }
    public int MipCount { get; set; }
}

public class MeshMetadata
{
    public bool IsSkeletal { get; set; }
    public int LodCount { get; set; }
    public List<LodMetadata> LodDetails { get; set; } = new();
    public List<MaterialSlotMetadata> MaterialSlots { get; set; } = new();
    public int BoneCount { get; set; }
    public List<string>? BoneNames { get; set; }
}

public class LodMetadata
{
    public int LodIndex { get; set; }
    public float ScreenSize { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
}

public class MaterialSlotMetadata
{
    public int SlotIndex { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public string MaterialPath { get; set; } = string.Empty;
}

public class AnimationMetadata
{
    public float FrameRate { get; set; }
    public int FrameCount { get; set; }
    public float Duration { get; set; }
    public string SkeletonPath { get; set; } = string.Empty;
    public int TrackCount { get; set; }
}

public class AudioMetadata
{
    public float Duration { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Format { get; set; } = string.Empty;
}
