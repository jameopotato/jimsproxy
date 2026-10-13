using System.Collections.Generic;
using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#513): the provenance rules that decide whether an aura timer goes on the wire.
/// A known value carries the Duration flag with both values on every emit; an unknown one
/// carries neither and records nothing. See <see cref="AuraTimerProvenance"/>.
/// </summary>
public class AuraTimerProvenanceTests
{
    private const int Minute = 60_000;

    // Rule 3: the duration source.
    [Fact]
    public void DurationSource_FixedWhenTheSpellIdFixesIt()
    {
        Assert.Equal(AuraDurationSource.Fixed, AuraTimerProvenance.DurationSourceFor(true, false, false, false));
    }

    [Fact]
    public void DurationSource_NoneWithoutATableEntry()
    {
        Assert.Equal(AuraDurationSource.None, AuraTimerProvenance.DurationSourceFor(false, false, false, false));
    }

    [Fact]
    public void DurationSource_LocalForTheLocalPlayersTalentSpellOrFinisher()
    {
        Assert.Equal(AuraDurationSource.Local, AuraTimerProvenance.DurationSourceFor(true, true, true, false));
        Assert.Equal(AuraDurationSource.Local, AuraTimerProvenance.DurationSourceFor(true, true, false, true));
    }

    [Fact]
    public void DurationSource_VariableForAnotherCastersTalentSpellOrFinisher()
    {
        Assert.Equal(AuraDurationSource.Variable, AuraTimerProvenance.DurationSourceFor(true, false, true, false));
        Assert.Equal(AuraDurationSource.Variable, AuraTimerProvenance.DurationSourceFor(true, false, false, true));
    }

    // Known = a known start class AND a fixed or local duration.
    [Fact]
    public void IsKnown_FalseForAnUnknownStart()
    {
        Assert.False(AuraTimerProvenance.IsKnown(AuraStartClass.Unknown, AuraDurationSource.Fixed, 30 * Minute, 0));
    }

    [Fact]
    public void IsKnown_FalseForAVariableOrMissingDuration()
    {
        Assert.False(AuraTimerProvenance.IsKnown(AuraStartClass.ObservedGo, AuraDurationSource.Variable, 30 * Minute, 0));
        Assert.False(AuraTimerProvenance.IsKnown(AuraStartClass.ObservedGo, AuraDurationSource.None, 0, 0));
    }

    [Fact]
    public void IsKnown_TrueForEveryObservedClassWithAFixedDuration()
    {
        foreach (var cls in new[] { AuraStartClass.Self, AuraStartClass.ObservedGo, AuraStartClass.ObservedSlot, AuraStartClass.Restored })
            Assert.True(AuraTimerProvenance.IsKnown(cls, AuraDurationSource.Fixed, 10_000, 0));
    }

    // The 2% rule: a one-second start bound is served for buffs of 50 s or more only.
    [Fact]
    public void IsKnown_StatsSeenServedOnlyWhenTheBoundIsSmallAgainstTheDuration()
    {
        int bound = AuraTimerProvenance.StatsStartBoundMs;
        Assert.True(AuraTimerProvenance.IsKnown(AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, 30 * Minute, bound));
        Assert.True(AuraTimerProvenance.IsKnown(AuraStartClass.StatsRelist, AuraDurationSource.Fixed, 50_000, bound));
        Assert.False(AuraTimerProvenance.IsKnown(AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, 40_000, bound));
        Assert.False(AuraTimerProvenance.IsKnown(AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, 10_000, bound));
    }

    // Rule 1: expired but present.
    [Fact]
    public void RemainingToServe_ReturnsTheDecayedRemaining()
    {
        var record = new AuraExpiryRecord(100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 30 * Minute, 0);
        Assert.Equal(40_000, AuraTimerProvenance.RemainingToServe(record, 60_000));
    }

    [Fact]
    public void RemainingToServe_NullWhenRunOutOrNearlySo()
    {
        var record = new AuraExpiryRecord(100_000, AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, 30 * Minute, 1000);
        Assert.Null(AuraTimerProvenance.RemainingToServe(record, 100_000));
        Assert.Null(AuraTimerProvenance.RemainingToServe(record, 100_000 - AuraTimerProvenance.ExpiredFloorMs + 1));
        Assert.Null(AuraTimerProvenance.RemainingToServe(record, 150_000));
        Assert.NotNull(AuraTimerProvenance.RemainingToServe(record, 100_000 - AuraTimerProvenance.ExpiredFloorMs));
    }

