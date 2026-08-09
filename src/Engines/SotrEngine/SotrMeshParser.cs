using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Engines.SotrEngine;

/// <summary>
/// Decodes Shadow of the Tomb Raider geometry from a <c>.tr11modeldata</c> resource body
/// (ResourceType.Model, subtype 27).
///
/// Layout verified against arcusmaximus/TrRebootModTools (MIT):
/// Templates/010 Editor/tr/tr11/tr11modeldata.bt, Templates/010 Editor/tr/trmodelcommon.bt,
/// and addons/io_scene_tr_reboot/tr/shadow/{ShadowModel,ShadowMesh,ShadowMeshPart,
/// ShadowModelDataHeader}.py.
///
/// The header's pointer fields hold offsets relative to the start of the resource *body*
/// (the bytes after the refDefinitions prefix). That is provable from the blend-shape block,
/// which addresses its own sub-tables as <c>bodyStart + offset</c>. Driving the parse off
/// those offsets rather than walking the file sequentially matters: meshes carrying blend
/// shapes have a variable-size block in the middle of the mesh data, and a sequential walk
/// desynchronises the moment it hits one.
///
/// ModelDataHeader — 0x160 bytes:
///   +0x000  char[4]   signature "Mesh"
///   +0x004  uint32    flags            bit 0x1 = has vertex weights, bit 0x4000 = has blend shapes
///   +0x008  int32     totalDataSize
///   +0x00C  int32     numIndices
///   +0x010  float[4]  boundingSphereCenter
///   +0x020  float[4]  boundingBoxMin
///   +0x030  float[4]  boundingBoxMax
///   +0x040  float[4]  positionScaleOffset
///   +0x050  float     boundingSphereRadius
///   +0x054  float[6]  lod distance / screen-size thresholds
///   +0x06C  int32     lodMode
///   +0x070  int32     modelType        1 = skinned
///   +0x074  float     sortBias
///   +0x078  uint32[32] boneUsageMap
///   +0x0F8  int64     meshPartsOffset
///   +0x100  int64     meshHeadersOffset
///   +0x108  int64     boneMappingsOffset
///   +0x110  int64     lodLevelsOffset
///   +0x118  int64     indexDataOffset
///   +0x120  uint16    numMeshParts
///   +0x122  uint16    numMeshes
///   +0x124  uint16    numBones
///   +0x126  uint16    numLodLevels
///   +0x128  int64     preTesselationInfoOffset   (0xFFFFFFFF when absent)
///   +0x130  int32     nameLength
///   +0x138  int64     nameOffset
///   +0x140  int32     numBlendShapes
///   +0x148  int64     blendShapeNamesOffset
///   +0x150  float     autoBumpScale
///
/// MeshHeader — 0x60 bytes each:
///   +0x00  int32   numParts
///   +0x04  uint16  numBones
///   +0x08  int64   boneIndicesOffset
///   +0x10  int64   vertexBuffer0Offset
///   +0x18  int64   vertexBuffer0Pointer
///   +0x20  int64   vertexBuffer1Offset
///   +0x28  int64   vertexBuffer1Pointer
///   +0x30  int32   vertexFormatSize
///   +0x38  int64   vertexFormatOffset
///   +0x40  int64   blendShapesHeaderOffset
///   +0x48  int32   numVertices
///
/// VertexFormat:
///   +0x00  uint64  hash
///   +0x08  uint16  numAttributes
///   +0x0A  uint8[2] vertexSizes    stride of each of the two vertex buffers
///   +0x10  numAttributes × 8-byte attributes:
///            +0x00  uint32  nameHash
///            +0x04  int16   offset within its vertex buffer
///            +0x06  uint8   class      index into the TR11 class→type table
///            +0x07  uint8   vertexBufferIdx
///
/// MeshPart — 0x60 bytes each:
///   +0x00  float[4] center
///   +0x10  int32    firstIndexIdx    start within the model-wide index array
///   +0x14  int32    numPrimitives    triangle count
///   +0x18  int32    numVertices
///   +0x1C  int32    flags
///   +0x20  int32    drawGroupId
///   +0x24  int32    order
///   +0x28  int32    actualMeshPart
///   +0x2C  int16    lodLevel
///   +0x30  int64    materialIdx
///   +0x38  int64[5] textureIndices
///
/// Indices are uint16 and are relative to the owning *mesh's* vertex buffer, so merging
/// several meshes into one LOD requires rebasing them.
/// </summary>
public static class SotrMeshParser
{
    private const int  HEADER_SIZE    = 0x160;
    private const int  MESH_HDR_SIZE  = 0x60;
    private const int  MESH_PART_SIZE = 0x60;
    private const uint NO_PRE_TESS    = 0xFFFFFFFF;

