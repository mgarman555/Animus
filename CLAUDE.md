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
> **Next (TLOU2):** open the app and eyeball the textured Ellie in the 3D viewer (only thing not yet
> GUI-checked), then the multi-game UI shell (`design/ui-mockup.html`).
>
> **Next (RAGE, active):** the RPF7 container layer is written and cross-checked but has never run
> against a real archive. Run `tools/rage_rpf_probe.py --scan-dir` on the GTA V install first — it is
> an independent Python implementation of the same format and it answers, in one pass, which edition
> is installed, which encryption the archives use, and whether the entry counts are sane. Then build
> the C# and confirm it agrees. Only after that does .ytd / .ydr / .ymap work make sense.

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
    RageEngine/                  # GTA V (RPF7). RDR2 is NOT supported — see note below
      RpfArchive.cs              # RPF7 container: TOC, entries, nested archives, deflate
      GtaCrypto.cs               # AES + NG block ciphers, joaat + GTA5Hash (MIT, see third_party/)
      GtaKeys.cs                 # loads a CodeWalker key dump (four .dat files)

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
- RAGE RPF7 container: correct magic, TOC decrypt (AES + NG), all three entry kinds, name table,
  directory tree, raw deflate, nested archives via `StartPos`. Cross-checked against a literal
  transcription of CodeWalker and against a synthetic archive (`tools/test_*.py`). **Not yet run
  against real GTA V bytes.**

### In Progress
- **GTA V map region export (active, one-week target)** — goal is a Del Perro Pier / Vespucci Beach
  blockout in UE5 for the First Days opening. Architecture: GAE owns the container and the placement
  layer, exports one asset per unique archetype plus a `region.json` placement manifest, and a UE
  Python script spawns instanced components from it. Explicitly NOT one merged static mesh — the
  point is to keep every asset swappable. Next steps in order: run the Python probe against the real
  install, build and diff the C#, then `gta5_cache_y.dat` region query, then ymap/ytyp parsing.
- **Multi-game UI shell + Codex/FModel hybrid** — see `design/ui-mockup.html`. `AssetBrowserView` is single-game;
  the data model already supports many games + a global cross-game search. Add the game rail + cross-game search VM.
- Note: full-res dict textures are LINEAR (no GOB untile); only the 64×64 embedded thumbnails are tiled (per-submesh
  decode in the viewer forces skip-untile for that reason)

### Planned
- FBX model export (CUE4Parse-Conversion)
- Audio playback with waveform (NAudio)
- Animation viewer with timeline scrubber
- RAGE: .ytd texture dictionary reader (smallest next win — BC formats GAE already decodes, no GPU
  tiling on PC, but note Stride is READ from the file at +0x56 on Legacy, not computed)
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

## GTA V / RAGE RPF7 — Key Facts (verified against the format, not yet against her install)

### Scope
GTA V (PC) only. **RDR2 is not supported and the code no longer claims it.** RPF8 has a different
header (decryption tag, platform id, 256-byte RSA signature), 24-byte entries, hash-only filenames,
and a cipher called TFIT whose keys no public tool holds. CodeWalker has zero RPF8 support either.

### Container
- Header at the archive's `StartPos`: `u32 Version` (must read **0x52504637**; bytes on disk are
  `37 46 50 52`), `u32 EntryCount`, `u32 NamesLength`, `u32 Encryption`
- Encryption: `NONE=0`, `OPEN=0x4E45504F`, `AES=0x0FFFFFF9`, `NG=0x0FEFFFFF`. NG shipped with the
  2015 PC port — it has nothing to do with the Enhanced edition
- Then `EntryCount * 16` bytes of entries, then `NamesLength` bytes of names. Both are encrypted
  together as one run
- Entry kind is decided by the **second** u32 (entry +4): `== 0x7FFFFF00` directory,
  `(x & 0x80000000) == 0` binary file, otherwise resource file
- Directory entry: `u32 nameOffset`, `u32 0x7FFFFF00`, `u32 entriesIndex`, `u32 entriesCount` —
  indices are into the single flat entry list in TOC order
- Binary entry: one packed `u64` at +0 (`nameOffset = buf & 0xFFFF`, `size = (buf>>16) & 0xFFFFFF`,
  `offset = (buf>>40) & 0xFFFFFF` in 512-byte sectors), `u32 uncompressedSize` at +8,
  `u32 encryptionType` at +12 (0 or 1 only). `size == 0` means stored, `size > 0` means raw DEFLATE
