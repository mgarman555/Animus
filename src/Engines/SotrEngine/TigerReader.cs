using System.Text;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Reads TAFS v5 archives — Shadow of the Tomb Raider PC (Foundation Engine, Eidos-Montréal).
///
/// Layout verified against arcusmaximus/TrRebootModTools (MIT) Shared/Cdc/Archive.cs +
/// Shared/Cdc/Shadow/ShadowArchive.cs, cross-checked with https://cdcengine.re/docs/files/tiger/.
///
/// Header — 56 bytes, little-endian:
///   +0x00  uint32   magic   = 0x53464154 ("TAFS")
///   +0x04  int32    version = 5
///   +0x08  int32    numParts
///   +0x0C  int32    numFiles
///   +0x10  int32    id
///   +0x14  int32    subId      (v5 only)
///   +0x18  char[32] platform   ("pcx64-w\0…")
///
/// TOC — numFiles × 32 bytes:
///   +0x00  uint64  nameHash          FNV-1 64 of the asset path
///   +0x08  uint64  locale            language/platform bitmask
///   +0x10  uint32  uncompressedSize
///   +0x14  uint32  compressedSize
///   +0x18  int16   archivePart       which .NNN.tiger holds the data
///   +0x1A  uint8   archiveId         which archive *set* holds it
///   +0x1B  uint8   archiveSubId
///   +0x1C  uint32  offset            byte offset inside the part file
///
/// archiveId/archiveSubId matter: an entry listed in one archive's TOC may point at data
/// owned by a different archive (that is how patches and DLC override base-game files), so
/// blobs are always resolved through <see cref="TigerArchiveSet"/> rather than locally.
///
/// Part file naming: the trailing "000.tiger" becomes "{part:D3}.tiger" —
/// bigfile.000.tiger → bigfile.005.tiger.
///
/// Blob data is either raw or wrapped in a <see cref="Cdrm"/> container.
/// </summary>
public sealed class TigerReader : IDisposable
{
    public const uint Magic = 0x53464154;   // "TAFS"

    private const int HEADER_SIZE   = 56;
    private const int TOC_ENTRY_SZ  = 32;
    private const string PART_SUFFIX = ".000.tiger";

    private readonly object _lock = new();
    private readonly Dictionary<int, FileStream> _partStreams = new();

    public string IndexPath { get; }
    public int    Version   { get; private set; }
    public int    NumParts  { get; private set; }
    public int    NumFiles  { get; private set; }
    public int    Id        { get; private set; }
    public int    SubId     { get; private set; }
    public string Platform  { get; private set; } = string.Empty;

    public List<TigerEntry> Entries { get; } = new();

    public TigerReader(string indexPath) => IndexPath = indexPath;

    // ── Open ─────────────────────────────────────────────────────────────────

    public void Open()
    {
        using var fs = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"Not a TAFS archive (magic=0x{magic:X8}): {Path.GetFileName(IndexPath)}");

        Version = br.ReadInt32();
        if (Version != 5)
            throw new NotSupportedException(
                $"Tiger version {Version} not supported — only v5 (Shadow of the Tomb Raider) is implemented.");

        NumParts = br.ReadInt32();
        NumFiles = br.ReadInt32();
        Id       = br.ReadInt32();
        SubId    = br.ReadInt32();
        Platform = Encoding.ASCII.GetString(br.ReadBytes(32)).TrimEnd('\0');

        if (NumFiles < 0 || (long)NumFiles * TOC_ENTRY_SZ > fs.Length - HEADER_SIZE)
            throw new InvalidDataException($"TAFS file count out of range ({NumFiles}).");

        Entries.Capacity = NumFiles;
        for (int i = 0; i < NumFiles; i++)
        {
            Entries.Add(new TigerEntry
            {
                Ordinal          = i,
                NameHash         = br.ReadUInt64(),
                Locale           = br.ReadUInt64(),
                UncompressedSize = br.ReadUInt32(),
                CompressedSize   = br.ReadUInt32(),
                ArchivePart      = br.ReadInt16(),
                ArchiveId        = br.ReadByte(),
                ArchiveSubId     = br.ReadByte(),
                Offset           = br.ReadUInt32(),
            });
        }
    }

    // ── Blob reads ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a blob from one of this archive's part files, transparently inflating it
    /// when it is CDRM-compressed.
    /// </summary>
    /// <param name="assumeUncompressed">
    /// Skip the CDRM probe. Set when the caller already knows the blob is stored raw —
    /// a resource whose refDefinitions + body exactly fill its archive slot.
    /// </param>
    /// <param name="chunkLimit">Cap on CDRM chunks to inflate; 1 peeks at the head cheaply.</param>
    public byte[] ReadBlob(int part, uint offset, uint length,
                           bool assumeUncompressed = false, int chunkLimit = int.MaxValue)
    {
        lock (_lock)
        {
            var stream = GetPartStream(part);

            if (!assumeUncompressed && Cdrm.HasMagic(stream, offset))
                return Cdrm.Decompress(stream, offset, chunkLimit);

            if (length == 0) return Array.Empty<byte>();

            long available = Math.Max(0, stream.Length - offset);
            int  count     = (int)Math.Min(length, available);
            if (count <= 0) return Array.Empty<byte>();

            var buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, count);
            return buffer;
        }
    }

    /// <summary>Full contents of a TOC entry.</summary>
    public byte[] ReadEntry(TigerEntry entry)
        => ReadBlob(entry.ArchivePart, entry.Offset, entry.UncompressedSize);

    /// <summary>
    /// First CDRM chunk of a TOC entry (or the whole thing when stored raw).
    /// Used by the background indexer, which only needs the head of each file.
    /// </summary>
    public byte[] PeekEntry(TigerEntry entry)
        => ReadBlob(entry.ArchivePart, entry.Offset, entry.UncompressedSize, chunkLimit: 1);

    // ── Part files ───────────────────────────────────────────────────────────

    public string GetPartFilePath(int part)
    {
        if (IndexPath.EndsWith(PART_SUFFIX, StringComparison.OrdinalIgnoreCase))
            return string.Concat(IndexPath.AsSpan(0, IndexPath.Length - PART_SUFFIX.Length),
                                 $".{part:D3}.tiger");
        return IndexPath;
    }

    private FileStream GetPartStream(int part)
    {
        if (!_partStreams.TryGetValue(part, out var stream))
        {
            stream = new FileStream(GetPartFilePath(part), FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 131072, useAsync: false);
            _partStreams[part] = stream;
        }
        return stream;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _partStreams.Values) s.Dispose();
            _partStreams.Clear();
        }
    }
}

// ── Data model ────────────────────────────────────────────────────────────────

public sealed class TigerEntry
{
    /// <summary>Zero-based position in the TOC.</summary>
    public int    Ordinal          { get; init; }
    /// <summary>FNV-1 64-bit hash of the asset path — the primary identifier.</summary>
    public ulong  NameHash         { get; init; }
    /// <summary>Language / voice / platform bitmask. 0xFFFF… = applies everywhere.</summary>
    public ulong  Locale           { get; init; }
    public uint   UncompressedSize { get; init; }
    public uint   CompressedSize   { get; init; }
    /// <summary>Which .NNN.tiger part holds the data (0 = the index file itself).</summary>
    public short  ArchivePart      { get; init; }
    /// <summary>Which archive set owns the data — not necessarily the one listing this entry.</summary>
    public byte   ArchiveId        { get; init; }
    public byte   ArchiveSubId     { get; init; }
    /// <summary>Byte offset from the start of the part file.</summary>
    public uint   Offset           { get; init; }
}