    // Rule 2: bulk listings record nothing.
    [Fact]
    public void BulkListing_StatusFlagPartial()
    {
        Assert.True(AuraTimerProvenance.IsBulkListing(true, 0, new List<uint>(), null, null));
    }

    [Fact]
    public void BulkListing_TwoOrMoreRestates()
    {
        Assert.True(AuraTimerProvenance.IsBulkListing(false, 2, new List<uint> { 1243, 1126 }, null, null));
        Assert.False(AuraTimerProvenance.IsBulkListing(false, 1, new List<uint> { 1243 }, null, null));
    }

    [Fact]
    public void BulkListing_RestatingTheWholeLastFullListWithinTheWindow()
    {
        var lastFull = new HashSet<uint> { 1243 };
        Assert.True(AuraTimerProvenance.IsBulkListing(false, 1, new List<uint> { 1243 }, lastFull, 2000));
        Assert.True(AuraTimerProvenance.IsBulkListing(false, 1, new List<uint> { 1243 }, lastFull, 2800));
        // Outside the window the same single restate is a genuine relist.
        Assert.False(AuraTimerProvenance.IsBulkListing(false, 1, new List<uint> { 1243 }, lastFull, 3001));
        // A different spell than the full list carried is not a resend of it.
        Assert.False(AuraTimerProvenance.IsBulkListing(false, 1, new List<uint> { 1126 }, lastFull, 1000));
    }

    [Fact]
    public void BulkListing_AFreshAddIsNotBulk()
    {
        Assert.False(AuraTimerProvenance.IsBulkListing(false, 0, new List<uint>(), new HashSet<uint> { 1243 }, 500));
    }

}

/// <summary>
/// The stats start classes for one out-of-view party stats delta (rules 2, 3 and 4), decided
/// against the member's slot table as it was before the delta.
/// </summary>
public class PartyDeltaClassificationTests
{
    private const uint Fortitude = 1243;        // fixed 30 min
    private const uint MarkOfTheWild = 1126;    // fixed 30 min
    private const uint BattleShout = 6673;      // talent-modified by the caster: variable
    private const uint Sprint = 2983;           // 15 s: too short for a 1 s bound
    private const int ThirtyMinutes = 30 * 60_000;

    private static uint Identity(uint id) => id;
    private static int FixedMs(uint id) => id switch
    {
        Fortitude => ThirtyMinutes,
        MarkOfTheWild => ThirtyMinutes,
        BattleShout => 120_000,
        Sprint => 15_000,
        _ => 0,
    };
    private static bool IsTalent(uint id) => id == BattleShout;
    private static bool IsFinisher(uint id) => false;

    private static uint[] Table(params (int slot, uint id)[] entries)
    {
        var table = PartyAuraSlotTable.NewTable();
        foreach (var (slot, id) in entries)
            table[slot] = id;
        return table;
    }

    private static List<PartyAuraSlotTable.SlotChange> Delta(params (int slot, uint id)[] entries)
    {
        var list = new List<PartyAuraSlotTable.SlotChange>();
        foreach (var (slot, id) in entries)
            list.Add(new PartyAuraSlotTable.SlotChange(slot, id));
        return list;
    }

    private static AuraTimerProvenance.PartyDeltaClassification Classify(
        uint[] before, List<PartyAuraSlotTable.SlotChange> delta, bool status = false,
        HashSet<uint>? lastFull = null, long? sinceFull = null)
        => AuraTimerProvenance.ClassifyPartyDelta(before, delta, status, lastFull, sinceFull, Identity, FixedMs, IsTalent, IsFinisher);

    // A spell newly listed in a non-bulk delta is STATS_FIRST_SEEN.
    [Fact]
    public void FreshAdd_IsStampedFirstSeen()
    {
        var result = Classify(Table((0, Fortitude)), Delta((1, MarkOfTheWild)));

        Assert.False(result.Bulk);
        var stamp = Assert.Single(result.Stamps);
        Assert.Equal((1, MarkOfTheWild, AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, ThirtyMinutes),
            (stamp.Slot, stamp.ModernSpellId, stamp.StartClass, stamp.Source, stamp.FullMs));
    }

