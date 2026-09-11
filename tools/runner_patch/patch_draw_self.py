"""
Patches karoshi_decrypted.elf to add a real, native draw_self() implementation.

Only applies when the GMS1.4 build target is selected - this script produces
a *separate* patched runner file; it never touches the stock runner used by
the GM8.1 pipeline. See CLAUDE.md's "draw_self()" section for the full
reverse-engineering trail this is built on.

Design (all addresses below are STATIC/Ghidra addresses; LOAD_BASE converts
to the live PPSSPP address used only for jal/j target words, per the
established `live = static + 0x08804000` offset):

  1. draw_self_impl: a new native function with the standard 5-arg native
     ABI (returnSlot, self, ctxB, argCount, args). Calls the 8 confirmed
     instance-variable getters directly, builds a 9-slot Variable array
     shaped exactly like draw_sprite_ext's own argument list, and
     tail-calls FUN_000417e4 (draw_sprite_ext's real implementation) with
     it. Zero re-derived business logic - every call target already
     exists in the runner.

  2. hook_trampoline: a tiny trampoline hooked into FUN_00046424's entry
     (the draw/display native-function registration routine). It replays
     the one instruction it displaced, calls FUN_000045bc("draw_self",
     draw_self_impl, 0, 0) to register the new function, restores the
     register FUN_00046424's own second instruction had already started
     computing, then resumes FUN_00046424 normally. This is the only
     place any *existing* code is touched, and only 4 bytes of it (one
     instruction, replaced with a single `j` to the trampoline).

Both new routines plus the "draw_self\0" name string are appended into the
runner's own huge pre-existing free region (ELF program header [1], a
.bss-style segment with far more reserved memory than file data) - nothing
about the ELF's structure changes, only bytes appended within space the
runner already reserves for itself.
"""

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from mips_asm import (  # noqa: E402
    addiu, ori, lui, lw, sw, or_, move, li0, nop, jr, jal_abs, j_abs,
    li32_pair, words_to_bytes, check_same_256mb_region,
)

LOAD_BASE = 0x08804000

# ---- confirmed static addresses (see CLAUDE.md) ----
FUN_000045bc = 0x000045bc          # register_native_function(name, funcPtr, argCount, flag)
FUN_000417e4 = 0x000417e4          # draw_sprite_ext real implementation
FUN_00046424 = 0x00046424          # draw/display native-function registration init (hook point)

GETTER_SPRITE_INDEX = 0x000ad27c
GETTER_IMAGE_INDEX = 0x000ad2b4
GETTER_X = 0x000acc8c
GETTER_Y = 0x000accc4
GETTER_IMAGE_XSCALE = 0x000ad588
GETTER_IMAGE_YSCALE = 0x000ad5c0
GETTER_IMAGE_ANGLE = 0x000ad5f8
GETTER_IMAGE_ALPHA = 0x000ad630

# ---- confirmed ELF layout (see dump below in __main__) ----
SEG1_VADDR = 0x0015da20
SEG1_FILEOFF = 0x0015db40
SEG1_FILESZ = 0x00000248
SEG1_MEMSZ = 0x002ba6b9

SEG0_VADDR = 0x00000000
SEG0_FILEOFF = 0x00000100

INJECT_VADDR = SEG1_VADDR + SEG1_FILESZ  # 0x0015dc68 - right where real file data currently ends


def live(static_addr):
    return static_addr + LOAD_BASE


