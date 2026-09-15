// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// State for a host-injected <em>virtual</em> gamepad — the Android touch overlay (and any
/// other host UI that injects pad input programmatically) writes here instead of needing an
/// SDL gamepad device. The Kotlin overlay (PadInput.kt / GamePadBridge.kt) forwards SDL
/// <c>SDL_GamepadButton</c>/<c>SDL_GamepadAxis</c> ids and SDL's short-range axis values
/// (-32768..32767, full state resent after every touch event) across the EmulatorBridge;
/// this class stores them thread-safely and merges them into the shared pad state that
/// <see cref="PadExports.ReadHostInputState"/> polls, so a touch-overlay press is
/// indistinguishable from a real SDL gamepad event to the emulator core — exactly what the
/// desktop single code path already guarantees for physical controllers.
/// </summary>
public static class VirtualPadInput
{
    private const byte StickCenter = 128; // HostWindowInput.ToStickByte(0) — SDL's resting stick position as a byte (the PS4's 0x80 convention)

    /// <summary>SDL-short deadzone (~1/8 of full deflection) below which an overlay stick counts as at rest.</summary>
    private const short StickDeadzone = 4096;

    private static readonly object Gate = new();
    private static HostGamepadButtons _buttons;
    private static short _leftXRaw;
    private static short _leftYRaw;
    private static short _rightXRaw;
    private static short _rightYRaw;
    private static byte _leftX = StickCenter;
    private static byte _leftY = StickCenter;
    private static byte _rightX = StickCenter;
    private static byte _rightY = StickCenter;
    private static byte _leftTrigger;
    private static byte _rightTrigger;
    private static bool _hasInput;

    /// <summary>
    /// True once this session has received any virtual pad input. While false, the shared
    /// input path behaves exactly as before (no virtual gamepad is advertised). Latched for
    /// the whole session so the guest never sees the pad connect/disconnect churn that
    /// per-touch reporting would cause; reset by <see cref="Clear"/> when emulation ends.
    /// </summary>
    public static bool HasInput
    {
        get
        {
            lock (Gate)
            {
                return _hasInput;
            }
        }
    }

    /// <summary>
    /// Applies one SDL <c>SDL_GamepadButton</c> id (the constants GamePadBridge.kt forwards —
    /// they mirror SDL's enum ordering) as a pressed/released state change. The L2/R2
    /// triggers arrive as axes, not buttons, matching the overlay's digital-to-analog mapping.
    /// Unknown ids are ignored so newer overlay builds stay compatible with older cores.
    /// </summary>
    public static void SetButton(int sdlGamepadButton, bool pressed)
    {
        var button = sdlGamepadButton switch
        {
            0 => HostGamepadButtons.Cross,      // SDL_GAMEPAD_BUTTON_SOUTH
            1 => HostGamepadButtons.Circle,     // SDL_GAMEPAD_BUTTON_EAST
            2 => HostGamepadButtons.Square,     // SDL_GAMEPAD_BUTTON_WEST
            3 => HostGamepadButtons.Triangle,   // SDL_GAMEPAD_BUTTON_NORTH
            4 => HostGamepadButtons.Create,     // SDL_GAMEPAD_BUTTON_BACK (Share/Select)
            5 => HostGamepadButtons.Ps,         // SDL_GAMEPAD_BUTTON_GUIDE
            6 => HostGamepadButtons.Options,    // SDL_GAMEPAD_BUTTON_START
            7 => HostGamepadButtons.L3,         // SDL_GAMEPAD_BUTTON_LEFT_STICK
            8 => HostGamepadButtons.R3,         // SDL_GAMEPAD_BUTTON_RIGHT_STICK
            9 => HostGamepadButtons.L1,         // SDL_GAMEPAD_BUTTON_LEFT_SHOULDER
            10 => HostGamepadButtons.R1,        // SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER
            11 => HostGamepadButtons.Up,        // SDL_GAMEPAD_BUTTON_DPAD_UP
            12 => HostGamepadButtons.Down,      // SDL_GAMEPAD_BUTTON_DPAD_DOWN
            13 => HostGamepadButtons.Left,      // SDL_GAMEPAD_BUTTON_DPAD_LEFT
            14 => HostGamepadButtons.Right,     // SDL_GAMEPAD_BUTTON_DPAD_RIGHT
            _ => HostGamepadButtons.None,
        };

        if (button == HostGamepadButtons.None)
        {
            return;
        }

        lock (Gate)
        {
            if (pressed)
            {
                _buttons |= button;
            }
            else
            {
                _buttons &= ~button;
            }

            _hasInput = true;
        }
    }

