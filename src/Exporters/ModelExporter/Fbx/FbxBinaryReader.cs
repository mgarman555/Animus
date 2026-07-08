using System.IO.Compression;
using System.Text;

namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

/// <summary>
/// Reads an FBX 7.x BINARY document back into an <see cref="FbxNode"/> tree.
///
/// Exists mainly for verification: it round-trips our own <see cref="FbxBinaryWriter"/> output and
/// parses genuine Blender/SDK-written FBX files, so tests can assert we understand the format the
/// same way real tools do. It is offset-driven — after a record's properties it reads child records
/// while the stream position is below the record's endOffset — so it does not depend on the writer's
/// null-terminator convention.
///
/// Supports version 7400 (4-byte record headers) and 7500 (8-byte). Array properties may be raw
/// (encoding 0) or zlib (encoding 1).
/// </summary>
public static class FbxBinaryReader
{
    public static List<FbxNode> Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var magic = r.ReadBytes(23);
        var expected = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");
        if (magic.Length < 23 || !magic.AsSpan(0, 20).SequenceEqual(expected))
            throw new InvalidDataException("Not an FBX binary file (bad magic).");

        uint version = r.ReadUInt32();
        bool wide = version >= 7500; // 7500+ uses 8-byte offsets/counts

        var nodes = new List<FbxNode>();
        while (true)
        {
            var node = ReadNode(r, wide);
            if (node == null) break; // top-level null record
            nodes.Add(node);
        }
        return nodes;
    }

    public static List<FbxNode> ReadFromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return Read(ms);
    }

    private static FbxNode? ReadNode(BinaryReader r, bool wide)
    {
        long endOffset  = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        long numProps   = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        long propListLen = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        byte nameLen    = r.ReadByte();

        // A null record (all header fields zero) terminates a nested list.
        if (endOffset == 0 && numProps == 0 && propListLen == 0 && nameLen == 0)
            return null;

        string name = Encoding.ASCII.GetString(r.ReadBytes(nameLen));
        var node = new FbxNode(name);

        for (long i = 0; i < numProps; i++)
            node.Properties.Add(ReadProperty(r));

        // Children follow if the record extends past its property list.
        while (r.BaseStream.Position < endOffset)
        {
            var child = ReadNode(r, wide);
            if (child == null) break;
            node.Children.Add(child);
        }

        r.BaseStream.Position = endOffset;
        return node;
    }

    private static object ReadProperty(BinaryReader r)
    {
        char code = (char)r.ReadByte();
        switch (code)
        {
            case 'Y': return r.ReadInt16();
            case 'C': return r.ReadByte() != 0;
            case 'I': return r.ReadInt32();
            case 'F': return r.ReadSingle();
            case 'D': return r.ReadDouble();
            case 'L': return r.ReadInt64();
            case 'S':
            {
                uint len = r.ReadUInt32();
                return Encoding.UTF8.GetString(r.ReadBytes((int)len));
            }
            case 'R':
            {
                uint len = r.ReadUInt32();
                return r.ReadBytes((int)len);
            }
            case 'f': return ReadArray(r, 4, (b, o) => BitConverter.ToSingle(b, o), a => a.Cast<float>().ToArray());
            case 'd': return ReadArray(r, 8, (b, o) => BitConverter.ToDouble(b, o), a => a.Cast<double>().ToArray());
            case 'i': return ReadArray(r, 4, (b, o) => BitConverter.ToInt32(b, o),  a => a.Cast<int>().ToArray());
            case 'l': return ReadArray(r, 8, (b, o) => BitConverter.ToInt64(b, o),  a => a.Cast<long>().ToArray());
            case 'b': return ReadArray(r, 1, (b, o) => b[o] != 0,                   a => a.Cast<bool>().ToArray());
            default:
                throw new InvalidDataException($"Unknown FBX property type code '{code}'.");
        }
    }

    private static Array ReadArray(BinaryReader r, int elemSize, Func<byte[], int, object> decode, Func<object[], Array> pack)
    {
        uint count = r.ReadUInt32();
        uint encoding = r.ReadUInt32();
        uint compLen = r.ReadUInt32();

        byte[] payload = r.ReadBytes((int)compLen);
        byte[] raw;
        if (encoding == 1)
        {
            using var ms = new MemoryStream(payload);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(checked((int)count * elemSize));
            z.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        else raw = payload;

        var items = new object[count];
        for (int i = 0; i < count; i++)
            items[i] = decode(raw, i * elemSize);
        return pack(items);
    }
}
