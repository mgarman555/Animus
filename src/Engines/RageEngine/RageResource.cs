using System.IO.Compression;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// A RAGE resource file (<c>RSC7</c>) — the container every .ytd/.ydr/.ydd/.yft ships in.
///
/// Header (16 bytes, uncompressed):
///   +0x00 u32 magic 0x37435352 ("RSC7")
///   +0x04 u32 version          (13 for a texture dictionary)
///   +0x08 u32 systemFlags      — size of the STRUCTURE segment, bit-packed
///   +0x0C u32 graphicsFlags    — size of the PIXEL DATA segment, bit-packed
/// Everything after the header is one raw DEFLATE stream which inflates to the system
/// segment followed immediately by the graphics segment.
///
/// Pointers inside a resource are virtual addresses, not file offsets: the top nibble names
/// the segment (5 = system, 6 = graphics) and the low 28 bits are the offset within it. That
/// is why a resource cannot be read as a flat buffer — every pointer has to be routed through
/// <see cref="Resolve"/> to land in the right segment.
///
/// Layout reference: CodeWalker (dexyfex), <c>RpfFile.GetSizeFromFlags</c> and
/// <c>ResourceDataReader</c>'s SYSTEM_BASE/GRAPHICS_BASE addressing.
/// </summary>
public sealed class RageResource
{
    public const uint Rsc7Magic = 0x37435352;

    private const int SystemSegment   = 5;
    private const int GraphicsSegment = 6;

    /// <summary>Structure data — headers, tables, pointers.</summary>
    public byte[] SystemData { get; private init; } = Array.Empty<byte>();

    /// <summary>Pixel / vertex payload.</summary>
    public byte[] GraphicsData { get; private init; } = Array.Empty<byte>();

    public uint Version { get; private init; }

    /// <summary>
    /// Parse and inflate an RSC7 resource. Returns null when the data is not a resource or the
    /// deflate stream is unusable — callers treat that as "not a resource", not as an error.
    /// </summary>
    public static RageResource? TryLoad(byte[] data, string label)
    {
        if (data.Length < 16) return null;
        if (BitConverter.ToUInt32(data, 0) != Rsc7Magic) return null;

        uint version       = BitConverter.ToUInt32(data, 4);
        uint systemFlags   = BitConverter.ToUInt32(data, 8);
        uint graphicsFlags = BitConverter.ToUInt32(data, 12);

        int systemSize   = GetSizeFromFlags(systemFlags);
        int graphicsSize = GetSizeFromFlags(graphicsFlags);

        if (systemSize < 0 || graphicsSize < 0 ||
            (long)systemSize + graphicsSize > 512L * 1024 * 1024)
        {
            Log.Warn($"RageResource[{label}]: implausible segment sizes " +
                     $"(system={systemSize}, graphics={graphicsSize})");
            return null;
        }

        byte[] inflated;
        try
        {
            using var src = new MemoryStream(data, 16, data.Length - 16, writable: false);
            using var ds  = new DeflateStream(src, CompressionMode.Decompress);
            using var outp = new MemoryStream(systemSize + graphicsSize);
            ds.CopyTo(outp);
            inflated = outp.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"RageResource[{label}]: inflate failed — {ex.Message}");
            return null;
        }

        // A truncated stream still yields whatever inflated cleanly; take what we got rather
        // than discarding a resource whose tail (often trailing mips) is missing.
        var sys = new byte[systemSize];
        var gfx = new byte[graphicsSize];
        Array.Copy(inflated, 0, sys, 0, Math.Min(systemSize, inflated.Length));
        if (inflated.Length > systemSize)
            Array.Copy(inflated, systemSize, gfx, 0,
                       Math.Min(graphicsSize, inflated.Length - systemSize));

        if (inflated.Length < systemSize + graphicsSize)
            Log.Info($"RageResource[{label}]: inflated {inflated.Length:N0} of " +
                     $"{systemSize + graphicsSize:N0} expected bytes — reading what is present");

        return new RageResource { SystemData = sys, GraphicsData = gfx, Version = version };
    }

    /// <summary>
    /// Route a virtual address to its segment. Returns false for a null or unrecognised
    /// pointer, or one that points past the end of its segment.
    /// </summary>
    public bool Resolve(ulong pointer, out byte[] segment, out int offset)
    {
        segment = Array.Empty<byte>();
        offset  = 0;
        if (pointer == 0) return false;

        int which = (int)((pointer >> 28) & 0xF);
        int off   = (int)(pointer & 0x0FFFFFFF);

        switch (which)
        {
            case SystemSegment:   segment = SystemData;   break;
            case GraphicsSegment: segment = GraphicsData; break;
            default: return false;
        }

        if (off < 0 || off >= segment.Length) return false;
        offset = off;
        return true;
    }

    /// <summary>True when the pointer resolves and at least <paramref name="need"/> bytes follow.</summary>
    public bool CanRead(ulong pointer, int need, out byte[] segment, out int offset)
        => Resolve(pointer, out segment, out offset) && offset + need <= segment.Length;

    /// <summary>Null-terminated ASCII at a virtual address.</summary>
    public string ReadString(ulong pointer, int maxLen = 256)
    {
        if (!Resolve(pointer, out var seg, out int o)) return string.Empty;
        int end = o;
        while (end < seg.Length && end - o < maxLen && seg[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(seg, o, end - o);
    }

    /// <summary>
    /// Segment size from a packed flags word. Nine bit-fields each contribute a count of pages
    /// at a different multiplier, and the low nibble sets the page size — so the total is
    /// <c>(0x200 &lt;&lt; ss) × Σ(pageCount × multiplier)</c>.
    ///
    /// Verbatim from CodeWalker's <c>RpfResourceFileEntry.GetSizeFromFlags</c>; the field
    /// widths are not guessable and getting one wrong silently truncates a segment.
    /// </summary>
    public static int GetSizeFromFlags(uint flags)
    {
        long s0 = ((flags >> 27) & 0x1)  << 0;   // ×1
        long s1 = ((flags >> 26) & 0x1)  << 1;   // ×2
        long s2 = ((flags >> 25) & 0x1)  << 2;   // ×4
        long s3 = ((flags >> 24) & 0x1)  << 3;   // ×8
        long s4 = ((flags >> 17) & 0x7F) << 4;   // ×16
        long s5 = ((flags >> 11) & 0x3F) << 5;   // ×32
        long s6 = ((flags >> 7)  & 0xF)  << 6;   // ×64
        long s7 = ((flags >> 5)  & 0x3)  << 7;   // ×128
        long s8 = ((flags >> 4)  & 0x1)  << 8;   // ×256
        long ss = (flags >> 0) & 0xF;

        long baseSize = 0x200L << (int)ss;
        long size = baseSize * (s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8);
        return size > int.MaxValue ? -1 : (int)size;
    }
}
