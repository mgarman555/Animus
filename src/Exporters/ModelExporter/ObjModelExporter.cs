using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using System.Globalization;
using System.Text;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// Exports mesh assets as Wavefront OBJ + MTL files.
///
/// OBJ is the right choice here: it's pure ASCII, zero dependencies, and both Blender
/// and Unreal Engine's import pipeline handle it flawlessly. Use this for all geometry
/// exports until the FBX SDK integration lands.
///
/// What this produces:
///   - {meshName}.obj  — geometry: vertices, normals (if available), UVs (if available),
///                       faces grouped by submesh / material slot
///   - {meshName}.mtl  — material library with one entry per material slot
///   - {meshName}_meta.json — full asset metadata (written by JsonMetadataExporter)
///
/// Coordinate system:
///   Unreal is Left-Handed, Y-forward, Z-up.
///   OBJ convention (and Blender default) is Right-Handed, Y-up.
///   The exporter applies the conversion: OBJ X = UE X, OBJ Y = UE Z, OBJ Z = -UE Y
///   so the mesh comes in oriented correctly in Blender without needing manual rotation.
///   Toggle ApplyCoordConversion = false in settings if you're importing back into UE.
/// </summary>
public class ObjModelExporter : IExporter
{
    public string ExporterName => "OBJ Model Exporter";

    public IReadOnlyList<AssetType> SupportedTypes => new[]
    {
        AssetType.StaticMesh,
        AssetType.SkeletalMesh
    };

    public IReadOnlyList<string> OutputExtensions => new[] { ".obj", ".mtl" };

    // ─── Single-asset export ──────────────────────────────────────────────────

    public async Task<ExportResult> ExportAsync(
        AssetData assetData,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        if (assetData is not MeshAssetData mesh)
            return Fail(assetData, "OBJ exporter only handles MeshAssetData.");

        if (mesh.Lods.Count == 0 || mesh.Lods[0].VertexBuffer == null)
            return Fail(assetData, "Mesh has no geometry data.");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(outputDirectory);

            var meshName = SanitizeFileName(mesh.Info.Name);
            var objPath  = Path.Combine(outputDirectory, meshName + ".obj");
            var mtlPath  = Path.Combine(outputDirectory, meshName + ".mtl");

            // LOD selection: respect settings.ModelLodLevel, default to LOD 0
            int lodIdx = settings.ModelLodLevel >= 0 && settings.ModelLodLevel < mesh.Lods.Count
                ? settings.ModelLodLevel : 0;
            var lod = mesh.Lods[lodIdx];

            await Task.Run(() =>
            {
                WriteObj(objPath, mtlPath, meshName, lod, mesh.MaterialSlots, settings);
                WriteMtl(mtlPath, meshName, mesh.MaterialSlots);
            });

            sw.Stop();
            return new ExportResult
            {
                Success      = true,
                OutputPath   = objPath,
                SourceAsset  = assetData.Info,
                FileSizeBytes = new FileInfo(objPath).Length,
                Duration     = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            return Fail(assetData, ex.Message);
        }
    }

