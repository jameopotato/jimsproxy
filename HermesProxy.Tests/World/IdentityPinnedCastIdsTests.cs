using System;
using System.Collections.Generic;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (T1 identity-pinned cast correspondence): the 1.12 server strips the client's
// CastID, so the proxy must re-derive which press a server event belongs to. T1 makes that
// deterministic — every local-player terminating event (SPELL_GO / CAST_FAILED / SPELL_FAILURE)
// and the watchdog's synthetic closure is stamped with the CastID recorded at SPELL_START,
// drawn from a per-spell FIFO in START order, instead of depending on which queue entry the
// dequeue heuristic picked. These tests pin the FIFO's ordering/lifecycle and the watchdog
// data-path closure. The packet-handler wiring is gated behind Settings.IdentityPinnedCastIdsActive
// (LowLatencyMode && IdentityPinnedCastIds); the last test guards that OFF can't activate it.
public class IdentityPinnedCastIdsTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static WowGuid128 CastId(ulong n) => new WowGuid128(n, 0);

    private static ClientCastRequest StartedCast(uint spellId, WowGuid128 serverGuid, long watchdogDeadlineMs = 0)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            HasStarted = true,
            ServerGUID = serverGuid,
            WatchdogDeadlineMs = watchdogDeadlineMs,
        };
    }

    // --- Core invariant: START order is preserved, so interleaved same-spell START/GO pair. ---

    [Fact]
    public void EnqueueThenPop_TwoSameSpellStarts_PopReturnsStartOrder()
    {
        // Two casts of the same spell are forwarded in START order (A then B). The first GO must
        // pop A's CastID (pairing GO 1 ↔ START 1) and the second GO pop B's — independent of any
        // dequeue heuristic. This is the START↔GO correspondence the whole feature exists to make
        // deterministic.
        var session = NewSession();
        var startA = CastId(101);
        var startB = CastId(102);
        session.EnqueueForwardedStartCastId(13877, startA);
        session.EnqueueForwardedStartCastId(13877, startB);

        Assert.True(session.TryPopForwardedStartCastId(13877, out var firstGo));
        Assert.Equal(startA, firstGo);
        Assert.True(session.TryPopForwardedStartCastId(13877, out var secondGo));
        Assert.Equal(startB, secondGo);
        Assert.False(session.TryPopForwardedStartCastId(13877, out _));
    }

    [Fact]
    public void TryPopForwardedStartCastId_NoEntry_ReturnsFalse()
    {
        // Skip-START instants (server emits GO with no preceding START) leave no FIFO entry — the
        // caller must fall back to the dequeued entry's ServerGUID, i.e. today's behavior.
        var session = NewSession();

        Assert.False(session.TryPopForwardedStartCastId(11305, out var castId));
        Assert.Equal(default, castId);
    }

    [Fact]
    public void TryPeekForwardedStartCastId_ReturnsFrontWithoutConsuming()
    {
        // SMSG_SPELL_FAILURE only PEEKS the pending cast (the trailing CAST_FAILED / GO / watchdog
        // pops it), so peeking the FIFO must not remove the entry — a later pop still pairs.
        var session = NewSession();
        var start = CastId(55);
        session.EnqueueForwardedStartCastId(133, start);

        Assert.True(session.TryPeekForwardedStartCastId(133, out var peeked));
        Assert.Equal(start, peeked);
        // Still present for the real terminating event.
        Assert.True(session.TryPopForwardedStartCastId(133, out var popped));
        Assert.Equal(start, popped);
    }

    // --- Watchdog needs remove-by-value: an evicted cast may not be the FIFO head. ---

    [Fact]
    public void RemoveForwardedStartCastId_MiddleEntry_RemovesByValueNotHead()
    {
        // The watchdog evicts a specific started cast that timed out — which may be neither the
        // oldest nor the newest. Removing it by value (not popping the head) keeps the remaining
        // casts' CastIDs intact so their own GOs still pair.
        var session = NewSession();
        var a = CastId(1);
        var b = CastId(2);
        var c = CastId(3);
        session.EnqueueForwardedStartCastId(133, a);
        session.EnqueueForwardedStartCastId(133, b);
        session.EnqueueForwardedStartCastId(133, c);

        Assert.True(session.RemoveForwardedStartCastId(133, b));

        Assert.True(session.TryPopForwardedStartCastId(133, out var first));
        Assert.Equal(a, first);
        Assert.True(session.TryPopForwardedStartCastId(133, out var second));
        Assert.Equal(c, second);              // b was removed from the middle, order otherwise intact
        Assert.False(session.TryPopForwardedStartCastId(133, out _));
    }

    [Fact]
    public void RemoveForwardedStartCastId_AbsentValue_ReturnsFalse()
    {
        var session = NewSession();
        session.EnqueueForwardedStartCastId(133, CastId(1));

        Assert.False(session.RemoveForwardedStartCastId(133, CastId(999)));
        Assert.False(session.RemoveForwardedStartCastId(999, CastId(1)));
        // The real entry is untouched.
        Assert.True(session.TryPopForwardedStartCastId(133, out var still));
        Assert.Equal(CastId(1), still);
    }

    // --- Leak backstop: bounded depth so a missed pop can't grow without bound. ---

    [Fact]
    public void EnqueueForwardedStartCastId_ExceedsCap_DropsOldest()
    {
        // If some obscure removal path ever fails to pop, the per-spell FIFO is bounded (cap 8):
        // the oldest are dropped, never growing unbounded. The dropped ones are the oldest casts,
        // which are the most likely to already be resolved.
        var session = NewSession();
        for (ulong i = 1; i <= 10; i++)
            session.EnqueueForwardedStartCastId(133, CastId(i));

        var survivors = new List<WowGuid128>();
        while (session.TryPopForwardedStartCastId(133, out var id))
            survivors.Add(id);

        Assert.Equal(8, survivors.Count);          // capped
        Assert.Equal(CastId(3), survivors[0]);     // oldest two (1,2) dropped
        Assert.Equal(CastId(10), survivors[^1]);   // newest retained
    }

    [Fact]
    public void ForwardedStartCastIds_DifferentSpells_AreIndependent()
    {
        var session = NewSession();
        session.EnqueueForwardedStartCastId(133, CastId(1));
        session.EnqueueForwardedStartCastId(13877, CastId(2));

        Assert.True(session.TryPopForwardedStartCastId(133, out var x));
        Assert.Equal(CastId(1), x);
        Assert.True(session.TryPopForwardedStartCastId(13877, out var y));
        Assert.Equal(CastId(2), y);
    }

    [Fact]
    public void ClearForwardedStartCastIds_RemovesAll()
    {
        // Reconnect / world transfer drops all in-flight cast bookkeeping, including this FIFO,
        // so stale pre-reconnect CastIDs can't pair against a new server's casts.
        var session = NewSession();
        session.EnqueueForwardedStartCastId(133, CastId(1));
        session.EnqueueForwardedStartCastId(13877, CastId(2));

        session.ClearForwardedStartCastIds();

        Assert.False(session.TryPopForwardedStartCastId(133, out _));
        Assert.False(session.TryPopForwardedStartCastId(13877, out _));
    }

    // --- Guaranteed closure: an orphaned cast (no server response) gets a correctly-stamped
    //     synthetic closure within the deadline, and the FIFO is kept consistent. ---

    [Fact]
    public void DrainExpiredWatchdogCasts_OrphanStartedCast_EvictsWithForwardedCastId_AndFifoRemovable()
    {
        // A started cast whose SPELL_GO / CAST_FAILED never arrived. Past its deadline, the
        // watchdog drains it; the synthetic CastFailed it sends carries cast.ServerGUID — which is
        // exactly the CastID forwarded at START (and stored in the FIFO), so the closure pairs with
        // the client's open cast. Removing that entry by value then prevents a later same-spell GO
        // from popping this dead cast's CastID.
        var session = NewSession();
        long now = Environment.TickCount64;
        var serverGuid = CastId(777);
        var orphan = StartedCast(13877, serverGuid, watchdogDeadlineMs: now - 1000); // already overdue
        session.PendingNormalCasts.Enqueue(orphan);
        session.EnqueueForwardedStartCastId(13877, serverGuid); // what START forwarded == ServerGUID

        session.DrainExpiredWatchdogCasts(now, out var normalEvicted, out var petEvicted);

        Assert.Single(normalEvicted);
        Assert.Same(orphan, normalEvicted[0]);
        Assert.Equal(serverGuid, normalEvicted[0].ServerGUID);          // synthetic closure CastID is correct
        Assert.Empty(petEvicted);
        Assert.Empty(session.PendingNormalCasts);

        // The watchdog wiring removes the evicted cast's forwarded CastID by value.
        Assert.True(session.RemoveForwardedStartCastId(13877, serverGuid));
        Assert.False(session.TryPopForwardedStartCastId(13877, out _));  // no stale head left behind
    }

    [Fact]
    public void DrainExpiredWatchdogCasts_NotYetExpired_DoesNotEvict()
    {
        // The bounded deadline must not fire early: a cast still within its 2.5s window is left
        // alone (a real GO / CAST_FAILED may still be in flight at high latency — no premature,
        // false-positive closure).
        var session = NewSession();
        long now = Environment.TickCount64;
        var inFlight = StartedCast(13877, CastId(5), watchdogDeadlineMs: now + 100000); // future deadline
        session.PendingNormalCasts.Enqueue(inFlight);

        session.DrainExpiredWatchdogCasts(now, out var normalEvicted, out _);

        Assert.Empty(normalEvicted);
        Assert.Single(session.PendingNormalCasts);
    }

    // --- Regression guard: OFF can't activate the bundle. ---

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]   // LowLatencyMode alone — inactive
    [InlineData(false, true, false)]   // sub-toggle alone — inactive
    [InlineData(true, true, true)]     // both — active
    public void IdentityPinnedCastIdsActive_RequiresBothToggles(bool lowLatency, bool subToggle, bool expectedActive)
    {
        // The entire T1 mechanism (FIFO stamping + the keepalive watchdog pump + watchdog FIFO
        // removal) is gated on this property. With it false, none of the new code paths run, so
        // the Hold-and-Fire path stays byte-identical — the default-OFF regression guarantee.
        bool savedLowLatency = global::Framework.Settings.LowLatencyMode;
        bool savedSubToggle = global::Framework.Settings.IdentityPinnedCastIds;
        try
        {
            global::Framework.Settings.LowLatencyMode = lowLatency;
            global::Framework.Settings.IdentityPinnedCastIds = subToggle;
            Assert.Equal(expectedActive, global::Framework.Settings.IdentityPinnedCastIdsActive);
        }
        finally
        {
            global::Framework.Settings.LowLatencyMode = savedLowLatency;
            global::Framework.Settings.IdentityPinnedCastIds = savedSubToggle;
        }
    }
}
