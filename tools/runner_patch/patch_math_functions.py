"""
Adds clamp(), lerp(), and dot_product() to the runner as real native code -
three GMS1.4 functions confirmed genuinely absent from GM8.1 (verified: none
of the three strings exist anywhere in the binary, not just unregistered).

Only applies to the GMS1.4 runner. Builds on top of the already-patched
karoshi_decrypted_gms14patched.elf (which has draw_self()) rather than the
stock ELF, continuing to allocate from the same free .bss-style region -
the final GMS1.4 runner ends up with every patch applied together.

Design, using the exact same confirmed mechanisms as draw_self() plus two
newly-confirmed ones (see CLAUDE.md):
  - Native ABI (re-confirmed via point_distance's own disassembly):
    (a0=returnSlot, a1=self, a2=ctxB, a3=argCount, t0=argsPtr) - the args
    pointer arrives in register $t0, not on the stack as draw_self()'s
    patch assumed (that patch happened to work anyway - see CLAUDE.md).
  - Unbox a boxed double argument to a hardware single-precision float:
    FUN_000fd8dc(loWord, hiWord) -> float in $f0.
  - Rebox a float result back to a boxed double: FUN_000fcbc4(f12=value)
    -> {lo: v0, hi: v1}.
  - clamp(value, min, max) needs no new arithmetic at all: GM8.1 already
    has min()/max() as real registered functions, so
    clamp(v, lo, hi) = min(max(v, lo), hi) - just copies 16-byte Variable
    structs into scratch argument arrays and delegates to the existing
    FUN_00032d1c (max) and FUN_000329e4 (min).
  - lerp(a,b,amt) = a + (b-a)*amt and dot_product(x1,y1,x2,y2) = x1*x2+y1*y2
    are genuine new arithmetic, using confirmed hardware single-precision
    FPU instructions (add.s/sub.s/mul.s) - no comparisons or branches
    needed by either, so no need for the (unverified) FPU compare/branch
    encoding.
  - One hook, into FUN_000352a8's EXIT (not its entry) - registering the
    three new functions only after all ~60 of its own registrations have
    already completed. An entry hook (registering before FUN_000352a8's own
    calls, mirroring how draw_self()'s hook into FUN_00046424 was structured)
    was tried first and consistently broke every function FUN_000352a8
    registers (string() included) while leaving the rest of the runner intact
    - booting fine as long as nothing called string() or any other function
    from that same routine. The exact mechanism was never nailed down (no
    CPU exception was raised - this is a "some registration got corrupted"
    failure, not a crash), but registering after instead of before sidesteps
    it entirely and is also the simpler hook: FUN_000352a8's real epilogue is
    `lw ra,0(sp)` / `jr ra` / `addiu sp,sp,0x10` (found at 0x35994-0x3599c);
    hooking `jr ra` itself means $ra is already correctly loaded and the
    original frame's `addiu sp,sp,0x10` restores $sp naturally in the
    (unmodified) delay slot before the trampoline even runs - no mid-function
    resume needed, just register three functions and return.
"""

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import (  # noqa: E402
    addiu, ori, lui, lw, sw, or_, move, li0, nop, jr, jal_abs, j_abs,
    li32_pair, words_to_bytes, check_same_256mb_region,
    add_s, sub_s, mul_s, mov_s, swc1, lwc1,
)

LOAD_BASE = 0x08804000

# ---- confirmed static addresses ----
FUN_000045bc = 0x000045bc          # register_native_function(name, funcPtr, argCount, flag)
FUN_000352a8 = 0x000352a8          # math/random/string registration routine
FUN_000352a8_JR_RA = 0x00035998    # its real "jr ra" (hook point - see module docstring)
FUN_00032d1c = 0x00032d1c          # max() real implementation
FUN_000329e4 = 0x000329e4          # min() real implementation
FUN_000fd8dc = 0x000fd8dc          # unbox: (loWord, hiWord) -> float in $f0
FUN_000fcbc4 = 0x000fcbc4          # rebox: f12=float -> {lo: v0, hi: v1}

# ---- confirmed ELF layout (same segment[1] as the draw_self() patch) ----
SEG1_VADDR = 0x0015da20
SEG1_FILEOFF = 0x0015db40
SEG1_MEMSZ = 0x002ba6b9
SEG0_FILEOFF = 0x00000100

# Continue allocating right after draw_self()'s patch (trampoline 0x15dc68,
# total injected 352 bytes -> ends at 0x15ddc8, already 4-byte aligned).
INJECT_VADDR = 0x0015ddc8


def live(static_addr):
    return static_addr + LOAD_BASE


def slot_off(base, i):
    return base + i * 0x10


