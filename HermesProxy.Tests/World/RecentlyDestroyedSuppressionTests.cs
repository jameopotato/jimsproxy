using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (out-of-range-ghost, issue #415): pins the stray-movement suppression contract for
/// just-destroyed / out-of-ranged units. Movement for a destroyed-and-not-yet-recreated guid is
/// NEVER legitimate (the modern client cannot render a unit before its CreateObject), so the
/// suppression mark must persist until the CreateObject clear — not expire on a timer. The old
/// 10s TTL leaked: units dwelling in the destroyed-but-still-broadcasting annulus past 10s
/// (lateral boundary-skimming players, patrolling NPCs, trailing pets) had their first post-TTL
/// movement packet relayed, re-ghosting them frozen at their last spot (289 at-edge encounters
/// across 67 field sessions).
/// </summary>
public class RecentlyDestroyedSuppressionTests
{
    private static GameSessionData NewState()
    {
        var state = WowGuidTestHelper.CreateMockGameSessionData();
        // The mock is built via GetUninitializedObject, which skips field initializers —
        // initialize the one private field this suite exercises.
        typeof(GameSessionData)
            .GetField("_recentlyDestroyedObjects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(state, new System.Collections.Concurrent.ConcurrentDictionary<WowGuid128, long>());
        return state;
    }

    private static WowGuid128 Creature(uint low) => WowGuid128.Create(HighGuidType703.Creature, 0, 1, low);

    // The leak regression: a mark far older than the old 10s TTL must STILL suppress.
    // This test FAILS against the TTL implementation (it evicted the mark and relayed).
    [Fact]
    public void Marked_SuppressedRegardlessOfAge()
    {
        var state = NewState();
        var guid = Creature(23594); // the Tanaris patrol NPC that leaked 6x in one evening

        state.MarkObjectRecentlyDestroyedAtTick(guid, System.Environment.TickCount64 - 3_600_000);

        Assert.True(state.WasObjectRecentlyDestroyed(guid, out long agoMs));
        Assert.True(agoMs >= 3_600_000);
    }

    // The normal lifecycle: CreateObject clears the mark and movement relays again.
    [Fact]
    public void Cleared_NotSuppressed()
    {
        var state = NewState();
        var guid = Creature(9208);

        state.MarkObjectRecentlyDestroyed(guid);
        Assert.True(state.WasObjectRecentlyDestroyed(guid, out _));

        state.ClearRecentlyDestroyedObject(guid);
        Assert.False(state.WasObjectRecentlyDestroyed(guid, out _));
    }

    [Fact]
    public void EmptyGuid_NeverMarked()
    {
        var state = NewState();

        state.MarkObjectRecentlyDestroyed(WowGuid128.Empty);

        Assert.False(state.WasObjectRecentlyDestroyed(WowGuid128.Empty, out _));
    }

    // Hygiene sweep: stale entries (units that never came back) are dropped once the dict is
    // large, so a long session can't grow it unbounded — but the sweep age is far above any
    // legitimate trailing-broadcast horizon and is NOT a relay permission.
    [Fact]
    public void Sweep_DropsStaleEntries_KeepsFresh()
    {
        var state = NewState();
        long staleTick = System.Environment.TickCount64 - 3_600_000; // 1h old >> sweep age
        for (uint i = 1; i <= 4200; i++)
            state.MarkObjectRecentlyDestroyedAtTick(Creature(i), staleTick);

        // A fresh mark over the threshold triggers the sweep.
        var fresh = Creature(999_999);
        state.MarkObjectRecentlyDestroyed(fresh);

        Assert.True(state.WasObjectRecentlyDestroyed(fresh, out _));
        Assert.True(state.RecentlyDestroyedCountForTest < 4200);
    }

    // Correctness beats memory: recent entries are never evicted just because the dict is big —
    // evicting a live mark would reopen the leak for exactly the crowded scenes that grow it.
    [Fact]
    public void Sweep_NeverEvictsRecentEntries()
    {
        var state = NewState();
        for (uint i = 1; i <= 5000; i++)
            state.MarkObjectRecentlyDestroyed(Creature(i));

        Assert.Equal(5000, state.RecentlyDestroyedCountForTest);
        Assert.True(state.WasObjectRecentlyDestroyed(Creature(1), out _));
        Assert.True(state.WasObjectRecentlyDestroyed(Creature(5000), out _));
    }
}