    // ─── Batch export ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();
        int total = assets.Count;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress
            {
                Total     = total,
                Completed = i,
                CurrentAsset = assets[i].Info.Name
            });

            var result = await ExportAsync(assets[i], outputDirectory, settings);
            results.Add(result);
        }

        return results;
    }

    // ─── OBJ writing ─────────────────────────────────────────────────────────

    private static void WriteObj(
        string objPath,
        string mtlPath,
        string meshName,
        LodData lod,
        IReadOnlyList<MaterialSlot> materialSlots,
        ExportSettings settings)
    {
        const int VERT_STRIDE = 12; // float32 × 3
        const int IDX_STRIDE  = 4;  // int32

        var vb  = lod.VertexBuffer!;
        var ib  = lod.IndexBuffer;
        int nv  = lod.VertexCount;
        int ni  = ib != null ? ib.Length / IDX_STRIDE : 0;
        bool hasUv  = lod.UvBuffer != null;
        float scale = settings.ModelScaleFactor;

        // Coord conversion: UE (X,Y,Z right-hand-Y-up) → OBJ (X,Z,-Y)
        // UE is left-handed Y-forward Z-up; OBJ is right-handed Y-up.
        // Transformation: objX = ueX, objY = ueZ, objZ = -ueY
        bool convert = settings.ApplyBlenderBoneCorrection;

        var sb = new StringBuilder();
        sb.AppendLine("# OBJ exported by Game Asset Explorer");
        sb.AppendLine($"# Mesh: {meshName}");
        sb.AppendLine($"# Vertices: {nv:N0}");
        sb.AppendLine($"# Triangles: {lod.TriangleCount:N0}");
        sb.AppendLine($"mtllib {Path.GetFileName(mtlPath)}");
        sb.AppendLine($"o {meshName}");
        sb.AppendLine();

        // Vertex positions
        for (int v = 0; v < nv; v++)
        {
            int off = v * VERT_STRIDE;
            float x = BitConverter.ToSingle(vb, off)     * scale;
            float y = BitConverter.ToSingle(vb, off + 4) * scale;
            float z = BitConverter.ToSingle(vb, off + 8) * scale;

            if (convert)
            {
                // UE→Blender: swap Y and Z, negate new Z (which was UE Y)
                sb.AppendLine(F3("v", x, z, -y));
            }
            else
            {
                sb.AppendLine(F3("v", x, y, z));
            }
        }

        sb.AppendLine();

        // UV coordinates
        if (hasUv && lod.UvBuffer != null)
        {
            var uvb = lod.UvBuffer;
            const int UV_STRIDE = 8;
            for (int v = 0; v < nv; v++)
            {
                int off = v * UV_STRIDE;
                float u  = BitConverter.ToSingle(uvb, off);
                float vt = BitConverter.ToSingle(uvb, off + 4);
                // OBJ V is flipped relative to DirectX/UE UV space
                sb.AppendLine(F2("vt", u, 1f - vt));
            }
            sb.AppendLine();
        }

        if (ib == null || ni == 0)
        {
            // No index buffer — just write vertices, no faces
            File.WriteAllText(objPath, sb.ToString());
            return;
        }

        // Build submesh boundaries. If none recorded, treat the whole LOD as one group.
        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo>
              {
                  new() { Name = meshName, IndexStart = 0, IndexCount = ni }
              };

        // Write face groups, one per submesh
        for (int s = 0; s < submeshes.Count; s++)
        {
            var sub = submeshes[s];
            string groupName = string.IsNullOrEmpty(sub.Name) ? $"submesh_{s}" : sub.Name;

            // Pick a material: try matching by slot index, fall back to slot 0
            var mat = materialSlots.Count > s
                ? materialSlots[s]
                : (materialSlots.Count > 0 ? materialSlots[0] : null);

            sb.AppendLine($"g {groupName}");
            if (mat != null)
                sb.AppendLine($"usemtl {SanitizeMatName(mat.MaterialName, s)}");

            int end = sub.IndexStart + sub.IndexCount;
            for (int i = sub.IndexStart; i < end; i += 3)
            {
                if (i + 2 >= ni) break;
                int i0 = BitConverter.ToInt32(ib, i       * IDX_STRIDE) + 1; // 1-indexed
                int i1 = BitConverter.ToInt32(ib, (i + 1) * IDX_STRIDE) + 1;
                int i2 = BitConverter.ToInt32(ib, (i + 2) * IDX_STRIDE) + 1;

                if (hasUv)
                    sb.AppendLine($"f {i0}/{i0} {i1}/{i1} {i2}/{i2}");
                else
                    sb.AppendLine($"f {i0} {i1} {i2}");
            }

            sb.AppendLine();
        }

        File.WriteAllText(objPath, sb.ToString());
    }

    private static void WriteMtl(
        string mtlPath,
        string meshName,
        IReadOnlyList<MaterialSlot> materialSlots)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MTL material library — Game Asset Explorer");
        sb.AppendLine($"# Mesh: {meshName}");
        sb.AppendLine();

        if (materialSlots.Count == 0)
        {
            // Always emit at least a default material so the OBJ is valid
            sb.AppendLine("newmtl default_material");
            sb.AppendLine("Ka 1.0 1.0 1.0");
            sb.AppendLine("Kd 0.8 0.8 0.8");
            sb.AppendLine("Ks 0.0 0.0 0.0");
            sb.AppendLine("d 1.0");
            sb.AppendLine("illum 1");
        }
        else
        {
            for (int i = 0; i < materialSlots.Count; i++)
            {
                var slot   = materialSlots[i];
                var matName = SanitizeMatName(slot.MaterialName, i);

                sb.AppendLine($"newmtl {matName}");
                sb.AppendLine("Ka 1.0 1.0 1.0");
                sb.AppendLine("Kd 0.8 0.8 0.8");
                sb.AppendLine("Ks 0.05 0.05 0.05");
                sb.AppendLine("Ns 10.0");
                sb.AppendLine("d 1.0");
                sb.AppendLine("illum 2");
                // Texture maps go here once we have the diffuse data path:
                // sb.AppendLine($"map_Kd {slot.MaterialName}_D.png");
                sb.AppendLine();
            }
        }

        File.WriteAllText(mtlPath, sb.ToString());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string F3(string prefix, float x, float y, float z)
        => string.Format(CultureInfo.InvariantCulture,
            "{0} {1:G6} {2:G6} {3:G6}", prefix, x, y, z);

    private static string F2(string prefix, float u, float v)
        => string.Format(CultureInfo.InvariantCulture,
            "{0} {1:G6} {2:G6}", prefix, u, v);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string SanitizeMatName(string name, int fallbackIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"material_{fallbackIndex}";

        // OBJ material names can't have spaces or slashes
        return name.Replace(' ', '_')
                   .Replace('/', '_')
                   .Replace('\\', '_')
                   .Replace(':', '_');
    }

    private static ExportResult Fail(AssetData asset, string message) => new()
    {
        Success       = false,
        ErrorMessage  = message,
        SourceAsset   = asset.Info
    };
}
