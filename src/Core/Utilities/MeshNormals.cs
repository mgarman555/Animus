using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Core.Utilities;

/// <summary>
/// Smooth (area-weighted) vertex normals from triangle geometry.
///
/// This is the one implementation. The viewer, the glTF exporter and the FBX exporter each
/// used to carry their own copy of the same accumulate-face-normals-and-normalise loop, which
/// is three places for the same bug to hide and three places to update when the animation path
/// needs normals re-derived after skinning.
///
/// The cross product is deliberately NOT normalised before accumulation: its magnitude is twice
/// the triangle's area, so summing raw face normals weights each contribution by area. A big
/// triangle should influence a shared vertex more than a sliver does.
/// </summary>
public static class MeshNormals
{
    /// <summary>
    /// Compute smooth normals for a position buffer (float32 XYZ, 12 bytes/vertex) indexed by
    /// <paramref name="indices"/>. Returns a parallel float32 XYZ buffer.
    ///
    /// Degenerate triangles contribute a zero-length cross product and so drop out on their
    /// own; a vertex touched only by degenerate triangles keeps a zero normal rather than a
    /// NaN one, which renderers treat as unlit instead of turning the surface black.
    /// </summary>
    public static byte[] ComputeSmooth(byte[] positions, int vertexCount, ReadOnlySpan<int> indices)
    {
        var accX = new double[vertexCount];
        var accY = new double[vertexCount];
        var accZ = new double[vertexCount];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if ((uint)i0 >= (uint)vertexCount ||
                (uint)i1 >= (uint)vertexCount ||
                (uint)i2 >= (uint)vertexCount) continue;

            int o0 = i0 * 12, o1 = i1 * 12, o2 = i2 * 12;
            if (o0 + 12 > positions.Length || o1 + 12 > positions.Length || o2 + 12 > positions.Length)
                continue;

            double p0x = BitConverter.ToSingle(positions, o0);
            double p0y = BitConverter.ToSingle(positions, o0 + 4);
            double p0z = BitConverter.ToSingle(positions, o0 + 8);

            double e1x = BitConverter.ToSingle(positions, o1)     - p0x;
            double e1y = BitConverter.ToSingle(positions, o1 + 4) - p0y;
            double e1z = BitConverter.ToSingle(positions, o1 + 8) - p0z;

            double e2x = BitConverter.ToSingle(positions, o2)     - p0x;
            double e2y = BitConverter.ToSingle(positions, o2 + 4) - p0y;
            double e2z = BitConverter.ToSingle(positions, o2 + 8) - p0z;

            double nx = e1y * e2z - e1z * e2y;
            double ny = e1z * e2x - e1x * e2z;
            double nz = e1x * e2y - e1y * e2x;

            accX[i0] += nx; accY[i0] += ny; accZ[i0] += nz;
            accX[i1] += nx; accY[i1] += ny; accZ[i1] += nz;
            accX[i2] += nx; accY[i2] += ny; accZ[i2] += nz;
        }

        var result = new byte[vertexCount * 12];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = accX[i], y = accY[i], z = accZ[i];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len > 1e-12) { x /= len; y /= len; z /= len; }
            else             { x = y = z = 0; }

            int o = i * 12;
            BitConverter.TryWriteBytes(result.AsSpan(o,     4), (float)x);
            BitConverter.TryWriteBytes(result.AsSpan(o + 4, 4), (float)y);
            BitConverter.TryWriteBytes(result.AsSpan(o + 8, 4), (float)z);
        }
        return result;
    }

    /// <summary>
    /// Compute (once) and cache this LOD's normals on <see cref="LodData.NormalBuffer"/>.
    /// Returns null when the LOD has no geometry to derive them from.
    ///
    /// Computing across the whole LOD rather than per submesh is safe because submesh index
    /// ranges are rebased into their own vertex range — no triangle spans two submeshes, so the
    /// result is identical to computing each separately, for one pass instead of N.
    /// </summary>
    public static byte[]? EnsureComputed(LodData lod)
    {
        if (lod.NormalBuffer != null) return lod.NormalBuffer;
        if (lod.VertexBuffer is not { } vb || lod.IndexBuffer is not { } ib) return null;

        int vertexCount = vb.Length / 12;
        if (vertexCount == 0) return null;

        var indices = new int[ib.Length / 4];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = BitConverter.ToInt32(ib, i * 4);

        lod.NormalBuffer = ComputeSmooth(vb, vertexCount, indices);
        return lod.NormalBuffer;
    }
}
