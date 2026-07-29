using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#442): the 1.14 client double-sends a keypress, so two pending entries can exist for
// one press (H7). SMSG_SPELL_GO consumes the STARTED one by design (preferStarted:true), leaving
// the unstarted twin for the server's duplicate-rejection CAST_FAILED to clean up.
//
// That second half never arrives for an ITEM use — the legacy server doesn't answer the duplicate
// CMSG_USE_ITEM in a form the proxy acts on. The orphan is then unreapable: unstarted (jams
// HasForwardedPendingCast -> every on-GCD press silently parked until relog), IsOffGcd (spared by
// ClearNonStartedNormalCasts), never SPELL_FAILURE-peeked (no WatchdogDeadlineMs), and a permanent
// HasInFlightNormalCastForSpell match (that item unusable for the session).
//
// Seen live on Ragnaros: the started entry's GO left a twin behind and the player lost all casting
// for 96 seconds until restarting. These pin the collapse and its blast radius.
public class ItemUseDuplicateCollapseTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest ItemUse(uint spellId, bool started) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        IsOffGcd = true,                  // HandleUseItem tags item-use casts off-GCD (#345)
        ItemGUID = new WowGuid128(1, 2),  // non-empty => item-use cast
    };

    private static ClientCastRequest SpellCast(uint spellId, bool started) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        // ItemGUID left empty => normal CMSG_CAST_SPELL
    };

    [Fact]
    public void UnstartedItemDuplicate_IsDropped()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: true));   // consumed by its GO
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: false));  // the orphaned twin

        var dropped = s.DropUnstartedItemUseDuplicates(18385);

        Assert.Single(dropped);
        Assert.False(dropped[0].HasStarted);
    }

    [Fact]
    public void AfterCollapse_NeitherGateStillTrips()
    {
        // The whole point: both the lockout AND the dead-item residual clear.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: true));
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: false));

        s.DropUnstartedItemUseDuplicates(18385);
        // simulate the GO consuming the started entry
        Assert.True(s.TryDequeuePendingNormalCast(18385, out _));

        Assert.False(s.HasForwardedPendingCast());              // on-GCD casting not parked
        Assert.False(s.HasInFlightNormalCastForSpell(18385));   // the item is usable again
        Assert.Empty(s.PendingNormalCasts);
    }

    [Fact]
    public void StartedItemCast_IsNeverDropped()
    {
        // Only the unstarted twin goes; the server owns started casts.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: true));

        var dropped = s.DropUnstartedItemUseDuplicates(18385);

        Assert.Empty(dropped);
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void UnstartedSpellDuplicate_IsNotDropped()
    {
        // H7's pairing for spells must stay intact — the duplicate's NOT_READY / SpellInProgress
        // CAST_FAILED still needs an entry to consume.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(133, started: true));
        s.PendingNormalCasts.Enqueue(SpellCast(133, started: false));

        var dropped = s.DropUnstartedItemUseDuplicates(133);

        Assert.Empty(dropped);
        Assert.Equal(2, s.PendingNormalCasts.Count);
    }

    [Fact]
    public void OtherSpellsAreUntouched()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: false));  // the target
        s.PendingNormalCasts.Enqueue(ItemUse(10058, started: false));  // a different item, in flight
        s.PendingNormalCasts.Enqueue(SpellCast(133, started: true));   // a live spell

        var dropped = s.DropUnstartedItemUseDuplicates(18385);

        Assert.Single(dropped);
        Assert.Equal(18385u, dropped[0].SpellId);
        Assert.Equal(2, s.PendingNormalCasts.Count);
        Assert.True(s.HasInFlightNormalCastForSpell(10058));  // the other item still tracked
    }

    [Fact]
    public void LegacyRenumberedItemSpell_IsMatched()
    {
        // SoM-renumbered on-use ids (e.g. Diamond Flask 17626 -> 363880): the GO carries the legacy
        // id, so the collapse must match on LegacySpellId too, exactly like the dequeue does.
        var s = NewSession();
        var twin = ItemUse(363880, started: false);
        twin.LegacySpellId = 17626;
        s.PendingNormalCasts.Enqueue(twin);

        var dropped = s.DropUnstartedItemUseDuplicates(17626);

        Assert.Single(dropped);
        Assert.Empty(s.PendingNormalCasts);
    }

    [Fact]
    public void WithoutCollapse_TheTwinJamsBothGates_BugState()
    {
        // The pre-fix state, pinned so the delta is demonstrable rather than asserted: the GO
        // consumes the started entry and the twin is left behind, at which point every on-GCD
        // press parks (HasForwardedPendingCast) and the item is unusable (HasInFlightNormalCast).
        // This is what the Ragnaros log shows. Compare with AfterCollapse_NeitherGateStillTrips.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: true));
        s.PendingNormalCasts.Enqueue(ItemUse(18385, started: false));

        Assert.True(s.TryDequeuePendingNormalCast(18385, out var consumed));  // the GO
        Assert.True(consumed!.HasStarted);                                   // took the started one

        Assert.Single(s.PendingNormalCasts);                    // the twin survives
        Assert.True(s.HasForwardedPendingCast());               // -> all on-GCD casting parked
        Assert.True(s.HasInFlightNormalCastForSpell(18385));    // -> that item dead for the session
    }

    [Fact]
    public void EmptyQueue_IsSafe()
    {
        var s = NewSession();
        Assert.Empty(s.DropUnstartedItemUseDuplicates(18385));
    }
}
