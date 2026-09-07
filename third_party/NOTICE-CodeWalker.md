# Third-party attribution: CodeWalker

Parts of `src/Engines/RageEngine/` are derived from **CodeWalker** by dexyfex
(https://github.com/dexyfex/CodeWalker), used here as a format reference for GTA V's RPF7
container.

## What was taken, and under what terms

**`GtaCrypto.cs`** is a port of `CodeWalker.Core/GameFiles/Utils/GTACrypto.cs` and the `GTA5Hash`
class from `GTAKeys.cs`. Both carry this header in the original source:

> Copyright(c) 2015 Neodymium
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software
> and associated documentation files (the "Software"), to deal in the Software without
> restriction, including without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
> Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
> BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

That is MIT, so the port is clean provided this notice travels with it.

**`RpfArchive.cs`** was written against the byte layouts in `CodeWalker.Core/GameFiles/RpfFile.cs`.
That file carries **no licence header**. CodeWalker's repository has no `LICENSE` file;
`Readme_Src.txt` states "This source code is released for educational purposes only" and
`Notice.txt` is a bare `Copyright(c) 2017-2019 dexyfex` with no grant. Our implementation is an
independent restatement of the format rather than a copy of that file's code, but the format
knowledge came from reading it and that is worth being honest about.

## Constraints this places on the project

GameAssetExplorer is private. If any part of it is ever published:

1. **The RAGE plugin does not go with it.** It is isolated in its own assembly
   (`GameAssetExplorer.RageEngine`) precisely so it can be dropped from a public build without
   touching anything else. This is a deliberate structural decision, not an accident of layout.
2. If the RAGE plugin were ever to be published, `GtaCrypto.cs` is fine to ship with this notice
   attached; `RpfArchive.cs` would need dexyfex's permission or a clean-room rewrite from public
   format documentation.
3. **Do not vendor `CodeWalker.Core` wholesale.** Beyond the unlicensed files, it contains GPL-3
   code (`Utils/Fbx.cs`, `Utils/FbxConverter.cs`, from hamish-milne/FbxWriter), which would pull
   copyleft into this codebase.

## What was NOT taken

No key material. `GtaKeys.cs` reads a key set the user produces themselves from their own copy of
the game; nothing derived from `gta5.exe` is stored in this repository.

CodeX / CodeX.Explorer contributed nothing here. Its source is distributed to Patreon supporters
via Discord and `CodeX.Core` is not publicly available, so it could not be used as a reference
even where it would have been the better one.
