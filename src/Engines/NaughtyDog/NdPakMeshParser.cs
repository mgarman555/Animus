using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Parses NaughtyDog TLOU2 .pak files to extract 3-D mesh geometry.
///
/// Navigation mirrors fmt_nd_pak.py (Noesis plugin by alphaZomega).
///
/// Fixup table (always Format A):
///   Header at fixupOff: [u32 pageEntryNum] [u32 dataOffset] [u32 count]
///   Entry: src:u16 dst:u16 off:u32 — key = pages[src].fo + off → dstPage
///
/// GEOMETRY_1 navigation:
///   Each page header at pageBase+18: numEntries (u16)
///   Each entry (16 bytes): namePtr(u64) resItemOffset(u32) pad(u32)
///   ResItem at pageBase+riOff: namePtr(u64) typePtr(u64) → string "GEOMETRY_1"
///
/// SubMeshDesc stride = 176 bytes (TLOU2):
///   +0x20 namePtr(ptr)   +0x30 m_pStreamDesc(ptr)  +0x40 m_pIndexes(ptr)
///   +0x88 numVerts(u32)  +0x8C numIndexes(u32)      +0x90 numStreamSrc(u32)
///
/// T2StreamDesc stride = 64 bytes:
///   +0x00 m_bufferOffset(ptr)  +0x10 bufferSize(u32)
///   +0x14 compType(u8)         +0x16 hi-nibble=stride(bytes)
///   +0x18..1B sizes[4](u8)     +0x20 qScale(float4)  +0x30 qOffs(float4)
///
/// Quantized position decoding (compType==64, Noesis-faithful):
///   Bits are packed CONTINUOUSLY across all vertices (no per-vertex byte reset).
///   Each vertex reads sizes[0]+sizes[1]+sizes[2]+sizes[3] bits in order.
///   pos_c = readBits(sizes[c]) * qScale[c] + qOffs[c]
///
/// Float32 position (j==0, stride==12):
///   Read 3×float32 per vertex directly from the buffer.
/// </summary>
public static class NdPakMeshParser
{
    const int PAGE_ENTRY_SZ = 12;
    const int FIX_ENTRY_SZ  = 8;
    const int SMD_STRIDE    = 176;

    struct Page { public int FileOffset; public int Size; }

    // ── Public API ────────────────────────────────────────────────────────────

    public static MeshAssetData? TryParse(byte[] data, AssetInfo info)
    {
        try   { return ParseInternal(data, info); }
        catch { return null; }
    }

    /// <summary>
    /// Lightweight metadata scan: reads all resource-item type strings and their key
    /// fields without decoding any geometry. Used to populate the FModel-style info
    /// panel for any ND .pak asset.
    /// </summary>
    public static Dictionary<string, object?> ParseMetadata(byte[] data)
    {
        var props = new Dictionary<string, object?>();
        try { ParseMetadataInternal(data, props); }
        catch { /* best-effort */ }
        return props;
    }

