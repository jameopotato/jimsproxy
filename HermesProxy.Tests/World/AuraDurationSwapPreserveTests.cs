using System;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (res-sickness-swap-race): vanilla cores send SMSG_UPDATE_AURA_DURATION
// immediately at aura apply while the field update installing the aura batches to end
// of tick, so on a direct slot swap (Ghost → Resurrection Sickness at a spirit-healer
// res) the new occupant's duration arrives a few ms BEFORE the swap. The UpdateHandler
// swap-wipe guard must keep that fresh push instead of discarding it as the previous
// occupant's leftover — while still wiping genuinely stale durations (the original
// Rupture Y→X bleed the guard was added for).
public class AuraDurationSwapPreserveTests
{
    private static WowGuid128 Player(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Player, counter);

    private const byte Slot = 32; // first debuff slot — where Ghost and Res Sickness land

    [Fact]
    public void HasFreshAuraDurationPush_PushJustArrived_True()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);

        // The observed race gap in jimsproxy-20260802-182258.jsonl was 8 ms.
        Assert.True(state.HasFreshAuraDurationPush(guid, Slot, currentTime: 100_008));
    }

    [Fact]
    public void HasFreshAuraDurationPush_PushAtWindowEdge_True()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);

        Assert.True(state.HasFreshAuraDurationPush(
            guid, Slot, currentTime: 100_000 + GameSessionData.AuraDurationPushFreshnessMs));
    }

    [Fact]
    public void HasFreshAuraDurationPush_PushOlderThanWindow_False()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);

        Assert.False(state.HasFreshAuraDurationPush(
            guid, Slot, currentTime: 100_001 + GameSessionData.AuraDurationPushFreshnessMs));
    }

    // Pin for the original Rupture Y→X guard: emit-path duration stores
    // (StoreAuraDurationLeft/Full — finisher snapshot, expiry restore, refresh paths)
    // must NOT arm the preserve window. Only the two server push handlers do.
    [Fact]
    public void HasFreshAuraDurationPush_OnlyEmitPathStores_False()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationLeft(guid, Slot, duration: 16000, currentTime: 100_000);
        state.StoreAuraDurationFull(guid, Slot, duration: 16000);

        Assert.False(state.HasFreshAuraDurationPush(guid, Slot, currentTime: 100_008));
    }

    [Fact]
    public void HasFreshAuraDurationPush_DifferentSlot_False()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);

        Assert.False(state.HasFreshAuraDurationPush(guid, slot: 33, currentTime: 100_008));
    }

    // TickCount skew safety: a push timestamp "in the future" must not count as fresh.
    [Fact]
    public void HasFreshAuraDurationPush_ClockWentBackwards_False()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);

        Assert.False(state.HasFreshAuraDurationPush(guid, Slot, currentTime: 99_950));
    }

    // The guard's wipe branch and the slot-cleared path both go through
    // ClearAuraDuration — a wiped slot must not report a fresh push afterwards.
    [Fact]
    public void ClearAuraDuration_DropsPushTime_SubsequentCheckFalse()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);
        state.ClearAuraDuration(guid, Slot);

        Assert.False(state.HasFreshAuraDurationPush(guid, Slot, currentTime: 100_008));
    }

    [Fact]
    public void EvictUnitAuraState_DropsPushTime()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);

        state.StoreAuraDurationPushTime(guid, Slot, currentTime: 100_000);
        state.EvictUnitAuraState(guid);

        Assert.False(state.UnitAuraDurationPushTime.ContainsKey(guid));
        Assert.False(state.HasFreshAuraDurationPush(guid, Slot, currentTime: 100_008));
    }

    // State-level replay of the logged sequence: SMSG_UPDATE_AURA_DURATION stores
    // left/full/push, the swap arrives 8 ms later, the preserved duration is served
    // decayed by GetAuraDuration. Uses real TickCount because GetAuraDuration decays
    // against it internally.
    [Fact]
    public void ResSicknessSequence_FreshPushSurvivesSwapAndServesDuration()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Player(27032);
        int now = Environment.TickCount;

        // HandleUpdateAuraDuration's stores (60 s sickness, sub-20 character)
        state.StoreAuraDurationLeft(guid, Slot, duration: 60_000, currentTime: now);
        state.StoreAuraDurationFull(guid, Slot, duration: 60_000);
        state.StoreAuraDurationPushTime(guid, Slot, currentTime: now);

        // The UpdateHandler guard sees Ghost → 15007 and consults the push window
        Assert.True(state.HasFreshAuraDurationPush(guid, Slot, Environment.TickCount));

        // Because the wipe is skipped, the stored duration is served (decayed)
        state.GetAuraDuration(guid, Slot, out int left, out int full);
        Assert.Equal(60_000, full);
        Assert.InRange(left, 59_000, 60_000);
    }
}
