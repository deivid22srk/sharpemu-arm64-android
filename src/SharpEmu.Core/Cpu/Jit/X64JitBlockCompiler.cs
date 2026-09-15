// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Jit;

public delegate ulong JitCompiledBlockFunc(IntPtr cpuContextPtr, IntPtr memoryPtr);

public sealed class X64JitBlockCompiler
{
    private const int MaxBlockInstructions = 100;
    private const int MaxInstructionBytes = 15;

    public unsafe struct CompiledBlockResult
    {
        public ulong StartRip;
        public ulong EndRip;
        public int InstructionCount;
        public byte[] NativeCode;
        public bool ContainsFallback;
        public string MnemonicList;
    }

    public static CompiledBlockResult CompileBlock(CpuContext context, ulong startRip)
    {
        var emitter = new Arm64Emitter(4096);
        var memory = context.Memory;
        var currentRip = startRip;
        var instCount = 0;
        var mnemonics = new List<string>();

        // ARM64 calling convention & register usage for compiled block:
        // Parameters:
        // X0 = CpuContext pointer
        // X1 = ICpuMemory pointer / handle (not strictly needed if CpuContext offset is used)
        // Saved registers: X19-X28 are callee-saved on AAPCS64.
        // Stack alignment: SP must be 16-byte aligned.

        // Prologue: Push callee-saved registers X19/X20 and FP/LR (X29/X30)
        emitter.PushPair(29, 30);
        emitter.PushPair(19, 20);

        // X19 = Pointer to raw CpuContext._registers array (fixed ulong*)
        emitter.MovX(19, 0);

        // Host Context register offsets in CpuContext (based on CpuRegister enum layout):
        // CpuRegister values: Rax=0, Rcx=1, Rdx=2, Rbx=3, Rsp=4, Rbp=5, Rsi=6, Rdi=7, R8..R15=8..15.
        // In CpuContext.Registers array, each entry is 8 bytes.

        Span<byte> instBytes = stackalloc byte[MaxInstructionBytes];
        var endOfBlock = false;

        while (instCount < MaxBlockInstructions && !endOfBlock)
        {
            if (!IcedDecoder.TryReadGuestBytes(memory, currentRip, MaxInstructionBytes, instBytes, out var readLength))
            {
                break;
            }

            var decoder = Decoder.Create(64, new ByteArrayCodeReader(instBytes[..readLength].ToArray()));
            decoder.IP = currentRip;
            decoder.Decode(out var instruction);

            if (instruction.Code == Code.INVALID || instruction.Length == 0)
            {
                break;
            }

            instCount++;
            mnemonics.Add(instruction.Mnemonic.ToString());

            // Emit instruction translation or helper call
            var nextRip = currentRip + (uint)instruction.Length;

            switch (instruction.Mnemonic)
            {
                case Mnemonic.Nop:
                case Mnemonic.Pause:
                    // No operation needed
                    break;

                case Mnemonic.Mov:
                    if (!EmitMov(emitter, instruction))
                    {
                        EmitFallbackHelper(emitter, currentRip);
                        endOfBlock = true;
                    }
                    break;

                case Mnemonic.Add:
                    if (!EmitAddSubLogical(emitter, instruction, isSub: false))
                    {
                        EmitFallbackHelper(emitter, currentRip);
                        endOfBlock = true;
                    }
                    break;

                case Mnemonic.Sub:
                    if (!EmitAddSubLogical(emitter, instruction, isSub: true))
                    {
                        EmitFallbackHelper(emitter, currentRip);
                        endOfBlock = true;
                    }
                    break;

                case Mnemonic.Cmp:
                    if (!EmitCmp(emitter, instruction))
                    {
                        EmitFallbackHelper(emitter, currentRip);
                        endOfBlock = true;
                    }
                    break;

                case Mnemonic.Jmp:
                    if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    {
                        var target = instruction.NearBranch64;
                        emitter.MovImm64(0, target);
                        endOfBlock = true;
                    }
                    else
                    {
                        EmitFallbackHelper(emitter, currentRip);
                        endOfBlock = true;
                    }
                    break;

                case Mnemonic.Ret:
                case Mnemonic.Call:
                case Mnemonic.Int3:
                case Mnemonic.Hlt:
                    // Terminal control flow -> fallback to dispatcher / interpreter return
                    EmitFallbackHelper(emitter, currentRip);
                    endOfBlock = true;
                    break;

                default:
                    // Any unhandled instruction ends the block via fallback to interpreter
                    EmitFallbackHelper(emitter, currentRip);
                    endOfBlock = true;
                    break;
            }

            currentRip = nextRip;

            if (instruction.FlowControl != FlowControl.Next)
            {
                endOfBlock = true;
            }
        }

        if (!endOfBlock)
        {
            // Block ended by instruction limit budget -> return nextRip to host
            emitter.MovImm64(0, currentRip);
        }

        // Epilogue
        emitter.PopPair(19, 20);
        emitter.PopPair(29, 30);
        emitter.Ret();

        return new CompiledBlockResult
        {
            StartRip = startRip,
            EndRip = currentRip,
            InstructionCount = instCount,
            NativeCode = emitter.ToArray(),
            ContainsFallback = endOfBlock,
            MnemonicList = string.Join(",", mnemonics),
        };
    }

