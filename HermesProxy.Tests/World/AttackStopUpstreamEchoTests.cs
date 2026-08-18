using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (post-kill upstream stop): the kill-time preempt (AttackStopPreemptOnDeathTests) pushes
// the modern client an early SMSG_ATTACK_STOP but is client-only — the client, told it stopped,
// never sends CMSG_ATTACK_STOP, so the legacy server keeps the swing running against the corpse
// and refuses it on the next swing tick (ATTACKSWING_DEADTARGET ~1.4-3.5s after essentially every
// kill; 26 swing errors across ~26 kills in the 2026-08-18 capture). The fix sends the legacy
// server the stop a real 1.12 client would have produced, and must then consume the server's echo
// (SMSG_ATTACKSTOP naming the corpse, wire-verified on Kronos) so it neither reaches the client as
// a duplicate nor runs the handshake bookkeeping — a player who re-engaged a NEW target within the
// echo RTT (~200ms) must keep that swing handshake intact (#464 wedge family).
public class AttackStopUpstreamEchoTests
{
    private static GameSessionData NewState() => WowGuidTestHelper.CreateMockGameSessionData();
    private static WowGuid64 Creature(uint counter) => new WowGuid64(HighGuidTypeLegacy.Creature, 1234, counter);

    [Fact]
    public void RecordThenConsume_ExactVictim_ConsumesExactlyOnce()
    {
        var state = NewState();
        var victim = Creature(1);
        state.RecordSyntheticUpstreamAttackStop(victim);

        Assert.True(state.TryConsumeSyntheticUpstreamStopEcho(victim));
        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(victim));
    }

    [Fact]
    public void Consume_DifferentVictim_DoesNotConsumeOrDisturbPending()
    {
        var state = NewState();
        var victim = Creature(1);
        state.RecordSyntheticUpstreamAttackStop(victim);

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(Creature(2)));
        Assert.True(state.TryConsumeSyntheticUpstreamStopEcho(victim));
    }

    [Fact]
    public void Consume_EmptyVictim_NeverMatches()
    {
        // An empty-victim SMSG_ATTACKSTOP can be a REAL server-initiated stop (CC, death, refusal)
        // and must always take the normal #464 path — the echo swallow pairs by exact victim only.
        var state = NewState();
        state.RecordSyntheticUpstreamAttackStop(Creature(1));

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(WowGuid64.Empty));
        Assert.True(state.TryConsumeSyntheticUpstreamStopEcho(Creature(1)));
    }

    [Fact]
    public void Record_EmptyVictim_IsIgnored()
    {
        var state = NewState();
        state.RecordSyntheticUpstreamAttackStop(WowGuid64.Empty);

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(WowGuid64.Empty));
    }

    [Fact]
    public void Consume_NothingRecorded_ReturnsFalse()
    {
        var state = NewState();

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(Creature(1)));
    }

    [Fact]
    public void Record_BoundedFifo_EvictsOldestBeyondEight()
    {
        // An echo that never comes (the server had already dropped its swing state) must not
        // accumulate forever. Spawn-unique high bits make a stale entry inert; the bound only
        // caps growth. Oldest goes first.
        var state = NewState();
        for (uint i = 1; i <= 9; i++)
            state.RecordSyntheticUpstreamAttackStop(Creature(i));

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(Creature(1)));
        for (uint i = 2; i <= 9; i++)
            Assert.True(state.TryConsumeSyntheticUpstreamStopEcho(Creature(i)));
    }

    [Fact]
    public void KillThenReengage_EchoConsumed_NewSwingHandshakeUntouched()
    {
        // The full sequence the fix exists for: settled melee on V, V dies (preempt clears the
        // target and records the upstream stop), player re-engages N inside the echo RTT, then
        // the server's echo naming V arrives. The handler consumes it and returns — the swing
        // handshake for N must be exactly as the re-engage left it.
        var state = NewState();
        var deadVictim = Creature(1);
        var newTarget = Creature(2);

        state.CurrentAttackTarget = deadVictim;
        state.WaitingForAttackStart = false;
        state.DeferredAttackStop = false;

        Assert.True(state.TryClearSettledAttackTargetOnDeath(deadVictim));
        state.RecordSyntheticUpstreamAttackStop(deadVictim);

        Assert.True(state.TryBeginLocalPlayerAttackSwing(newTarget));

        Assert.True(state.TryConsumeSyntheticUpstreamStopEcho(deadVictim));
        Assert.Equal(newTarget, state.CurrentAttackTarget);
        Assert.True(state.WaitingForAttackStart);
    }

    [Fact]
    public void KillThenReengage_NoPendingEcho_ApplyStopStillPreservesTargetSwitch()
    {
        // Disjointness with the existing #464/#321 semantics: when nothing was recorded (fix
        // disabled, or an organic server stop), a corpse-named stop that falls through to
        // ApplyLocalPlayerAttackStop mid re-engage keeps the new handshake (PreserveTargetSwitch).
        var state = NewState();
        var deadVictim = Creature(1);
        var newTarget = Creature(2);

        state.CurrentAttackTarget = deadVictim;
        Assert.True(state.TryClearSettledAttackTargetOnDeath(deadVictim));
        Assert.True(state.TryBeginLocalPlayerAttackSwing(newTarget));

        Assert.False(state.TryConsumeSyntheticUpstreamStopEcho(deadVictim));
        var outcome = state.ApplyLocalPlayerAttackStop(deadVictim);

        Assert.Equal(PlayerAttackStopOutcome.PreserveTargetSwitch, outcome);
        Assert.Equal(newTarget, state.CurrentAttackTarget);
        Assert.True(state.WaitingForAttackStart);
    }
}
