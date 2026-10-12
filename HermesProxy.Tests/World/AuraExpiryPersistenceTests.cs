using System;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// Covers the wall-clock aura expiry map (UnitAuraExpiry) that restores buff
// timers after relog / unit destroy (PallyPower blessing timers restarting at
// full duration was the reported bug). JimsProxy (#513): entries now carry the
// start class and duration source that made the value known.
public class AuraExpiryPersistenceTests
{
    private static WowGuid128 MakeUnit(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);

    private const int BlessingOfMight = 25291;
    private const int FifteenMinutesMs = 15 * 60 * 1000;

    private static void Record(GameSessionData state, WowGuid128 unit, int spellId, int remainingMs) =>
        state.RecordAuraExpiry(unit, spellId, remainingMs, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, FifteenMinutesMs);

    private static int? Remaining(GameSessionData state, WowGuid128 unit, int spellId)
    {
        var record = state.TryGetAuraExpiry(unit, spellId);
        if (record == null)
            return null;
        return (int)(record.Value.ExpiryTick - Environment.TickCount64);
    }

    [Fact]
    public void RecordThenTryGet_ReturnsRemainingWithinRecordedWindow()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        Record(state, rogue, BlessingOfMight, FifteenMinutesMs);
        int? remaining = Remaining(state, rogue, BlessingOfMight);

        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value, 1, FifteenMinutesMs);
    }

    [Fact]
    public void TryGet_UnknownEntry_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();
        Assert.Null(state.TryGetAuraExpiry(MakeUnit(1), BlessingOfMight));
    }

    [Fact]
    public void RecordAuraExpiry_NonPositiveRemaining_IsIgnored()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        Record(state, rogue, BlessingOfMight, 0);
        Record(state, rogue, BlessingOfMight, -5000);

        Assert.Empty(state.UnitAuraExpiry);
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsNullAndRemovesIt()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.UnitAuraExpiry[(rogue, BlessingOfMight)] = new AuraExpiryRecord(
            Environment.TickCount64 - 1, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, FifteenMinutesMs, 0);

        Assert.Null(state.TryGetAuraExpiry(rogue, BlessingOfMight));
        Assert.Empty(state.UnitAuraExpiry);
    }

    [Fact]
    public void ClearAuraExpiry_RemovesOnlyThatSpellOnThatUnit()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);
        var mage = MakeUnit(2);

        Record(state, rogue, BlessingOfMight, FifteenMinutesMs);
        Record(state, rogue, 1126, FifteenMinutesMs);
        Record(state, mage, BlessingOfMight, FifteenMinutesMs);

        state.ClearAuraExpiry(rogue, BlessingOfMight);

        Assert.Null(state.TryGetAuraExpiry(rogue, BlessingOfMight));
        Assert.NotNull(state.TryGetAuraExpiry(rogue, 1126));
        Assert.NotNull(state.TryGetAuraExpiry(mage, BlessingOfMight));
    }

    [Fact]
    public void EvictUnitAuraState_DoesNotTouchExpiryMap()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        state.StoreAuraDurationLeft(rogue, slot: 0, duration: 30000, currentTime: 1000);
        Record(state, rogue, BlessingOfMight, FifteenMinutesMs);

        state.EvictUnitAuraState(rogue);

        Assert.False(state.UnitAuraDurationLeft.ContainsKey(rogue));
        Assert.NotNull(state.TryGetAuraExpiry(rogue, BlessingOfMight));
    }

    [Fact]
    public void RecordAuraExpiry_Rerecord_OverwritesWithNewExpiry()
    {
        var state = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);

        Record(state, rogue, BlessingOfMight, 1000);
        Record(state, rogue, BlessingOfMight, FifteenMinutesMs);

        int? remaining = Remaining(state, rogue, BlessingOfMight);
        Assert.NotNull(remaining);
        Assert.True(remaining!.Value > 1000, $"rebuff should extend the timer, got {remaining}");
    }

    [Fact]
    public void RecordAuraExpiry_OverCapacity_PrunesOnlyExpiredEntries()
    {
        var state = GameSessionData.CreateForTesting();
        var live = MakeUnit(999_999);

        for (ulong i = 0; i < 4200; i++)
            state.UnitAuraExpiry[(MakeUnit(i), (int)i)] = new AuraExpiryRecord(
                Environment.TickCount64 - 1, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 1000, 0);
        Record(state, live, BlessingOfMight, FifteenMinutesMs);

        Record(state, MakeUnit(5000), 1126, FifteenMinutesMs);

        Assert.NotNull(state.TryGetAuraExpiry(live, BlessingOfMight));
        Assert.True(state.UnitAuraExpiry.Count <= 3, $"expired entries should be pruned, count={state.UnitAuraExpiry.Count}");
    }

    // The relog carry-over: a recreated GameSessionData must keep the same expiry
    // map so buff timers on party members resume instead of restarting at full.
    [Fact]
    public void CreateNewGameSessionData_CarriesExpiryMapAcrossRelog()
    {
        var before = GameSessionData.CreateForTesting();
        var rogue = MakeUnit(1);
        Record(before, rogue, BlessingOfMight, FifteenMinutesMs);

        var after = GameSessionData.CreateForTesting(previous: before);

        Assert.NotNull(after.TryGetAuraExpiry(rogue, BlessingOfMight));
    }
}
