using System.Numerics;
using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Animation;

/// <summary>
/// Linear-blend skinning on the CPU. The WPF viewport has no GPU skinning path, so posing a
/// mesh means rewriting its position buffer every frame; this is the hot loop that does it.
///
/// Buffers are the same flat float32 XYZ layout <see cref="LodData.VertexBuffer"/> uses
/// (12 bytes per vertex), so the result can be handed straight back to the viewer or an
/// exporter without a conversion pass.
/// </summary>
public static class CpuSkinner
{
    /// <summary>
    /// Skin <paramref name="restPositions"/> (float32 XYZ, 12 bytes/vertex) with
    /// <paramref name="palette"/> and write the result into <paramref name="dest"/>.
    /// <paramref name="dest"/> must be at least as long as the rest buffer; passing the same
    /// array as source and destination is not supported.
    ///
    /// Vertices with no usable influence (all-zero weight row, or every index out of range)
    /// are copied through unchanged so an incomplete skin table degrades to a static mesh
    /// instead of collapsing everything onto the origin.
    /// </summary>
    public static void SkinPositions(byte[] restPositions, SkinBinding skin,
                                     Matrix4x4[] palette, byte[] dest)
    {
        int vertCount = Math.Min(restPositions.Length / 12, skin.VertexCount);
        int inf = Math.Max(skin.InfluencesPerVertex, 1);

        for (int v = 0; v < vertCount; v++)
        {
            int o = v * 12;
            var rest = new Vector3(
                BitConverter.ToSingle(restPositions, o),
                BitConverter.ToSingle(restPositions, o + 4),
                BitConverter.ToSingle(restPositions, o + 8));

            var acc = Vector3.Zero;
            float total = 0f;
            int baseIdx = v * inf;

            for (int k = 0; k < inf; k++)
            {
                float w = skin.BoneWeights[baseIdx + k];
                if (w <= 0f) continue;
                int b = skin.BoneIndices[baseIdx + k];
                if (b >= palette.Length) continue;

                acc   += Vector3.Transform(rest, palette[b]) * w;
                total += w;
            }

            var final = total > 1e-6f ? acc / total : rest;

            BitConverter.TryWriteBytes(dest.AsSpan(o,     4), final.X);
            BitConverter.TryWriteBytes(dest.AsSpan(o + 4, 4), final.Y);
            BitConverter.TryWriteBytes(dest.AsSpan(o + 8, 4), final.Z);
        }

        // Vertices past the skin table (shouldn't happen, but a truncated table must not
        // leave stale geometry behind) pass through untouched.
        for (int o = vertCount * 12; o + 12 <= restPositions.Length && o + 12 <= dest.Length; o += 12)
            Buffer.BlockCopy(restPositions, o, dest, o, 12);
    }

    /// <summary>
    /// Same as <see cref="SkinPositions(byte[], SkinBinding, Matrix4x4[], byte[])"/> but for a
    /// single submesh's vertex range, which is how the viewer updates one
    /// <c>MeshGeometry3D</c> at a time.
    /// </summary>
    public static void SkinPositionRange(byte[] restPositions, SkinBinding skin,
                                         Matrix4x4[] palette,
                                         int vertexStart, int vertexCount,
                                         Vector3[] dest)
    {
        int inf = Math.Max(skin.InfluencesPerVertex, 1);

        for (int i = 0; i < vertexCount; i++)
        {
            int v = vertexStart + i;
            int o = v * 12;
            if (o + 12 > restPositions.Length) break;

            var rest = new Vector3(
                BitConverter.ToSingle(restPositions, o),
                BitConverter.ToSingle(restPositions, o + 4),
                BitConverter.ToSingle(restPositions, o + 8));

            if (v >= skin.VertexCount) { dest[i] = rest; continue; }

            var acc = Vector3.Zero;
            float total = 0f;
            int baseIdx = v * inf;

            for (int k = 0; k < inf; k++)
            {
                float w = skin.BoneWeights[baseIdx + k];
                if (w <= 0f) continue;
                int b = skin.BoneIndices[baseIdx + k];
                if (b >= palette.Length) continue;

                acc   += Vector3.Transform(rest, palette[b]) * w;
                total += w;
            }

            dest[i] = total > 1e-6f ? acc / total : rest;
        }
    }
}
