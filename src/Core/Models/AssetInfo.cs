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

    /// <summary>
    /// Animation clips already decoded for this mesh (usually the ones in its own pak).
    /// </summary>
    public List<AnimationAssetData> Animations { get; set; } = new();

    /// <summary>
    /// Clips that exist for this character but have not been decoded yet. A full character's
    /// animation set runs to hundreds of megabytes, so they are listed at load time and
    /// decoded only when the viewer actually asks for one via <see cref="ResolveAnimations"/>.
    /// </summary>
    public List<AnimationSourceRef> AnimationSources { get; set; } = new();

    /// <summary>
    /// Set by the engine plugin: decodes one listed source into clips on demand. Null when the
    /// engine has no lazy animation path. Not serialised — it is a live callback, not data.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<AnimationSourceRef, Task<IReadOnlyList<AnimationAssetData>>>? ResolveAnimations { get; set; }

    /// <summary>
    /// Every skeleton the engine could bind this mesh to, as engine-specific locators. Automatic
    /// resolution picks one, but it can only check that a rig has ENOUGH bones — a different rig
    /// of sufficient size passes that test and puts every weight on the wrong joint. This list
    /// is what lets the viewer offer a manual correction.
    /// </summary>
    public IReadOnlyList<string> AvailableSkeletonPaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Set by the engine plugin: loads one of <see cref="AvailableSkeletonPaths"/>. Not serialised.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, SkeletonData?>? ResolveSkeletonOverride { get; set; }
}

/// <summary>
/// A pointer to animation data that exists but has not been read yet. <see cref="Locator"/> is
/// opaque and owned by the engine that produced it (for Naughty Dog paks it is the absolute
/// path of the <c>anim-*.pak</c>).
/// </summary>
public class AnimationSourceRef
{
    public string DisplayName { get; set; } = string.Empty;
    public string Locator     { get; set; } = string.Empty;
    public string EngineId    { get; set; } = string.Empty;
    /// <summary>Bytes on disk, so the UI can warn before opening something enormous.</summary>
    public long   SizeBytes   { get; set; }
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
    /// Smooth vertex normals, float32 XYZ (12 bytes/vertex), parallel to
    /// <see cref="VertexBuffer"/> and in the same space. Derived from the triangles rather than
    /// decoded, and computed on demand by <c>MeshNormals.EnsureComputed</c> — the viewer and
    /// every exporter share this one buffer instead of each deriving its own.
    /// </summary>
    public byte[]? NormalBuffer { get; set; }

    /// <summary>
    /// Original submesh boundaries within the merged buffers.
    /// Empty list = treat the whole LOD as a single unnamed submesh.
    /// </summary>
    public List<SubmeshInfo> Submeshes { get; set; } = new();

    /// <summary>
    /// Per-vertex bone influences for this LOD, parallel to <see cref="VertexBuffer"/>.
    /// Null when the mesh has no skinning data (static meshes, or a skeletal mesh whose
    /// skin table could not be resolved).
    /// </summary>
    public SkinBinding? Skin { get; set; }
}

/// <summary>
/// Per-vertex bone influences for one LOD. Indices address
/// <see cref="SkeletonData.Bones"/> of the mesh's skeleton.
///
/// Layout is flat and fixed-width: vertex <c>v</c>'s influence <c>i</c> lives at
/// <c>v * InfluencesPerVertex + i</c> in both arrays. Unused influence slots carry
/// weight 0. Weights are already normalised so each vertex's row sums to 1
/// (rows that decoded to all-zero are left at zero and the skinner falls back to
/// the rest pose for that vertex).
/// </summary>
public class SkinBinding
{
    /// <summary>Fixed number of influence slots stored per vertex (4, 8 or 12).</summary>
    public int InfluencesPerVertex { get; set; }

    /// <summary>Vertex count this binding covers — <c>BoneIndices.Length / InfluencesPerVertex</c>.</summary>
    public int VertexCount { get; set; }

    /// <summary>Bone index per influence slot. 0 where the slot is unused.</summary>
    public ushort[] BoneIndices { get; set; } = Array.Empty<ushort>();

    /// <summary>Normalised weight per influence slot. 0 where the slot is unused.</summary>
    public float[] BoneWeights { get; set; } = Array.Empty<float>();

    /// <summary>Highest bone index actually referenced — used to sanity-check against a skeleton.</summary>
    public int MaxBoneIndex { get; set; } = -1;
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

    /// <summary>Material name from the submesh's m_material struct (e.g. "pants-uv", "shoes"). Empty if unresolved.</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>
    /// VRAM_DESC texPath of this submesh's diffuse (BaseColor01) map, used for full-resolution
    /// texturedict lookup. Each submesh of a multi-material asset (e.g. a body = tank top + pants +
    /// shoes) carries its own diffuse here. Empty if the material has no resolvable colour map.
    /// </summary>
    public string DiffuseTexturePath { get; set; } = string.Empty;

