// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

/// <summary>
/// Tests for the host-injected virtual gamepad (Android touch overlay path — GameSession →
/// VirtualPadInput → HostWindowInput's gamepad source, see VirtualPadInput's doc comment).
/// Button/axis ids mirror the SDL constants GamePadBridge.kt forwards.
/// </summary>
public sealed class VirtualPadInputTests
{
    public VirtualPadInputTests()
    {
        VirtualPadInput.Clear();
    }

    [Fact]
    public void Clear_InitiallyInactive()
    {
        VirtualPadInput.Clear();
        Assert.False(VirtualPadInput.HasInput);
    }

    [Fact]
    public void SetButton_MapsSdlButtonIdsToHostButtons()
    {
        VirtualPadInput.SetButton(0, pressed: true); // SDL_GAMEPAD_BUTTON_SOUTH -> Cross
        var state = VirtualPadInput.BuildStandaloneState();
        Assert.True(state.Connected);
        Assert.Equal(HostGamepadButtons.Cross, state.Buttons);

        VirtualPadInput.SetButton(0, pressed: false);
        VirtualPadInput.SetButton(3, pressed: true); // NORTH -> Triangle
        VirtualPadInput.SetButton(11, pressed: true); // DPAD_UP -> Up
        state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(HostGamepadButtons.Triangle | HostGamepadButtons.Up, state.Buttons);

        VirtualPadInput.SetButton(3, pressed: false);
        Assert.Equal(HostGamepadButtons.Up, VirtualPadInput.BuildStandaloneState().Buttons);
    }

    [Fact]
    public void SetButton_IgnoresUnknownIds()
    {
        VirtualPadInput.SetButton(42, pressed: true);
        VirtualPadInput.SetButton(-1, pressed: true);
        Assert.Equal(HostGamepadButtons.None, VirtualPadInput.BuildStandaloneState().Buttons);
    }

    [Fact]
    public void SetAxis_MapsStickRangeToBytes()
    {
        VirtualPadInput.SetAxis(0, 0);        // LEFT_X center (SDL short range)
        VirtualPadInput.SetAxis(1, -32768);   // LEFT_Y full deflection (SDL negative)
        var state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(128, state.LeftX);       // SDL center maps to the PS4's 0x80 convention
        Assert.Equal(0, state.LeftY);

        VirtualPadInput.SetAxis(0, 32767);    // full right
        state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(255, state.LeftX);
    }

    [Fact]
    public void SetAxis_TriggersRaiseL2R2BitsPastDigitalThreshold()
    {
        VirtualPadInput.SetAxis(4, 32767); // LEFT_TRIGGER fully pressed
        var state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(255, state.LeftTrigger);
        Assert.True((state.Buttons & HostGamepadButtons.L2) != 0);

        VirtualPadInput.SetAxis(4, -32768); // released
        state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(0, state.LeftTrigger);
        Assert.True((state.Buttons & HostGamepadButtons.L2) == 0);
    }

    [Fact]
    public void MergeInto_OrsButtonsAndKeepsPhysicalAxesWhenOverlayAtRest()
    {
        var physical = new HostGamepadState(
            Connected: true,
            Buttons: HostGamepadButtons.Circle,
            LeftX: 40,   // deflected left
            LeftY: 128,
            RightX: 128,
            RightY: 128,
            LeftTrigger: 200,
            RightTrigger: 0);

        VirtualPadInput.SetButton(0, pressed: true); // virtual Cross pressed
        var merged = VirtualPadInput.MergeInto(physical);

        Assert.Equal(HostGamepadButtons.Circle | HostGamepadButtons.Cross, merged.Buttons);
        // Virtual buttons do not seize the axes: physical stick/trigger stay authoritative
        // while the overlay sticks rest (finger lifted) — see MergeInto's doc comment.
        Assert.Equal(40, merged.LeftX);
        Assert.Equal(200, merged.LeftTrigger);
    }

    [Fact]
    public void MergeInto_VirtualAxesWinWhileOverlayIsDeflected()
    {
        var physical = new HostGamepadState(
            Connected: true,
            Buttons: HostGamepadButtons.None,
            LeftX: 40,
            LeftY: 128,
            RightX: 128,
            RightY: 128,
            LeftTrigger: 200,
            RightTrigger: 0);

        VirtualPadInput.SetAxis(0, 32767);     // overlay stick pushed right (SDL range)
        VirtualPadInput.SetAxis(5, 32767);     // overlay R2 fully pressed

        var merged = VirtualPadInput.MergeInto(physical);
        Assert.Equal(255, merged.LeftX);       // virtual axis engaged -> wins
        Assert.Equal(255, merged.RightTrigger); // trigger max() picks the pressed one
        Assert.Equal(200, merged.LeftTrigger);
    }

    [Fact]
    public void MergeInto_NoVirtualInputReturnsPhysicalUnchanged()
    {
        var physical = new HostGamepadState(
            Connected: true,
            Buttons: HostGamepadButtons.Cross,
            LeftX: 10,
            LeftY: 128,
            RightX: 128,
            RightY: 128,
            LeftTrigger: 0,
            RightTrigger: 0);

        var merged = VirtualPadInput.MergeInto(physical);
        Assert.Equal(physical, merged);
    }

    [Fact]
    public void Clear_ResetsButtonsAxesAndLatch()
    {
        VirtualPadInput.SetButton(0, pressed: true);
        VirtualPadInput.SetAxis(0, 255);
        Assert.True(VirtualPadInput.HasInput);

        VirtualPadInput.Clear();

        Assert.False(VirtualPadInput.HasInput);
        var state = VirtualPadInput.BuildStandaloneState();
        Assert.Equal(HostGamepadButtons.None, state.Buttons);
        Assert.Equal(128, state.LeftX);
        Assert.Equal(128, state.LeftY);
        Assert.Equal(0, state.LeftTrigger);
    }
}
