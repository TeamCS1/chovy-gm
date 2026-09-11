"""
Conservative retry of the color_get_saturation()/color_get_value() fix.

patch_hsv_double_apply_fix.py replaced FUN_000bf15c's body outright (skip
the erroneous FUN_000bf1e8 second pass entirely, return FUN_000bf100's
result directly). That fix's own instruction encoding was verified correct
bit-for-bit (the jal target resolves exactly right), but live PPSSPP
testing crashed reproducibly - "Bad Execution Address" with an unstable PC
but a *consistent* RA (always the instruction right after the fix's own
call to FUN_000bf100), and every crash reported "Thread switch to X" as the
destination. Isolation testing (reverting only this patch, keeping the
color-swap fix) confirmed the crash disappears without it - so it's a real
effect of this specific change, not an unrelated pre-existing bug.

This suggests something about *skipping* the FUN_000bf1e8 call (or the
surrounding stack traffic) is load-bearing for reasons not yet understood -
maybe a timing dependency, maybe a genuine side effect. Rather than keep
guessing why, this version makes the most conservative possible change:
run every single original instruction unchanged, in the same order, with
the same calls (including the erroneous FUN_000bf1e8 pass and everything it
does) - and ONLY change which value ends up in $v0 at the very end. The
correct first-pass result (FUN_000bf100's return, before FUN_000bf1e8 ever
runs) gets stashed in an unused stack slot (offset 0x18, never touched by
the original 21-word body) immediately after it's computed, and the final
`lw v0,...` is redirected to reload it from there instead of from the
erroneous second-pass result at offset 0x10.

Since the original 21-word body has no spare room for the one extra stash
instruction, this needs a hook + trampoline (same shape as
patch_math_functions.py): FUN_000bf15c's entry becomes a `j` to a new
22-instruction copy in the runner's free memory region, appended after
whatever earlier patches have already claimed.
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

FUN_000bf15c = 0x000bf15c        # hook site (entry) - original 21-word body, now just a `j`
FUN_000bf15c_END = 0x000bf1ac
FUN_000bf100 = 0x000bf100         # correct single-pass RGB->HSV (unmodified)
FUN_000bf1e8 = 0x000bf1e8         # erroneous second pass (unmodified, still called - see module docstring)

# ---- confirmed ELF layout (same segment[1] every earlier patch in this
# tool has used - see patch_math_functions.py) ----
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
    w.append(addiu("sp", "sp", -FRAME))     # 1  (identical to original from here...)
    w.append(sw("a0", 0x0, "sp"))            # 2
    w.append(sw("ra", 0x14, "sp"))            # 3
    w.append(jal_abs(live(FUN_000bf100)))      # 4
    w.append(move("a0", "sp"))                  # 5  (delay slot)
    w.append(sw("v0", 0x18, "sp"))                # 6  NEW: stash the correct first-pass result
    w.append(sw("v0", 0x8, "sp"))                   # 7  (rest unchanged from here)
    w.append(lh("a0", 0x8, "sp"))                     # 8
    w.append(lbu("a1", 0xa, "sp"))                      # 9
    w.append(sh("a0", 0x4, "sp"))                         # 10
    w.append(sb("a1", 0x6, "sp"))                           # 11
    w.append(lh("a0", 0x4, "sp"))                             # 12
    w.append(lbu("a1", 0x6, "sp"))                              # 13
    w.append(sh("a0", 0xc, "sp"))                                 # 14
    w.append(sb("a1", 0xe, "sp"))                                   # 15
    w.append(jal_abs(live(FUN_000bf1e8)))                             # 16 (still called - preserve its side effects)
    w.append(lw("a0", 0xc, "sp"))                                       # 17 (delay slot)
    w.append(sw("v0", 0x10, "sp"))                                        # 18 (store the wrong result, same as original)
    w.append(lw("v0", 0x18, "sp"))                                          # 19 CHANGED: reload the correct stashed value instead
    w.append(lw("ra", 0x14, "sp"))                                            # 20
    w.append(jr("ra"))                                                         # 21
    w.append(addiu("sp", "sp", FRAME))                                          # 22 (delay slot)
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
    assert inject_vaddr % 4 == 0, "expected the existing watermark to already be word-aligned"

    # Confirm FUN_000bf15c is still the ORIGINAL 21-word body (i.e. neither
    # this nor the earlier double-apply patch has already been applied).
    hook_file_off = SEG0_FILEOFF + FUN_000bf15c
    current = bytes(data[hook_file_off:hook_file_off + 21 * 4])
    expected_original = bytes.fromhex(
        "e0ffbd27" "0000a4af" "1400bfaf" "40fc020c" "2520a003"
        "0800a2af" "0800a487" "0a00a593" "0400a4a7" "0600a5a3"
        "0400a487" "0600a593" "0c00a4a7" "0e00a5a3" "7afc020c"
        "0c00a48f" "1000a2af" "1000a28f" "1400bf8f" "0800e003"
        "2000bd27"
    )
    assert current == expected_original, (
        f"FUN_000bf15c isn't the expected original 21-word body (got {current.hex()}) - "
        "already patched, or wrong file"
    )

    trampoline_bytes = build_trampoline(inject_vaddr)
    print(f"trampoline: {hex(inject_vaddr)} (live {hex(live(inject_vaddr))}), {len(trampoline_bytes)} bytes")

    new_filesz = (inject_vaddr - p_vaddr) + len(trampoline_bytes)
    trampoline_file_off = SEG1_FILEOFF + (inject_vaddr - p_vaddr)
    if trampoline_file_off + len(trampoline_bytes) > len(data):
        data.extend(b"\x00" * (trampoline_file_off + len(trampoline_bytes) - len(data)))
    data[trampoline_file_off:trampoline_file_off + len(trampoline_bytes)] = trampoline_bytes
    struct.pack_into("<I", data, 0x34 + 1 * 32 + 16, new_filesz)

    # Hook FUN_000bf15c's entry: replace it with `j trampoline` / `nop`,
    # leaving the rest of its original 21-word space as harmless dead code.
    hook_words = [j_abs(live(inject_vaddr)), nop()]
    hook_bytes = words_to_bytes(hook_words)
    data[hook_file_off:hook_file_off + len(hook_bytes)] = hook_bytes

    elf_path.write_bytes(bytes(data))
    print(f"wrote {elf_path}")
    print(f"FUN_000bf15c entry @ live {hex(live(FUN_000bf15c))} -> j {hex(live(inject_vaddr))}")
    print("All original instructions/side effects preserved; only the final "
          "returned value is redirected to the correct first-pass result.")

    bin_path.write_bytes(bytes(data))
    print(f"synced to {bin_path}")


if __name__ == "__main__":
    main()
