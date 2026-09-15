// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Iced.Intel;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Interpreter;

/// <summary>
/// Basic-block decode cache for <see cref="X64InterpreterBackend"/> (these are partial-class
/// members; the class itself lives in X64InterpreterBackend.cs).
///
/// The per-instruction <c>_decodeCache</c> removed the single most expensive step of the
/// interpreter loop — Iced re-decoding — but every executed instruction still paid, every
/// single time: the hashed cache probe, the fresh-bytes read for the self-modifying-code
/// guard, the byte comparison, the import-stub dictionary probe, and the ring-buffer push
/// bookkeeping around the mnemonic switch. This cache lifts all of that to <em>basic
/// block</em> granularity: a straight-line run of decoded instructions (ending at a control
/// transfer, an import-stub address, the size cap, or an undecodable byte) is decoded once
/// and validated as one contiguous byte range per execution.
///
/// This is the "cached interpreter" design point (the same stepping stone Dolphin's cached
/// interpreter and PPSSPP's IR JIT sit on): execution still goes through the exact same
/// <see cref="X64InterpreterBackend.TryExecuteInstruction"/> handlers the interpreter has
/// always used — no code is emitted, so guest-visible semantics cannot diverge from the
/// legacy per-instruction path by construction. It is also, structurally, the skeleton of
/// the future ARM64 JIT recompiler: same cache key (guest RIP + mapping generation), same
/// self-modifying-code invalidation contract (<see cref="ICpuMemory.MappingGeneration"/> /
/// <see cref="ICpuMemory.TryIsRegionNonWritable"/>), same block boundaries a translating
/// backend would use. See ARCHITECTURE_DECISION.md for the full rationale.
///
/// The backend is instantiated per guest thread (see X64InterpreterGuestThreadScheduler), so
/// like <c>_decodeCache</c> this state needs no locking and is not shared across threads.
/// </summary>
public sealed partial class X64InterpreterBackend
{
    /// <summary>
    /// Upper bound on instructions per cached block. Hot loops and straight-line prologues
    /// are far smaller than this; the cap only bounds worst-case memory per block and the
    /// validation buffer. A run longer than this simply splits into consecutive blocks (the
    /// next block starts at the fall-through RIP).
    /// INVARIANT (SMC safety): a block of at most <see cref="MaxBlockBytes"/> bytes spans at
    /// most 2 pages, so checking writability at the block's two endpoints covers every page
    /// the block touches. Raising the size cap past 2 pages (8 KiB) would silently break the
    /// both-ends-writability check in <see cref="TryBuildBlock"/>.
    /// </summary>
    public const int MaxBlockInstructions = 64;

    /// <summary>Upper bound on encoded bytes per block (worst case: 15-byte instructions).</summary>
    public const int MaxBlockBytes = MaxBlockInstructions * MaxInstructionBytes;

    /// <summary>
    /// Entry cap for the block cache. Blocks are small but unbounded growth on long sessions
    /// with dynamically loaded/unloaded guest code (dlopen/dlclose, guest JIT rewrites) would
    /// leak; this bounds the dictionary at decode-cache order of magnitude (the legacy decode
    /// cache is 65,536 fixed slots per thread).
    /// </summary>
    private const int MaxCachedBlocks = 32 * 1024;

    private readonly Dictionary<ulong, CachedBlock> _blockCache = new();

    /// <summary>
    /// Reusable scratch buffer for block byte-range validation reads that exceed the
    /// stackalloc threshold (256 bytes). The backend is per guest thread (see class doc), so
    /// no locking is needed; reusing one MaxBlockBytes-sized buffer keeps the >256-byte
    /// revalidation path allocation-free instead of heap-allocating on every execution of a
    /// large block in a writable (byte-validated) region.
    /// </summary>
    private readonly byte[] _blockValidationScratch = new byte[MaxBlockBytes];

    /// <summary>
    /// One cached basic block: a contiguous byte range of guest code plus its decoded
    /// instructions. <see cref="InstructionBytes"/> keeps the exact encoded bytes of each
    /// instruction (not a slice into <see cref="AllBytes"/>) because the diagnostics ring
    /// (<see cref="PushRecent"/>) retains byte[] references long after the block was last
    /// executed, and re-slicing per executed instruction would reintroduce an allocation on
    /// the hot path this cache exists to remove.
    /// </summary>
    private sealed class CachedBlock
    {
        public required ulong StartRip { get; init; }

