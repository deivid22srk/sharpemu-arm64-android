// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;

namespace SharpEmu.Android;

/// <summary>
/// Drives one game's emulation via the exact same <see cref="SharpEmuRuntime"/> entry point the
/// desktop CLI uses (<c>SharpEmu.CLI.Program.RunEmulator</c>) — Android has no separate "emulator
/// core" of its own to reimplement; this is the same runtime, same interpreter, just started from
/// a different host shell. Runs on <see cref="GameActivity.Main"/>'s dedicated SDLThread.
/// </summary>
internal static class GameSession
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.Android");
    private static ISharpEmuRuntime? _runtime;

    public static void RunOnCurrentThread(string gamePath, string titleId, string appRoot)
    {
        SharpEmuLog.MinimumLevel = LogLevel.Debug;
        Log.Info(BuildInfo.Banner);

        // Video presenter setup (Vulkan swapchain) happens inside SharpEmuRuntime.Run once the
        // guest is far enough along to create a display — this call only configures which backend/
        // window mode it will use, mirroring SharpEmu.CLI.Program.RunEmulator.
        //
        // HostVideoOptions.Default's WindowMode=Windowed + fixed 1920x1080 size is a desktop-shaped
        // default: SdlHostWindow.ApplyConfiguredMode never calls SDL_SetWindowFullscreen for
        // Windowed, so on Android — which has no desktop window chrome/resizing at all, only the
        // Activity's SurfaceView — the SDL surface stayed pinned at its created 1920x1080 size in
        // the corner of the real (2340x1080) display instead of filling it, confirmed on-device
        // (game rendered small, top-left, misaligned from the Activity's own SensorLandscape lock).
        // Borderless drives SDL_SetWindowFullscreen(true), which stretches the surface to the full
        // SurfaceView bounds — the width/height below are still passed as SDL's initial/logical
        // window size before that fullscreen call takes over, so keep them as a landscape aspect
        // rather than desktop's arbitrary 1920x1080 (harmless either way once fullscreen applies,
        // but avoids a landscape->portrait->landscape flash while the window briefly exists at its
        // created size).
        var videoOptions = OperatingSystem.IsAndroid()
            ? HostVideoOptions.Default with { WindowMode = HostWindowMode.Borderless }
            : HostVideoOptions.Default;
        if (!HostVideoHost.TryConfigureVideo(videoOptions))
        {
            Log.Error("[LOADER][ERROR] Video options could not be applied.");
            return;
        }

        var options = new SharpEmuRuntimeOptions
        {
            // On Android/ARM64, use the native ARM64 JIT recompiler engine for real dynamic binary translation.
            CpuEngine = CpuExecutionEngine.JitRecompiler,
        };

        using var runtime = SharpEmuRuntime.CreateDefault(options);
        _runtime = runtime;
        try
        {
            Log.Info($"[LOADER] Starting: {gamePath} (titleId={titleId}, appRoot={appRoot})");
            var result = runtime.Run(gamePath);
            Log.Info($"[LOADER] Result: {result}");

            if (result != OrbisGen2Result.ORBIS_GEN2_OK && !string.IsNullOrWhiteSpace(runtime.LastExecutionDiagnostics))
            {
                // Mirrors SharpEmu.CLI.Program's error-path logging (runtime.LastExecutionDiagnostics)
                // so a guest CPU fault's RIP/opcode/faulting address reach logcat here too, instead of
                // only the bare OrbisGen2Result enum value.
                Log.Warn(runtime.LastExecutionDiagnostics);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SharpEmu failed to run.", ex);
        }
        finally
        {
            _runtime = null;
        }
    }

    public static void Stop()
    {
        // TODO: ISharpEmuRuntime has no cooperative-stop entry point yet (the desktop CLI's own
        // Ctrl+C handler just calls VideoOutExports.NotifyHostInterrupt() — wire the same call here
        // once GameActivity needs to interrupt a run in progress, e.g. the user backing out mid-game
        // rather than letting the process/":game" Activity simply be torn down).
    }

    // --- Virtual gamepad -------------------------------------------------------------------
    // TODO: route through the same guest-visible pad state SdlHostWindow/HostWindowInput already
    // maintain for physical controllers, so a touch-overlay press is indistinguishable from a real
    // SDL gamepad event to the emulator core (matching desktop's existing single code path).
    public static void SetPadButton(int button, bool pressed) { }
    public static void SetPadAxis(int axis, int value) { }
    public static void RequestRenderDiagCapture() { }
}