- Resource entry: `u16 nameOffset` at +0, 3-byte size at +2, 3-byte offset at +5 masked `& 0x7FFFFF`,
  `u32 systemFlags` at +8, `u32 graphicsFlags` at +12. `size == 0xFFFFFF` is a **sentinel**: the real
  size is smeared across the first 16 payload bytes as `b[7] | b[14]<<8 | b[5]<<16 | b[2]<<24`
- Body offset is `StartPos + fileOffset * 512`, **plus 0x10 for resources only** (skipping the RSC7
  header, whose flags are already in the TOC). Binary entries take no header skip
- Page flags decode to a byte size via `0x200 << (flags & 0xF)` times a page count assembled from
  scattered bit groups; resource version is `((sys>>28)&0xF)<<4 | ((gfx>>28)&0xF)`
- **Nested archives are the whole ballgame.** Essentially all map data lives in RPFs stored as binary
  entries inside other RPFs. A child's `StartPos = parent.StartPos + entry.FileOffset * 512`, and its
  own `Name` and size are the NG key-derivation inputs for its own TOC

### Encryption
- **NG is not AES and shares no code with it.** 17 rounds of a table-driven SP network: RoundA for
  rounds 0, 1 and 16, RoundB for 2..15, keyed by 101 x 272-byte subkeys and 17 x 16 x 256 uint32
  tables. Any trailing partial block passes through in plaintext
- Per-file subkey index is `(GTA5Hash(name) + length + (101 - 40)) % 101`. **`GTA5Hash` is not
  joaat** — it is LUT-substituted (`temp = 1025 * (LUT[c] + result); result = (temp>>6) ^ temp`,
  returning `32769 * ((9*result >> 11) ^ 9*result)`) and the name is **not lowercased**
- Four key artifacts are needed, not three. The 256-byte hash LUT is the one people omit, and without
  it every TOC decrypts to noise with no other symptom. Get them by running CodeWalker once and
  saving its keys: `gtav_aes_key.dat` (32 B), `gtav_ng_key.dat` (101x272), `gtav_ng_decrypt_tables.dat`
  (17x16x256x4), `gtav_hash_lut.dat` (256 B). Drop them in `%AppData%\GameAssetExplorer\RageKeys`
- **Only tables of contents are encrypted.** Asset bodies are plaintext — CodeWalker sets
  `IsEncrypted` for `.ysc` scripts alone. Once a TOC opens, .ydr/.ytd/.ymap need deflate and nothing
  else. There is no AES key you can paste into a settings box; that setting has been removed

### Region selection (next task, not yet built)
- `update\update.rpf\common\data\gta5_cache_y.dat` is a precomputed spatial index over every ymap
  in the game: flat 64-byte records with name, parent name, content flags and both extents. One AABB
  pass gives the exact cell list with LOD parent links. Do not guess ymap names
- Del Perro / Vespucci live in `_cityw/venice_01` (prefix `vb_`) and `_cityw/santamon_01` (prefix
  `sm_`), both inside `x64m.rpf`. `bh1_` is Rockford Hills, not the beach
- Del Perro Pier runs southwest from about (-1660,-1120) to (-1885,-1225), lower deck z ~ 8.5, upper
  z ~ 12.9. Ferris Whale at (-1663.97, -1126.74, 29.32). Sea level is z = 0.0 across the region.
  Note it is ~248 m long against the real Santa Monica Pier's ~500 m — a caricature, not a model

### Editions
The container half (RPF, NG, RSC7, ymap, ytyp) is identical between Legacy and Enhanced. Drawable and
texture internals are not: `Texture` 144 -> 128 bytes, `VertexBuffer` 128 -> 64 with a 320-byte Gen9
declaration, and the vertex buffer must be structurally rebuilt because component ordering changed.
A Legacy-targeted reader on an Enhanced install mounts fine, lists fine, reads placements fine, then
produces garbage geometry. Establish which edition is installed before writing any drawable code.

### Validation pattern
Same as the TLOU2 work: Python first, against real bytes, then port. `tools/rage_rpf_probe.py` is an
independent implementation of everything above; `tools/test_rage_crypto.py` differentially tests the
NG cipher against a literal CodeWalker transcription (2000 random blocks); `tools/test_rpf_roundtrip.py`
builds a synthetic RPF7 with a nested archive and reads it back. All three pass. None of them has seen
a real GTA V archive yet, which is the single next thing to do.

## Test Assets (on Madi's PC)

| Path | Contents |
|---|---|
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-arms.pak` | Ellie arms geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-body.pak` | Ellie body geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-head.pak` | Ellie head geo + embedded texture |
| Installed UE4 games | Jedi Fallen Order, Jedi Survivor (encrypted) |
| Installed UE5 games | Fortnite (encrypted), Hellblade |
| Installed RAGE games | GTA5, Red Dead Redemption 2 |
