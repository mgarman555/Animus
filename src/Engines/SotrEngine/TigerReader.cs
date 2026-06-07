using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Reads TAFS v5 archives — Shadow of the Tomb Raider PC (Foundation Engine, Eidos-Montréal).
///
/// Format references:
///   https://cdcengine.re/docs/files/tiger/
///   https://github.com/Ekey/CDCE.TIGER.Tool      (MIT — Ekey)
///   https://github.com/arcusmaximus/TrRebootModTools (MIT — arcusmaximus)
///
/// Archive layout (all little-endian):
///   Header  56 bytes
///     +0x00  uint32   magic   = 0x53464154 ("TAFS")
///     +0x04  int32    version = 5
///     +0x08  int32    numParts
///     +0x0C  int32    numFiles
///     +0x10  int32    id
///     +0x14  int32    subId   (v5 only)
///     +0x18  char[32] platform ("pcx64-w\0...")
///   TOC  numFiles × 32 bytes
///     +0x00  uint64  nameHash         FNV-1A 64-bit hash of asset path
///     +0x08  uint64  locale           language/locale bitmask (0 = common)
///     +0x10  int32   decompressedSize uncompressed byte count
///     +0x14  int32   compressedSize   on-disk byte count (may be 0 when equal to decompressedSize)
///     +0x18  uint16  tigerPart        which .NNN.tiger data file contains this entry
///     +0x1A  uint16  priority         load-priority for conflict resolution
///     +0x1C  uint32  offset           absolute byte offset inside the part file
///
/// MRDC compression (raw Deflate, chunk-based):
///   Magic "MRDC" (4 bytes) at start of entry data.
///   Followed by one or more chunks until decompressedSize bytes are produced:
///     uint32  header     — bits[31:8] = decompressed chunk size, bits[7:0] = flags
///     uint32  compSize   — compressed byte count for this chunk
///     byte[]  data       — raw Deflate bytes; stored uncompressed when compSize == decompChunkSize
///
/// Part file naming: replace last "000.tiger" in the index path with "{part:D3}.tiger".
///   bigfile.000.tiger → bigfile.005.tiger  (part 5)
///   bigfile.dlc.outfit.4.002.000.tiger → bigfile.dlc.outfit.4.002.003.tiger  (part 3)
/// </summary>
public sealed class TigerReader : IDisposable
{
    public const uint Magic = 0x53464154;   // "TAFS" in memory

    private const int HEADER_SIZE   = 56;
    private const int TOC_ENTRY_SZ  = 32;
    private const int MRDC_CHUNK_SZ = 0x40000;  // max 256 KB per chunk

    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly Dictionary<ushort, FileStream> _partStreams = new();

    public string IndexPath => _indexPath;
    public int NumParts { get; private set; }
    public int NumFiles { get; private set; }
    public List<TigerEntry> Entries { get; } = new();

    public TigerReader(string indexPath) => _indexPath = indexPath;

    // ── Open ─────────────────────────────────────────────────────────────────

    public void Open()
    {
        using var fs = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"Not a TAFS archive (magic=0x{magic:X8}): {Path.GetFileName(_indexPath)}");

        int version = br.ReadInt32();
        if (version != 5)
            throw new NotSupportedException(
                $"Tiger version {version} not supported — only v5 (Shadow of the Tomb Raider) is implemented.");

        NumParts         = br.ReadInt32();
        NumFiles         = br.ReadInt32();
        /* id    */       br.ReadInt32();
        /* subId */       br.ReadInt32();   // v5 addition
        /* platform */    br.ReadBytes(32); // "pcx64-w\0..."
        // 4+4+4+4+4+4+32 = 56 bytes consumed

