using System.Text;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Reads a Shadow of the Tomb Raider .drm file — a ResourceCollection, version 23.
///
/// This is the part of the format that is easy to get wrong: a .drm is a *manifest*, not a
/// container. It holds no texture or mesh bytes at all. It lists the resources a piece of
/// content needs, and for each one records where that resource's bytes live inside the
/// .tiger archives. The actual assets are separate blobs resolved through
/// <see cref="TigerArchiveSet.ReadResource"/>.
///
/// Layout verified against arcusmaximus/TrRebootModTools (MIT):
/// Shared/Cdc/ResourceCollection.cs, Shared/Cdc/TrResourceCollection.cs,
/// Shared/Cdc/Shadow/ShadowResourceCollection.cs.
///
/// Header — 32 bytes, little-endian:
///   +0x00  int32  version = 23
///   +0x04  int32  includeLength        byte length of the includes block
///   +0x08  int32  dependenciesLength   byte length of the dependencies block
///   +0x0C  int32  paddingLength
///   +0x10  int32  size
///   +0x14  int32  flags
///   +0x18  int32  numResources
///   +0x1C  int32  mainResourceIndex    -1 when the collection has no primary resource
///   +0x20  uint64 locale               (Shadow uses an 8-byte locale)
///
/// Then, in this order:
///   numResources × ResourceIdentification (24 bytes)
///   dependenciesLength bytes of { uint64 locale; zero-terminated path }
///   includeLength      bytes of { uint64 locale; zero-terminated path }
///   numResources × ResourceLocation (24 bytes)
///
/// ResourceIdentification:
///   +0x00  uint32 bodySize
///   +0x04  uint8  type            0 here means the slot is disabled
///   +0x05  uint8  flags
///   +0x06  int16  padding
///   +0x08  uint32 subTypeAndRefDefinitionsSize
///                   subType            = (value & 0xFF) >> 1
///                   refDefinitionsSize = value >> 8
///   +0x0C  int32  id
///   +0x10  uint64 locale
///
/// ResourceLocation:
///   +0x00  int32  uniqueKey       type = uniqueKey >> 24, id = uniqueKey &amp; 0x00FFFFFF
///   +0x04  int32  padding
///   +0x08  int16  archivePart
///   +0x0A  uint8  archiveId
///   +0x0B  uint8  archiveSubId
///   +0x0C  uint32 offsetInArchive
///   +0x10  uint32 sizeInArchive
///   +0x14  uint32 decompressionOffset   (repack bookkeeping; not needed to read)
///
/// The authoritative type comes from the *location* record and the subtype from the
/// *identification* record — they are deliberately split across the two tables.
/// </summary>
public sealed class SotrDrmReader
{
    public const int DrmVersion = 23;

    private const int HEADER_SIZE = 0x20;
    private const int LOCALE_SIZE = 8;
    private const int IDENT_SIZE  = 24;
    private const int LOC_SIZE    = 24;

    /// <summary>Platform prefix the engine puts in front of every dependency path.</summary>
    public const string PlatformPrefix = "pcx64-w\\";

    public int   MainResourceIndex { get; private set; } = -1;
    public int   Flags             { get; private set; }
    public ulong Locale            { get; private set; }

    public List<SotrResource> Resources    { get; } = new();
    /// <summary>Other .drm files this collection pulls in (dependencies followed by includes).</summary>
    public List<string>       Dependencies { get; } = new();

    // ── Parse ────────────────────────────────────────────────────────────────

    /// <summary>Returns null when the buffer is not a readable v23 collection.</summary>
    public static SotrDrmReader? TryParse(byte[] drm)
    {
        try   { return Parse(drm); }
        catch { return null; }
    }

    /// <summary>Cheap check for a v23 collection header without doing a full parse.</summary>
    public static bool LooksLikeDrm(byte[] data)
        => data.Length >= HEADER_SIZE && BitConverter.ToInt32(data, 0) == DrmVersion;

