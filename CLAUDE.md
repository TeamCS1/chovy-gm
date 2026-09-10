# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

chovy-gm ("Chovy-GayMaker") is a decompiled and patched build of YoYoGames' internal GameMaker Asset Compiler v1.0.98, modified to exclusively target the Sony PSP. It takes a standalone GameMaker 8/8.1 executable (a self-contained `.exe` a game was exported to) and recompiles its assets/GML scripts into a PSP-native package, then assembles a bootable PSP/PSVita ISO around it using an unreleased YoYoGames PSP "runner" (from the PSP minis `Karoshi` or `GreenTechPlus`).

This is C#/.NET Framework Windows Forms desktop software, not a web or cross-platform project. It is inherently Windows-only (registry access, WinForms, x86 P/Invoke-adjacent unsafe code).

## Build

Requires Visual Studio 2013 (or a compatible MSBuild with the v12.0 toolset) and the .NET Framework 4.6.1 targeting pack, on Windows.

NuGet packages are already vendored under `packages/` (old-style `packages.config`, not `PackageReference`), so a fresh clone does not need `nuget restore` to build.

Build with MSBuild (VS2013's `MSBuild.exe`, or a modern one — VS2022's works fine despite the `ToolsVersion="12.0"`/`VisualStudioVersion = 12.0` markers in the project files):

```
MSBuild.exe GMAssetCompiler.sln /p:Configuration=Release /p:Platform=x86
```

- Only `Debug|x86` and `Release|x86` configurations exist — there is no AnyCPU or x64 build.
- Output assembly name is `CHOVY-GM.exe` (see `AssemblyName` in `GMAssetCompiler.csproj`), despite the root namespace being `ChovyUI`. Build output lands in `bin\Release\` (or `bin\Debug\`) alongside `Ionic.Zip.Reduced.dll`/`NAudio.dll`.
- There is no test project, no CI configuration, and no lint/formatter config in this repo.
- **`TargetFrameworkVersion` is `v4.7.2`**, not the `v4.6.1` the project originally shipped with. It was bumped because a VS2022 install can have the 4.6.1 targeting pack registered but hollow (IntelliSense XML docs present, actual reference DLLs missing — `Get-ChildItem ...\.NETFramework\v4.6.1 -Filter *.dll` returns nothing), which fails the build with `MSB3644`. 4.7.2/4.8 are drop-in compatible for this codebase and typically have the DLLs present; if `MSB3644` reappears on a different machine, check `.NETFramework\v4.7.2` similarly before assuming a full dev-pack install is required.

## Running

The built `.exe` expects to sit alongside a specific runtime folder layout (shipped in the GitHub release zips, not committed to the repo):

- `RUNNER\` — the PSP ISO skeleton (`PSP_GAME\SYSDIR\{KAROSHI,GREENTECHPLUS}.BIN`, `PARAM.SFO`, etc.) that gets copied and patched per build.
- `IMG\ICON0.PNG`, `IMG\PIC0.PNG` — default PSP icon/pic assets.
- External tools referenced by the README but not part of this source tree: Sony's `umdgenc` (official UMD ISO builder), `at3tool` (WAV → Sony `.at3`), and `fluidsynth` (MIDI → WAV for in-game music).

Launching the exe with no arguments shows the `ChovyUI` dialog first (pick a GM 8/8.1 exe, PSP Title ID, icon/pic, and controller button mapping — persisted to `HKCU\Software\CHOVYProject\Chovy-GM`). Clicking "BUILD ISO" stages `RUNNER\` into `<gmexe dir>\_iso_temp\`, then `Program.Main` re-invokes the compiler in `-c` (compile-only) mode against the chosen exe. CLI options are also parseable directly via `NDesk.Options` (`-?` for the list), but `SetMachineType` currently hardcodes PSP regardless of the `-m` flag passed.

## Architecture

### Compile pipeline (in order)

1. **`Loader`** (`GMAssetCompiler/Loader.cs`) — reads the target file. If it's a GM8.1 exe, it locates and XOR/CRC-decrypts (`CheckFor8_1`/`Process_Encrypt`) the embedded GMK-format asset blob, then parses it into a `GMAssets` graph. It can also re-read an already-compiled `.psp` IFF file chunk-by-chunk for debugging (`LoadPSP`) — several chunk types (`FONT`, `TMLN`) are deliberately skipped there because parsing them crashes on real PSP game files.
2. **GML compilation** — `GMLCompile`/`Lex`/`LexTree`/`GMLToken` tokenize and parse GameMaker Language source pulled from `GMAssets`; `GML2VM` compiles it to the bytecode VM format (`eVM_Instruction`, `VMBuffer`) that the PSP runner interprets. `GML2JavaScript` and `YYObfuscate` are dead code paths inherited from the original HTML5 export target.
3. **Texture packing** — `TexturePage`/`TexturePageEntry`/`Texture`/`Squish` (a DXT compressor port) pack sprite/background bitmaps into fixed-size pages per `IMachineType.TPageWidth/Height` (512×512 for PSP), encoded as DXT (`DDS.cs`).
4. **`IFFSaver`** (`GMAssetCompiler/IFFSaver.cs`) — the actual output writer. Always writes to `<OutputDir>\_iso_temp\PSP_GAME\USRDIR\games\game.psp` regardless of the name passed in — this hardcoded path is what ties compilation to the ISO staging area `ChovyUI` already created.
5. **`UmdGen.UMDGEN`** (`UMDGEN.CS`) — generates the `.ufl`/`.umi` sector-layout manifest files Sony's `umdgenc` needs to actually burn `_iso_temp\` into a bootable ISO. This step is not invoked automatically by this codebase; it's documented as an external post-processing tool.

### The machine-type abstraction is now vestigial

`IMachineType` (`GMAssetCompiler/IMachineType.cs`) and `GMAssetCompiler.Machines\{PSP,Android,IOS,Symbian,Windows,HTML5}.cs` are inherited from when this compiler targeted many GameMaker export platforms. Only `PSP.cs` is exercised — `Program.SetMachineType` force-overwrites any requested machine to `"psp"`. The other four machine classes and their associated savers (`HTML5Saver`, obfuscation, texture-group JS handling in `Program.cs`) are unreachable/untested leftovers; treat them as reference material, not working code paths.

### `GMAC1098/`

A near-complete duplicate copy of the project tree, representing the pre-patch decompiled baseline (GameMaker Asset Compiler v1.0.98 before PSP-specific modifications). It's kept for diffing against the current patched sources (see `GMAC1098/how it should be!.log` / `how it shouldnt be.log`) and is not part of the build — the root `GMAssetCompiler.sln`/`.csproj` only reference the top-level sources.

### `ChovyUI` vs `GMAssetCompiler.Form1`

Two separate WinForms UIs exist: `ChovyUI` (root namespace, `ChovyUI.cs`) is the PSP-packaging front-end shown at startup (title/icon/controller config, "BUILD ISO" button). `GMAssetCompiler.Form1` is the original GameMaker Asset Compiler's own asset-browser/compile UI, still invoked when `Program.Main` runs without `-c`/`CompileOnly`.

### Logging

`GMAssetCompiler.Trace` writes to `TraceGMA.log` next to the executable, gzip-rotating it past 200KB and pruning `.gz` archives older than 30 days.