    static void ParseMetadataInternal(byte[] data, Dictionary<string, object?> props)
    {
        if (data.Length < 0x20) return;
        uint magic = R32(data, 0);
        if (magic != 0xA79 && magic != 0x10A79 && magic != 0x80000A79 && magic != 0xA7D) return;

        int pageCt  = (int)R32(data, 0x10);
        int ptOff   = (int)R32(data, 0x14);
        int fixupOff = (int)R32(data, 0x1C);
        if (pageCt < 1 || pageCt > 10_000) return;

        props["_Magic"]  = $"0x{magic:X}";
        props["_Pages"]  = pageCt;

        var pages = new Page[pageCt];
        for (int i = 0; i < pageCt; i++)
        {
            int o = ptOff + i * PAGE_ENTRY_SZ;
            if (o + 8 > data.Length) return;
            pages[i] = new Page { FileOffset = (int)R32(data, o), Size = (int)R32(data, o + 4) };
        }

        int fixDataOff = (int)R32(data, fixupOff + 4);
        int fixCount   = (int)R32(data, fixupOff + 8);
        if (fixDataOff <= 0) fixDataOff = fixupOff;
        var fixups = new Dictionary<int, int>(Math.Max(fixCount, 64));
        for (int i = 0; i < fixCount; i++)
        {
            int fo = fixDataOff + i * FIX_ENTRY_SZ;
            if (fo + 8 > data.Length) break;
            int src = BitConverter.ToUInt16(data, fo);
            int dst = BitConverter.ToUInt16(data, fo + 2);
            uint poff = BitConverter.ToUInt32(data, fo + 4);
            if (src < pageCt && dst < pageCt)
                fixups[pages[src].FileOffset + (int)poff] = dst;
        }

        const int VRAM_HEADER_SZ = 16;
        int vramIdx = 0;
        var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        // isTLOU2 check for GEOMETRY_1 pad
        int loginPageIdx = (int)R32(data, 0x08);
        int loginPageOff = (int)R32(data, 0x0C);
        bool isTLOU2 = loginPageIdx >= 0 && loginPageIdx < pageCt
            && R32(data, pages[loginPageIdx].FileOffset + loginPageOff + 32) == 74565u;
        int padSz = isTLOU2 ? 48 : 32;

        for (int p = 0; p < pageCt; p++)
        {
            int start = pages[p].FileOffset;
            int nEnt  = R16(data, start + 18);
            if (nEnt <= 0 || nEnt > 65535) continue;

            int cur = start + 20;
            for (int ph = 0; ph < nEnt; ph++)
            {
                if (cur + 16 > data.Length) break;
                int riOff = (int)R32(data, cur + 8);
                cur += 16;

                int riBase = start + riOff;
                if (riBase + 16 > data.Length) continue;

                long typePtr = BitConverter.ToInt64(data, riBase + 8);
                if (typePtr <= 0) continue;
                int tOff = start + (int)typePtr;
                if (tOff < 0 || tOff >= data.Length) continue;
                string typeName = ReadString(data, tOff);
                if (string.IsNullOrEmpty(typeName)) continue;

                typeCounts.TryGetValue(typeName, out int tc);
                typeCounts[typeName] = tc + 1;

                if (typeName == "VRAM_DESC")
                {
                    int vb = start + riOff + VRAM_HEADER_SZ;
                    if (vb + 120 > data.Length) continue;

                    uint pakOff  = R32(data, vb + 40);
                    uint vramSz  = R32(data, vb + 48);
                    ulong mHash  = BitConverter.ToUInt64(data, vb + 56);
                    int imgFmt   = (int)R32(data, vb + 72);
                    int mipCt    = (int)R32(data, vb + 80);
                    int width    = (int)R32(data, vb + 84);
                    int height   = (int)R32(data, vb + 88);
                    string fmt   = imgFmt switch { 71=>"BC1", 80=>"BC4", 83=>"BC5", 98=>"BC7", _=>$"fmt{imgFmt}" };
                    string path  = vb + 116 <= data.Length ? ReadString(data, vb + 112) : "";

                    string szStr = vramSz < 1024 * 1024
                        ? $"{vramSz / 1024.0:F1} KB"
                        : $"{vramSz / (1024.0 * 1024):F2} MB";

                    // Emit compact summary line + detail fields using _-prefix to keep them grouped
                    string prefix = $"Texture[{vramIdx}]";
                    props[prefix]            = $"{fmt}  {width}×{height}  ({szStr}  {mipCt} mips)";
                    props[prefix + ".path"]  = path;
                    props[prefix + ".hash"]  = $"0x{mHash:X16}";
                    vramIdx++;
                }
                else if (typeName == "GEOMETRY_1")
                {
                    int ghOff = start + riOff + padSz;
                    if (ghOff + 12 <= data.Length)
                    {
                        int numSMD = (int)R32(data, ghOff + 8);
                        props["Geometry.SubMeshes"] = numSMD;
                    }
                }
                else if (typeName == "MATERIAL_TABLE_1")
                {
                    // Just note its presence — material table has complex variable layout
                    props["MaterialTable"] = "present";
                }
            }
        }

        // Summary: all resource types found
        if (typeCounts.Count > 0)
            props["ResourceTypes"] = string.Join("  ·  ", typeCounts.Select(kv => $"{kv.Value}× {kv.Key}"));
    }

