# GameAssetExplorer — Claude Code Guide

Educational game asset explorer (.exe) for browsing, previewing, and exporting assets from multiple game engines. Personal tool for Madi's extract → Blender → reimport-into-UE pipeline.

> **START HERE (handoff):** Read `HANDOFF.md` and `design/SESSION_NOTES.md` first. A Cowork session
> reverse-engineered and proved the full TLOU2 extraction path in Python (`tools/`).
> Git history is in `handoff/GameAssetExplorer.bundle` (see `handoff/PUSH_INSTRUCTIONS.md`).
>
> **Done since handoff (verified vs real Ellie paks):** ① `SMD_STRIDE` fixed 176→192 — all groups now
> decode (head 9/body 4/arms 2 at LOD0). ② `MATERIAL_TABLE` per-submesh texture linkage — each submesh
> resolves its own diffuse/normal texPath. ③ Per-submesh full-res textures wired to the viewer (plugin
> resolves each via the texturedict; `SkeletalMeshViewerWindow` paints each submesh its own brush). Body
> decodes as denim shirt + Ellie's jeans + Converse high-tops, each its own 2K atlas (PNG-verified).
> **Next:** open the app and eyeball the textured Ellie in the 3D viewer (only thing not yet GUI-checked),
> then the multi-game UI shell (`design/ui-mockup.html`) and the RAGE `.ytd` reader.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Language | C# 12 / .NET 8 |
| UI | WPF (Windows only) |
| MVVM | CommunityToolkit.Mvvm |
| UE parsing | CUE4Parse + CUE4Parse-Conversion |
| Image decode | BCnEncoder.NET + SkiaSharp |
| 3D viewport | HelixToolkit.Wpf (WPF3D fallback in SkeletalMeshViewerWindow) |
| Audio | NAudio |
| Serialisation | System.Text.Json |

---

## Solution Structure

```
src/
  Core/                          # Shared models + interfaces (no dependencies)
    Interfaces/IGameEngine.cs    # Engine plugin contract
    Interfaces/IExporter.cs      # Exporter plugin contract
    Models/AssetInfo.cs          # AssetInfo, AssetData subclasses, AssetType enum
    Models/GameConfig.cs         # Persistent game library entry
    Services/ConfigManager.cs    # Load/save games.json to %AppData%
    Services/PluginLoader.cs     # Runtime + built-in plugin registration

  App/                           # WPF application (WinExe)
    ViewModels/                  # CommunityToolkit MVVM view-models
    Views/                       # XAML windows and controls
    Views/ObjLoader.cs           # Wavefront OBJ → MeshGeometry3D

  Engines/
    UnrealEngine/                # CUE4Parse wrapper (UE4/UE5 .pak, .utoc/.ucas)
    NaughtyDog/                  # TLOU2 .pak binary parser (custom format)
      NdPakMeshParser.cs         # Geometry decoding (quantised bitstream)
      PsarcReader.cs             # PSARC archive reader
    SotrEngine/                  # Shadow of the Tomb Raider (Foundation Engine, .tiger)
      Cdrm.cs                    # CDRM compressed-blob container (chunked zlib)
      TigerReader.cs             # TAFS v5 archive: header, TOC, blob reads
      TigerArchiveSet.cs         # Archives keyed by (archiveId, subId); resolves resources
      SotrDrmReader.cs           # .drm ResourceCollection — a manifest, not a container
      SotrTextureReader.cs       # PCD9 texture header → mip chain
      SotrMeshParser.cs          # .tr11modeldata geometry
    RageEngine/                  # GTA5 / RDR2 (RPF archives) — stub

  Exporters/
    TextureExporter/             # BC1/BC3/BC5/BC7 → PNG via BCnEncoder.NET
    ModelExporter/               # FBX export (stub)
    MetadataExporter/            # JSON sidecar files
```

---

## Code Conventions

- **No mocking in tests** — any future tests must hit real data, not stubs.
- **Minimal abstractions** — don't add helpers for one-off things. Keep it simple.
- **No trailing summary paragraphs** — Claude should not summarise what it just did.
- **No comments on unchanged code** — only comment non-obvious new logic.
- Dark UI theme: background `#1A1A1B`, panels `#252526`, borders `#3F3F46`, text `#CCCCCC`.

---

## Current Status

