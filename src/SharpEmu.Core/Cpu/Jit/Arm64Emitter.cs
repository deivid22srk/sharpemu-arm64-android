// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Core.Cpu.Jit;

public enum Arm64Condition
{
    EQ = 0x0,
    NE = 0x1,
    CS = 0x2, HS = 0x2,
    CC = 0x3, LO = 0x3,
    MI = 0x4,
    PL = 0x5,
    VS = 0x6,
    VC = 0x7,
    HI = 0x8,
    LS = 0x9,
    GE = 0xA,
    LT = 0xB,
    GT = 0xC,
    LE = 0xD,
    AL = 0xE,
    NV = 0xF,
}

public sealed class Arm64Emitter
{
    private byte[] _buffer;
    private int _position;

    public Arm64Emitter(int initialCapacity = 4096)
    {
        _buffer = new byte[initialCapacity];
        _position = 0;
    }

    public int CodeSize => _position;

    public byte[] Buffer => _buffer;

    public byte[] ToArray()
    {
        var result = new byte[_position];
        Array.Copy(_buffer, 0, result, 0, _position);
        return result;
    }

    public void Reset()
    {
        _position = 0;
    }

    private void EnsureCapacity(int bytesNeeded)
    {
        if (_position + bytesNeeded > _buffer.Length)
        {
            var newCap = Math.Max(_buffer.Length * 2, _position + bytesNeeded);
            Array.Resize(ref _buffer, newCap);
        }
    }

