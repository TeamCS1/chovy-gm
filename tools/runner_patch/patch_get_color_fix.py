"""
Companion fix to patch_color_swap_fix.py.

After fixing FUN_00071ff0 (the color+alpha packer that fed the GPU), live
PPSSPP testing showed rendering was correct but draw_get_color()'s own
round-trip broke (draw_set_color(c) -> draw_get_color() no longer equals c).

Root cause: draw_get_color() -> FUN_0006fdc0() -> FUN_00072030(DAT_00123bd0)
extracts the RGB part of the stored color for the GML caller, but
FUN_00072030 does the *same* Red/Blue swap FUN_00071ff0 used to do:

    (color & 0xff) << 0x10 | color & 0xff00 | color >> 0x10 & 0xff

This swap used to be necessary to undo FUN_00071ff0's swap and recover the
original make_color_rgb-format value. Now that FUN_00071ff0 no longer swaps,
DAT_00123bd0 already holds the color in make_color_rgb's own 0x00BBGGRR
format - FUN_00072030 just needs to return it unchanged (aside from
stripping the alpha byte already stored above bit 24, if any).

Same style of fix as the first patch: replace the swap with a masked
pass-through, padded to the original function's exact instruction count so
no free-memory allocation or hook redirection is needed.
"""

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import sll, srl, jr, nop, words_to_bytes  # noqa: E402

SEG0_FILEOFF = 0x00000100

FUN_00072030 = 0x00072030  # buggy RGB un-swap (8 words, ends at 0x0007204c inclusive)
FUN_00072030_END = 0x0007204c
ORIGINAL_WORD_COUNT = (FUN_00072030_END - FUN_00072030) // 4 + 1


def build_fix():
    """v0 = color & 0xffffff, bytes untouched (no swap)."""
    w = []
    w.append(sll("v0", "a0", 0x8))    # v0 = color << 8
    w.append(srl("v0", "v0", 0x8))    # v0 = color, masked back to 24 bits
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

    file_off = SEG0_FILEOFF + FUN_00072030
    current = bytes(data[file_off:file_off + ORIGINAL_WORD_COUNT * 4])

    expected = bytes.fromhex(
        "ff008530"  # andi a1,a0,0xff
        "00ff8630"  # andi a2,a0,0xff00
        "002c0500"  # sll a1,a1,0x10
        "02240400"  # srl a0,a0,0x10
        "2510a600"  # or v0,a1,a2
        "ff008430"  # andi a0,a0,0xff
        "0800e003"  # jr ra
        "25104400"  # or v0,v0,a0  (delay slot)
    )
    assert current == expected, (
        f"unexpected bytes at FUN_00072030 (file offset {hex(file_off)}): "
        f"got {current.hex()}, expected {expected.hex()} - already patched, or wrong file/offset"
    )

    fixed = build_fix()
    assert len(fixed) == len(expected)
    data[file_off:file_off + len(fixed)] = fixed

    elf_path.write_bytes(bytes(data))
    print(f"wrote {elf_path}")
    print(f"FUN_00072030 @ file offset {hex(file_off)} (live {hex(FUN_00072030 + 0x08804000)}): "
          "R/B un-swap removed, masked pass-through in place")

    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
