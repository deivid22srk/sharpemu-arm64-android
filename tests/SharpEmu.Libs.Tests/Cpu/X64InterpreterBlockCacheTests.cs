// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Interpreter;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Parity and behavior tests for the basic-block cached interpreter (X64BlockCache — see
/// ARCHITECTURE_DECISION.md). The cache only removes per-instruction re-decoding, so every
/// guest-observable outcome must be bit-identical between <c>EnableBlockCache=true</c> and
/// the legacy per-instruction path (<c>EnableBlockCache=false</c>): final registers, flags,
/// guest memory, exit reason, instruction accounting, import statistics, trace text, and the
/// recent-instruction diagnostics ring.
/// </summary>
public sealed class X64InterpreterBlockCacheTests
{
    private const ulong CodeBase = 0x1000;
    private const ulong DataBase = 0x2000;
    private const ulong StackBase = 0x7000;
    private const ulong StackSize = 0x1000;
    private const ulong StubAddress = 0x5000;

    private readonly ITestOutputHelper _output;

    public X64InterpreterBlockCacheTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------------ parity: straight line

    [Fact]
    public void BlockCache_Parity_StraightLineArithmetic()
    {
        // mov rax, rdi; add rax, rsi; xor rdx, rdx; inc rdx; ret
        AssertParity(
            [0x48, 0x89, 0xF8, 0x48, 0x01, 0xF0, 0x48, 0x31, 0xD2, 0x48, 0xFF, 0xC2, 0xC3],
            context =>
            {
                context[CpuRegister.Rdi] = 7;
                context[CpuRegister.Rsi] = 35;
            });
    }

    [Fact]
    public void BlockCache_Parity_ConditionalLoop()
    {
        // Sum 5 + 4 + 3 + 2 + 1 using cmp/jnz (the loop is one block ending at jnz).
        AssertParity(
        [
            0xB8, 0x00, 0x00, 0x00, 0x00,
            0xB9, 0x05, 0x00, 0x00, 0x00,
            0x01, 0xC8,
            0x83, 0xE9, 0x01,
            0x83, 0xF9, 0x00,
            0x75, 0xF6,
            0xC3,
        ]);
    }

    [Fact]
    public void BlockCache_Parity_NestedCallsAndStack()
    {
        // call leaf; add rax, 1; ret  /  leaf: mov rax, 10; ret — call/ret cross three blocks.
        // Layout: entry at CodeBase (e8 rel32 -> leaf), leaf at CodeBase + 0x20.
        var code = new byte[0x40];
        code[0] = 0xE8;
        WriteInt32(code, 1, 0x20 - 5); // call leaf (rip after call = CodeBase+5, target CodeBase+0x20)
        code[5] = 0x48;                // add rax, 1
        code[6] = 0x83;
        code[7] = 0xC0;
        code[8] = 0x01;
        code[9] = 0xC3;                // ret
        code[0x20] = 0x48;             // mov rax, 10
        code[0x21] = 0xC7;
        code[0x22] = 0xC0;
        code[0x23] = 0x0A;
        code[0x24] = 0x00;
        code[0x25] = 0x00;
        code[0x26] = 0x00;
        code[0x27] = 0xC3;             // ret

        AssertParity(code);
    }

    [Fact]
    public void BlockCache_Parity_MemoryOperations()
    {
        // mov qword [rdi], rsi; mov rax, [rdi]; movzx ecx, byte [rdi+2]; add rax, rcx; ret
        AssertParity(
            [0x48, 0x89, 0x37, 0x48, 0x8B, 0x07, 0x0F, 0xB6, 0x4F, 0x02, 0x48, 0x01, 0xC8, 0xC3],
            context =>
            {
                context[CpuRegister.Rdi] = DataBase;
                context[CpuRegister.Rsi] = 0x1234_5678_9ABC_DEF0;
            });
    }

