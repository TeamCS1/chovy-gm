"""
Fixes color_get_saturation()/color_get_value() always returning 0 (and,
less obviously, makes color_get_hue() correct for every color rather than
just coincidentally correct for the one test case that happened to pass).

Traced via Ghidra, raw disassembly (not the decompiler's ambiguous
byte-reinterpretation casts, which were actively misleading here):
color_get_hue/saturation/value (FUN_0003e900/FUN_0003e960/FUN_0003e9c0) all
call the same shared helper, FUN_000bf15c(color), then each correctly
extracts a DIFFERENT byte from its result - bits 0-7 for hue, bits 8-15 for
saturation, bits 16-23 for value. These three wrapper functions are not the
bug.

The bug is entirely inside FUN_000bf15c. It correctly computes the packed
`V<<16 | S<<8 | H` triple via FUN_000bf100(&color) (a real, correct-looking
RGB-to-HSV conversion, confirmed by hand-tracing its formula) - then, instead
of returning that, it feeds the *already-computed HSV triple* into
FUN_000bf1e8 as if it were a second raw color and re-runs the entire
RGB-to-HSV conversion again (FUN_000bf1e8 is byte-identical source to
FUN_000bf100). For our test color (pure red via make_color_hsv(0,255,255))
this second, nonsensical pass happens to collapse to (0,0,0), which is why
saturation and value read back as flat 0 - not because they're extracting
the wrong byte, but because the shared value they're all reading from is
itself wrong. Fix: make FUN_000bf15c return FUN_000bf100's result directly,
dropping the erroneous second pass entirely.

Same style as patch_color_swap_fix.py: an in-place instruction patch. The
fixed function (8 real instructions) fits inside the original's 21-word
(84-byte) frame with room to spare, so no free-memory allocation or hook
redirection is needed.
"""

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import addiu, sw, lw, move, jr, jal_abs, nop, words_to_bytes, check_same_256mb_region  # noqa: E402

SEG0_FILEOFF = 0x00000100
LOAD_BASE = 0x08804000

FUN_000bf15c = 0x000bf15c  # buggy double-apply wrapper (21 words, ends at 0x000bf1ac inclusive)
FUN_000bf15c_END = 0x000bf1ac
FUN_000bf100 = 0x000bf100  # correct, single-pass RGB->HSV conversion (unmodified)
ORIGINAL_WORD_COUNT = (FUN_000bf15c_END - FUN_000bf15c) // 4 + 1


def live(addr):
    return addr + LOAD_BASE


def build_fix():
    """Just call FUN_000bf100(&color) and return its result - no second pass."""
    check_same_256mb_region(live(FUN_000bf15c), live(FUN_000bf100))
    FRAME = 0x20  # keep the original frame size; simplest and there's no reason to shrink it
    w = []
    w.append(addiu("sp", "sp", -FRAME))
    w.append(sw("a0", 0x0, "sp"))         # stash the color argument on the stack
    w.append(sw("ra", 0x14, "sp"))
    w.append(jal_abs(live(FUN_000bf100)))
    w.append(move("a0", "sp"))             # delay slot: a0 = &color (FUN_000bf100 takes a pointer)
    w.append(lw("ra", 0x14, "sp"))
    w.append(jr("ra"))
    w.append(addiu("sp", "sp", FRAME))     # delay slot; v0 already holds FUN_000bf100's correct result
    while len(w) < ORIGINAL_WORD_COUNT:
        w.append(nop())                     # pad out to the original function's exact size
    assert len(w) == ORIGINAL_WORD_COUNT
    return words_to_bytes(w)


def main():
    elf_path = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")
    bin_path = Path(r"C:\Users\Owner\Documents\Chevo GM\chovy-gm\bin\Release\RUNNER_DECRYPTED\KAROSHI_GMS14PATCHED.BIN")

    data = bytearray(elf_path.read_bytes())

    file_off = SEG0_FILEOFF + FUN_000bf15c
    current = bytes(data[file_off:file_off + ORIGINAL_WORD_COUNT * 4])

    expected = bytes.fromhex(
        "e0ffbd27"  # addiu sp,sp,-0x20
        "0000a4af"  # sw a0,0x0(sp)
        "1400bfaf"  # sw ra,0x14(sp)
        "40fc020c"  # jal FUN_000bf100
        "2520a003"  # move a0,sp            (delay slot)
        "0800a2af"  # sw v0,0x8(sp)
        "0800a487"  # lh a0,0x8(sp)
        "0a00a593"  # lbu a1,0xa(sp)
        "0400a4a7"  # sh a0,0x4(sp)
        "0600a5a3"  # sb a1,0x6(sp)
        "0400a487"  # lh a0,0x4(sp)
        "0600a593"  # lbu a1,0x6(sp)
        "0c00a4a7"  # sh a0,0xc(sp)
        "0e00a5a3"  # sb a1,0xe(sp)
        "7afc020c"  # jal FUN_000bf1e8      (the erroneous second pass)
        "0c00a48f"  # lw a0,0xc(sp)         (delay slot)
        "1000a2af"  # sw v0,0x10(sp)
        "1000a28f"  # lw v0,0x10(sp)
        "1400bf8f"  # lw ra,0x14(sp)
        "0800e003"  # jr ra
        "2000bd27"  # addiu sp,sp,0x20      (delay slot)
    )
    assert current == expected, (
        f"unexpected bytes at FUN_000bf15c (file offset {hex(file_off)}): "
        f"got {current.hex()}, expected {expected.hex()} - already patched, or wrong file/offset"
    )

    fixed = build_fix()
    assert len(fixed) == len(expected)
    data[file_off:file_off + len(fixed)] = fixed

    elf_path.write_bytes(bytes(data))
    print(f"wrote {elf_path}")
    print(f"FUN_000bf15c @ file offset {hex(file_off)} (live {hex(live(FUN_000bf15c))}): "
          "erroneous second HSV pass removed, now returns FUN_000bf100's result directly")

    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
