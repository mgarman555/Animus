# Game Asset Explorer — Session Notes (2026-06-06)

Solo Cowork session while Madi was away. Focus per her direction: **UI/UX polish toward an
FModel + Codex/CodeWalker hybrid**, plus **TLOU2 extraction**, prioritizing RAGE / TLOU2 / Jedi.

---

## What was decided / confirmed

- The codebase is further along than the skill doc assumed: real C# .NET 8 WPF app with a clean
  `IGameEngine` / `IExporter` plugin model and a unified `AssetInfo` / `AssetData` type system that
  already covers every asset type Madi listed (mesh, skeletal mesh, **armature/parent connector**,
  texture, animation, audio, material, level).
- Four engine plugins exist: **UnrealEngine** (CUE4Parse, working), **NaughtyDog** (TLOU2 .pak,
  substantial), **RageEngine** (GTA5/RDR2 RPF, mostly stub), **SotrEngine** (Tomb Raider, present).
- The existing UI is already FModel-shaped (3-panel: tree / grouped list / preview+info, plus a
  merge queue). The redesign is polish + a multi-game shell + Codex-style modes, not a rewrite.

## What was verified against REAL data

Wrote an independent Python validator (`tools/nd_pak_probe.py`) and ran it on the Ellie test paks.
It confirms the C# `NdPakReader` / VRAM_DESC assumptions are correct on real bytes:

| Pak | magic | game | GEOMETRY_1 | submeshes | VRAM_DESC | diffuse fmt |
|-----|-------|------|-----------|-----------|-----------|-------------|
| ellie-head | 0xA79 | TLOU2 | yes | 30 | 192 | BC7 |
| ellie-body | 0xA79 | TLOU2 | yes | 15 | 106 | BC1 + BC5 normal |
| ellie-arms | 0xA79 | TLOU2 | yes | 5  | 62  | BC7 |

Confirmed the texture-in-viewer path end to end:
- VRAM_DESC offsets (+40 pakOffset, +48 size, +56 hash, +72 fmt, +80 mips, +84 w, +88 h, +112 texPath)
  all decode to sane values.
- Embedded textures really are only **64×64 thumbnails** (a few 128×64 / 64×128) — confirms the note
  that full-res must come from `texturedict3/`.
- `texturedict3/common-dict.pak` (2.0 GB, magic 0x10A79) is present and keyed by the same hashes,
  so **full-res diffuse/normal/ORM lookup by hash is viable**.

## Delivered this session

1. **`design/ui-mockup.html`** — interactive, single-file concept of the merged explorer, using the
   exact App.xaml theme tokens so it ports straight to WPF. Click-through, populated with real assets
   from these files. Demonstrates:
   - **Game rail** (multi-game shell) with engine badges — UE / ND / RAGE.
   - **Global search across all mounted games** + per-type filter chips.
   - **FModel feel**: virtual directory tree, asset grid/list toggle, properties + JSON sidecar preview.
   - **Codex/CodeWalker feel**: engine-aware inspector — RAGE shows .ytd dicts, .ymap placement,
     .ybn collision, .ycd clips; ND shows submeshes/LOD/VRAM/skeleton; UE shows material slots/sockets.
   - **Parent connectors** surfaced as a first-class type (armature/skeleton drives head+body+arms).
   - **Merge queue** + normalized export panel (FBX Y-up/cm, ORM unpacked, JSON sidecar, Unreal layout).
2. **`tools/nd_pak_probe.py`** — working TLOU2 pak inspector (verified against real paks). Useful as a
   ground-truth diff against the C# parser and Noesis fmt_nd_pak.py.

## UPDATE — working extractor built (Python allowed)

Once Python/C++ was greenlit, I stopped speccing and built it. `tools/nd_texture_extract.py` is a
**working, verified TLOU2 texture extractor** that mirrors `NdTextureDictionary.cs`:

- actor pak → VRAM_DESC → texPath → hash → look up in `texturedict3/common-dict*.pak` → decode → PNG.
- `common-dict.pak` has only ~1.5 MB of structural metadata before its 2 GB blob, so indexing is cheap
  (3,233 dict entries indexed in seconds); we then seek straight to a texture and read just its bytes.
- Confirmed the dict textures are **linear** (no GPU tiling), unlike the 64px embedded thumbnails.
- Diffuse vs normal vs AO is auto-classified from the texPath ("-color" / "normal" / "nao").

**Result (real output):** Ellie body diffuse, 64×64 thumbnail → **full-res 1024×1024 BC1**, decoded to a
PNG that is visibly her red plaid flannel UV atlas. Decode path validated on BC1 (BC5/BC7 wired too).
Sample PNGs in `/outputs/tlou2_textures/`.

This Python tool is now the reference implementation for the C# texture-in-viewer feature: the offsets,
hash lookup, format priority (BC7>BC5>BC4>BC1) and linear layout are all proven against real bytes.

Next on this thread: port the same decode into `SkeletalMeshViewerWindow` (BCnEncoder) as confirmed,
and build the geometry decoder (quantised continuous bitstream) the same way — Python first, validate
against the Ellie paks, then port to C#.

## Open / next steps (in priority order)

1. **Review the mockup** and mark what lands vs. what to change before I port it into XAML.
2. **AssetBrowserView → multi-game shell**: add the game rail + global cross-game search VM. Today the
   browser is single-game; the model already supports many.
3. **TLOU2 texture-in-viewer (buildable now)**: wire VRAM_DESC → pick BC7 diffuse → untile 64² thumb →
   BCnEncoder decode → ImageBrush. Then full-res via common-dict.pak hash lookup. Offsets confirmed.
4. **RAGE engine**: biggest functional gap. .ytd texture-dict reader is the smallest first win
   (loose .ytd files are on disk to test against); .ymap stays on the CodeWalker→Blender path.

## Constraints hit (so the next session knows)

- This sandbox is Linux with no .NET and no network to Microsoft, so the WPF app can't be compiled or
  run here. Verification was done by re-implementing the binary format in Python against real data.
  C#-level compile/run testing has to happen on Madi's Windows machine.
