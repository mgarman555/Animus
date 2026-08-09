using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Exporters.MetadataExporter;
using GameAssetExplorer.Exporters.ModelExporter;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace GameAssetExplorer.App.Services;

/// <summary>
/// Which parts of a loaded character the viewer wants written to disk. Flags so the
/// four viewer buttons (Geometry / Textures / Armature / All) map to one code path:
/// "All" is just every flag set.
/// </summary>
[Flags]
public enum CharacterExportParts
{
    None      = 0,
    Geometry  = 1 << 0,
    Textures  = 1 << 1,
    Armature  = 1 << 2,
    All       = Geometry | Textures | Armature,
}

/// <summary>One decoded texture the viewer is handing to the exporter, ready to PNG-encode.</summary>
public record ExportTexture(string Name, BitmapSource Image);

public class CharacterExportRequest
{
    public required MeshAssetData Mesh { get; init; }
    public required AssetInfo Info { get; init; }

    /// <summary>Index into <see cref="MeshAssetData.Lods"/> the viewer currently shows.</summary>
    public int LodIndex { get; init; }

    /// <summary>Root folder the user picked. A per-character subfolder is created under it.</summary>
    public required string OutputRoot { get; init; }

    public required ExportSettings Settings { get; init; }

    /// <summary>
    /// Textures already decoded by the viewer (with the swap/flip/format the auto-detect settled
    /// on), so the exported PNGs match exactly what's on screen. Empty = nothing to write.
    /// </summary>
    public IReadOnlyList<ExportTexture> Textures { get; init; } = Array.Empty<ExportTexture>();
}

public class CharacterExportResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> Written { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? ErrorMessage { get; set; }

    public string Summary => Success
        ? $"Exported {Written.Count} file(s) → {OutputDirectory}"
        : $"Export failed: {ErrorMessage}";
}

/// <summary>
/// Writes a loaded, LOD-selected character out of the 3D viewer in the normalized layout:
/// <c>{root}/{Character}/{Character}.gltf</c> + <c>Textures/</c> + <c>_meta.json</c> (+ a
/// skeleton sidecar). Each of the four parts can be written independently — the viewer passes
/// the flags matching the button the user clicked. Geometry alone omits the skeleton; "All"
/// embeds it in the model, so there's never a redundant standalone armature file next to a
/// skinned mesh.
/// </summary>
public class CharacterExporter
{
    public async Task<CharacterExportResult> ExportAsync(
        CharacterExportRequest req, CharacterExportParts parts)
    {
        var result = new CharacterExportResult();
        try
        {
            string charDir = Path.Combine(req.OutputRoot, NormalizePart(req.Info.Name));
            Directory.CreateDirectory(charDir);
            result.OutputDirectory = charDir;

            int lod = req.LodIndex >= 0 && req.LodIndex < req.Mesh.Lods.Count ? req.LodIndex : 0;
            bool wantAll = (parts & CharacterExportParts.All) == CharacterExportParts.All;

            // ── Geometry ────────────────────────────────────────────────────────
            if (parts.HasFlag(CharacterExportParts.Geometry))
            {
                var geoSettings = req.Settings.Clone();
                geoSettings.ModelLodLevel = lod;
                geoSettings.PreserveVirtualPaths = false;
                // Geometry-only omits the skeleton; "All" embeds it in the same model file so
                // the skinned character is one importable asset.
                geoSettings.ExportSkeleton = wantAll;

                IExporter modelExporter = geoSettings.ModelFormat switch
                {
                    ModelExportFormat.Obj => new ObjModelExporter(),
                    ModelExportFormat.Fbx => new FbxModelExporter(),
                    _                     => new GltfModelExporter(),
                };

                var r = await modelExporter.ExportAsync(req.Mesh, charDir, geoSettings);
                if (r.Success) result.Written.Add(r.OutputPath);
                else result.Warnings.Add($"Geometry: {r.ErrorMessage}");
            }

            // ── Textures ────────────────────────────────────────────────────────
            if (parts.HasFlag(CharacterExportParts.Textures))
            {
                if (req.Textures.Count == 0)
                {
                    result.Warnings.Add("Textures: no decoded texture available for this character.");
                }
                else
                {
                    string texDir = Path.Combine(charDir, "Textures");
                    Directory.CreateDirectory(texDir);
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tex in req.Textures)
                    {
                        string baseName = NormalizePart(tex.Name);
                        string name = baseName; int n = 1;
                        while (!used.Add(name)) name = $"{baseName}_{n++}";
                        string path = Path.Combine(texDir, name + ".png");
                        await Task.Run(() => WritePng(tex.Image, path));
                        result.Written.Add(path);
                    }
                }
            }

            // ── Armature ────────────────────────────────────────────────────────
            if (parts.HasFlag(CharacterExportParts.Armature))
            {
                if (HasArmature(req.Mesh))
                {
                    string skelPath = Path.Combine(charDir, NormalizePart(req.Info.Name) + "_armature.json");
                    await WriteSkeletonJsonAsync(req.Mesh, req.Info, skelPath);
                    result.Written.Add(skelPath);
                }
                else
                {
                    result.Warnings.Add(
                        "Armature: this character has no parsed skeleton — nothing to export. " +
                        "(TLOU2 character/clothing paks carry no embedded JOINT_HIERARCHY.)");
                }
            }

            // ── Metadata sidecar (always, so the Blender/UE addon has context) ──
            var metaSettings = req.Settings.Clone();
            metaSettings.PreserveVirtualPaths = false;
            var meta = await new JsonMetadataExporter().ExportAsync(req.Mesh, charDir, metaSettings);
            if (meta.Success) result.Written.Add(meta.OutputPath);

            result.Success = result.Written.Count > 0;
            if (!result.Success && result.ErrorMessage == null)
                result.ErrorMessage = result.Warnings.Count > 0
                    ? string.Join("; ", result.Warnings)
                    : "Nothing was written.";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public static bool HasArmature(MeshAssetData mesh) =>
        (mesh.Skeleton?.Bones.Count ?? 0) > 0;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static async Task WriteSkeletonJsonAsync(MeshAssetData mesh, AssetInfo info, string path)
    {
        var bones = mesh.Skeleton!.Bones;
        var doc = new SkeletonDocument
        {
            AssetName = info.Name,
            BoneCount = bones.Count,
            // Transforms are the raw parsed bind pose in the source engine's space; the addon
            // applies any axis conversion so nothing is baked in irreversibly here.
            CoordinateSystem = "source (unconverted bind pose)",
            Bones = bones.Select((b, i) => new SkeletonBone
            {
                Index = i,
                Name = b.Name,
                ParentIndex = b.ParentIndex,
                Position = b.Position,
                Rotation = b.Rotation,
                Scale = b.Scale,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(doc, SkeletonJsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private static readonly JsonSerializerOptions SkeletonJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string NormalizePart(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "unnamed" : s;
    }

    private class SkeletonDocument
    {
        public string AssetName { get; set; } = string.Empty;
        public int BoneCount { get; set; }
        public string CoordinateSystem { get; set; } = string.Empty;
        public List<SkeletonBone> Bones { get; set; } = new();
    }

    private class SkeletonBone
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public int ParentIndex { get; set; }
        public float[] Position { get; set; } = new float[3];
        public float[] Rotation { get; set; } = new float[4];
        public float[] Scale { get; set; } = new float[3];
    }
}
