using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Exporters.TextureExporter;

/// <summary>
/// Exports textures as .dds, keeping the original block-compressed surface untouched.
///
/// This complements <see cref="PngTextureExporter"/> rather than replacing it. PNG is the
/// right default for the Blender→UE pipeline, but decoding to PNG only works for formats
/// BCnEncoder understands — R8, R8G8 and BC6H all fall out of that path, and every export
/// discards the mip chain and re-encodes the pixels. Writing DDS is lossless: the bytes that
/// shipped in the game are the bytes on disk, mips included.
///
/// Formats with a legacy FourCC (BC1/BC2/BC3) use the classic 128-byte header so older tools
/// can read them. Everything else gets the DX10 extended header carrying the DXGI format
/// directly. sRGB variants are written as their linear equivalents because Blender rejects
/// the _SRGB DXGI values.
/// </summary>
public class DdsTextureExporter : IExporter
{
    public string ExporterName => "DDS Texture Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.Texture };
    public IReadOnlyList<string> OutputExtensions => new[] { ".dds" };

    private const uint DDS_MAGIC = 0x20534444;   // "DDS "

    private const uint DDS_HEADER_FLAGS_TEXTURE  = 0x00001007;
    private const uint DDS_HEADER_FLAGS_MIPMAP   = 0x00020000;
    private const uint DDS_HEADER_FLAGS_PITCH      = 0x00000008;
    private const uint DDS_HEADER_FLAGS_LINEARSIZE = 0x00080000;
    private const uint DDS_HEADER_FLAGS_VOLUME   = 0x00800000;
    private const uint DDS_SURFACE_FLAGS_TEXTURE = 0x00001000;
    private const uint DDS_SURFACE_FLAGS_MIPMAP  = 0x00400008;
    private const uint DDS_SURFACE_FLAGS_CUBEMAP = 0x00000008;
    private const uint DDS_CUBEMAP_ALLFACES      = 0x0000FE00;
    private const uint DDS_RESOURCE_MISC_TEXTURECUBE = 0x00000004;
    private const uint DDS_CAPS2_VOLUME          = 0x00200000;

    private const uint DDS_FOURCC = 0x00000004;
    private const uint DDS_RGB    = 0x00000040;
    private const uint DDS_RGBA   = 0x00000041;

    private const uint DDS_DIMENSION_TEXTURE2D = 3;
    private const uint DDS_DIMENSION_TEXTURE3D = 4;

    public async Task<ExportResult> ExportAsync(
        AssetData assetData,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        var startTime = DateTime.UtcNow;

        if (assetData is not TextureAssetData texture)
            return Fail(assetData.Info, "Asset is not a texture.");

        if (texture.Mips.Count == 0 || texture.Mips[0].Data.Length == 0)
            return Fail(assetData.Info, "Texture has no surface data.");

        try
        {
            var outputPath = BuildOutputPath(texture.Info, outputDirectory, settings, ".dds");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!settings.OverwriteExisting && File.Exists(outputPath))
            {
                return new ExportResult
                {
                    Success       = true,
                    OutputPath    = outputPath,
                    SourceAsset   = assetData.Info,
                    FileSizeBytes = new FileInfo(outputPath).Length,
                    Duration      = DateTime.UtcNow - startTime,
                };
            }

            byte[] dds = BuildDds(texture);
            await File.WriteAllBytesAsync(outputPath, dds);

            return new ExportResult
            {
                Success       = true,
                OutputPath    = outputPath,
                SourceAsset   = assetData.Info,
                FileSizeBytes = dds.Length,
                Duration      = DateTime.UtcNow - startTime,
            };
        }
        catch (Exception ex)
        {
            return Fail(assetData.Info, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();

        for (int i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ExportProgress
            {
                Total        = assets.Count,
                Completed    = i,
                CurrentAsset = assets[i].Info.Name,
            });

            results.Add(await ExportAsync(assets[i], outputDirectory, settings));
        }

        return results;
    }

    // ─── DDS construction ─────────────────────────────────────────────────────

