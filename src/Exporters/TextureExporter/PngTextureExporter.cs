using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using SkiaSharp;

namespace GameAssetExplorer.Exporters.TextureExporter;

/// <summary>
/// Exports UE textures to PNG.
///
/// The main challenge here: Unreal stores textures in block-compressed formats
/// (BC1/DXT1, BC3/DXT5, BC5, BC7) that aren't natively readable by Blender or most tools.
/// We decode to raw RGBA first, then encode to PNG.
///
/// Why PNG and not DDS? DDS files containing BC7 compressed data aren't supported by
/// Unreal Engine's built-in texture importer — it only accepts uncompressed or specific
/// DDS formats. PNG is universally supported and survives the Blender->Unreal pipeline.
/// </summary>
public class PngTextureExporter : IExporter
{
    public string ExporterName => "PNG Texture Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.Texture };
    public IReadOnlyList<string> OutputExtensions => new[] { ".png" };

    public async Task<ExportResult> ExportAsync(
        AssetData assetData,
        string outputDirectory,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        var startTime = DateTime.UtcNow;

        if (assetData is not TextureAssetData texture)
            return Fail(assetData.Info, "Asset is not a texture.");

        try
        {
            var outputPath = BuildOutputPath(texture.Info, outputDirectory, settings, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!settings.OverwriteExisting && File.Exists(outputPath))
            {
                return new ExportResult
                {
                    Success = true,
                    OutputPath = outputPath,
                    SourceAsset = assetData.Info,
                    FileSizeBytes = new FileInfo(outputPath).Length,
                    Duration = DateTime.UtcNow - startTime
                };
            }

            // Get the mip we want (0 = full res)
            var mipIndex = settings.TextureMipLevel < 0 ? 0 : settings.TextureMipLevel;
            mipIndex = Math.Min(mipIndex, texture.Mips.Count - 1);

            byte[] rgbaPixels;

            if (texture.DecodedPixels != null)
            {
                // Already decoded — use directly
                rgbaPixels = texture.DecodedPixels;
            }
            else if (texture.Mips.Count > mipIndex)
            {
                var mip = texture.Mips[mipIndex];
                rgbaPixels = await DecodeTextureAsync(
                    mip.Data,
                    mip.Width,
                    mip.Height,
                    texture.SourceFormat);
            }
            else
            {
                return Fail(texture.Info, "No pixel data available to export.");
            }

            // Write PNG using SkiaSharp
            await Task.Run(() =>
            {
                var width = texture.Mips.Count > mipIndex ? texture.Mips[mipIndex].Width : texture.Width;
                var height = texture.Mips.Count > mipIndex ? texture.Mips[mipIndex].Height : texture.Height;

                using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                System.Runtime.InteropServices.Marshal.Copy(rgbaPixels, 0, bitmap.GetPixels(), rgbaPixels.Length);

                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(outputPath);
                encoded.SaveTo(stream);
            });

            var fileInfo = new FileInfo(outputPath);
            return new ExportResult
            {
                Success = true,
                OutputPath = outputPath,
                SourceAsset = assetData.Info,
                FileSizeBytes = fileInfo.Length,
                Duration = DateTime.UtcNow - startTime
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
        var total = assets.Count;

        for (int i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ExportProgress
            {
                Total = total,
                Completed = i,
                CurrentAsset = assets[i].Info.Name
            });

            var result = await ExportAsync(assets[i], outputDirectory, settings);
            results.Add(result);
        }

        return results;
    }

    // ─── Texture Decoding ─────────────────────────────────────────────────────

    private static async Task<byte[]> DecodeTextureAsync(byte[] data, int width, int height, string format)
    {
        return await Task.Run(() => DecodeTexture(data, width, height, format));
    }

    private static byte[] DecodeTexture(byte[] data, int width, int height, string format)
    {
        var compressionFormat = MapToCompressionFormat(format);

        if (compressionFormat == CompressionFormat.Unknown)
        {
            // If we don't recognize the format, return as-is (might already be RGBA)
            return data;
        }

        // BCnEncoder.NET handles all the block-compressed formats UE uses
        var decoder = new BcDecoder();
        var rawColors = decoder.DecodeRaw(data, width, height, compressionFormat);

        // Convert ColorRgba32 array to raw byte array (RGBA order for SkiaSharp)
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rawColors.Length; i++)
        {
            rgba[i * 4 + 0] = rawColors[i].r;
            rgba[i * 4 + 1] = rawColors[i].g;
            rgba[i * 4 + 2] = rawColors[i].b;
            rgba[i * 4 + 3] = rawColors[i].a;
        }
        return rgba;
    }

    private static CompressionFormat MapToCompressionFormat(string ueFormat) => ueFormat.ToUpperInvariant() switch
    {
        "DXT1" or "BC1" => CompressionFormat.Bc1,
        "DXT3" or "BC2" => CompressionFormat.Bc2,
        "DXT5" or "BC3" => CompressionFormat.Bc3,
        "BC4" or "ATI1N" => CompressionFormat.Bc4,
        "BC5" or "ATI2N" => CompressionFormat.Bc5,
        "BC6H" => CompressionFormat.Bc6S,
        "BC7" => CompressionFormat.Bc7,
        "ATF_RGB_DXT1" => CompressionFormat.Bc1,
        "ATF_RGBA_DXT5" => CompressionFormat.Bc3,
        _ => CompressionFormat.Unknown
    };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildOutputPath(AssetInfo info, string outputDir, ExportSettings settings, string ext)
    {
        if (settings.PreserveVirtualPaths)
        {
            // Strip the leading /Game/ and use the rest as the folder structure
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
        Success = false,
        ErrorMessage = error,
        SourceAsset = info
    };
}
