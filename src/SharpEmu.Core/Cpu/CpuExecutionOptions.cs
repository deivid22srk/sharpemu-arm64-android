// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;

namespace SharpEmu.Core.Cpu;

public readonly struct CpuExecutionOptions
{
    public bool EnableDisasmDiagnostics { get; init; }

    public CpuExecutionEngine CpuEngine { get; init; }

    public bool InterpreterTrace { get; init; }

    /// <summary>
    /// Instruction budget for the interpreter backend. 0 (the default, and
    /// what an unset <c>--cpu-interpreter-max-instructions</c> resolves to)
    /// means unlimited — matching real hardware and reference interpreters
    /// (e.g. shadPS4), which run until the guest program stops itself
    /// rather than an artificial host-imposed cap.
    /// </summary>
    public int InterpreterMaxInstructions { get; init; }

    public int EffectiveInterpreterMaxInstructions => InterpreterMaxInstructions;

    /// <summary>
    /// Disables the x64 interpreter's basic-block decode cache (see X64BlockCache), forcing
    /// the legacy per-instruction decode path. Inverted naming on purpose: with a plain
    /// "enabled" bool, <c>default(CpuExecutionOptions)</c> would silently turn the cache off,
    /// while a "disabled" flag keeps unset/default options on the fast path. Semantics are
    /// unchanged either way (same execution handlers; the per-instruction path remains as
    /// fallback); the CLI's <c>--cpu-no-block-cache</c> flag sets this for A/B comparisons.
    /// </summary>
    public bool InterpreterBlockCacheDisabled { get; init; }

    public bool StrictDynlibResolution { get; init; }

    public int ImportTraceLimit { get; init; }

    /// <summary>
    /// An optional debugger attached to this execution session. When set, the
    /// dispatcher notifies it at each frame boundary via
    /// <see cref="ICpuDebugHook.OnFrameEnter"/> / <see cref="ICpuDebugHook.OnFrameExit"/>.
    /// Null when no debugger is attached, which is the default and imposes no
    /// runtime cost.
    /// </summary>
    public ICpuDebugHook? DebugHook { get; init; }
}
