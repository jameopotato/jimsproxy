using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

public class AuraStateEvictionTests
{
    private static WowGuid128 MakeUnit(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);

    // Validates the bug fix: SMSG_DESTROY_OBJECT must drop ALL per-target
    // aura state, not just ObjectCache + LastAuraCasterOnTarget. Pre-fix
    // the modern client surfaced stale buffs after a render roundtrip.
    [Fact]
    public void EvictUnitAuraState_PopulatedTarget_ClearsAllFourTables()
    {
        var state = GameSessionData.CreateForTesting();
        var target = MakeUnit(1);
        var caster = MakeUnit(99);

        state.StoreAuraDurationLeft(target, slot: 0, duration: 30000, currentTime: 1000);
        state.StoreAuraDurationLeft(target, slot: 1, duration: 60000, currentTime: 1000);
        state.StoreAuraDurationFull(target, slot: 0, duration: 30000);
        state.StoreAuraCaster(target, slot: 0, caster);

        Assert.True(state.UnitAuraDurationLeft.ContainsKey(target));
        Assert.True(state.UnitAuraDurationUpdateTime.ContainsKey(target));
        Assert.True(state.UnitAuraDurationFull.ContainsKey(target));
        Assert.True(state.UnitAuraCaster.ContainsKey(target));

        int evicted = state.EvictUnitAuraState(target);

        // Eviction count reflects the number of aura slots present in the
        // duration-left table (the canonical "how many auras did we know
        // about" signal — used in the object.destroy diagnostic).
        Assert.Equal(2, evicted);
        Assert.False(state.UnitAuraDurationLeft.ContainsKey(target));
        Assert.False(state.UnitAuraDurationUpdateTime.ContainsKey(target));
        Assert.False(state.UnitAuraDurationFull.ContainsKey(target));
        Assert.False(state.UnitAuraCaster.ContainsKey(target));
    }

    // Other targets' state is untouched — eviction is strictly per-guid.
    [Fact]
    public void EvictUnitAuraState_OnlyAffectsRequestedGuid()
    {
        var state = GameSessionData.CreateForTesting();
        var keep = MakeUnit(1);
        var evict = MakeUnit(2);

        state.StoreAuraDurationLeft(keep, slot: 0, duration: 30000, currentTime: 1000);
        state.StoreAuraDurationLeft(evict, slot: 0, duration: 30000, currentTime: 1000);
        state.StoreAuraCaster(keep, slot: 0, MakeUnit(99));
        state.StoreAuraCaster(evict, slot: 0, MakeUnit(99));

        state.EvictUnitAuraState(evict);

        Assert.True(state.UnitAuraDurationLeft.ContainsKey(keep));
        Assert.True(state.UnitAuraCaster.ContainsKey(keep));
        Assert.False(state.UnitAuraDurationLeft.ContainsKey(evict));
        Assert.False(state.UnitAuraCaster.ContainsKey(evict));
    }

    // No-op when the guid was never seen — must not throw.
    [Fact]
    public void EvictUnitAuraState_UnknownGuid_ReturnsZero()
    {
        var state = GameSessionData.CreateForTesting();
        int evicted = state.EvictUnitAuraState(MakeUnit(42));
        Assert.Equal(0, evicted);
    }
}
