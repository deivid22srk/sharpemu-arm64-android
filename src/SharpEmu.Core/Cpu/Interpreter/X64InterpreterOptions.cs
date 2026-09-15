// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Interpreter;

public readonly struct X64InterpreterOptions
{
    public bool Trace { get; init; }

    public int MaxInstructions { get; init; }

    /// <summary>
    /// Disables the basic-block decode cache (see X64BlockCache), falling back to the legacy
    /// per-instruction decode path for every instruction. Inverted naming on purpose:
    /// <c>default(X64InterpreterOptions)</c> must leave the cache ENABLED (the cache is purely
    /// a decode-time optimization with identical guest-visible semantics, so there is no
    /// scenario where an unset option should silently turn it off), and a plain
    /// <c>EnableBlockCache</c> bool would default to false exactly there. Guest-visible
    /// semantics are identical either way; this switch exists for A/B benchmarking against
    /// the legacy path and for isolating a suspected cache-level issue.
    /// </summary>
    public bool DisableBlockCache { get; init; }
}
