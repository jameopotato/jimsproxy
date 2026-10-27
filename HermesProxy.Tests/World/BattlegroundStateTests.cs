using System.Collections.Generic;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#504): IsInBattleground's decision, see <see cref="GameSessionData.DecideInBattleground"/>.
/// After a disconnect and rejoin inside a battleground the queue-slot table is empty and the server
/// does not re-send the status, so the old queue-only check returned false on the battleground map.
/// The fallback tests below (no entry, non-matching entry) fail against that behaviour. The exit
/// window (a slot that just ended while still on the map) must NOT fall back: the server restores
/// the home group before the teleport out and its group list arrives on the battleground map.
/// </summary>
public class BattlegroundStateTests
{
    const uint WarsongMap = 489;
    const uint WarsongBgId = 2;
    const uint ArathiMap = 529;
    const uint NoExit = 0;

    private static Dictionary<uint, uint>.ValueCollection Queues(params uint[] values)
    {
        var dict = new Dictionary<uint, uint>();
        for (uint slot = 0; slot < values.Length; slot++)
            dict[slot + 1] = values[slot];
        return dict.Values;
    }

    [Fact]
    public void NotABattlegroundMap_IsFalse()
    {
        bool inBg = GameSessionData.DecideInBattleground(0u, bgId: 0, Queues(0u), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.False(inBg);
        Assert.False(usedFallback);
    }

    [Fact]
    public void NoCurrentMap_IsFalse()
    {
        bool inBg = GameSessionData.DecideInBattleground(null, WarsongBgId, Queues(WarsongMap), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.False(inBg);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Vanilla_MatchingQueueEntry_IsTrue_WithoutFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(ArathiMap, WarsongMap), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Vanilla_NoQueueEntries_IsTrue_WithFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    [Fact]
    public void NullQueueCollection_IsTrue_WithFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, null, legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    // A cleared slot stores 0 and another queue stores its own map; neither matches the current map.
    [Fact]
    public void Vanilla_NonMatchingQueueEntries_IsTrue_WithFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(0u, ArathiMap), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    // Vanilla compares the stored value with the map id, not the battleground id.
    [Fact]
    public void Vanilla_StoredBattlegroundId_DoesNotMatch()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(WarsongBgId), legacyIsVanilla: true, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    // TBC and later store the battleground id.
    [Fact]
    public void Tbc_MatchingBattlegroundId_IsTrue_WithoutFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(WarsongBgId), legacyIsVanilla: false, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Tbc_StoredMapId_DoesNotMatch()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(WarsongMap), legacyIsVanilla: false, NoExit, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    // The exit window: the slot just ended (stored 0) while still on the map, so the home group
    // list that follows must not be filed as the battleground raid.
    [Fact]
    public void ExitWindow_OnTheExitedMap_IsFalse_NoFallback()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(0u), legacyIsVanilla: true, exitedMapId: WarsongMap, out bool usedFallback);

        Assert.False(inBg);
        Assert.False(usedFallback);
    }

    // A matching stored entry still wins inside the exit window (a re-queue that landed the same map).
    [Fact]
    public void ExitWindow_MatchingEntry_StillTrue()
    {
        bool inBg = GameSessionData.DecideInBattleground(WarsongMap, WarsongBgId, Queues(WarsongMap), legacyIsVanilla: true, exitedMapId: WarsongMap, out bool usedFallback);

        Assert.True(inBg);
        Assert.False(usedFallback);
    }

    // The marker names one map; a different battleground map falls back as usual.
    [Fact]
    public void ExitWindow_OtherMap_StillFallsBack()
    {
        bool inBg = GameSessionData.DecideInBattleground(ArathiMap, 3, Queues(), legacyIsVanilla: true, exitedMapId: WarsongMap, out bool usedFallback);

        Assert.True(inBg);
        Assert.True(usedFallback);
    }

    // The session-level helpers: the marker is set on exit and cleared by any status that names a
    // map; a slot is "seen" once any status stored a value for it, even 0.
    [Fact]
    public void Session_ExitMarkerAndSeenSlots()
    {
        var state = GameSessionData.CreateForTesting();

        state.MarkBattlegroundExited(WarsongMap);
        state.ClearBattlegroundExited();
        state.StoreBattleFieldQueueType(1, 0);

        Assert.True(state.HasBattleFieldQueueType(1));
        Assert.False(state.HasBattleFieldQueueType(2));
    }
}
