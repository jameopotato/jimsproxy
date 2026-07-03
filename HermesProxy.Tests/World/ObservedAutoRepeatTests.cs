using System;
using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (observed-bow retract): the retract that lowers an observed shooter's latched ranged-aim
// (Auto Shot 75 / wand Shoot 5019) is driven by DETERMINISTIC stop edges — target death, shooter
// death/move, retarget — instead of a time-based quiescence sweep. These cover the GameState tracking
// the WorldClient packet handlers key off; no clock, so every case is synchronous.
public class ObservedAutoRepeatTests
{
    static ObservedAutoRepeatTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();
    private static WowGuid128 Shooter(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);
    private static WowGuid128 Mob(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 54321, counter);

    [Fact]
    public void TryEnd_FreshSession_ReturnsFalse()
    {
        var session = NewSession();
        Assert.False(session.TryEndObservedAutoRepeat(Shooter(1)));
    }

    [Fact]
    public void NoteThenStop_RetractsExactlyOnce()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivity(hunter, Mob(1));

        // First stop edge retracts; a second must not re-fire (one cancel per latch).
        Assert.True(session.TryEndObservedAutoRepeat(hunter));
        Assert.False(session.TryEndObservedAutoRepeat(hunter));
    }

    [Fact]
    public void VictimDeath_RetractsEveryShooterAimedAtIt()
    {
        var session = NewSession();
        var s1 = Shooter(1);
        var s2 = Shooter(2);
        var s3 = Shooter(3);
        var mob = Mob(1);
        session.NoteObservedAutoRepeatActivity(s1, mob);
        session.NoteObservedAutoRepeatActivity(s2, mob);
        session.NoteObservedAutoRepeatActivity(s3, Mob(2)); // aimed at a different mob

        var retracted = session.EndObservedAutoRepeatForVictim(mob);

        Assert.Equal(2, retracted.Count);
        Assert.Contains(s1, retracted);
        Assert.Contains(s2, retracted);
        Assert.DoesNotContain(s3, retracted);
        // s3 is still latched against the surviving mob.
        Assert.True(session.TryEndObservedAutoRepeat(s3));
    }

    [Fact]
    public void VictimDeath_UnrelatedVictim_RetractsNothingAndKeepsLatch()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivity(hunter, Mob(1));

        Assert.Empty(session.EndObservedAutoRepeatForVictim(Mob(2)));
        // Untouched — still latched.
        Assert.True(session.TryEndObservedAutoRepeat(hunter));
    }

    [Fact]
    public void TargetChange_ToDifferentUnit_Retracts()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivity(hunter, Mob(1));

        Assert.True(session.TryEndObservedAutoRepeatOnTargetChange(hunter, Mob(2)));
        // Removed by the retract — a following stop edge is a no-op.
        Assert.False(session.TryEndObservedAutoRepeat(hunter));
    }

    [Fact]
    public void TargetChange_ToClearedTarget_Retracts()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivity(hunter, Mob(1));

        Assert.True(session.TryEndObservedAutoRepeatOnTargetChange(hunter, WowGuid128.Empty));
    }

    [Fact]
    public void TargetChange_SameTarget_DoesNotRetract()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        var mob = Mob(1);
        session.NoteObservedAutoRepeatActivity(hunter, mob);

        // A redundant UNIT_FIELD_TARGET update for the same mob must NOT lower a still-firing bow.
        Assert.False(session.TryEndObservedAutoRepeatOnTargetChange(hunter, mob));
        // Still latched.
        Assert.True(session.TryEndObservedAutoRepeat(hunter));
    }

    [Fact]
    public void TargetChange_UntrackedShooter_DoesNotRetract()
    {
        var session = NewSession();
        // No latch for this shooter — a target change must not synthesize a phantom cancel.
        Assert.False(session.TryEndObservedAutoRepeatOnTargetChange(Shooter(1), Mob(1)));
    }

    [Fact]
    public void Note_RefreshesTarget_DeathMatchesTheLatestMob()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivity(hunter, Mob(1));
        // Mid-series retarget: the next shot lands on a new mob, refreshing the cached target.
        session.NoteObservedAutoRepeatActivity(hunter, Mob(2));

        // The OLD target dying no longer matches.
        Assert.Empty(session.EndObservedAutoRepeatForVictim(Mob(1)));
        // The CURRENT target dying retracts.
        Assert.Contains(hunter, session.EndObservedAutoRepeatForVictim(Mob(2)));
    }

    [Fact]
    public void PerShooter_StoppingOneKeepsTheOther()
    {
        var session = NewSession();
        var stopped = Shooter(1);
        var firing = Shooter(2);
        var mob = Mob(1);
        session.NoteObservedAutoRepeatActivity(stopped, mob);
        session.NoteObservedAutoRepeatActivity(firing, mob);

        Assert.True(session.TryEndObservedAutoRepeat(stopped));
        // The other shooter is untouched.
        Assert.True(session.TryEndObservedAutoRepeat(firing));
    }

    [Fact]
    public void ClearAll_DropsAllTracking()
    {
        var session = NewSession();
        session.NoteObservedAutoRepeatActivity(Shooter(1), Mob(1));
        session.NoteObservedAutoRepeatActivity(Shooter(2), Mob(1));

        session.ClearAllObservedAutoRepeat();

        Assert.False(session.TryEndObservedAutoRepeat(Shooter(1)));
        Assert.False(session.TryEndObservedAutoRepeat(Shooter(2)));
    }

    [Fact]
    public void ResetInFlightCastState_ClearsObservedAutoRepeat()
    {
        var session = NewSession();
        session.NoteObservedAutoRepeatActivity(Shooter(1), Mob(1));

        session.ResetInFlightCastState();

        Assert.False(session.TryEndObservedAutoRepeat(Shooter(1)));
    }

    // Quiescence catch-all: a shooter that stops with its target still alive and stands still (out of
    // ammo, /stopattack, LoS, target dummy) is covered by NO edge, so the sweep must retract it once it
    // goes quiet past the threshold — and never before.
    [Fact]
    public void Sweep_QuietShooter_RetractsAfterThreshold()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivityForTest(hunter, Mob(1), nowMs: 0);

        // Still within the cadence window — a steady shooter must not flicker.
        Assert.Empty(session.SweepObservedAutoRepeat(3999));
        // Past the quiet threshold — the series ended, lower the bow.
        Assert.Contains(hunter, session.SweepObservedAutoRepeat(4000));
        // Removed by the sweep — a second sweep is a no-op.
        Assert.Empty(session.SweepObservedAutoRepeat(10000));
    }

    [Fact]
    public void Sweep_LatestShotRefreshesQuietWindow()
    {
        var session = NewSession();
        var hunter = Shooter(1);
        session.NoteObservedAutoRepeatActivityForTest(hunter, Mob(1), nowMs: 0);
        // A fresh shot at t=3000 pushes the quiet deadline out to 7000.
        session.NoteObservedAutoRepeatActivityForTest(hunter, Mob(1), nowMs: 3000);

        Assert.Empty(session.SweepObservedAutoRepeat(6999));
        Assert.Contains(hunter, session.SweepObservedAutoRepeat(7000));
    }
}
