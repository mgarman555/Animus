# GameAssetExplorer — Claude Code Guide

Educational game asset explorer (.exe) for browsing, previewing, and exporting assets from multiple game engines. Personal tool for Madi's extract → Blender → reimport-into-UE pipeline.

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
- NaughtyDog pak mesh parsing: quantised continuous-bitstream positions, flat LOD grouping via "ShapeN" names

### In Progress
- **Texture display in 3D mesh viewer** (highest priority)
  - `MeshAssetData` already has `DiffuseTextureData/Width/Height/Format` fields
  - Need: VRAM_DESC scan in `NdPakMeshParser.cs` → populate those fields
  - Need: `SkeletalMeshViewerWindow` decode (BCnEncoder) + apply as `ImageBrush`
  - Note: embedded textures are 64×64 GPU-tiled thumbnails (NVidia "1D thin"); untiling required before BC decode

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
- SMD array pointer at `ghOff+40` (fixup → absolute); stride = **176 bytes**
- Key SMD field offsets: `+0x20` namePtr, `+0x30` streamDescPtr, `+0x40` indexPtr, `+0x88` numVerts, `+0x8C` numIdx, `+0x90` numStreams
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