### Working
- Game library home screen (add/disable/remove games)
- Auto-detect UE version from game directory
- Mount UE4/UE5 `.pak` / `.utoc/.ucas` archives with AES decryption
- Full virtual asset tree browsing
- Texture export → PNG (BC block decode + SkiaSharp)
- JSON metadata sidecar export
- Skeletal mesh 3D viewer: mouse-orbit, LOD switch, OBJ fallback, smooth normals
- NaughtyDog pak mesh parsing: quantised continuous-bitstream positions, all submesh groups decode (stride 192), flat LOD grouping via "ShapeN" names
- NaughtyDog per-submesh materials: each submesh resolves its own diffuse/normal texPath from its `m_material` struct
- NaughtyDog per-submesh textures: plugin resolves each submesh's diffuse to full-res via `texturedict3`;
  `SkeletalMeshViewerWindow` decodes + paints each submesh its own `ImageBrush` (PNG-verified; GUI eyeball pending)
- Mesh-level diffuse: VRAM_DESC scan + full-res `texturedict3` hash lookup + BCnEncoder decode + ImageBrush
- SOTR (Shadow of the Tomb Raider): TAFS v5 archives + CDRM decompression, .drm ResourceCollection
  parsing, resource index with %AppData% cache, PCD9 textures, `.tr11modeldata` geometry
- Raw export for any asset without a decoder (`RawAssetData`), so nothing is unexportable
- DDS texture export (`DdsTextureExporter`) — lossless, keeps mips, covers R8/R8G8/BC6H which PNG can't

### In Progress
- **Multi-game UI shell + Codex/FModel hybrid** — see `design/ui-mockup.html`. `AssetBrowserView` is single-game;
  the data model already supports many games + a global cross-game search. Add the game rail + cross-game search VM.
- Note: full-res dict textures are LINEAR (no GOB untile); only the 64×64 embedded thumbnails are tiled (per-submesh
  decode in the viewer forces skip-untile for that reason)

### Planned
- FBX model export (CUE4Parse-Conversion)
- Audio playback with waveform (NAudio)
- Animation viewer with timeline scrubber
- RAGE engine plugin completion (GTA5, RDR2)
- Full-quality texture loading from `texturedict3/common-dict.pak` (hash-based lookup)

---

## NaughtyDog TLOU2 Pak Format — Key Facts

### File Layout
- Header at 0x00; magic `0xA79` / `0x10A79` / etc.
- `pageCt = R32(0x10)`, `ptOff = R32(0x14)` — page table (12 bytes/entry: fileOffset, size, flags)
- `fixupOff = R32(0x1C)` — pointer-fixup table (8 bytes/entry: srcPage u16, dstPage u16, pageOffset u32)
- After all pages: raw texture/stream data (pakOffset is relative to this boundary)