def build_clamp_impl():
    """clamp(value, min, max) = min(max(value, min), max) - pure delegation, no FPU."""
    FRAME = 0x58
    SCRATCH_A = 0x18   # [value, lo] - args for the max() call
    SCRATCH_B = 0x38   # [maxResult, hi] - args for the min() call; maxResult written by max() itself

    w = []
    w.append(addiu("sp", "sp", -FRAME))
    w.append(sw("ra", 0x00, "sp"))
    w.append(sw("s0", 0x04, "sp"))
    w.append(sw("s1", 0x08, "sp"))
    w.append(sw("s2", 0x0C, "sp"))
    w.append(sw("s3", 0x10, "sp"))
    w.append(move("s0", "a0"))   # original returnSlot
    w.append(move("s1", "t0"))   # original argsPtr [value, lo, hi]
    w.append(move("s2", "a1"))   # original self
    w.append(move("s3", "a2"))   # original ctxB

    # Copy args[0] (value) and args[1] (lo) into scratch A - but force each slot's
    # "unused" field (offset+4) to zero rather than trusting the source. max()'s
    # real implementation reads that field as a live string-pointer check
    # (bne on it, independent of the tag) - a VM operand-stack slot previously
    # used for a string and reused for a number could still carry a stale
    # non-null pointer there, sending max() down the wrong path with garbage.
    for src_off in (0x00, 0x10):
        dst_off = SCRATCH_A + src_off
        w.append(lw("t1", src_off + 0x0, "s1"))
        w.append(sw("t1", dst_off + 0x0, "sp"))      # tag
        w.append(sw("zero", dst_off + 0x4, "sp"))    # unused, forced 0
        w.append(lw("t1", src_off + 0x8, "s1"))
        w.append(sw("t1", dst_off + 0x8, "sp"))      # value lo
        w.append(lw("t1", src_off + 0xC, "s1"))
        w.append(sw("t1", dst_off + 0xC, "sp"))      # value hi

    # max(returnSlot=scratchB, self, ctxB, argCount=2, argsPtr=scratchA)
    check_same_256mb_region(live(INJECT_VADDR), live(FUN_00032d1c))
    w.append(addiu("a0", "sp", SCRATCH_B))
    w.append(move("a1", "s2"))
    w.append(move("a2", "s3"))
    w.append(addiu("a3", "zero", 2))
    w.append(addiu("t0", "sp", SCRATCH_A))
    w.append(jal_abs(live(FUN_00032d1c)))
    w.append(nop())

    # max() never writes its result's "unused" field (byte+4) - zero it defensively
    # before it's read back as an input argument to min().
    w.append(sw("zero", SCRATCH_B + 0x04, "sp"))

    # copy args[2] (hi) into scratchB+0x10 - same forced-zero treatment for
    # the "unused" field as above.
    dst_off = SCRATCH_B + 0x10
    w.append(lw("t1", 0x20 + 0x0, "s1"))
    w.append(sw("t1", dst_off + 0x0, "sp"))      # tag
    w.append(sw("zero", dst_off + 0x4, "sp"))    # unused, forced 0
    w.append(lw("t1", 0x20 + 0x8, "s1"))
    w.append(sw("t1", dst_off + 0x8, "sp"))      # value lo
    w.append(lw("t1", 0x20 + 0xC, "s1"))
    w.append(sw("t1", dst_off + 0xC, "sp"))      # value hi

    # min(returnSlot=original s0, self, ctxB, argCount=2, argsPtr=scratchB)
    check_same_256mb_region(live(INJECT_VADDR), live(FUN_000329e4))
    w.append(move("a0", "s0"))
    w.append(move("a1", "s2"))
    w.append(move("a2", "s3"))
    w.append(addiu("a3", "zero", 2))
    w.append(addiu("t0", "sp", SCRATCH_B))
    w.append(jal_abs(live(FUN_000329e4)))
    w.append(nop())

    w.append(lw("ra", 0x00, "sp"))
    w.append(lw("s0", 0x04, "sp"))
    w.append(lw("s1", 0x08, "sp"))
    w.append(lw("s2", 0x0C, "sp"))
    w.append(lw("s3", 0x10, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(jr("ra"))
    w.append(nop())

    return words_to_bytes(w)


def _unbox_call(addr, w, slot_base):
    check_same_256mb_region(live(addr), live(FUN_000fd8dc))
    w.append(lw("a1", slot_base + 0xC, "s1"))
    w.append(jal_abs(live(FUN_000fd8dc)))
    w.append(lw("a0", slot_base + 0x8, "s1"))  # delay slot


def build_lerp_impl(addr):
    """lerp(a, b, amt) = a + (b - a) * amt"""
    FRAME = 0x18
    w = []
    w.append(addiu("sp", "sp", -FRAME))
    w.append(sw("s0", 0x00, "sp"))
    w.append(sw("s1", 0x04, "sp"))
    w.append(swc1("f20", 0x08, "sp"))
    w.append(swc1("f22", 0x0C, "sp"))
    w.append(sw("ra", 0x10, "sp"))
    w.append(move("s0", "a0"))
    w.append(move("s1", "t0"))
    w.append(sw("zero", 0x0, "s0"))

    _unbox_call(addr, w, slot_off(0, 0))  # a
    w.append(mov_s("f20", "f0"))

    _unbox_call(addr, w, slot_off(0, 1))  # b
    w.append(sub_s("f22", "f0", "f20"))    # f22 = b - a

    _unbox_call(addr, w, slot_off(0, 2))  # amt
    w.append(mul_s("f22", "f22", "f0"))    # f22 = (b - a) * amt

    w.append(add_s("f12", "f20", "f22"))   # f12 = a + (b - a) * amt

    check_same_256mb_region(live(addr), live(FUN_000fcbc4))
    w.append(jal_abs(live(FUN_000fcbc4)))
    w.append(nop())
    w.append(sw("v1", 0xC, "s0"))
    w.append(sw("v0", 0x8, "s0"))

    w.append(lw("s0", 0x00, "sp"))
    w.append(lw("s1", 0x04, "sp"))
    w.append(lwc1("f20", 0x08, "sp"))
    w.append(lwc1("f22", 0x0C, "sp"))
    w.append(lw("ra", 0x10, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(jr("ra"))
    w.append(nop())

    return words_to_bytes(w)


def build_dot_product_impl(addr):
    """dot_product(x1, y1, x2, y2) = x1*x2 + y1*y2"""
    FRAME = 0x18
    w = []
    w.append(addiu("sp", "sp", -FRAME))
    w.append(sw("s0", 0x00, "sp"))
    w.append(sw("s1", 0x04, "sp"))
    w.append(swc1("f20", 0x08, "sp"))
    w.append(swc1("f22", 0x0C, "sp"))
    w.append(sw("ra", 0x10, "sp"))
    w.append(move("s0", "a0"))
    w.append(move("s1", "t0"))
    w.append(sw("zero", 0x0, "s0"))

    _unbox_call(addr, w, slot_off(0, 0))  # x1
    w.append(mov_s("f20", "f0"))

    _unbox_call(addr, w, slot_off(0, 1))  # y1
    w.append(mov_s("f22", "f0"))

    _unbox_call(addr, w, slot_off(0, 2))  # x2
    w.append(mul_s("f20", "f20", "f0"))    # f20 = x1 * x2

    _unbox_call(addr, w, slot_off(0, 3))  # y2
    w.append(mul_s("f22", "f22", "f0"))    # f22 = y1 * y2

    w.append(add_s("f12", "f20", "f22"))   # f12 = x1*x2 + y1*y2

    check_same_256mb_region(live(addr), live(FUN_000fcbc4))
    w.append(jal_abs(live(FUN_000fcbc4)))
    w.append(nop())
    w.append(sw("v1", 0xC, "s0"))
    w.append(sw("v0", 0x8, "s0"))

    w.append(lw("s0", 0x00, "sp"))
    w.append(lw("s1", 0x04, "sp"))
    w.append(lwc1("f20", 0x08, "sp"))
    w.append(lwc1("f22", 0x0C, "sp"))
    w.append(lw("ra", 0x10, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(jr("ra"))
    w.append(nop())

    return words_to_bytes(w)


def build_trampoline(addr, name_addrs, func_addrs, arg_counts):
    """Hooked into FUN_000352a8's *exit* (its `jr ra`), not its entry - see
    module docstring for why. By the time this runs, FUN_000352a8's own ~60
    registrations are already done, $ra is already correctly loaded (by the
    unmodified `lw ra,0(sp)` that ran just before this hook fires), and the
    original frame's `addiu sp,sp,0x10` has already executed too (the
    unmodified delay slot of the `j` that replaces `jr ra`). All this
    trampoline needs to do is register three functions and return.
    """
    check_same_256mb_region(live(addr), live(FUN_000045bc))
    check_same_256mb_region(live(addr), live(FUN_000352a8))

    FRAME = 0x10
    w = []
    w.append(move("t8", "ra"))          # stash the already-correct return address
    w.append(addiu("sp", "sp", -FRAME))  # our own small frame (FUN_000352a8's own frame is already popped)
    w.append(sw("t8", 0x08, "sp"))       # commit to a stack slot - safe across any number of calls

    for name_addr, func_addr, arg_count in zip(name_addrs, func_addrs, arg_counts):
        w += li32_pair("a0", live(name_addr))
        w += li32_pair("a1", live(func_addr))
        w.append(addiu("a2", "zero", arg_count))  # argCount - draw_self()'s patch used 0 correctly
        w.append(li0("a3"))                        # (it takes zero args); these three don't
        w.append(jal_abs(live(FUN_000045bc)))
        w.append(nop())

    w.append(lw("ra", 0x08, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(jr("ra"))
    w.append(nop())

    return words_to_bytes(w)


def main():
    src = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")
    dst = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")

    data = bytearray(src.read_bytes())

    # sanity-check assumptions about the (already draw_self()-patched) file layout
    (p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_flags, p_align) = struct.unpack_from(
        "<IIIIIIII", data, 0x34 + 1 * 32
    )
    assert p_type == 1, "expected PT_LOAD"
    assert p_vaddr == SEG1_VADDR
    assert p_offset == SEG1_FILEOFF
    assert p_memsz == SEG1_MEMSZ
    current_filesz = p_filesz
    expected_content_end = INJECT_VADDR - SEG1_VADDR
    assert current_filesz == expected_content_end, (
        f"expected current filesz {hex(expected_content_end)} (draw_self patch's end), "
        f"got {hex(current_filesz)} - INJECT_VADDR needs updating"
    )

    hook_word_at_exit = struct.unpack_from("<I", data, SEG0_FILEOFF + FUN_000352a8_JR_RA)[0]
    assert hook_word_at_exit == 0x03E00008, f"unexpected instruction at hook site: {hex(hook_word_at_exit)}"  # jr ra

    # --- lay out the new content ---
    trampoline_addr = INJECT_VADDR
    ARG_COUNTS = [3, 3, 4]  # clamp(value,min,max), lerp(a,b,amt), dot_product(x1,y1,x2,y2)

    trampoline_probe = build_trampoline(trampoline_addr, [trampoline_addr] * 3, [trampoline_addr] * 3, ARG_COUNTS)
    trampoline_size = len(trampoline_probe)

    clamp_addr = trampoline_addr + trampoline_size
    clamp_bytes = build_clamp_impl()

    lerp_addr = clamp_addr + len(clamp_bytes)
    lerp_bytes = build_lerp_impl(lerp_addr)

    dot_product_addr = lerp_addr + len(lerp_bytes)
    dot_product_bytes = build_dot_product_impl(dot_product_addr)

    names_addr = dot_product_addr + len(dot_product_bytes)
    name_bytes = b""
    name_offsets = {}
    for name in ("clamp", "lerp", "dot_product"):
        name_offsets[name] = names_addr + len(name_bytes)
        encoded = name.encode("ascii") + b"\x00"
        if len(encoded) % 4 != 0:
            encoded += b"\x00" * (4 - (len(encoded) % 4))
        name_bytes += encoded

    trampoline_bytes = build_trampoline(
        trampoline_addr,
        [name_offsets["clamp"], name_offsets["lerp"], name_offsets["dot_product"]],
        [clamp_addr, lerp_addr, dot_product_addr],
        ARG_COUNTS,
    )
    assert len(trampoline_bytes) == trampoline_size

    blob = trampoline_bytes + clamp_bytes + lerp_bytes + dot_product_bytes + name_bytes
    print(f"trampoline:   {hex(trampoline_addr)}  ({len(trampoline_bytes)} bytes)")
    print(f"clamp:        {hex(clamp_addr)}  ({len(clamp_bytes)} bytes)")
    print(f"lerp:         {hex(lerp_addr)}  ({len(lerp_bytes)} bytes)")
    print(f"dot_product:  {hex(dot_product_addr)}  ({len(dot_product_bytes)} bytes)")
    print(f"names:        {hex(names_addr)}  ({len(name_bytes)} bytes)")
    print(f"total injected this patch: {len(blob)} bytes")

    total_used_so_far = (INJECT_VADDR - SEG1_VADDR) + len(blob)
    assert total_used_so_far <= (SEG1_MEMSZ), "ran out of the runner's free region"

    file_off = SEG1_FILEOFF + (INJECT_VADDR - SEG1_VADDR)
    if file_off + len(blob) > len(data):
        data.extend(b"\x00" * (file_off + len(blob) - len(data)))
    data[file_off:file_off + len(blob)] = blob

    new_filesz = (INJECT_VADDR - SEG1_VADDR) + len(blob)
    struct.pack_into("<I", data, 0x34 + 1 * 32 + 16, new_filesz)

    hook_word = j_abs(live(trampoline_addr))
    struct.pack_into("<I", data, SEG0_FILEOFF + FUN_000352a8_JR_RA, hook_word)

    dst.write_bytes(bytes(data))
    print(f"\nwrote {dst}")
    print(f"hook: FUN_000352a8's jr ra @ live {hex(live(FUN_000352a8_JR_RA))} -> j {hex(live(trampoline_addr))}")
    print(f"registered: clamp @ {hex(live(clamp_addr))}, lerp @ {hex(live(lerp_addr))}, "
          f"dot_product @ {hex(live(dot_product_addr))}")


if __name__ == "__main__":
    main()