    public static SotrDrmReader Parse(byte[] drm)
    {
        if (drm.Length < HEADER_SIZE + LOCALE_SIZE)
            throw new InvalidDataException("DRM too small to hold a collection header.");

        using var ms = new MemoryStream(drm, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        int version           = br.ReadInt32();
        if (version != DrmVersion)
            throw new NotSupportedException($"Only version {DrmVersion} .drm files are supported (got {version}).");

        int includeLength      = br.ReadInt32();
        int dependenciesLength = br.ReadInt32();
        /* paddingLength */      br.ReadInt32();
        /* size          */      br.ReadInt32();
        int flags              = br.ReadInt32();
        int numResources       = br.ReadInt32();
        int mainResourceIndex  = br.ReadInt32();

        if (numResources       < 0 || numResources       > 65536   ||
            includeLength      < 0 || includeLength      > drm.Length ||
            dependenciesLength < 0 || dependenciesLength > drm.Length)
            throw new InvalidDataException("DRM header fields out of range.");

        ulong headerLocale = br.ReadUInt64();

        var result = new SotrDrmReader
        {
            Flags             = flags,
            MainResourceIndex = mainResourceIndex,
            Locale            = headerLocale,
        };

        // ── Identification table ──────────────────────────────────────────────
        var idents = new (uint BodySize, byte Type, int SubType, uint RefDefSize, int Id, ulong Locale)[numResources];
        for (int i = 0; i < numResources; i++)
        {
            uint  bodySize = br.ReadUInt32();
            byte  type     = br.ReadByte();
            /* flags   */    br.ReadByte();
            /* padding */    br.ReadInt16();
            uint  packed   = br.ReadUInt32();
            int   id       = br.ReadInt32();
            ulong locale   = br.ReadUInt64();

            idents[i] = (bodySize, type, (int)((packed & 0xFF) >> 1), packed >> 8, id, locale);
        }

        // ── Dependency + include blocks ───────────────────────────────────────
        result.ReadDependencyBlock(br, dependenciesLength);
        result.ReadDependencyBlock(br, includeLength);

        // ── Location table ────────────────────────────────────────────────────
        for (int i = 0; i < numResources; i++)
        {
            if (ms.Position + LOC_SIZE > ms.Length) break;

            int   uniqueKey  = br.ReadInt32();
            /* padding */      br.ReadInt32();
            short part       = br.ReadInt16();
            byte  archiveId  = br.ReadByte();
            byte  archiveSub = br.ReadByte();
            uint  offset     = br.ReadUInt32();
            uint  size       = br.ReadUInt32();
            uint  decompOff  = br.ReadUInt32();

            var ident = idents[i];

            result.Resources.Add(new SotrResource
            {
                Index               = i,
                Type                = (SotrResourceType)((uint)uniqueKey >> 24),
                SubType             = ident.SubType,
                Id                  = uniqueKey & 0x00FFFFFF,
                Locale              = ident.Locale,
                BodySize            = ident.BodySize,
                RefDefinitionsSize  = ident.RefDefSize,
                ArchiveId           = archiveId,
                ArchiveSubId        = archiveSub,
                ArchivePart         = part,
                Offset              = offset,
                Length              = size,
                DecompressionOffset = decompOff,
                // A zero identification type marks the slot as an empty placeholder.
                Enabled             = ident.Type != (byte)SotrResourceType.Empty,
            });
        }

        return result;
    }

    private void ReadDependencyBlock(BinaryReader br, int length)
    {
        if (length <= 0) return;

        long end = br.BaseStream.Position + length;
        if (end > br.BaseStream.Length) end = br.BaseStream.Length;

        while (br.BaseStream.Position + LOCALE_SIZE < end)
        {
            br.ReadUInt64();                       // per-dependency locale
            string path = ReadZeroTerminated(br, end);
            if (path.Length == 0) continue;

            // The engine stores these without the platform folder, and without ".drm"
            // when the name has no extension of its own.
            if (!path.Contains('.')) path += ".drm";
            Dependencies.Add(PlatformPrefix + path);
        }

        if (br.BaseStream.Position < end)
            br.BaseStream.Position = end;
    }

    private static string ReadZeroTerminated(BinaryReader br, long limit)
    {
        var sb = new StringBuilder(64);
        while (br.BaseStream.Position < limit)
        {
            byte b = br.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}

// ── Data model ────────────────────────────────────────────────────────────────

/// <summary>
/// One resource listed by a .drm collection: what it is, and where its bytes live.
/// </summary>
public sealed class SotrResource
{
    /// <summary>Position within the owning collection's resource table.</summary>
    public int    Index               { get; init; }
    public SotrResourceType Type      { get; init; }
    public int    SubType             { get; init; }
    public int    Id                  { get; init; }
    public ulong  Locale              { get; init; }

    /// <summary>Payload size after the refDefinitions prefix.</summary>
    public uint   BodySize            { get; init; }
    /// <summary>Size of the pointer-fixup prefix that precedes the body.</summary>
    public uint   RefDefinitionsSize  { get; init; }

    public int    ArchiveId           { get; init; }
    public int    ArchiveSubId        { get; init; }
    public int    ArchivePart         { get; init; }
    public uint   Offset              { get; init; }
    /// <summary>Bytes occupied in the archive — compressed when it does not equal refDefs + body.</summary>
    public uint   Length              { get; init; }
    public uint   DecompressionOffset { get; init; }

    /// <summary>False when the collection marks this slot as an empty placeholder.</summary>
    public bool   Enabled             { get; init; }

    /// <summary>Stable identity for de-duplicating a resource seen from several collections.</summary>
    public (SotrResourceType, int, ulong) Key => (Type, Id, Locale);
}

/// <summary>
/// cdc resource types. Values match TrRebootModTools' <c>ResourceType</c> enum.
/// </summary>
public enum SotrResourceType
{
    Unknown                = 0,
    Empty                  = 1,
    Animation              = 2,
    SpeedTree              = 3,
    BlendShapeDriver       = 4,
    Texture                = 5,
    SoundBank              = 6,
    Dtp                    = 7,
    Script                 = 8,
    ShaderLib              = 9,
    Material               = 10,
    GlobalContentReference = 11,
    Model                  = 12,
    CollisionModel         = 13,
    ObjectReference        = 14,
    AnimationLib           = 15,
    Biome                  = 16,
    LocalString            = 17,
}

/// <summary>Resource subtypes that change how a resource is interpreted or named.</summary>
public static class SotrResourceSubType
{
    public const int Texture    = 5;
    public const int Model      = 26;   // .tr11model — the wrapper that references model data
    public const int ModelData  = 27;   // .tr11modeldata — the actual geometry
    public const int ShResource = 116;
    public const int CubeLut    = 117;
}
