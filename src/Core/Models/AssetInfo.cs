namespace GameAssetExplorer.Core.Models;

/// <summary>
/// Lightweight descriptor for an asset. We build a list of these when mounting a game
/// without loading any actual asset data. Think of it as the file tree entry.
/// </summary>
public class AssetInfo
{
    /// <summary>Virtual path inside the game's package, e.g. "/Game/Characters/Cal/Cal_Body"</summary>
    public string VirtualPath { get; set; } = string.Empty;

    /// <summary>Just the filename without extension, e.g. "Cal_Body"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What kind of asset this is</summary>
    public AssetType Type { get; set; }

    /// <summary>Compressed size on disk in bytes</summary>
    public long CompressedSize { get; set; }

    /// <summary>Uncompressed size in bytes (what it becomes when loaded)</summary>
    public long UncompressedSize { get; set; }

    /// <summary>Which archive file (.pak, .utoc) this asset lives in</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>Engine-specific class name, e.g. "Texture2D", "StaticMesh", "SkeletalMesh"</summary>
    public string EngineClassName { get; set; } = string.Empty;

    /// <summary>Whether this asset is encrypted (needs AES key to read)</summary>
    public bool IsEncrypted { get; set; }
}

/// <summary>
/// Fully loaded asset data. Only exists in memory after calling IGameEngine.LoadAssetAsync().
/// Each asset type has its own strongly-typed subclass below.
/// </summary>
public abstract class AssetData
{
    public AssetInfo Info { get; set; } = new();

    /// <summary>
    /// Every property the engine stores on this asset, as key-value pairs.
    /// This feeds directly into JSON metadata export — nothing gets lost.
    /// </summary>
    public Dictionary<string, object?> RawProperties { get; set; } = new();
}

/// <summary>Loaded texture asset with decoded pixel data</summary>
public class TextureAssetData : AssetData
{
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// All mip levels. Index 0 = full resolution.
    /// Each mip is half the resolution of the previous.
    /// </summary>
    public List<MipData> Mips { get; set; } = new();

    /// <summary>Source pixel format from the game, e.g. "BC7", "BC1", "RGBA8"</summary>
    public string SourceFormat { get; set; } = string.Empty;

    /// <summary>Texture category hint, e.g. "Diffuse", "Normal", "Roughness"</summary>
    public string TextureGroup { get; set; } = string.Empty;

    /// <summary>Whether sRGB color space is applied (diffuse maps = true, normal maps = false)</summary>
    public bool IsSrgb { get; set; }

    /// <summary>Raw RGBA pixel bytes (decoded from whatever the source format was)</summary>
    public byte[]? DecodedPixels { get; set; }
}

public class MipData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>Loaded static or skeletal mesh</summary>
public class MeshAssetData : AssetData
{
    public bool IsSkeletal { get; set; }
    public List<LodData> Lods { get; set; } = new();
    public SkeletonData? Skeleton { get; set; }
    public List<MaterialSlot> MaterialSlots { get; set; } = new();
    public BoundingBox Bounds { get; set; } = new();

    /// <summary>
    /// Optional embedded diffuse/color texture (raw block-compressed bytes).
    /// Decode with BcDecoder using <see cref="DiffuseTextureFormat"/>.
    /// Note: TLOU2 data is GPU-tiled (1D-thin); image may appear scrambled
    /// without first applying the untile pass.
    /// </summary>
    public byte[]?  DiffuseTextureData   { get; set; }
    public int      DiffuseTextureWidth  { get; set; }
    public int      DiffuseTextureHeight { get; set; }
    /// <summary>DXGI format name, e.g. "BC7", "BC1".</summary>
    public string   DiffuseTextureFormat { get; set; } = string.Empty;
    /// <summary>
    /// texPath from VRAM_DESC+112, e.g. "/art/characters/ellie/body/E3A7F201C94B6D8.bdn".
    /// Used to look up the full-resolution texture from a texturedict pak.
    /// </summary>
    public string   DiffuseTexturePath   { get; set; } = string.Empty;
}

public class LodData
{
    public int LodIndex { get; set; }
    public float ScreenSize { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public byte[]? VertexBuffer { get; set; }
    public byte[]? IndexBuffer { get; set; }
    /// <summary>UV0 coordinates — 8 bytes/vertex: float32 U followed by float32 V. Null if not parsed.</summary>
    public byte[]? UvBuffer { get; set; }

    /// <summary>
    /// Original submesh boundaries within the merged buffers.
    /// Empty list = treat the whole LOD as a single unnamed submesh.
    /// </summary>
    public List<SubmeshInfo> Submeshes { get; set; } = new();
}

/// <summary>
/// Records where a single submesh lives within the merged vertex/index buffers
/// of its parent LOD. Index range is in 32-bit indices (post-merge).
/// </summary>
public class SubmeshInfo
{
    public string Name { get; set; } = string.Empty;
    public int VertexStart { get; set; }
    public int VertexCount { get; set; }
    public int IndexStart { get; set; }
    public int IndexCount { get; set; }
}

public class SkeletonData
{
    public List<BoneInfo> Bones { get; set; } = new();
}

public class BoneInfo
{
    public string Name { get; set; } = string.Empty;
    public int ParentIndex { get; set; } = -1;
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new float[4]; // Quaternion
    public float[] Scale { get; set; } = new float[3];
}

public class MaterialSlot
{
    public int SlotIndex { get; set; }
    public string MaterialPath { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
}

public class BoundingBox
{
    public float[] Min { get; set; } = new float[3];
    public float[] Max { get; set; } = new float[3];
}

/// <summary>Loaded animation sequence</summary>
public class AnimationAssetData : AssetData
{
    public float FrameRate { get; set; }
    public int FrameCount { get; set; }
    public float Duration => FrameCount / Math.Max(FrameRate, 0.001f);
    public string SkeletonPath { get; set; } = string.Empty;
    public List<AnimTrack> Tracks { get; set; } = new();
}

public class AnimTrack
{
    public string BoneName { get; set; } = string.Empty;
    public List<float[]> PositionKeys { get; set; } = new();  // [frame][x,y,z]
    public List<float[]> RotationKeys { get; set; } = new();  // [frame][x,y,z,w]
    public List<float[]> ScaleKeys { get; set; } = new();     // [frame][x,y,z]
}

/// <summary>Loaded audio asset</summary>
public class AudioAssetData : AssetData
{
    public float Duration { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string SourceFormat { get; set; } = string.Empty; // "Wwise", "OGG", "WAV", etc.

    /// <summary>Decoded PCM audio data for playback and waveform rendering</summary>
    public float[]? PcmSamples { get; set; }

    /// <summary>Raw compressed audio bytes (for pass-through export)</summary>
    public byte[]? RawAudioData { get; set; }
}

/// <summary>All asset types we can encounter across different engines</summary>
public enum AssetType
{
    Unknown,
    Texture,
    StaticMesh,
    SkeletalMesh,
    Animation,
    Audio,
    Material,
    Blueprint,
    Level,
    Cinematic,
    DataTable,
    StringTable,
    Font,
    ParticleSystem,
    Other
}
