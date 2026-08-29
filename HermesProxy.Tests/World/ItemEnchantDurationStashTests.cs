using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (temp-enchant-0s-after-relogin): Kronos can send
// SMSG_ITEM_ENCHANT_TIME_UPDATE — the only carrier of a temp enchant's remaining
// time that the client's weapon-buff countdown reads — BEFORE the item's create
// block (2026-08-14 and 2026-08-28 logs). The client discards the push for a guid
// it has not constructed, and the create's enchantment duration field is a stale
// save-time snapshot no client generation uses for the timer, so the buff renders
// as a permanently flashing "0s". The handler stashes the pre-create push in
// GameSessionData (decay-tracked); the item-create translation arms a re-emit,
// which HandleUpdateObject sends AFTER the create's packet has gone out — the
// proxy-layer equivalent of the servers' "must be after add to map" ordering.
public class ItemEnchantDurationStashTests
{
    private static WowGuid128 Item(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Item, counter);

    private const uint TempSlot = 1;             // vanilla TEMP_ENCHANTMENT_SLOT — sharpening stones, oils
    private const uint StoneSeconds = 1800;      // 30 min sharpening stone

    // --- stash + consume ---

    [Fact]
    public void ConsumePendingItemEnchantDurations_JustStored_ReturnsFullMs()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(83718230); // the MH weapon low from jimsproxy-20260814-090718.jsonl

        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 100_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000);

        Assert.NotNull(consumed);
        var entry = Assert.Single(consumed);
        Assert.Equal(TempSlot, entry.LegacySlot);
        Assert.Equal(StoneSeconds * 1000, entry.DurationMs);
    }

    // The observed login gap (push at .213, create at .216) is milliseconds, but a
    // bank-parked enchanted item's create only arrives at bank-open — the stashed
    // value must decay by the time it spent waiting, not replay at full.
    [Fact]
    public void ConsumePendingItemEnchantDurations_TimePassedSinceStore_DecaysRemaining()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 100_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000 + 25_000);

        var entry = Assert.Single(consumed!);
        Assert.Equal(StoneSeconds * 1000 - 25_000, entry.DurationMs);
    }

    // Consume-once: the create that eats the stash must leave nothing behind for a
    // later create to inject stale time from.
    [Fact]
    public void ConsumePendingItemEnchantDurations_SecondCall_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 100_000);
        state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000);

        Assert.Null(state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_001));
    }

    [Fact]
    public void ConsumePendingItemEnchantDurations_DifferentGuid_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();

        state.StorePendingItemEnchantDuration(Item(1), TempSlot, StoneSeconds, nowTick: 100_000);

        Assert.Null(state.ConsumePendingItemEnchantDurations(Item(2), nowTick: 100_000));
    }

    // A push that fully expired while stashed must not resurrect as a 0/negative
    // duration — dropping it reproduces the server's own removal that is coming.
    [Fact]
    public void ConsumePendingItemEnchantDurations_ExpiredWhileStashed_DropsEntry()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, durationSeconds: 10, nowTick: 100_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000 + 10_000);

        Assert.True(consumed == null || consumed.Count == 0);
    }

    // TickCount skew safety (same rule as HasFreshAuraDurationPush): a receipt tick
    // "in the future" must not decay the push, only time genuinely elapsed.
    [Fact]
    public void ConsumePendingItemEnchantDurations_ClockWentBackwards_NoDecay()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 100_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 99_000);

        var entry = Assert.Single(consumed!);
        Assert.Equal(StoneSeconds * 1000, entry.DurationMs);
    }

    // A refreshed push (re-applied stone before the create — same slot) replaces the
    // older value instead of duplicating the slot.
    [Fact]
    public void StorePendingItemEnchantDuration_SameSlotTwice_LatestWins()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, durationSeconds: 300, nowTick: 100_000);
        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 101_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 101_000);

        var entry = Assert.Single(consumed!);
        Assert.Equal(StoneSeconds * 1000, entry.DurationMs);
    }

    // Both weapons stoned: two slots on... two guids, but also two slots on one guid
    // (Perm + Temp both timed is impossible for Perm, but the stash must not conflate
    // distinct slots regardless).
    [Fact]
    public void ConsumePendingItemEnchantDurations_TwoSlotsOneItem_ReturnsBoth()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, 0, durationSeconds: 600, nowTick: 100_000);
        state.StorePendingItemEnchantDuration(guid, TempSlot, StoneSeconds, nowTick: 100_000);
        var consumed = state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000);

        Assert.Equal(2, consumed!.Count);
    }

    // Zero-duration pushes are removal signals, not countdowns — never stashed.
    [Fact]
    public void StorePendingItemEnchantDuration_ZeroSeconds_NotStashed()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(1);

        state.StorePendingItemEnchantDuration(guid, TempSlot, durationSeconds: 0, nowTick: 100_000);

        Assert.Null(state.ConsumePendingItemEnchantDurations(guid, nowTick: 100_000));
    }

    // --- post-create re-emit ---

    // 2026-08-28 model correction (.pkt-proven): the client's weapon-buff timer is
    // driven ONLY by SMSG_ITEM_ENCHANT_TIME_UPDATE — the create block's enchantment
    // duration field is a stale save-time snapshot that no client generation reads
    // for the countdown (every mangos-lineage core ships the stale field AND sends
    // the packet from SendInitialPacketsAfterAddToMap: "must be after add to map").
    // So the consume site arms a re-emit, and HandleUpdateObject flushes it to the
    // client AFTER the update packet carrying the item's create has gone out —
    // replicating canon server ordering at the proxy layer.

    [Fact]
    public void ArmEnchantTimeReemit_ThenTake_ReturnsSecondsFromMs()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Item(61878924); // the MH weapon low from jimsproxy-20260828-104459.jsonl

        state.ArmEnchantTimeReemit(guid, TempSlot, durationMs: 1_304_000);
        var reemits = state.TakeEnchantTimeReemits();

        Assert.NotNull(reemits);
        var entry = Assert.Single(reemits);
        Assert.Equal(guid, entry.ItemGuid);
        Assert.Equal(TempSlot, entry.ModernSlot);
        Assert.Equal(1304u, entry.DurationSeconds);
    }

    // Canon wire unit is whole seconds (leftduration / 1000) — truncate, don't round.
    [Fact]
    public void ArmEnchantTimeReemit_SubSecondRemainder_TruncatesToWholeSeconds()
    {
        var state = GameSessionData.CreateForTesting();

        state.ArmEnchantTimeReemit(Item(1), TempSlot, durationMs: 1_999);
        var reemits = state.TakeEnchantTimeReemits();

        Assert.Equal(1u, Assert.Single(reemits!).DurationSeconds);
    }

    // A sub-second remainder would truncate to 0 — the exact broken display value,
    // and 0 doubles as the client's removal signal. Never emit it; the enchant is
    // about to expire server-side anyway.
    [Fact]
    public void ArmEnchantTimeReemit_UnderOneSecond_NotArmed()
    {
        var state = GameSessionData.CreateForTesting();

        state.ArmEnchantTimeReemit(Item(1), TempSlot, durationMs: 999);

        Assert.Null(state.TakeEnchantTimeReemits());
    }

    // Take is grab-and-clear: the flush at the end of one HandleUpdateObject must
    // not replay into the next update packet.
    [Fact]
    public void TakeEnchantTimeReemits_SecondTake_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();

        state.ArmEnchantTimeReemit(Item(1), TempSlot, durationMs: 60_000);
        state.TakeEnchantTimeReemits();

        Assert.Null(state.TakeEnchantTimeReemits());
    }

    [Fact]
    public void TakeEnchantTimeReemits_NothingArmed_ReturnsNull()
    {
        var state = GameSessionData.CreateForTesting();

        Assert.Null(state.TakeEnchantTimeReemits());
    }

    // Both weapons stoned in one login update: both arms survive to the flush.
    [Fact]
    public void ArmEnchantTimeReemit_MultipleItems_AllReturnedInOrder()
    {
        var state = GameSessionData.CreateForTesting();

        state.ArmEnchantTimeReemit(Item(1), TempSlot, durationMs: 600_000);
        state.ArmEnchantTimeReemit(Item(2), TempSlot, durationMs: 300_000);
        var reemits = state.TakeEnchantTimeReemits();

        Assert.Equal(2, reemits!.Count);
        Assert.Equal(Item(1), reemits[0].ItemGuid);
        Assert.Equal(600u, reemits[0].DurationSeconds);
        Assert.Equal(Item(2), reemits[1].ItemGuid);
        Assert.Equal(300u, reemits[1].DurationSeconds);
    }

    // --- slot translation (vanilla server pinned by TestEnvironmentInitializer) ---

    // Perm/Temp share indices across versions; vanilla's Prop block (3..6) sits at
    // 8..11 in the modern layout. The old passthrough only ever worked because timed
    // enchants live in Temp=1.
    [Theory]
    [InlineData(0u, 0u)]   // Perm
    [InlineData(1u, 1u)]   // Temp
    [InlineData(3u, 8u)]   // Prop0
    [InlineData(4u, 9u)]   // Prop1
    [InlineData(5u, 10u)]  // Prop2
    [InlineData(6u, 11u)]  // Prop3
    public void TranslateEnchantmentSlotToModern_VanillaSlots_MapToClassicLayout(uint legacy, uint modern)
    {
        Assert.Equal(modern, WorldClient.TranslateEnchantmentSlotToModern(legacy));
    }
}
