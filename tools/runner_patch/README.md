# Runner patch scripts

Standalone Python tools that binary-patch the decrypted PSP runner ELF
(`karoshi_decrypted_gms14patched.elf`) to add or fix GMS1.4 functionality.
They are deliberately outside the C# `GMAssetCompiler`/`ChovyUI` project —
each one edits the ELF directly and writes it back out, plus a synced copy
to `chovy-gm/bin/Release/RUNNER_DECRYPTED/KAROSHI_GMS14PATCHED.BIN` (the
file the build pipeline actually stages for a GMS1.4 PPSSPP test build —
see `CLAUDE.md` for the full staging/target logic). None of this touches
the GM8.1 runner, and none of it survives into a real signed EBOOT — this
only ever affects the opt-in decrypted-EBOOT PPSSPP testing path.

Every script hardcodes its own input/output paths near the top (matching
this machine's layout under `C:\Users\Owner\Documents\Chevo GM\`) and
re-verifies the exact bytes it expects to find before patching, so it
either patches cleanly or asserts loudly — never silently patches the
wrong thing or double-applies.

Run one from a plain `python <script>.py` (no arguments). After running,
rebuild the RUNNER_DECRYPTED test build as usual (see `CLAUDE.md`).

## Currently applied, all live in the shipped `.elf`/`.BIN` together

- **`mips_asm.py`** — a deliberately tiny MIPS32/o32 encoder (`lui`/`ori`/
  `addiu`/`lw`/`sw`/`or`/`sll`/`srl`/`lh`/`lbu`/`sh`/`sb`/`jal`/`j`/`jr`/
  `nop`), not a general assembler. Every encoding is commented with the
  real captured bytes it was verified against. Imported by the other
  scripts, not run standalone.
- **`patch_draw_self.py`** — adds `draw_self()`, absent from stock GM8.1,
  by calling 8 existing image-property getters directly and tail-calling
  the runner's own `draw_sprite_ext` implementation. The original proof of
  the "append to the `.bss`-style free region + hook one function's entry"
  mechanism this whole toolchain is built on.
- **`patch_math_functions.py`** — adds `clamp()`, `lerp()`, `dot_product()`
  on top of the `draw_self()`-patched ELF, using the same free-region
  append mechanism plus float unbox/rebox helpers. GMS1.4-only.
- **`patch_color_swap_fix.py`** / **`patch_get_color_fix.py`** — fix a
  Red/Blue channel swap in `draw_set_color()`/`draw_get_color()` (matches
  `YoYoGames/GameMaker-Bugs` issue #7070). Pure in-place instruction edits,
  no free-memory allocation.
- **`patch_hsv_minmax_fix.py`** — the real fix for
  `color_get_hue()`/`color_get_saturation()`/`color_get_value()`: two bugs
  in the shared RGB-to-HSV routine (a duplicated `min` block used as `max`,
  and a saturation scale of `2550.0` instead of `255.0`). Five in-place
  word edits; also reverts `patch_hsv_conservative_fix.py`'s hook, since
  the original calling code is correct once the HSV routine itself is.
  Verified 45/45 across a 15-colour round-trip battery. See `CLAUDE.md`
  for the full writeup, including the wrong turns below.

## Superseded / abandoned — kept only as a record of the investigation

Do **not** run these against a fresh ELF; they are not part of the current
patch set and are reverted or bypassed by `patch_hsv_minmax_fix.py`.

- **`patch_hsv_conservative_fix.py`** — an interim workaround that
  preserved every original instruction and only redirected which
  intermediate value got returned. Dodged a crash and reached 13/15, but
  was still wrong (saturation, and untested hues). Its trampoline is left
  as harmless dead code in the free region of the currently-shipped ELF —
  nothing jumps to it any more.
- **`patch_hsv_double_apply_fix.py`** — assumed the HSV routine was a
  redundant second pass and skipped it. Crashed PPSSPP reproducibly
  ("Bad Execution Address"), because that routine was the only one that
  actually computed anything.
- **`patch_hsv_real_fix.py`** — assumed the Red/Blue byte swap upstream
  was the bug and fed the HSV routine an unswapped color. The swap was
  correct all along; this regressed a passing test (13/15 → 12/15).
