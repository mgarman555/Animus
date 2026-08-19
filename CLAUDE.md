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
>
> **Animation foundation (this pass — not yet GUI-checked against real paks):** `JOINT_HIERARCHY` →
> `SkeletonData`, per-submesh skin weights, base `*-skel.pak` discovery, a pose/skinning runtime, a
> viewport transport bar with live CPU skinning, and glTF export of skins + animations. The clip
> KEYFRAME BITSTREAM is the one open piece — see "TLOU2 animation" below.
>
> **Next:** open the app on `actor97/ellie-*.pak` and eyeball ① the textured Ellie (still never
> GUI-checked) and ② the armature overlay + bind pose; then run `tools/nd_anim_probe.py` on a real
> `anim-*.pak` and feed the result back into `NdAnimParser`. After that: the multi-game UI shell
> (`design/ui-mockup.html`) and the RAGE `.ytd` reader.

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
    Animation/SkeletonMath.cs    # Bind pose: local→world, inverse bind, skinning palette
    Animation/AnimationSampler.cs# Clip + time → parent-local pose for a skeleton
    Animation/CpuSkinner.cs      # Linear-blend skinning (WPF has no GPU skinning path)

  App/                           # WPF application (WinExe)
    ViewModels/                  # CommunityToolkit MVVM view-models
    Views/                       # XAML windows and controls
    Views/ObjLoader.cs           # Wavefront OBJ → MeshGeometry3D

  Engines/
    UnrealEngine/                # CUE4Parse wrapper (UE4/UE5 .pak, .utoc/.ucas)
    NaughtyDog/                  # TLOU2 .pak binary parser (custom format)
      NdPakMeshParser.cs         # Geometry decoding (quantised bitstream) + skin hookup
      NdSkeletonParser.cs        # JOINT_HIERARCHY → SkeletonData (bind pose)
      NdSkinParser.cs            # Per-submesh vertex→bone weights
      NdSkeletonLocator.cs       # Finds the shared <name>-skel.pak for a part pak
      NdAnimParser.cs            # ANIM/ANIM_GROUP/ANIM_STREAM discovery + track detection
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
- NaughtyDog pak mesh parsing: quantised continuous-bitstream positions, all submesh groups decode (stride 192), flat LOD grouping via "ShapeN" names
- NaughtyDog per-submesh materials: each submesh resolves its own diffuse/normal texPath from its `m_material` struct
- NaughtyDog per-submesh textures: plugin resolves each submesh's diffuse to full-res via `texturedict3`;
  `SkeletalMeshViewerWindow` decodes + paints each submesh its own `ImageBrush` (PNG-verified; GUI eyeball pending)
- Mesh-level diffuse: VRAM_DESC scan + full-res `texturedict3` hash lookup + BCnEncoder decode + ImageBrush
- NaughtyDog skeletons: `JOINT_HIERARCHY` → bone names, parent links, parent-local bind TRS
- NaughtyDog skinning: per-submesh weight tables (up to 12 influences/vertex), merged per LOD
- Base-skeleton discovery: `ellie-body.pak` → `ellie-skel.pak`; NPCs fall back to a shared rig
  (`base-male-skel.pak`, `base-female-skel.pak`, …) by bone-count coverage + name overlap
- Animation runtime: clip → parent-local pose → world → skinning palette → CPU skinning
- Viewport transport bar: clip picker, play/pause/loop, timeline scrubber, live re-skinning, and a
  real armature overlay (full world transforms, drawn in the current pose)
- glTF export with skins (joints/weights/inverse-bind matrices) and animation channels
- `MeshMerger` carries skin bindings across a merge and remaps bone indices into the merged skeleton

### In Progress
- **TLOU2 animation clip bitstream** — the container and clip naming are exact; the keyframe payload
  is measured rather than read from a spec, because no public decoder exists. See below.
- **Multi-game UI shell + Codex/FModel hybrid** — see `design/ui-mockup.html`. `AssetBrowserView` is single-game;
  the data model already supports many games + a global cross-game search. Add the game rail + cross-game search VM.
- Note: full-res dict textures are LINEAR (no GOB untile); only the 64×64 embedded thumbnails are tiled (per-submesh
  decode in the viewer forces skip-untile for that reason)

### Planned
- FBX model export (CUE4Parse-Conversion)
- Audio playback with waveform (NAudio)
- Cinematic viewer (`CINEMATIC_1` / `CIN_SEQUENCE_1` / `CAMERA_TABLE_1`) — the clip decoder is the
  prerequisite; the resources are already discovered and named by `NdPakReader`
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

