# GameAssetExplorer — Handoff to Claude Code

This file is the entry point for continuing the project in **Claude Code** on the Windows machine.
Read `CLAUDE.md` (project guide) and `design/SESSION_NOTES.md` (full Cowork session log) alongside it.

---

## TL;DR of where things stand

- The app is a working C# .NET 8 WPF multi-game asset explorer (UE / TLOU2 / RAGE / SOTR plugins).
- During a Cowork session, the **TLOU2 extraction path was fully reverse-engineered and proven in
  Python** against the real Ellie paks: texture decode (all formats) and geometry decode + multi-part
  actor merge both work end to end. Those Python tools live in `tools/` and are the reference
  implementations to port into the C# engine.
- One real **bug** was found in the C# parser (SMD stride) — details below.

---

## What's verified working (Python reference tools in `tools/`)

| Tool | What it does | Status |
|------|--------------|--------|
| `nd_pak_probe.py` | Dumps ND .pak structure (pages, fixups, ResItems, VRAM_DESC) | Verified vs real paks |
| `nd_texture_extract.py` | actor pak hash → `texturedict3/common-dict*.pak` → BC decode → PNG | 100% coverage, all formats |
| `nd_mesh_extract.py` | GEOMETRY_1 → submeshes → quantised geometry + UV → merged OBJ | Full Ellie merged (49,119 v) |

Run examples (need: `pip install numpy pillow texture2ddecoder`):
```bash
python tools/nd_pak_probe.py
python tools/nd_texture_extract.py  "<...>/actor97/ellie-body.pak"
python tools/nd_mesh_extract.py     "<...>/ellie-head.pak" "<...>/ellie-body.pak" "<...>/ellie-arms.pak"
```

## BUG to fix in C# (high priority, quick)

`src/Engines/NaughtyDog/NdPakMeshParser.cs` line ~40: `const int SMD_STRIDE = 176;`
**Correct value is 192.** At 176 the parser only catches submesh 0 plus one coincidental alignment,
so multi-group assets (the whole body, head, etc.) come out nearly empty. Verified 192 by scanning
contiguous valid SubMeshDesc signatures across ellie-body/head/arms. Change to 192 and re-test.
(LOD is the digit in the `LODShape<N>` token; submesh names are `<part>_lod0_LODShape<LOD>_shader<N>`.)

## Prioritized next tasks

1. **Port the stride fix (176→192) into `NdPakMeshParser.cs`** and confirm body/head now decode all groups.
2. **MATERIAL_TABLE_1 parsing** — assign the correct texture per submesh (a body = tank top + pants +
   shoes, each its own material). Today the Python exporter bakes one diffuse per part as a placeholder.
   This is what turns "correct geometry, approximate texturing" into a fully textured Ellie.
3. **Port the texture path** (`nd_texture_extract.py`) into `SkeletalMeshViewerWindow` /
   `NdTextureDictionary.cs`: full-res lookup is linear (no untile) — confirmed.
4. **UI: multi-game shell + Codex/FModel hybrid** — see `design/ui-mockup.html` (interactive concept,
   uses the real App.xaml theme tokens). `AssetBrowserView` is currently single-game; the data model
   already supports many games + a global cross-game search.
5. **RAGE engine** (.ytd texture-dict reader first — loose .ytd test files are on disk).

## Git / GitHub

History was committed in the Cowork sandbox (the host mount can't host a live `.git`). Full history is
in `handoff/GameAssetExplorer.bundle`. To restore and connect a GitHub remote on Windows:

```bash
cd C:\Users\madie\Desktop\Personal\GameAssetExplorer
git clone handoff\GameAssetExplorer.bundle .tmprepo
move .tmprepo\.git .git
rmdir /s /q .tmprepo
git checkout -- .
git remote add origin https://github.com/<you>/GameAssetExplorer.git
git push -u origin master
```
`handoff/PUSH_INSTRUCTIONS.md` has the same steps. Two commits so far:
baseline, then the geometry decoder + stride fix + session notes.

## Test assets (on this machine)

`Desktop\Game Assets\TLOU2\common_unpacked\actor97\ellie-{head,body,arms,...}.pak` (raw ND paks),
`...\texturedict3\common-dict*.pak` (full-res textures), `GTA 5\*.ytd` (RAGE), `Jedi Survivor\` (FModel export).