    public void EmitUInt32(uint rawInstruction)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), rawInstruction);
        _position += 4;
    }

    public void EmitUInt32At(int offset, uint rawInstruction)
    {
        if (offset < 0 || offset + 4 > _position)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset), rawInstruction);
    }

    // --- Register Move / Immediate ---

    public void MovX(int dstX, int srcX)
    {
        // ORR Xd, XZR, Xm -> 0xAA0003E0 | (src << 16) | dst
        EmitUInt32(0xAA0003E0U | ((uint)srcX << 16) | (uint)dstX);
    }

    public void MovW(int dstW, int srcW)
    {
        // ORR Wd, WZR, Wm -> 0x2A0003E0 | (src << 16) | dst
        EmitUInt32(0x2A0003E0U | ((uint)srcW << 16) | (uint)dstW);
    }

    public void MovImm64(int dstX, ulong value)
    {
        ushort hw0 = (ushort)(value & 0xFFFF);
        ushort hw1 = (ushort)((value >> 16) & 0xFFFF);
        ushort hw2 = (ushort)((value >> 32) & 0xFFFF);
        ushort hw3 = (ushort)((value >> 48) & 0xFFFF);

        // MOVZ Xd, #hw0, LSL #0
        EmitUInt32(0xD2800000U | ((uint)hw0 << 5) | (uint)dstX);

        if (hw1 != 0)
        {
            // MOVK Xd, #hw1, LSL #16
            EmitUInt32(0xF2A00000U | ((uint)hw1 << 5) | (uint)dstX);
        }
        if (hw2 != 0)
        {
            // MOVK Xd, #hw2, LSL #32
            EmitUInt32(0xF2C00000U | ((uint)hw2 << 5) | (uint)dstX);
        }
        if (hw3 != 0)
        {
            // MOVK Xd, #hw3, LSL #48
            EmitUInt32(0xF2E00000U | ((uint)hw3 << 5) | (uint)dstX);
        }
    }

    public void MovImm32(int dstW, uint value)
    {
        ushort hw0 = (ushort)(value & 0xFFFF);
        ushort hw1 = (ushort)(value >> 16);

        // MOVZ Wd, #hw0, LSL #0
        EmitUInt32(0x52800000U | ((uint)hw0 << 5) | (uint)dstW);

        if (hw1 != 0)
        {
            // MOVK Wd, #hw1, LSL #16
            EmitUInt32(0x72A00000U | ((uint)hw1 << 5) | (uint)dstW);
        }
    }

    // --- Arithmetic / Logic ---

    public void AddX(int dstX, int src1X, int src2X)
    {
        // ADD Xd, Xn, Xm -> 0x8B000000 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0x8B000000U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void AddXImm(int dstX, int srcX, uint imm12)
    {
        // ADD Xd, Xn, #imm12 -> 0x91000000 | ((imm12 & 0xFFF) << 10) | (src << 5) | dst
        EmitUInt32(0x91000000U | ((imm12 & 0xFFFU) << 10) | ((uint)srcX << 5) | (uint)dstX);
    }

    public void SubX(int dstX, int src1X, int src2X)
    {
        // SUB Xd, Xn, Xm -> 0xCB000000 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0xCB000000U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void SubXImm(int dstX, int srcX, uint imm12)
    {
        // SUB Xd, Xn, #imm12 -> 0xD1000000 | ((imm12 & 0xFFF) << 10) | (src << 5) | dst
        EmitUInt32(0xD1000000U | ((imm12 & 0xFFFU) << 10) | ((uint)srcX << 5) | (uint)dstX);
    }

    public void MulX(int dstX, int src1X, int src2X)
    {
        // MADD Xd, Xn, Xm, XZR -> 0x9B007C00 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0x9B007C00U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void AndX(int dstX, int src1X, int src2X)
    {
        // AND Xd, Xn, Xm -> 0x8A000000 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0x8A000000U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void OrrX(int dstX, int src1X, int src2X)
    {
        // ORR Xd, Xn, Xm -> 0xAA000000 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0xAA000000U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void EorX(int dstX, int src1X, int src2X)
    {
        // EOR Xd, Xn, Xm -> 0xCA000000 | (src2 << 16) | (src1 << 5) | dst
        EmitUInt32(0xCA000000U | ((uint)src2X << 16) | ((uint)src1X << 5) | (uint)dstX);
    }

    public void LslX(int dstX, int srcX, int shift)
    {
        shift &= 63;
        var immr = (64 - shift) & 63;
        var imms = 63 - shift;
        // UBFM Xd, Xn, #immr, #imms
        EmitUInt32(0xD3400000U | ((uint)immr << 16) | ((uint)imms << 10) | ((uint)srcX << 5) | (uint)dstX);
    }

    public void LsrX(int dstX, int srcX, int shift)
    {
        shift &= 63;
        // UBFM Xd, Xn, #shift, #63
        EmitUInt32(0xD3400000U | ((uint)shift << 16) | (63U << 10) | ((uint)srcX << 5) | (uint)dstX);
    }

    public void AsrX(int dstX, int srcX, int shift)
    {
        shift &= 63;
        // SBFM Xd, Xn, #shift, #63
        EmitUInt32(0x93400000U | ((uint)shift << 16) | (63U << 10) | ((uint)srcX << 5) | (uint)dstX);
    }

    public void CmpX(int reg1X, int reg2X)
    {
        // SUBS XZR, Xn, Xm -> 0xEB00001F | (src2 << 16) | (src1 << 5)
        EmitUInt32(0xEB00001FU | ((uint)reg2X << 16) | ((uint)reg1X << 5));
    }

    public void CmpXImm(int regX, uint imm12)
    {
        // SUBS XZR, Xn, #imm12 -> 0xF100001F | ((imm12 & 0xFFF) << 10) | (src << 5)
        EmitUInt32(0xF100001FU | ((imm12 & 0xFFFU) << 10) | ((uint)regX << 5));
    }

    // --- Load / Store Memory ---

    public void LdrX(int dstX, int baseX, int offset)
    {
        if (offset >= 0 && offset % 8 == 0 && offset < 32768)
        {
            var imm12 = (uint)(offset / 8);
            // LDR Xt, [Xn, #offset] (scaled)
            EmitUInt32(0xF9400000U | (imm12 << 10) | ((uint)baseX << 5) | (uint)dstX);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // LDUR Xt, [Xn, #simm9]
            EmitUInt32(0xF8400000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)dstX);
        }
    }

    public void StrX(int srcX, int baseX, int offset)
    {
        if (offset >= 0 && offset % 8 == 0 && offset < 32768)
        {
            var imm12 = (uint)(offset / 8);
            // STR Xt, [Xn, #offset] (scaled)
            EmitUInt32(0xF9000000U | (imm12 << 10) | ((uint)baseX << 5) | (uint)srcX);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // STUR Xt, [Xn, #simm9]
            EmitUInt32(0xF8000000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)srcX);
        }
    }

    public void LdrW(int dstW, int baseX, int offset)
    {
        if (offset >= 0 && offset % 4 == 0 && offset < 16384)
        {
            var imm12 = (uint)(offset / 4);
            // LDR Wt, [Xn, #offset] (scaled)
            EmitUInt32(0xB9400000U | (imm12 << 10) | ((uint)baseX << 5) | (uint)dstW);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // LDUR Wt, [Xn, #simm9]
            EmitUInt32(0xB8400000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)dstW);
        }
    }

    public void StrW(int srcW, int baseX, int offset)
    {
        if (offset >= 0 && offset % 4 == 0 && offset < 16384)
        {
            var imm12 = (uint)(offset / 4);
            // STR Wt, [Xn, #offset] (scaled)
            EmitUInt32(0xB9000000U | (imm12 << 10) | ((uint)baseX << 5) | (uint)srcW);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // STUR Wt, [Xn, #simm9]
            EmitUInt32(0xB8000000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)srcW);
        }
    }

    public void LdrB(int dstW, int baseX, int offset)
    {
        if (offset >= 0 && offset < 4096)
        {
            // LDRB Wt, [Xn, #offset]
            EmitUInt32(0x39400000U | ((uint)offset << 10) | ((uint)baseX << 5) | (uint)dstW);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // LDURB Wt, [Xn, #simm9]
            EmitUInt32(0x38400000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)dstW);
        }
    }

    public void StrB(int srcW, int baseX, int offset)
    {
        if (offset >= 0 && offset < 4096)
        {
            // STRB Wt, [Xn, #offset]
            EmitUInt32(0x39000000U | ((uint)offset << 10) | ((uint)baseX << 5) | (uint)srcW);
        }
        else
        {
            var simm9 = (uint)offset & 0x1FFU;
            // STURB Wt, [Xn, #simm9]
            EmitUInt32(0x38000000U | (simm9 << 12) | ((uint)baseX << 5) | (uint)srcW);
        }
    }

    // --- Control Flow ---

    public void Ret()
    {
        // RET -> 0xD65F03C0
        EmitUInt32(0xD65F03C0U);
    }

    public void Br(int regX)
    {
        // BR Xn -> 0xD61F0000 | (src << 5)
        EmitUInt32(0xD61F0000U | ((uint)regX << 5));
    }

    public void Blr(int regX)
    {
        // BLR Xn -> 0xD63F0000 | (src << 5)
        EmitUInt32(0xD63F0000U | ((uint)regX << 5));
    }

    public int B(int offsetInstructions = 0)
    {
        var pos = _position;
        // B offset -> 0x14000000 | (simm26 & 0x3FFFFFF)
        EmitUInt32(0x14000000U | ((uint)offsetInstructions & 0x03FFFFFFU));
        return pos;
    }

    public int Bcc(Arm64Condition cond, int offsetInstructions = 0)
    {
        var pos = _position;
        // B.cond offset -> 0x54000000 | ((simm19 & 0x7FFFF) << 5) | cond
        EmitUInt32(0x54000000U | (((uint)offsetInstructions & 0x7FFFFU) << 5) | (uint)cond);
        return pos;
    }

    public void PatchBranchOffset(int branchPos, int targetPos)
    {
        var offsetBytes = targetPos - branchPos;
        var offsetInst = offsetBytes / 4;
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(branchPos));

        if ((raw & 0xFC000000U) == 0x14000000U) // B
        {
            raw = (raw & 0xFC000000U) | ((uint)offsetInst & 0x03FFFFFFU);
        }
        else if ((raw & 0xFF000000U) == 0x54000000U) // B.cond
        {
            raw = (raw & 0xFF00001FU) | (((uint)offsetInst & 0x7FFFFU) << 5);
        }

        EmitUInt32At(branchPos, raw);
    }

    // --- Prologue / Epilogue / Stack Helpers ---

    public void PushPair(int reg1X, int reg2X)
    {
        // STP reg1X, reg2X, [SP, #-16]! -> pre-indexed pair store
        // 0xA9BF0000 | (reg2 << 10) | (31 << 5) | reg1
        EmitUInt32(0xA9BF0000U | ((uint)reg2X << 10) | (31U << 5) | (uint)reg1X);
    }

    public void PopPair(int reg1X, int reg2X)
    {
        // LDP reg1X, reg2X, [SP], #16 -> post-indexed pair load
        // 0xA8C10000 | (reg2 << 10) | (31 << 5) | reg1
        EmitUInt32(0xA8C10000U | ((uint)reg2X << 10) | (31U << 5) | (uint)reg1X);
    }
}
