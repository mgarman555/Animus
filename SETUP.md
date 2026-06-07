# GameAssetExplorer — Setup Guide

## What You Need

- **Visual Studio 2022** (Community edition is free)
  - Install with the **.NET Desktop Development** workload checked
  - This gives you WPF, C# 12, and .NET 8 support
- **.NET 8 SDK** (included with Visual Studio if you select the workload above)
- **Git** (optional, for version control)

## Opening the Project

1. Open Visual Studio 2022
2. Click **Open a project or solution**
3. Navigate to this folder and select `GameAssetExplorer.sln`
4. Visual Studio will load all 6 projects in the solution

## Restoring NuGet Packages

On first open, Visual Studio will automatically restore all NuGet packages.
If it doesn't, right-click the solution in Solution Explorer → **Restore NuGet Packages**.

The key packages being installed:
- **CUE4Parse** — Unreal Engine file parsing (same library FModel uses)
- **CUE4Parse-Conversion** — Converts UE assets to standard formats
- **SkiaSharp** — Image processing and PNG export
- **BCnEncoder.NET** — Decodes DXT/BC block-compressed textures
- **HelixToolkit.Wpf** — 3D viewport
- **NAudio** — Audio playback
- **CommunityToolkit.Mvvm** — MVVM bindings

## Building

Set the startup project to **App** (right-click App → Set as Startup Project), then press **F5** to build and run.

## Project Structure

```
GameAssetExplorer/
├── src/
│   ├── Core/               → Shared interfaces & data models (no UI, no engine code)
│   ├── App/                → WPF desktop application (UI layer)
│   ├── Engines/
│   │   └── UnrealEngine/   → CUE4Parse-based UE4/UE5 parser
│   └── Exporters/
│       ├── TextureExporter/    → PNG output for textures
│       └── MetadataExporter/   → JSON sidecar files for all assets
```

## Adding a Game (Jedi Survivor)

When the app launches, click **Add Game** on the home screen and fill in:

| Field | Value |
|-------|-------|
| Display Name | Star Wars Jedi: Survivor |
| Game Directory | `C:\...\Jedi Survivor\` (your install path) |
| Engine | Unreal Engine |
| Engine Version | 4.27 |
| AES Key | (see below) |

**AES Key for Jedi Survivor:**
Jedi Survivor's pak files are encrypted. You'll need the AES key.
The key can be found on the FModel AES Keys community list or by using UE4SS.
Format: `0x` followed by 64 hex characters.

## What's Working in This Build

- [x] Game library home screen with enable/disable toggles
- [x] Add/configure game entries with full metadata
- [x] Auto-detect Unreal Engine from game directory
- [x] Mount UE4/UE5 .pak and .utoc/.ucas archives
- [x] AES decryption for encrypted archives
- [x] Full virtual file tree browsing
- [x] Asset preview loading
- [x] Texture export to PNG (with BC block decode)
- [x] Full JSON metadata export with all properties

## Coming Next

- [ ] Model export to FBX with skeleton data
- [ ] Audio playback with waveform display
- [ ] Animation viewer with timeline scrubber
- [ ] WPF UI polish (the ViewModels are ready, Views need XAML)
- [ ] Model export to FBX
- [ ] RAGE engine plugin (GTA5, RDR2)

## Important Notes on the Blender → Unreal Pipeline

The FBX exporter (coming next) is configured with:
- **Scale**: 1.0 by default (Unreal's centimeter scale)
- **Bone axis correction**: On by default (fixes the Blender bone orientation issue)
- **Material slot names preserved**: Always on

When importing FBX into Blender: set Import Scale to 0.01 to convert cm → m.
When reimporting into Unreal from Blender: export at scale 1.0 from Blender,
import with "Convert Scene" off in Unreal.

The JSON sidecar file contains the original virtual path, material slot names,
and LOD data — this is what the future Blender addon will read to auto-assign materials.