    // Vertex attribute name hashes (trmodelcommon.bt)
    private const uint ATTR_POSITION  = 0xD2F7D823;
    private const uint ATTR_TEXCOORD1 = 0x8317902A;

    // ── Public entry point ───────────────────────────────────────────────────

    public static MeshAssetData? TryParse(byte[] body, AssetInfo info)
    {
        try   { return Parse(body, info); }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOTR-Model] '{info.Name}': {ex.Message}");
            return null;
        }
    }

    public static bool LooksLikeModelData(byte[] body)
        => body.Length >= HEADER_SIZE
        && body[0] == (byte)'M' && body[1] == (byte)'e' && body[2] == (byte)'s' && body[3] == (byte)'h';

    // ── Core parser ──────────────────────────────────────────────────────────

    private static MeshAssetData? Parse(byte[] d, AssetInfo info)
    {
        if (!LooksLikeModelData(d)) return null;

        uint flags        = R32(d, 0x004);
        int  numIndices   = (int)R32(d, 0x00C);
        int  modelType    = (int)R32(d, 0x070);

        long meshPartsOff   = (long)R64(d, 0x0F8);
        long meshHeadersOff = (long)R64(d, 0x100);
        long indexDataOff   = (long)R64(d, 0x118);

        int numMeshParts  = R16(d, 0x120);
        int numMeshes     = R16(d, 0x122);
        int numBones      = R16(d, 0x124);

        uint preTess      = (uint)R64(d, 0x128);

        bool hasBlendShapes = (flags & 0x4000) != 0;
        bool hasWeights     = (flags & 0x0001) != 0;

        if (preTess != NO_PRE_TESS && preTess != 0)
            throw new NotSupportedException("model uses pre-tesselation data");

        if (numMeshes <= 0 || numMeshes > 4096)   return null;
        if (numMeshParts <= 0 || numMeshParts > 65536) return null;
        if (numIndices <= 0)                      return null;

        if (!InRange(d, meshHeadersOff, (long)numMeshes * MESH_HDR_SIZE))
            throw new InvalidDataException("mesh header table out of range");
        if (!InRange(d, meshPartsOff, (long)numMeshParts * MESH_PART_SIZE))
            throw new InvalidDataException("mesh part table out of range");
        if (!InRange(d, indexDataOff, (long)numIndices * 2))
            throw new InvalidDataException("index buffer out of range");

        // ── Index buffer (model-wide, uint16) ─────────────────────────────────
        var indices = new ushort[numIndices];
        for (int i = 0; i < numIndices; i++)
            indices[i] = (ushort)R16(d, (int)(indexDataOff + i * 2));

        // ── Meshes: vertex format + decoded positions / UVs ───────────────────
        var meshes = new MeshGeometry?[numMeshes];
        for (int m = 0; m < numMeshes; m++)
        {
            int hdr = (int)(meshHeadersOff + (long)m * MESH_HDR_SIZE);
            meshes[m] = ReadMesh(d, hdr);
        }

        // ── Mesh parts, grouped into LODs ─────────────────────────────────────
        // Parts appear in mesh order: each mesh claims numParts consecutive entries.
        var partsPerMesh = new int[numMeshes];
        for (int m = 0; m < numMeshes; m++)
            partsPerMesh[m] = (int)R32(d, (int)(meshHeadersOff + (long)m * MESH_HDR_SIZE));

        var lodGroups = new SortedDictionary<int, List<MeshPart>>();
        int meshIdx = 0, claimed = 0;

        for (int p = 0; p < numMeshParts; p++)
        {
            while (meshIdx < numMeshes - 1 && claimed >= partsPerMesh[meshIdx])
            {
                meshIdx++;
                claimed = 0;
            }

            int b = (int)(meshPartsOff + (long)p * MESH_PART_SIZE);
            var part = new MeshPart
            {
                MeshIndex     = meshIdx,
                FirstIndexIdx = (int)R32(d, b + 0x10),
                NumPrimitives = (int)R32(d, b + 0x14),
                Flags         = (int)R32(d, b + 0x1C),
                LodLevel      = (short)R16(d, b + 0x2C),
                MaterialIdx   = (long)R64(d, b + 0x30),
            };
            claimed++;   // counted before any skip, so the part-to-mesh walk stays in step

            // Bit 0 marks a shadow-caster proxy: invisible geometry that exists only to cast
            // shadows. Merging it into the LOD doubles the mesh with a coarse duplicate.
            if ((part.Flags & 1) != 0) continue;
            if (part.NumPrimitives <= 0) continue;
            if (part.FirstIndexIdx < 0 || part.FirstIndexIdx + part.NumPrimitives * 3 > numIndices) continue;
            if (meshes[part.MeshIndex] == null) continue;

            int lod = part.LodLevel < 0 ? 0 : part.LodLevel;
            if (!lodGroups.TryGetValue(lod, out var list))
                lodGroups[lod] = list = new List<MeshPart>();
            list.Add(part);
        }

        if (lodGroups.Count == 0) return null;

        // ── Merge each LOD ────────────────────────────────────────────────────
        var lods = new List<LodData>();
        var materialSlots = new SortedDictionary<long, MaterialSlot>();

        foreach (var (lod, parts) in lodGroups)
        {
            var positions = new List<float>();
            var uvs       = new List<float>();
            var merged    = new List<uint>();
            var submeshes = new List<SubmeshInfo>();
            var meshBase  = new Dictionary<int, int>();
            int vertexCount = 0;

            foreach (var part in parts)
            {
                var geo   = meshes[part.MeshIndex]!;
                int count = part.NumPrimitives * 3;

                // Indices are mesh-local; a part addressing past its own vertex buffer means
                // the part-to-mesh assignment is wrong, so drop it rather than emit bad tris.
                bool addressable = true;
                for (int i = 0; i < count; i++)
                {
                    if (indices[part.FirstIndexIdx + i] >= geo.VertexCount) { addressable = false; break; }
                }
                if (!addressable) continue;

                if (!meshBase.TryGetValue(part.MeshIndex, out int baseVertex))
                {
                    baseVertex = vertexCount;
                    meshBase[part.MeshIndex] = baseVertex;

                    positions.AddRange(geo.Positions);
                    uvs.AddRange(geo.Uvs);
                    vertexCount += geo.VertexCount;
                }

                int indexStart = merged.Count;
                for (int i = 0; i < count; i++)
                    merged.Add((uint)(baseVertex + indices[part.FirstIndexIdx + i]));

                submeshes.Add(new SubmeshInfo
                {
                    Name         = $"mesh{part.MeshIndex}_lod{lod}_mat{part.MaterialIdx}",
                    VertexStart  = baseVertex,
                    VertexCount  = geo.VertexCount,
                    IndexStart   = indexStart,
                    IndexCount   = count,
                    MaterialName = $"material_{part.MaterialIdx}",
                });

                materialSlots.TryAdd(part.MaterialIdx, new MaterialSlot
                {
                    SlotIndex    = materialSlots.Count,
                    MaterialName = $"material_{part.MaterialIdx}",
                    MaterialPath = string.Empty,
                });
            }

            if (vertexCount == 0 || merged.Count < 3) continue;

            lods.Add(new LodData
            {
                LodIndex      = lod,
                VertexCount   = vertexCount,
                TriangleCount = merged.Count / 3,
                VertexBuffer  = ToBytes(positions),
                UvBuffer      = ToBytes(uvs),
                IndexBuffer   = ToBytes(merged),
                Submeshes     = submeshes,
            });
        }

        if (lods.Count == 0) return null;

        return new MeshAssetData
        {
            Info          = info,
            IsSkeletal    = hasWeights || modelType == 1 || numBones > 0,
            Lods          = lods,
            MaterialSlots = materialSlots.Values.ToList(),
            Bounds = new BoundingBox
            {
                Min = new[] { F32(d, 0x020), F32(d, 0x024), F32(d, 0x028) },
                Max = new[] { F32(d, 0x030), F32(d, 0x034), F32(d, 0x038) },
            },
            RawProperties = new Dictionary<string, object?>
            {
                ["Model Format"]    = "tr11modeldata",
                ["Meshes"]          = numMeshes,
                ["Mesh Parts"]      = numMeshParts,
                ["Bones"]           = numBones,
                ["Indices"]         = numIndices,
                ["LODs Parsed"]     = lods.Count,
                ["Has Blend Shapes"] = hasBlendShapes,
                ["Skinned"]         = hasWeights || modelType == 1,
                ["Vertices_LOD0"]   = lods[0].VertexCount,
                ["Triangles_LOD0"]  = lods[0].TriangleCount,
            },
        };
    }

    // ── Per-mesh vertex decode ───────────────────────────────────────────────

    private static MeshGeometry? ReadMesh(byte[] d, int hdr)
    {
        if (hdr < 0 || hdr + MESH_HDR_SIZE > d.Length) return null;

        long vb0Off      = (long)R64(d, hdr + 0x10);
        long vb1Off      = (long)R64(d, hdr + 0x20);
        int  fmtSize     = (int)R32(d, hdr + 0x30);
        long fmtOff      = (long)R64(d, hdr + 0x38);
        int  numVertices = (int)R32(d, hdr + 0x48);

        if (numVertices <= 0 || numVertices > 4_000_000) return null;
        if (!InRange(d, fmtOff, 0x10)) return null;

        int numAttributes = R16(d, (int)(fmtOff + 0x08));
        int stride0       = d[(int)(fmtOff + 0x0A)];
        int stride1       = d[(int)(fmtOff + 0x0B)];

        if (numAttributes <= 0 || numAttributes > 64) return null;
        if (!InRange(d, fmtOff, 0x10 + (long)numAttributes * 8)) return null;
        if (fmtSize > 0 && fmtSize != 0x10 + numAttributes * 8) return null;

        long[] bufferOffsets = { vb0Off, vb1Off };
        int[]  strides       = { stride0, stride1 };

        // Validate whichever buffers actually carry attributes
        for (int b = 0; b < 2; b++)
        {
            if (strides[b] == 0) continue;
            if (!InRange(d, bufferOffsets[b], (long)numVertices * strides[b])) return null;
        }

        var positions = new float[numVertices * 3];
        var uvs       = new float[numVertices * 2];
        bool gotPosition = false;

        for (int a = 0; a < numAttributes; a++)
        {
            int  ab       = (int)(fmtOff + 0x10 + a * 8);
            uint name     = R32(d, ab);
            int  attrOff  = (short)R16(d, ab + 4);
            int  cls      = d[ab + 6];
            int  bufIdx   = d[ab + 7];

            if (bufIdx < 0 || bufIdx > 1) continue;
            if (strides[bufIdx] == 0) continue;
            if (name != ATTR_POSITION && name != ATTR_TEXCOORD1) continue;

            var type = ClassToType(cls);
            if (type == VertexType.Unsupported) continue;

            // TEXCOORDS2/4 are 16-bit fixed point: the SNORM value is the UV divided by 16,
            // which is how the format fits a >1 tiling coordinate into a normalised short.
            // Without the scale every SOTTR UV comes out at 1/16 of its true magnitude.
            float scale = (name == ATTR_TEXCOORD1 && (cls == 25 || cls == 26)) ? 16f : 1f;

            long baseOff = bufferOffsets[bufIdx];
            int  stride  = strides[bufIdx];

            for (int v = 0; v < numVertices; v++)
            {
                int at = (int)(baseOff + (long)v * stride + attrOff);
                if (at < 0 || at + TypeSize(type) > d.Length) break;

                ReadAttribute(d, at, type, out float x, out float y, out float z, out _);

                if (name == ATTR_POSITION)
                {
                    positions[v * 3]     = x;
                    positions[v * 3 + 1] = y;
                    positions[v * 3 + 2] = z;
                }
                else
                {
                    uvs[v * 2]     = x * scale;
                    uvs[v * 2 + 1] = y * scale;
                }
            }

            if (name == ATTR_POSITION) gotPosition = true;
        }

        if (!gotPosition) return null;

        return new MeshGeometry
        {
            VertexCount = numVertices,
            Positions   = positions,
            Uvs         = uvs,
        };
    }

    // ── Vertex attribute types ───────────────────────────────────────────────

    private enum VertexType
    {
        Unsupported,
        Float1, Float2, Float3, Float4,
        R8G8B8A8Unorm, R8G8B8A8Uint,
        R16G16Sint, R16G16B16A16Sint,
        R16G16B16A16Uint, R32G32B32A32Uint,
        R16G16Snorm, R16G16B16A16Snorm,
        R16G16Unorm, R16G16B16A16Unorm,
        R10G10B10A2Uint, R10G10B10A2Unorm,
    }

    /// <summary>
    /// TR11 vertex attribute class → concrete type, per the
    /// <c>gTr11VertexAttributeClassTypes</c> table in trmodelcommon.bt.
    /// Class 21 (DEC4N) is absent from that table; it is a 4-component
    /// 10:10:10:2 normalised value, mapped here accordingly.
    /// </summary>
    private static VertexType ClassToType(int cls) => cls switch
    {
        0  => VertexType.Float1,
        1  => VertexType.Float2,
        2  => VertexType.Float3,
        3  => VertexType.Float4,
        4  => VertexType.R8G8B8A8Unorm,      // COLOR32
        5  => VertexType.R8G8B8A8Unorm,      // VECTORC32
        6  => VertexType.R8G8B8A8Uint,       // WEIGHTSC32
        7  => VertexType.R8G8B8A8Uint,       // INDICESC32
        8  => VertexType.R8G8B8A8Uint,       // UBYTE4
        9  => VertexType.R16G16Sint,
        10 => VertexType.R16G16B16A16Sint,
        11 => VertexType.R16G16B16A16Uint,
        12 => VertexType.R32G32B32A32Uint,
        13 => VertexType.R8G8B8A8Unorm,      // UBYTE4N
        14 => VertexType.R16G16Snorm,
        15 => VertexType.R16G16B16A16Snorm,
        16 => VertexType.R16G16Unorm,
        17 => VertexType.R16G16B16A16Unorm,
        18 => VertexType.R10G10B10A2Uint,    // UDEC3
        19 => VertexType.R10G10B10A2Unorm,   // UDEC3N
        20 => VertexType.R10G10B10A2Unorm,   // DEC3N
        21 => VertexType.R10G10B10A2Unorm,   // DEC4N
        22 => VertexType.R8G8B8A8Unorm,      // WEIGHTSUB4N
        23 => VertexType.R8G8B8A8Uint,       // WEIGHTSUB4
        24 => VertexType.R16G16B16A16Uint,   // WEIGHTSUHALF4
        25 => VertexType.R16G16Snorm,        // TEXCOORDS2
        26 => VertexType.R16G16B16A16Snorm,  // TEXCOORDS4
        _  => VertexType.Unsupported,
    };

    private static int TypeSize(VertexType t) => t switch
    {
        VertexType.Float1 => 4,
        VertexType.Float2 => 8,
        VertexType.Float3 => 12,
        VertexType.Float4 => 16,
        VertexType.R8G8B8A8Unorm or VertexType.R8G8B8A8Uint => 4,
        VertexType.R16G16Sint or VertexType.R16G16Snorm or VertexType.R16G16Unorm => 4,
        VertexType.R16G16B16A16Sint or VertexType.R16G16B16A16Uint
            or VertexType.R16G16B16A16Snorm or VertexType.R16G16B16A16Unorm => 8,
        VertexType.R32G32B32A32Uint => 16,
        VertexType.R10G10B10A2Uint or VertexType.R10G10B10A2Unorm => 4,
        _ => 0,
    };

    private static void ReadAttribute(byte[] d, int o, VertexType t,
                                      out float x, out float y, out float z, out float w)
    {
        x = y = z = 0f; w = 0f;
        switch (t)
        {
            case VertexType.Float1: x = F32(d, o); break;
            case VertexType.Float2: x = F32(d, o); y = F32(d, o + 4); break;
            case VertexType.Float3: x = F32(d, o); y = F32(d, o + 4); z = F32(d, o + 8); break;
            case VertexType.Float4: x = F32(d, o); y = F32(d, o + 4); z = F32(d, o + 8); w = F32(d, o + 12); break;

            case VertexType.R8G8B8A8Unorm:
                x = d[o] / 255f; y = d[o + 1] / 255f; z = d[o + 2] / 255f; w = d[o + 3] / 255f; break;
            case VertexType.R8G8B8A8Uint:
                x = d[o]; y = d[o + 1]; z = d[o + 2]; w = d[o + 3]; break;

            case VertexType.R16G16Sint:
                x = S16(d, o); y = S16(d, o + 2); break;
            case VertexType.R16G16B16A16Sint:
                x = S16(d, o); y = S16(d, o + 2); z = S16(d, o + 4); w = S16(d, o + 6); break;
            case VertexType.R16G16B16A16Uint:
                x = R16(d, o); y = R16(d, o + 2); z = R16(d, o + 4); w = R16(d, o + 6); break;
            case VertexType.R32G32B32A32Uint:
                x = R32(d, o); y = R32(d, o + 4); z = R32(d, o + 8); w = R32(d, o + 12); break;

            case VertexType.R16G16Snorm:
                x = S16(d, o) / 32768f; y = S16(d, o + 2) / 32768f; break;
            case VertexType.R16G16B16A16Snorm:
                x = S16(d, o) / 32768f; y = S16(d, o + 2) / 32768f;
                z = S16(d, o + 4) / 32768f; w = S16(d, o + 6) / 32768f; break;

            case VertexType.R16G16Unorm:
                x = R16(d, o) / 65535f; y = R16(d, o + 2) / 65535f; break;
            case VertexType.R16G16B16A16Unorm:
                x = R16(d, o) / 65535f; y = R16(d, o + 2) / 65535f;
                z = R16(d, o + 4) / 65535f; w = R16(d, o + 6) / 65535f; break;

            case VertexType.R10G10B10A2Uint:
            {
                uint p = R32(d, o);
                x = p & 0x3FF; y = (p >> 10) & 0x3FF; z = (p >> 20) & 0x3FF; w = (p >> 30) & 0x3;
                break;
            }
            case VertexType.R10G10B10A2Unorm:
            {
                uint p = R32(d, o);
                x = (p & 0x3FF) / 1023f; y = ((p >> 10) & 0x3FF) / 1023f;
                z = ((p >> 20) & 0x3FF) / 1023f; w = ((p >> 30) & 0x3) / 3f;
                break;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class MeshGeometry
    {
        public int     VertexCount { get; init; }
        public float[] Positions   { get; init; } = Array.Empty<float>();
        public float[] Uvs         { get; init; } = Array.Empty<float>();
    }

    private sealed class MeshPart
    {
        public int  MeshIndex     { get; init; }
        public int  FirstIndexIdx { get; init; }
        public int  NumPrimitives { get; init; }
        public int  Flags         { get; init; }
        public int  LodLevel      { get; init; }
        public long MaterialIdx   { get; init; }
    }

    private static bool InRange(byte[] d, long offset, long length)
        => offset > 0 && length >= 0 && offset + length <= d.Length;

    private static byte[] ToBytes(List<float> values)
    {
        var bytes = new byte[values.Count * 4];
        for (int i = 0; i < values.Count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static byte[] ToBytes(List<uint> values)
    {
        var bytes = new byte[values.Count * 4];
        for (int i = 0; i < values.Count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static uint  R32(byte[] b, int o) => o >= 0 && o + 4 <= b.Length ? BitConverter.ToUInt32(b, o) : 0u;
    private static ulong R64(byte[] b, int o) => o >= 0 && o + 8 <= b.Length ? BitConverter.ToUInt64(b, o) : 0UL;
    private static int   R16(byte[] b, int o) => o >= 0 && o + 2 <= b.Length ? BitConverter.ToUInt16(b, o) : 0;
    private static short S16(byte[] b, int o) => o >= 0 && o + 2 <= b.Length ? BitConverter.ToInt16(b, o) : (short)0;
    private static float F32(byte[] b, int o) => o >= 0 && o + 4 <= b.Length ? BitConverter.ToSingle(b, o) : 0f;
}
