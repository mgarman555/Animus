using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Parses the PSARC (PlayStation Archive) format used by Naughty Dog games.
///
/// Format overview (all integers big-endian):
///   Header  (32 bytes): magic, version, compression type, TOC length,
///                        entry size, entry count, block size, flags
///   TOC entries (30 bytes each): MD5 digest, block index, uncompressed size (5 bytes),
///                                 data offset (5 bytes)
///   Block-size table: variable-width integers giving the compressed byte count per block
///
/// Entry 0 is always the path manifest — a UTF-8 newline-delimited list that maps
/// TOC entry N to the real file path at line N-1.
///
/// Compression: zlib (standard header + deflate payload).
/// </summary>
public sealed class PsarcReader : IDisposable
{
    private const uint Magic     = 0x50534152; // "PSAR" — standard PSARC
    private const uint MagicDsar = 0x44534152; // "DSAR" — TLOU2 PC Remastered encrypted variant

    private readonly string _archivePath;
    private FileStream?     _stream;
    private int[]           _blockTable  = Array.Empty<int>();
    private int             _blockEntryBytes;

    public uint            BlockSize        { get; private set; }
    public string          CompressionType  { get; private set; } = "zlib";
    public List<PsarcEntry> Entries         { get; } = new();

    public PsarcReader(string archivePath) => _archivePath = archivePath;

    // ── Open / parse ──────────────────────────────────────────────────────────

    public void Open()
    {
        _stream = new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Parse();
    }

    private void Parse()
    {
        using var br = new BinaryReader(_stream!, Encoding.ASCII, leaveOpen: true);

        // ── Header ──────────────────────────────────────────────────────────
        uint magic = ReadU32(br);
        if (magic == MagicDsar)
            throw new NotSupportedException(
                $"DSAR format detected in {Path.GetFileName(_archivePath)}. " +
                "TLOU2 PC Remastered uses an encrypted PSARC variant (DSAR) that is not yet supported. " +
                "The encryption key for this format has not been publicly documented.");
        if (magic != Magic)
            throw new InvalidDataException($"Not a PSARC file (magic 0x{magic:X8}): {Path.GetFileName(_archivePath)}");

        ReadU16(br); ReadU16(br);                      // version (1.4 — ignored)
        CompressionType = Encoding.ASCII.GetString(br.ReadBytes(4)).TrimEnd('\0');

        uint tocLength   = ReadU32(br);
        uint entrySize   = ReadU32(br);   // always 30
        uint entryCount  = ReadU32(br);
        BlockSize        = ReadU32(br);   // typically 65536
        ReadU32(br);                      // archive flags (ignored)

        // Determine bytes per block-table entry.
        // Entries store compressed block size (0 = stored raw at full BlockSize).
        // Max stored value is BlockSize-1, so we need enough bytes to hold BlockSize.
        //   BlockSize=256   → 1 byte  (max 255)
        //   BlockSize=65536 → 2 bytes (max 65535; the standard case for all ND games)
        //   BlockSize>65536 → 3 bytes
        _blockEntryBytes = BlockSize switch
        {
            <= 0x0000_00FF => 1,
            <= 0x0001_0000 => 2,   // ← covers standard 65536 (0x10000)
            <= 0x00FF_FFFF => 3,
            _              => 4
        };

        // ── TOC entries ──────────────────────────────────────────────────────
        var raw = new (byte[] digest, uint blockIdx, ulong uncompSize, ulong offset)[entryCount];
        for (int i = 0; i < entryCount; i++)
            raw[i] = (br.ReadBytes(16), ReadU32(br), Read5(br), Read5(br));

        // ── Block-size table ─────────────────────────────────────────────────
        long blockTableStart = 32 + entryCount * 30L;
        long blockTableBytes = tocLength - blockTableStart;
        int  blockCount      = (int)(blockTableBytes / _blockEntryBytes);

        _stream!.Seek(blockTableStart, SeekOrigin.Begin);
        _blockTable = new int[blockCount];
        for (int i = 0; i < blockCount; i++)
            _blockTable[i] = _blockEntryBytes switch
            {
                1 => br.ReadByte(),
                2 => (int)ReadU16(br),
                3 => (int)Read3(br),
                _ => (int)ReadU32(br)
            };

        // ── Manifest (entry 0) → file paths ──────────────────────────────────
        if (entryCount == 0) return;

        var mRaw      = raw[0];
        var mBytes    = ExtractRaw(mRaw.offset, mRaw.uncompSize, mRaw.blockIdx);
        var paths     = Encoding.UTF8.GetString(mBytes)
                                     .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 1; i < entryCount; i++)
        {
            var r    = raw[i];
            string p = (i - 1 < paths.Length) ? paths[i - 1].Trim() : $"__unknown_{i}";
            Entries.Add(new PsarcEntry
            {
                Path             = p,
                Offset           = r.offset,
                UncompressedSize = r.uncompSize,
                BlockIndex       = r.blockIdx,
                NameDigest       = r.digest
            });
        }
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    public byte[] ExtractFile(PsarcEntry entry)
        => ExtractRaw(entry.Offset, entry.UncompressedSize, entry.BlockIndex);