    /// <summary>
    /// Applies one SDL <c>SDL_GamepadAxis</c> value (SDL short range, as forwarded by
    /// GamePadBridge.kt). Stick axes convert through the same <see cref="HostWindowInput.ToStickByte"/>
    /// mapping the physical-controller path uses; trigger axes additionally raise the L2/R2
    /// button bits past the same &gt;64 threshold <see cref="SdlGamepadStateReader.Read"/> uses
    /// for physical triggers, so guest code that only reads digital L2/R2 keeps working.
    /// </summary>
    public static void SetAxis(int sdlGamepadAxis, int value)
    {
        var clamped = Math.Clamp(value, short.MinValue, short.MaxValue);
        lock (Gate)
        {
            switch (sdlGamepadAxis)
            {
                case 0: // SDL_GAMEPAD_AXIS_LEFTX
                    _leftXRaw = (short)clamped;
                    _leftX = HostWindowInput.ToStickByte((short)clamped);
                    break;
                case 1: // SDL_GAMEPAD_AXIS_LEFTY
                    _leftYRaw = (short)clamped;
                    _leftY = HostWindowInput.ToStickByte((short)clamped);
                    break;
                case 2: // SDL_GAMEPAD_AXIS_RIGHTX
                    _rightXRaw = (short)clamped;
                    _rightX = HostWindowInput.ToStickByte((short)clamped);
                    break;
                case 3: // SDL_GAMEPAD_AXIS_RIGHTY
                    _rightYRaw = (short)clamped;
                    _rightY = HostWindowInput.ToStickByte((short)clamped);
                    break;
                case 4: // SDL_GAMEPAD_AXIS_LEFT_TRIGGER
                    _leftTrigger = HostWindowInput.ToTriggerByte((short)clamped);
                    if (_leftTrigger > 64)
                    {
                        _buttons |= HostGamepadButtons.L2;
                    }
                    else
                    {
                        _buttons &= ~HostGamepadButtons.L2;
                    }

                    break;
                case 5: // SDL_GAMEPAD_AXIS_RIGHT_TRIGGER
                    _rightTrigger = HostWindowInput.ToTriggerByte((short)clamped);
                    if (_rightTrigger > 64)
                    {
                        _buttons |= HostGamepadButtons.R2;
                    }
                    else
                    {
                        _buttons &= ~HostGamepadButtons.R2;
                    }

                    break;
                default:
                    return; // unknown axis: leave state and the _hasInput latch untouched
            }

            _hasInput = true;
        }
    }

    /// <summary>
    /// Merges the virtual pad into an existing (physical) gamepad state read from SDL.
    /// Buttons OR together so a touch press and a physical press are equivalent; stick/trigger
    /// axes follow the overlay's values only while it is actively deflected away from rest
    /// (the overlay resends its full state after every touch event, so resting values mean
    /// "finger lifted" and the physical controller's axes should stay authoritative).
    /// </summary>
    public static HostGamepadState MergeInto(HostGamepadState physical)
    {
        lock (Gate)
        {
            if (!_hasInput)
            {
                return physical;
            }

            // Only DEFLECTED virtual sticks (outside the SDL-short deadzone) override the
            // physical axes: the overlay resends its full state (sticks at rest included)
            // after every touch event, so a resting overlay stick means "finger lifted" and
            // must not fight a physical controller. Virtual buttons always OR in (a touch
            // press is equivalent to a physical press), and triggers combine via max() so
            // either input source can raise them.
            var sticksEngaged =
                Math.Abs(_leftXRaw) > StickDeadzone || Math.Abs(_leftYRaw) > StickDeadzone ||
                Math.Abs(_rightXRaw) > StickDeadzone || Math.Abs(_rightYRaw) > StickDeadzone;

            return physical with
            {
                Buttons = physical.Buttons | _buttons,
                LeftX = sticksEngaged ? _leftX : physical.LeftX,
                LeftY = sticksEngaged ? _leftY : physical.LeftY,
                RightX = sticksEngaged ? _rightX : physical.RightX,
                RightY = sticksEngaged ? _rightY : physical.RightY,
                LeftTrigger = Math.Max(physical.LeftTrigger, _leftTrigger),
                RightTrigger = Math.Max(physical.RightTrigger, _rightTrigger),
            };
        }
    }

    /// <summary>
    /// Builds the standalone virtual-gamepad state for hosts with no physical SDL gamepad
    /// connected (the common Android touch-overlay case). Reported as connected and generic —
    /// matching how <see cref="PadExports.ReadHostInputState"/> already presents a merged pad.
    /// </summary>
    public static HostGamepadState BuildStandaloneState()
    {
        lock (Gate)
        {
            return new HostGamepadState(
                Connected: true,
                Buttons: _buttons,
                LeftX: _leftX,
                LeftY: _leftY,
                RightX: _rightX,
                RightY: _rightY,
                LeftTrigger: _leftTrigger,
                RightTrigger: _rightTrigger,
                Type: HostGamepadType.Generic,
                Connection: HostGamepadConnection.Wireless);
        }
    }

    /// <summary>
    /// Resets all virtual pad state (buttons, axes, and the has-input latch). Called when an
    /// emulation session ends so the next session starts from a clean pad, mirroring
    /// <see cref="HostWindowInput.Disconnect"/>'s cleanup of physical input state.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _buttons = HostGamepadButtons.None;
            _leftXRaw = 0;
            _leftYRaw = 0;
            _rightXRaw = 0;
            _rightYRaw = 0;
            _leftX = StickCenter;
            _leftY = StickCenter;
            _rightX = StickCenter;
            _rightY = StickCenter;
            _leftTrigger = 0;
            _rightTrigger = 0;
            _hasInput = false;
        }
    }
}
