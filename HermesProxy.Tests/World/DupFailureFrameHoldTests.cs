using System;
using HermesProxy;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (dup-failure frame hold, the #394 collision strand): a dup press's CAST_FAILED
// delivered while its same-spell STARTED cast is in flight can share a client frame with that
// cast's SPELL_GO (the legacy server batches the dup rejection into the tick that completes
// the cast — 2/2 specimens in the 2026-08-14 Stonetavern JSONL at Δ0-1ms). The client's
// kit-cancel sweep runs by (unit, visualID), not CastID, so the same-frame FAILED can tear the
// live kit ahead of the GO's end-event close and orphan the loop sound. The fix holds the
// fully-built dup failure and releases it after the started cast's terminal event forwards
// (SugarProxy's AddFailedPacket/GetFailedPacket shape — hold by data dependency, no clocks),
// with a stale sweep tied to the pending-cast lifecycle so a silently-evicted anchor can't
// strand the dup's button-release. These tests cover the store: the hold predicate
// (HasStartedPendingCastForSpell), FIFO hold/release, and stale detection.
public class DupFailureFrameHoldTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId, bool started = false, uint legacySpellId = 0)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            LegacySpellId = legacySpellId,
            Timestamp = Environment.TickCount,
            HasStarted = started,
        };
    }

    private static GameSessionData.HeldDupFailure MakeHeld(
        uint spellId,
        uint reason = (uint)HermesProxy.World.Enums.SpellCastResultVanilla.SpellInProgress)
    {
        var held = new GameSessionData.HeldDupFailure
        {
            SpellId = spellId,
            ReasonId = reason,
            HeldAtMs = Environment.TickCount64,
        };
        held.Packets.Add(new CastFailed { SpellID = spellId, Reason = reason });
        return held;
    }

    // ---- HasStartedPendingCastForSpell (the hold predicate) ----

    [Fact]
    public void HasStarted_EmptyQueue_ReturnsFalse()
    {
        Assert.False(NewSession().HasStartedPendingCastForSpell(2053));
    }

    [Fact]
    public void HasStarted_OnlyUnstartedSameSpell_ReturnsFalse()
    {
        // A dup with no started twin (skip-START instant, or the cast already terminated):
        // nothing to collide with — the failure must deliver immediately, not hold.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2053));

        Assert.False(session.HasStartedPendingCastForSpell(2053));
    }

    [Fact]
    public void HasStarted_StartedSameSpell_ReturnsTrue()
    {
        // The specimen shape: Lesser Heal (2053) started (bar filling), dup press bounced.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2053, started: true));

        Assert.True(session.HasStartedPendingCastForSpell(2053));
    }

    [Fact]
    public void HasStarted_MatchesByLegacySpellId()
    {
        // SoM-renumbered item cast: the legacy server replies with the old id.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(363880, started: true, legacySpellId: 17626));

        Assert.True(session.HasStartedPendingCastForSpell(17626));
        Assert.True(session.HasStartedPendingCastForSpell(363880));
    }

    // ---- Hold / release bookkeeping ----

    [Fact]
    public void TakeHeld_ReturnsInHoldOrder_ThenNull()
    {
        // Two dup bounces during one cast (spam) must replay in the order the server
        // rejected them — Sugar appends to a slice and drains it FIFO; so do we.
        var session = NewSession();
        var first = MakeHeld(2053);
        var second = MakeHeld(2053);
        session.HoldDupFailure(first);
        session.HoldDupFailure(second);
        Assert.Equal(2, session.HeldDupFailureCount);

        var taken = session.TakeHeldDupFailures(2053);

        Assert.NotNull(taken);
        Assert.Equal(2, taken!.Count);
        Assert.Same(first, taken[0]);
        Assert.Same(second, taken[1]);
        Assert.Equal(0, session.HeldDupFailureCount);
        Assert.Null(session.TakeHeldDupFailures(2053)); // drained — a second GO releases nothing
    }

    [Fact]
    public void TakeHeld_OtherSpell_ReturnsNullAndLeavesEntry()
    {
        // A different spell's GO must not release another spell's held dup.
        var session = NewSession();
        session.HoldDupFailure(MakeHeld(2053));

        Assert.Null(session.TakeHeldDupFailures(592));
        Assert.Equal(1, session.HeldDupFailureCount);
    }

    // ---- Stale sweep (anchor lifecycle) ----

    [Fact]
    public void TakeStale_AnchorStillStarted_ReturnsNull()
    {
        // The started cast is still in flight — the hold must stand until its terminal.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2053, started: true));
        session.HoldDupFailure(MakeHeld(2053));

        Assert.Null(session.TakeStaleHeldDupFailures());
        Assert.Equal(1, session.HeldDupFailureCount);
    }

    [Fact]
    public void TakeStale_AnchorGone_ReturnsHeld()
    {
        // The anchor left the queue through an eviction path (no GO, no real CAST_FAILED):
        // the held dup must come back for delivery — an unanswered press strands the
        // client's action button lit.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2053, started: true));
        var held = MakeHeld(2053);
        session.HoldDupFailure(held);

        session.TryDequeuePendingNormalCast(2053, out _); // the anchor leaves (any path)
        var stale = session.TakeStaleHeldDupFailures();

        Assert.NotNull(stale);
        Assert.Same(held, Assert.Single(stale!));
        Assert.Equal(0, session.HeldDupFailureCount);
    }

    [Fact]
    public void TakeStale_MixedSpells_ReturnsOnlyOrphaned()
    {
        // Spell 2053's anchor died; spell 592's is still casting — only 2053's dup releases.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(592, started: true));
        var orphaned = MakeHeld(2053);
        session.HoldDupFailure(orphaned);
        session.HoldDupFailure(MakeHeld(592));

        var stale = session.TakeStaleHeldDupFailures();

        Assert.NotNull(stale);
        Assert.Same(orphaned, Assert.Single(stale!));
        Assert.Equal(1, session.HeldDupFailureCount);
        Assert.NotNull(session.TakeHeldDupFailures(592));
    }

    [Fact]
    public void TakeStale_UnstartedEntryDoesNotAnchor()
    {
        // Only a STARTED same-spell cast anchors a hold. A lone unstarted entry (a fresh
        // press racing in after the anchor terminated) must not keep the old dup captive.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2053));
        session.HoldDupFailure(MakeHeld(2053));

        var stale = session.TakeStaleHeldDupFailures();

        Assert.NotNull(stale);
        Assert.Single(stale!);
    }

    // ---- The specimen, end-to-end at store level ----

    [Fact]
    public void StonetavernSpecimen_HoldThenReleaseOnGoDequeue()
    {
        // 2026-08-14, both specimens: press A starts Lesser Heal (2053); dup press B bounces
        // (SpellInProgress) ~70-125ms before cast-end; the rejection and A's GO arrive in one
        // batch. Wiring contract: the CAST_FAILED handler dequeues B (preferStarted:false),
        // sees A still started (hold predicate true) → holds; the GO handler dequeues A and
        // takes the held batch AFTER forwarding the GO.
        var session = NewSession();
        var castA = MakeCast(2053, started: true);
        var dupB = MakeCast(2053);
        session.PendingNormalCasts.Enqueue(castA);
        session.PendingNormalCasts.Enqueue(dupB);

        // CAST_FAILED(dup B): consume the unstarted dup, spare the started cast (H7).
        Assert.True(session.TryDequeuePendingNormalCast(2053, out var consumed, preferStarted: false));
        Assert.Same(dupB, consumed);
        Assert.True(session.HasStartedPendingCastForSpell(2053)); // hold predicate
        session.HoldDupFailure(MakeHeld(2053));

        // SPELL_GO(cast A): dequeue the started cast, then release the held dup after it.
        Assert.True(session.TryDequeuePendingNormalCast(2053, out var completed));
        Assert.Same(castA, completed);
        var released = session.TakeHeldDupFailures(2053);
        Assert.NotNull(released);
        Assert.Single(released!);
    }
}
