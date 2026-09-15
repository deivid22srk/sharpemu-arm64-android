// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Jit;

public sealed unsafe class JitBlockCache : IDisposable
{
    public sealed class CachedBlock
    {
        public ulong StartRip { get; }
        public ulong EndRip { get; }
        public int InstructionCount { get; }
        public IntPtr CodePointer { get; }
        public int CodeSize { get; }
        public JitCompiledBlockFunc CompiledFunc { get; }
        public long MappingGeneration { get; }
        public long ExecutionCount;

        public CachedBlock(
            ulong startRip,
            ulong endRip,
            int instructionCount,
            IntPtr codePointer,
            int codeSize,
            JitCompiledBlockFunc compiledFunc,
            long mappingGeneration)
        {
            StartRip = startRip;
            EndRip = endRip;
            InstructionCount = instructionCount;
            CodePointer = codePointer;
            CodeSize = codeSize;
            CompiledFunc = compiledFunc;
            MappingGeneration = mappingGeneration;
            ExecutionCount = 0;
        }
    }

    private readonly ConcurrentDictionary<ulong, CachedBlock> _cache = new();
    private readonly List<IntPtr> _allocatedBuffers = new();
    private readonly object _allocLock = new();
    private long _cacheHits;
    private long _cacheMisses;

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    public int CachedBlockCount => _cache.Count;

    public CachedBlock GetOrCompile(CpuContext context, ulong rip)
    {
        if (_cache.TryGetValue(rip, out var block))
        {
            if (context.Memory.MappingGeneration == block.MappingGeneration)
            {
                Interlocked.Increment(ref _cacheHits);
                Interlocked.Increment(ref block.ExecutionCount);
                return block;
            }
            else
            {
                // Memory mapping invalidated
                _cache.TryRemove(rip, out _);
            }
        }

        Interlocked.Increment(ref _cacheMisses);

        var compiled = X64JitBlockCompiler.CompileBlock(context, rip);
        var nativeCode = compiled.NativeCode;

        IntPtr codePtr;
        lock (_allocLock)
        {
            codePtr = AllocateExecutableMemory(nativeCode.Length);
            _allocatedBuffers.Add(codePtr);
        }

        Marshal.Copy(nativeCode, 0, codePtr, nativeCode.Length);
        MakeMemoryExecutable(codePtr, nativeCode.Length);

        var compiledFunc = Marshal.GetDelegateForFunctionPointer<JitCompiledBlockFunc>(codePtr);
        var newBlock = new CachedBlock(
            compiled.StartRip,
            compiled.EndRip,
            compiled.InstructionCount,
            codePtr,
            nativeCode.Length,
            compiledFunc,
            context.Memory.MappingGeneration);

        _cache[rip] = newBlock;
        Interlocked.Increment(ref newBlock.ExecutionCount);
        return newBlock;
    }

    public void InvalidateAll()
    {
        _cache.Clear();
    }

    private static IntPtr AllocateExecutableMemory(int size)
    {
        if (OperatingSystem.IsWindows())
        {
            return VirtualAlloc(IntPtr.Zero, (UIntPtr)size, 0x1000 | 0x2000, 0x40); // MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE
        }

        // POSIX / Linux / Android / macOS
        var ptr = mmap(IntPtr.Zero, (UIntPtr)size, 0x1 | 0x2 | 0x4, 0x20 | 0x02, -1, 0); // PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS
        if (ptr == new IntPtr(-1))
        {
            throw new OutOfMemoryException("Failed to allocate RWX memory via mmap.");
        }
        return ptr;
    }

    private static void MakeMemoryExecutable(IntPtr ptr, int size)
    {
        // Memory allocated with PAGE_EXECUTE_READWRITE / PROT_EXEC is already executable.
        // Clear CPU instruction cache for the allocated range if on ARM/ARM64.
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm)
        {
            ClearInstructionCache(ptr, size);
        }
    }

    private static void ClearInstructionCache(IntPtr ptr, int size)
    {
        // On POSIX/Linux/Android, __builtin___clear_cache or sys_cacheflush can be called.
        // For managed runtime execution, gcc/clang builtin thunk or __clear_cache via libgcc/libc is standard.
        try
        {
            __clear_cache(ptr, ptr + size);
        }
        catch
        {
            // Fallback if __clear_cache export is absent
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern void __clear_cache(IntPtr beg, IntPtr end);

    public void Dispose()
    {
        _cache.Clear();
        lock (_allocLock)
        {
            _allocatedBuffers.Clear();
        }
    }
}