        public required int InstructionCount { get; init; }

        public required Instruction[] Instructions { get; init; }

        public required byte[][] InstructionBytes { get; init; }

        /// <summary>
        /// Parallel to <see cref="Instructions"/>: which instructions write guest memory
        /// (stores, pushes, calls). Executing any of them revalidates the block's byte range
        /// before the next cached instruction runs, closing the self-modifying-code window
        /// a pre-decoded block would otherwise open (see TryExecuteBlock).
        /// </summary>
        public required bool[] WritesMemory { get; init; }

        /// <summary>Concatenated encoded bytes of the whole block — the SMC validation image.</summary>
        public required byte[] AllBytes { get; init; }

        public required int TotalLength { get; init; }

        /// <summary>
        /// Set when every byte of the block lies in a region ordinary guest stores cannot
        /// write (self-modifying code is architecturally impossible there). Paired with
        /// <see cref="CachedGeneration"/> — the same safety contract the per-instruction
        /// decode cache uses (see ICpuMemory.MappingGeneration's doc comment).
        /// </summary>
        public bool IsNonWritable { get; set; }

        public long CachedGeneration { get; set; }
    }

    /// <summary>Outcome of executing one cached block.</summary>
    private enum BlockExecutionOutcome
    {
        /// <summary>All instructions of the block executed; dispatch continues at the fall-through RIP.</summary>
        Completed,

        /// <summary>Execution ended (fault/trap/unsupported instruction or instruction budget exhausted); <c>result</c> is authoritative.</summary>
        ReturnResult,

        /// <summary>A defensive invariant failed (register-state desync or a non-terminal instruction unexpectedly changed RIP); the dispatcher must skip the block cache once and single-step the offending instruction through the legacy path.</summary>
        RetryFromDispatcher,
    }

    /// <summary>
    /// Looks up (or builds) the cached block starting at <paramref name="rip"/> and validates
    /// it against self-modifying code. Validation mirrors the per-instruction decode cache's
    /// two-tier policy, at block granularity: blocks cached from non-writable regions are
    /// trusted while <see cref="ICpuMemory.MappingGeneration"/> is unchanged (no guest-memory
    /// read at all); everything else re-reads the block's contiguous byte range once per
    /// execution and compares it against the decode-time image. A block that fails validation
    /// (guest rewrote its code, or the region was unmapped) is dropped and rebuilt on the next
    /// dispatch; the caller falls back to the legacy per-instruction path for this iteration.
    /// </summary>
    private bool TryGetValidatedBlock(
        CpuContext context,
        ulong rip,
        IReadOnlyDictionary<ulong, string> importStubs,
        [NotNullWhen(true)] out CachedBlock? block)
    {
        if (_blockCache.TryGetValue(rip, out var cached))
        {
            var generation = context.Memory.MappingGeneration;
            if (cached.IsNonWritable && generation == cached.CachedGeneration)
            {
                block = cached;
                return true;
            }

            if (ValidateBlockBytes(context, cached))
            {
                // Bytes still match. Refresh the non-writable classification and the generation
                // stamp so a block that had to byte-validate once (because some unrelated mapping
                // changed) can take the no-read fast path again until the next structural change.
                // Recomputing writability here is required, not an optimization: a generation bump
                // may have reprotected the region.
                cached.IsNonWritable =
                    context.Memory.TryIsRegionNonWritable(rip) &&
                    context.Memory.TryIsRegionNonWritable(rip + (ulong)cached.TotalLength - 1);
                cached.CachedGeneration = generation;
                block = cached;
                return true;
            }

            // Stale (code rewritten, or the region is no longer fully mapped). Drop and let the
            // next dispatch rebuild from the fresh bytes — identical recovery to the decode cache.
            _blockCache.Remove(rip);
            block = null;
            return false;
        }

        if (TryBuildBlock(context, rip, importStubs, out var built))
        {
            if (_blockCache.Count >= MaxCachedBlocks)
            {
                // Bounded-ness sweep: rebuilding a block is cheap (it is a decode-once cache) and
                // hot code re-caches on its very next dispatch, so a full clear is a deliberately
                // crude but safe way to keep the dictionary bounded on sessions that churn code.
                _blockCache.Clear();
            }

            _blockCache[rip] = built;
            block = built;
            return true;
        }

        block = null;
        return false;
    }

