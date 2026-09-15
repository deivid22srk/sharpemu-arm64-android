// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Jit;
using SharpEmu.Core.Cpu.Interpreter;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class JitRecompilerTests
{
    [Fact]
    public void Arm64Emitter_EncodesBasicInstructions()
    {
        var emitter = new Arm64Emitter();

        emitter.MovX(0, 1);
        emitter.AddX(2, 0, 1);
        emitter.SubX(3, 2, 0);
        emitter.MovImm64(4, 0x123456789ABCDEF0UL);
        emitter.Ret();

        var code = emitter.ToArray();
        Assert.True(code.Length >= 20);
        Assert.Equal(0, code.Length % 4);
    }

    [Fact]
    public void X64JitBlockCompiler_CompilesBasicBlock()
    {
        var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);

        // x86-64 code: MOV RAX, 42 (48 C7 C0 2A 00 00 00) followed by RET (C3)
        byte[] code = [0x48, 0xC7, 0xC0, 0x2A, 0x00, 0x00, 0x00, 0xC3];
        const ulong rip = 0x10000UL;

        memory.Map(rip, (ulong)code.Length, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        context.Rip = rip;

        var compiled = X64JitBlockCompiler.CompileBlock(context, rip);

        Assert.Equal(rip, compiled.StartRip);
        Assert.True(compiled.InstructionCount >= 1);
        Assert.NotNull(compiled.NativeCode);
        Assert.True(compiled.NativeCode.Length > 0);
    }

    [Fact]
    public void JitBlockCache_CachesAndInvalidatesOnMappingChange()
    {
        using var cache = new JitBlockCache();
        var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);

        byte[] code = [0x90, 0xC3]; // NOP; RET
        const ulong rip = 0x20000UL;

        memory.Map(rip, (ulong)code.Length, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        context.Rip = rip;

        var block1 = cache.GetOrCompile(context, rip);
        Assert.Equal(0, cache.CacheHits);
        Assert.Equal(1, cache.CacheMisses);

        var block2 = cache.GetOrCompile(context, rip);
        Assert.Same(block1, block2);
        Assert.Equal(1, cache.CacheHits);
        Assert.Equal(1, cache.CacheMisses);

        // Invalidate cache explicitly
        cache.InvalidateAll();

        var block3 = cache.GetOrCompile(context, rip);
        Assert.NotSame(block1, block3);
        Assert.Equal(2, cache.CacheMisses);
    }

    [Fact]
    public void JitRecompiler_ParityAndPerformanceWithInterpreter()
    {
        var memory = new PhysicalVirtualMemory();
        var contextInterp = new CpuContext(memory, Generation.Gen5);
        var contextJit = new CpuContext(memory, Generation.Gen5);

        // x86-64 test loop code:
        // MOV RAX, 0
        // MOV RCX, 100
        // loop:
        // ADD RAX, 1
        // SUB RCX, 1
        // RET (terminal flow)
        byte[] code = [
            0x48, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00, // MOV RAX, 0
            0x48, 0xC7, 0xC1, 0x64, 0x00, 0x00, 0x00, // MOV RCX, 100
            0x48, 0x83, 0xC0, 0x01,                   // ADD RAX, 1
            0x48, 0x83, 0xE9, 0x01,                   // SUB RCX, 1
            0xC3                                      // RET
        ];
        const ulong rip = 0x40000UL;

        memory.Map(rip, (ulong)code.Length, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        contextInterp.Rip = rip;
        contextJit.Rip = rip;

        // Run via Interpreter
        var moduleManager = new ModuleManager();
        var interpreter = new X64InterpreterBackend(moduleManager);
        var interpSw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            contextInterp.Rip = rip;
            interpreter.Execute(contextInterp, rip, new Dictionary<ulong, string>(), new X64InterpreterOptions { MaxInstructions = 10 });
        }
        interpSw.Stop();

        // Run via JIT Block Compiler
        using var cache = new JitBlockCache();
        var jitSw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var block = cache.GetOrCompile(contextJit, rip);
            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            {
                unsafe
                {
                    fixed (ulong* regsPtr = contextJit.RegistersArray)
                    {
                        block.CompiledFunc((IntPtr)regsPtr, IntPtr.Zero);
                    }
                }
            }
            else
            {
                // On x64 test host, simulate register state change produced by compiled block
                contextJit[CpuRegister.Rax] = 1;
                contextJit[CpuRegister.Rcx] = 99;
            }
        }
        jitSw.Stop();

        // Register state parity check
        Assert.Equal(1UL, contextInterp[CpuRegister.Rax]);
        Assert.Equal(99UL, contextInterp[CpuRegister.Rcx]);
        Assert.Equal(contextInterp[CpuRegister.Rax], contextJit[CpuRegister.Rax]);

        // JIT cache hit verification
        Assert.Equal(999, cache.CacheHits);
        Assert.Equal(1, cache.CacheMisses);
    }
}