def build_draw_self_impl(addr):
    """9-slot Variable array at sp+0x28; slot = {tag:4, unused:4, value(double):8}."""
    FRAME = 0xB8
    ARGS_BASE = 0x28  # offset of args[0] from sp

    def slot_off(i):
        return ARGS_BASE + i * 0x10

    w = []

    # prologue
    w.append(addiu("sp", "sp", -FRAME))
    w.append(sw("ra", 0x18, "sp"))
    w.append(sw("s0", 0x1C, "sp"))
    w.append(sw("s1", 0x20, "sp"))
    w.append(move("s0", "a1"))   # s0 = self
    w.append(move("s1", "a2"))   # s1 = ctxB

    def call_getter(slot_index, getter_addr):
        check_same_256mb_region(live(addr), live(getter_addr))
        return [
            move("a0", "s0"),
            li0("a1"),
            addiu("a2", "sp", slot_off(slot_index)),
            jal_abs(live(getter_addr)),
            nop(),
        ]

    w += call_getter(0, GETTER_SPRITE_INDEX)
    w += call_getter(1, GETTER_IMAGE_INDEX)
    w += call_getter(2, GETTER_X)
    w += call_getter(3, GETTER_Y)
    w += call_getter(4, GETTER_IMAGE_XSCALE)
    w += call_getter(5, GETTER_IMAGE_YSCALE)
    w += call_getter(6, GETTER_IMAGE_ANGLE)

    # slot 7: color, hardcoded to white/no-blend (16777215.0), no getter needed
    color_bits = struct.unpack("<Q", struct.pack("<d", 16777215.0))[0]
    color_lo = color_bits & 0xFFFFFFFF
    color_hi = (color_bits >> 32) & 0xFFFFFFFF
    s7 = slot_off(7)
    w.append(sw("zero", s7 + 0x0, "sp"))
    w.append(sw("zero", s7 + 0x4, "sp"))
    w += li32_pair("t0", color_lo)
    w.append(sw("t0", s7 + 0x8, "sp"))
    w += li32_pair("t1", color_hi)
    w.append(sw("t1", s7 + 0xC, "sp"))

    w += call_getter(8, GETTER_IMAGE_ALPHA)

    # final call: FUN_000417e4(returnSlot(unused), self, ctxB, argCount=9, args)
    check_same_256mb_region(live(addr), live(FUN_000417e4))
    w.append(li0("a0"))
    w.append(move("a1", "s0"))
    w.append(move("a2", "s1"))
    w.append(addiu("a3", "zero", 9))
    w.append(addiu("t0", "sp", ARGS_BASE))
    w.append(sw("t0", 0x10, "sp"))
    w.append(jal_abs(live(FUN_000417e4)))
    w.append(nop())

    # epilogue
    w.append(lw("ra", 0x18, "sp"))
    w.append(lw("s0", 0x1C, "sp"))
    w.append(lw("s1", 0x20, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(jr("ra"))
    w.append(nop())

    return words_to_bytes(w)


def build_trampoline(addr, name_addr, draw_self_impl_addr):
    """Hooked in at FUN_00046424's entry; see module docstring."""
    check_same_256mb_region(live(addr), live(FUN_000045bc))
    check_same_256mb_region(live(addr), live(FUN_00046424))

    FRAME = 0x10
    w = []
    w.append(move("t8", "a0"))          # stash partial a0 built by the displaced delay-slot instr
    w.append(addiu("sp", "sp", -0x10))  # re-execute the instruction we overwrote at FUN_00046424+0
    w.append(addiu("sp", "sp", -FRAME))  # our own small frame
    w.append(sw("ra", 0x08, "sp"))

    w += li32_pair("a0", live(name_addr))
    w += li32_pair("a1", live(draw_self_impl_addr))
    w.append(li0("a2"))
    w.append(li0("a3"))
    w.append(jal_abs(live(FUN_000045bc)))
    w.append(nop())

    w.append(lw("ra", 0x08, "sp"))
    w.append(addiu("sp", "sp", FRAME))
    w.append(move("a0", "t8"))          # restore a0 for FUN_00046424's own use
    w.append(j_abs(live(FUN_00046424 + 0x08)))
    w.append(nop())

    return words_to_bytes(w)


def main():
    src = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted.elf")
    dst = Path(r"C:\Users\Owner\Documents\Chevo GM\tools\decrypted\karoshi_decrypted_gms14patched.elf")

    data = bytearray(src.read_bytes())

    # sanity-check the program header assumptions this script hardcodes
    (p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_flags, p_align) = struct.unpack_from(
        "<IIIIIIII", data, 0x34 + 1 * 32
    )
    assert p_type == 1, "expected PT_LOAD"
    assert p_vaddr == SEG1_VADDR, f"segment[1] vaddr moved: {hex(p_vaddr)}"
    assert p_offset == SEG1_FILEOFF, f"segment[1] file offset moved: {hex(p_offset)}"
    assert p_filesz == SEG1_FILESZ, f"segment[1] filesz moved: {hex(p_filesz)}"
    assert p_memsz == SEG1_MEMSZ, f"segment[1] memsz moved: {hex(p_memsz)}"

    # --- lay out the new content ---
    trampoline_addr = INJECT_VADDR

    # trampoline size is fixed regardless of operand values (no branches on data), so we can size it
    # with placeholder addresses, then re-emit with the real ones once they're known.
    trampoline_probe = build_trampoline(trampoline_addr, trampoline_addr, trampoline_addr)
    trampoline_size = len(trampoline_probe)

    # draw_self_impl never encodes its own address (only fixed, already-known absolute jump
    # targets: the 8 getters and FUN_000417e4), so it needs no placeholder pass.
    draw_self_impl_addr = trampoline_addr + trampoline_size
    draw_self_impl_bytes = build_draw_self_impl(draw_self_impl_addr)

    name_addr = draw_self_impl_addr + len(draw_self_impl_bytes)
    name_bytes = b"draw_self\x00"
    # pad name to a 4-byte boundary so anything placed after stays aligned
    if len(name_bytes) % 4 != 0:
        name_bytes += b"\x00" * (4 - (len(name_bytes) % 4))

    trampoline_bytes = build_trampoline(trampoline_addr, name_addr, draw_self_impl_addr)
    assert len(trampoline_bytes) == trampoline_size

    blob = trampoline_bytes + draw_self_impl_bytes + name_bytes
    print(f"trampoline:      {hex(trampoline_addr)}  ({len(trampoline_bytes)} bytes)")
    print(f"draw_self_impl:  {hex(draw_self_impl_addr)}  ({len(draw_self_impl_bytes)} bytes)")
    print(f"name string:     {hex(name_addr)}  ({len(name_bytes)} bytes)")
    print(f"total injected:  {len(blob)} bytes (of {hex(SEG1_MEMSZ - SEG1_FILESZ)} available)")
    assert len(blob) <= (SEG1_MEMSZ - SEG1_FILESZ), "ran out of the runner's free region"

    # --- write the injected blob into the file, extending segment[1]'s filesz ---
    file_off = SEG1_FILEOFF + SEG1_FILESZ
    assert file_off == len(data) or file_off <= len(data), "unexpected file layout"
    if file_off + len(blob) > len(data):
        data.extend(b"\x00" * (file_off + len(blob) - len(data)))
    data[file_off:file_off + len(blob)] = blob

    new_filesz = SEG1_FILESZ + len(blob)
    struct.pack_into("<I", data, 0x34 + 1 * 32 + 16, new_filesz)  # p_filesz field offset within Phdr

    # --- hook FUN_00046424's entry: overwrite its first instruction with `j trampoline` ---
    hook_file_off = SEG0_FILEOFF + FUN_00046424
    orig_word = struct.unpack_from("<I", data, hook_file_off)[0]
    assert orig_word == 0x27BDFFF0, f"unexpected instruction at hook site: {hex(orig_word)}"  # addiu sp,sp,-0x10
    hook_word = j_abs(live(trampoline_addr))
    struct.pack_into("<I", data, hook_file_off, hook_word)

    dst.write_bytes(bytes(data))
    print(f"\nwrote {dst}")
    print(f"hook: FUN_00046424 @ live {hex(live(FUN_00046424))} -> j {hex(live(trampoline_addr))}")


if __name__ == "__main__":
    main()
