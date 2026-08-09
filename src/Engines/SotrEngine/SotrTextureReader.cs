using System.Text;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Decodes Shadow of the Tomb Raider texture resources.
///
/// SOTTR textures are not DDS files — they are cdc "PCD9" textures: a 28-byte header
/// carrying a raw DXGI format number, followed by the block-compressed surface data with
/// no DDS wrapper at all. Scanning a texture resource for a "DDS " magic finds nothing.
/// This reader parses the real header and splits the mip chain; wrapping the surface back
/// up as a .dds file is the exporter's job.
///
/// Layout verified against arcusmaximus/TrRebootModTools (MIT), Shared/Cdc/CdcTexture.cs.
///
/// Header — 28 bytes, little-endian:
///   +0x00  uint32  magic = 0x39444350 — the bytes 'P','C','D','9'
///   +0x04  uint32  format             DXGI_FORMAT enum value
///   +0x08  uint32  size               payload byte count
///   +0x0C  uint32  highResMipMapLevels
///   +0x10  uint16  width
///   +0x12  uint16  height
///   +0x14  uint16  volumeDepth
///   +0x16  uint8   depth
///   +0x17  uint8   mipMapLevels
///   +0x18  uint16  flags              0x2000 = a 0x100-byte extra block follows the header
///                                     0x8000 = cube map
///   +0x1A  uint8   class
///   +0x1B  uint8   tileMode
/// </summary>
public sealed class SotrTextureReader
{
    /// <summary>Little-endian uint32 for the on-disk byte sequence 'P','C','D','9'.</summary>
    public const uint Magic = 0x39444350;

    private const int HEADER_SIZE = 0x1C;

    // DXGI formats we name explicitly
    private const uint DXGI_R8G8B8A8_UNORM      = 28;
    private const uint DXGI_R8G8B8A8_UNORM_SRGB = 29;
    private const uint DXGI_R8G8_UNORM          = 49;
    private const uint DXGI_R8_UNORM            = 61;
    private const uint DXGI_BC1_UNORM           = 71;
    private const uint DXGI_BC1_UNORM_SRGB      = 72;
    private const uint DXGI_BC2_UNORM           = 74;
    private const uint DXGI_BC2_UNORM_SRGB      = 75;
    private const uint DXGI_BC3_UNORM           = 77;
    private const uint DXGI_BC3_UNORM_SRGB      = 78;
    private const uint DXGI_BC4_UNORM           = 80;
    private const uint DXGI_BC5_UNORM           = 83;
    private const uint DXGI_B8G8R8A8_UNORM      = 87;
    private const uint DXGI_B8G8R8A8_UNORM_SRGB = 91;
    private const uint DXGI_BC6H_UF16           = 95;
    private const uint DXGI_BC7_UNORM           = 98;
    private const uint DXGI_BC7_UNORM_SRGB      = 99;

    public uint   Format              { get; private set; }
    public int    Width               { get; private set; }
    public int    Height              { get; private set; }
    public int    VolumeDepth         { get; private set; }
    public int    MipMapLevels        { get; private set; }
    /// <summary>
    /// Mip levels the engine streams from elsewhere rather than storing in this resource.
    /// Zero for the vast majority of SOTTR textures.
    /// </summary>
    public int    HighResMipMapLevels { get; private set; }
    public int    Flags               { get; private set; }
    public int    TileMode            { get; private set; }
    public byte[] Data                { get; private set; } = Array.Empty<byte>();

    public bool IsCubeMap => (Flags & 0x8000) != 0;
    public bool IsSrgb    => MapSrgbToRegular(Format) != Format;

    /// <summary>Short format name — "BC7", "BC1", … — matching what the PNG exporter expects.</summary>
    public string FormatName => Format switch
    {
        DXGI_BC1_UNORM or DXGI_BC1_UNORM_SRGB           => "BC1",
        DXGI_BC2_UNORM or DXGI_BC2_UNORM_SRGB           => "BC2",
        DXGI_BC3_UNORM or DXGI_BC3_UNORM_SRGB           => "BC3",
        DXGI_BC4_UNORM                                  => "BC4",
        DXGI_BC5_UNORM                                  => "BC5",
        DXGI_BC6H_UF16                                  => "BC6H",
        DXGI_BC7_UNORM or DXGI_BC7_UNORM_SRGB           => "BC7",
        DXGI_R8G8B8A8_UNORM or DXGI_R8G8B8A8_UNORM_SRGB => "RGBA8",
        DXGI_B8G8R8A8_UNORM or DXGI_B8G8R8A8_UNORM_SRGB => "BGRA8",
        DXGI_R8G8_UNORM                                 => "R8G8",
        DXGI_R8_UNORM                                   => "R8",
        _                                               => $"DXGI:{Format}",
    };

    // ── Parse ────────────────────────────────────────────────────────────────

    public static bool LooksLikeTexture(byte[] data)
        => data.Length >= HEADER_SIZE && BitConverter.ToUInt32(data, 0) == Magic;

    /// <summary>Returns null when the buffer is not a PCD9 texture.</summary>
    public static SotrTextureReader? TryRead(byte[] body)
    {
        try   { return Read(body); }
        catch { return null; }
    }

