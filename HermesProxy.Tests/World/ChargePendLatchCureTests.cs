using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (charge strafe-latch cure): the arm predicate, TTL gate, and synth
/// counter classification. See <see cref="ChargePendLatchCure"/> for the wire
/// evidence behind each rule.
/// </summary>
public class ChargePendLatchCureTests
{
    // The wire-proven orphan signature (3/3 field latches + 2/2 deliberate repros):
    // a pending strafe start in the charge-GO CHANGE_TRANSPORT flags.
    [Theory]
    [InlineData((uint)MovementFlagModern.PendingStrafeRight)]
    [InlineData((uint)MovementFlagModern.PendingStrafeLeft)]
    [InlineData((uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeRight))] // the exact 08-28 C30 shape
    public void ShouldArm_PendingStrafeStart_True(uint flags)
    {
        Assert.True(ChargePendLatchCure.ShouldArm(flags));
    }

    // Real (key-backed) strafe flags resolve from physical key state at spline
    // exit (tonight's C1–C4 controls) — never arm on them.
    [Theory]
    [InlineData((uint)MovementFlagModern.StrafeLeft)]
    [InlineData((uint)MovementFlagModern.StrafeRight)]
    [InlineData((uint)(MovementFlagModern.Forward | MovementFlagModern.StrafeRight | MovementFlagModern.Falling))] // session-2 C5: mid-air press, direct apply
    public void ShouldArm_RealStrafeFlags_False(uint flags)
    {
        Assert.False(ChargePendLatchCure.ShouldArm(flags));
    }

    // Pending STOPS resolve safely at spline exit (07-11 C1: PendStrafeStop+PendFwd
    // both wiped clean), and PendingForward did not orphan in its one specimen —
    // out of scope until evidence says otherwise.
    [Theory]
    [InlineData((uint)MovementFlagModern.PendingStrafeStop)]
    [InlineData((uint)MovementFlagModern.PendingStop)]
    [InlineData((uint)(MovementFlagModern.StrafeLeft | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeStop | MovementFlagModern.PendingForward))] // the exact 07-11 C1 shape
    [InlineData(0u)]
    public void ShouldArm_NonArmingFlags_False(uint flags)
    {
        Assert.False(ChargePendLatchCure.ShouldArm(flags));
    }

    // Arm→fire spans the charge spline (≤ ~0.95s observed) plus the server's
    // unroot lag (~0.6s); a stale arm (e.g. a pend-carrying real transport
    // boarding, where no spline-unroot ever follows) must expire.
    [Fact]
    public void IsArmed_WithinTtl_True()
    {
        Assert.True(ChargePendLatchCure.IsArmed(armedAtMs: 1000, nowMs: 1000 + ChargePendLatchCure.ArmTtlMs));
    }

    [Fact]
    public void IsArmed_Expired_False()
    {
        Assert.False(ChargePendLatchCure.IsArmed(armedAtMs: 1000, nowMs: 1001 + ChargePendLatchCure.ArmTtlMs));
    }

    [Fact]
    public void IsArmed_Disarmed_False()
    {
        Assert.False(ChargePendLatchCure.IsArmed(armedAtMs: 0, nowMs: 5000));
    }

