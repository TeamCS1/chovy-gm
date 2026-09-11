"""
Minimal MIPS32 (Allegrex/o32) assembler helpers.

Only implements exactly the instruction shapes the runner patches need -
this is deliberately not a general-purpose assembler. Every encoding here
is a direct implementation of the standard MIPS32 instruction formats, so
correctness rests on the well-known field layouts, not on anything
runner-specific.

Instruction word layouts (all 32-bit, big picture only - bit numbering is
MSB=31..LSB=0):
  R-type: [op:6][rs:5][rt:5][rd:5][shamt:5][funct:6]
  I-type: [op:6][rs:5][rt:5][immediate:16]
  J-type: [op:6][target:26]
"""

REG = {
    "zero": 0, "at": 1, "v0": 2, "v1": 3,
    "a0": 4, "a1": 5, "a2": 6, "a3": 7,
    "t0": 8, "t1": 9, "t2": 10, "t3": 11, "t4": 12, "t5": 13, "t6": 14, "t7": 15,
    "s0": 16, "s1": 17, "s2": 18, "s3": 19, "s4": 20, "s5": 21, "s6": 22, "s7": 23,
    "t8": 24, "t9": 25, "k0": 26, "k1": 27,
    "gp": 28, "sp": 29, "fp": 30, "s8": 30, "ra": 31,
}

FREG = {f"f{i}": i for i in range(32)}


def _u16(v):
    v &= 0xFFFF
    return v


def _u32(v):
    return v & 0xFFFFFFFF


def r_type(op, rs, rt, rd, shamt, funct):
    return _u32((op << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (shamt << 6) | funct)


def i_type(op, rs, rt, imm):
    return _u32((op << 26) | (rs << 21) | (rt << 16) | _u16(imm))


def j_type(op, target_word_addr26):
    return _u32((op << 26) | (target_word_addr26 & 0x03FFFFFF))


def r(name):
    return REG[name]


def fr(name):
    return FREG[name]


# ---- instructions actually used ----

def addiu(rt, rs, imm):
    return i_type(0x09, r(rs), r(rt), imm)


def ori(rt, rs, imm):
    return i_type(0x0D, r(rs), r(rt), imm)


def lui(rt, imm):
    return i_type(0x0F, 0, r(rt), imm)


def lw(rt, offset, base):
    return i_type(0x23, r(base), r(rt), offset)


def sw(rt, offset, base):
    return i_type(0x2B, r(base), r(rt), offset)


# Verified against real captured bytes from FUN_0003e960's own disassembly:
# "lh a0,0x8(sp)" -> 87a40008, "lbu a1,0xa(sp)" -> 93a5000a,
# "sh a0,0x4(sp)" -> a7a40004, "sb a1,0x6(sp)" -> a3a50006.
def lh(rt, offset, base):
    return i_type(0x21, r(base), r(rt), offset)


def lbu(rt, offset, base):
    return i_type(0x24, r(base), r(rt), offset)


def sh(rt, offset, base):
    return i_type(0x29, r(base), r(rt), offset)


def sb(rt, offset, base):
    return i_type(0x28, r(base), r(rt), offset)


def or_(rd, rs, rt):
    return r_type(0x00, r(rs), r(rt), r(rd), 0, 0x25)


# Shift-by-immediate: source register goes in the rt field, rs=0 - verified
# against real captured bytes from FUN_00071ff0's own disassembly
# ("sll a1,a1,0x18" -> 00 2e 05 00, "srl a0,a0,0x10" -> 02 24 04 00).
def sll(rd, rt, shamt):
    return r_type(0x00, 0, r(rt), r(rd), shamt, 0x00)


def srl(rd, rt, shamt):
    return r_type(0x00, 0, r(rt), r(rd), shamt, 0x02)


def move(rd, rs):
    return or_(rd, rs, "zero")


def li0(rd):
    return or_(rd, "zero", "zero")


def nop():
    return 0x00000000


def jr(rs):
    return r_type(0x00, r(rs), 0, 0, 0, 0x08)


def jal_abs(target_addr):
    assert target_addr % 4 == 0
    return j_type(0x03, (target_addr >> 2))


def j_abs(target_addr):
    assert target_addr % 4 == 0
    return j_type(0x02, (target_addr >> 2))


def li32_pair(rt, value):
    """lui+ori pair that builds an exact 32-bit constant into rt (no sign issues, ori is logical)."""
    value = _u32(value)
    hi = (value >> 16) & 0xFFFF
    lo = value & 0xFFFF
    return [lui(rt, hi), ori(rt, rt, lo)]


# ---- COP1 (FPU) single-precision instructions ----
# Format verified bit-for-bit against real bytes from this runner's own point_distance
# implementation (FUN_00034f2c): op=0x11, fmt=16(S), then [ft][fs][fd][funct] - the
# textbook MIPS I FPU 3-register format. funct codes confirmed the same way:
# add.s=0x00, sub.s=0x01, mul.s=0x02, sqrt.s=0x04, mov.s=0x06. swc1/lwc1 (op=0x39/0x31)
# reuse the plain I-type [op][base][ft][offset] shape, also confirmed against real bytes.
_FMT_S = 0x10


def _cop1_r(ft, fs, fd, funct):
    return _u32((0x11 << 26) | (_FMT_S << 21) | (fr(ft) << 16) | (fr(fs) << 11) | (fd << 6) | funct)


def add_s(fd, fs, ft):
    return _cop1_r(ft, fs, fr(fd), 0x00)


def sub_s(fd, fs, ft):
    return _cop1_r(ft, fs, fr(fd), 0x01)


def mul_s(fd, fs, ft):
    return _cop1_r(ft, fs, fr(fd), 0x02)


def sqrt_s(fd, fs):
    return _cop1_r("f0", fs, fr(fd), 0x04)


def mov_s(fd, fs):
    return _cop1_r("f0", fs, fr(fd), 0x06)


def swc1(ft, offset, base):
    return _u32((0x39 << 26) | (r(base) << 21) | (fr(ft) << 16) | _u16(offset))


def lwc1(ft, offset, base):
    return _u32((0x31 << 26) | (r(base) << 21) | (fr(ft) << 16) | _u16(offset))


def words_to_bytes(words):
    out = bytearray()
    for w in words:
        out += _u32(w).to_bytes(4, "little")
    return bytes(out)


def check_same_256mb_region(a, b):
    """j/jal only replace the low 28 bits of PC; both addresses must share top 4 bits."""
    assert (a & 0xF0000000) == (b & 0xF0000000), (
        f"j/jal target {hex(b)} not reachable via direct jump from {hex(a)} "
        "(top 4 address bits differ)"
    )
