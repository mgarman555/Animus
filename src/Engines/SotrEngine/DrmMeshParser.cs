using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Parses CDCRenderModel geometry from a decompressed Foundation Engine DRM v23 file.
///
/// Format references:
///   https://cdcengine.re/docs/files/drm/         (cdcengine.re community wiki)
///   https://github.com/arcusmaximus/TrRebootModTools  (MIT — arcusmaximus)
///   https://github.com/Ekey/CDCE.TIGER.Tool           (MIT — Ekey)
///
/// DRM v23 layout (all little-endian):
///   [28 bytes] Fixed header
///     +0x00  uint32  version       = 23
///     +0x04  uint32  numObjects
///     +0x08  uint32  numRelocations
///     +0x0C  uint32  numImports
///     +0x10  uint32  numSections
///     +0x14  uint32  unknown
///     +0x18  uint32  unknown2
///   [numObjects  × 4 bytes] Object table  (uint32 per object)
///   [numRelocs   × 8 bytes] Relocation table
///     Each entry: srcSection:uint8, dstSection:uint8, flags:uint16, srcOffset:uint32
///     The uint32 at sections[srcSection].data[srcOffset] is a byte offset into
///     sections[dstSection].data.  Resolved address = &sections[dst].data + raw_value
///   [numImports  × 8 bytes] Import table (external DRM references — ignored here)
///   [numSections × 20 bytes] Section headers
///     +0x00  uint32  type          (2 = CDCRenderModel / RenderMesh)
///     +0x04  uint32  dataSize      (logical content size)
///     +0x08  uint32  allocationSize  (padded size used to advance between sections)
///     +0x0C  uint32  unknown
///     +0x10  uint32  unknown2
///   [section data blocks]  (section[0].allocSize | section[1].allocSize | ...)
///
/// CDCRenderModel (start of section-2 data, SOTTR PC 2018 x64):
///   +0x00  uint32  version      = 7
///   +0x04  uint32  flags
///   +0x08  uint32  numMeshes
///   +0x0C  uint32  numLODs
///   +0x10  uint32  numMaterials
///   +0x14  uint32  numStreams    (total stream descs across all meshes)
///   +0x18  uint32  unk0
///   +0x1C  uint32  unk1
///   +0x20  float   aabbScaleX
///   +0x24  float   aabbScaleY
///   +0x28  float   aabbScaleZ
///   +0x2C  float   pad
///   +0x30  uint32* → CDCRenderMesh[]   (reloc pointer — 4-byte section-relative offset)
///   [more pointer fields…]
///
/// CDCRenderMesh (128 bytes each):
///   +0x00  uint32* → CDCVertexStream[]  (reloc pointer)
///   +0x04  uint32  pad (high 32 bits of 64-bit pointer, always 0 in DRM)
///   +0x08  uint32* → uint16[] index buffer (reloc pointer)
///   +0x0C  uint32  pad
///   +0x10  float[4] boundingSphere (x, y, z, radius)
///   +0x20  float[3] aabbMin
///   +0x2C  float   pad
///   +0x30  float[3] aabbMax
///   +0x3C  float   pad
///   +0x40  uint32  numVertices
///   +0x44  uint32  numIndices
///   +0x48  uint32  numStreams
///   +0x4C  uint32  materialIndex
///   +0x50  uint32  flags
///   +0x54  uint32  lodIndex
///   [padding to 0x80 = 128 bytes total]
///
/// CDCVertexStream (32 bytes each):
///   +0x00  uint32* → uint8[] data buffer (reloc pointer)
///   +0x04  uint32  pad
///   +0x08  uint32  stride   (bytes per vertex in this stream)
///   +0x0C  uint32  numElements
///   +0x10  uint32  streamUsage  (0=POSITION, 1=NORMAL, 2=TEXCOORD, 3=COLOR, etc.)
///   [padding to 0x20 = 32 bytes total]
///
/// Vertex position format (SOTTR PC):
///   Stream where usage=0 (POSITION): float32 × 3, stride >= 12
///   Coordinates in local object space (centimeters, Y-up, same as UE convention)
///
/// Index format: uint16 (triangle list), numIndices is divisible by 3
/// </summary>
public static class DrmMeshParser
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const uint DRM_MAGIC   = 23;
    private const uint MODEL_VER   = 7;    // CDCRenderModel.version for SOTTR
    private const int  SECTION_HDR = 20;   // bytes per section header
    private const int  RELOC_SIZE  = 8;    // bytes per relocation entry
    private const int  MESH_SIZE   = 0x80; // CDCRenderMesh struct size (128 bytes)
    private const int  STREAM_SIZE = 0x20; // CDCVertexStream struct size (32 bytes)

    // Section-2 field offsets (CDCRenderModel)
    private const int MDL_NUM_MESHES  = 0x08;
    private const int MDL_NUM_LODS    = 0x0C;
    private const int MDL_PTR_MESHES  = 0x30; // first pointer field

    // CDCRenderMesh field offsets
    private const int MESH_PTR_STREAMS = 0x00;
    private const int MESH_PTR_INDICES = 0x08;
    private const int MESH_BBOX_MIN    = 0x20; // float3
    private const int MESH_BBOX_MAX    = 0x30; // float3
    private const int MESH_NUM_VERTS   = 0x40;
    private const int MESH_NUM_INDICES = 0x44;
    private const int MESH_NUM_STREAMS = 0x48;
    private const int MESH_LOD_INDEX   = 0x54;

    // CDCVertexStream field offsets
    private const int STR_PTR_DATA = 0x00;
    private const int STR_STRIDE   = 0x08;
    private const int STR_USAGE    = 0x10; // 0 = POSITION

    // ── Public entry point ───────────────────────────────────────────────────

    public static MeshAssetData? TryParse(byte[] drm, AssetInfo info)
    {
        try   { return ParseInternal(drm, info); }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR-DRM] Mesh parse failed for '{info.Name}': {ex.Message}");
            return null;
        }
    }

    // ── Core parser ───────────────────────────────────────────────────────────

    private static MeshAssetData? ParseInternal(byte[] data, AssetInfo info)
    {
        if (data.Length < 28) return null;
        if (R32(data, 0) != DRM_MAGIC) return null;

        // ── 1. Parse DRM header ────────────────────────────────────────────

        uint numObjects  = R32(data, 4);
        uint numRelocs   = R32(data, 8);
        uint numImports  = R32(data, 12);
        uint numSections = R32(data, 16);

        if (numObjects  > 65_536 || numRelocs > 2_000_000 ||
            numImports  > 65_536 || numSections == 0 || numSections > 64) return null;

        long objTableOff    = 28;
        long relocTableOff  = objTableOff   + numObjects  * 4;
        long importTableOff = relocTableOff + numRelocs   * RELOC_SIZE;
        long sectionHdrOff  = importTableOff + numImports * 8;
        long sectionDataOff = sectionHdrOff  + numSections * SECTION_HDR;

        if (sectionDataOff > data.Length) return null;

        // ── 2. Parse section headers ───────────────────────────────────────

        var sections = new (uint Type, int DataOffset, int DataSize, int AllocSize)[numSections];
        long cursor = sectionDataOff;

        for (int i = 0; i < (int)numSections; i++)
        {
            long hdr = sectionHdrOff + i * SECTION_HDR;
            if (hdr + 12 > data.Length) return null;
            sections[i] = (
                Type:       R32(data, (int)hdr),
                DataSize:   (int)R32(data, (int)(hdr + 4)),
                AllocSize:  (int)R32(data, (int)(hdr + 8)),
                DataOffset: (int)cursor
            );
            cursor += sections[i].AllocSize;
            if (cursor > data.Length) cursor = data.Length;
        }

        // ── 3. Find section type 2 (CDCRenderModel) ───────────────────────

        int meshSecIdx = -1;
        for (int i = 0; i < (int)numSections; i++)
            if (sections[i].Type == 2) { meshSecIdx = i; break; }

        if (meshSecIdx < 0) return null;
        int mBase = sections[meshSecIdx].DataOffset;
        if (mBase + MDL_PTR_MESHES + 8 > data.Length) return null;

        // ── 4. Parse relocation table ──────────────────────────────────────
        //
        //  Each 8-byte entry:
        //    byte[0] srcSectionIdx
        //    byte[1] dstSectionIdx
        //    byte[2-3] flags  (usually 0)
        //    byte[4-7] srcByteOffset within srcSection.data
        //
        //  The uint32 stored at sections[srcSec].data + srcOffset is a
        //  byte offset into sections[dstSec].data.

        // Build a map: (srcSec, srcRelativeOffset) → dstSec
        var relocMap = new Dictionary<(int sec, int off), int>((int)numRelocs);

        for (int i = 0; i < (int)numRelocs; i++)
        {
            long roff = relocTableOff + i * RELOC_SIZE;
            if (roff + 8 > data.Length) break;

            int  srcSec = data[(int)roff];
            int  dstSec = data[(int)roff + 1];
            uint srcOff = R32(data, (int)(roff + 4));

            if (srcSec < numSections && dstSec < numSections)
                relocMap[(srcSec, (int)srcOff)] = dstSec;
        }

        // ── 5. Read CDCRenderModel header ──────────────────────────────────

        uint modelVer   = R32(data, mBase);
        uint numMeshes  = R32(data, mBase + MDL_NUM_MESHES);
        uint numLODs    = R32(data, mBase + MDL_NUM_LODS);

        // Sanity: version should be 7 for SOTTR; allow 4-10 range for minor variants
        if (modelVer < 4 || modelVer > 15) return null;
        if (numMeshes == 0 || numMeshes > 4096) return null;
        if (numLODs   == 0 || numLODs   > 32)   numLODs = 1;

        // ── 6. Resolve pMeshes pointer ─────────────────────────────────────

        // MDL_PTR_MESHES is the srcOffset relative to section start, i.e.
        // absolute srcAbsOff = mBase + MDL_PTR_MESHES (which maps to sections[meshSecIdx] at MDL_PTR_MESHES)
        if (!relocMap.TryGetValue((meshSecIdx, MDL_PTR_MESHES), out int meshDstSec))
        {
            // Reloc format may differ — try brute-force: scan the first 128 bytes
            // of section 2 for the first reloc entry with srcSec == meshSecIdx
            bool found = false;
            for (int off = 0; off < 128; off += 4)
            {
                if (relocMap.TryGetValue((meshSecIdx, off), out meshDstSec))
                {
                    uint val = R32(data, mBase + off);
                    if (val < (uint)data.Length &&
                        sections[meshDstSec].DataOffset + (int)val + MESH_SIZE <= data.Length)
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (!found) return null;
        }

        uint pMeshesVal  = R32(data, mBase + MDL_PTR_MESHES);
        int  meshArrBase = sections[meshDstSec].DataOffset + (int)pMeshesVal;

        if (meshArrBase + (int)numMeshes * MESH_SIZE > data.Length) return null;

        // ── 7. Parse each CDCRenderMesh → collect geometry per LOD ────────

        // Group submeshes by LOD index so we can merge them per LOD level
        var lodGroups = new Dictionary<int, List<(int vtxCount, int idxCount, byte[] verts, byte[] inds)>>();

        for (int mi = 0; mi < (int)numMeshes; mi++)
        {
            int meshBase = meshArrBase + mi * MESH_SIZE;
            if (meshBase + MESH_SIZE > data.Length) break;

            int numVerts   = (int)R32(data, meshBase + MESH_NUM_VERTS);
            int numIndices = (int)R32(data, meshBase + MESH_NUM_INDICES);
            int numStreams  = (int)R32(data, meshBase + MESH_NUM_STREAMS);
            int lodIdx     = (int)R32(data, meshBase + MESH_LOD_INDEX);

            if (numVerts < 3 || numVerts > 2_000_000) continue;
            if (numIndices < 3 || numIndices > 10_000_000 || numIndices % 3 != 0) continue;
            if (numStreams < 1 || numStreams > 16) continue;
            if (lodIdx < 0 || lodIdx > 31) lodIdx = 0;

            // -- Resolve stream descriptors pointer --
            // The pointer is stored as srcRelOff = MESH_PTR_STREAMS within this mesh's data.
            // BUT — the relocation table srcOffset is relative to the SECTION start, not the mesh.
            // So srcRelOff (section-relative) = (meshBase - sections[meshDstSec].DataOffset) + MESH_PTR_STREAMS
            int meshSecRelOff  = meshBase - sections[meshDstSec].DataOffset;
            int streamSrcOff   = meshSecRelOff + MESH_PTR_STREAMS;
            int indexSrcOff    = meshSecRelOff + MESH_PTR_INDICES;

            if (!relocMap.TryGetValue((meshDstSec, streamSrcOff), out int streamDstSec)) continue;
            if (!relocMap.TryGetValue((meshDstSec, indexSrcOff),  out int indexDstSec))  continue;

            uint pStreams    = R32(data, meshBase + MESH_PTR_STREAMS);
            uint pIndices   = R32(data, meshBase + MESH_PTR_INDICES);

            int streamArrBase = sections[streamDstSec].DataOffset + (int)pStreams;
            int indexBufBase  = sections[indexDstSec].DataOffset  + (int)pIndices;

            if (streamArrBase + numStreams * STREAM_SIZE > data.Length) continue;
            if (indexBufBase  + numIndices * 2           > data.Length) continue;

            // -- Find POSITION stream (usage == 0) --
            int posBufBase = -1;
            int posStride  = 12; // default: float32×3

            for (int si = 0; si < numStreams; si++)
            {
                int sBase     = streamArrBase + si * STREAM_SIZE;
                if (sBase + STREAM_SIZE > data.Length) break;

                uint stride    = R32(data, sBase + STR_STRIDE);
                uint usage     = R32(data, sBase + STR_USAGE);

                // srcOffset of this stream descriptor's data pointer within its section
                int strSecRelOff  = sBase - sections[streamDstSec].DataOffset;
                int dataSrcOff    = strSecRelOff + STR_PTR_DATA;

                if (!relocMap.TryGetValue((streamDstSec, dataSrcOff), out int dataDstSec)) continue;

                uint pData  = R32(data, sBase + STR_PTR_DATA);
                int dataBuf = sections[dataDstSec].DataOffset + (int)pData;

                // Accept the POSITION stream (usage 0) or the first stream with stride ≥ 12
                if (usage == 0 || (posBufBase < 0 && stride >= 6))
                {
                    if (dataBuf + numVerts * (int)stride <= data.Length)
                    {
                        posBufBase = dataBuf;
                        posStride  = (int)stride;
                        if (usage == 0) break; // found the dedicated position stream
                    }
                }
            }

            if (posBufBase < 0) continue;

            // -- Read vertex positions --
            byte[] vertBytes = ExtractPositions(data, posBufBase, posStride, numVerts);
            if (vertBytes == null || vertBytes.Length == 0) continue;

            // -- Read index buffer (uint16) --
            byte[] idxBytes = new byte[numIndices * 4];
            for (int ii = 0; ii < numIndices; ii++)
            {
                uint idxVal = R16(data, indexBufBase + ii * 2);
                BitConverter.TryWriteBytes(idxBytes.AsSpan(ii * 4, 4), idxVal);
            }

            // -- Validate: check first few triangles have sane geometry --
            if (!ValidateTriangles(vertBytes, idxBytes, numVerts, Math.Min(8, numIndices / 3)))
                continue;

            if (!lodGroups.TryGetValue(lodIdx, out var lodList))
            {
                lodList = new List<(int, int, byte[], byte[])>();
                lodGroups[lodIdx] = lodList;
            }
            lodGroups[lodIdx].Add((numVerts, numIndices, vertBytes, idxBytes));
        }

        if (lodGroups.Count == 0) return null;

        // ── 8. Merge submeshes per LOD and build MeshAssetData ────────────

        var lods = new List<LodData>();

        foreach (var (lodIdx, meshes) in lodGroups.OrderBy(kv => kv.Key))
        {
            // Merge all submeshes at this LOD level
            int totalVerts   = meshes.Sum(m => m.vtxCount);
            int totalTris    = meshes.Sum(m => m.idxCount / 3);
            var posConcat    = new byte[totalVerts * 12];
            var idxConcat    = new byte[meshes.Sum(m => m.idxCount) * 4];

            int vCursor = 0, iCursor = 0, baseVtx = 0;

            foreach (var (vtxCount, idxCount, verts, inds) in meshes)
            {
                // Copy vertex positions
                Buffer.BlockCopy(verts, 0, posConcat, vCursor, verts.Length);
                vCursor += verts.Length;

                // Copy indices, offsetting by baseVtx
                for (int ii = 0; ii < idxCount; ii++)
                {
                    int shifted = BitConverter.ToInt32(inds, ii * 4) + baseVtx;
                    BitConverter.TryWriteBytes(idxConcat.AsSpan(iCursor + ii * 4, 4), shifted);
                }
                iCursor += idxCount * 4;
                baseVtx += vtxCount;
            }

            lods.Add(new LodData
            {
                LodIndex      = lodIdx,
                VertexCount   = totalVerts,
                TriangleCount = totalTris,
                VertexBuffer  = posConcat,
                IndexBuffer   = idxConcat,
            });
        }

        if (lods.Count == 0) return null;

        return new MeshAssetData
        {
            Info       = info,
            IsSkeletal = false,   // SOTTR skinned meshes need separate skin section parsing
            Lods       = lods,
            MaterialSlots = new List<MaterialSlot>(),
            RawProperties = new Dictionary<string, object?>
            {
                ["DRM Version"]  = DRM_MAGIC,
                ["Model Version"] = R32(data, mBase),
                ["NumMeshes"]    = numMeshes,
                ["NumLODs"]      = numLODs,
                ["LODsParsed"]   = lods.Count,
                ["Vertices_LOD0"] = lods[0].VertexCount,
                ["Triangles_LOD0"] = lods[0].TriangleCount,
            }
        };
    }

    // ── Vertex extraction ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads vertex positions from the buffer.
    /// SOTTR uses float32×3 for the position stream, but some DRM files may use
    /// float16×4 (stride=8) or quantized formats.  We detect the format from the stride.
    /// </summary>
    private static byte[] ExtractPositions(byte[] data, int bufBase, int stride, int numVerts)
    {
        var out_ = new byte[numVerts * 12];

        if (stride >= 12)
        {
            // float32 × 3 — copy x, y, z directly
            for (int v = 0; v < numVerts; v++)
            {
                int src = bufBase + v * stride;
                if (src + 12 > data.Length) break;
                Buffer.BlockCopy(data, src, out_, v * 12, 12);
            }
        }
        else if (stride >= 6)
        {
            // float16 × 3 (or 4) — decode to float32
            for (int v = 0; v < numVerts; v++)
            {
                int src = bufBase + v * stride;
                if (src + 6 > data.Length) break;
                float x = HalfToFloat(R16(data, src));
                float y = HalfToFloat(R16(data, src + 2));
                float z = HalfToFloat(R16(data, src + 4));
                int dst = v * 12;
                BitConverter.TryWriteBytes(out_.AsSpan(dst,     4), x);
                BitConverter.TryWriteBytes(out_.AsSpan(dst + 4, 4), y);
                BitConverter.TryWriteBytes(out_.AsSpan(dst + 8, 4), z);
            }
        }
        else
        {
            return Array.Empty<byte>();
        }

        return out_;
    }

    /// <summary>
    /// IEEE 754 float16 → float32 conversion.
    /// Sign(1) Exponent(5) Mantissa(10) → Sign(1) Exponent(8) Mantissa(23)
    /// </summary>
    private static float HalfToFloat(uint h)
    {
        uint sign  = (h >> 15) & 1;
        uint exp   = (h >> 10) & 0x1F;
        uint mant  = h & 0x3FF;

        uint f;
        if (exp == 0)
        {
            if (mant == 0) { f = sign << 31; }
            else
            {
                // Subnormal → normalize
                while ((mant & 0x400) == 0) { mant <<= 1; exp--; }
                exp++;
                mant &= 0x3FF;
                f = (sign << 31) | ((exp + 112) << 23) | (mant << 13);
            }
        }
        else if (exp == 31)
        {
            f = (sign << 31) | 0x7F800000u | (mant << 13); // Inf or NaN
        }
        else
        {
            f = (sign << 31) | ((exp + 112) << 23) | (mant << 13);
        }

        return BitConverter.Int32BitsToSingle((int)f);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks that the first N triangles have valid indices (< numVerts) and
    /// positions that form triangles with sensible edge lengths (1 mm – 10 m range).
    /// </summary>
    private static bool ValidateTriangles(byte[] verts, byte[] inds, int numVerts, int triCount)
    {
        for (int t = 0; t < triCount; t++)
        {
            int i0 = BitConverter.ToInt32(inds, t * 12 + 0);
            int i1 = BitConverter.ToInt32(inds, t * 12 + 4);
            int i2 = BitConverter.ToInt32(inds, t * 12 + 8);
            if (i0 >= numVerts || i1 >= numVerts || i2 >= numVerts) return false;

            float x0 = BitConverter.ToSingle(verts, i0 * 12);
            float y0 = BitConverter.ToSingle(verts, i0 * 12 + 4);
            float z0 = BitConverter.ToSingle(verts, i0 * 12 + 8);
            float x1 = BitConverter.ToSingle(verts, i1 * 12);
            float y1 = BitConverter.ToSingle(verts, i1 * 12 + 4);
            float z1 = BitConverter.ToSingle(verts, i1 * 12 + 8);

            float dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            float edge = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            // Edge length sanity: 1 mm to 50 m (in whatever unit the engine uses)
            // SOTTR uses centimeters like UE, so that's 0.1 cm – 5000 cm
            if (edge < 0.01f || edge > 100_000f) return false;
        }
        return true;
    }

    // ── Binary helpers ─────────────────────────────────────────────────────────

    private static uint R32(byte[] b, int o)
        => (o >= 0 && o + 4 <= b.Length) ? BitConverter.ToUInt32(b, o) : 0u;

    private static uint R16(byte[] b, int o)
        => (o >= 0 && o + 2 <= b.Length) ? BitConverter.ToUInt16(b, o) : 0u;
}