    // Fire on the orphan's first appearance: armed pend's real bit set, pend bit
    // gone. Shapes below are verbatim from the 08-29 falsification captures.
    [Fact]
    public void ShouldFire_PendApplied_True()
    {
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeRight);
        // charge 1: SPLINE_DONE/MOVE_STOP [StrafeR|Falling]
        Assert.True(ChargePendLatchCure.ShouldFire(armed, (uint)(MovementFlagModern.StrafeRight | MovementFlagModern.Falling)));
        // charge 1: FALL_LAND [StrafeR]
        Assert.True(ChargePendLatchCure.ShouldFire(armed, (uint)MovementFlagModern.StrafeRight));
    }

    [Fact]
    public void ShouldFire_PendAppliedLeft_True()
    {
        // charge 2: armed [Fwd|Falling|PendStop|PendStrafeL], FALL_LAND [StrafeL]
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStop | MovementFlagModern.PendingStrafeLeft);
        Assert.True(ChargePendLatchCure.ShouldFire(armed, (uint)MovementFlagModern.StrafeLeft));
    }

    // The arming packet itself and mid-spline packets still carry the pend
    // unapplied (real bit absent or pend bit still set) — no fire.
    [Fact]
    public void ShouldFire_PendStillQueued_False()
    {
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeRight);
        Assert.False(ChargePendLatchCure.ShouldFire(armed, armed)); // the arming CHANGE_TRANSPORT
        // charge 2's SPLINE_DONE [Falling|PendStrafeL]: pend not yet applied
        uint armedL = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeLeft);
        Assert.False(ChargePendLatchCure.ShouldFire(armedL, (uint)(MovementFlagModern.Falling | MovementFlagModern.PendingStrafeLeft)));
    }

    // Defensive (never wire-observed): real bit AND the armed pend bit set in the
    // same packet must NOT fire — a pend still queued could re-apply after the
    // pulse and re-latch; wait for a packet where the pend is consumed.
    [Fact]
    public void ShouldFire_RealBitWithPendStillPresent_False()
    {
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeRight);
        Assert.False(ChargePendLatchCure.ShouldFire(armed,
            (uint)(MovementFlagModern.StrafeRight | MovementFlagModern.PendingStrafeRight)));
    }

    // The OTHER side's strafe appearing is the player's own fresh input, not the
    // armed pend applying — no fire.
    [Fact]
    public void ShouldFire_OppositeSideStrafe_False()
    {
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeLeft);
        Assert.False(ChargePendLatchCure.ShouldFire(armed, (uint)MovementFlagModern.StrafeRight));
    }

    // Flags with no strafe content at all (the common post-exit packets when the
    // pend was resolved) — no fire; the TTL expires the arm.
    [Fact]
    public void ShouldFire_NoStrafeContent_False()
    {
        uint armed = (uint)(MovementFlagModern.Forward | MovementFlagModern.Falling | MovementFlagModern.PendingStrafeRight);
        Assert.False(ChargePendLatchCure.ShouldFire(armed, (uint)MovementFlagModern.Falling));
        Assert.False(ChargePendLatchCure.ShouldFire(armed, 0));
    }

    [Fact]
    public void ExpectedRealStrafeMask_MapsPendsToRealBits()
    {
        Assert.Equal((uint)MovementFlagModern.StrafeLeft,
            ChargePendLatchCure.ExpectedRealStrafeMask((uint)MovementFlagModern.PendingStrafeLeft));
        Assert.Equal((uint)MovementFlagModern.StrafeRight,
            ChargePendLatchCure.ExpectedRealStrafeMask((uint)(MovementFlagModern.Forward | MovementFlagModern.PendingStrafeRight)));
        Assert.Equal(0u, ChargePendLatchCure.ExpectedRealStrafeMask((uint)MovementFlagModern.StrafeRight));
    }

    [Fact]
    public void IsCureCounter_OwnCounters_True()
    {
        Assert.True(ChargePendLatchCure.IsCureCounter(ChargePendLatchCure.SynthCounterRoot));
        Assert.True(ChargePendLatchCure.IsCureCounter(ChargePendLatchCure.SynthCounterUnroot));
    }

    // Never claim the carried-root cure's counter (or the legacy server's constant
    // 0) — the dedicated ack breadcrumb must not shadow either.
    [Fact]
    public void IsCureCounter_ForeignCounters_False()
    {
        Assert.False(ChargePendLatchCure.IsCureCounter(WorldEntryCeremonyTracker.SynthCounterUnroot));
        Assert.False(ChargePendLatchCure.IsCureCounter(0));
        Assert.False(ChargePendLatchCure.IsCureCounter(0xFFFFFFFF));
    }

    // Cross-invariant: the cure counters must sit inside the synth range so the
    // existing generic swallow in HandleMoveForceAck2 remains a safety net — a
    // cure ack must NEVER reach the legacy server (kick-counter exposure) even if
    // the dedicated branch is ever reordered or removed.
    [Fact]
    public void CureCounters_AreInsideSynthSwallowRange()
    {
        Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(ChargePendLatchCure.SynthCounterRoot));
        Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(ChargePendLatchCure.SynthCounterUnroot));
    }

    // And they must not collide with the carried-root cure's counter, whose ack
    // path logs a different event.
    [Fact]
    public void CureCounters_DistinctFromCarriedRootCure()
    {
        Assert.NotEqual(WorldEntryCeremonyTracker.SynthCounterUnroot, ChargePendLatchCure.SynthCounterRoot);
        Assert.NotEqual(WorldEntryCeremonyTracker.SynthCounterUnroot, ChargePendLatchCure.SynthCounterUnroot);
    }
}
