using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Interfaces;

/// <summary>
/// Contract for anything that can export assets to disk.
/// TextureExporter, ModelExporter, MetadataExporter all implement this.
/// </summary>
public interface IExporter
{
    /// <summary>Display name, e.g. "PNG Texture Exporter"</summary>
    string ExporterName { get; }

    /// <summary>Which asset types this exporter handles, e.g. [AssetType.Texture]</summary>
    IReadOnlyList<AssetType> SupportedTypes { get; }

    /// <summary>Output file extension(s) this exporter produces, e.g. [".png"]</summary>
    IReadOnlyList<string> OutputExtensions { get; }

    /// <summary>
    /// Export a single asset to the specified output directory.
    /// Returns the full path of the exported file.
    /// </summary>
    Task<ExportResult> ExportAsync(
        AssetData asset,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null);

    /// <summary>
    /// Batch export multiple assets. Shows aggregate progress.
    /// </summary>
    Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
