using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// CDRM — the compressed-blob container used by entries inside .tiger archives.
///
/// Verified against arcusmaximus/TrRebootModTools (MIT), Shared/Cdc/ArchiveDecompressionStream.cs.
///
/// Layout (little-endian):
///   +0x00  uint32  magic = 0x4D524443 — the bytes 'C','D','R','M' in file order
///   +0x04  int32   type
///   +0x08  int32   numChunks
///   +0x0C  int32   (unused)
///   +0x10  numChunks × 8-byte chunk descriptors:
///            uint32  packed  — the uncompressed chunk size is bits [31:8]
///            uint32  compressedSize
///
/// Chunk payloads begin at align16(0x10 + 8 × numChunks) and each payload is padded
/// to the next 16-byte boundary. A chunk whose compressed size equals its uncompressed
/// size is stored verbatim; otherwise it is zlib — a 2-byte header followed by a raw
/// Deflate stream, so the 2 bytes must be skipped before handing it to DeflateStream.
/// </summary>
internal static class Cdrm
{
    /// <summary>Little-endian uint32 for the on-disk byte sequence 'C','D','R','M'.</summary>
    public const uint Magic = 0x4D524443;

    public static bool HasMagic(Stream stream, long offset)
    {
        if (offset < 0 || offset + 4 > stream.Length) return false;

        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> magic = stackalloc byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = stream.Read(magic[read..]);
            if (n == 0) return false;
            read += n;
        }
        return BitConverter.ToUInt32(magic) == Magic;
    }

    /// <summary>
    /// Decompresses a CDRM blob starting at <paramref name="offset"/>.
    /// <paramref name="chunkLimit"/> caps how many chunks are decoded — pass 1 to cheaply
    /// peek at the head of a large asset instead of inflating all of it.
    /// </summary>
    public static byte[] Decompress(Stream stream, long offset, int chunkLimit = int.MaxValue)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a CDRM blob.");

        reader.ReadInt32();                    // type
        int numChunks = reader.ReadInt32();
        reader.ReadInt32();                    // unused

        if (numChunks < 0 || numChunks > 0x100000)
            throw new InvalidDataException($"CDRM chunk count out of range ({numChunks}).");

        int take   = Math.Min(numChunks, Math.Max(chunkLimit, 0));
        var chunks = new (long Offset, int Unpacked, int Packed)[take];

        long payload = Align16(offset + 0x10 + 8L * numChunks);
        long total   = 0;

        for (int i = 0; i < numChunks; i++)
        {
            int unpacked = (int)(reader.ReadUInt32() >> 8);
            int packed   = reader.ReadInt32();

            if (unpacked < 0 || packed < 0)
                throw new InvalidDataException("CDRM chunk size out of range.");

            if (i < take)
            {
                chunks[i] = (payload, unpacked, packed);
                total += unpacked;
            }
            payload = Align16(payload + packed);
        }

        var output  = new byte[total];
        int written = 0;

        foreach (var (chunkOffset, unpacked, packed) in chunks)
        {
            if (unpacked == 0) continue;

            if (packed == unpacked)
            {
                stream.Seek(chunkOffset, SeekOrigin.Begin);
                stream.ReadExactly(output, written, unpacked);
            }
            else
            {
                // zlib: skip the 2-byte header, the remainder is a raw Deflate stream
                stream.Seek(chunkOffset + 2, SeekOrigin.Begin);
                using var deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
                deflate.ReadExactly(output, written, unpacked);
            }
            written += unpacked;
        }

        return output;
    }

    private static long Align16(long value) => (value + 0xF) & ~0xFL;
}