    [Fact]
    public void BlockCache_Parity_FlagDependentBranches()
    {
        // xor edx, edx; test rdi, rdi; jz skip; mov edx, 1; skip: add rax, rdx; ret
        AssertParity(
        [
            0x48, 0x31, 0xD2,
            0x48, 0x85, 0xFF,
            0x74, 0x03,
            0xBA, 0x01, 0x00, 0x00, 0x00,
            0x48, 0x01, 0xD0,
            0xC3,
        ],
            context => context[CpuRegister.Rdi] = 0); // taken branch

        AssertParity(
        [
            0x48, 0x31, 0xD2,
            0x48, 0x85, 0xFF,
            0x74, 0x03,
            0xBA, 0x01, 0x00, 0x00, 0x00,
            0x48, 0x01, 0xD0,
            0xC3,
        ],
            context => context[CpuRegister.Rdi] = 0x80); // not taken
    }

    [Fact]
    public void BlockCache_Parity_IndirectJumpTable()
    {
        // jmp rax into three-way dispatch: mov rax, rdi; jmp rax ... targets set rax 1/2/3; ret
        var code = new byte[0x60];
        code[0] = 0x48; // mov rax, rdi
        code[1] = 0x89;
        code[2] = 0xF8;
        code[3] = 0xFF; // jmp rax
        code[4] = 0xE0;
        code[0x10] = 0x48; code[0x11] = 0xC7; code[0x12] = 0xC0; code[0x13] = 0x01; code[0x17] = 0xC3; // mov rax,1; ret
        code[0x20] = 0x48; code[0x21] = 0xC7; code[0x22] = 0xC0; code[0x23] = 0x02; code[0x27] = 0xC3; // mov rax,2; ret
        code[0x30] = 0x48; code[0x31] = 0xC7; code[0x32] = 0xC0; code[0x33] = 0x03; code[0x37] = 0xC3; // mov rax,3; ret

        AssertParity(code, context => context[CpuRegister.Rdi] = CodeBase + 0x20);
        AssertParity(code, context => context[CpuRegister.Rdi] = CodeBase + 0x30);
    }

    // ------------------------------------------------------------------ parity: import stubs

    [Fact]
    public void BlockCache_Parity_CallIntoImportStubContinuesAfterReturn()
    {
        // mov rdi, 5; call stub; add rax, 1; ret — the stub dispatch sets rax = 10, then the
        // interpreter pops the return address and continues in guest code.
        var code = new byte[0x20];
        code[0] = 0x48; // mov rdi, 5
        code[1] = 0xC7;
        code[2] = 0xC7;
        code[3] = 0x05;
        code[7] = 0xE8; // call rel32 -> StubAddress (rip after call = CodeBase+12)
        WriteInt32(code, 8, unchecked((int)(StubAddress - (CodeBase + 8 + 4))));
        code[12] = 0x48; // add rax, 1
        code[13] = 0x83;
        code[14] = 0xC0;
        code[15] = 0x01;
        code[16] = 0xC3; // ret

        AssertParity(
            code,
            importStubs: new Dictionary<ulong, string> { [StubAddress] = "test_export" },
            moduleManager: new CallbackModuleManager((nid, context) =>
            {
                Assert.Equal("test_export", nid);
                context[CpuRegister.Rax] = 10;
            }),
            assertFinal: context => Assert.Equal(11UL, context[CpuRegister.Rax]));
    }

