using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

/// <summary>
/// Writes an FBX 7.4 (version 7400) BINARY document from an <see cref="FbxNode"/> tree.
///
/// This is the format Blender's importer actually accepts — the previous ASCII writer produced
/// files Blender refuses to read. Layout follows the reverse-engineered binary spec
/// (https://code.blender.org/2013/08/fbx-binary-file-format-specification/):
///   23-byte magic, u32 version, then node records; each record is
///   [u32 endOffset][u32 numProps][u32 propListLen][u8 nameLen][name][properties][children][null?].
/// Array properties are zlib-compressed (encoding=1). Version 7400 uses 4-byte offsets.
///
/// Nested-list rule (SDK/Blender compatible): a node writes its child list followed by a single
/// 13-byte null terminator iff it has children OR has zero properties (the "empty compound" case).
/// <see cref="FbxBinaryReader"/> is offset-driven (reads children while pos &lt; endOffset), so it
/// parses both our output and genuine SDK-written files regardless of this subtlety.
/// </summary>
public static class FbxBinaryWriter
{
    private const uint Version = 7400;
    // "Kaydara FBX Binary  " (two trailing spaces) + 0x00 0x1A 0x00 = 23 bytes.
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("Kaydara FBX Binary  ").Concat(new byte[] { 0x00, 0x1A, 0x00 }).ToArray();

    public static void Write(Stream stream, IReadOnlyList<FbxNode> topLevel)
    {
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);

        foreach (var node in topLevel)
            WriteNode(w, node);
        WriteNullRecord(w); // terminates the top-level list

        WriteFooter(w);
    }

    public static byte[] WriteToBytes(IReadOnlyList<FbxNode> topLevel)
    {
        using var ms = new MemoryStream();
        Write(ms, topLevel);
        return ms.ToArray();
    }

    private static void WriteNode(BinaryWriter w, FbxNode node)
    {
        long headerStart = w.BaseStream.Position;

        // Reserve the three u32 header fields; patched once the record length is known.
        w.Write(0u); // endOffset
        w.Write(0u); // numProperties
        w.Write(0u); // propertyListLen

        var nameBytes = Encoding.ASCII.GetBytes(node.Name);
        w.Write((byte)nameBytes.Length);
        w.Write(nameBytes);

        long propsStart = w.BaseStream.Position;
        foreach (var p in node.Properties)
            WriteProperty(w, p);
        long propsEnd = w.BaseStream.Position;
        uint propListLen = (uint)(propsEnd - propsStart);

        bool nested = node.Children.Count > 0 || node.Properties.Count == 0;
        if (nested)
        {
            foreach (var child in node.Children)
                WriteNode(w, child);
            WriteNullRecord(w);
        }

        long endOffset = w.BaseStream.Position;

        // Patch header fields.
        w.BaseStream.Position = headerStart;
        w.Write((uint)endOffset);
        w.Write((uint)node.Properties.Count);
        w.Write(propListLen);
        w.BaseStream.Position = endOffset;
    }

    private static void WriteNullRecord(BinaryWriter w)
    {
        // 13 zero bytes for version < 7500 (three u32 + one u8).
        w.Write(new byte[13]);
    }

    private static void WriteProperty(BinaryWriter w, object p)
    {
        switch (p)
        {
            case short s:  w.Write((byte)'Y'); w.Write(s); break;
            case bool b:   w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case int i:    w.Write((byte)'I'); w.Write(i); break;
            case float f:  w.Write((byte)'F'); w.Write(f); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case long l:   w.Write((byte)'L'); w.Write(l); break;

            case string str:
                w.Write((byte)'S');
                var sb = Encoding.UTF8.GetBytes(str);
                w.Write((uint)sb.Length);
                w.Write(sb);
                break;

            case byte[] raw:
                w.Write((byte)'R');
                w.Write((uint)raw.Length);
                w.Write(raw);
                break;

            case float[] fa:  WriteArray(w, 'f', fa.Length, 4, buf => { foreach (var v in fa) buf.Write(v); }); break;
            case double[] da: WriteArray(w, 'd', da.Length, 8, buf => { foreach (var v in da) buf.Write(v); }); break;
            case int[] ia:    WriteArray(w, 'i', ia.Length, 4, buf => { foreach (var v in ia) buf.Write(v); }); break;
            case long[] la:   WriteArray(w, 'l', la.Length, 8, buf => { foreach (var v in la) buf.Write(v); }); break;
            case bool[] ba:   WriteArray(w, 'b', ba.Length, 1, buf => { foreach (var v in ba) buf.Write((byte)(v ? 1 : 0)); }); break;

            default:
                throw new NotSupportedException($"FBX property type not supported: {p.GetType()}");
        }
    }

    /// <summary>
    /// Writes an array property: type code, then [u32 count][u32 encoding][u32 compressedLen][data].
    /// Encoding 1 = zlib. We always zlib-compress (matches genuine FBX files and shrinks big buffers);
    /// if compression somehow grows the payload we fall back to raw encoding 0.
    /// </summary>
    private static void WriteArray(BinaryWriter w, char code, int count, int elemSize, Action<BinaryWriter> writeElems)
    {
        using var rawMs = new MemoryStream(count * elemSize);
        using (var rawW = new BinaryWriter(rawMs, Encoding.ASCII, leaveOpen: true))
            writeElems(rawW);
        var rawBytes = rawMs.ToArray();

        using var compMs = new MemoryStream();
        using (var z = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rawBytes, 0, rawBytes.Length);
        var compBytes = compMs.ToArray();

        w.Write((byte)code);
        w.Write((uint)count);
        if (compBytes.Length < rawBytes.Length)
        {
            w.Write(1u);                       // encoding: zlib
            w.Write((uint)compBytes.Length);
            w.Write(compBytes);
        }
        else
        {
            w.Write(0u);                       // encoding: raw
            w.Write((uint)rawBytes.Length);
            w.Write(rawBytes);
        }
    }

    // Footer: Blender ignores it entirely; the FBX SDK is stricter. We emit the documented
    // layout with the well-known constant magics. If UE's SDK importer ever rejects a file this
    // is the isolated place to revisit (verified on the Windows PC).
    private static readonly byte[] FooterMagic =
    {
        0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E, 0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B
    };

    private static void WriteFooter(BinaryWriter w)
    {
        // 16-byte pre-footer id (content-independent constant; Blender does not validate it).
        w.Write(new byte[16]);

        // Pad with zeros to a 16-byte boundary (offset measured from here per the spec).
        long pos = w.BaseStream.Position;
        int pad = (int)(((pos + 15) & ~15L) - pos);
        if (pad == 0) pad = 16;
        w.Write(new byte[pad]);

        w.Write(new byte[4]);   // 4 zero bytes
        w.Write(Version);       // version again
        w.Write(new byte[120]); // reserved
        w.Write(FooterMagic);
    }
}
