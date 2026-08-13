using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (empty-victim wedge): Kronos answers a Charge at an evading/leashing mob with
// SMSG_ATTACKSTOP(player, 0) instead of SMSG_ATTACKSTART. The client immediately re-sends
// CMSG_ATTACK_SWING. Before the fix, an EMPTY stop victim never matched CurrentAttackTarget,
// so the stop fell through the "target-switch" branch and cleared nothing — CurrentAttackTarget
// stayed pinned to the mob and the de-dupe guard swallowed every later swing, so the player's
// auto-attack never recovered (no swings at all for the rest of the session).
//
// Sequence below is lifted from modern_42597_1786595484.pkt (2026-08-12, player low=27032,
// wolf low=4628), Charge at t=0:
//     +0.000  C2S CMSG_ATTACK_SWING(4628)          -> forwarded
//     +0.219  S2C SMSG_ATTACK_STOP(player, EMPTY)  <- Kronos refuses the engage
//     +0.422  C2S CMSG_ATTACK_SWING(4628)          -> MUST be forwarded, was swallowed
// The same sequence appears a second time in that capture 386s earlier (mob 5477), which only
// recovered because a second Charge eventually produced a real ATTACK_START.
public class AttackStopEmptyVictimWedgeTests
{
    private static GameSessionData NewState() => WowGuidTestHelper.CreateMockGameSessionData();
    private static WowGuid64 Creature(uint counter) => new WowGuid64(HighGuidTypeLegacy.Creature, 1234, counter);

    private static readonly WowGuid64 Wolf = Creature(4628);

    [Fact]
    public void ChargeAtEvadingMob_EmptyVictimStop_DoesNotWedgeAutoAttack()
    {
        var state = NewState();

        // +0.000 — swing rides along with the Charge and is forwarded.
        Assert.True(state.TryBeginLocalPlayerAttackSwing(Wolf));
        Assert.True(state.WaitingForAttackStart);

        // +0.219 — Kronos refuses the engage: stop naming the player, with NO victim.
        var outcome = state.ApplyLocalPlayerAttackStop(WowGuid64.Empty);

        Assert.Equal(PlayerAttackStopOutcome.ClearRejectedHandshake, outcome);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
        Assert.False(state.WaitingForAttackStart);

        // +0.422 — the client's retry MUST reach the legacy server. This is the regression:
        // before the fix this returned false and the swing was swallowed forever.
        Assert.True(state.TryBeginLocalPlayerAttackSwing(Wolf));
    }

    [Fact]
    public void MatchingVictimStop_StillClearsRejectedHandshake()
    {
        // PR #321 / 8c2c34e6 behaviour — the named-victim form of the same rejection.
        var state = NewState();
        var mob = Creature(1);
        Assert.True(state.TryBeginLocalPlayerAttackSwing(mob));

        var outcome = state.ApplyLocalPlayerAttackStop(mob);

        Assert.Equal(PlayerAttackStopOutcome.ClearRejectedHandshake, outcome);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
        Assert.False(state.WaitingForAttackStart);
        Assert.True(state.TryBeginLocalPlayerAttackSwing(mob));
    }

    [Fact]
    public void TargetSwitchStop_ForOldVictim_StillPreservesNewTarget()
    {
        // The behaviour the empty-victim branch must NOT cannibalise: a stop naming the OLD
        // target while a swing handshake for the NEW target is in flight keeps the new target.
        var state = NewState();
        var oldTarget = Creature(1);
        var newTarget = Creature(2);
        Assert.True(state.TryBeginLocalPlayerAttackSwing(oldTarget));
        Assert.True(state.TryBeginLocalPlayerAttackSwing(newTarget));

        var outcome = state.ApplyLocalPlayerAttackStop(oldTarget);

        Assert.Equal(PlayerAttackStopOutcome.PreserveTargetSwitch, outcome);
        Assert.Equal(newTarget, state.CurrentAttackTarget);
        Assert.True(state.WaitingForAttackStart);
        // Still mid-handshake on the new target, so a duplicate swing stays swallowed.
        Assert.False(state.TryBeginLocalPlayerAttackSwing(newTarget));
    }

    [Fact]
    public void DeferredStop_TakesPrecedence_AndIsFlushedToServer()
    {
        var state = NewState();
        var mob = Creature(1);
        Assert.True(state.TryBeginLocalPlayerAttackSwing(mob));
        state.DeferredAttackStop = true; // client sent CMSG_ATTACK_STOP mid-handshake

        var outcome = state.ApplyLocalPlayerAttackStop(WowGuid64.Empty);

        Assert.Equal(PlayerAttackStopOutcome.FlushDeferredStop, outcome);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
        Assert.False(state.DeferredAttackStop);
    }

    [Fact]
    public void SettledStop_NotWaiting_StillClearsTarget()
    {
        // Gouge / Blind / Vanish etc. stop a settled auto-attack; empty victim must not
        // divert this away from the settled branch.
        var state = NewState();
        var mob = Creature(1);
        state.CurrentAttackTarget = mob;
        state.WaitingForAttackStart = false;

        var outcome = state.ApplyLocalPlayerAttackStop(WowGuid64.Empty);

        Assert.Equal(PlayerAttackStopOutcome.ClearSettledTarget, outcome);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
    }

    [Fact]
    public void DuplicateSwing_AtSameVictim_IsStillSwallowed()
    {
        // The de-dupe guard itself must survive the refactor.
        var state = NewState();
        var mob = Creature(1);

        Assert.True(state.TryBeginLocalPlayerAttackSwing(mob));
        Assert.False(state.TryBeginLocalPlayerAttackSwing(mob));
    }

    [Fact]
    public void Swing_CancelsPendingDeferredStop()
    {
        var state = NewState();
        var oldTarget = Creature(1);
        var newTarget = Creature(2);
        state.CurrentAttackTarget = oldTarget;
        state.DeferredAttackStop = true;

        Assert.True(state.TryBeginLocalPlayerAttackSwing(newTarget));

        Assert.False(state.DeferredAttackStop);
        Assert.Equal(newTarget, state.CurrentAttackTarget);
    }
}