        Entries.Capacity = NumFiles;
        for (int i = 0; i < NumFiles; i++)
        {
            Entries.Add(new TigerEntry
            {
                Ordinal          = i,
                NameHash         = br.ReadUInt64(),
                Locale           = br.ReadUInt64(),
                DecompressedSize = br.ReadInt32(),
                CompressedSize   = br.ReadInt32(),
                TigerPart        = br.ReadUInt16(),
                Priority         = br.ReadUInt16(),
                Offset           = br.ReadUInt32(),
            });
        }
    }

    // ── Extract ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the raw bytes for an entry from the appropriate part file.
    /// If the data starts with "MRDC", applies chunk-based Deflate decompression.
    /// Thread-safe: multiple concurrent extracts from the same reader are safe.
    /// </summary>
    public byte[] ExtractEntry(TigerEntry entry)
    {
        if (entry.DecompressedSize <= 0) return Array.Empty<byte>();

        lock (_lock)
        {
            var stream = GetPartStream(entry.TigerPart);
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            // Peek first 4 bytes for MRDC magic without disturbing the read position
            var magic4 = new byte[4];
            int got = stream.Read(magic4, 0, 4);
            if (got < 4) return Array.Empty<byte>();
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            bool isMrdc = magic4[0] == 'M' && magic4[1] == 'R'
                       && magic4[2] == 'D' && magic4[3] == 'C';

            if (!isMrdc)
            {
                // Uncompressed — read exactly DecompressedSize bytes
                var buf = new byte[entry.DecompressedSize];
                ReadFully(stream, buf);
                return buf;
            }

            // MRDC: seek past magic, decompress chunks
            stream.Seek(entry.Offset + 4, SeekOrigin.Begin);
            return DecompressMrdc(stream, entry.DecompressedSize);
        }
    }

    // ── Peek (cheap first-chunk read for type classification) ────────────────

    /// <summary>
    /// Returns up to <paramref name="count"/> decompressed bytes from the entry without
    /// extracting the full asset. For MRDC data only the first chunk is decompressed.
    /// Used by the background type scanner.
    /// </summary>
    /// <summary>
    /// Returns the first decompressed chunk of an entry without extracting the full asset.
    /// For MRDC data only the first Deflate chunk is decompressed (up to 256 KB).
    /// For uncompressed entries the full entry bytes are returned.
    /// Used by the background type scanner — much cheaper than ExtractEntry.
    /// </summary>
    public byte[] PeekBytes(TigerEntry entry)
    {
        if (entry.DecompressedSize <= 0) return Array.Empty<byte>();

        lock (_lock)
        {
            var stream = GetPartStream(entry.TigerPart);
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            var magic4 = new byte[4];
            if (stream.Read(magic4, 0, 4) < 4) return Array.Empty<byte>();

            bool isMrdc = magic4[0] == 'M' && magic4[1] == 'R'
                       && magic4[2] == 'D' && magic4[3] == 'C';

            if (!isMrdc)
            {
                // Uncompressed — return the whole thing (usually small)
                stream.Seek(entry.Offset, SeekOrigin.Begin);
                var buf = new byte[entry.DecompressedSize];
                ReadFully(stream, buf);
                return buf;
            }

            // MRDC: decompress the first chunk in full — no byte limit
            stream.Seek(entry.Offset + 4, SeekOrigin.Begin);
            return DecompressMrdcFirstChunk(stream);
        }
    }

    private static byte[] DecompressMrdcFirstChunk(Stream src)
    {
        using var br = new BinaryReader(src, System.Text.Encoding.ASCII, leaveOpen: true);

        uint hdr      = br.ReadUInt32();
        uint compSz   = br.ReadUInt32();
        int  decompSz = (int)(hdr >> 8);
        if (decompSz == 0 || compSz == 0) return Array.Empty<byte>();

        if (compSz == (uint)decompSz)
            return br.ReadBytes((int)compSz);   // stored uncompressed

        var compressed = br.ReadBytes((int)compSz);
        using var ms      = new System.IO.MemoryStream(compressed);
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        var chunk = new byte[decompSz];
        deflate.ReadExactly(chunk, 0, decompSz);
        return chunk;
    }

    // ── MRDC decompression ────────────────────────────────────────────────────

    private static byte[] DecompressMrdc(Stream src, int expectedSize)
    {
        using var output = new MemoryStream(Math.Max(expectedSize, 4096));
        using var br     = new BinaryReader(src, Encoding.ASCII, leaveOpen: true);

        while (output.Length < expectedSize)
        {
            // chunk header: bits[31:8] = decompressed chunk size
            uint hdr       = br.ReadUInt32();
            uint compSz    = br.ReadUInt32();
            int  decompSz  = (int)(hdr >> 8);

            if (decompSz == 0 || compSz == 0) break;

            // Clamp to remaining needed so we don't over-write
            int needed = expectedSize - (int)output.Length;
            int outSz  = Math.Min(decompSz, needed);

            if (compSz == (uint)decompSz)
            {
                // Stored uncompressed — copy directly
                var raw = br.ReadBytes((int)compSz);
                output.Write(raw, 0, Math.Min(raw.Length, outSz));
            }
            else
            {
                // Raw Deflate (no zlib header/Adler checksum)
                var compressed = br.ReadBytes((int)compSz);
                using var ms      = new MemoryStream(compressed);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                var decompressed  = new byte[decompSz];
                deflate.ReadExactly(decompressed, 0, decompSz);
                output.Write(decompressed, 0, outSz);
            }
        }

        return output.ToArray();
    }

    // ── Part file access ──────────────────────────────────────────────────────

    private FileStream GetPartStream(ushort part)
    {
        if (!_partStreams.TryGetValue(part, out var stream))
        {
            // Replace the last "000.tiger" segment with "{part:D3}.tiger"
            // Works for both base game (bigfile.000.tiger) and DLC (bigfile.dlc.*.000.tiger)
            const int suffixLen = 9; // "000.tiger".Length
            string partPath = _indexPath[..^suffixLen] + $"{part:D3}.tiger";
            stream = new FileStream(partPath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 131072, useAsync: false);
            _partStreams[part] = stream;
        }
        return stream;
    }

    private static void ReadFully(Stream stream, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = stream.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
    }

    public void Dispose()
    {
        foreach (var s in _partStreams.Values) s.Dispose();
        _partStreams.Clear();
    }
}

// ── Data model ────────────────────────────────────────────────────────────────

public sealed class TigerEntry
{
    /// <summary>Zero-based position in the TOC.</summary>
    public int    Ordinal          { get; init; }
    /// <summary>FNV-1A 64-bit hash of the asset path — used as the primary identifier.</summary>
    public ulong  NameHash         { get; init; }
    /// <summary>Locale bitmask: 0 = common (all languages), non-zero = language-specific.</summary>
    public ulong  Locale           { get; init; }
    /// <summary>Uncompressed byte count of the asset data.</summary>
    public int    DecompressedSize { get; init; }
    /// <summary>On-disk byte count. May be 0 when the entry is stored uncompressed.</summary>
    public int    CompressedSize   { get; init; }
    /// <summary>Which .NNN.tiger data file contains this entry (0 = the index file itself).</summary>
    public ushort TigerPart        { get; init; }
    /// <summary>Load priority for conflict resolution across archives.</summary>
    public ushort Priority         { get; init; }
    /// <summary>Absolute byte offset from the start of the part file.</summary>
    public uint   Offset           { get; init; }
}
