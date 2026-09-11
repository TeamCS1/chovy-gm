# chovy-gm

At long last. GameMaker 8.1 (and now GameMaker: Studio 1.4) to PSP!

## About this fork

This is a continuation of [Li's original chovy-gm](https://git.silica.codes/Li/chovy-gm), which was archived in 2024. All credit for the original decompilation/patching work, the GM8.1-to-PSP pipeline, and the GUI goes to them — this fork picks up where that project left off, adding GameMaker: Studio 1.4 project support and reverse-engineering the runner itself to patch in genuinely missing GML functions.

## What is this code?

It's a decompiled and patched GameMaker Asset Compiler v1.0.98 (this was before there was any obfuscation), modified to produce PSP-compatible files. It originally only read GM8.1 executables; this fork adds a second path that reads GameMaker: Studio 1.4 `.gmx` projects directly.

NOTE: this is still buggy, mostly because it's built around **Karoshi** (a PSP mini released by YoYo Games back in 2011) as the "runner" for the game, adapting an early GameMaker Studio compiler to build files for it. That runner was never released publicly as a GM export module and was basically always in beta before being abandoned in favor of the PSVita version. Don't expect everything to work *well* — but a lot more of it works than it used to.

The only bugs that can realistically be fixed here are ones relating to compilation, not to the runner's own behavior (with the exception of the small set of native functions patched directly into the runner — see below).

(ISOs built with this WILL work in Chovy-Sign.)

## What's new in this fork

- **GameMaker: Studio 1.4 project support** — build straight from a `.gmx` project folder instead of a compiled GM8.1 `.exe`. Sprites, scripts, objects, rooms, sounds, backgrounds, paths, fonts, timelines, and room tiles all work (extensions don't, yet).
- **Early warnings for unsupported functions** — this PSP runner only understands a fixed set of about 1,185 built-in GameMaker functions (see the [function ledger](docs/runner-function-ledger.html) for the full list). Calling anything outside that set used to fail silently on the PSP with no error at all. Now you get a warning on your PC at compile time instead of a mystery crash on-device.
- **A handful of missing functions patched back in** — `draw_self()`, `clamp()`, `lerp()`, and `dot_product()` didn't exist in the original runner. They've been reverse-engineered and added as real machine code, confirmed working on real GMS1.4 builds. GM8.1 projects are completely unaffected.
- **One-click PPSSPP testing** — a checkbox in the GUI swaps in a pre-decrypted runner so a build boots straight into the PPSSPP emulator, skipping the manual decrypt step every test used to need.

Want the technical deep-dive — file format differences, CLI flags, how the runner patching actually works? See `CLAUDE.md`.

## Dependencies

chovy-gm release zip includes a few other executables:

- **at3tool** — an official Sony tool for converting WAV into their proprietary .at3 format. If anyone has a library that can handle this, please let us know.
- **umdgenc** — the official Sony UMD ISO builder. It was the only UMD ISO builder that would actually work properly on the PSVita (makes sense, since it's the official tool) — the reason most others don't work is that they align sectors in whatever order; the PSP doesn't care, but PSPEmu on the PSVita does.
- **fluidsynth** — a MIDI synthesizer, used for converting MID files inside GM executables into WAV and then into .at3. Source: https://github.com/FluidSynth/fluidsynth
- **EBOOT.BIN** — the same executable found in the PSP mini "Karoshi"; it's effectively the GameMaker interpreter (the "runner").
