using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using GameAssetExplorer.Core.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Decodes a TextureAssetData into a WPF BitmapSource.
/// Handles already-decoded pixels and BC1–BC7 block-compressed mip data.
/// </summary>
internal static class TextureDecodeHelper
{
    public static BitmapSource? TryDecode(TextureAssetData tex)
    {
        try
        {
            byte[]? pixels = null;
            int w = tex.Width, h = tex.Height;

            if (tex.DecodedPixels != null)
            {
                pixels = tex.DecodedPixels;
            }
            else if (tex.Mips.Count > 0)
            {
                var mip = tex.Mips[0];
                w = mip.Width; h = mip.Height;
                pixels = DecodeBC(mip.Data, w, h, tex.SourceFormat);
            }

            if (pixels == null || w <= 0 || h <= 0) return null;

            var bmp = BitmapSource.Create(w, h,
                dpiX: 96, dpiY: 96,
                pixelFormat: PixelFormats.Bgra32,
                palette: null,
                pixels: SwapRB(pixels),
                stride: w * 4);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static byte[] SwapRB(byte[] rgba)
    {
        var bgra = (byte[])rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    private static byte[]? DecodeBC(byte[] data, int w, int h, string format)
    {
        // CUE4Parse reports EPixelFormat names like "PF_BC5" / "PF_DXT5"; strip the prefix.
        var norm = format.ToUpperInvariant();
        if (norm.StartsWith("PF_")) norm = norm[3..];

        var fmt = norm switch
        {
            "DXT1" or "BC1"            => CompressionFormat.Bc1,
            "DXT3" or "BC2"            => CompressionFormat.Bc2,
            "DXT5" or "BC3"            => CompressionFormat.Bc3,
            "BC4" or "ATI1N"           => CompressionFormat.Bc4,
            "BC5" or "ATI2N"           => CompressionFormat.Bc5,
            "BC6H"                     => CompressionFormat.Bc6S,
            "BC7"                      => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };
        if (fmt == CompressionFormat.Unknown) return null;

        var decoder = new BcDecoder();
        var colors  = decoder.DecodeRaw(data, w, h, fmt);
        var rgba    = new byte[w * h * 4];

        // BC5 stores only X/Y of a tangent-space normal; reconstruct Z into blue so normal
        // maps display as proper blue/purple (and are usable), exactly like FModel.
        bool isNormalBc5 = norm is "BC5" or "ATI2N";
        for (int i = 0; i < colors.Length; i++)
        {
            byte r = colors[i].r, g = colors[i].g;
            rgba[i * 4 + 0] = r;
            rgba[i * 4 + 1] = g;
            if (isNormalBc5)
            {
                float nx = r / 127.5f - 1f, ny = g / 127.5f - 1f;
                float nz = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                rgba[i * 4 + 2] = (byte)((nz * 0.5f + 0.5f) * 255f);
                rgba[i * 4 + 3] = 255;
            }
            else
            {
                rgba[i * 4 + 2] = colors[i].b;
                rgba[i * 4 + 3] = colors[i].a;
            }
        }
        return rgba;
    }
}