    private static void EmitFallbackHelper(Arm64Emitter emitter, ulong targetRip)
    {
        // Return targetRip in X0 (RAX / Return register in AAPCS64)
        emitter.MovImm64(0, targetRip);
    }

    private static bool EmitMov(Arm64Emitter emitter, in Instruction instruction)
    {
        if (instruction.OpCount != 2) return false;

        // Reg to Reg Mov
        if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register)
        {
            var dstReg = GetCpuRegisterIndex(instruction.Op0Register);
            var srcReg = GetCpuRegisterIndex(instruction.Op1Register);

            if (dstReg >= 0 && srcReg >= 0)
            {
                // Ldr X10, [X19, #(srcReg * 8)] (CpuContext.Registers offset)
                emitter.LdrX(10, 19, srcReg * 8);
                // Str X10, [X19, #(dstReg * 8)]
                emitter.StrX(10, 19, dstReg * 8);
                return true;
            }
        }

        // Imm to Reg Mov
        if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind.ToString().StartsWith("Immediate", StringComparison.Ordinal))
        {
            var dstReg = GetCpuRegisterIndex(instruction.Op0Register);
            if (dstReg >= 0)
            {
                var imm = instruction.GetImmediate(1);
                emitter.MovImm64(10, imm);
                emitter.StrX(10, 19, dstReg * 8);
                return true;
            }
        }

        return false;
    }

    private static bool EmitAddSubLogical(Arm64Emitter emitter, in Instruction instruction, bool isSub)
    {
        if (instruction.OpCount != 2) return false;

        if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register)
        {
            var dstReg = GetCpuRegisterIndex(instruction.Op0Register);
            var srcReg = GetCpuRegisterIndex(instruction.Op1Register);

            if (dstReg >= 0 && srcReg >= 0)
            {
                emitter.LdrX(10, 19, dstReg * 8);
                emitter.LdrX(11, 19, srcReg * 8);
                if (isSub)
                {
                    emitter.SubX(10, 10, 11);
                }
                else
                {
                    emitter.AddX(10, 10, 11);
                }
                emitter.StrX(10, 19, dstReg * 8);
                return true;
            }
        }

        if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind.ToString().StartsWith("Immediate", StringComparison.Ordinal))
        {
            var dstReg = GetCpuRegisterIndex(instruction.Op0Register);
            if (dstReg >= 0)
            {
                var imm = instruction.GetImmediate(1);
                emitter.LdrX(10, 19, dstReg * 8);
                emitter.MovImm64(11, imm);
                if (isSub)
                {
                    emitter.SubX(10, 10, 11);
                }
                else
                {
                    emitter.AddX(10, 10, 11);
                }
                emitter.StrX(10, 19, dstReg * 8);
                return true;
            }
        }

        return false;
    }

    private static bool EmitCmp(Arm64Emitter emitter, in Instruction instruction)
    {
        if (instruction.OpCount != 2) return false;

        if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register)
        {
            var reg0 = GetCpuRegisterIndex(instruction.Op0Register);
            var reg1 = GetCpuRegisterIndex(instruction.Op1Register);
            if (reg0 >= 0 && reg1 >= 0)
            {
                emitter.LdrX(10, 19, reg0 * 8);
                emitter.LdrX(11, 19, reg1 * 8);
                emitter.CmpX(10, 11);
                return true;
            }
        }

        return false;
    }

    private static int GetCpuRegisterIndex(Register register)
    {
        return register switch
        {
            Register.RAX or Register.EAX or Register.AX or Register.AL => (int)CpuRegister.Rax,
            Register.RCX or Register.ECX or Register.CX or Register.CL => (int)CpuRegister.Rcx,
            Register.RDX or Register.EDX or Register.DX or Register.DL => (int)CpuRegister.Rdx,
            Register.RBX or Register.EBX or Register.BX or Register.BL => (int)CpuRegister.Rbx,
            Register.RSP or Register.ESP or Register.SP or Register.SPL => (int)CpuRegister.Rsp,
            Register.RBP or Register.EBP or Register.BP or Register.BPL => (int)CpuRegister.Rbp,
            Register.RSI or Register.ESI or Register.SI or Register.SIL => (int)CpuRegister.Rsi,
            Register.RDI or Register.EDI or Register.DI or Register.DIL => (int)CpuRegister.Rdi,
            Register.R8 or Register.R8D or Register.R8W or Register.R8L => (int)CpuRegister.R8,
            Register.R9 or Register.R9D or Register.R9W or Register.R9L => (int)CpuRegister.R9,
            Register.R10 or Register.R10D or Register.R10W or Register.R10L => (int)CpuRegister.R10,
            Register.R11 or Register.R11D or Register.R11W or Register.R11L => (int)CpuRegister.R11,
            Register.R12 or Register.R12D or Register.R12W or Register.R12L => (int)CpuRegister.R12,
            Register.R13 or Register.R13D or Register.R13W or Register.R13L => (int)CpuRegister.R13,
            Register.R14 or Register.R14D or Register.R14W or Register.R14L => (int)CpuRegister.R14,
            Register.R15 or Register.R15D or Register.R15W or Register.R15L => (int)CpuRegister.R15,
            _ => -1,
        };
    }
}
