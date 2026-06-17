using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (engineering-malfunction jam): TryEvictForwardedItemUseCast clears a forwarded-but-
// unstarted ITEM-use cast that a server-side substitute spell preempted — e.g. Goblin Mortar (13237)
// orphaned when the malfunction substitutes Malfunction Explosion (13261), whose CAST_FAILED arrives
// status != 2 and is discarded, so the mortar's forwarded cast never starts/fails and jams
// HasForwardedPendingCast() forever. Eviction is gated to a known substitute->device map: only a known
// substitute trigger (13261) fires it, and only its mapped device cast (13237) is evicted. These pin
// the guards: known-substitute trigger only, mapped-device victim only, item-use only, started/normal
// casts left intact — so an unrelated combat status-0 (e.g. Defensive State 5302) never evicts a
// healthy in-flight item, and a different in-flight item is never mistaken for the malfunctioning one.
public class ItemUseMalfunctionEvictionTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest ItemUse(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        ItemGUID = new WowGuid128(1, 2), // non-empty => item-use cast
    };

    private static ClientCastRequest SpellCast(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        // ItemGUID left default (empty) => normal CMSG_CAST_SPELL
    };

    [Fact]
    public void EvictsForwardedItemUse_PreemptedBySubstitute()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(13237)); // Goblin Mortar, forwarded-unstarted

        Assert.True(s.TryEvictForwardedItemUseCast(13261, out var orphan)); // 13261 = Malfunction Explosion
        Assert.Equal(13237u, orphan!.SpellId);
        Assert.Empty(s.PendingNormalCasts);
        Assert.False(s.HasForwardedPendingCast()); // jam condition cleared
    }

    [Fact]
    public void DoesNotEvict_NormalSpellCast()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(116)); // Frostbolt, forwarded — NOT item-use

        Assert.False(s.TryEvictForwardedItemUseCast(13261, out _));
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void DoesNotEvict_StartedItemUse()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(13237, started: true));

        Assert.False(s.TryEvictForwardedItemUseCast(13261, out _));
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void DoesNotEvict_OnItemsOwnSpellId()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(13237));

        // trigger == the item-use spell's own id: not a known malfunction substitute, so no
        // eviction (an item's own status-0 ack must never self-evict)
        Assert.False(s.TryEvictForwardedItemUseCast(13237, out _));
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void EvictsOnlyTheOrphan_LeavesOtherCasts()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(SpellCast(116, started: true)); // started Frostbolt
        s.PendingNormalCasts.Enqueue(ItemUse(13237));                // the orphan
        s.PendingNormalCasts.Enqueue(SpellCast(133));                // forwarded Fireball (not item-use)

        Assert.True(s.TryEvictForwardedItemUseCast(13261, out var orphan));
        Assert.Equal(13237u, orphan!.SpellId);
        Assert.Equal(2, s.PendingNormalCasts.Count); // the two non-item casts remain
    }

    [Fact]
    public void DoesNotEvict_OnUnrelatedTrigger()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(13237)); // Goblin Mortar, forwarded-unstarted

        // 5302 = Defensive State: an incidental status-0 CAST_FAILED seen during normal combat,
        // NOT a malfunction substitute. The in-flight item must be left alone.
        Assert.False(s.TryEvictForwardedItemUseCast(5302, out _));
        Assert.Single(s.PendingNormalCasts);
    }

    [Fact]
    public void DoesNotEvict_WhenOrphanIsNotTheMappedDevice()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUse(17534)); // a potion, forwarded-unstarted (not the Mortar)

        // Real malfunction trigger (13261) but the only in-flight item-use is a potion, not the
        // Goblin Mortar (13237) it preempts — so nothing is evicted.
        Assert.False(s.TryEvictForwardedItemUseCast(13261, out _));
        Assert.Single(s.PendingNormalCasts);
    }
}
