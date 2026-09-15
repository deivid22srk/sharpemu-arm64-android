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
    public void BlockCache_IntraBlockSelfModifyingCodeExecutesPatchedInstruction()
    {
        // inst0 @+0 : mov dword ptr [rip+1], 0x2A   (C7 05 01 00 00 00 2A 00 00 00) — 10 bytes;
        //             writes to CodeBase+11, which is the immediate of inst1 below.
        // inst1 @+10: mov eax, 0x11111111           (B8 11 11 11 11) — patched to 0x2A by inst0.
        // inst2 @+15: ret
        // A pre-decoded block would run inst1 with its STALE immediate; the legacy path decodes
        // inst1 from the patched bytes and must see 0x2A. The cached path revalidates the block
        // after the store, drops it, and single-steps the patched instruction — same result.
        var code = new byte[16];
        code[0] = 0xC7; code[1] = 0x05;
        WriteInt32(code, 2, 1);           // rip-relative disp: target CodeBase+11 - (CodeBase+10)
        WriteInt32(code, 6, 0x2A);        // store immediate
        code[10] = 0xB8;                  // mov eax, imm32
        WriteInt32(code, 11, 0x1111_1111);
        code[15] = 0xC3;

        var memory = new VirtualMemory();
        memory.Map(CodeBase, 0x1000, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
        memory.Map(StackBase, StackSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var context = new CpuContext(memory, Generation.Gen5) { Rip = CodeBase, Rflags = 0x202 };
        context[CpuRegister.Rsp] = StackBase + StackSize;
        Assert.True(context.PushUInt64(0));

        var backend = new X64InterpreterBackend(new ModuleManager());
        var result = backend.Execute(context, CodeBase, new Dictionary<ulong, string>(), new X64InterpreterOptions { MaxInstructions = 1000 });

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result.Result);
        Assert.Equal(0x2AUL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void BlockCache_WritableRegionsValidateEveryExecution()
    {
        // VirtualMemory does not implement TryIsRegionNonWritable/MappingGeneration (interface
        // defaults), so this test verifies the FALLBACK contract: writable-validated blocks
        // still execute correctly across re-runs of the same backend. The generation-based
        // trust path has its own dedicated tests below (GenerationTrackingMemory).
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

    // ------------------------------------------------------------------ throughput

    [Fact]
    public void BlockCache_ThroughputBenchmark()
    {
        // Loop body = 8 straight-line adds + dec + jnz (10 instructions per block) — a
        // representative straight-line-heavy shape: the cached path pays one block validation
        // per iteration where the legacy path pays one per-instruction decode-cache validation
        // per instruction, on top of identical handler work.
        var code = new List<byte>
        {
            0x48, 0xC7, 0xC1, 0xA0, 0x86, 0x01, 0x00, // mov rcx, 100000
        };
        for (var i = 0; i < 8; i++)
        {
            code.AddRange([0x48, 0xFF, 0xC0]); // add rax, 1
        }

        code.AddRange([0x48, 0xFF, 0xC9]);         // dec rcx  (loop:)
        // jnz rel8 back to the first add (offset 7): rip after jnz = 36, rel8 = 7 - 36 = -29.
        code.AddRange([0x75, 0xE3]);
        code.Add(0xC3);                            // ret
        const int iterations = 100_000;
        // 1 (mov) + 10 per iteration + 1 (ret), plus headroom (see budget note below).
        var budget = 2 + iterations * 10 + 100;

        double RunPath(bool enableBlockCache)
        {
            var run = Execute(CreateContext([.. code]), enableBlockCache: enableBlockCache, options: new X64InterpreterOptions { MaxInstructions = budget });
            Assert.Equal(CpuExitReason.ReturnedToHost, run.Result.Reason);
            Assert.Equal((ulong)(8 * iterations), run.Context[CpuRegister.Rax]);
            return run.Stopwatch.ElapsedTicks;
        }

        // Warm both code paths first (JIT tiering + cache fill must not be measured), then
        // alternate runs and take medians, so neither path gets a systematic first/faster-order
        // advantage. No timing assertion beyond a conservative sanity floor — CI variance makes
        // tight timing assertions flaky.
        RunPath(enableBlockCache: false);
        RunPath(enableBlockCache: true);

        const int rounds = 5;
        var legacyTicks = new List<double>(rounds);
        var cachedTicks = new List<double>(rounds);
        for (var round = 0; round < rounds; round++)
        {
            legacyTicks.Add(RunPath(enableBlockCache: false));
            cachedTicks.Add(RunPath(enableBlockCache: true));
        }

        legacyTicks.Sort();
        cachedTicks.Sort();
        var legacyMedian = legacyTicks[rounds / 2];
        var cachedMedian = cachedTicks[rounds / 2];
        var speedup = legacyMedian / Math.Max(1, cachedMedian);
        _output.WriteLine(
            $"block-cache benchmark (median of {rounds}, 10-instruction block): " +
            $"legacy={legacyMedian:F0} ticks, cached={cachedMedian:F0} ticks, speedup={speedup:F2}x");

        Assert.True(legacyMedian > 0 && cachedMedian > 0);
    }

    // ------------------------------------------------------------------ invariant

    [Fact]
    public void BlockCache_SizeCapKeepsTwoPageWritabilityInvariant()
    {
        // TryBuildBlock classifies a block as non-writable by checking ONLY its two endpoints.
        // That is sound while a block spans at most two pages (its endpoints then cover every
        // page the block touches); this test pins the invariant so raising the size cap cannot
        // silently break it. Keep these in sync with X64InterpreterBackend's consts.
        Assert.True(X64InterpreterBackend.MaxBlockBytes <= 2 * 4096,
            $"MaxBlockBytes ({X64InterpreterBackend.MaxBlockBytes}) must not exceed two pages; " +
            "the both-ends writability check in TryBuildBlock assumes it.");
    }

    [Fact]
    public void BlockCache_TrustPathSkipsByteValidationWhileGenerationIsStable()
    {
        var code = new byte[] { 0x48, 0xFF, 0xC0, 0xC3 }; // inc rax; ret
        var memory = new GenerationTrackingMemory();
        memory.Map(CodeBase, 0x1000, code);
        memory.Map(StackBase, StackSize);
        memory.MarkNonWritable(CodeBase, 0x1000);

        var backend = new X64InterpreterBackend(new ModuleManager());
        var options = new X64InterpreterOptions { MaxInstructions = 1000 };

        // Round 1: cold — block is built and validated by bytes (code reads happen).
        var firstContext = CreateContext(memory);
        var first = backend.Execute(firstContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, first.Result);
        Assert.True(memory.CodeTryReadCallCount > 0);
        var readsAfterColdRun = memory.CodeTryReadCallCount;

        // Round 2: warm — the block is trusted (non-writable + generation unchanged), so the
        // dispatch path must NOT re-read/validate the code bytes at all. (Only code-region
        // reads are counted — stack/data reads belong to instruction execution, not decoding.)
        var secondContext = CreateContext(memory);
        var second = backend.Execute(secondContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, second.Result);
        Assert.Equal(1UL, secondContext[CpuRegister.Rax]);
        Assert.Equal(readsAfterColdRun, memory.CodeTryReadCallCount);

        // Round 3: after a generation bump (a structural remap anywhere), the trust fast path
        // must be dropped and the block revalidated by bytes exactly once, then trusted again.
        memory.BumpGeneration();
        var thirdContext = CreateContext(memory);
        var third = backend.Execute(thirdContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, third.Result);
        Assert.Equal(1UL, thirdContext[CpuRegister.Rax]);
        Assert.Equal(readsAfterColdRun + 1, memory.CodeTryReadCallCount);

        var fourthContext = CreateContext(memory);
        var fourth = backend.Execute(fourthContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, fourth.Result);
        Assert.Equal(1UL, fourthContext[CpuRegister.Rax]);
        Assert.Equal(readsAfterColdRun + 1, memory.CodeTryReadCallCount);
    }

    [Fact]
    public void BlockCache_TrustPathFollowsRegionThatBecameWritable()
    {
        var code = new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }; // mov eax, 1; ret
        var memory = new GenerationTrackingMemory();
        memory.Map(CodeBase, 0x1000, code);
        memory.Map(StackBase, StackSize);
        memory.MarkNonWritable(CodeBase, 0x1000);

        var backend = new X64InterpreterBackend(new ModuleManager());
        var options = new X64InterpreterOptions { MaxInstructions = 1000 };

        var firstContext = CreateContext(memory);
        var first = backend.Execute(firstContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, first.Result);
        Assert.Equal(1UL, firstContext[CpuRegister.Rax]);

        // The region is reprotected writable AND its bytes change — a generation bump is what
        // makes the trusted entry invalid; the byte-range validation must then see the new code.
        memory.MarkWritable(CodeBase, 0x1000);
        memory.BumpGeneration();
        Assert.True(memory.TryWrite(CodeBase, [0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3]));

        var secondContext = CreateContext(memory);
        var second = backend.Execute(secondContext, CodeBase, new Dictionary<ulong, string>(), options);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, second.Result);
        Assert.Equal(0x2AUL, secondContext[CpuRegister.Rax]);
    }

    // ------------------------------------------------------------------ stub boundary edges

    [Fact]
    public void BlockCache_Parity_FallThroughIntoImportStub()
    {
        // Two nops then fall through straight into the stub address: the block builder must
        // end the block BEFORE the stub, and the dispatcher must handle it like any stub entry.
        // AssertParity runs the program once per cache mode, so the stub dispatches exactly
        // twice (once for the cached run, once for the legacy run).
        var importStubs = new Dictionary<ulong, string> { [CodeBase + 2] = "nop_export" };
        var dispatchCount = 0;
        AssertParity(
            [0x90, 0x90],
            importStubs: importStubs,
            moduleManager: new CallbackModuleManager((_, _) => dispatchCount++),
            assertFinal: _ => Assert.Equal(2, dispatchCount));
    }

    [Fact]
    public void BlockCache_Parity_ConditionalBranchIntoImportStub()
    {
        // xor rax, rax; jz rel32 stub; ud2 — the taken jcc ends a block and enters the stub.
        // rel8 cannot reach StubAddress from CodeBase, hence the rel32 encoding (0F 84).
        var code = new byte[0x10];
        code[0] = 0x48; code[1] = 0x31; code[2] = 0xC0; // xor rax, rax
        code[3] = 0x0F; code[4] = 0x84;                 // jz rel32 (rip after = CodeBase+9)
        WriteInt32(code, 5, unchecked((int)(StubAddress - (CodeBase + 9))));
        code[9] = 0x0F; code[10] = 0x0B;                // ud2 (must never run)

        var importStubs = new Dictionary<ulong, string> { [StubAddress] = "jcc_export" };
        var dispatchCount = 0;
        AssertParity(
            code,
            importStubs: importStubs,
            moduleManager: new CallbackModuleManager((_, _) => dispatchCount++),
            assertFinal: _ => Assert.Equal(2, dispatchCount));
    }

    [Fact]
    public void BlockCache_Parity_RunLongerThanOneBlockSplitsCleanly()
    {
        // 80 nops (longer than the 64-instruction block cap) + mov eax, 42 + ret — exercises
        // the split path where a block ends at the size cap and the next block starts at the
        // fall-through RIP.
        var code = new List<byte>(84);
        for (var i = 0; i < 80; i++)
        {
            code.Add(0x90);
        }

        code.AddRange([0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3]);
        AssertParity([.. code], assertFinal: context => Assert.Equal(42UL, context[CpuRegister.Rax]));
    }

    [Fact]
    public void BlockCache_Parity_TraceTextIsIdentical()
    {
        var code = new byte[]
        {
            0xB8, 0x05, 0x00, 0x00, 0x00, // mov eax, 5
            0x83, 0xC0, 0x07,             // add eax, 7
            0xC3,                         // ret
        };

        var cachedContext = CreateContext(code);
        var legacyContext = CreateContext(code);
        var options = new X64InterpreterOptions { MaxInstructions = 1000, Trace = true };
        var cached = Execute(cachedContext, enableBlockCache: true, options: options);
        var legacy = Execute(legacyContext, enableBlockCache: false, options: options);

        AssertResultsEqual(legacy.Result, cached.Result);
        Assert.NotNull(cached.Result.Trace);
        Assert.Equal(legacy.Result.Trace, cached.Result.Trace);
    }

    private static CpuContext CreateContext(GenerationTrackingMemory memory)
    {
        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = CodeBase,
            Rflags = 0x202,
        };
        context[CpuRegister.Rsp] = StackBase + StackSize;
        Assert.True(context.PushUInt64(0));
        return context;
    }

    /// <summary>
    /// VirtualMemory wrapper implementing the SMC trust contract (MappingGeneration +
    /// TryIsRegionNonWritable) with an observable code-region read counter, so the block
    /// cache's generation-based fast path can be tested deterministically instead of only
    /// on-device. Only reads inside the code region are counted — stack/data reads belong to
    /// instruction execution, not to decode/validation.
    /// </summary>
    private sealed class GenerationTrackingMemory : ICpuMemory
    {
        private readonly VirtualMemory _inner = new();
        private readonly List<(ulong Start, ulong Length)> _nonWritableRanges = [];

        public int CodeTryReadCallCount;

        public long MappingGeneration { get; private set; }

        public void BumpGeneration() => MappingGeneration++;

        public void Map(ulong address, ulong length, byte[]? data = null) =>
            _inner.Map(address, length, 0, data is null ? ReadOnlySpan<byte>.Empty : data, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);

        public void MarkNonWritable(ulong address, ulong length) => _nonWritableRanges.Add((address, length));

        public void MarkWritable(ulong address, ulong length) =>
            _nonWritableRanges.RemoveAll(r => r.Start <= address && address < r.Start + r.Length);

        public bool TryIsRegionNonWritable(ulong address) =>
            _nonWritableRanges.Any(r => r.Start <= address && address < r.Start + r.Length);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress >= CodeBase && virtualAddress < CodeBase + 0x1000)
            {
                Interlocked.Increment(ref CodeTryReadCallCount);
            }

            return _inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => _inner.TryWrite(virtualAddress, source);
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
        if (legacy.Trace is not null && cached.Trace is not null)
        {
            Assert.Equal(legacy.Trace, cached.Trace);
        }
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