    /// <summary>VRAM_DESC texPath of this submesh's normal (Normal01) map. Empty if none.</summary>
    public string NormalTexturePath { get; set; } = string.Empty;

    /// <summary>
    /// Full-resolution diffuse for this submesh: raw block-compressed bytes (decode with
    /// <see cref="DiffuseTextureFormat"/>). Resolved from <see cref="DiffuseTexturePath"/> via the
    /// texturedict; null until resolved (or if the submesh has no colour map). Lets the viewer
    /// texture each submesh individually instead of sharing one mesh-level diffuse.
    /// </summary>
    public byte[]? DiffuseTextureData { get; set; }
    public int     DiffuseTextureWidth  { get; set; }
    public int     DiffuseTextureHeight { get; set; }
    /// <summary>DXGI format name of <see cref="DiffuseTextureData"/>, e.g. "BC7", "BC1".</summary>
    public string  DiffuseTextureFormat { get; set; } = string.Empty;
}

public class SkeletonData
{
    public List<BoneInfo> Bones { get; set; } = new();

    /// <summary>
    /// Where the skeleton came from — either the asset's own pak or the base
    /// <c>*-skel.pak</c> it was borrowed from. Shown in the viewer so it is obvious
    /// when a part pak is being posed by a shared skeleton.
    /// </summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Case-insensitive bone-name → index map, built lazily on first use.</summary>
    public Dictionary<string, int> NameToIndex()
    {
        var map = new Dictionary<string, int>(Bones.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Bones.Count; i++) map.TryAdd(Bones[i].Name, i);
        return map;
    }
}

/// <summary>
/// One joint. <see cref="Position"/>/<see cref="Rotation"/>/<see cref="Scale"/> are the
/// bind-pose transform expressed in PARENT-LOCAL space — the world bind pose is the
/// running product down the parent chain (see <c>SkeletonMath.ComputeWorldBind</c>).
/// </summary>
public class BoneInfo
{
    public string Name { get; set; } = string.Empty;
    public int ParentIndex { get; set; } = -1;
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new float[4] { 0, 0, 0, 1 }; // Quaternion (x,y,z,w)
    public float[] Scale { get; set; } = new float[3] { 1, 1, 1 };

    /// <summary>GroupID from the joint hierarchy's parenting table (diagnostic).</summary>
    public int GroupIndex { get; set; } = -1;
    /// <summary>ChildID from the parenting table (diagnostic).</summary>
    public int ChildIndex { get; set; } = -1;
    /// <summary>ChainID from the parenting table — helper joints inherit their chain's matrix.</summary>
    public int ChainIndex { get; set; } = -1;

    /// <summary>
    /// False for helper/straggler joints that had no entry in the transform table and were
    /// given a borrowed or identity bind transform. Those joints are still emitted so bone
    /// indices stay aligned, but they should not be trusted for measurement.
    /// </summary>
    public bool HasBindTransform { get; set; } = true;
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

/// <summary>
/// One animation clip. Key lists are FRAME-indexed: <c>PositionKeys[f]</c> is the value at
/// frame <c>f</c>. A track with exactly one key is constant for the whole clip; a track with
/// an empty list leaves that channel at the bind pose.
/// </summary>
public class AnimationAssetData : AssetData
{
    public float FrameRate { get; set; } = 30f;
    public int FrameCount { get; set; }
    public float Duration => FrameCount <= 1 ? 0f : (FrameCount - 1) / Math.Max(FrameRate, 0.001f);
    public string SkeletonPath { get; set; } = string.Empty;
    public List<AnimTrack> Tracks { get; set; } = new();

    /// <summary>Clip name as it appears in the pak (e.g. "ellie-walk-fwd").</summary>
    public string ClipName { get; set; } = string.Empty;

    /// <summary>
    /// True when the clip stores deltas to be layered on top of a base pose rather than an
    /// absolute pose. Additive clips played standalone look broken — the viewer says so.
    /// </summary>
    public bool IsAdditive { get; set; }

    /// <summary>Joint names the clip animates, in the clip's own order (diagnostic).</summary>
    public List<string> JointNames { get; set; } = new();

    /// <summary>
    /// True when each track is known to drive a specific named joint. False means the tracks
    /// were recovered but not attributed, so playing the clip would move the wrong joints —
    /// the viewer holds the bind pose and says so instead.
    /// </summary>
    public bool JointMappingResolved { get; set; }
}

public class AnimTrack
{
    public string BoneName { get; set; } = string.Empty;
    public List<float[]> PositionKeys { get; set; } = new();  // [frame][x,y,z]
    public List<float[]> RotationKeys { get; set; } = new();  // [frame][x,y,z,w]
    public List<float[]> ScaleKeys { get; set; } = new();     // [frame][x,y,z]

    /// <summary>
    /// Index into the target <see cref="SkeletonData.Bones"/>, resolved by name when the clip
    /// is bound to a skeleton. -1 until bound, or when the joint has no counterpart.
    /// </summary>
    public int BoneIndex { get; set; } = -1;
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
