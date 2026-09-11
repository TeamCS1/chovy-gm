"""
Fixes the Red/Blue channel swap in this runner's color rendering.

Confirmed via live PPSSPP testing (a 15-check Drawing: Color & Blending
battery in Sample.gmx): draw_set_color-based rendering consistently swaps
Red and Blue - a pure "red" draws as blue and vice versa, matching a real,
documented cross-version GameMaker bug (github.com/YoYoGames/GameMaker-Bugs
issue #7070). make_color_rgb/color_get_red/green/blue are internally
consistent with each other (not swapped) - the bug is purely in the render
path.

Root cause, traced via Ghidra (draw_set_color -> FUN_0006fc20 -> FUN_00071ff0):
FUN_00071ff0(color, alpha) combines a packed 24-bit color with an alpha byte
for the GPU. make_color_rgb packs colors as R + G*256 + B*65536 (0x00BBGGRR),
which is already the byte order the PSP's GU hardware wants (ABGR8888) - the
function only needs to OR that with (alpha << 24). Instead it explicitly
swaps the R and B bytes (moves bits[7:0] up to bits[23:16], and bits[23:16]
down to bits[7:0]) before combining - undoing byte ordering that didn't need
touching. Fix: replace the swap logic with a straight pass-through.

This is a pure in-place instruction patch, not a new function - the fixed
version needs 5 real instructions (sll/sll/srl/or/jr) vs the original 10,
so it fits inside the original function's own bounds with no free-memory
allocation, no registration-table changes, and no hook redirection needed.
"""

import struct
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import sll, srl, or_, jr, nop, words_to_bytes  # noqa: E402

SEG0_FILEOFF = 0x00000100  # segment 0's vaddr is 0, so file_off = SEG0_FILEOFF + static_vaddr

FUN_00071ff0 = 0x00071ff0  # buggy color+alpha packer (10 words, ends at 0x00072014 inclusive)
FUN_00071ff0_END = 0x00072014
ORIGINAL_WORD_COUNT = (FUN_00071ff0_END - FUN_00071ff0) // 4 + 1


def build_fix():
    """v0 = (alpha << 24) | (color & 0xffffff), color's bytes untouched."""
    w = []
    w.append(sll("v0", "a1", 0x18))   # v0 = alpha << 24
    w.append(sll("a0", "a0", 0x8))    # a0 = color << 8   (clears any garbage above bit 23)
    w.append(srl("a0", "a0", 0x8))    # a0 = color, masked back to 24 bits
    w.append(or_("v0", "v0", "a0"))   # v0 = (alpha<<24) | color
    w.append(jr("ra"))
    w.append(nop())                    # branch delay slot
    while len(w) < ORIGINAL_WORD_COUNT:
        w.append(nop())                # pad out to the original function's exact size
    assert len(w) == ORIGINAL_WORD_COUNT
    return words_to_bytes(w)


def main():
    elf_path = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")
    bin_path = Path(r"C:\Users\Owner\Documents\Chevo GM\chovy-gm\bin\Release\RUNNER_DECRYPTED\KAROSHI_GMS14PATCHED.BIN")

    data = bytearray(elf_path.read_bytes())

    file_off = SEG0_FILEOFF + FUN_00071ff0
    current = bytes(data[file_off:file_off + ORIGINAL_WORD_COUNT * 4])

    # Sanity-check the exact original bytes captured live from Ghidra, word by word.
    expected = bytes.fromhex(
        "ff008630"  # andi a2,a0,0xff
        "002e0500"  # sll a1,a1,0x18
        "00340600"  # sll a2,a2,0x10
        "2528a600"  # or a1,a1,a2
        "00ff8630"  # andi a2,a0,0xff00
        "02240400"  # srl a0,a0,0x10
        "2510a600"  # or v0,a1,a2
        "ff008430"  # andi a0,a0,0xff
        "0800e003"  # jr ra
        "25104400"  # or v0,v0,a0  (delay slot)
    )
    assert current == expected, (
        f"unexpected bytes at FUN_00071ff0 (file offset {hex(file_off)}): "
        f"got {current.hex()}, expected {expected.hex()} - already patched, or wrong file/offset"
    )

    fixed = build_fix()
    assert len(fixed) == len(expected)
    data[file_off:file_off + len(fixed)] = fixed

    elf_path.write_bytes(bytes(data))
    print(f"wrote {elf_path}")
    print(f"FUN_00071ff0 @ file offset {hex(file_off)} (live {hex(FUN_00071ff0 + 0x08804000)}): "
          "R/B swap removed, alpha|color pass-through in place")

    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
