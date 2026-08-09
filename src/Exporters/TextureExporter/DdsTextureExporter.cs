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
    private const uint DDS_HEADER_FLAGS_LINEARSIZE = 0x00080000;
    private const uint DDS_SURFACE_FLAGS_TEXTURE = 0x00001000;
    private const uint DDS_SURFACE_FLAGS_MIPMAP  = 0x00400008;

    private const uint DDS_FOURCC = 0x00000004;
    private const uint DDS_RGB    = 0x00000040;
    private const uint DDS_RGBA   = 0x00000041;

    private const uint DDS_DIMENSION_TEXTURE2D = 3;

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
        GetPixelFormat(texture.SourceFormat, out uint pfFlags, out uint fourCc, out uint bitCount,
                       out uint rMask, out uint gMask, out uint bMask, out uint aMask,
                       out uint dxgiFormat);

        bool useDx10 = fourCc == MakeFourCc("DX10");
        int  mipCount = texture.Mips.Count;
        int  surfaceBytes = texture.Mips.Sum(m => m.Data.Length);

        using var ms = new MemoryStream(148 + surfaceBytes);
        using var bw = new BinaryWriter(ms);

        bw.Write(DDS_MAGIC);

        bw.Write(124u);                                                  // dwSize
        bw.Write(DDS_HEADER_FLAGS_TEXTURE
                 | (mipCount > 1 ? DDS_HEADER_FLAGS_MIPMAP : 0)
                 | DDS_HEADER_FLAGS_LINEARSIZE);
        bw.Write((uint)texture.Height);
        bw.Write((uint)texture.Width);
        bw.Write((uint)texture.Mips[0].Data.Length);                     // pitchOrLinearSize
        bw.Write(1u);                                                    // depth
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

        bw.Write(DDS_SURFACE_FLAGS_TEXTURE | (mipCount > 1 ? DDS_SURFACE_FLAGS_MIPMAP : 0));
        bw.Write(0u);                                                    // caps2
        bw.Write(0u);                                                    // caps3
        bw.Write(0u);                                                    // caps4
        bw.Write(0u);                                                    // reserved2

        if (useDx10)
        {
            bw.Write(dxgiFormat);
            bw.Write(DDS_DIMENSION_TEXTURE2D);
            bw.Write(0u);                                                // miscFlag
            bw.Write(1u);                                                // arraySize
            bw.Write(0u);                                                // miscFlags2
        }

        foreach (var mip in texture.Mips)
            bw.Write(mip.Data);

        bw.Flush();
        return ms.ToArray();
    }

    private static void GetPixelFormat(string format,
                                       out uint flags, out uint fourCc, out uint bitCount,
                                       out uint rMask, out uint gMask, out uint bMask, out uint aMask,
                                       out uint dxgiFormat)
    {
        flags = fourCc = bitCount = rMask = gMask = bMask = aMask = 0;
        dxgiFormat = 0;

        switch (Normalize(format))
        {
            case "DXT1" or "BC1":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT1");
                return;

            case "DXT3" or "BC2":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT3");
                return;

            case "DXT5" or "BC3":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DXT5");
                return;

            case "BC4" or "ATI1N":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 80;
                return;

            case "BC5" or "ATI2N":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 83;
                return;

            case "BC6H":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 95;
                return;

            case "BC7":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 98;
                return;

            case "R8":
                flags = DDS_RGB; bitCount = 8; rMask = 0xFF;
                return;

            case "R8G8":
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 49;
                return;

            case "BGRA8" or "B8G8R8A8":
                flags = DDS_RGBA; bitCount = 32;
                bMask = 0x000000FF; gMask = 0x0000FF00; rMask = 0x00FF0000; aMask = 0xFF000000;
                return;

            case "RGBA8" or "R8G8B8A8":
                flags = DDS_RGBA; bitCount = 32;
                rMask = 0x000000FF; gMask = 0x0000FF00; bMask = 0x00FF0000; aMask = 0xFF000000;
                return;

            default:
                // Unknown name: fall back to BC7, the most common modern surface.
                flags = DDS_FOURCC; fourCc = MakeFourCc("DX10"); dxgiFormat = 98;
                return;
        }
    }

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