### JOINT_HIERARCHY (bind pose)
The payload starts at `payloadStart = pageStart + resItemOffset + ResItemPaddingSz`, exactly like every
other resource. `+0x14` is just the field offset of the joint count inside it — **not** a header this
resource prepends. (Field names below come from the `nd_pak.bt` 010 template; the Noesis walk computes
the same addresses with different names.) So `jointBase = payloadStart + 0x14`:
- `payloadStart+0x00` u32 version · `+0x10` numJSegments · `+0x14` **nodeCount** · `+0x18`/`+0x1C`
  boneCount1/2 (skipped) · `+0x20` **ptr** matsOffset · `+0x28`/`+0x30` ptr (real pointers, never
  dereferenced) · `+0x38` **ptr** namesOffset
- Xform sub-header at `X = matsOffset`: `X+18` u16 xformCount · `X+32` u32 headerSize · `X+60` u32
  hierarchyOffset. Also `X+40` u32 `uknFloatsOffs` → nodeCount × 3×4 float matrices — a **second**
  matrix table nothing reads yet; probe it first if the bind pose ever disagrees with the mesh.
- Transform array at `X + headerSize`, **stride 48**: `+0` float3 scale (+4 pad) · `+16` float4 quat (x,y,z,**w**) · `+32` float3 position (+4 pad)
- **The quaternion must be CONJUGATED** and the transform is **PARENT-LOCAL**, not world. Both are
  proven by the reference's import/export round-trip (`multiplyBones` on load, parent-inverse on save).
  `NdSkeletonParser` still probes both quaternion readings and picks whichever agrees with the mesh's
  own skin weights, so a future build that flips the convention self-corrects instead of silently posing wrong.
- Parenting table at `X + hierarchyOffset + hashesSize` where `hashesSize = u32 @ X + hierarchyOffset + 20`;
  **stride 16**, four **signed** i32: GroupID, **ParentID** (-1 = root), ChildID, ChainID
- Name table at `namesOffset`, **stride 16**: `+0` u64 hash, `+8` u64 name offset **relative to the page
  start** (a raw offset, NOT a pointer fixup)
- **boneMap** = every joint whose parent chain terminates at joint 0. The transform array is indexed by
  **rank within boneMap**, not by global joint index — bone `b`'s matrix is `matrixList[boneMap.IndexOf(b)]`.
  Joints outside boneMap (helpers, `_grp`) get no row; they borrow their chain's transform and are flagged
  `HasBindTransform = false`. Bone INDICES stay global and dense, which is what the skin table's 10-bit
  indices address.
- Bind scale is read but the reference discards it; we keep it only when it is finite and in (1e-4, 100).
- `GlobalScale = 100` in the Noesis plugin scales bones *and* geometry alike, so a port that keeps
  geometry in native units must not apply it.

### Skin weights (per submesh)
The skin-data pointer lives in the SubMeshDesc at **+0x58 or +0x60** — the reference walks a 176-byte
struct where it sits at +0x58, but the real 192-byte layout inserts 8 bytes somewhere between the
material pointer (+0x48, confirmed unshifted) and the count block (+0x88, confirmed shifted).
`NdSkinParser` validates both candidates against the data and uses whichever is self-consistent.
- skinDesc: `+0x04` u32 **total influences for the whole submesh** (summed over every vertex — *not*
  a per-vertex cap; proved by the reference's writer emitting `runningOffset/4` there at L3133).
  Gating on it as if it were a per-vertex maximum rejects every real submesh and loads the character
  unskinned with no error. · `+0x10` **ptr** index map · `+0x18` **ptr** weight blob
- index map: `numVerts × { u32 count, u32 byteOffset }`
- weights at `weightBlob + byteOffset`: **one u32 per influence** — bits 0..21 weight, bits 22..31 bone
  index. (The reference reads this as `readBits(22)` + `readBits(10)`; Noesis's bit reader is LSB-first
  and re-aligns on every seek, so the pair is exactly a little-endian u32 and the blob is 4-byte aligned.)
- Raw weights are normalised per vertex, so no dependence on the encoder's fixed-point scale (the
  encoder uses `int(weight * 4194303)`, i.e. 2²²−1). Bone indices are **global** joint indices, while
  the transform table is indexed by **boneMap rank** — two coexisting index spaces; crossing them gives
  a rig that looks right in bind pose and shears the moment it moves.