    private static byte[] BuildDds(TextureAssetData texture)
    {
        GetPixelFormat(texture, out uint pfFlags, out uint fourCc, out uint bitCount,
                       out uint rMask, out uint gMask, out uint bMask, out uint aMask,
                       out uint dxgiFormat, out bool blockCompressed);

        bool useDx10  = fourCc == MakeFourCc("DX10");
        bool cube     = texture.IsCubeMap;
        int  depth    = Math.Max(1, texture.Depth);
        bool volume   = depth > 1;
        int  mipCount = texture.Mips.Count;
        int  surfaceBytes = texture.Mips.Sum(m => m.Data.Length);

        // A cube map's six faces share one blob, so the top-level size is a sixth of it.
        uint topLevel = (uint)texture.Mips[0].Data.Length;
        if (cube) topLevel /= 6;

        using var ms = new MemoryStream(148 + surfaceBytes);
        using var bw = new BinaryWriter(ms);

        bw.Write(DDS_MAGIC);

        bw.Write(124u);                                                  // dwSize
        bw.Write(DDS_HEADER_FLAGS_TEXTURE
                 | (mipCount > 1 ? DDS_HEADER_FLAGS_MIPMAP : 0)
                 | (volume ? DDS_HEADER_FLAGS_VOLUME : 0)
                 // Block-compressed surfaces declare a linear size; uncompressed ones a row pitch.
                 | (blockCompressed ? DDS_HEADER_FLAGS_LINEARSIZE : DDS_HEADER_FLAGS_PITCH));
        bw.Write((uint)texture.Height);
        bw.Write((uint)texture.Width);
        bw.Write(blockCompressed
                     ? topLevel
                     : (uint)(((long)texture.Width * bitCount + 7) / 8));  // pitchOrLinearSize
        bw.Write((uint)depth);
        bw.Write((uint)mipCount);
        for (int i = 0; i < 11; i++) bw.Write(0u);                       // reserved1

        bw.Write(32u);                                                   // DDS_PIXELFORMAT.dwSize
        bw.Write(pfFlags);
        bw.Write(fourCc);
        bw.Write(bitCount);
        bw.Write(rMask);
        bw.Write(gMask);
        bw.Write(bMask);
        bw.Write(aMask);

        bw.Write(DDS_SURFACE_FLAGS_TEXTURE
                 | (mipCount > 1 ? DDS_SURFACE_FLAGS_MIPMAP : 0)
                 | (cube ? DDS_SURFACE_FLAGS_CUBEMAP : 0));              // caps
        bw.Write((cube ? DDS_CUBEMAP_ALLFACES : 0u)
                 | (volume ? DDS_CAPS2_VOLUME : 0u));                    // caps2
        bw.Write(0u);                                                    // caps3
        bw.Write(0u);                                                    // caps4
        bw.Write(0u);                                                    // reserved2

        if (useDx10)
        {
            bw.Write(dxgiFormat);
            bw.Write(volume ? DDS_DIMENSION_TEXTURE3D : DDS_DIMENSION_TEXTURE2D);
            bw.Write(cube ? DDS_RESOURCE_MISC_TEXTURECUBE : 0u);         // miscFlag
            bw.Write(1u);                                                // arraySize
            bw.Write(0u);                                                // miscFlags2
        }

        foreach (var mip in texture.Mips)
            bw.Write(mip.Data);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Resolves the DDS pixel-format block for a texture.
    ///
    /// The numeric <see cref="TextureAssetData.SourceDxgiFormat"/> is authoritative when the
    /// plugin supplied one; <see cref="TextureAssetData.SourceFormat"/> is a display string
    /// whose spelling differs between engines, so it is only a fallback. An unrecognised
    /// format throws rather than guessing: silently stamping a BC7 header onto, say, a 32-bit
    /// float surface produces a file that opens and decodes to noise, which is far worse than
    /// a failed export.
    /// </summary>
    private static void GetPixelFormat(TextureAssetData texture,
                                       out uint flags, out uint fourCc, out uint bitCount,
                                       out uint rMask, out uint gMask, out uint bMask, out uint aMask,
                                       out uint dxgiFormat, out bool blockCompressed)
    {
        flags = fourCc = bitCount = rMask = gMask = bMask = aMask = 0;
        dxgiFormat = 0;

        string name = Normalize(texture.SourceFormat);

        // Legacy FourCC formats first — older tools read these without a DX10 header.
        switch (name)
        {
            case "DXT1" or "BC1":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT1"); blockCompressed = true;
                return;

            case "DXT3" or "BC2":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT3"); blockCompressed = true;
                return;

            case "DXT5" or "BC3":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT5"); blockCompressed = true;
                return;

            case "R8" or "G8" or "R8_UNORM":
                flags = DDS_RGB; bitCount = 8; rMask = 0xFF;
                blockCompressed = false;
                return;

            case "BGRA8" or "B8G8R8A8":
                flags = DDS_RGBA; bitCount = 32;
                bMask = 0x000000FF; gMask = 0x0000FF00; rMask = 0x00FF0000; aMask = 0xFF000000;
                blockCompressed = false;
                return;

            case "RGBA8" or "R8G8B8A8" or "RGBA" or "A8R8G8B8":
                flags = DDS_RGBA; bitCount = 32;
                rMask = 0x000000FF; gMask = 0x0000FF00; bMask = 0x00FF0000; aMask = 0xFF000000;
                blockCompressed = false;
                return;
        }

        // Everything else goes out as DX10 carrying the numeric DXGI value.
        uint dxgi = name switch
        {
            "BC4" or "ATI1N" or "BC4U" => 80,
            "BC5" or "ATI2N" or "BC5U" => 83,
            "BC6H"                     => 95,
            "BC7"                      => 98,
            "R8G8"                     => 49,
            _                          => 0,
        };

        // Prefer whatever the plugin actually read out of the file over a display string.
        if (dxgi == 0 && texture.SourceDxgiFormat is uint fromPlugin && fromPlugin != 0)
            dxgi = fromPlugin;

        // "DXGI:123" is a reader's fallback spelling for a format with no short name.
        if (dxgi == 0 && name.StartsWith("DXGI:", StringComparison.Ordinal))
            uint.TryParse(name.AsSpan(5), out dxgi);

        if (dxgi == 0)
            throw new NotSupportedException(
                $"No DDS mapping for texture format '{texture.SourceFormat}'.");

        dxgiFormat      = MapSrgbToLinear(dxgi);
        flags           = DDS_FOURCC;
        fourCc          = MakeFourCc("DX10");
        blockCompressed = IsBlockCompressed(dxgiFormat);
    }

    private static bool IsBlockCompressed(uint dxgi)
        => dxgi is >= 70 and <= 84 or >= 94 and <= 99;

    /// <summary>Blender and most importers reject the _SRGB DXGI values.</summary>
    private static uint MapSrgbToLinear(uint dxgi) => dxgi switch
    {
        29 => 28, 72 => 71, 75 => 74, 78 => 77, 91 => 87, 99 => 98,
        _  => dxgi,
    };

    /// <summary>CUE4Parse reports EPixelFormat names like "PF_BC5"; strip the prefix for matching.</summary>
    private static string Normalize(string format)
    {
        var f = (format ?? "").ToUpperInvariant();
        return f.StartsWith("PF_") ? f[3..] : f;
    }

    private static uint MakeFourCc(string s)
        => s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildOutputPath(AssetInfo info, string outputDir, ExportSettings settings, string ext)
    {
        if (settings.PreserveVirtualPaths)
        {
            var virtualPath = info.VirtualPath.TrimStart('/');
            if (virtualPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                virtualPath = virtualPath.Substring(5);

            var relativeDir = Path.GetDirectoryName(virtualPath) ?? "";
            return Path.Combine(outputDir, relativeDir, info.Name + ext);
        }

        return Path.Combine(outputDir, info.Name + ext);
    }

    private static ExportResult Fail(AssetInfo info, string error) => new()
    {
        Success      = false,
        ErrorMessage = error,
        SourceAsset  = info,
    };
}
