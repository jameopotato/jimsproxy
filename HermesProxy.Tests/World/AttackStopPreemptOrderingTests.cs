using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#450 — killing-blow ordering): gating for the arm/flush state machine that
// releases the ghost-swing preempt stop (PR #389) AFTER the trailing killing-blow
// SMSG_ATTACKER_STATE_UPDATE instead of inline from SMSG_PARTY_KILL_LOG. Kronos sends the
// melee killing blow after the kill log in the same burst; emitting the stop between the
// two makes the modern client re-play the hit as a fresh swing on the corpse — floating
// text + swing sound seconds late, after the loot window (issue #450 capture, 2026-07-31).
// Wire contract locked in here:
//   ArmPreemptAttackStop        — kill log arms; a second kill returns the older victim
//                                 so the caller can flush it before re-arming
//   TryConsumePreemptAttackStop — the pairing trigger: ONLY the local player's hit on the
//                                 armed victim releases (or cancels, at the ATTACK_STOP
//                                 echo site) the stop
//   TakePreemptAttackStopForFlush — the drain trigger: socket empty, no hit trailed
public class AttackStopPreemptOrderingTests
{
    private static GameSessionData NewState() => WowGuidTestHelper.CreateMockGameSessionData();
    private static readonly WowGuid128 Player = new(0x100, 0x0800040000000000);
    private static readonly WowGuid128 Mob = new(0xF5C, 0x2000040000005400);
    private static readonly WowGuid128 OtherMob = new(0xF60, 0x2000040000005400);

    [Fact]
    public void ArmPreemptAttackStop_NothingArmed_SetsPendingAndReturnsDefault()
    {
        var state = NewState();

        var prior = state.ArmPreemptAttackStop(Mob);

        Assert.Equal(default, prior);
        Assert.Equal(Mob, state.PendingPreemptAttackStopVictim);
    }

    [Fact]
    public void ArmPreemptAttackStop_SecondKillInBurst_ReturnsOlderVictimAndRearms()
    {
        // Multi-kill burst (cleave double kill): the older stop must be handed back for an
        // immediate flush — it can never pair with its ASU once a newer kill re-arms.
        var state = NewState();
        state.ArmPreemptAttackStop(Mob);

        var prior = state.ArmPreemptAttackStop(OtherMob);

        Assert.Equal(Mob, prior);
        Assert.Equal(OtherMob, state.PendingPreemptAttackStopVictim);
    }

    [Fact]
    public void TryConsumePreemptAttackStop_PlayersHitOnArmedVictim_ConsumesAndReturnsTrue()
    {
        // The Kronos shape from the #450 capture: kill log arms, the trailing 215-damage
        // ASU (attacker = player, victim = dead mob) releases the stop.
        var state = NewState();
        state.CurrentPlayerGuid = Player;
        state.ArmPreemptAttackStop(Mob);

        bool consumed = state.TryConsumePreemptAttackStop(Player, Mob);

        Assert.True(consumed);
        Assert.Equal(default, state.PendingPreemptAttackStopVictim);
    }

    [Fact]
    public void TryConsumePreemptAttackStop_HitOnDifferentVictim_PendingIntact()
    {
        // Player already swinging the NEXT mob while the stop for the dead one is armed —
        // that hit must not steal the armed stop.
        var state = NewState();
        state.CurrentPlayerGuid = Player;
        state.ArmPreemptAttackStop(Mob);

        bool consumed = state.TryConsumePreemptAttackStop(Player, OtherMob);

        Assert.False(consumed);
        Assert.Equal(Mob, state.PendingPreemptAttackStopVictim);
    }

    [Fact]
    public void TryConsumePreemptAttackStop_AttackerNotLocalPlayer_PendingIntact()
    {
        // Another mob's swing on the player (or a groupmate's hit) in the same burst is
        // not the pairing packet.
        var state = NewState();
        state.CurrentPlayerGuid = Player;
        state.ArmPreemptAttackStop(Mob);

        bool consumed = state.TryConsumePreemptAttackStop(OtherMob, Player);

        Assert.False(consumed);
        Assert.Equal(Mob, state.PendingPreemptAttackStopVictim);
    }

    [Fact]
    public void TryConsumePreemptAttackStop_NothingArmed_ReturnsFalse()
    {
        var state = NewState();
        state.CurrentPlayerGuid = Player;

        Assert.False(state.TryConsumePreemptAttackStop(Player, Mob));
    }

    [Fact]
    public void TakePreemptAttackStopForFlush_ReturnsVictimOnceThenDefault()
    {
        // Drain flush: first take emits, a second take (next drain pass) must find nothing —
        // the stop can never be sent twice.
        var state = NewState();
        state.ArmPreemptAttackStop(Mob);

        Assert.Equal(Mob, state.TakePreemptAttackStopForFlush());
        Assert.Equal(default, state.TakePreemptAttackStopForFlush());
        Assert.Equal(default, state.PendingPreemptAttackStopVictim);
    }
}