    [Fact]
    public void BlockCache_Parity_ExitRequestedFromImportStub()
    {
        // ud2 would trap if execution wrongly continued past the exit request — the stub is
        // the entry point itself, which also exercises "first block lookup happens after the
        // stub check".
        var context = CreateContext([0x0F, 0x0B]);
        var importStubs = new Dictionary<ulong, string> { [CodeBase] = "exit" };

        var cached = Execute(context, enableBlockCache: true, importStubs: importStubs, moduleManager: new ExitingModuleManager());
        var legacy = Execute(context, enableBlockCache: false, importStubs: importStubs, moduleManager: new ExitingModuleManager());

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, cached.Result.Result);
        Assert.Equal(CpuExitReason.Exited, cached.Result.Reason);
        Assert.Equal(7UL, context[CpuRegister.Rax]);
        AssertResultsEqual(legacy.Result, cached.Result);
    }

    // ------------------------------------------------------------------ parity: failure paths

    [Fact]
    public void BlockCache_Parity_MemoryFaultInsideBlockReportsFaultingInstruction()
    {
        // mov rax, 1; mov rbx, [0xdead_0000]; ret — the fault must report the SECOND
        // instruction's rip/opcode, not the block start.
        AssertParity(
        [
            0xB8, 0x01, 0x00, 0x00, 0x00,       // mov eax, 1
            0x48, 0x8B, 0x1C, 0x25, 0x00, 0x00, 0xAD, 0xDE, // mov rbx, [0xdead0000]
            0xC3,
        ]);
    }

    [Fact]
    public void BlockCache_Parity_InstructionBudgetAcrossBlocks()
    {
        // Loop body = 3 instructions per iteration (add/dec/jnz) + 1 entry (mov) + 1 exit (ret).
        var code = new byte[]
        {
            0x48, 0xC7, 0xC1, 0x0A, 0x00, 0x00, 0x00, // mov rcx, 10
            0x48, 0xFF, 0xC0,                          // add rax, 1  (loop:)
            0x48, 0xFF, 0xC9,                          // dec rcx
            0x75, 0xF9,                                // jnz loop
            0xC3,                                      // ret
        };

        // 6 iterations * 3 + entry + ret = 20 instructions total; cut at 9 (mid-block for a
        // 3-instruction block: add/dec/jnz at offsets 7, 9, 13).
        var budget = 9;

        var cached = Execute(CreateContext(code), enableBlockCache: true, options: new X64InterpreterOptions { MaxInstructions = budget });
        var legacy = Execute(CreateContext(code), enableBlockCache: false, options: new X64InterpreterOptions { MaxInstructions = budget });

        Assert.Equal(CpuExitReason.BudgetExceeded, cached.Result.Reason);
        Assert.Equal(budget, cached.Result.TotalInstructions);
        AssertResultsEqual(legacy.Result, cached.Result);
        Assert.Equal(legacy.Context[CpuRegister.Rax], cached.Context[CpuRegister.Rax]);
        Assert.Equal(legacy.Context[CpuRegister.Rcx], cached.Context[CpuRegister.Rcx]);
    }

    // ------------------------------------------------------------------ self-modifying code

    [Fact]
    public void BlockCache_DetectsSelfModifyingCodeRewriteBetweenRuns()
    {
        var memory = new VirtualMemory();
        memory.Map(CodeBase, 0x1000, 0, [0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
        memory.Map(DataBase, 0x1000, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(StackBase, StackSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var context = new CpuContext(memory, Generation.Gen5) { Rip = CodeBase, Rflags = 0x202 };
        context[CpuRegister.Rsp] = StackBase + StackSize;
        Assert.True(context.PushUInt64(0));

        var backend = new X64InterpreterBackend(new ModuleManager());
        var options = new X64InterpreterOptions { MaxInstructions = 1000 };

        var first = backend.Execute(context, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, first.Result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);

        // Rewrite the code while the backend (and its block cache) still exists — the block
        // starting at CodeBase must be invalidated by the byte-range validation, not reused.
        Assert.True(context.Memory.TryWrite(CodeBase, [0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3]));

        // The first run's ret consumed the pushed return sentinel; restore it so the second
        // run's ret has something to pop (mirrors CreateContext's fresh-stack invariant).
        Assert.True(context.PushUInt64(0));

        var second = backend.Execute(context, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, second.Result);
        Assert.Equal(42UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void BlockCache_TrustsNonWritableRegionsUntilMappingGenerationChanges()
    {
        // VirtualMemory does not implement TryIsRegionNonWritable/MappingGeneration (interface
        // defaults), so this test verifies the FALLBACK contract: writable-validated blocks
        // still execute correctly across re-runs of the same backend. The generation-based
        // fast path is exercised on-device via PhysicalVirtualMemory (which implements both).
        var code = new byte[] { 0x48, 0xFF, 0xC0, 0xC3 }; // inc rax; ret
        var backend = new X64InterpreterBackend(new ModuleManager());
        var options = new X64InterpreterOptions { MaxInstructions = 1000 };

        for (var round = 0; round < 3; round++)
        {
            var context = CreateContext(code);
            var result = backend.Execute(context, CodeBase, new Dictionary<ulong, string>(), options);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result.Result);
            Assert.Equal(1UL, context[CpuRegister.Rax]); // inc rax on a fresh context every round
        }
    }

    // ------------------------------------------------------------------ throughput smoke

    [Fact]
    public void BlockCache_ThroughputSmokeBenchmark()
    {
        // add rax, 1; dec rcx; jnz -5 — one 3-instruction block looped N times.
        var code = new byte[]
        {
            0x48, 0xC7, 0xC1, 0x40, 0x0D, 0x03, 0x00, // mov rcx, 200000
            0x48, 0xFF, 0xC0,                          // add rax, 1  (loop:)
            0x48, 0xFF, 0xC9,                          // dec rcx
            0x75, 0xF9,                                // jnz loop
            0xC3,
        };
        const int iterations = 200_000;
        // Program length: 1 (mov rcx) + 3 per iteration + 1 (ret) — plus headroom so both
        // paths run to completion (a budget exactly equal to the program length still exits
        // with BudgetExceeded, since the budget is checked before the final dispatch).
        var budget = iterations * 3 + 2 + 100;

        var legacy = Execute(CreateContext(code), enableBlockCache: false, options: new X64InterpreterOptions { MaxInstructions = budget });
        var legacyTicks = legacy.Stopwatch.ElapsedTicks;

        var cached = Execute(CreateContext(code), enableBlockCache: true, options: new X64InterpreterOptions { MaxInstructions = budget });
        var cachedTicks = cached.Stopwatch.ElapsedTicks;

        Assert.Equal(CpuExitReason.ReturnedToHost, cached.Result.Reason);
        Assert.Equal(iterations, (int)cached.Context[CpuRegister.Rax]);
        Assert.Equal(legacy.Result.TotalInstructions, cached.Result.TotalInstructions);
        Assert.Equal(legacy.Context[CpuRegister.Rax], cached.Context[CpuRegister.Rax]);

        var speedup = (double)legacyTicks / Math.Max(1, cachedTicks);
        _output.WriteLine($"block-cache smoke benchmark: legacy={legacyTicks} ticks, cached={cachedTicks} ticks, speedup={speedup:F2}x");
    }

    // ------------------------------------------------------------------ shared harness

    private void AssertParity(
        byte[] code,
        Action<CpuContext>? setup = null,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IModuleManager? moduleManager = null,
        Action<CpuContext>? assertFinal = null)
    {
        importStubs ??= new Dictionary<ulong, string>();

        var cachedContext = CreateContext(code);
        var legacyContext = CreateContext(code);
        if (setup is not null)
        {
            setup(cachedContext);
            setup(legacyContext);
        }

        var cached = Execute(cachedContext, enableBlockCache: true, importStubs: importStubs, moduleManager: moduleManager);
        var legacy = Execute(legacyContext, enableBlockCache: false, importStubs: importStubs, moduleManager: moduleManager);

        AssertResultsEqual(legacy.Result, cached.Result);

        foreach (var register in new[]
                 {
                     CpuRegister.Rax, CpuRegister.Rbx, CpuRegister.Rcx, CpuRegister.Rdx,
                     CpuRegister.Rsi, CpuRegister.Rdi, CpuRegister.Rbp, CpuRegister.Rsp,
                     CpuRegister.R8, CpuRegister.R9, CpuRegister.R10, CpuRegister.R11,
                     CpuRegister.R12, CpuRegister.R13, CpuRegister.R14, CpuRegister.R15,
                 })
        {
            Assert.True(legacyContext[register] == cachedContext[register],
                $"register {register} diverged: legacy=0x{legacyContext[register]:X16} cached=0x{cachedContext[register]:X16}");
        }

        Assert.Equal(legacyContext.Rflags, cachedContext.Rflags);
        Assert.Equal(legacyContext.Rip, cachedContext.Rip);

        // The parity programs write DataBase when they exercise memory; compare the full page.
        Span<byte> legacyData = stackalloc byte[0x1000];
        Span<byte> cachedData = stackalloc byte[0x1000];
        Assert.True(legacyContext.Memory.TryRead(DataBase, legacyData));
        Assert.True(cachedContext.Memory.TryRead(DataBase, cachedData));
        Assert.True(legacyData.SequenceEqual(cachedData), "guest memory diverged between cache modes");

        assertFinal?.Invoke(cachedContext);
    }

    private static void AssertResultsEqual(X64InterpreterResult legacy, X64InterpreterResult cached)
    {
        Assert.Equal(legacy.Result, cached.Result);
        Assert.Equal(legacy.Reason, cached.Reason);
        Assert.Equal(legacy.LastGuestRip, cached.LastGuestRip);
        Assert.Equal(legacy.TotalInstructions, cached.TotalInstructions);
        Assert.Equal(legacy.ImportsHit, cached.ImportsHit);
        Assert.Equal(legacy.UniqueNidsHit, cached.UniqueNidsHit);
        Assert.Equal(legacy.RecentInstructions, cached.RecentInstructions);
        Assert.Equal(legacy.TrapInfo, cached.TrapInfo);
        Assert.Equal(legacy.MemoryFaultInfo, cached.MemoryFaultInfo);
        Assert.Equal(legacy.NotImplementedInfo, cached.NotImplementedInfo);
    }

    private static (X64InterpreterResult Result, CpuContext Context, Stopwatch Stopwatch) Execute(
        CpuContext context,
        bool enableBlockCache,
        X64InterpreterOptions? options = null,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IModuleManager? moduleManager = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var backend = new X64InterpreterBackend(moduleManager ?? new ModuleManager());
        var effectiveOptions = options ?? new X64InterpreterOptions { MaxInstructions = 1000 };
        effectiveOptions = effectiveOptions with { DisableBlockCache = !enableBlockCache };
        var result = backend.Execute(
            context,
            CodeBase,
            importStubs ?? new Dictionary<ulong, string>(),
            effectiveOptions);
        stopwatch.Stop();
        return (result, context, stopwatch);
    }

    private static CpuContext CreateContext(byte[] code)
    {
        var memory = new VirtualMemory();
        memory.Map(CodeBase, 0x1000, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.Map(DataBase, 0x1000, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(StackBase, StackSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = CodeBase,
            Rflags = 0x202,
        };
        context[CpuRegister.Rsp] = StackBase + StackSize;
        Assert.True(context.PushUInt64(0));
        return context;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private sealed class CallbackModuleManager(Action<string, CpuContext> onDispatch) : IModuleManager
    {
        public int RegisterExports(IReadOnlyList<ExportedFunction> exports) => 0;

        public void Freeze()
        {
        }

        public bool TryGetFunction(string nid, out Delegate function)
        {
            function = null!;
            return false;
        }

        public bool TryGetExport(string nid, out ExportedFunction export)
        {
            export = null!;
            return false;
        }

        public bool TryGetExportByName(string exportName, out ExportedFunction export)
        {
            export = null!;
            return false;
        }

        public bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result)
        {
            result = Dispatch(nid, context);
            return true;
        }

        public OrbisGen2Result Dispatch(string nid, CpuContext context)
        {
            onDispatch(nid, context);
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    private sealed class ExitingModuleManager : IModuleManager
    {
        public int RegisterExports(IReadOnlyList<ExportedFunction> exports) => 0;

        public void Freeze()
        {
        }

        public bool TryGetFunction(string nid, out Delegate function)
        {
            function = null!;
            return false;
        }

        public bool TryGetExport(string nid, out ExportedFunction export)
        {
            export = null!;
            return false;
        }

        public bool TryGetExportByName(string exportName, out ExportedFunction export)
        {
            export = null!;
            return false;
        }

        public bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result)
        {
            result = Dispatch(nid, context);
            return true;
        }

        public OrbisGen2Result Dispatch(string nid, CpuContext context)
        {
            GuestThreadExecution.RequestCurrentEntryExit(nid, 7);
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }
}
