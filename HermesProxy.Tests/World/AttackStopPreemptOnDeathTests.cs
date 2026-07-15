using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (ghost-swing fix): gating for the preemptive auto-attack SMSG_ATTACK_STOP the proxy
// sends to the modern client when the unit we're meleeing dies (SMSG_PARTY_KILL_LOG), instead of
// waiting ~1 RTT for the legacy server to echo our stop. These tests lock in the disjoint-state
// contract with PR #321 ("auto-attack stuck for ~30s when server rejects CMSG_ATTACK_SWING"):
// the preempt fires ONLY in a settled auto-attack, and must defer (leave state untouched)
// whenever a swing-start handshake is in flight — that state is owned by the SMSG_ATTACK_STOP
// handler's #321 logic.
public class AttackStopPreemptOnDeathTests
{
    private static GameSessionData NewState() => WowGuidTestHelper.CreateMockGameSessionData();
    private static WowGuid64 Creature(uint counter) => new WowGuid64(HighGuidTypeLegacy.Creature, 1234, counter);

    [Fact]
    public void TryClearSettledAttackTargetOnDeath_SettledAttackOnDeadVictim_ClearsAndReturnsTrue()
    {
        var state = NewState();
        var victim = Creature(1);
        state.CurrentAttackTarget = victim;
        state.WaitingForAttackStart = false;
        state.DeferredAttackStop = false;

        bool preempt = state.TryClearSettledAttackTargetOnDeath(victim);

        Assert.True(preempt);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
    }

    [Fact]
    public void TryClearSettledAttackTargetOnDeath_DifferentVictim_DoesNotFireOrClear()
    {
        // A different unit died (a party member's kill, our DoT on another mob). Our melee
        // target is unaffected — must not fire and must not touch CurrentAttackTarget.
        var state = NewState();
        var attacking = Creature(1);
        state.CurrentAttackTarget = attacking;

        bool preempt = state.TryClearSettledAttackTargetOnDeath(Creature(2));

        Assert.False(preempt);
        Assert.Equal(attacking, state.CurrentAttackTarget);
    }

    [Fact]
    public void TryClearSettledAttackTargetOnDeath_WaitingForAttackStart_DefersToPr321()
    {
        // Swing-start handshake in flight: PR #321's SMSG_ATTACK_STOP path owns this state.
        // We must not preempt and must not clear, or the two fixes would fight over the target.
        var state = NewState();
        var victim = Creature(1);
        state.CurrentAttackTarget = victim;
        state.WaitingForAttackStart = true;

        bool preempt = state.TryClearSettledAttackTargetOnDeath(victim);

        Assert.False(preempt);
        Assert.Equal(victim, state.CurrentAttackTarget);
    }

    [Fact]
    public void TryClearSettledAttackTargetOnDeath_DeferredAttackStop_DoesNotFire()
    {
        var state = NewState();
        var victim = Creature(1);
        state.CurrentAttackTarget = victim;
        state.DeferredAttackStop = true;

        bool preempt = state.TryClearSettledAttackTargetOnDeath(victim);

        Assert.False(preempt);
        Assert.Equal(victim, state.CurrentAttackTarget);
    }

    [Fact]
    public void TryClearSettledAttackTargetOnDeath_NotAutoAttacking_ReturnsFalse()
    {
        // Pure caster: never sent CMSG_ATTACK_SWING, so there is no settled target to tear down.
        var state = NewState();

        bool preempt = state.TryClearSettledAttackTargetOnDeath(Creature(1));

        Assert.False(preempt);
        Assert.Equal(WowGuid64.Empty, state.CurrentAttackTarget);
    }
}
