using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.ModelExporter.Fbx;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// Exports meshes (and, via <see cref="FbxSceneBuilder"/>, skeletons/skinning/animation as those
/// land) to BINARY FBX 7.4 — the format Blender actually imports. Replaces the old ASCII writer,
/// which Blender refused to read.
///
/// Geometry, per-vertex normals (from <see cref="LodData.Normals"/> when available, else computed),
/// UV0 and per-submesh phong materials are emitted. Skin weights and animation are added by the
/// scene builder in later phases; texture image sidecars are wired with the texture-export step.
/// </summary>
public class FbxModelExporter : IExporter
{
    public string ExporterName => "FBX Model Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.StaticMesh, AssetType.SkeletalMesh };
    public IReadOnlyList<string> OutputExtensions => new[] { ".fbx" };

    public async Task<ExportResult> ExportAsync(
        AssetData assetData, string outputDirectory, ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        if (assetData is not MeshAssetData mesh)
            return Fail(assetData, "FBX exporter only handles MeshAssetData.");
        if (mesh.Lods.Count == 0 || mesh.Lods[0].VertexBuffer == null)
            return Fail(assetData, "Mesh has no geometry data.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var meshName = FbxName.Sanitize(mesh.Info.Name, "mesh", 0);
            var fbxPath  = Path.Combine(outputDirectory, meshName + ".fbx");

            int lodIdx = settings.ModelLodLevel >= 0 && settings.ModelLodLevel < mesh.Lods.Count
                ? settings.ModelLodLevel : 0;
            var lod = mesh.Lods[lodIdx];

            await Task.Run(() =>
            {
                var nodes = FbxSceneBuilder.Build(mesh, lod, settings);
                using var fs = File.Create(fbxPath);
                FbxBinaryWriter.Write(fs, nodes);
            });

            sw.Stop();
            return new ExportResult
            {
                Success = true, OutputPath = fbxPath, SourceAsset = assetData.Info,
                FileSizeBytes = new FileInfo(fbxPath).Length, Duration = sw.Elapsed
            };
        }
        catch (Exception ex) { return Fail(assetData, ex.Message); }
    }

    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets, string outputDirectory, ExportSettings settings,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();
        for (int i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress { Total = assets.Count, Completed = i, CurrentAsset = assets[i].Info.Name });
            results.Add(await ExportAsync(assets[i], outputDirectory, settings));
        }
        return results;
    }

    private static ExportResult Fail(AssetData asset, string message) =>
        new() { Success = false, ErrorMessage = message, SourceAsset = asset.Info };
}
