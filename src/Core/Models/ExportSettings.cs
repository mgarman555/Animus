namespace GameAssetExplorer.Core.Models;

/// <summary>
/// Everything the user can configure about how exports work.
/// Persisted per-game so settings carry over between sessions.
/// </summary>
public class ExportSettings
{
    // ─── Texture Settings ─────────────────────────────────────────────────────

    /// <summary>Output format for textures. PNG is the default and safest for Blender/UE pipelines.</summary>
    public TextureExportFormat TextureFormat { get; set; } = TextureExportFormat.Png;

    /// <summary>
    /// Which mip level to export. -1 = full resolution (mip 0).
    /// If you're exporting for reimport into Unreal, always use -1.
    /// </summary>
    public int TextureMipLevel { get; set; } = -1;

    // ─── Model Settings ───────────────────────────────────────────────────────

    /// <summary>Primary model export format. glTF is the default — self-contained (Assimp writes it)
    /// and imported natively by both Blender and UE5. (FBX export is ASCII-only here, which Blender
    /// can't read; OBJ loses skeleton/materials.)</summary>
    public ModelExportFormat ModelFormat { get; set; } = ModelExportFormat.GlTf;

    /// <summary>
    /// Which LOD to export. -1 = LOD0 (highest quality).
    /// For reimporting into Unreal, you almost always want LOD0.
    /// </summary>
    public int ModelLodLevel { get; set; } = -1;

    /// <summary>
    /// Scale to apply on FBX export.
    /// Unreal uses centimeters internally, Blender defaults to meters.
    /// Setting this to 1.0 exports at Unreal's native scale.
    /// Set to 0.01 if you want 1 unit = 1 meter in Blender.
    /// </summary>
    public float ModelScaleFactor { get; set; } = 1.0f;

    /// <summary>
    /// Whether to apply the bone axis correction on export.
    /// Unreal's skeleton is Y-forward, Z-up. Blender is Y-forward, Z-up but with a different
    /// bone orientation. This toggle applies the correction so bones import cleanly in Blender.
    /// </summary>
    public bool ApplyBlenderBoneCorrection { get; set; } = true;

    /// <summary>Include the skeleton in FBX exports for skeletal meshes</summary>
    public bool ExportSkeleton { get; set; } = true;

    /// <summary>Include material slot names in FBX (helps the Blender/UE addon assign materials)</summary>
    public bool ExportMaterialSlotNames { get; set; } = true;

    // ─── Audio Settings ───────────────────────────────────────────────────────

    public AudioExportFormat AudioFormat { get; set; } = AudioExportFormat.Wav;

    // ─── Metadata Settings ────────────────────────────────────────────────────

    /// <summary>
    /// Export a JSON sidecar file alongside every exported asset.
    /// This is critical for the Blender and Unreal addons — don't turn it off.
    /// </summary>
    public bool ExportMetadataJson { get; set; } = true;

    /// <summary>
    /// Include every raw engine property in the JSON, even obscure ones.
    /// Recommended: true. The addon can ignore what it doesn't need.
    /// </summary>
    public bool IncludeAllProperties { get; set; } = true;

    // ─── File Organization ────────────────────────────────────────────────────

    /// <summary>
    /// Mirror the game's virtual path structure in the output folder.
    /// e.g. /Game/Characters/Cal/Cal_Body.png instead of just Cal_Body.png.
    /// Highly recommended — it prevents name collisions on large exports.
    /// </summary>
    public bool PreserveVirtualPaths { get; set; } = true;

    /// <summary>Overwrite existing files, or skip and keep existing ones</summary>
    public bool OverwriteExisting { get; set; } = false;
}

public enum TextureExportFormat { Png, Tga, Dds }
public enum ModelExportFormat { Fbx, Obj, GlTf }
public enum AudioExportFormat { Wav, Ogg }

/// <summary>Result of a single export operation</summary>
public class ExportResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public AssetInfo SourceAsset { get; set; } = new();
    public long FileSizeBytes { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>Progress info for batch exports — fed into the UI progress bar</summary>
public class ExportProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public string CurrentAsset { get; set; } = string.Empty;
    public double PercentComplete => Total == 0 ? 0 : (double)Completed / Total * 100;
}
