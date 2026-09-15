// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using System.Threading;
using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Core.Cpu.Interpreter;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class CpuDispatcher : ICpuDispatcher, IDisposable
{
    private enum EntryFrameKind
    {
        ProcessEntry,
        ModuleInitializer,
    }

    // The top of the x86-64 user address space (0x7FFD..0x7FFF) is only
    // freely mappable on Windows; on macOS/Linux it hosts the dyld shared
    // cache / vdso and (under Rosetta 2) the translator runtime, so POSIX
    // hosts use the equivalent layout one slot lower at 0x6FFx.
    //
    // Android's arm64 kernels commonly expose only a 39-bit process VA space (max ~512 GiB, confirmed
    // on a Samsung Galaxy S23 via /proc/self/maps) — the 0x6FFx/0x7FFx desktop values above (bit 46
    // set, ~70 TB+) are unreachable there, so every MAP_FIXED_NOREPLACE probe in the TryMap*Region
    // helpers below failed identically, the same way SelfLoader.ImportStubBaseAddress's did (see its
    // comment). None of these six addresses are part of the PS4/PS5 ABI — they're SharpEmu's own
    // internal stack/TLS/stub bookkeeping — so any reachable, mutually non-overlapping addresses work.
    //
    // First attempt used 0x20_0000_0000 (128 GiB) as the cluster base — this turned out to exactly
    // collide with KernelMemoryCompatExports.DefaultMapSearchBase, the address every *guest* sceKernelMmap
    // call with no hint searches upward from (unbounded growth, shared across all POSIX hosts, not
    // Android-specific). On a real device this manifested as the guest's own heap-growth mmaps getting
    // shoved past this fixed cluster, eventually landing hundreds of GiB away in the same region as
    // Android's own untracked native allocations — the guest register then held a real-looking but
    // wrong pointer (0xB4... prefixed) that later faulted. Fixed by moving this whole cluster below
    // DefaultMapSearchBase entirely: spaced 1 GiB apart (each TryMap*Region helper only searches up to
    // 512 MiB downward from its base) in the 48-59 GiB range, comfortably above the guest image (32 GiB
    // for PS5) and clear of SelfLoader's import stub region (64 GiB) and DefaultMapSearchBase (128 GiB).
    private static readonly ulong StackBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000C_0000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFF_F000_0000UL : 0x6FFF_F000_0000UL);
    internal const ulong StackSize = 0x0020_0000UL;
    private static readonly ulong TlsBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000D_0000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFE_0000_0000UL : 0x6FFE_0000_0000UL);
    private const ulong TlsSize = 0x0001_0000UL;
    // The static TLS blocks live at negative offsets from the TCB (FreeBSD
    // amd64 variant II). Keep every host in sync with GuestTlsTemplate's
    // startup reservation; PS5 modules routinely reach beyond one host page.
    private const ulong TlsPrefixSize = GuestTlsTemplate.StartupStaticTlsReservation;
    private static readonly ulong BootstrapStubBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000E_0000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFD_F000_0000UL : 0x6FFD_F000_0000UL);
    private static readonly ulong BootstrapPayloadBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000E_4000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFD_E000_0000UL : 0x6FFD_E000_0000UL);
    private static readonly ulong DynlibFallbackStubBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000E_8000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFD_D000_0000UL : 0x6FFD_D000_0000UL);
    private static readonly ulong ReturnToHostStubBaseAddress = OperatingSystem.IsAndroid()
        ? 0x0000_000E_C000_0000UL
        : (OperatingSystem.IsWindows() ? 0x7FFD_C000_0000UL : 0x6FFD_C000_0000UL);
    private const ulong BootstrapRegionSize = 0x0000_1000UL;
    private const ulong ReturnToHostStubStride = 0x0100_0000UL;
    private const ulong BootstrapPayloadResultOffset = 0x28UL;
    private const ulong BootstrapStatusOffset = 0x100UL;
    private static readonly byte[] BootstrapStartSignature =
    [
        0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56,
        0x41, 0x55, 0x41, 0x54, 0x53, 0x50, 0x48, 0x89,
    ];
    private readonly IVirtualMemory _virtualMemory;
    private readonly IModuleManager _moduleManager;
    private INativeCpuBackend? _nativeCpuBackend;
    // PhysicalVirtualMemory.Map() does not throw when re-mapping an address
    // that is already in use — it silently re-applies protection and
    // zero-fills the region, which would corrupt a live thread's stack if a
    // second thread's stack/TLS region were ever mapped onto the same slot.
    // These counters guarantee each call claims a slot no earlier call (from
    // any thread) has already used, regardless of the exception-based retry
    // below (kept for genuine host-level allocation failures).
    private int _nextStackSlot = -1;
    private int _nextTlsSlot = -1;

    public CpuDispatcher(
        IVirtualMemory virtualMemory,
        IModuleManager moduleManager,
        INativeCpuBackend? nativeCpuBackend = null)
    {
        _virtualMemory = virtualMemory ?? throw new ArgumentNullException(nameof(virtualMemory));
        _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
        _nativeCpuBackend = nativeCpuBackend;
    }

    public ulong? LastEntryPoint { get; private set; }

    public CpuTrapInfo? LastTrapInfo { get; private set; }

    public CpuMemoryFaultInfo? LastMemoryFaultInfo { get; private set; }

    public CpuControlTransferInfo? LastControlTransferInfo { get; private set; }

    public CpuNotImplementedInfo? LastNotImplementedInfo { get; private set; }

    public string? LastImportResolutionTrace { get; private set; }

    public string? LastBasicBlockTrace { get; private set; }

    public string? LastMilestoneLog { get; private set; }

    public string? LastRecentInstructionWindow { get; private set; }

    public string? LastRecentControlTransferTrace { get; private set; }

    public CpuSessionSummary LastSessionSummary { get; private set; }

    public OrbisGen2Result DispatchEntry(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string processImageName = "eboot.bin",
        CpuExecutionOptions executionOptions = default)
    {
        Console.Error.WriteLine("[DISPATCHER] === DispatchEntry START ===");
        Console.Error.WriteLine($"[DISPATCHER] entryPoint=0x{entryPoint:X16}, generation={generation}");

        try
        {
            return DispatchEntryCore(entryPoint, generation, importStubs, runtimeSymbols, processImageName, executionOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DISPATCHER] FATAL EXCEPTION in DispatchEntry: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[DISPATCHER] Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    public OrbisGen2Result DispatchModuleInitializer(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string moduleName = "module",
        CpuExecutionOptions executionOptions = default)
    {
        Console.Error.WriteLine("[DISPATCHER] === DispatchModuleInitializer START ===");
        Console.Error.WriteLine($"[DISPATCHER] moduleInit=0x{entryPoint:X16}, generation={generation}, module={moduleName}");

        try
        {
            return DispatchEntryCore(
                entryPoint,
                generation,
                importStubs,
                runtimeSymbols,
                moduleName,
                executionOptions,
                EntryFrameKind.ModuleInitializer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DISPATCHER] FATAL EXCEPTION in DispatchModuleInitializer: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[DISPATCHER] Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private OrbisGen2Result DispatchEntryCore(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string processImageName = "eboot.bin",
        CpuExecutionOptions executionOptions = default,
        EntryFrameKind frameKind = EntryFrameKind.ProcessEntry)
    {
        Console.Error.WriteLine("[DISPATCHER] DispatchEntryCore STARTING...");

        LastEntryPoint = entryPoint;
        LastTrapInfo = null;
        LastMemoryFaultInfo = null;
        LastControlTransferInfo = null;
        LastNotImplementedInfo = null;
        LastImportResolutionTrace = null;
        LastBasicBlockTrace = null;
        LastMilestoneLog = null;
        LastRecentInstructionWindow = null;
        LastRecentControlTransferTrace = null;
        LastSessionSummary = default;
        OrbisGen2Result FailEarly(OrbisGen2Result result, CpuExitReason reason = CpuExitReason.UnhandledException)
        {
            LastSessionSummary = new CpuSessionSummary(
                result,
                reason,
                exitCode: null,
                lastGuestRip: entryPoint,
                lastStubRip: 0,
                totalInstructions: 0,
                importsHit: 0,
                uniqueNidsHit: 0);
            return result;
        }

        var stackBase = TryMapStackRegion();
        if (stackBase == 0)
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var tlsBase = TryMapTlsRegion();
        if (tlsBase == 0)
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var trackedMemory = new TrackedCpuMemory(_virtualMemory);
        var context = new CpuContext(trackedMemory, generation)
        {
            Rip = entryPoint,
            Rflags = 0x202,
            FsBase = tlsBase,
            GsBase = tlsBase,
        };

        var returnToHostStubAddress = TryMapReturnToHostStubRegion();
        if (returnToHostStubAddress == 0)
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        context[CpuRegister.Rsp] = stackBase + StackSize - sizeof(ulong);
        if (!context.TryWriteUInt64(context[CpuRegister.Rsp], returnToHostStubAddress))
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!InitializeGuestFrameChainSentinel(context))
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!InitializeTls(context, tlsBase))
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var effectiveImportStubs = importStubs is null
            ? new Dictionary<ulong, string>()
            : new Dictionary<ulong, string>(importStubs);
        var entryParamsConfigured = false;
        if (frameKind == EntryFrameKind.ProcessEntry)
        {
            var programExitHandlerStubAddress = TryMapDynlibFallbackStubRegion();
            if (programExitHandlerStubAddress == 0)
            {
                return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!InitializeProcessEntryFrame(context, processImageName, programExitHandlerStubAddress))
            {
                return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            entryParamsConfigured = true;

            if (ShouldInjectBootstrapPayload(entryPoint))
            {
                if (!TryInstallBootstrapPayload(context, effectiveImportStubs))
                {
                    return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }
        else if (!InitializeModuleInitializerFrame(context))
        {
            return FailEarly(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var entryFrameDiagnostic = BuildEntryFrameDiagnostic(
            entryPoint,
            context,
            sentinelEnabled: true,
            sentinelValue: returnToHostStubAddress,
            entryParamsConfigured: entryParamsConfigured);

        if (executionOptions.CpuEngine == CpuExecutionEngine.Interpreter || executionOptions.CpuEngine == CpuExecutionEngine.JitRecompiler)
        {
            var isJit = executionOptions.CpuEngine == CpuExecutionEngine.JitRecompiler;
            LastMilestoneLog = string.Concat(
                entryFrameDiagnostic,
                Environment.NewLine,
                $"CpuEngine: {(isJit ? "arm64-jit-recompiler" : "x64-interpreter")} trace={executionOptions.InterpreterTrace} max_instructions={executionOptions.EffectiveInterpreterMaxInstructions}");

            var interpreterOptions = new X64InterpreterOptions
            {
                Trace = executionOptions.InterpreterTrace,
                MaxInstructions = executionOptions.EffectiveInterpreterMaxInstructions,
            };
            var interpreterScheduler = new X64InterpreterGuestThreadScheduler(
                this,
                _moduleManager,
                effectiveImportStubs,
                interpreterOptions);
            var previousGuestThreadScheduler = GuestThreadExecution.Scheduler;
            GuestThreadExecution.Scheduler = interpreterScheduler;

            X64InterpreterResult interpreterResult;
            try
            {
                if (isJit)
                {
                    using var blockCache = new SharpEmu.Core.Cpu.Jit.JitBlockCache();
                    var interpreter = new X64InterpreterBackend(_moduleManager, interpreterScheduler);

                    // JIT dispatch loop
                    var concreteImportStubs = effectiveImportStubs as Dictionary<ulong, string> ?? new Dictionary<ulong, string>(effectiveImportStubs);
                    context.Rip = entryPoint;
                    var executedInstructions = 0;
                    var importsHit = 0;
                    var uniqueImports = new HashSet<string>(StringComparer.Ordinal);

                    while (context.Rip != 0)
                    {
                        if (concreteImportStubs.TryGetValue(context.Rip, out var nid))
                        {
                            importsHit++;
                            uniqueImports.Add(nid);
                            _ = _moduleManager.Dispatch(nid, context);

                            if (GuestThreadExecution.TryConsumeCurrentEntryExit(out var exitValue, out _))
                            {
                                context[CpuRegister.Rax] = exitValue;
                                break;
                            }

                            if (GuestThreadExecution.TryConsumeCurrentThreadBlock(
                                    out _, out _, out _, out var wakeKey, out var waiter, out var blockDeadline))
                            {
                                interpreterScheduler.TryBlockCurrentThread(wakeKey, blockDeadline);
                                if (waiter is not null)
                                {
                                    context[CpuRegister.Rax] = unchecked((ulong)waiter.Resume());
                                }
                            }

                            if (!context.PopUInt64(out var returnAddress))
                            {
                                break;
                            }

                            context.Rip = returnAddress;
                            continue;
                        }

                        // JIT compilation/execution block attempt
                        try
                        {
                            var cachedBlock = blockCache.GetOrCompile(context, context.Rip);
                            executedInstructions += cachedBlock.InstructionCount;

                            // Execute translated block
                            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
                            {
                                unsafe
                                {
                                    fixed (ulong* regsPtr = context.RegistersArray)
                                    {
                                        var nextRip = cachedBlock.CompiledFunc((IntPtr)regsPtr, IntPtr.Zero);
                                        if (nextRip != context.Rip && nextRip != 0)
                                        {
                                            context.Rip = nextRip;
                                        }
                                        else
                                        {
                                            // Fallback single-instruction execution via interpreter
                                            var singleStepResult = interpreter.Execute(context, context.Rip, effectiveImportStubs, new X64InterpreterOptions { MaxInstructions = 1 });
                                            if (singleStepResult.Result != OrbisGen2Result.ORBIS_GEN2_OK)
                                            {
                                                interpreterResult = singleStepResult;
                                                goto JitFinished;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // On x64 hosts (e.g. dev/CI machine), block compilation is verified and execution steps via interpreter fallback
                                var singleStepResult = interpreter.Execute(context, context.Rip, effectiveImportStubs, new X64InterpreterOptions { MaxInstructions = Math.Max(1, cachedBlock.InstructionCount) });
                                if (singleStepResult.Result != OrbisGen2Result.ORBIS_GEN2_OK)
                                {
                                    interpreterResult = singleStepResult;
                                    goto JitFinished;
                                }
                            }
                        }
                        catch
                        {
                            // Fallback to interpreter for unsupported or invalid blocks
                            var singleStepResult = interpreter.Execute(context, context.Rip, effectiveImportStubs, new X64InterpreterOptions { MaxInstructions = 1 });
                            if (singleStepResult.Result != OrbisGen2Result.ORBIS_GEN2_OK)
                            {
                                interpreterResult = singleStepResult;
                                goto JitFinished;
                            }
                        }
                    }

                    interpreterResult = new X64InterpreterResult(
                        OrbisGen2Result.ORBIS_GEN2_OK,
                        CpuExitReason.ReturnedToHost,
                        context.Rip,
                        executedInstructions,
                        importsHit,
                        uniqueImports.Count,
                        null, null, null, null, string.Empty);

                    JitFinished: ;
                }
                else
                {
                    var interpreter = new X64InterpreterBackend(_moduleManager, interpreterScheduler);
                    interpreterResult = interpreter.Execute(context, entryPoint, effectiveImportStubs, interpreterOptions);
                }
            }
            finally
            {
                GuestThreadExecution.Scheduler = previousGuestThreadScheduler;
            }

            LastTrapInfo = interpreterResult.TrapInfo;
            LastMemoryFaultInfo = interpreterResult.MemoryFaultInfo;
            LastNotImplementedInfo = interpreterResult.NotImplementedInfo;
            LastBasicBlockTrace = interpreterResult.Trace;
            LastRecentInstructionWindow = interpreterResult.RecentInstructions;
            LastSessionSummary = new CpuSessionSummary(
                interpreterResult.Result,
                interpreterResult.Reason,
                exitCode: null,
                lastGuestRip: interpreterResult.LastGuestRip,
                lastStubRip: 0,
                totalInstructions: interpreterResult.TotalInstructions,
                importsHit: interpreterResult.ImportsHit,
                uniqueNidsHit: interpreterResult.UniqueNidsHit);
            return interpreterResult.Result;
        }

        if (executionOptions.CpuEngine != CpuExecutionEngine.NativeOnly)
        {
            LastMilestoneLog = string.Concat(
                entryFrameDiagnostic,
                Environment.NewLine,
                $"CpuEngine: {executionOptions.CpuEngine} (unsupported)");
            LastNotImplementedInfo = new CpuNotImplementedInfo(
                CpuNotImplementedSource.NativeBackend,
                entryPoint,
                nid: null,
                exportName: "cpu_engine_unsupported",
                libraryName: executionOptions.CpuEngine.ToString(),
                detail: "Unsupported CPU engine mode.");
            return FailEarly(
                OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
                CpuExitReason.NativeBackendUnavailable);
        }

        LastMilestoneLog = string.Concat(
            entryFrameDiagnostic,
            Environment.NewLine,
            "CpuEngine: native-only");
        // Frame boundaries an attached debugger observes; null hook = a branch.
        var debugHook = executionOptions.DebugHook;
        var debugFrame = debugHook is null
            ? null
            : new CpuContextDebugFrame(
                frameKind == EntryFrameKind.ProcessEntry
                    ? CpuDebugFrameKind.ProcessEntry
                    : CpuDebugFrameKind.ModuleInitializer,
                entryPoint,
                processImageName,
                context,
                effectiveImportStubs);
        debugHook?.OnFrameEnter(debugFrame!);

        // Belt-and-suspenders: Program.cs's argument-parsing gate is the real
        // fix (it forces CpuExecutionEngine.Interpreter and rejects an
        // explicit --cpu-engine=native on Android before this method can ever
        // be reached), but fail loud here too rather than let a caller that
        // bypasses that gate construct a backend that assumes an x86-64 host.
        if (OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "Native guest execution is impossible on Android/ARM64 — use CpuExecutionEngine.Interpreter.");
        }

        _nativeCpuBackend ??= new DirectExecutionBackend(_moduleManager);
        // Let backend stall reports reference the same frame as entry.
        (_nativeCpuBackend as DirectExecutionBackend)?.SetActiveDebugFrame(debugFrame);
        if (_nativeCpuBackend.TryExecute(
                context,
                entryPoint,
                generation,
                effectiveImportStubs,
                runtimeSymbols ?? new Dictionary<string, ulong>(StringComparer.Ordinal),
                executionOptions,
                out var nativeResult))
        {
            debugHook?.OnFrameExit(debugFrame!, nativeResult);
            LastSessionSummary = new CpuSessionSummary(
                nativeResult,
                nativeResult == OrbisGen2Result.ORBIS_GEN2_OK
                    ? CpuExitReason.ReturnedToHost
                    : CpuExitReason.UnhandledException,
                exitCode: null,
                lastGuestRip: context.Rip,
                lastStubRip: 0,
                totalInstructions: 0,
                importsHit: 0,
                uniqueNidsHit: 0);
            return nativeResult;
        }

        debugHook?.OnFrameExit(debugFrame!, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);

        var backendName = string.IsNullOrWhiteSpace(_nativeCpuBackend.BackendName)
            ? "native-backend"
            : _nativeCpuBackend.BackendName;
        var backendError = string.IsNullOrWhiteSpace(_nativeCpuBackend.LastError)
            ? "unknown backend error"
            : _nativeCpuBackend.LastError;
        LastNotImplementedInfo = new CpuNotImplementedInfo(
            CpuNotImplementedSource.NativeBackend,
            entryPoint,
            nid: null,
            exportName: "cpu_engine_native_only",
            libraryName: backendName,
            detail: backendError);
        LastMilestoneLog = string.Concat(
            LastMilestoneLog,
            Environment.NewLine,
            $"CpuEngine native-only failed: {backendError}");
        Console.Error.WriteLine($"[DISPATCHER] Native backend FAILED: {backendError}");
        return FailEarly(
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
            CpuExitReason.NativeBackendUnavailable);
    }

    internal ulong TryMapStackRegion()
    {
        const ulong stackStride = 0x0100_0000UL;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var i = Interlocked.Increment(ref _nextStackSlot);
            if (i >= 32)
            {
                return 0;
            }

            var candidateBase = StackBaseAddress - ((ulong)i * stackStride);
            try
            {
                _virtualMemory.Map(
                    candidateBase,
                    StackSize,
                    fileOffset: 0,
                    fileData: ReadOnlySpan<byte>.Empty,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    internal ulong TryMapTlsRegion()
    {
        const ulong tlsStride = 0x0100_0000UL;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var i = Interlocked.Increment(ref _nextTlsSlot);
            if (i >= 32)
            {
                return 0;
            }

            var candidateBase = TlsBaseAddress - ((ulong)i * tlsStride);
            var mappedBase = candidateBase - TlsPrefixSize;
            try
            {
                _virtualMemory.Map(
                    mappedBase,
                    TlsSize + TlsPrefixSize,
                    fileOffset: 0,
                    fileData: ReadOnlySpan<byte>.Empty,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    internal static bool InitializeTls(CpuContext context, ulong tlsBase)
    {
        if (!context.TryWriteUInt64(tlsBase - 0xF0, 0) ||
            !context.TryWriteUInt64(tlsBase + 0x00, tlsBase) ||
            !context.TryWriteUInt64(tlsBase + 0x10, tlsBase) ||
            !context.TryWriteUInt64(tlsBase + 0x28, 0xC0DEC0DECAFEBA00UL) ||
            !context.TryWriteUInt64(tlsBase + 0x60, tlsBase))
        {
            return false;
        }

        // Seed the static TLS block below the thread pointer with the main
        // module's initialized thread-locals (variant II layout).
        SharpEmu.HLE.GuestTlsTemplate.SeedThreadBlock(context, tlsBase);
        return true;
    }

    internal static bool InitializeGuestFrameChainSentinel(CpuContext context)
    {
        var stackTop = context[CpuRegister.Rsp] + sizeof(ulong);
        var sentinelFrame = AlignDown(stackTop - 0x20, 16);
        var seedRsp = sentinelFrame - sizeof(ulong);
        if (!context.TryWriteUInt64(sentinelFrame, 0) ||
            !context.TryWriteUInt64(sentinelFrame + sizeof(ulong), 0) ||
            !context.TryWriteUInt64(seedRsp, 0))
        {
            return false;
        }

        context[CpuRegister.Rbp] = sentinelFrame;
        context[CpuRegister.Rsp] = seedRsp;
        return true;
    }

    private static bool InitializeProcessEntryFrame(
        CpuContext context,
        string processImageName,
        ulong programExitHandlerAddress)
    {
        var imageName = string.IsNullOrWhiteSpace(processImageName) ? "eboot.bin" : processImageName;
        var arguments = new List<string>(3) { imageName };
        var configuredArguments = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_ARGS");
        if (!string.IsNullOrWhiteSpace(configuredArguments))
        {
            // The PS5 entry-parameter ABI exposes three inline argv pointers.
            // Two compatibility arguments are therefore safe without changing
            // the fixed 0x20-byte structure expected by existing titles.
            var compatibilityArguments = configuredArguments.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            arguments.AddRange(compatibilityArguments.Take(2));
        }

        var cursor = context[CpuRegister.Rsp];
        var argumentAddresses = new ulong[arguments.Count];
        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            var encoded = Encoding.UTF8.GetBytes(arguments[index] + '\0');
            cursor = AlignDown(cursor - (ulong)encoded.Length, 16);
            if (!context.Memory.TryWrite(cursor, encoded))
            {
                return false;
            }

            argumentAddresses[index] = cursor;
        }

        const ulong entryParamsSize = 0x20;
        var entryParamsAddress = AlignDown(cursor - entryParamsSize, 16);
        if (!TryWriteUInt32(context, entryParamsAddress + 0x00, (uint)arguments.Count) ||
            !TryWriteUInt32(context, entryParamsAddress + 0x04, 0) ||
            !context.TryWriteUInt64(entryParamsAddress + 0x08, argumentAddresses[0]) ||
            !context.TryWriteUInt64(
                entryParamsAddress + 0x10,
                argumentAddresses.Length > 1 ? argumentAddresses[1] : 0) ||
            !context.TryWriteUInt64(
                entryParamsAddress + 0x18,
                argumentAddresses.Length > 2 ? argumentAddresses[2] : 0))
        {
            return false;
        }

        if (arguments.Count > 1)
        {
            Console.Error.WriteLine(
                $"[DISPATCHER] Guest arguments: {string.Join(' ', arguments.Skip(1))}");
        }

        var entryStackPointer = entryParamsAddress - sizeof(ulong);
        if (!context.TryWriteUInt64(entryStackPointer, 0))
        {
            return false;
        }

        context[CpuRegister.Rsp] = entryStackPointer;
        context[CpuRegister.Rdi] = entryParamsAddress;
        context[CpuRegister.Rsi] = programExitHandlerAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0;
        return true;
    }

    private static bool InitializeModuleInitializerFrame(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0;
        return true;
    }

    private static ulong AlignDown(ulong value, ulong alignment)
    {
        return value & ~(alignment - 1);
    }

    private static bool TryWriteUInt32(CpuContext context, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return context.Memory.TryWrite(address, buffer);
    }

    private static string BuildEntryFrameDiagnostic(
        ulong entryPoint,
        CpuContext context,
        bool sentinelEnabled,
        ulong sentinelValue,
        bool entryParamsConfigured)
    {
        var initialRsp = context[CpuRegister.Rsp];
        var stackTopText = context.TryReadUInt64(initialRsp, out var stackTop)
            ? $"0x{stackTop:X16}"
            : "??";
        var sentinelText = sentinelEnabled ? "yes" : "no";
        var sentinelValueText = sentinelEnabled ? $"0x{sentinelValue:X16}" : "??";
        var entryParamsText = entryParamsConfigured ? "yes" : "no";
        return
            $"EntryFrame: entry_rip=0x{entryPoint:X16} initial_rsp=0x{initialRsp:X16} [rsp]={stackTopText} sentinel_enabled={sentinelText} sentinel_value={sentinelValueText} entry_params_configured={entryParamsText}";
    }

    private bool ShouldInjectBootstrapPayload(ulong entryPoint)
    {
        Span<byte> probe = stackalloc byte[16];
        if (!_virtualMemory.TryRead(entryPoint, probe))
        {
            return false;
        }

        for (var i = 0; i < BootstrapStartSignature.Length; i++)
        {
            if (probe[i] != BootstrapStartSignature[i])
            {
                return false;
            }
        }

        return true;
    }

    private bool TryInstallBootstrapPayload(CpuContext context, IDictionary<ulong, string> importStubs)
    {
        var stubAddress = TryMapBootstrapStubRegion();
        if (stubAddress == 0)
        {
            return false;
        }

        var payloadAddress = TryMapBootstrapPayloadRegion();
        if (payloadAddress == 0)
        {
            return false;
        }

        var statusAddress = payloadAddress + BootstrapStatusOffset;
        if (!TryWriteUInt64(payloadAddress, stubAddress) ||
            !TryWriteUInt64(payloadAddress + 0x08, statusAddress) ||
            !TryWriteUInt64(payloadAddress + 0x10, statusAddress) ||
            !TryWriteUInt64(payloadAddress + 0x18, statusAddress) ||
            !TryWriteUInt64(payloadAddress + 0x20, statusAddress) ||
            !TryWriteUInt64(payloadAddress + BootstrapPayloadResultOffset, statusAddress) ||
            !TryWriteUInt32(statusAddress, 0))
        {
            return false;
        }

        importStubs[stubAddress] = RuntimeStubNids.BootstrapBridge;
        importStubs[stubAddress + 0x0A] = RuntimeStubNids.BootstrapBridge;

        context[CpuRegister.Rdi] = payloadAddress;
        return true;
    }

    private ulong TryMapBootstrapStubRegion()
    {
        var stubData = new byte[(int)BootstrapRegionSize];
        stubData[0] = 0xCC;
        stubData[1] = 0xC3;

        const ulong stride = 0x0100_0000UL;
        for (var i = 0; i < 16; i++)
        {
            var candidateBase = BootstrapStubBaseAddress - ((ulong)i * stride);
            try
            {
                _virtualMemory.Map(
                    candidateBase,
                    BootstrapRegionSize,
                    fileOffset: 0,
                    stubData,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    private ulong TryMapBootstrapPayloadRegion()
    {
        const ulong stride = 0x0100_0000UL;
        for (var i = 0; i < 16; i++)
        {
            var candidateBase = BootstrapPayloadBaseAddress - ((ulong)i * stride);
            try
            {
                _virtualMemory.Map(
                    candidateBase,
                    BootstrapRegionSize,
                    fileOffset: 0,
                    ReadOnlySpan<byte>.Empty,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    private ulong TryMapDynlibFallbackStubRegion()
    {
        var stubData = new byte[(int)BootstrapRegionSize];
        stubData[0] = 0x31;
        stubData[1] = 0xC0;
        stubData[2] = 0xC3;

        const ulong stride = 0x0100_0000UL;
        for (var i = 0; i < 16; i++)
        {
            var candidateBase = DynlibFallbackStubBaseAddress - ((ulong)i * stride);
            try
            {
                _virtualMemory.Map(
                    candidateBase,
                    BootstrapRegionSize,
                    fileOffset: 0,
                    stubData,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    private ulong TryMapReturnToHostStubRegion()
    {
        var stubData = new byte[(int)BootstrapRegionSize];
        stubData[0] = 0xF4;
        stubData[1] = 0xCC;

        for (var i = 0; i < 16; i++)
        {
            var candidateBase = ReturnToHostStubBaseAddress - ((ulong)i * ReturnToHostStubStride);
            try
            {
                _virtualMemory.Map(
                    candidateBase,
                    BootstrapRegionSize,
                    fileOffset: 0,
                    stubData,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        return 0;
    }

    private bool TryWriteUInt64(ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return _virtualMemory.TryWrite(address, buffer);
    }

    private bool TryWriteUInt32(ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return _virtualMemory.TryWrite(address, buffer);
    }

    /// <summary>
    /// True when the disposed native backend left its session state alive
    /// because guest workers were still executing guest code. The guest
    /// address space must then stay mapped as well.
    /// </summary>
    internal bool NativeSessionLeaked { get; private set; }

    public void Dispose()
    {
        if (_nativeCpuBackend is IDisposable disposableBackend)
        {
            disposableBackend.Dispose();
        }

        NativeSessionLeaked = _nativeCpuBackend is DirectExecutionBackend { GuestSessionLeaked: true };
        _nativeCpuBackend = null;
    }
}