    // ── Core parser ───────────────────────────────────────────────────────────

    static MeshAssetData? ParseInternal(byte[] data, AssetInfo info)
    {
        if (data.Length < 0x20) return null;

        uint magic = R32(data, 0);
        if (magic != 0xA79 && magic != 0x10A79 && magic != 0x80000A79 && magic != 0xA7D)
            return null;

        int pageCt       = (int)R32(data, 0x10);
        int ptOff        = (int)R32(data, 0x14);
        int fixupOff     = (int)R32(data, 0x1C);
        int loginPageIdx = (int)R32(data, 0x08);
        int loginPageOff = (int)R32(data, 0x0C);
        if (pageCt < 1 || pageCt > 200) return null;

        // ── Page table ────────────────────────────────────────────────────────
        var pages = new Page[pageCt];
        for (int i = 0; i < pageCt; i++)
        {
            int o = ptOff + i * PAGE_ENTRY_SZ;
            if (o + 8 > data.Length) return null;
            pages[i] = new Page
            {
                FileOffset = (int)R32(data, o),
                Size       = (int)R32(data, o + 4)
            };
        }

        // ── Fixup table (always Format A) ─────────────────────────────────────
        int fixDataOff = (int)R32(data, fixupOff + 4);
        int fixCount   = (int)R32(data, fixupOff + 8);
        if (fixDataOff <= 0) fixDataOff = fixupOff;

        var fixups = new Dictionary<int, int>(Math.Max(fixCount, 64));
        for (int i = 0; i < fixCount; i++)
        {
            int fo = fixDataOff + i * FIX_ENTRY_SZ;
            if (fo + 8 > data.Length) break;
            int  src  = BitConverter.ToUInt16(data, fo);
            int  dst  = BitConverter.ToUInt16(data, fo + 2);
            uint poff = BitConverter.ToUInt32(data, fo + 4);
            if (src >= pageCt || dst >= pageCt) continue;
            fixups[pages[src].FileOffset + (int)poff] = dst;
        }

        // ── isTLOU2 ───────────────────────────────────────────────────────────
        if (loginPageIdx < 0 || loginPageIdx >= pageCt) return null;
        int loginStart = pages[loginPageIdx].FileOffset + loginPageOff;
        bool isTLOU2   = R32(data, loginStart + 32) == 74565u;
        int  padSz     = isTLOU2 ? 48 : 32;

        // ── Find GEOMETRY_1 ───────────────────────────────────────────────────
        int geoStart = -1, geoItemOff = -1;
        for (int p = 0; p < pageCt && geoItemOff < 0; p++)
        {
            int start = pages[p].FileOffset;
            int nEnt  = R16(data, start + 18);
            if (nEnt <= 0 || nEnt > 65535) continue;
            int cur = start + 20;
            for (int ph = 0; ph < nEnt; ph++)
            {
                if (cur + 16 > data.Length) break;
                int riOff  = (int)R32(data, cur + 8);
                cur += 16;
                int riBase = start + riOff;
                if (riBase + 16 > data.Length) continue;
                long typePtr = BitConverter.ToInt64(data, riBase + 8);
                if (typePtr <= 0) continue;
                int tOff = start + (int)typePtr;
                if (tOff < 0 || tOff >= data.Length) continue;
                if (ReadString(data, tOff) == "GEOMETRY_1")
                { geoStart = start; geoItemOff = riOff; break; }
            }
        }
        if (geoItemOff < 0) return null;

        // ── Geometry header ───────────────────────────────────────────────────
        int ghOff  = geoStart + geoItemOff + padSz;
        if (ghOff + 44 > data.Length) return null;
        int numSMD = (int)R32(data, ghOff + 8);
        if (numSMD <= 0 || numSMD > 256) return null;

        if (!fixups.TryGetValue(ghOff + 40, out int smPage)) return null;
        long smLo   = BitConverter.ToInt64(data, ghOff + 40);
        int  smBase = pages[smPage].FileOffset + (int)smLo;

        // ── Collect all valid embedded SMDs (flat list, no LOD grid) ──────────
        // Groups by LOD index extracted from the SMD name ("ShapeN" digit).
        // All SMDs default to LOD0 when name is missing or has no digit.
        var smdByLod = new Dictionary<int, List<SMDInfo>>();

        for (int i = 0; i < numSMD; i++)
        {
            int sd = smBase + SMD_STRIDE * i;
            if (sd + 0x94 > data.Length) continue;

            int nVerts = (int)R32(data, sd + 0x88);
            int nIdx   = (int)R32(data, sd + 0x8C);
            int nSS    = (int)R32(data, sd + 0x90);

            if (nVerts < 3 || nVerts > 500_000)  continue;
            if (nIdx   < 3 || nIdx > 3_000_000 || nIdx % 3 != 0) continue;
            if (nSS    < 1 || nSS > 32)           continue;

            // m_pIndexes at +0x40
            if (!fixups.TryGetValue(sd + 0x40, out int ixPage)) continue;
            long ixLo  = BitConverter.ToInt64(data, sd + 0x40);
            int  ixAbs = pages[ixPage].FileOffset + (int)ixLo;
            if (ixAbs < 0 || ixAbs + nIdx * 2 > data.Length) continue;

            // Quick index sanity check
            bool idxOk = true;
            for (int k = 0; k < Math.Min(16, nIdx); k++)
                if (BitConverter.ToUInt16(data, ixAbs + k * 2) >= (uint)nVerts) { idxOk = false; break; }
            if (!idxOk) continue;

            // m_pStreamDesc at +0x30
            if (!fixups.TryGetValue(sd + 0x30, out int sdPage2)) continue;
            int sdBase2 = pages[sdPage2].FileOffset + (int)BitConverter.ToInt64(data, sd + 0x30);

            // Find position stream and UV stream in one pass.
            //
            // Stream component-type IDs (per fmt_nd_pak.py loadGeometry):
            //   ctype  Channel    Use
            //   ─────  ─────────  ───────────────────────────────
            //    64    Positions  (continuous-bitstream quantised XYZ)
            //    65    UV1        DIFFUSE / main texture mapping
            //    75    UV2        Lightmap or alt mapping
            //    76    UVX        Extra UV channels
            //
            // For texturing the diffuse atlas we want UV1 (ctype=65). Falling back to
            // UV2 (ctype=75) only when 65 is absent — using UV2 for diffuse made parts of
            // the texture appear in wrong places on the mesh because UV2 maps a different
            // (lightmap-style) atlas.
            int posStreamIdx = -1, uvStreamIdx = -1, uvFallbackIdx = -1;
            for (int j = 0; j < nSS; j++)
            {
                int strmAt = sdBase2 + 64 * j;
                if (strmAt + 0x40 > data.Length) break;
                if (!fixups.ContainsKey(strmAt)) continue;

                byte ctype  = data[strmAt + 0x14];
                int  stride = (data[strmAt + 0x16] >> 4) & 0xF;

                if (posStreamIdx < 0 && ((j == 0 && stride == 12) || ctype == 64))
                    posStreamIdx = j;
                else if (uvStreamIdx < 0 && ctype == 65)
                    uvStreamIdx = j;
                else if (uvFallbackIdx < 0 && ctype == 75)
                    uvFallbackIdx = j;
            }
            if (uvStreamIdx < 0) uvStreamIdx = uvFallbackIdx;   // fall back to UV2 if no UV1
            if (posStreamIdx < 0) continue;

            int strmOff = sdBase2 + 64 * posStreamIdx;
            if (!fixups.TryGetValue(strmOff, out int bufPage)) continue;
            int bufAbs = pages[bufPage].FileOffset + (int)BitConverter.ToInt64(data, strmOff);

            byte posCompType = data[strmOff + 0x14];
            int  posStride   = (data[strmOff + 0x16] >> 4) & 0xF;
            byte sz0 = data[strmOff + 0x18];
            byte sz1 = data[strmOff + 0x19];
            byte sz2 = data[strmOff + 0x1A];
            byte sz3 = data[strmOff + 0x1B];
            float qSx = BitConverter.ToSingle(data, strmOff + 0x20);
            float qSy = BitConverter.ToSingle(data, strmOff + 0x24);
            float qSz = BitConverter.ToSingle(data, strmOff + 0x28);
            float qOx = BitConverter.ToSingle(data, strmOff + 0x30);
            float qOy = BitConverter.ToSingle(data, strmOff + 0x34);
            float qOz = BitConverter.ToSingle(data, strmOff + 0x38);

            bool posF32 = (posStreamIdx == 0 && posStride == 12);

            if (posF32)
            {
                if (bufAbs + (long)nVerts * 12 > data.Length) continue;
            }
            else
            {
                if (sz0 == 0 && sz1 == 0 && sz2 == 0) continue;
                long bitsPerVert = sz0 + sz1 + sz2 + sz3;
                if (bufAbs + (bitsPerVert * nVerts + 7) / 8 > data.Length) continue;
            }

            // UV stream metadata (ctype=75, quantised continuous bit stream)
            int   uvBufAbs = -1;
            byte  uvSz0 = 0, uvSz1 = 0;
            float uvScU = 1, uvScV = 1, uvOfU = 0, uvOfV = 0;
            if (uvStreamIdx >= 0)
            {
                int uvOff = sdBase2 + 64 * uvStreamIdx;
                if (fixups.TryGetValue(uvOff, out int uvBufPage))
                {
                    uvBufAbs = pages[uvBufPage].FileOffset + (int)BitConverter.ToInt64(data, uvOff);
                    uvSz0 = data[uvOff + 0x18];
                    uvSz1 = data[uvOff + 0x19];
                    uvScU = BitConverter.ToSingle(data, uvOff + 0x20);
                    uvScV = BitConverter.ToSingle(data, uvOff + 0x24);
                    uvOfU = BitConverter.ToSingle(data, uvOff + 0x30);
                    uvOfV = BitConverter.ToSingle(data, uvOff + 0x34);
                    long uvTotal = ((long)uvSz0 + uvSz1) * nVerts;
                    if (uvBufAbs < 0 || uvBufAbs + (uvTotal + 7) / 8 > data.Length)
                        uvBufAbs = -1; // out of range — skip
                }
            }

            // Read LOD index from SMD name ("ShapeN" suffix); also keep short name for UI
            int lodIdx = 0;
            string smdShortName = string.Empty;
            if (fixups.TryGetValue(sd + 0x20, out int namePageIdx))
            {
                long nameOff = BitConverter.ToInt64(data, sd + 0x20);
                int nameAbs  = pages[namePageIdx].FileOffset + (int)nameOff;
                string fullName = ReadString(data, nameAbs);
                int pipeIdx = fullName.LastIndexOf('|');
                smdShortName = pipeIdx >= 0 ? fullName[(pipeIdx + 1)..] : fullName;
                int shapePos = smdShortName.IndexOf("Shape");
                if (shapePos >= 0 && shapePos + 5 < smdShortName.Length
                    && char.IsDigit(smdShortName[shapePos + 5]))
                    lodIdx = smdShortName[shapePos + 5] - '0';
            }

            var smdInfo = new SMDInfo
            {
                Name = smdShortName,
                NVerts = nVerts, NIdx = nIdx,
                IxAbs  = ixAbs, VbAbs = bufAbs,
                PosF32 = posF32, Stride = posStride,
                Sz0 = sz0, Sz1 = sz1, Sz2 = sz2, Sz3 = sz3,
                QScX = qSx, QScY = qSy, QScZ = qSz,
                QOfX = qOx, QOfY = qOy, QOfZ = qOz,
                UvBufAbs = uvBufAbs,
                UvSz0 = uvSz0, UvSz1 = uvSz1,
                UvScU = uvScU, UvScV = uvScV,
                UvOfU = uvOfU, UvOfV = uvOfV,
            };

            if (!smdByLod.TryGetValue(lodIdx, out var bucket))
                smdByLod[lodIdx] = bucket = new List<SMDInfo>();
            bucket.Add(smdInfo);
        }

        if (smdByLod.Count == 0) return null;

        // ── Build one LodData per LOD group ───────────────────────────────────
        var lods = new List<LodData>();
        foreach (var (lodIdx, smds) in smdByLod.OrderBy(kv => kv.Key))
        {
            var posBytes = new List<byte>();
            var idxBytes = new List<byte>();
            var uvBytes  = new List<byte>();
            bool hasUvs  = smds.All(s => s.UvBufAbs >= 0);
            int totalVerts = 0, totalTris = 0;
            var submeshes = new List<SubmeshInfo>();

            foreach (var s in smds)
            {
                int submeshIndexStart = idxBytes.Count / 4;
                int submeshVertexStart = totalVerts;

                // Index buffer (rebased to merged vertex offset)
                var ibuf = new byte[s.NIdx * 4];
                for (int ii = 0; ii < s.NIdx; ii++)
                {
                    int idx = BitConverter.ToUInt16(data, s.IxAbs + ii * 2) + totalVerts;
                    BitConverter.TryWriteBytes(ibuf.AsSpan(ii * 4, 4), idx);
                }
                idxBytes.AddRange(ibuf);

                // Vertex positions
                var pbuf = new byte[s.NVerts * 12];
                if (s.PosF32)
                {
                    for (int v = 0; v < s.NVerts; v++)
                    {
                        int off = s.VbAbs + v * 12;
                        int dst = v * 12;
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst,     4), BitConverter.ToSingle(data, off));
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst + 4, 4), BitConverter.ToSingle(data, off + 4));
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst + 8, 4), BitConverter.ToSingle(data, off + 8));
                    }
                }
                else
                {
                    int bitOff = 0;
                    for (int v = 0; v < s.NVerts; v++)
                    {
                        float x = ReadBitsUint(data, s.VbAbs, ref bitOff, s.Sz0) * s.QScX + s.QOfX;
                        float y = ReadBitsUint(data, s.VbAbs, ref bitOff, s.Sz1) * s.QScY + s.QOfY;
                        float z = ReadBitsUint(data, s.VbAbs, ref bitOff, s.Sz2) * s.QScZ + s.QOfZ;
                        if (s.Sz3 > 0) ReadBitsUint(data, s.VbAbs, ref bitOff, s.Sz3);
                        int dst = v * 12;
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst,     4), x);
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst + 4, 4), y);
                        BitConverter.TryWriteBytes(pbuf.AsSpan(dst + 8, 4), z);
                    }
                }
                posBytes.AddRange(pbuf);

                // UV0 coordinates (ctype=75, continuous bit stream)
                if (hasUvs && s.UvBufAbs >= 0 && s.UvSz0 > 0 && s.UvSz1 > 0)
                {
                    var ubuf = new byte[s.NVerts * 8];
                    int bitOff = 0;
                    for (int v = 0; v < s.NVerts; v++)
                    {
                        float u = ReadBitsUint(data, s.UvBufAbs, ref bitOff, s.UvSz0) * s.UvScU + s.UvOfU;
                        float vv = ReadBitsUint(data, s.UvBufAbs, ref bitOff, s.UvSz1) * s.UvScV + s.UvOfV;
                        int dst = v * 8;
                        BitConverter.TryWriteBytes(ubuf.AsSpan(dst,     4), u);
                        BitConverter.TryWriteBytes(ubuf.AsSpan(dst + 4, 4), vv);
                    }
                    uvBytes.AddRange(ubuf);
                }

                submeshes.Add(new SubmeshInfo
                {
                    Name        = string.IsNullOrEmpty(s.Name) ? $"Shape{submeshes.Count}" : s.Name,
                    VertexStart = submeshVertexStart,
                    VertexCount = s.NVerts,
                    IndexStart  = submeshIndexStart,
                    IndexCount  = s.NIdx,
                });

                totalVerts += s.NVerts;
                totalTris  += s.NIdx / 3;
            }

            if (totalVerts > 0)
                lods.Add(new LodData
                {
                    LodIndex      = lodIdx,
                    VertexCount   = totalVerts,
                    TriangleCount = totalTris,
                    VertexBuffer  = posBytes.ToArray(),
                    IndexBuffer   = idxBytes.ToArray(),
                    UvBuffer      = hasUvs && uvBytes.Count == totalVerts * 8 ? uvBytes.ToArray() : null,
                    Submeshes     = submeshes,
                });
        }

        if (lods.Count == 0) return null;

        var lod0 = lods[0];
        var props = new Dictionary<string, object?>
        {
            ["Vertices"]  = lod0.VertexCount,
            ["Triangles"] = lod0.TriangleCount,
            ["LODs"]      = lods.Count,
        };

        var meshAssetData = new MeshAssetData
        {
            Info          = info,
            IsSkeletal    = true,
            Lods          = lods,
            MaterialSlots = new List<MaterialSlot>(),
            RawProperties = props,
        };

        // ── Scan for embedded thumbnail texture (VRAM_DESC) ───────────────────
        var texture = TryParseVramDesc(data, pages, pageCt, fixups, isTLOU2, info.Name);
        if (texture.HasValue)
        {
            var tex = texture.Value;
            meshAssetData.DiffuseTextureData   = tex.RawBytes;
            meshAssetData.DiffuseTextureWidth  = tex.Width;
            meshAssetData.DiffuseTextureHeight = tex.Height;
            meshAssetData.DiffuseTextureFormat = tex.Format;
            meshAssetData.DiffuseTexturePath   = tex.TexPath;
            props["Texture"]     = $"{tex.Width}×{tex.Height} {tex.Format}";
            props["TexturePath"] = tex.TexPath;
        }
        else
        {
            props["Texture"] = "none (no VRAM_DESC)";
        }

        return meshAssetData;
    }

    // ── VRAM_DESC Texture Parsing ─────────────────────────────────────────────

    struct TextureData
    {
        public byte[]? RawBytes;
        public int Width, Height;
        public string Format;
        public string TexPath;  // null-terminated string at vramBase+112
    }

    static TextureData? TryParseVramDesc(byte[] data, Page[] pages, int pageCt,
        Dictionary<int, int> fixups, bool isTLOU2, string assetName)
    {
        // VRAM_DESC data starts immediately after the 16-byte ResItem header.
        const int VRAM_HEADER_SZ = 16;

        TextureData? best = null;
        int          bestScore = int.MinValue;

        for (int p = 0; p < pageCt; p++)
        {
            int start = pages[p].FileOffset;
            int nEnt  = R16(data, start + 18);
            if (nEnt <= 0 || nEnt > 65535) continue;

            int cur = start + 20;
            for (int ph = 0; ph < nEnt; ph++)
            {
                if (cur + 16 > data.Length) break;
                int riOff = (int)R32(data, cur + 8);
                cur += 16;

                int riBase = start + riOff;
                if (riBase + 16 > data.Length) continue;

                long typePtr = BitConverter.ToInt64(data, riBase + 8);
                if (typePtr <= 0) continue;

                int tOff = start + (int)typePtr;
                if (tOff < 0 || tOff >= data.Length) continue;

                if (ReadString(data, tOff) != "VRAM_DESC") continue;

                int vramBase = start + riOff + VRAM_HEADER_SZ;
                if (vramBase + 120 > data.Length) continue;

                int pakOffset = (int)R32(data, vramBase + 40);
                int vramSize  = (int)R32(data, vramBase + 48);
                int imgFormat = (int)R32(data, vramBase + 72);
                int width     = (int)R32(data, vramBase + 84);
                int height    = (int)R32(data, vramBase + 88);

                if (width < 4 || width > 4096 || height < 4 || height > 4096) continue;
                if (vramSize < 16 || vramSize > 10_000_000) continue;

                int texDataOffset = pages[pageCt - 1].FileOffset + pages[pageCt - 1].Size + pakOffset;
                if (texDataOffset < 0 || texDataOffset + vramSize > data.Length) continue;

                // BC4 = roughness/AO (single-channel), BC5 = normal map (two-channel) — skip
                if (imgFormat != 98 && imgFormat != 71) continue;

                string format = imgFormat == 98 ? "BC7" : "BC1";

                string texPath = vramBase + 116 <= data.Length
                    ? ReadString(data, vramBase + 112)
                    : string.Empty;

                // ── Score this entry to find the main diffuse colour texture ──
                // texPath format: "[BUILD_INTERMEDIATE]common/texture4/.../source.tga/HASH.ndb"
                // The source texture name is the parent directory of the hash file.
                string sourceName = string.Empty;
                if (!string.IsNullOrEmpty(texPath))
                {
                    // Normalise to forward slashes, split on '/'
                    var parts = texPath.Replace('\\', '/').Split('/');
                    if (parts.Length >= 2)
                        sourceName = parts[^2].ToLowerInvariant(); // e.g. "ellie-arms-seattle-color.tga"
                }

                string assetLower = assetName.ToLowerInvariant();
                int score = 0;

                bool isColor   = sourceName.Contains("-color")   || sourceName.Contains("_color");
                bool isNormal  = sourceName.Contains("-normal")  || sourceName.Contains("_normal");
                bool isMask    = sourceName.Contains("-mask")    || sourceName.Contains("_mask");
                bool isRough   = sourceName.Contains("-roughness")|| sourceName.Contains("-rough");
                bool isWrinkle = sourceName.Contains("wrinkle")  || sourceName.Contains("fist_fix");
                bool matchesAsset = !string.IsNullOrEmpty(assetLower) && sourceName.Contains(assetLower);

                if (isColor)         score += 30;
                if (matchesAsset)    score += 20;
                if (isNormal)        score -= 25;
                if (isMask)          score -= 15;
                if (isRough)         score -= 20;
                if (isWrinkle)       score -= 20;
                if (format == "BC7") score += 2;
                if (format == "BC1") score += 1;

                if (score > bestScore)
                {
                    byte[] texBytes = new byte[vramSize];
                    Array.Copy(data, texDataOffset, texBytes, 0, vramSize);
                    best = new TextureData
                    {
                        RawBytes = texBytes, Width = width, Height = height,
                        Format = format, TexPath = texPath
                    };
                    bestScore = score;
                }
            }
        }

        // Only return if we found a genuine colour candidate (score > 0).
        // Negative-score entries are wrinkle/mask/normal maps — showing them
        // as diffuse looks wrong. Better to show a grey mesh and wait for the
        // full-res texturedict lookup.
        return bestScore > 0 ? best : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static uint R32(byte[] b, int off) =>
        off >= 0 && off + 4 <= b.Length ? BitConverter.ToUInt32(b, off) : 0u;

    static int R16(byte[] b, int off) =>
        off >= 0 && off + 2 <= b.Length ? BitConverter.ToUInt16(b, off) : 0;

    static string ReadString(byte[] b, int off)
    {
        var sb = new System.Text.StringBuilder(32);
        while (off >= 0 && off < b.Length && b[off] != 0)
            sb.Append((char)b[off++]);
        return sb.ToString();
    }

    /// <summary>
    /// Read <paramref name="bits"/> bits from a CONTINUOUS bit stream rooted at
    /// <paramref name="byteBase"/>.  <paramref name="bitOff"/> is the running
    /// bit-offset into the stream; it is advanced by <paramref name="bits"/> on
    /// return.  Bit order is LSB-first within each byte (Noesis readBits semantics).
    /// </summary>
    static float ReadBitsUint(byte[] data, int byteBase, ref int bitOff, int bits)
    {
        if (bits == 0) return 0f;
        uint result = 0;
        for (int i = 0; i < bits; i++)
        {
            int absIdx  = byteBase + (bitOff + i) / 8;
            int bitInBy = (bitOff + i) % 8;
            if (absIdx < data.Length && ((data[absIdx] >> bitInBy) & 1) != 0)
                result |= (1u << i);
        }
        bitOff += bits;
        return result;
    }

    class SMDInfo
    {
        public string Name = "";
        public int   NVerts, NIdx;
        public int   IxAbs, VbAbs, Stride;
        public bool  PosF32;
        public byte  Sz0, Sz1, Sz2, Sz3;
        public float QScX, QScY, QScZ;
        public float QOfX, QOfY, QOfZ;
        // UV stream (ctype=75)
        public int   UvBufAbs = -1;
        public byte  UvSz0, UvSz1;
        public float UvScU, UvScV, UvOfU, UvOfV;
    }
}
