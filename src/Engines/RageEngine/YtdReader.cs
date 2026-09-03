using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// Reads a RAGE texture dictionary (<c>.ytd</c>) — the container GTA V and RDR 2 bundle their
/// textures in — out of an <see cref="RageResource"/>.
///
/// Structure layout, all offsets from the start of the dictionary (virtual address
/// <c>0x50000000</c>, i.e. the top of the system segment):
///
///   TextureDictionary (64 bytes)
///     +0x00  ResourceFileBase (VFT + 3 unused u32)
///     +0x10  4 × u32 unused
///     +0x20  name-hash list   { u64 ptr, u16 count, u16 capacity, u32 pad }
///     +0x30  texture list     { u64 ptr, u16 count, u16 capacity, u32 pad }
///              → the pointer addresses an array of u64, one per texture
///
///   TextureBase (80 bytes) then Texture (to 144 bytes)
///     +0x28  u64 name pointer
///     +0x50  u16 width   +0x52 u16 height   +0x54 u16 depth   +0x56 u16 stride
///     +0x58  u32 format  (D3DFMT / FourCC)
///     +0x5D  u8  mip levels
///     +0x70  u64 pixel-data pointer  → the graphics segment
///
/// Layout reference: CodeWalker (dexyfex), <c>Texture.cs</c>.
/// </summary>
public static class YtdReader
{
    private const int DictTextureListOffset = 0x30;
    private const int TextureStride         = 144;

    public sealed class YtdTexture
    {
        public string Name   { get; init; } = string.Empty;
        public int    Width  { get; init; }
        public int    Height { get; init; }
        public int    Levels { get; init; }
        public string Format { get; init; } = string.Empty;
        /// <summary>Mip chain, index 0 = full resolution.</summary>
        public List<MipData> Mips { get; init; } = new();
    }

    /// <summary>
    /// Decode every texture in the dictionary. Returns an empty list when the data is not a
    /// texture dictionary; individual textures that fail are skipped rather than aborting the
    /// dictionary, since one bad entry should not cost the other few hundred.
    /// </summary>
    public static List<YtdTexture> Read(byte[] fileData, string label)
    {
        var results = new List<YtdTexture>();

        var res = RageResource.TryLoad(fileData, label);
        if (res == null) return results;

        var sys = res.SystemData;
        if (sys.Length < 64)
        {
            Log.Warn($"YtdReader[{label}]: system segment too small ({sys.Length} bytes)");
            return results;
        }

        ulong listPtr = BitConverter.ToUInt64(sys, DictTextureListOffset);
        int   count   = BitConverter.ToUInt16(sys, DictTextureListOffset + 8);

        if (count <= 0 || count > 8192)
        {
            Log.Warn($"YtdReader[{label}]: implausible texture count {count}");
            return results;
        }
        if (!res.CanRead(listPtr, count * 8, out var listSeg, out int listOff))
        {
            Log.Warn($"YtdReader[{label}]: texture pointer array does not resolve " +
                     $"(ptr=0x{listPtr:X}, count={count})");
            return results;
        }

        for (int i = 0; i < count; i++)
        {
            ulong texPtr = BitConverter.ToUInt64(listSeg, listOff + i * 8);
            var tex = ReadTexture(res, texPtr, label, i);
            if (tex != null) results.Add(tex);
        }

        Log.Info($"YtdReader[{label}]: {results.Count}/{count} textures decoded " +
                 $"(system {sys.Length:N0} B, graphics {res.GraphicsData.Length:N0} B)");
        return results;
    }

    private static YtdTexture? ReadTexture(RageResource res, ulong texPtr, string label, int index)
    {
        if (!res.CanRead(texPtr, TextureStride, out var seg, out int o)) return null;

        int width  = BitConverter.ToUInt16(seg, o + 0x50);
        int height = BitConverter.ToUInt16(seg, o + 0x52);
        int levels = seg[o + 0x5D];
        uint fmt   = BitConverter.ToUInt32(seg, o + 0x58);
        ulong dataPtr  = BitConverter.ToUInt64(seg, o + 0x70);
        ulong namePtr  = BitConverter.ToUInt64(seg, o + 0x28);

        if (width  is <= 0 or > 16384) return null;
        if (height is <= 0 or > 16384) return null;
        if (levels <= 0) levels = 1;
        if (levels > 16) levels = 16;

        string format = FormatName(fmt);
        if (format.Length == 0)
        {
            Log.Info($"YtdReader[{label}]: texture {index} has unsupported format 0x{fmt:X8}");
            return null;
        }

        string name = res.ReadString(namePtr);
        if (name.Length == 0) name = $"texture_{index}";

        var mips = new List<MipData>(levels);
        if (res.Resolve(dataPtr, out var pix, out int pixOff))
        {
            int w = width, h = height, cursor = pixOff;
            for (int m = 0; m < levels; m++)
            {
                int size = MipSize(format, w, h);
                if (size <= 0 || cursor + size > pix.Length) break;

                var buf = new byte[size];
                Array.Copy(pix, cursor, buf, 0, size);
                mips.Add(new MipData { Width = w, Height = h, Data = buf });

                cursor += size;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
        }

        if (mips.Count == 0) return null;

        return new YtdTexture
        {
            Name = name, Width = width, Height = height,
            Levels = mips.Count, Format = format, Mips = mips,
        };
    }

    /// <summary>D3D format / FourCC → the block-format names the texture decoder understands.</summary>
    public static string FormatName(uint format) => format switch
    {
        0x31545844 => "BC1",    // DXT1
        0x33545844 => "BC2",    // DXT3
        0x35545844 => "BC3",    // DXT5
        0x31495441 => "BC4",    // ATI1
        0x32495441 => "BC5",    // ATI2
        0x20374342 => "BC7",
        21         => "BGRA8",  // D3DFMT_A8R8G8B8
        22         => "BGRX8",  // D3DFMT_X8R8G8B8
        32         => "RGBA8",  // D3DFMT_A8B8G8R8
        28         => "A8",
        50         => "L8",
        _          => string.Empty,
    };

    /// <summary>
    /// Bytes in one mip. Block formats round up to whole 4×4 blocks, so a 1×1 BC1 mip still
    /// occupies a full 8-byte block — walking the chain with the unrounded size drifts and
    /// every mip after the first comes out shifted.
    /// </summary>
    public static int MipSize(string format, int w, int h)
    {
        int blocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
        return format switch
        {
            "BC1" or "BC4"                   => blocks * 8,
            "BC2" or "BC3" or "BC5" or "BC7" => blocks * 16,
            "BGRA8" or "BGRX8" or "RGBA8"    => w * h * 4,
            "A8" or "L8"                     => w * h,
            _                                => 0,
        };
    }
}
