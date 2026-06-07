using System.Numerics;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Applies <c>m_papTransform</c> per-submesh world matrices to a legacy-parsed
/// <see cref="MeshAssetData"/>. This is a non-destructive post-process: it walks the
/// pak's m_papTransform table (using <see cref="NdPakReader"/> for fixup resolution),
/// matches matrices to submesh boundaries by name, and rotates+translates the relevant
/// vertex ranges in-place.
///
/// Why we need this: TLOU2 character paks pack many shapes (head/body/clothing/etc.)
/// into one pak, and each shape's vertex coordinates are stored in its own *local* space.
/// Without applying the per-submesh transform, the merged mesh appears split into
/// disjoint islands. <c>fmt_nd_pak.py</c>'s <c>movePositionsBuffer</c> applies these
/// during render — we apply them here once during load.
/// </summary>
public static class NdTransformApplier
{
    /// <summary>Returns the number of submesh ranges that got a matrix applied.</summary>
    public static int ApplyTo(NdPakReader reader, MeshAssetData mesh, string label)
    {
        if (reader.GeoEntry == null || !reader.IsTLOU2) return 0;
        if (mesh.Lods.Count == 0) return 0;
        if (mesh.Lods.Sum(l => l.Submeshes.Count) == 0) return 0;

        var data = reader.Data;
        var geo  = reader.GeoEntry.Value;

        // ── Geometry header ─────────────────────────────────────────────────
        int ghOff = geo.PageStart + geo.ResItemOffset + reader.ResItemPaddingSz;
        if (ghOff + 80 > data.Length) return 0;

        int numSubmesh   = (int)BitConverter.ToUInt32(data, ghOff + 8);
        int numMaterials = (int)BitConverter.ToUInt32(data, ghOff + 16);
        if (numSubmesh <= 0 || numSubmesh > 1024) return 0;

        var submeshesAbsPtr = reader.ReadPointerFixup(ghOff + 40);
        if (submeshesAbsPtr is null) return 0;
        int submeshesAbs = (int)submeshesAbsPtr.Value;

        var papPtr = reader.ReadPointerFixup(ghOff + 56);
        if (papPtr is null || papPtr.Value <= 0)
        {
            Log.Info($"NdTransformApplier[{label}]: no m_papTransform pointer in geometry header");
            return 0;
        }
        int papBase = (int)papPtr.Value;

        // ── Walk SubMeshDesc table once to build name → AbsOffset map ───────
        // Names match the legacy parser's SubmeshInfo.Name (short tail after final '|').
        var smdAbsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var smdAbsOffsetsSet = new HashSet<int>();

        for (int i = 0; i < numSubmesh; i++)
        {
            int sd = submeshesAbs + 176 * i;
            if (sd + 176 > data.Length) continue;

            var namePtr = reader.ReadPointerFixup(sd + 32);
            string fullName = namePtr.HasValue && namePtr.Value > 0
                ? ReadString(data, (int)namePtr.Value)
                : "";
            int pipe = fullName.LastIndexOf('|');
            string shortName = pipe >= 0 ? fullName[(pipe + 1)..] : fullName;

            // First name wins (matches legacy behaviour for duplicates)
            if (!smdAbsByName.ContainsKey(shortName))
                smdAbsByName[shortName] = sd;
            smdAbsOffsetsSet.Add(sd);
        }

        // ── Probe m_papTransform inner layout ───────────────────────────────
        // fmt_nd_pak docs: xformPtr@+152, count@+212. Real PC layout may have a +8 shift
        // (consistent with the SubMeshDesc shift we already discovered). Try both and
        // accept whichever produces submesh pointers that match real SMD addresses.
        var candidateLayouts = new[]
        {
            (xformOff: 152, countOff: 212, name: "documented"),
            (xformOff: 160, countOff: 220, name: "shifted+8"),
        };

        Dictionary<int, Matrix4x4> chosen = new();
        string chosenLayoutName = "";

        foreach (var layout in candidateLayouts)
        {
            var found = new Dictionary<int, Matrix4x4>();
            int total = 0, valid = 0;

            for (int m = 0; m < numMaterials; m++)
            {
                var matStructPtr = reader.ReadPointerFixup(papBase + 8 * m);
                if (matStructPtr is null || matStructPtr.Value <= 0) continue;
                int matAt = (int)matStructPtr.Value;
                if (matAt + layout.countOff + 4 > data.Length) continue;

                var mat = ReadMat44(data, matAt);

                var xformsPtr = reader.ReadPointerFixup(matAt + layout.xformOff);
                if (xformsPtr is null || xformsPtr.Value <= 0) continue;
                int xformsAt = (int)xformsPtr.Value;
                int numXforms = (int)BitConverter.ToUInt32(data, matAt + layout.countOff);
                if (numXforms < 0 || numXforms > 4096) continue;

                for (int j = 0; j < numXforms; j++)
                {
                    int entryAddr = xformsAt + j * 112 + 64;
                    if (entryAddr + 8 > data.Length) break;

                    var smPtr = reader.ReadPointerFixup(entryAddr, zeroIsValid: true);
                    if (smPtr is null) continue;
                    int absOffs = (int)smPtr.Value;
                    if (absOffs == 0) continue;       // 0-pointer = orphan entry

                    total++;
                    if (smdAbsOffsetsSet.Contains(absOffs)) valid++;

                    if (!found.ContainsKey(absOffs)) found[absOffs] = mat;
                }
            }

            if (total > 0 && valid * 2 >= total)
            {
                chosen = found;
                chosenLayoutName = layout.name;
                Log.Info($"NdTransformApplier[{label}]: papTransform layout '{layout.name}' accepted ({valid}/{total} valid)");
                break;
            }
            else
            {
                Log.Info($"NdTransformApplier[{label}]: papTransform layout '{layout.name}' rejected ({valid}/{total} valid)");
            }
        }

        if (chosen.Count == 0)
        {
            Log.Info($"NdTransformApplier[{label}]: no usable m_papTransform matrices found");
            return 0;
        }

        // ── Apply matrices to legacy mesh's vertex buffers ──────────────────
        int appliedCount = 0;
        foreach (var lod in mesh.Lods)
        {
            if (lod.VertexBuffer == null || lod.Submeshes.Count == 0) continue;

            foreach (var sub in lod.Submeshes)
            {
                if (!smdAbsByName.TryGetValue(sub.Name, out int absOffs)) continue;
                if (!chosen.TryGetValue(absOffs, out var mat)) continue;

                ApplyMatrixToRange(lod.VertexBuffer, sub.VertexStart, sub.VertexCount, mat);
                appliedCount++;
            }
        }

        return appliedCount;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ApplyMatrixToRange(byte[] vb, int vStart, int vCount, Matrix4x4 mat)
    {
        // Decompose: rotation as quaternion (per fmt_nd_pak.movePositionsBuffer); translation
        // from the matrix's 4th row (System.Numerics row-major M41/M42/M43).
        var rotation    = Quaternion.CreateFromRotationMatrix(mat);
        var translation = new Vector3(mat.M41, mat.M42, mat.M43);

        for (int i = 0; i < vCount; i++)
        {
            int o = (vStart + i) * 12;
            if (o + 12 > vb.Length) break;

            var p = new Vector3(
                BitConverter.ToSingle(vb, o),
                BitConverter.ToSingle(vb, o + 4),
                BitConverter.ToSingle(vb, o + 8));

            var t = Vector3.Transform(p, rotation) + translation;

            BitConverter.TryWriteBytes(vb.AsSpan(o,     4), t.X);
            BitConverter.TryWriteBytes(vb.AsSpan(o + 4, 4), t.Y);
            BitConverter.TryWriteBytes(vb.AsSpan(o + 8, 4), t.Z);
        }
    }

    private static Matrix4x4 ReadMat44(byte[] d, int o)
    {
        return new Matrix4x4(
            BitConverter.ToSingle(d, o +  0), BitConverter.ToSingle(d, o +  4),
            BitConverter.ToSingle(d, o +  8), BitConverter.ToSingle(d, o + 12),
            BitConverter.ToSingle(d, o + 16), BitConverter.ToSingle(d, o + 20),
            BitConverter.ToSingle(d, o + 24), BitConverter.ToSingle(d, o + 28),
            BitConverter.ToSingle(d, o + 32), BitConverter.ToSingle(d, o + 36),
            BitConverter.ToSingle(d, o + 40), BitConverter.ToSingle(d, o + 44),
            BitConverter.ToSingle(d, o + 48), BitConverter.ToSingle(d, o + 52),
            BitConverter.ToSingle(d, o + 56), BitConverter.ToSingle(d, o + 60));
    }

    private static string ReadString(byte[] d, int o)
    {
        if (o < 0 || o >= d.Length) return string.Empty;
        int end = o;
        while (end < d.Length && d[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(d, o, end - o);
    }
}
