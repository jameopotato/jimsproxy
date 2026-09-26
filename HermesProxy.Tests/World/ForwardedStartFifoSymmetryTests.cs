using System;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (fifo-terminator-symmetry): the forwarded-START CastID FIFO must obey
// "every enqueue has exactly one consuming terminator, matched or not". An orphan
// START (pending entry evicted before the START arrived) enqueues like any other,
// but its terminator used to take the unmatched paths, which never consumed the
// entry — leaving the FIFO off-by-one for every later same-spell cast for the rest
// of the session (the mining cast-bar-overrun wedge, jimsproxy-20260820-191703).
// These tests pin the session-level state the new unmatched-terminator paths key on.
public class ForwardedStartFifoSymmetryTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static WowGuid128 CastId(ulong low) => new WowGuid128(0x1234, low);

    private static ClientCastRequest MakeCast(uint spellId, bool hasStarted = false, uint legacySpellId = 0)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            LegacySpellId = legacySpellId,
            Timestamp = Environment.TickCount,
            HasStarted = hasStarted,
        };
    }

    [Fact]
    public void HasStartedPendingCastForSpell_TrueOnlyForStartedSameSpell()
    {
        var session = NewSession();
        session.EnqueuePendingNormalCast(MakeCast(10248, hasStarted: false));

        Assert.False(session.HasStartedPendingCastForSpell(10248));

        session.EnqueuePendingNormalCast(MakeCast(10248, hasStarted: true));

        Assert.True(session.HasStartedPendingCastForSpell(10248));
        Assert.False(session.HasStartedPendingCastForSpell(2053));
    }

    [Fact]
    public void HasStartedPendingCastForSpell_MatchesLegacySpellId()
    {
        var session = NewSession();
        session.EnqueuePendingNormalCast(MakeCast(25007, hasStarted: true, legacySpellId: 24997));

        Assert.True(session.HasStartedPendingCastForSpell(24997));
        Assert.True(session.HasStartedPendingCastForSpell(25007));
    }

    // The GO-side guard's exact precondition set: an orphan's forwarded-START entry in the
    // FIFO, a fresh unstarted press pending, no started pending cast. The guard must resolve
    // the GO from the FIFO and leave the press's pending entry untouched for its own
    // terminator — dequeuing it here is the entry-steal that re-arms the wedge.
    [Fact]
    public void OrphanGoScenario_FifoResolvesWithoutStealingUnstartedPress()
    {
        var session = NewSession();
        var orphanCastId = CastId(20733);   // orphan START forwarded after its pending entry died
        session.EnqueueForwardedStartCastId(10248, orphanCastId);
        var freshPress = MakeCast(10248, hasStarted: false);
        session.EnqueuePendingNormalCast(freshPress);

        Assert.True(session.HasNonStartedPendingCastForSpell(10248));
        Assert.False(session.HasStartedPendingCastForSpell(10248));

        Assert.True(session.TryPopForwardedStartCastId(10248, out var recovered));
        Assert.Equal(orphanCastId, recovered);

        // The press is still queued for its own START/GO/failure to consume.
        Assert.True(session.HasNonStartedPendingCastForSpell(10248));
        Assert.False(session.TryPopForwardedStartCastId(10248, out _));
    }

    // Terminator symmetry for the wedge specimen itself: the orphan's failure pops its FIFO
    // entry, so the next cast's START↔terminator pairing starts from an aligned head.
    [Fact]
    public void OrphanFailure_PopRestoresFifoAlignmentForNextCast()
    {
        var session = NewSession();
        var orphanCastId = CastId(20733);
        session.EnqueueForwardedStartCastId(10248, orphanCastId);

        // Unmatched CAST_FAILED path: pop consumes the orphan entry.
        Assert.True(session.TryPopForwardedStartCastId(10248, out var popped));
        Assert.Equal(orphanCastId, popped);

        // Next mining cast enqueues and pops ITS OWN CastID — no off-by-one.
        var nextCastId = CastId(20734);
        session.EnqueueForwardedStartCastId(10248, nextCastId);
        Assert.True(session.TryPeekForwardedStartCastId(10248, out var peeked));
        Assert.Equal(nextCastId, peeked);
        Assert.True(session.TryPopForwardedStartCastId(10248, out var next));
        Assert.Equal(nextCastId, next);
    }
}
