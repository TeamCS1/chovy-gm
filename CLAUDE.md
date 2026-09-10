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

**Headless mode** (added on top of the original tool, for scripted/CI-less testing without the WinForms dialog):

```
CHOVY-GM.exe --headless <gm81.exe> <output dir> <RUNNER template dir> [titleID]
```

This replicates exactly what `ChovyUI.BuildISO_Click` + compile-only mode do (stage `RUNNER\` into `<output dir>\_iso_temp\`, select the `KAROSHI.BIN` runner as `EBOOT.BIN`, compile), without needing icon/pic files or a GUI session. Default `titleID` is `TEST00000` if omitted. See `Program.Main`'s `--headless` branch.

**Native DLLs required at runtime, not restored by NuGet or committed to this repo** — copy them into `bin\Release\` (or wherever the exe runs from) from a release zip / real PSP toolchain before compiling anything for real:
- `squish.dll` — the DXT texture compressor `Squish.cs` P/Invokes into. Without it, compilation dies with `DllNotFoundException` right at the texture-writing stage.
- `PVRTexLib.dll` — `squish.dll`'s own dependency (texture format conversion backend).
- `msvcr90.dll` (VC++ 2008 CRT) — also a `squish.dll` dependency. It's commonly present system-wide only as a WinSxS side-by-side assembly (`C:\Windows\WinSxS\x86_microsoft.vc90.crt_*`), and `squish.dll` apparently lacks the manifest needed to bind to it there — a private/loose copy next to the exe is the reliable fix (`Microsoft.VCRedist.2008.x86` via winget installs the WinSxS assembly to copy from).

Without these three, compilation gets all the way through asset/audio parsing and dies specifically at DXT texture compression.

If the source game has MIDI music, two more externals are needed to convert it (`fluidsynth.exe`, `gm.sf2`, plus `fluidsynth`'s own DLLs: `libfluidsynth-2.dll`, `libglib-2.0-0.dll`, `libgobject-2.0-0.dll`, `libgthread-2.0-0.dll`, `libinstpatch-0.dll`, `libsndfile-1.dll`) — all present in the release zips, same story as the texture DLLs above. Without them, compilation fails specifically at the `Converting ... To .WAV` step for any `.mid` audio resource; games with only `.wav` sounds don't need this.

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

## The PSP runner (`KAROSHI.BIN`/`GREENTECHPLUS.BIN`) — reverse-engineering notes

These are not homebrew ELFs; they're Sony's standard signed/encrypted module format (`~PSP` header — module name embedded at offset `0x0C` literally reads `"Runner"`). This is a separate, PSP-hardware-level encryption layer (KIRK engine, tag-keyed) from the PSN PKG/RIF licensing used only for the original store *distribution* of the Minis — since these files are already-extracted, post-install EBOOTs, only the module encryption applies.

**Decrypting them** doesn't require real hardware: [`John-K/pspdecrypt`](https://github.com/John-K/pspdecrypt) (GPLv3, decryption code lifted from PPSSPP itself, MIT/public-domain `libkirk` crypto with the historically-recovered PSP root keys) decrypts both files offline. Its `pspDecryptPRX()` in `PrxDecrypter.cpp` + `libkirk/*.c` is fully self-contained (no OpenSSL/zlib needed — those are only pulled in by the unrelated PSAR/firmware-updater code paths in that repo). Both `KAROSHI.BIN` and `GREENTECHPLUS.BIN` decrypt cleanly under the same signing tag (`D9160BF0`), confirming it's a publicly-known key, not something exotic. Output is a plain 32-bit MIPS ELF.

**Testing the decrypted runner in PPSSPP:**
- PPSSPP flatly rejects the *encrypted* file regardless of naming/extension tricks (`Identify_File` in `Core/Loaders.cpp` never gets a chance to accept it) — decrypt first.
- Boot the **`_iso_temp` folder itself**, not the loose `EBOOT.BIN` file directly. Pointing PPSSPP at the bare EBOOT file skips the `disc0:` virtual filesystem mount, so the runner's own `sceIoOpen("disc0:/PSP_GAME/USRDIR/games/game.psp", ...)` calls fail with `NODEV` and it never sees your compiled data at all, even though the file is sitting right there. Booting the folder (which PPSSPP recognizes as `PSP_DISC_DIRECTORY` since it contains a `PSP_GAME` subfolder) mounts `disc0:` properly.
- Use `CPUCore = 0` (Interpreter) in `ppsspp.ini`, not the default JIT. Under JIT the runner segfaults PPSSPP itself (host-level `0xc0000005`, confirmed via Windows Event Log) partway through generic PSP SDK init code — this reproduces with *just the bare runner*, no GameMaker content involved, so it's a PPSSPP JIT-recompiler bug tickled by this old/unusual code, not a bug in the runner or in compiled output. The interpreter runs the identical code without crashing (just much slower).

### The pipeline works end-to-end — confirmed

**A real GM8.1 game, compiled by this tool, boots and plays as an actual running game** through the decrypted Karoshi runner in PPSSPP. This was confirmed with a second, smaller test game (`neonquest.exe`, ~6.8MB) after an initial test game (`Pillar of Autumn V1.exe`, ~37MB) reproducibly got stuck at the splash screen and exited instead of starting. That first result briefly looked like it might be a fundamental problem with the runner or pipeline (see reverse-engineering notes below); the second game proved otherwise. **Treat any single-game failure as game-specific until proven otherwise — always cross-check against a second, different compiled game before suspecting the compiler or runner.**

### Reverse-engineering toolchain for the runner (Ghidra, headless)

Standalone Ghidra doesn't understand the PSP's ELF flavor (custom relocation types, NID-keyed import table) out of the box. The working setup:
- [`kotcrab/ghidra-allegrex`](https://github.com/kotcrab/ghidra-allegrex) — Ghidra processor/loader extension adding the Allegrex CPU and PSP ELF support. Install by extracting its release zip into `<ghidra install>/Ghidra/Extensions/`. Import with language `Allegrex:LE:32:default`; Ghidra then auto-detects the loader as "PSP Executable (ELF)".
- [`pspdev/psp-ghidra-scripts`](https://github.com/pspdev/psp-ghidra-scripts)' `SonyPSPResolveNIDs.py` resolves the NID-keyed import table to real function names (`sceKernelExitGame` etc.) using its bundled `ppsspp_niddb*.xml`. **It's a Jython script and Ghidra 12.x dropped Jython for PyGhidra** — running it needs either a working PyGhidra install (its `pyghidra_launcher.py` will try to `pip install` a JPype wheel that has no prebuilt binary for a very new Python, then falls back to compiling it, which needs MSVC build tools — a heavy, avoidable detour) or a **from-scratch Java port** of the same logic, which is what actually got used here: a `GhidraScript` subclass parsing `PspModuleImport`/`PspModuleInfo` via `CParser`, reading the same NID XML with `javax.xml.parsers`, calling the same `createFunction`/`createLabel` flat-API methods. Entirely scriptable via `support/analyzeHeadless <project dir> <name> -import <elf> -processor Allegrex:LE:32:default -scriptPath <dir> -postScript <Script>.java` — no GUI needed for any of this.
- **Correlating a Ghidra (static) address to a live PPSSPP address**: they differ by a constant offset, but it is *not* safely guessable (image base 0x08800000 is common lore but not exact here) and PPSSPP's own thread-entry-point column in its Threads panel is *also* not the right reference (it's a specific thread's start function, not the module's). The one reliable way: find one instruction Ghidra resolved to a named import call (e.g. a `jal sceKernelExitGame`), find the *same* call live in PPSSPP's disassembly (matching the surrounding instructions, not just the address), and subtract. Cross-check against a second independent instruction match before trusting the offset — two wrong guesses preceded the correct one in this session. A correct offset should also satisfy: `live_root_thread_entry - offset == e_entry` read straight from the raw ELF header (`Elf32_Ehdr.e_entry` at file offset `0x18`).
- PPSSPP's own "Save .sym file" (Debug menu) reliably crashes it when used against this runner, timing-independent — don't rely on it; the Java NID-resolution route above is the practical path to readable names.

### What's actually still open

With the pipeline confirmed working via `neonquest.exe`, the `Pillar of Autumn V1.exe` splash-then-exit is now a narrower, single-game compatibility question, not a pipeline bug. What's been ruled out for it via static analysis of `karoshi_decrypted.elf` (all function addresses below are Ghidra/static, not live):
- Not a PSN/DRM check — `FUN_000ce54c` (the post-load "gate" `main()` checks) has no conditional logic at all, just ~13 fixed subsystem-init calls and an unconditional `return 1`.
- Not a chunk-parsing rejection — `FUN_000c5790`'s chunk dispatch recognizes every chunk tag this compiler writes (`GEN8`, `OPTN`, `SPRT`, `SOND`, `BGND`, `PATH`, `SCPT`, `FONT`, `TMLN`, `OBJT`, `ROOM`, `DAFL`, `TPAG`, `STRG`, `TXTR`, `AUDO`); the `FORM` magic and declared size field in the real compiled `game.psp` both check out exactly against the actual file size.
- Not a GML compile error — object actions, timeline actions, and scripts all funnel through the same `FUN_00005284` → `FUN_000146a4` "compilation error" path (confirmed by decompiling `FUN_0009c6d4`, the script-compile function, which calls the identical `FUN_00005284`). A live breakpoint at `FUN_000146a4`'s entry across one full boot-to-exit run never fired — nothing anywhere in this game's GML fails to compile.
- Not the graphics-mode gate in `FUN_0006f004` — it checks the display's reported color depth against 16/32-bit, but the query function `FUN_000d4a54` is hardcoded to always return `0x20`, so that check can never fail.
- Real, substantial GML source text is genuinely embedded and well-formed in the compiled `game.psp` (verified via `grep -a` for game-specific object names and GM8 syntax patterns like `with (`/`global.` — not generic placeholders, not the empty-code fallback).

Leading open theory: something in `Pillar of Autumn`'s specific asset content (not its GML) trips a failure path not yet traced — e.g. a room/object/resource edge case somewhere in the untraced portions of `FUN_00083a00`/`FUN_000a4a28`'s own per-item logic, or a runtime crash inside actual per-frame execution after the loop genuinely starts rather than a load-time failure at all. A live breakpoint on `FUN_000b24d4` (Ghidra address `0x000b24d4`; converts to the live PPSSPP address via the offset method above) — the function that sets the "running" flag and is only reached once `PrepareGame` fully succeeds — would confirm definitively whether the main loop starts at all for this specific game.
