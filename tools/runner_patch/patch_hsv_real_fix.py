"""
The real fix for color_get_saturation()/color_get_value(), superseding
patch_hsv_conservative_fix.py.

That conservative fix avoided the mystery crash (see its own docstring),
and got hue and value right for pure red - but saturation stayed wrong
(0 instead of 255). Investigating why revealed a wrong premise from earlier
analysis: raw disassembly (not the decompiler's read, which was misleading)
shows FUN_000bf100 is NOT a second HSV computation at all - it's a tiny
14-instruction function that just re-packs the color's bytes, swapping Red
and Blue (the exact same bug class already fixed in the render path -
FUN_00071ff0/FUN_00072030). The *only* real floating-point RGB-to-HSV math
in this whole call chain lives in FUN_000bf1e8.

So FUN_000bf15c's actual bug: it corrupts the color's byte order via the
buggy FUN_000bf100 *before* handing it to the real converter FUN_000bf1e8 -
which then computes real HSV, just from the wrong (R/B-swapped) input. For
pure red this coincidentally produces a plausible-looking hue (0) and value
(255) - since swapping R and B doesn't change which channel holds the max
value when only one of them is non-zero - but wrecks saturation, which is
sensitive to exactly which channel is used where. The conservative fix's
h=0/v=255 "successes" were themselves a coincidence of this one test color,
not evidence the fix was actually correct.

FUN_000bf1e8 takes the packed color as a plain value in $a0 (not a pointer -
confirmed via its own disassembly, `sw a0,0x0(sp)` then per-byte `lbu`s), so
it can be called directly with the original, unmodified color - no pointer
wrapping needed at all.

Same maximally-conservative approach as the previous attempt: change
nothing about what the original code already does (both existing calls,
including the buggy FUN_000bf100 one, still run exactly as before, in case
that unexplained crash-avoidance is timing-related) - only add one further
call to FUN_000bf1e8 with the real, unswapped color, and return that result
instead of the original's.
"""

import struct
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import (  # noqa: E402
    addiu, sw, lw, lh, lbu, sb, sh, move, jr, jal_abs, j_abs, nop,
    words_to_bytes, check_same_256mb_region,
)

SEG0_FILEOFF = 0x00000100
LOAD_BASE = 0x08804000

FUN_000bf15c = 0x000bf15c
FUN_000bf100 = 0x000bf100          # buggy R/B-byte-swap repack (unmodified - still called, see docstring)
FUN_000bf1e8 = 0x000bf1e8           # the real RGB->HSV conversion (unmodified)

SEG1_VADDR = 0x0015da20
SEG1_FILEOFF = 0x0015db40
SEG1_MEMSZ = 0x002ba6b9


def live(addr):
    return addr + LOAD_BASE


def build_trampoline(addr):
    check_same_256mb_region(live(addr), live(FUN_000bf100))
    check_same_256mb_region(live(addr), live(FUN_000bf1e8))
    FRAME = 0x20
    w = []
    w.append(addiu("sp", "sp", -FRAME))     # 1  (everything from here to instruction 17 is
    w.append(sw("a0", 0x0, "sp"))            # 2   byte-for-byte what the original 21-word
    w.append(sw("ra", 0x14, "sp"))            # 3   FUN_000bf15c already did - unchanged)
    w.append(jal_abs(live(FUN_000bf100)))      # 4
    w.append(move("a0", "sp"))                  # 5  (delay slot)
    w.append(sw("v0", 0x8, "sp"))                 # 6
    w.append(lh("a0", 0x8, "sp"))                   # 7
    w.append(lbu("a1", 0xa, "sp"))                    # 8
    w.append(sh("a0", 0x4, "sp"))                       # 9
    w.append(sb("a1", 0x6, "sp"))                         # 10
    w.append(lh("a0", 0x4, "sp"))                           # 11
    w.append(lbu("a1", 0x6, "sp"))                            # 12
    w.append(sh("a0", 0xc, "sp"))                               # 13
    w.append(sb("a1", 0xe, "sp"))                                 # 14
    w.append(jal_abs(live(FUN_000bf1e8)))                           # 15
    w.append(lw("a0", 0xc, "sp"))                                     # 16 (delay slot)
    w.append(sw("v0", 0x10, "sp"))                                      # 17
    # --- everything above is the untouched original; only what follows is new ---
    w.append(lw("a0", 0x0, "sp"))            # 18  NEW: reload the real, unswapped color
    w.append(jal_abs(live(FUN_000bf1e8)))     # 19  NEW: real HSV conversion, correct input this time
    w.append(nop())                            # 20  (delay slot - a0 already set by the lw above)
    w.append(lw("ra", 0x14, "sp"))               # 21  v0 already holds the correct result
    w.append(jr("ra"))                             # 22
    w.append(addiu("sp", "sp", FRAME))               # 23 (delay slot)
    return words_to_bytes(w)


def main():
    elf_path = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")
    bin_path = Path(r"C:\Users\Owner\Documents\Chevo GM\chovy-gm\bin\Release\RUNNER_DECRYPTED\KAROSHI_GMS14PATCHED.BIN")

    data = bytearray(elf_path.read_bytes())

    (p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_flags, p_align) = struct.unpack_from(
        "<IIIIIIII", data, 0x34 + 1 * 32
    )
    assert p_type == 1, "expected PT_LOAD"
    assert p_vaddr == SEG1_VADDR
    assert p_offset == SEG1_FILEOFF
    assert p_memsz == SEG1_MEMSZ
    inject_vaddr = p_vaddr + p_filesz
    assert inject_vaddr % 4 == 0

    # FUN_000bf15c should currently be hooked to the *conservative* fix's
    # trampoline (a `j` + nop), not the original 21-word body - confirm
    # that's genuinely the state we're building on top of.
    hook_file_off = SEG0_FILEOFF + FUN_000bf15c
    current_hook = bytes(data[hook_file_off:hook_file_off + 8])
    j_opcode = struct.unpack_from("<I", current_hook, 0)[0] >> 26
    assert j_opcode == 0x02, (
        f"expected FUN_000bf15c's entry to already be a `j` (from the conservative fix), "
        f"got word {hex(struct.unpack_from('<I', current_hook, 0)[0])} - wrong starting state"
    )

    trampoline_bytes = build_trampoline(inject_vaddr)
    print(f"trampoline: {hex(inject_vaddr)} (live {hex(live(inject_vaddr))}), {len(trampoline_bytes)} bytes")

    new_filesz = (inject_vaddr - p_vaddr) + len(trampoline_bytes)
    trampoline_file_off = SEG1_FILEOFF + (inject_vaddr - p_vaddr)
    if trampoline_file_off + len(trampoline_bytes) > len(data):
        data.extend(b"\x00" * (trampoline_file_off + len(trampoline_bytes) - len(data)))
    data[trampoline_file_off:trampoline_file_off + len(trampoline_bytes)] = trampoline_bytes
    struct.pack_into("<I", data, 0x34 + 1 * 32 + 16, new_filesz)

    hook_words = [j_abs(live(inject_vaddr)), nop()]
    hook_bytes = words_to_bytes(hook_words)
    data[hook_file_off:hook_file_off + len(hook_bytes)] = hook_bytes

    elf_path.write_bytes(bytes(data))
    print(f"wrote {elf_path}")
    print(f"FUN_000bf15c entry @ live {hex(live(FUN_000bf15c))} -> j {hex(live(inject_vaddr))}")
    print("Original calls preserved; now also calls FUN_000bf1e8 a second time "
          "with the real, unswapped color, and returns that result.")

    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
