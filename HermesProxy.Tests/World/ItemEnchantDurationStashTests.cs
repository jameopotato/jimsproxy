using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (temp-enchant-0s-after-relogin): at login vanilla cores send
// SMSG_ITEM_ENCHANT_TIME_UPDATE — the only carrier of a temp enchant's remaining
// time — BEFORE the item's create block (2026-08-14 logs: both logins pre-create,
// one pre-login-verify). The 1.14 client discards updates for guids it has not
// constructed, and the create's enchantment duration field is zero, so the buff
// renders as a permanently flashing "0s". The handler stashes the push in
// GameSessionData and the item-create translation consumes it into the create
// block's duration field, decayed by the time it spent stashed.
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

    // --- injection gate ---

    // Inject only where the create shows a live enchant whose duration the server
    // left empty (vanilla's login shape). A real duration in the field wins.
    [Theory]
    [InlineData(2504, null, true)]   // enchant ID, no duration field → the bug shape, inject
    [InlineData(2504, 0u, true)]     // explicit zero duration → inject
    [InlineData(2504, 1_800_000u, false)] // server provided a real duration → keep it
    [InlineData(0, null, false)]     // no enchant in the slot → nothing to time
    public void ShouldInjectEnchantDuration_GateShapes(int enchantId, uint? duration, bool expected)
    {
        var enchantment = new ItemEnchantment { ID = enchantId, Duration = duration };

        Assert.Equal(expected, GameSessionData.ShouldInjectEnchantDuration(enchantment));
    }

    [Fact]
    public void ShouldInjectEnchantDuration_NullEntry_False()
    {
        Assert.False(GameSessionData.ShouldInjectEnchantDuration(null));
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