    public static SotrTextureReader Read(byte[] body)
    {
        if (body.Length < HEADER_SIZE)
            throw new InvalidDataException("Buffer too small for a PCD9 header.");

        using var ms = new MemoryStream(body, writable: false);
        using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Not a PCD9 texture (magic=0x{magic:X8}).");

        var tex = new SotrTextureReader();
        tex.Format              = br.ReadUInt32();
        uint size               = br.ReadUInt32();
        tex.HighResMipMapLevels = (int)br.ReadUInt32();
        tex.Width               = br.ReadUInt16();
        tex.Height              = br.ReadUInt16();
        tex.VolumeDepth         = br.ReadUInt16();
        /* depth */               br.ReadByte();
        tex.MipMapLevels        = br.ReadByte();
        tex.Flags               = br.ReadUInt16();
        /* class */               br.ReadByte();
        tex.TileMode            = br.ReadByte();

        // 0x2000 marks a 0x100-byte block between the header and the surface data
        if ((tex.Flags & 0x2000) != 0 && ms.Position + 0x100 <= ms.Length)
            ms.Position += 0x100;

        long available = ms.Length - ms.Position;
        int  take      = (int)Math.Min(size, Math.Max(0, available));
        tex.Data = take > 0 ? br.ReadBytes(take) : Array.Empty<byte>();

        if (tex.MipMapLevels < 1) tex.MipMapLevels = 1;
        if (tex.VolumeDepth  < 1) tex.VolumeDepth  = 1;

        return tex;
    }

    // ── Mip chain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits the surface into individual mip levels.
    ///
    /// A texture resource stores every level back to back with no offset table, so the split
    /// has to be derived from the format's block layout, starting at the header's own
    /// Width/Height/MipMapLevels — the reference maps those three straight onto the payload.
    ///
    /// Cube maps (six faces) and volume textures (VolumeDepth slices) pack several surfaces
    /// into the same blob with no per-surface table, so they are handed back whole rather
    /// than mis-split into what would look like a mip chain.
    ///
    /// <see cref="HighResMipMapLevels"/> is deliberately NOT used to offset the walk. Nothing
    /// in the reference implementation consumes it — it declares the field and never reads it
    /// — so treating it as "top levels live elsewhere" would invent a layout the format does
    /// not have. A short payload is instead handled by the truncation guard below.
    /// </summary>
    public List<(int Width, int Height, byte[] Data)> SplitMips()
    {
        var mips = new List<(int Width, int Height, byte[] Data)>();
        if (Data.Length == 0) return mips;

        int w = Math.Max(1, Width), h = Math.Max(1, Height);

        if (IsCubeMap || VolumeDepth > 1)
        {
            mips.Add((w, h, Data));
            return mips;
        }

        int levels = MipMapLevels;
        int offset = 0;
        for (int i = 0; i < levels && offset < Data.Length; i++)
        {
            int size = SurfaceSize(Format, w, h);
            if (size <= 0) break;

            // A truncated chain still yields usable levels; emit what is present and stop.
            if (offset + size > Data.Length) size = Data.Length - offset;
            if (size <= 0) break;

            mips.Add((w, h, Data.AsSpan(offset, size).ToArray()));
            offset += size;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        if (mips.Count == 0)
            mips.Add((Math.Max(1, Width), Math.Max(1, Height), Data));

        return mips;
    }

    /// <summary>Byte count of a single mip level in the given DXGI format.</summary>
    private static int SurfaceSize(uint dxgi, int w, int h)
    {
        int blockBytes = dxgi switch
        {
            DXGI_BC1_UNORM or DXGI_BC1_UNORM_SRGB or DXGI_BC4_UNORM => 8,
            DXGI_BC2_UNORM or DXGI_BC2_UNORM_SRGB
                or DXGI_BC3_UNORM or DXGI_BC3_UNORM_SRGB
                or DXGI_BC5_UNORM or DXGI_BC6H_UF16
                or DXGI_BC7_UNORM or DXGI_BC7_UNORM_SRGB => 16,
            _ => 0,
        };

        if (blockBytes > 0)
            return ((w + 3) / 4) * ((h + 3) / 4) * blockBytes;

        int bitsPerPixel = dxgi switch
        {
            DXGI_R8_UNORM   => 8,
            DXGI_R8G8_UNORM => 16,
            DXGI_R8G8B8A8_UNORM or DXGI_R8G8B8A8_UNORM_SRGB
                or DXGI_B8G8R8A8_UNORM or DXGI_B8G8R8A8_UNORM_SRGB => 32,
            _ => 0,
        };

        return bitsPerPixel > 0 ? w * h * bitsPerPixel / 8 : 0;
    }

    private static uint MapSrgbToRegular(uint dxgi) => dxgi switch
    {
        DXGI_R8G8B8A8_UNORM_SRGB => DXGI_R8G8B8A8_UNORM,
        DXGI_BC1_UNORM_SRGB      => DXGI_BC1_UNORM,
        DXGI_BC2_UNORM_SRGB      => DXGI_BC2_UNORM,
        DXGI_BC3_UNORM_SRGB      => DXGI_BC3_UNORM,
        DXGI_BC7_UNORM_SRGB      => DXGI_BC7_UNORM,
        DXGI_B8G8R8A8_UNORM_SRGB => DXGI_B8G8R8A8_UNORM,
        _                        => dxgi,
    };
}