    // The same spell relisted alone, outside the bulk window, is STATS_RELIST.
    [Fact]
    public void SingleRelistOutsideTheWindow_IsStampedRelist()
    {
        var result = Classify(Table((0, Fortitude), (1, MarkOfTheWild)), Delta((0, Fortitude)),
            lastFull: new HashSet<uint> { Fortitude, MarkOfTheWild }, sinceFull: 20_000);

        var stamp = Assert.Single(result.Stamps);
        Assert.Equal(AuraStartClass.StatsRelist, stamp.StartClass);
    }

    // Rule 2: a status-flag partial records nothing, even with a fresh add in it.
    [Fact]
    public void StatusFlagPartial_RecordsNothing()
    {
        var result = Classify(Table((0, Fortitude)), Delta((1, MarkOfTheWild)), status: true);

        Assert.True(result.Bulk);
        Assert.Empty(result.Stamps);
    }

    // Rule 2: two or more restates is a bulk resend.
    [Fact]
    public void TwoRestates_RecordNothing()
    {
        var result = Classify(Table((0, Fortitude), (1, MarkOfTheWild)), Delta((0, Fortitude), (1, MarkOfTheWild)));

        Assert.True(result.Bulk);
        Assert.Equal(2, result.RestatedCount);
    }

    // Rule 2: a partial restating the whole last full list within 3 s of it is a resend
    // (2.0 s and 2.8 s), while 3.1 s later the same restate is a genuine relist.
    [Fact]
    public void AllRestatingPartialAfterAFull_WithinThreeSecondsIsBulk()
    {
        var before = Table((0, Fortitude));
        var lastFull = new HashSet<uint> { Fortitude };

        Assert.True(Classify(before, Delta((0, Fortitude)), lastFull: lastFull, sinceFull: 2000).Bulk);
        Assert.True(Classify(before, Delta((0, Fortitude)), lastFull: lastFull, sinceFull: 2800).Bulk);
        var later = Classify(before, Delta((0, Fortitude)), lastFull: lastFull, sinceFull: 3100);
        Assert.False(later.Bulk);
        Assert.Equal(AuraStartClass.StatsRelist, Assert.Single(later.Stamps).StartClass);
    }

    // Rule 4: a cleared slot drops the spell's record unless it sits in another slot too.
    [Fact]
    public void Removal_ClearsTheSpell_UnlessItSitsElsewhere()
    {
        var once = Classify(Table((0, Fortitude), (1, MarkOfTheWild)), Delta((0, 0)));
        var cleared = Assert.Single(once.Cleared);
        Assert.Equal((0, Fortitude, false), (cleared.Slot, cleared.LegacySpellId, cleared.SpellStillElsewhere));

        var twice = Classify(Table((0, Fortitude), (1, Fortitude)), Delta((0, 0)));
        Assert.True(Assert.Single(twice.Cleared).SpellStillElsewhere);
    }

    // A slot swap clears the old spell and stamps the new one as first seen.
    [Fact]
    public void SlotSwap_ClearsOldAndStampsNew()
    {
        var result = Classify(Table((0, Fortitude)), Delta((0, MarkOfTheWild)));

        Assert.Equal(Fortitude, Assert.Single(result.Cleared).LegacySpellId);
        var stamp = Assert.Single(result.Stamps);
        Assert.Equal((MarkOfTheWild, AuraStartClass.StatsFirstSeen), (stamp.ModernSpellId, stamp.StartClass));
    }

    // Rule 3: a talent-modified spell from an unknown caster is not knowable; a fresh one
    // must also drop any older record of that spell on the member.
    [Fact]
    public void VariableDurationSpell_IsNotStamped_AndDropsAnOlderRecord()
    {
        var result = Classify(Table(), Delta((0, BattleShout)));

        Assert.Empty(result.Stamps);
        Assert.Equal(BattleShout, Assert.Single(result.UnknownFreshModernIds));
    }

    // The 2% rule: a 15 s buff is too short for a 1 s start bound.
    [Fact]
    public void ShortBuff_IsNotStamped()
    {
        var result = Classify(Table(), Delta((0, Sprint)));

        Assert.Empty(result.Stamps);
        Assert.Equal(Sprint, Assert.Single(result.UnknownFreshModernIds));
    }

    // A zero-only delta with no previous occupant changes nothing.
    [Fact]
    public void ClearOfAnEmptySlot_IsANoOp()
    {
        var result = Classify(Table(), Delta((5, 0)));

        Assert.Empty(result.Cleared);
        Assert.Empty(result.Stamps);
        Assert.False(result.Bulk);
    }
}