- `NdSkinParser` scores both candidate offsets on structural invariants plus corroboration (the `+0x04`
  total matching, the blob being exactly consumed, rows sequential, rows summing to 2²²−1) and takes
  the better one.

### Is the mesh actually on this skeleton?
`SkeletonMath.MeasureRestAgreement` measures the average gap between a vertex and the weight-blended
world-bind position of its driving joints, as a fraction of rig size. A correct pairing reads a few
percent; a wrong-but-large-enough rig reads hundreds. The viewer prints it in the transport bar, since
`NdSkeletonLocator` can only verify a rig has *enough* bones — a different rig of sufficient size
passes that test and puts every weight on the wrong joint. Override the rig from the viewer's Rig
dropdown when the check fails. (The threshold assumes character density; a two-bone prop can read 50%
while being perfectly correct.)

### Base skeletons
Character part paks carry skin weights but **no joints** — the joints live once in
`<world>/actor97/<name>-skel.pak`. `NdSkeletonLocator` resolves them by progressively shorter name
stems (`abby-prisoner-hair` → `abby-prisoner` → `abby`), then falls back to any rig whose bone count
covers the mesh's highest referenced bone index, ranked by shared name tokens. That fallback is what
makes NPCs work: they share `base-male-skel.pak` / `base-female-skel.pak` / `base-teen-skel.pak` / etc.

### TLOU2 animation — what is known and what is not
**Known (exact).** Clips ship in `anim-*.pak`. Resource-type strings are `ANIM`, `ANIM_GROUP` and
`ANIM_STREAM` (confirmed against a reverse-engineered TLOU2 Remastered runtime SDK, where they are
ItemIds 0x29 / 0x2A / 0x33 next to JOINT_HIERARCHY at 0x2B). Cinematics appear as `CINEMATIC_1`,
`CIN_SEQUENCE_1`, `CUTSCENE_DATA` and `CAMERA_TABLE_1`. Each ResItem carries its own name string, so
enumerating and naming clips is exact and needs no format knowledge.

**Not known.** The keyframe payload. There is no public decoder: `fmt_nd_pak.py` registers itself with
`-noanims` and contains no animation code, the `nd_pak.bt` template leaves ANIM_GROUP unparsed, and the
one closed-source tool that emits anims documents its own output as unreliable. Anyone decoding TLOU2
clips is doing original reverse engineering.

**How `NdAnimParser` handles that.** It does not guess offsets — it looks for what a rotation track
provably *is*, at every 4-byte alignment, in two passes:
1. **Uncompressed** — a run of unit-length float4s whose samples move smoothly. Both properties are
   physical invariants of joint animation and neither holds for unrelated bytes.
2. **Quantised** — the "smallest three" packing (2-bit index naming the dropped largest component, then
   three signed fixed-point components) at 48-bit/3×15, 64-bit/3×20 and 32-bit/3×10. These decode to
   unit quaternions by construction, so smoothness is the only evidence and the bar is higher: longer
   minimum runs and a tighter step threshold. The packing that explains the most samples wins.

The same continuity test separates joint-major storage from frame-major. Translation-track candidates
are counted and reported but not decoded — without a known joint ordering an unattributed position
track cannot be assigned to a bone, and guessing would move the wrong joint.

Measured on random data: **zero false positives across 16 MB** for both the uncompressed and quantised
detectors. A 16 MB sweep of all three packings takes ~1.8 s; the scan is capped at 64 MB and logs when
the cap bites. When nothing validates, the parser decodes nothing and reports why, rather than emitting
a plausible pose that would be wrong in the viewport.

**Closing the gap.** Run `python tools/nd_anim_probe.py <anim-*.pak>` on real files. It prints every
resource with its name, annotates each 8-byte header slot (resolvable pointer / float / small int),
reports unit-quaternion runs and a smallest-three compressed-quaternion probe, and gives per-page byte
entropy so packed bitstreams are distinguishable from plain tables. That output is the measurement the
decoder is missing.

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

## Test Assets (on Madi's PC)

| Path | Contents |
|---|---|
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-arms.pak` | Ellie arms geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-body.pak` | Ellie body geo + embedded texture |
| `Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-head.pak` | Ellie head geo + embedded texture |
| Installed UE4 games | Jedi Fallen Order, Jedi Survivor (encrypted) |
| Installed UE5 games | Fortnite (encrypted), Hellblade |
| Installed RAGE games | GTA5, Red Dead Redemption 2 |
