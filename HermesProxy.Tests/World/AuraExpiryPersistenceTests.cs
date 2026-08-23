using System;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// Covers the wall-clock aura expiry map (UnitAuraExpiryTick) that restores buff
// timers after relog / unit destroy (PallyPower blessing timers restarting at
// full duration was the reported bug).
public class AuraExpiryPersistenceTests
{
    private static WowGuid128 MakeUnit(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);

    private const int BlessingOfMight = 25291;
    private const int FifteenMinutesMs = 15 * 60 * 1000;

    [Fact]
    public void RecordThenTryGet_ReturnsRemainingWithinRecordedWindow()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.RecordAuraExpiry(rogue, BlessingOfMight, FifteenMinutesMs);
        int? remaining = state.TryGetAuraRemainingMs(rogue, BlessingOfMight);

        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value, 1, FifteenMinutesMs);
    }

    [Fact]
    public void TryGet_UnknownEntry_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();
        Assert.Null(state.TryGetAuraRemainingMs(MakeUnit(1), BlessingOfMight));
    }

    [Fact]
    public void RecordAuraExpiry_NonPositiveRemaining_IsIgnored()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.RecordAuraExpiry(rogue, BlessingOfMight, 0);
        state.RecordAuraExpiry(rogue, BlessingOfMight, -5000);

        Assert.Empty(state.UnitAuraExpiryTick);
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsNullAndRemovesIt()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.UnitAuraExpiryTick[(rogue, BlessingOfMight)] = Environment.TickCount64 - 1;

        Assert.Null(state.TryGetAuraRemainingMs(rogue, BlessingOfMight));
        Assert.Empty(state.UnitAuraExpiryTick);
    }

    [Fact]
    public void ClearAuraExpiry_RemovesOnlyThatSpellOnThatUnit()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);
        var mage = MakeUnit(2);

        state.RecordAuraExpiry(rogue, BlessingOfMight, FifteenMinutesMs);
        state.RecordAuraExpiry(rogue, 1126, FifteenMinutesMs);
        state.RecordAuraExpiry(mage, BlessingOfMight, FifteenMinutesMs);

        state.ClearAuraExpiry(rogue, BlessingOfMight);

        Assert.Null(state.TryGetAuraRemainingMs(rogue, BlessingOfMight));
        Assert.NotNull(state.TryGetAuraRemainingMs(rogue, 1126));
        Assert.NotNull(state.TryGetAuraRemainingMs(mage, BlessingOfMight));
    }

    [Fact]
    public void EvictUnitAuraState_DoesNotTouchExpiryMap()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.StoreAuraDurationLeft(rogue, slot: 0, duration: 30000, currentTime: 1000);
        state.RecordAuraExpiry(rogue, BlessingOfMight, FifteenMinutesMs);

        state.EvictUnitAuraState(rogue);

        Assert.False(state.UnitAuraDurationLeft.ContainsKey(rogue));
        Assert.NotNull(state.TryGetAuraRemainingMs(rogue, BlessingOfMight));
    }

    [Fact]
    public void RecordAuraExpiry_Rerecord_OverwritesWithNewExpiry()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.RecordAuraExpiry(rogue, BlessingOfMight, 1000);
        state.RecordAuraExpiry(rogue, BlessingOfMight, FifteenMinutesMs);

        int? remaining = state.TryGetAuraRemainingMs(rogue, BlessingOfMight);
        Assert.NotNull(remaining);
        Assert.True(remaining!.Value > 1000, $"rebuff should extend the timer, got {remaining}");
    }

    [Fact]
    public void RecordAuraExpiry_OverCapacity_PrunesOnlyExpiredEntries()
    {
        var state = GameSessionData.CreateForTesting();
        var live = MakeUnit(999_999);

        for (ulong i = 0; i < 4200; i++)
            state.UnitAuraExpiryTick[(MakeUnit(i), (int)i)] = Environment.TickCount64 - 1;
        state.RecordAuraExpiry(live, BlessingOfMight, FifteenMinutesMs);

        state.RecordAuraExpiry(MakeUnit(5000), 1126, FifteenMinutesMs);

        Assert.NotNull(state.TryGetAuraRemainingMs(live, BlessingOfMight));
        Assert.True(state.UnitAuraExpiryTick.Count <= 3, $"expired entries should be pruned, count={state.UnitAuraExpiryTick.Count}");
    }

    // The relog carry-over: a recreated GameSessionData must keep the same expiry
    // map so buff timers on party members resume instead of restarting at full.
    [Fact]
    public void CreateNewGameSessionData_CarriesExpiryMapAcrossRelog()
    {
        var before = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);
        before.RecordAuraExpiry(rogue, BlessingOfMight, FifteenMinutesMs);

        var after = GameSessionData.CreateForTesting(previous: before);

        Assert.NotNull(after.TryGetAuraRemainingMs(rogue, BlessingOfMight));
    }
}