### GEOMETRY_1 / SubMeshDesc
- Scan page headers for type string `"GEOMETRY_1"` (via fixup pointer at `riBase+8`)
- TLOU2 pad: `+16` bytes before geometry header (`isTLOU2` check: `R32(loginStart+32) == 74565`)
- SMD array pointer at `ghOff+40` (fixup → absolute); stride = **192 bytes** (TLOU2 PC — Noesis's 176 is wrong here; the count fields sit +8 from where the Noesis walk puts them, so the real struct is 16 bytes longer)
- Key SMD field offsets: `+0x20` namePtr, `+0x30` streamDescPtr, `+0x40` indexPtr, `+0x48` m_material, `+0x88` numVerts, `+0x8C` numIdx, `+0x90` numStreams
- numMaterials field (`ghOff+16`) reads 0 on these paks — do NOT gate material parsing on it; use each submesh's `+0x48` m_material pointer directly
- LOD index: read from name suffix `"ShapeN"` (N = 0..3)
- **Continuous bitstream**: quantised positions use a single shared `bitOff` across all vertices — do NOT reset per vertex

### T2StreamDesc fields (64 bytes each)
- `+0x14` component type, `+0x16` stride = **(byte >> 4) & 0xF** (high nibble only)
- `+0x18..+0x1B` = sz0, sz1, sz2, sz3 (bits per component) — read all four even if sz3==0
- `+0x20` qScaleX, `+0x24` qScaleY, `+0x28` qScaleZ
- `+0x30` qOffX, `+0x34` qOffY, `+0x38` qOffZ

### VRAM_DESC (embedded textures)
- Scan same page-header loop for type string `"VRAM_DESC"`
- TLOU2: `vramBase = pageStart + riOff + 16`
- `+40` pakOffset (relative to after-pages), `+48` vramSize, `+72` imgFormat
- `+80` mipCount, `+84` width, `+88` height, `+112` texPath string
- imgFormat map: 98 → BC7, 71 → BC1, 80 → BC4, 83 → BC5
- Prefer imgFormat == 98 (BC7) for diffuse colour
- Texture bytes start at: `pages[pageCt-1].FileOffset + pages[pageCt-1].Size + pakOffset`
- Embedded textures are 64×64 GPU-tiled (NVidia 1D-thin GOB layout); must untile before decoding

### MATERIAL_TABLE / per-submesh textures (verified vs Ellie paks)
Each submesh resolves its own material+textures via the `m_material` fixup pointer at **SMD `+0x48`**:
- Material struct: `+0x00` shaderAssetName(ptr→matName e.g. "…/pants-uv:…"), `+0x08` shaderType(ptr),
  `+0x20` **texDescList**(ptr), `+0x114` **texCount**(u32)
- Each texDesc entry is **48 bytes**: `+0x00` name(ptr → `"g_tNdFetchBaseColor01Map"` etc.),
  `+0x18` sub(ptr → `{ +0x00 path(ptr), +0x08 vramHash(u64) }`)
- `vramHash` keys the VRAM_DESC table (hash @ `vramBase+56`) → texPath @ `vramBase+112`; the texPath's
  trailing filename hash drives the full-res `texturedict3` lookup
- Diffuse = name contains `"BaseColor01"`, fallback any `"Color0"` (eyes); normal = `"Normal01"`.
  Alpha-only parts (eyelashes, tears) legitimately have no colour map → leave diffuse empty
- Implemented in `NdPakMeshParser.ParseSubmeshMaterial`; stored on `SubmeshInfo.{MaterialName,
  DiffuseTexturePath, NormalTexturePath}`. Authoritative reference: `fmt_nd_pak.py` ~L2296-2356

---

## Shadow of the Tomb Raider (Foundation Engine) — Key Facts

Reverse-engineered against **arcusmaximus/TrRebootModTools** (MIT) and cross-checked with
`cdcengine.re`. Byte-level logic re-verified in `tools/sotr_format_check.py` (run it — it tests the
CDRM path against real zlib, plus every struct offset below).

### The thing to internalise: `.drm` is a MANIFEST, not a container
`.tiger` archives hold *files* (FNV-1 64 hash → bytes). Nearly all are `.drm`, and a `.drm` holds
**no pixels and no vertices** — it is a table of the resources some content needs, each pointing at
where its bytes live in the archives. So the browsable asset is the **resource**, not the tiger entry.
Searching a `.drm` for a `DDS ` magic or a mesh header finds nothing. This is the trap the first
version of this plugin fell into.

### TAFS v5 archive (`bigfile.NNN.tiger`)
- Header 56 B: `+0x00` magic `0x53464154` "TAFS", `+0x04` version=5, `+0x08` numParts,
  `+0x0C` numFiles, `+0x10` id, `+0x14` subId, `+0x18` platform[32] (`"pcx64-w"`)
- TOC entry **32 B**: `+0x00` nameHash(u64), `+0x08` locale(u64), `+0x10` uncompressedSize,
  `+0x14` compressedSize, `+0x18` archivePart(i16), `+0x1A` **archiveId**(u8),
  `+0x1B` **archiveSubId**(u8), `+0x1C` offset(u32)
- `archiveId`/`archiveSubId` select **which archive set** owns the data — often *not* the one whose
  TOC you read it from. That indirection is how patches and DLC override base files; always resolve
  through `TigerArchiveSet`, never the local reader
- Part path: trailing `000.tiger` → `{part:D3}.tiger`

### CDRM compression (the five things that were wrong before)
On-disk magic bytes are **`CDRM`** (`0x4D524443` as a LE uint32) — *not* `MRDC`.
- Container: `+0x00` magic, `+0x04` type, `+0x08` numChunks, `+0x0C` unused
- Chunk table at `+0x10`: numChunks × `{ u32 packed (uncompressed size = `>> 8`), u32 compressedSize }`
- Payloads start at **align16(0x10 + 8×numChunks)**, and **each payload is padded to 16 bytes**
- `compressedSize == uncompressedSize` → stored verbatim
- Otherwise **skip 2 bytes** (zlib header) and inflate the rest as **raw Deflate**

### DRM v23 ResourceCollection
- Header 32 B: `+0x00` version=23, `+0x04` includeLength, `+0x08` dependenciesLength,
  `+0x0C` paddingLength, `+0x10` size, `+0x14` flags, `+0x18` numResources, `+0x1C` mainResourceIndex
- `+0x20` locale(u64), then in order: identifications, **dependencies, then includes**
  (note: read order is the reverse of the header field order), then locations
- ResourceIdentification **24 B**: `+0x00` bodySize, `+0x04` type(u8, 1=Empty→disabled),
  `+0x08` packed → `subType = (v & 0xFF) >> 1`, `refDefinitionsSize = v >> 8`; `+0x0C` id, `+0x10` locale
- ResourceLocation **24 B**: `+0x00` uniqueKey (`type = >>24`, `id = & 0xFFFFFF`), `+0x08` archivePart(i16),
  `+0x0A` archiveId, `+0x0B` archiveSubId, `+0x0C` offset, `+0x10` sizeInArchive, `+0x14` decompressionOffset
- **Type comes from the location record, subType from the identification record** — deliberately split
- Resource bytes = `[refDefinitions][body]`; stored raw iff `refDefinitionsSize + bodySize == sizeInArchive`,
  otherwise CDRM. `decompressionOffset` is repack bookkeeping and is not needed to read

### Textures are PCD9, not DDS
- 28 B header: `+0x00` magic `0x39444350` ("PCD9"), `+0x04` **DXGI format number**, `+0x08` size,
  `+0x0C` highResMipMapLevels, `+0x10` width(u16), `+0x12` height(u16), `+0x14` volumeDepth,
  `+0x16` depth, `+0x17` mipMapLevels, `+0x18` flags, `+0x1A` class, `+0x1B` tileMode
- `flags & 0x2000` → a 0x100-byte block sits between header and surface; `flags & 0x8000` → cube map
- Surface is **linear, all mips back to back, no offset table** — split by block size
- `highResMipMapLevels > 0` means the top levels stream from elsewhere, so the payload starts partway
  down the chain (the reader drops that many levels and reports the size it can actually decode)

### `.tr11modeldata` geometry
- Header **0x160 B**: `+0x00` "Mesh", `+0x04` flags (`0x1` skinned, `0x4000` blend shapes),
  `+0x0C` numIndices, `+0x20` bboxMin, `+0x30` bboxMax, `+0x70` modelType, `+0xF8` meshPartsOffset,
  `+0x100` meshHeadersOffset, `+0x118` indexDataOffset, `+0x120` numMeshParts/Meshes/Bones/LodLevels (u16 ×4),
  `+0x128` preTesselationInfoOffset (`0xFFFFFFFF` = absent)
- **Header pointer fields are offsets relative to the start of the resource body.** Proven by the
  blend-shape block, which addresses its sub-tables as `bodyStart + offset`. Drive the parse off these
  rather than walking sequentially — meshes with blend shapes have a variable-size block in the middle
  and a sequential walk desyncs at the first one
- MeshHeader **0x60 B**: `+0x00` numParts, `+0x04` numBones, `+0x10`/`+0x20` vertex buffer offsets,
  `+0x30` vertexFormatSize, `+0x38` vertexFormatOffset, `+0x48` numVertices
- VertexFormat: `+0x08` numAttributes, `+0x0A` vertexSizes[2] (stride per buffer), attributes at `+0x10`,
  **8 B each**: `+0x00` nameHash, `+0x04` offset(i16), `+0x06` class, `+0x07` vertexBufferIdx.
  Size is always `0x10 + 8 × numAttributes`
- Attribute hashes: POSITION `0xD2F7D823`, NORMAL `0x36F5E414`, TEXCOORD1 `0x8317902A`,
  SKIN_WEIGHTS `0x48E691C0`, SKIN_INDICES `0x5156D8D3`
- Class → type via the TR11 table (`trmodelcommon.bt`). Class 21 (DEC4N) is **missing from that table
  upstream**; it is 10:10:10:2 normalised
- MeshPart **0x60 B**: `+0x10` firstIndexIdx, `+0x14` numPrimitives, `+0x2C` lodLevel, `+0x30` materialIdx.
  Parts are assigned to meshes in order, each mesh claiming `numParts` consecutive entries
- Indices are **u16 and mesh-local** — rebase them when merging meshes into one LOD

### Not yet decoded
Skeletons/skinning, blend shapes, animations (`.tr11anim`), materials (`.tr11material` — texture
bindings would let the viewer texture SOTR meshes the way it does TLOU2), Wwise `.bnk` stream extraction.
All of these still export as raw bytes.

---

## Test Assets (on Madi's PC)

| Path | Contents |
|---|---|
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-arms.pak` | Ellie arms geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-body.pak` | Ellie body geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-head.pak` | Ellie head geo + embedded texture |
| Installed UE4 games | Jedi Fallen Order, Jedi Survivor (encrypted) |
| Installed UE5 games | Fortnite (encrypted), Hellblade |
| Installed RAGE games | GTA5, Red Dead Redemption 2 |
| Shadow of the Tomb Raider install | `bigfile.000.tiger` + parts — needed to validate the SOTR plugin (never yet run against real data) |
