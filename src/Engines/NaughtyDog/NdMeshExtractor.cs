using System.Numerics;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Session 2 port of <c>fmt_nd_pak.py</c>'s geometry parsing
/// (<c>readPak</c> + <c>PakSubmesh</c> + <c>T2StreamDesc</c> + <c>loadGeometry</c>).
///
/// Consumes an <see cref="NdPakReader"/> (Session 1 foundation) and produces a
/// <see cref="MeshAssetData"/> with per-submesh boundaries and m_papTransform applied.
///
/// Reference: <c>C:\Tools\Noesis\plugins\python\fmt_nd_pak.py</c> v1.53.
/// </summary>
public static class NdMeshExtractor
{
    public static MeshAssetData? TryExtract(NdPakReader reader, AssetInfo info)
    {
        if (reader.GeoEntry == null)
        {
            Log.Info($"NdMeshExtractor[{info.Name}]: no GEOMETRY_1 entry in pak");
            return null;
        }
        if (!reader.IsTLOU2)
        {
            Log.Info($"NdMeshExtractor[{info.Name}]: non-TLOU2 path not yet supported (game={reader.Game})");
            return null;
        }

        try
        {
            var ctx = new GeoContext(reader, info.Name);
            if (!ctx.ReadHeader()) return null;
            if (!ctx.ReadSubmeshes()) return null;
            ctx.ReadPapTransforms();
            ctx.Decode();
            return ctx.Build(info);
        }
        catch (Exception ex)
        {
            Log.Warn($"NdMeshExtractor[{info.Name}]: extraction threw — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

// ── Internal context ─────────────────────────────────────────────────────────

internal class GeoContext
{
    private readonly NdPakReader _r;
    private readonly byte[]      _data;
    private readonly string      _label;

    // Geometry header
    private int _ghOff;
    private int _numSubmesh;
    private int _numLODs;
    private int _numMaterials;
    private int _submeshesAbs;
    private long _papTransform;

    // Discovered structures
    private readonly List<Submesh> _submeshes = new();
    private readonly Dictionary<int, List<Matrix4x4>> _xformsBySubmesh = new();

    public GeoContext(NdPakReader r, string label)
    {
        _r = r;
        _data = r.Data;
        _label = label;
    }

    // ── Header ────────────────────────────────────────────────────────────────

    public bool ReadHeader()
    {
        if (_r.GeoEntry is not { } geo) return false;

        // ghOff = pageStart + resItemOffset + ResItemPaddingSz
        _ghOff = geo.PageStart + geo.ResItemOffset + _r.ResItemPaddingSz;
        if (_ghOff + 80 > _data.Length)
        {
            Log.Warn($"NdMeshExtractor[{_label}]: geometry header OOB (ghOff=0x{_ghOff:X})");
            return false;
        }

        // Per fmt_nd_pak.py 2070-2079:
        //   +0:  m_version (u32)
        //   +4:  m_isForeground (u32)
        //   +8:  m_numSubMeshDesc (u32)
        //   +12: m_numLODs (u32)
        //   +16: m_numMaterials (u32)
        //   ... (m_unk4..m_unk8 = 5 × u32)
        //   +40: SubmeshesOffs (ptr fixup)
        _numSubmesh   = (int)R32(_ghOff + 8);
        _numLODs      = (int)R32(_ghOff + 12);
        _numMaterials = (int)R32(_ghOff + 16);

        if (_numSubmesh <= 0 || _numSubmesh > 1024)
        {
            Log.Warn($"NdMeshExtractor[{_label}]: implausible numSubmesh={_numSubmesh}");
            return false;
        }

        var smPtr = _r.ReadPointerFixup(_ghOff + 40);
        if (smPtr is null || smPtr.Value <= 0)
        {
            Log.Warn($"NdMeshExtractor[{_label}]: no SubmeshesOffs fixup at 0x{_ghOff + 40:X}");
            return false;
        }
        _submeshesAbs = (int)smPtr.Value;

        // TLOU2 / TLOUP1: m_papTransform is 16 bytes after SubmeshesOffs
        // (fmt_nd_pak 2083-2086: uknStruct@+48, m_papTransform@+56, textureDescs@+64, ...)
        var papPtr = _r.ReadPointerFixup(_ghOff + 56);
        _papTransform = papPtr ?? 0;

        Log.Info($"NdMeshExtractor[{_label}]: header  numSubmesh={_numSubmesh}  numLODs={_numLODs}  numMaterials={_numMaterials}  submeshesAbs=0x{_submeshesAbs:X}  papTransform=0x{_papTransform:X}");
        return true;
    }

    // ── Submesh table ────────────────────────────────────────────────────────

    public bool ReadSubmeshes()
    {
        var successIndices = new List<int>();

        for (int i = 0; i < _numSubmesh; i++)
        {
            int sd = _submeshesAbs + 176 * i;
            if (sd + 176 > _data.Length)
            {
                Log.Warn($"NdMeshExtractor[{_label}]: submesh {i} OOB at 0x{sd:X}");
                continue;
            }

            // Per fmt_nd_pak.py 2142-2171 (TLOU2 branch).
            // Fields in *file order* (8-byte aligned cursor walk):
            //   +0..+31  : bbox[2][4] floats
            //   +32      : submeshName ptr (fixup, may be 0)
            //   +40      : ukn64_0
            //   +48      : m_pStreamDesc ptr (fixup)
            //   +56      : ukn64_1
            //   +64      : m_pIndexes ptr (fixup, TP1 zero-condition true)
            //   +72      : m_material ptr (fixup)
            //   +80      : ukn64_2
            //   +88      : skinDataOffset ptr (fixup)
            //   +96      : ukn64_3
            //   +104     : ukn64_4
            //   +112     : nrmRecalcDescOffs ptr (fixup)
            //   +120     : uknStringOffs (u64)
            //
            // EMPIRICAL CORRECTION: fmt_nd_pak.py docs put count fields at +0x80/+0x84/+0x88,
            // but the actual TLOU2 PC layout has them shifted by +8 — there's an extra 8-byte
            // field between uknStringOffs and m_numVertexes that the script's walk doesn't show.
            // Validated by the legacy parser producing correct vertex counts at +0x88.
            //   +136 (0x88): m_numVertexes (u32)
            //   +140 (0x8C): m_numIndexes (u32)
            //   +144 (0x90): m_numStreamSource (u32)

            var namePtr     = _r.ReadPointerFixup(sd + 32);
            var streamPtr   = _r.ReadPointerFixup(sd + 48);
            var indexPtr    = _r.ReadPointerFixup(sd + 64, zeroIsValid: true);
            var materialPtr = _r.ReadPointerFixup(sd + 72);
            var skinPtr     = _r.ReadPointerFixup(sd + 88);

            int numVerts   = (int)R32(sd + 0x88);
            int numIndices = (int)R32(sd + 0x8C);
            int numStreams = (int)R32(sd + 0x90);

            // Sanity bounds
            if (numVerts <= 0 || numVerts > 2_000_000) { Log.Info($"NdMeshExtractor[{_label}]: SMD#{i} skipped — numVerts={numVerts}"); continue; }
            if (numIndices < 3 || numIndices > 8_000_000 || numIndices % 3 != 0) { Log.Info($"NdMeshExtractor[{_label}]: SMD#{i} skipped — numIndices={numIndices}"); continue; }
            if (numStreams < 1 || numStreams > 16) { Log.Info($"NdMeshExtractor[{_label}]: SMD#{i} skipped — numStreams={numStreams}"); continue; }

            // Read submesh name (full path; we keep the trailing element after '|')
            string fullName = namePtr.HasValue && namePtr.Value > 0
                ? ReadString((int)namePtr.Value)
                : $"Submesh{i}";
            int pipe = fullName.LastIndexOf('|');
            string shortName = pipe >= 0 ? fullName[(pipe + 1)..] : fullName;

            int lodIndex = ParseLodSuffix(shortName);

            var submesh = new Submesh
            {
                Index       = i,
                AbsOffset   = sd,
                Name        = shortName,
                FullName    = fullName,
                NumVerts    = numVerts,
                NumIndices  = numIndices,
                IndexAddr   = (int)(indexPtr ?? 0),
                MaterialPtr = (int)(materialPtr ?? 0),
                LodIndex    = lodIndex,
            };

            // Read each stream descriptor (T2StreamDesc — 64 bytes per entry)
            int streamBase = (int)(streamPtr ?? 0);
            if (streamBase <= 0)
            {
                Log.Info($"NdMeshExtractor[{_label}]: SMD#{i} {shortName} — no stream desc ptr");
                continue;
            }

            for (int j = 0; j < numStreams; j++)
            {
                int stOff = streamBase + 64 * j;
                if (stOff + 64 > _data.Length) break;

                var bufPtr = _r.ReadPointerFixup(stOff + 0, zeroIsValid: true);

                int  streamNumVerts = (int)R32(stOff + 8);
                int  bufferSize     = (int)R32(stOff + 16);
                byte compType       = _data[stOff + 20];
                byte unk2           = _data[stOff + 21];
                byte packed22       = _data[stOff + 22];
                byte unk3           = (byte)(packed22 & 0x0F);          // low nibble
                byte stride         = (byte)((packed22 >> 4) & 0x0F);   // high nibble
                byte unk4           = _data[stOff + 23];

                var sizes = new byte[]
                {
                    _data[stOff + 24], _data[stOff + 25],
                    _data[stOff + 26], _data[stOff + 27],
                };

                var qScale = new[]
                {
                    BitConverter.ToSingle(_data, stOff + 32),
                    BitConverter.ToSingle(_data, stOff + 36),
                    BitConverter.ToSingle(_data, stOff + 40),
                    BitConverter.ToSingle(_data, stOff + 44),
                };
                var qOffs = new[]
                {
                    BitConverter.ToSingle(_data, stOff + 48),
                    BitConverter.ToSingle(_data, stOff + 52),
                    BitConverter.ToSingle(_data, stOff + 56),
                    BitConverter.ToSingle(_data, stOff + 60),
                };

                submesh.Streams.Add(new StreamDef
                {
                    Index           = j,
                    BufferAddr      = (int)(bufPtr ?? 0),
                    NumVerts        = streamNumVerts,
                    BufferSize      = bufferSize,
                    CompType        = compType,
                    Stride          = stride,
                    Sizes           = sizes,
                    QScale          = qScale,
                    QOffs           = qOffs,
                });
            }

            _submeshes.Add(submesh);
            successIndices.Add(i);
        }

        Log.Info($"NdMeshExtractor[{_label}]: parsed {_submeshes.Count}/{_numSubmesh} submeshes  (real submesh indices: {string.Join(", ", successIndices)})");

        // Diagnostic summary: explain why this asset may render unexpectedly. Most TLOU2
        // ND character paks contain only 1–4 actual triangle-mesh entries; the rest are
        // auxiliary descriptors that share the 176-byte SubMeshDesc layout but aren't
        // renderable geometry. The "every 12th index succeeds" pattern is normal — there's
        // 1 mesh + 11 auxiliary descriptors per LOD bucket.
        if (_papTransform == 0 && _r.JointEntry == null)
        {
            Log.Info($"NdMeshExtractor[{_label}]: this is a character/clothing pak with no embedded skeleton (joint=False) and no static-placement matrices (papTransform=0). Vertices are in bone-local space and will appear unposed until a base-skeleton pak is loaded (Session 4 work). Missing parts (e.g. straps, buckles) live in sibling paks like abby-backpack-straps-cloth.pak.");
        }

        return _submeshes.Count > 0;
    }

    // ── m_papTransform: per-submesh world transforms ─────────────────────────

    public void ReadPapTransforms()
    {
        if (_papTransform <= 0 || _numMaterials <= 0) return;

        // Per fmt_nd_pak.py 2099-2116:
        //   m_papTransform points to an array of u64 fixup ptrs (one per material).
        //   Each material entry resolves to a struct:
        //     +0   : 4×4 matrix (16 floats)
        //     +152 : submeshXformsOffs (ptr fixup)
        //     +212 : numSubmeshXforms (u32)
        //   submeshXforms is array of 112-byte entries; each has at +64 a ptr to the
        //   submesh (whose value matches our submesh.AbsOffset).

        int papBase = (int)_papTransform;
        int totalAttached = 0;

        // Per fmt_nd_pak the inner offsets are submeshXformsOffs@+152, count@+212.
        // Same +8-byte-shift mystery as the SubMeshDesc layout may apply here. We probe
        // both layouts and use whichever produces sane submesh-pointer hits.
        var candidateLayouts = new[] {
            (xformPtrOff: 152, countOff: 212, label: "documented"),
            (xformPtrOff: 160, countOff: 220, label: "shifted+8"),
        };

        foreach (var layout in candidateLayouts)
        {
            int attemptedAttachments = 0;
            int validHits = 0;
            var localXforms = new Dictionary<int, List<Matrix4x4>>();

            for (int m = 0; m < _numMaterials; m++)
            {
                int matEntryAddr = papBase + 8 * m;
                var matStructPtr = _r.ReadPointerFixup(matEntryAddr);
                if (matStructPtr is null || matStructPtr.Value <= 0) continue;

                int matStructAt = (int)matStructPtr.Value;
                if (matStructAt + layout.countOff + 4 > _data.Length) continue;

                var mat = ReadMat44(matStructAt);

                var submeshXformsPtr = _r.ReadPointerFixup(matStructAt + layout.xformPtrOff);
                if (submeshXformsPtr is null || submeshXformsPtr.Value <= 0) continue;

                int submeshXformsAt = (int)submeshXformsPtr.Value;
                int numSubmeshXforms = (int)R32(matStructAt + layout.countOff);
                if (numSubmeshXforms < 0 || numSubmeshXforms > 1024) continue;  // sanity

                for (int j = 0; j < numSubmeshXforms; j++)
                {
                    int entryAddr = submeshXformsAt + j * 112 + 64;
                    if (entryAddr + 8 > _data.Length) break;

                    var smPtr = _r.ReadPointerFixup(entryAddr, zeroIsValid: true);
                    if (smPtr is null) continue;
                    attemptedAttachments++;

                    int submeshOffs = (int)smPtr.Value;
                    // Validate: this address should match one of our submesh AbsOffsets
                    if (_submeshes.Any(s => s.AbsOffset == submeshOffs))
                        validHits++;

                    if (!localXforms.TryGetValue(submeshOffs, out var list))
                        localXforms[submeshOffs] = list = new List<Matrix4x4>();
                    list.Add(mat);
                }
            }

            // Accept the layout if at least half of attached submesh-pointers match
            // a real SubMeshDesc address. Otherwise try the next candidate.
            if (validHits > 0 && validHits * 2 >= attemptedAttachments)
            {
                _xformsBySubmesh.Clear();
                foreach (var kv in localXforms) _xformsBySubmesh[kv.Key] = kv.Value;
                totalAttached = attemptedAttachments;
                Log.Info($"NdMeshExtractor[{_label}]: m_papTransform layout '{layout.label}' accepted ({validHits}/{attemptedAttachments} valid)");
                break;
            }
            else
            {
                Log.Info($"NdMeshExtractor[{_label}]: m_papTransform layout '{layout.label}' rejected ({validHits}/{attemptedAttachments} valid)");
            }
        }

        Log.Info($"NdMeshExtractor[{_label}]: attached {totalAttached} m_papTransform matrices to {_xformsBySubmesh.Count} submeshes");
    }

    // ── Vertex / index decode + transform application ────────────────────────

    public void Decode()
    {
        foreach (var sm in _submeshes)
        {
            DecodeSubmesh(sm);
        }
    }

    private void DecodeSubmesh(Submesh sm)
    {
        // Indices: u16 raw at IndexAddr
        if (sm.IndexAddr > 0 && sm.IndexAddr + sm.NumIndices * 2 <= _data.Length)
        {
            sm.IndexBytes = new byte[sm.NumIndices * 4];   // upcast to u32
            for (int i = 0; i < sm.NumIndices; i++)
            {
                int  byteOff = sm.IndexAddr + i * 2;
                uint idx     = BitConverter.ToUInt16(_data, byteOff);
                BitConverter.TryWriteBytes(sm.IndexBytes.AsSpan(i * 4, 4), idx);
            }
        }

        // Streams: positions (idx 0 with stride==12 OR compType==64), UV0 (75), UV1 (65), UVx (76)
        StreamDef? posStream = null;
        StreamDef? uvStream  = null;

        foreach (var st in sm.Streams)
        {
            // Position stream: first stream with stride==12 (raw float32) OR any stream type 64 (quantised)
            if (posStream == null && ((st.Index == 0 && st.Stride == 12) || st.CompType == 64))
            {
                posStream = st;
            }
            else if (uvStream == null && st.CompType == 75)
            {
                uvStream = st;
            }
        }

        if (posStream != null) sm.Positions = DecodePositionStream(posStream, sm.NumVerts);
        if (uvStream  != null) sm.Uvs       = DecodeUvStream(uvStream, sm.NumVerts);

        // Apply m_papTransform (first matrix only — instancing handled at LOD-build time)
        if (sm.Positions != null && _xformsBySubmesh.TryGetValue(sm.AbsOffset, out var mats) && mats.Count > 0)
        {
            ApplyTransform(sm.Positions, mats[0]);
            sm.AppliedXform = mats[0];
            sm.HasXform = true;
        }
    }

    private byte[]? DecodePositionStream(StreamDef st, int submeshNumVerts)
    {
        // Two possible forms:
        //   (a) Raw float32 positions at st.BufferAddr — when st.Stride == 12 and st.Index == 0.
        //   (b) Quantised continuous bitstream — when st.CompType == 64.
        if (st.BufferAddr <= 0) return null;

        if (st.Stride == 12 && st.Index == 0)
        {
            int byteCount = submeshNumVerts * 12;
            if (st.BufferAddr + byteCount > _data.Length) return null;
            var copy = new byte[byteCount];
            Buffer.BlockCopy(_data, st.BufferAddr, copy, 0, byteCount);
            return copy;
        }

        // Bitstream decode
        var output = new byte[submeshNumVerts * 12];
        var reader = new BitReader(_data, st.BufferAddr);
        int n = Math.Min(st.NumVerts, submeshNumVerts);

        for (int v = 0; v < n; v++)
        {
            float x = 0, y = 0, z = 0;

            if (st.Sizes[0] > 0) x = reader.ReadBits(st.Sizes[0]) * st.QScale[0] + st.QOffs[0];
            else if (st.CompType == 64) x = st.QScale[0] + st.QOffs[0];

            if (st.Sizes[1] > 0) y = reader.ReadBits(st.Sizes[1]) * st.QScale[1] + st.QOffs[1];
            else if (st.CompType == 64) y = st.QScale[1] + st.QOffs[1];

            if (st.Sizes[2] > 0) z = reader.ReadBits(st.Sizes[2]) * st.QScale[2] + st.QOffs[2];
            else if (st.CompType == 64) z = st.QScale[2] + st.QOffs[2];

            // sizes[3] is read but discarded (per fmt_nd_pak: only used for type 64 c<3 fallback)
            if (st.Sizes[3] > 0) reader.ReadBits(st.Sizes[3]);

            int dst = v * 12;
            BitConverter.TryWriteBytes(output.AsSpan(dst,     4), x);
            BitConverter.TryWriteBytes(output.AsSpan(dst + 4, 4), y);
            BitConverter.TryWriteBytes(output.AsSpan(dst + 8, 4), z);
        }

        return output;
    }

    private byte[]? DecodeUvStream(StreamDef st, int submeshNumVerts)
    {
        if (st.BufferAddr <= 0) return null;
        if (st.Sizes[0] == 0 || st.Sizes[1] == 0) return null;

        var output = new byte[submeshNumVerts * 8];
        var reader = new BitReader(_data, st.BufferAddr);
        int n = Math.Min(st.NumVerts, submeshNumVerts);

        for (int v = 0; v < n; v++)
        {
            float u = reader.ReadBits(st.Sizes[0]) * st.QScale[0] + st.QOffs[0];
            float vv = reader.ReadBits(st.Sizes[1]) * st.QScale[1] + st.QOffs[1];
            int dst = v * 8;
            BitConverter.TryWriteBytes(output.AsSpan(dst,     4), u);
            BitConverter.TryWriteBytes(output.AsSpan(dst + 4, 4), vv);
        }
        return output;
    }

    // Apply rigid (rotation + translation) transform to a packed 12 b/vertex float32 buffer.
    // Per fmt_nd_pak.movePositionsBuffer: extract quaternion from rotation portion, rotate,
    // then add translation row.
    private static void ApplyTransform(byte[] posBuf, Matrix4x4 mat)
    {
        // Decompose into quaternion + translation. If matrix has scale we keep it via Transform.
        var rotation = Quaternion.CreateFromRotationMatrix(mat);
        var translation = new Vector3(mat.M41, mat.M42, mat.M43);

        int n = posBuf.Length / 12;
        for (int v = 0; v < n; v++)
        {
            int o = v * 12;
            var pos = new Vector3(
                BitConverter.ToSingle(posBuf, o),
                BitConverter.ToSingle(posBuf, o + 4),
                BitConverter.ToSingle(posBuf, o + 8));

            var rotated = Vector3.Transform(pos, rotation) + translation;

            BitConverter.TryWriteBytes(posBuf.AsSpan(o,     4), rotated.X);
            BitConverter.TryWriteBytes(posBuf.AsSpan(o + 4, 4), rotated.Y);
            BitConverter.TryWriteBytes(posBuf.AsSpan(o + 8, 4), rotated.Z);
        }
    }

    // ── LOD grouping + final MeshAssetData build ─────────────────────────────

    public MeshAssetData Build(AssetInfo info)
    {
        var byLod = _submeshes
            .Where(s => s.Positions != null && s.IndexBytes != null)
            .GroupBy(s => s.LodIndex)
            .OrderBy(g => g.Key);

        var lods = new List<LodData>();
        int xformedCount = 0;

        foreach (var lodGroup in byLod)
        {
            var posList = new List<byte>();
            var idxList = new List<byte>();
            var uvList  = new List<byte>();
            bool hasUvs = lodGroup.All(s => s.Uvs != null);
            int totalVerts = 0;
            int totalTris  = 0;
            var subBoundaries = new List<SubmeshInfo>();

            foreach (var sm in lodGroup)
            {
                int submeshIndexStart  = idxList.Count / 4;
                int submeshVertexStart = totalVerts;

                // Re-base index buffer to merged vertex offset
                int idxLen = sm.IndexBytes!.Length / 4;
                var rebased = new byte[sm.IndexBytes.Length];
                for (int i = 0; i < idxLen; i++)
                {
                    int globalIdx = BitConverter.ToInt32(sm.IndexBytes, i * 4) + totalVerts;
                    BitConverter.TryWriteBytes(rebased.AsSpan(i * 4, 4), globalIdx);
                }
                idxList.AddRange(rebased);

                // Positions
                posList.AddRange(sm.Positions!);

                // UVs
                if (hasUvs && sm.Uvs != null)
                    uvList.AddRange(sm.Uvs);

                subBoundaries.Add(new SubmeshInfo
                {
                    Name        = string.IsNullOrEmpty(sm.Name) ? $"Shape{subBoundaries.Count}" : sm.Name,
                    VertexStart = submeshVertexStart,
                    VertexCount = sm.NumVerts,
                    IndexStart  = submeshIndexStart,
                    IndexCount  = sm.NumIndices,
                });

                totalVerts += sm.NumVerts;
                totalTris  += sm.NumIndices / 3;
                if (sm.HasXform) xformedCount++;
            }

            if (totalVerts > 0)
            {
                lods.Add(new LodData
                {
                    LodIndex      = lodGroup.Key,
                    VertexCount   = totalVerts,
                    TriangleCount = totalTris,
                    VertexBuffer  = posList.ToArray(),
                    IndexBuffer   = idxList.ToArray(),
                    UvBuffer      = hasUvs && uvList.Count == totalVerts * 8 ? uvList.ToArray() : null,
                    Submeshes     = subBoundaries,
                });
            }
        }

        Log.Info($"NdMeshExtractor[{_label}]: built {lods.Count} LOD(s); applied m_papTransform to {xformedCount}/{_submeshes.Count} submeshes");

        var mesh = new MeshAssetData
        {
            Info       = info,
            IsSkeletal = info.Type == AssetType.SkeletalMesh,
            Lods       = lods,
        };
        return mesh;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private uint  R32(int o) => BitConverter.ToUInt32(_data, o);
    private ulong R64(int o) => BitConverter.ToUInt64(_data, o);

    private string ReadString(int o)
    {
        if (o < 0 || o >= _data.Length) return string.Empty;
        int end = o;
        while (end < _data.Length && _data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(_data, o, end - o);
    }

    private Matrix4x4 ReadMat44(int o)
    {
        // 16 floats as 4 vec4s; build Matrix4x4 row-major (System.Numerics convention).
        return new Matrix4x4(
            BitConverter.ToSingle(_data, o +  0), BitConverter.ToSingle(_data, o +  4),
            BitConverter.ToSingle(_data, o +  8), BitConverter.ToSingle(_data, o + 12),
            BitConverter.ToSingle(_data, o + 16), BitConverter.ToSingle(_data, o + 20),
            BitConverter.ToSingle(_data, o + 24), BitConverter.ToSingle(_data, o + 28),
            BitConverter.ToSingle(_data, o + 32), BitConverter.ToSingle(_data, o + 36),
            BitConverter.ToSingle(_data, o + 40), BitConverter.ToSingle(_data, o + 44),
            BitConverter.ToSingle(_data, o + 48), BitConverter.ToSingle(_data, o + 52),
            BitConverter.ToSingle(_data, o + 56), BitConverter.ToSingle(_data, o + 60));
    }

    private static int ParseLodSuffix(string shortName)
    {
        int sp = shortName.IndexOf("Shape");
        if (sp >= 0 && sp + 5 < shortName.Length && char.IsDigit(shortName[sp + 5]))
            return shortName[sp + 5] - '0';
        return 0;
    }
}

// ── Plain data containers (private to this file) ─────────────────────────────

internal class Submesh
{
    public int    Index;
    public int    AbsOffset;       // SubMeshDesc absolute byte offset (matches m_papTransform key)
    public string Name = "";
    public string FullName = "";
    public int    NumVerts;
    public int    NumIndices;
    public int    IndexAddr;
    public int    MaterialPtr;
    public int    LodIndex;
    public List<StreamDef> Streams = new();

    // Decoded
    public byte[]?   Positions;     // 12 b/vertex float32 (post-xform)
    public byte[]?   Uvs;           // 8 b/vertex float32 U/V
    public byte[]?   IndexBytes;    // 4 b/index uint32
    public bool      HasXform;
    public Matrix4x4 AppliedXform;
}

internal class StreamDef
{
    public int     Index;
    public int     BufferAddr;
    public int     NumVerts;
    public int     BufferSize;
    public byte    CompType;
    public byte    Stride;
    public byte[]  Sizes  = new byte[4];
    public float[] QScale = new float[4];
    public float[] QOffs  = new float[4];
}

// ── BitReader: continuous LSB-first bit stream over a byte buffer ────────────

internal class BitReader
{
    private readonly byte[] _data;
    private readonly int    _byteBase;
    private int             _bitPos;   // total bits consumed since _byteBase

    public BitReader(byte[] data, int byteBase)
    {
        _data     = data;
        _byteBase = byteBase;
        _bitPos   = 0;
    }

    /// <summary>
    /// Read up to 32 bits, LSB-first within each byte (matches Noesis readBits semantics
    /// used by fmt_nd_pak.py).
    /// </summary>
    public uint ReadBits(int count)
    {
        if (count <= 0) return 0;
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int totalBit = _bitPos + i;
            int byteOff  = _byteBase + (totalBit >> 3);
            int bitInByte = totalBit & 7;
            if (byteOff < 0 || byteOff >= _data.Length) break;
            uint bit = ((uint)_data[byteOff] >> bitInByte) & 1u;
            value |= bit << i;
        }
        _bitPos += count;
        return value;
    }
}
