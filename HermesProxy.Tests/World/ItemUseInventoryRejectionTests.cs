using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#442): Kronos rejects a CMSG_USE_ITEM with SMSG_INVENTORY_CHANGE_FAILURE carrying
// EMPTY item GUIDs (observed live 2026-07-29: a duplicate use straddling its predecessor's
// SPELL_GO, result 23/ItemNotFound, both GUIDs zero on the wire). The GUID-keyed dequeue in
// HandleInventoryChangeFailure can't match those, and the orphaned entry is unreapable:
// unstarted (jams HasForwardedPendingCast -> every on-GCD press silently parked until relog or
// map change), IsOffGcd since #345 (spared by ClearNonStartedNormalCasts), never
// SPELL_FAILURE-peeked (no WatchdogDeadlineMs), and a permanent HasInFlightNormalCastForSpell
// match (that item unusable for the session).
//
// TryDequeueOldestUnstartedItemCast is the fallback pair for those anonymous rejections: FIFO
// pairing with the oldest unresolved item-use entry. These pin its selection rules and that
// evicting the orphan clears BOTH gates.
public class ItemUseInventoryRejectionTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest ItemUse(uint spellId, ulong itemLow, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        IsOffGcd = true,                        // HandleUseItem tags item-use casts off-GCD (#345)
        ItemGUID = new WowGuid128(itemLow, 2),  // non-empty => item-use cast
    };

    private static ClientCastRequest SpellCast(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        // ItemGUID left empty => normal CMSG_CAST_SPELL
    };

    [Fact]
    public void DequeuesTheOrphanedItemEntry()
    {
        // The live repro shape: one unstarted item entry left by an anonymous rejection.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));  // Mana Ruby press B

        Assert.True(s.TryDequeueOldestUnstartedItemCast(out var cast));
        Assert.Equal(10058u, cast!.SpellId);
        Assert.Empty(s.PendingNormalCasts);
    }

    [Fact]
    public void EvictionClearsBothGates()
    {
        // The whole point: the lockout gate AND the dead-item gate both clear.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));

        Assert.True(s.HasForwardedPendingCast());               // jammed before
        Assert.True(s.HasInFlightNormalCastForSpell(10058));    // item dead before

        Assert.True(s.TryDequeueOldestUnstartedItemCast(out _));

        Assert.False(s.HasForwardedPendingCast());              // casting flows again
        Assert.False(s.HasInFlightNormalCastForSpell(10058));   // item usable again
    }

    [Fact]
    public void StartedItemCast_IsNeverTaken()
    {
        // A started cast belongs to the server — its own GO/CAST_FAILED resolves it.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, itemLow: 7, started: true));

        Assert.False(s.TryDequeueOldestUnstartedItemCast(out _));
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void SpellCasts_AreNeverTaken()
    {
        // An anonymous INVENTORY failure is an item-path rejection; it must never consume a
        // normal CMSG_CAST_SPELL entry (those pair with SPELL_FAILURE / CAST_FAILED).
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(10181));           // forwarded Frostbolt
        s.PendingNormalCasts.Enqueue(SpellCast(133, started: true));

        Assert.False(s.TryDequeueOldestUnstartedItemCast(out _));
        Assert.Equal(2, s.PendingNormalCasts.Count);
    }

    [Fact]
    public void OldestItemEntryWins_FifoOrder()
    {
        // Two different items in flight (observed shape: robe + gem forwarded 1ms apart).
        // FIFO pairs the rejection with the older one; the newer survives untouched.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, itemLow: 7));   // ROBE, older
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));  // Ruby, newer

        Assert.True(s.TryDequeueOldestUnstartedItemCast(out var cast));
        Assert.Equal(18385u, cast!.SpellId);
        Assert.True(s.HasInFlightNormalCastForSpell(10058));        // Ruby still tracked
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void SurroundingEntries_KeepTheirOrder()
    {
        // The drain-rebuild must not scramble the FIFO for everything it keeps.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(116, started: true));
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));   // the orphan
        s.PendingNormalCasts.Enqueue(SpellCast(133));

        Assert.True(s.TryDequeueOldestUnstartedItemCast(out var cast));
        Assert.Equal(10058u, cast!.SpellId);

        Assert.True(s.PendingNormalCasts.TryDequeue(out var first));
        Assert.True(s.PendingNormalCasts.TryDequeue(out var second));
        Assert.Equal(116u, first!.SpellId);
        Assert.Equal(133u, second!.SpellId);
    }

    [Fact]
    public void EmptyQueue_IsSafe()
    {
        var s = NewSession();
        Assert.False(s.TryDequeueOldestUnstartedItemCast(out var cast));
        Assert.Null(cast);
    }

    [Fact]
    public void GuidDequeue_EmptyGuid_MatchesNothing()
    {
        // Issue A (review): spell entries leave ItemGUID at default (struct), so without the
        // empty-GUID guard TryDequeueItemCast(empty) would pair `empty == empty` with the
        // unstarted Frostbolt — a spurious visible CastFailed for a healthy cast, and the real
        // orphan surviving behind the handler's else-if.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(10181));             // forwarded Frostbolt
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));  // the gem orphan

        Assert.False(s.TryDequeueItemCast(WowGuid128.Empty, out var cast));
        Assert.Null(cast);
        Assert.Equal(2, s.PendingNormalCasts.Count);
    }

    [Fact]
    public void RaidShape_FallbackTakesTheItem_NotTheSpell()
    {
        // The composed raid shape end-to-end at the state layer: guid dequeue declines the
        // anonymous rejection, the FIFO fallback then evicts the ITEM orphan and the healthy
        // spell entry survives untouched.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(10181));             // forwarded Frostbolt, older
        s.PendingNormalCasts.Enqueue(ItemUse(10058, itemLow: 11));  // the gem orphan, newer

        Assert.False(s.TryDequeueItemCast(WowGuid128.Empty, out _));
        Assert.True(s.TryDequeueOldestUnstartedItemCast(out var cast));
        Assert.Equal(10058u, cast!.SpellId);

        Assert.True(s.PendingNormalCasts.TryDequeue(out var survivor));
        Assert.Equal(10181u, survivor!.SpellId);
        Assert.Empty(s.PendingNormalCasts);
    }
}
