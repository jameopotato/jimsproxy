using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (engineering-malfunction jam): TryEvictForwardedItemUseCast clears a forwarded-but-
// unstarted ITEM-use cast that a server-side substitute spell preempted — e.g. Goblin Mortar (13237)
// orphaned when the malfunction substitutes Malfunction Explosion (13261), whose CAST_FAILED arrives
// status != 2 and is discarded, so the mortar's forwarded cast never starts/fails and jams
// HasForwardedPendingCast() forever. These pin the guards: item-use only, no self-eviction on the
// item's own spell, and started/normal casts are left intact.
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

        // trigger == the item-use spell: an item's own status-0 ack must not self-evict
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
}
