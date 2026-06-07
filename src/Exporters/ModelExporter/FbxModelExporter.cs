using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// FBX model exporter — full implementation coming in the next milestone.
/// Skeleton, bone axis correction, material slots, and LOD selection all handled here.
/// Placeholder ensures the project compiles while we build the texture/browse pipeline first.
/// </summary>
public class FbxModelExporter : IExporter
{
    public string ExporterName => "FBX Model Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.StaticMesh, AssetType.SkeletalMesh };
    public IReadOnlyList<string> OutputExtensions => new[] { ".fbx" };

    public Task<ExportResult> ExportAsync(
        AssetData assetData,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        // Full FBX export implementation coming next milestone
        return Task.FromResult(new ExportResult
        {
            Success = false,
            ErrorMessage = "FBX export is not yet implemented. Coming in the next build.",
            SourceAsset = assetData.Info
        });
    }

    public Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ExportResult>>(
            assets.Select(a => new ExportResult
            {
                Success = false,
                ErrorMessage = "FBX export is not yet implemented.",
                SourceAsset = a.Info
            }).ToList()
        );
    }
}