    /// <summary>
    /// Re-reads the block's contiguous byte range once and compares it against the decode-time
    /// image (the self-modifying-code guard for blocks in writable regions). Allocation-free:
    /// short ranges use a stackalloc window, long ranges reuse the per-backend scratch buffer.
    /// </summary>
    private bool ValidateBlockBytes(CpuContext context, CachedBlock cached)
    {
        var length = cached.TotalLength;
        Span<byte> buffer = length <= 256
            ? stackalloc byte[256]
            : _blockValidationScratch;
        var window = buffer[..length];
        if (context.Memory.TryRead(cached.StartRip, window))
        {
            return window.SequenceEqual(cached.AllBytes);
        }

        // The contiguous read can fail for a block that spans two mapped regions (memory
        // implementations reject cross-region reads). Validate per instruction instead —
        // each instruction was readable at build time, so per-instruction reads stay inside
        // a single region. Same guard, coarser granularity preserved.
        for (var i = 0; i < cached.InstructionCount; i++)
        {
            var instructionBytes = cached.InstructionBytes[i];
            var encodedLength = cached.Instructions[i].Length;
            var slice = buffer[..encodedLength];
            if (!context.Memory.TryRead(cached.Instructions[i].IP, slice) ||
                !slice.SequenceEqual(instructionBytes.AsSpan(0, encodedLength)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes one basic block starting at <paramref name="startRip"/>. A block is a maximal
    /// run of decodable instructions that (a) contains no import-stub address, and (b) ends
    /// at the first control-transfer instruction (any <see cref="FlowControl"/> other than
    /// <see cref="FlowControl.Next"/> — call/jmp/jcc/ret/loop/indirect branches all
    /// terminate), the instruction cap, or the first undecodable/unreadable byte (which
    /// simply ends the block early; if that byte is ever actually reached, the legacy
    /// per-instruction path surfaces the exact same decode-failure diagnostics it always
    /// has). REP-prefixed string instructions get no special treatment: whatever flow
    /// control Iced reports for them, they execute through the same handler as the legacy
    /// path, which runs the whole repeat in one handler call.
    /// </summary>
    private bool TryBuildBlock(
        CpuContext context,
        ulong startRip,
        IReadOnlyDictionary<ulong, string> importStubs,
        [NotNullWhen(true)] out CachedBlock? block)
    {
        var instructions = new List<Instruction>(MaxBlockInstructions);
        var instructionBytes = new List<byte[]>(MaxBlockInstructions);
        var rip = startRip;
        Span<byte> readBuffer = stackalloc byte[MaxInstructionBytes];

        for (var count = 0; count < MaxBlockInstructions; count++)
        {
            // Import stubs are intercepted at dispatch time by address, never decoded or
            // executed — a fall-through into the stub region must end the block before it.
            if (importStubs.ContainsKey(rip))
            {
                break;
            }

            if (!IcedDecoder.TryReadGuestBytes(context.Memory, rip, MaxInstructionBytes, readBuffer, out var freshLength))
            {
                break;
            }

            var encoded = readBuffer[..freshLength].ToArray();
            if (!TryDecodeRaw(rip, encoded, out var instruction))
            {
                break;
            }

            instructions.Add(instruction);
            instructionBytes.Add(encoded);
            rip += (uint)instruction.Length;

            if (instruction.FlowControl != FlowControl.Next)
            {
                break;
            }
        }

        if (instructions.Count == 0)
        {
            block = null;
            return false;
        }

        var writesMemory = new bool[instructions.Count];
        for (var i = 0; i < instructions.Count; i++)
        {
            writesMemory[i] = InstructionWritesMemory(instructions[i]);
        }

        var totalLength = checked((int)(rip - startRip));
        var allBytes = new byte[totalLength];
        var offset = 0;
        for (var i = 0; i < instructionBytes.Count; i++)
        {
            // instructionBytes[i] can be longer than the instruction itself (the guest read
            // prefetches up to 15 bytes; the same oversized array is what the legacy decode
            // cache stores for its diagnostics ring). Only the instruction's own encoded
            // bytes belong to the block's contiguous validation image.
            var encodedLength = instructions[i].Length;
            instructionBytes[i].AsSpan(0, encodedLength).CopyTo(allBytes.AsSpan(offset, encodedLength));
            offset += encodedLength;
        }

        // A block spanning two regions uses the conservative policy: byte-validated every
        // execution (IsNonWritable=false) unless both ends are in non-writable regions.
        var isNonWritable =
            context.Memory.TryIsRegionNonWritable(startRip) &&
            context.Memory.TryIsRegionNonWritable(rip - 1);

        block = new CachedBlock
        {
            StartRip = startRip,
            InstructionCount = instructions.Count,
            Instructions = [.. instructions],
            InstructionBytes = [.. instructionBytes],
            WritesMemory = writesMemory,
            AllBytes = allBytes,
            TotalLength = totalLength,
            IsNonWritable = isNonWritable,
            CachedGeneration = context.Memory.MappingGeneration,
        };
        return true;
    }

    /// <summary>
    /// Reports whether executing this instruction writes guest memory. Used to close the
    /// self-modifying-code window of pre-decoded blocks: after any memory-writing instruction
    /// of a block runs, the block's byte range is revalidated before the next cached
    /// instruction executes (see TryExecuteBlock). Push/call/enter write the stack implicitly
    /// (no memory operand); everything else is classified through Iced's operand-access info,
    /// restricted to memory-mapped operand kinds — register writes can never touch code.
    /// </summary>
    private static bool InstructionWritesMemory(in Instruction instruction)
    {
        switch (instruction.Mnemonic)
        {
            case Mnemonic.Push:
            case Mnemonic.Call:
            case Mnemonic.Enter:
            case Mnemonic.Pushfq:
            case Mnemonic.Pushfd:
            case Mnemonic.Pushf:
                return true;
        }

        var info = new InstructionInfoFactory().GetInfo(instruction);
        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            switch (info.GetOpAccess(operand))
            {
                // ReadCondWrite matters: it is the memory shape of cmpxchg/cmpxchg8b/16b —
                // the classic lock-free patch instruction — which DOES store when the
                // comparison succeeds. Missing it would leave an intra-block SMC window open.
                case OpAccess.Write:
                case OpAccess.CondWrite:
                case OpAccess.ReadWrite:
                case OpAccess.ReadCondWrite:
                    switch (instruction.GetOpKind(operand))
                    {
                        case OpKind.Memory:
                        case OpKind.MemorySegSI:
                        case OpKind.MemorySegESI:
                        case OpKind.MemorySegRSI:
                        case OpKind.MemorySegDI:
                        case OpKind.MemorySegEDI:
                        case OpKind.MemorySegRDI:
                        case OpKind.MemoryESDI:
                        case OpKind.MemoryESEDI:
                        case OpKind.MemoryESRDI:
                            return true;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Executes the pre-decoded instructions of <paramref name="block"/> through the same
    /// <see cref="X64InterpreterBackend.TryExecuteInstruction"/> handlers the legacy
    /// per-instruction loop uses. Instruction counting, the diagnostic heartbeat, the
    /// recent-instruction ring, tracing, and every failure result are byte-identical to what
    /// the legacy path produces for the same instruction sequence — the cache only removes
    /// per-instruction re-decoding, re-hashing, byte re-reading, and stub-dictionary probes
    /// (none of which can occur strictly inside a block: stubs terminate blocks at build time,
    /// and control transfers terminate them at decode time).
    /// </summary>
    private BlockExecutionOutcome TryExecuteBlock(
        CpuContext context,
        CachedBlock block,
        StringBuilder? trace,
        ref int executed,
        int instructionLimit,
        int importsHit,
        int uniqueNidsHit,
        out X64InterpreterResult? result)
    {
        for (var i = 0; i < block.InstructionCount; i++)
        {
            ref var instruction = ref block.Instructions[i];
            var bytes = block.InstructionBytes[i];
            var oldRip = context.Rip;

            // Heartbeat parity with the legacy loop (which prints at the top of every
            // dispatch iteration): index 0 was already printed by the dispatcher for this
            // executed-count value, so only subsequent instructions print here.
            if (i != 0 && (executed & 0xFFFFF) == 0)
            {
                Console.Error.WriteLine(
                    $"[CPU-INTERP][HEARTBEAT] thread={Thread.CurrentThread.Name ?? "primary"} executed={executed} rip=0x{context.Rip:X16}");
            }

            if (oldRip != instruction.IP)
            {
                // Defensive: only instruction handlers set RIP, and any RIP-changing
                // instruction ends a block, so this should be unreachable. Treat it as a
                // decode-cache-style invalidation and single-step the offending address
                // through the legacy path rather than guessing.
                InvalidateBlock(block.StartRip);
                result = null;
                return BlockExecutionOutcome.RetryFromDispatcher;
            }

            PushRecent(instruction, bytes);
            if (trace is not null)
            {
                trace.AppendLine(FormatInstruction(instruction, bytes));
            }

            if (!TryExecuteInstruction(context, instruction, out var changedRip, out var failure))
            {
                // Parity note: the legacy loop's failure returns happen before the loop's
                // executed++ (a faulting instruction is not counted as executed) — same here.
                result = failure.Kind switch
                {
                    InterpreterFailureKind.MemoryRead => MemoryFault(
                        context,
                        oldRip,
                        bytes.Length > 0 ? bytes[0] : null,
                        failure.Address,
                        failure.Size,
                        isWrite: false,
                        executed,
                        importsHit,
                        uniqueNidsHit,
                        trace),
                    InterpreterFailureKind.MemoryWrite => MemoryFault(
                        context,
                        oldRip,
                        bytes.Length > 0 ? bytes[0] : null,
                        failure.Address,
                        failure.Size,
                        isWrite: true,
                        executed,
                        importsHit,
                        uniqueNidsHit,
                        trace),
                    InterpreterFailureKind.Trap => Trap(
                        oldRip,
                        bytes.Length > 0 ? bytes[0] : (byte)0xCC,
                        executed,
                        importsHit,
                        uniqueNidsHit,
                        trace),
                    _ => NotImplemented(
                        oldRip,
                        instruction.Mnemonic.ToString(),
                        failure.Detail,
                        executed,
                        importsHit,
                        uniqueNidsHit,
                        trace),
                };
                return BlockExecutionOutcome.ReturnResult;
            }

            if (!changedRip)
            {
                context.Rip = oldRip + (uint)instruction.Length;
            }
            else if (i < block.InstructionCount - 1)
            {
                // A non-terminal instruction changed RIP. Cannot happen by construction
                // (control transfers terminate blocks) — if some Iced FlowControl ever
                // disagrees with a handler, fail safe: invalidate and single-step.
                InvalidateBlock(block.StartRip);
                result = null;
                return BlockExecutionOutcome.RetryFromDispatcher;
            }

            executed++;

            if (instructionLimit != 0 && executed >= instructionLimit)
            {
                // Budget exhausted exactly where the legacy loop would exhaust it: after the
                // instructionLimit'th instruction completed (including its RIP advance).
                result = BudgetExceeded(context, instructionLimit, executed, importsHit, uniqueNidsHit, trace);
                return BlockExecutionOutcome.ReturnResult;
            }

            if (block.WritesMemory[i] &&
                !(block.IsNonWritable && context.Memory.MappingGeneration == block.CachedGeneration) &&
                !ValidateBlockBytes(context, block))
            {
                // Self-modifying code: this store just rewrote (part of) its own block. The
                // legacy path decodes every instruction from fresh guest bytes, so it would
                // execute the NEW code next — invalidate and single-step the next address
                // through the legacy path (skipBlockOnce in Execute) for identical semantics.
                // Trusted blocks (every byte in a non-writable region, generation unchanged)
                // skip the revalidation: no guest store can reach their pages.
                InvalidateBlock(block.StartRip);
                result = null;
                return BlockExecutionOutcome.RetryFromDispatcher;
            }
        }

        result = null;
        return BlockExecutionOutcome.Completed;
    }

    private void InvalidateBlock(ulong startRip)
    {
        _blockCache.Remove(startRip);
    }
}
