using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#479 PvP fear statue, D1): the forced-speed-acknowledgement reshaping gate.
/// See <see cref="FearOnsetAckGate"/>.
/// </summary>
public class FearOnsetAckGateTests
{
    private const uint Falling = (uint)MovementFlagModern.Falling;
    private const uint StrafeLeft = (uint)MovementFlagModern.StrafeLeft;
    private const uint Forward = (uint)MovementFlagModern.Forward;
    private const uint Swimming = (uint)MovementFlagModern.Swimming;
    private const uint Root = (uint)MovementFlagModern.Root;

    // The onset transient seen in every decoded statue: control lost, Falling, fall time 0, zero jump speeds.
    [Fact]
    public void ControlLost_FallingWithZeroFall_Matches()
    {
        Assert.True(FearOnsetAckGate.IsOnsetTransientFallingAck(hasControl: false, modernFlags: Falling, fallTime: 0, jumpVerticalSpeed: 0f, jumpHorizontalSpeed: 0f));
    }

    // 25695's shape: a held strafe bit rides along with Falling.
    [Fact]
    public void ControlLost_FallingPlusHeldStrafe_Matches()
    {
        Assert.True(FearOnsetAckGate.IsOnsetTransientFallingAck(false, Falling | StrafeLeft, 0, 0f, 0f));
    }

    // Any acknowledgement while the player is in control is left alone (speed buffs, mounts, dismounts).
    [Fact]
    public void InControl_SameShape_DoesNotMatch()
    {
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(true, Falling, 0, 0f, 0f));
    }

    // A real fall in progress has a running fall timer (a knockback, a jump): never touched.
    [Fact]
    public void ControlLost_FallTimeRunning_DoesNotMatch()
    {
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(false, Falling, 412, 0f, 0f));
    }

    // A real fall has jump speeds: never touched.
    [Fact]
    public void ControlLost_JumpSpeedPresent_DoesNotMatch()
    {
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(false, Falling, 0, 7.96f, 0f));
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(false, Falling, 0, 0f, 3.5f));
    }

    // No Falling flag at all: not the onset transient, whatever else is set.
    [Fact]
    public void ControlLost_NoFallingFlag_DoesNotMatch()
    {
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(false, Forward, 0, 0f, 0f));
        Assert.False(FearOnsetAckGate.IsOnsetTransientFallingAck(false, 0, 0, 0f, 0f));
    }

    // The cleared acknowledgement goes out with flags 0 (the native shape at this spot).
    [Fact]
    public void ClearMovingMask_FallingPlusStrafe_BecomesZero()
    {
        Assert.Equal(0u, FearOnsetAckGate.ClearMovingMask(Falling | StrafeLeft));
        Assert.Equal(0u, FearOnsetAckGate.ClearMovingMask(Falling));
    }

    // Non-moving bits survive the clear.
    [Fact]
    public void ClearMovingMask_KeepsNonMovingBits()
    {
        Assert.Equal(Swimming | Root, FearOnsetAckGate.ClearMovingMask(Falling | Forward | Swimming | Root));
    }
}