/// <summary>The expiry map: known values only, class-tagged, dropped on the offline edge.</summary>
public class AuraExpiryMapTests
{
    static AuraExpiryMapTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static readonly WowGuid128 Member = WowGuid128.Create(HighGuidType703.Player, 1001);
    private static readonly WowGuid128 Other = WowGuid128.Create(HighGuidType703.Player, 1002);

    [Fact]
    public void Record_KeepsTheClassAndSource()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 1_500_000, AuraStartClass.StatsFirstSeen, AuraDurationSource.Fixed, 1_800_000, 1000);

        var record = state.TryGetAuraExpiry(Member, 1243);

        Assert.NotNull(record);
        Assert.Equal(AuraStartClass.StatsFirstSeen, record.Value.StartClass);
        Assert.Equal(AuraDurationSource.Fixed, record.Value.Source);
        Assert.Equal(1_800_000, record.Value.FullMs);
        Assert.Equal(1000, record.Value.BoundMs);
    }

    [Fact]
    public void Record_RefusesAnUnknownStart()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 1_500_000, AuraStartClass.Unknown, AuraDurationSource.Fixed, 1_800_000);

        Assert.Null(state.TryGetAuraExpiry(Member, 1243));
    }

    [Fact]
    public void Record_FullIsNeverBelowRemaining()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 10_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 5_000);

        Assert.Equal(10_000, state.TryGetAuraExpiry(Member, 1243)!.Value.FullMs);
    }

    [Fact]
    public void OfflineEdge_DropsOnlyThatMembersEntries()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);
        state.RecordAuraExpiry(Member, 1126, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);
        state.RecordAuraExpiry(Other, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);

        int dropped = state.DropAuraExpiriesForUnit(Member);

        Assert.Equal(2, dropped);
        Assert.Null(state.TryGetAuraExpiry(Member, 1243));
        Assert.Null(state.TryGetAuraExpiry(Member, 1126));
        Assert.NotNull(state.TryGetAuraExpiry(Other, 1243));
    }

    [Fact]
    public void Clear_RemovesOneEntry()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);

        state.ClearAuraExpiry(Member, 1243);

        Assert.Null(state.TryGetAuraExpiry(Member, 1243));
    }

    // The record carries the caster, and a re-record without one keeps the one on file.
    [Fact]
    public void Record_KeepsTheCasterAcrossARerecordWithoutOne()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000, 0, Other);

        state.RecordAuraExpiry(Member, 1243, 90_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);

        Assert.Equal(Other, state.TryGetAuraExpiry(Member, 1243)!.Value.Caster);
    }

    // Rule 5 as verified in the field: the member's buffs pause while they are offline, so the
    // return shifts their records by the offline interval instead of dropping them.
    [Fact]
    public void OfflineMark_ThenReturn_ShiftsThatMembersEntries()
    {
        var state = GameSessionData.CreateForTesting();
        state.RecordAuraExpiry(Member, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);
        state.RecordAuraExpiry(Other, 1243, 100_000, AuraStartClass.ObservedGo, AuraDurationSource.Fixed, 100_000);
        long before = state.TryGetAuraExpiry(Member, 1243)!.Value.ExpiryTick;
        long otherBefore = state.TryGetAuraExpiry(Other, 1243)!.Value.ExpiryTick;

        state.MarkPartyMemberOffline(Member);
        // Simulate 7 s offline without sleeping.
        state.PartyMemberOfflineSince[Member] -= 7_000;
        var result = state.ShiftAuraExpiriesIfReturning(Member);

        Assert.NotNull(result);
        Assert.InRange(result.Value.OfflineMs, 7_000, 7_500);
        Assert.Equal(1, result.Value.Shifted);
        Assert.InRange(state.TryGetAuraExpiry(Member, 1243)!.Value.ExpiryTick - before, 7_000, 7_500);
        Assert.Equal(otherBefore, state.TryGetAuraExpiry(Other, 1243)!.Value.ExpiryTick);
        // A second return without a mark does nothing.
        Assert.Null(state.ShiftAuraExpiriesIfReturning(Member));
    }

    [Fact]
    public void OfflineMark_IsNotReset_ByARepeatedOfflineStatus()
    {
        var state = GameSessionData.CreateForTesting();
        state.MarkPartyMemberOffline(Member);
        long first = state.PartyMemberOfflineSince[Member];

        state.PartyMemberOfflineSince[Member] = first - 5_000;
        state.MarkPartyMemberOffline(Member);

        Assert.Equal(first - 5_000, state.PartyMemberOfflineSince[Member]);
    }
}