    private byte[] ExtractRaw(ulong fileOffset, ulong uncompSize, uint firstBlockIdx)
    {
        if (uncompSize == 0) return Array.Empty<byte>();

        var  output   = new byte[uncompSize];
        int  written  = 0;
        uint blockIdx = firstBlockIdx;

        while (written < (int)uncompSize)
        {
            int remaining      = (int)uncompSize - written;
            int blockUncompLen = (int)Math.Min(BlockSize, (ulong)remaining);
            int compLen        = (blockIdx < _blockTable.Length) ? _blockTable[blockIdx] : 0;

            _stream!.Seek((long)fileOffset, SeekOrigin.Begin);

            if (compLen == 0)
            {
                // Block stored uncompressed at exactly blockUncompLen bytes
                _ = _stream.Read(output, written, blockUncompLen);
                fileOffset += (ulong)blockUncompLen;
            }
            else
            {
                // zlib-compressed block — try ZLibStream first (handles 2-byte header),
                // fall back to raw DeflateStream for blocks without the header
                var compressed = new byte[compLen];
                _ = _stream.Read(compressed, 0, compLen);

                try
                {
                    using var ms  = new MemoryStream(compressed);
                    using var zls = new ZLibStream(ms, CompressionMode.Decompress);
                    int got = 0;
                    while (got < blockUncompLen)
                    {
                        int n = zls.Read(output, written + got, blockUncompLen - got);
                        if (n == 0) break;
                        got += n;
                    }
                }
                catch
                {
                    // Fallback: raw deflate (skip 2-byte zlib header if present)
                    int skip = (compressed.Length >= 2 &&
                                compressed[0] == 0x78 &&
                                (compressed[1] == 0x9C || compressed[1] == 0xDA || compressed[1] == 0x01))
                               ? 2 : 0;
                    using var ms2  = new MemoryStream(compressed, skip, compressed.Length - skip);
                    using var dfl  = new DeflateStream(ms2, CompressionMode.Decompress);
                    int got = 0;
                    while (got < blockUncompLen)
                    {
                        int n = dfl.Read(output, written + got, blockUncompLen - got);
                        if (n == 0) break;
                        got += n;
                    }
                }

                fileOffset += (ulong)compLen;
            }

            written += blockUncompLen;
            blockIdx++;
        }

        return output;
    }

    // ── Big-endian helpers ────────────────────────────────────────────────────

    private static uint   ReadU32(BinaryReader r) { var b = r.ReadBytes(4); return (uint) ((b[0]<<24)|(b[1]<<16)|(b[2]<<8)|b[3]); }
    private static ushort ReadU16(BinaryReader r) { var b = r.ReadBytes(2); return (ushort)((b[0]<<8)|b[1]); }
    private static uint   Read3  (BinaryReader r) { var b = r.ReadBytes(3); return (uint) ((b[0]<<16)|(b[1]<<8)|b[2]); }
    private static ulong  Read5  (BinaryReader r) { var b = r.ReadBytes(5); return ((ulong)b[0]<<32)|((ulong)b[1]<<24)|((ulong)b[2]<<16)|((ulong)b[3]<<8)|b[4]; }

    public void Dispose() => _stream?.Dispose();
}

public sealed class PsarcEntry
{
    public string Path             { get; set; } = string.Empty;
    public ulong  Offset           { get; set; }
    public ulong  UncompressedSize { get; set; }
    public uint   BlockIndex       { get; set; }
    public byte[] NameDigest       { get; set; } = Array.Empty<byte>();
}
