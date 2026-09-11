"""
The real fix for color_get_hue()/color_get_saturation()/color_get_value(),
superseding patch_hsv_conservative_fix.py (which is reverted by this script).

Two genuine bugs, both inside the runner's one real RGB-to-HSV routine,
FUN_000bf1e8. Found by re-reading its decompile after live PPSSPP register
debugging proved it returns a flat 0x00000000 for a well-formed input -
a result no amount of caller-side patching could have fixed.

BUG 1 - min computed twice, never max.
FUN_000bf1e8 has two consecutive, *identical* three-way comparison blocks:
  block A (0xbf22c-0xbf274) -> f17 = min(R,G,B)     [correct, intended as min]
  block B (0xbf278-0xbf2b0) -> f13 = min(R,G,B)     [WRONG - intended as max]
f13 is then used everywhere as the maximum:
  sub.s f17,f13,f17   -> delta = max - min
  div.s f22,f17,f13   -> saturation = delta / max
  mul.s f12,f13,f24   -> value = max * 255
With both blocks computing min, delta is always exactly 0, so saturation and
hue collapse to 0, and value reports the minimum channel instead of the
maximum. That is precisely the all-zeros observed live for pure red
(min(255,0,0) = 0), and it also explains the original unpatched
h=0 s=0 v=0 that started this whole investigation.

Fix: invert the three comparisons in block B by swapping the operands of
each `c.le.s fs,ft` (testing ft <= fs instead of fs <= ft), which turns the
min selection into a max selection. Equality ties are unaffected (when the
two channels are equal, min and max are the same value).

BUG 2 - saturation scaled by 2550.0 instead of 255.0.
The saturation byte is computed as `S_fraction * 2550.0` (the constant
0x451F6000 built by `lui a0,0x451f` / `ori a0,a0,0x6000`), then stored with
`sb` - a *byte* store. Full saturation therefore wraps to 2550 & 0xFF = 246,
and mid-range values wrap to nonsense. Value and hue use the correct 255.0
(0x437F0000) and 255/360 scalings respectively; only saturation has the
stray extra factor of ten.

Fix: change the constant to 255.0f (lui 0x437f / ori 0x0000).

Both fixes are pure in-place word edits inside FUN_000bf1e8 - identical
instruction count, no free-memory allocation, no hook redirection. With
FUN_000bf1e8 correct, FUN_000bf15c's *original* logic is correct too
(FUN_000bf100's R/B byte swap is deliberate and correct - it maps the
stored 0x00BBGGRR color into the channel order FUN_000bf1e8's hue formula
expects), so this script also restores FUN_000bf15c's original body,
undoing patch_hsv_conservative_fix.py's workaround.
"""

import struct
from pathlib import Path

SEG0_FILEOFF = 0x00000100
LOAD_BASE = 0x08804000

FUN_000bf15c = 0x000bf15c
FUN_000bf1e8 = 0x000bf1e8

# --- FUN_000bf1e8 word edits: (static addr, expected original word, fixed word, note) ---
# c.le.s encoding: [op=0x11][fmt=0x10][ft:5][fs:5][cc:5][funct=0x3E]
# Swapping fs/ft inverts the comparison, turning each min-select into a max-select.
EDITS = [
    (0x000bf280, 0x460E903E, 0x4612703E, "c.le.s f18,f14 -> c.le.s f14,f18  (f18 = max(G,B))"),
    (0x000bf290, 0x4612803E, 0x4610903E, "c.le.s f16,f18 -> c.le.s f18,f16  (keep R only if R is largest)"),
    (0x000bf2a4, 0x460E683E, 0x460D703E, "c.le.s f13,f14 -> c.le.s f14,f13  (f13 = max(G,B))"),
    (0x000bf388, 0x3C04451F, 0x3C04437F, "lui a0,0x451f -> lui a0,0x437f    (saturation scale 2550.0 -> 255.0)"),
    (0x000bf38c, 0x34846000, 0x34840000, "ori a0,a0,0x6000 -> ori a0,a0,0x0 (   \"      \"     \"     )"),
]

# FUN_000bf15c's original 21-word body, restored so the (now-correct) chain
# FUN_000bf100 -> FUN_000bf1e8 runs as originally designed.
FUN_000bf15c_ORIGINAL = bytes.fromhex(
    "e0ffbd27" "0000a4af" "1400bfaf" "40fc020c" "2520a003"
    "0800a2af" "0800a487" "0a00a593" "0400a4a7" "0600a5a3"
    "0400a487" "0600a593" "0c00a4a7" "0e00a5a3" "7afc020c"
    "0c00a48f" "1000a2af" "1000a28f" "1400bf8f" "0800e003"
    "2000bd27"
)


def main():
    elf_path = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")
    bin_path = Path(r"C:\Users\Owner\Documents\Chevo GM\chovy-gm\bin\Release\RUNNER_DECRYPTED\KAROSHI_GMS14PATCHED.BIN")

    data = bytearray(elf_path.read_bytes())

    for static_addr, expected, fixed, note in EDITS:
        off = SEG0_FILEOFF + static_addr
        current = struct.unpack_from("<I", data, off)[0]
        assert current == expected, (
            f"unexpected word at {hex(static_addr)} (file offset {hex(off)}): "
            f"got {hex(current)}, expected {hex(expected)} - already patched, or wrong file"
        )
        struct.pack_into("<I", data, off, fixed)
        print(f"  {hex(static_addr)} (live {hex(static_addr + LOAD_BASE)}): {note}")

    # Undo patch_hsv_conservative_fix.py's entry hook - the original body is
    # correct once FUN_000bf1e8 is. Its orphaned trampoline stays in the free
    # region as harmless dead code (nothing jumps to it any more).
    hook_off = SEG0_FILEOFF + FUN_000bf15c
    current_first = struct.unpack_from("<I", data, hook_off)[0]
    assert (current_first >> 26) == 0x02, (
        f"expected FUN_000bf15c to start with a `j` (the conservative fix's hook), "
        f"got {hex(current_first)}"
    )
    data[hook_off:hook_off + len(FUN_000bf15c_ORIGINAL)] = FUN_000bf15c_ORIGINAL
    print(f"  {hex(FUN_000bf15c)}: conservative-fix hook removed, original body restored")

    elf_path.write_bytes(bytes(data))
    print(f"\nwrote {elf_path}")
    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
