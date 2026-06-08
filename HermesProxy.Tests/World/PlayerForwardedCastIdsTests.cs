using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (cast-go-castid-recovery): PlayerForwardedCastIds records the client-facing
// CastID forwarded for the local player's SMSG_SPELL_START so HandleSpellGo can re-stamp
// the GO with the SAME id when no PendingNormalCast / melee / auto-repeat entry matches.
// This is the deterministic fix for the orphaned-cast family (stuck casting animation +
// looping cast sound): the 1.14 client pairs START<->GO by CastID, and a mismatch leaves
// the cast un-terminated. It hits server-initiated player casts with no CMSG (GO loot
// subspells e.g. Whipper Root 15343, weapon/trinket procs) and casts whose pending entry
// was consumed by an interleaved duplicate CAST_FAILED before the GO (Blade Flurry,
// re-clicked gathers).
//
// The full handler wiring (store at forward-time, recall as the last-resort GO fallback)
// needs a live WorldSocket and isn't reachable from this harness. These tests pin the two
// GameState invariants the fix relies on: the store/recall round-trip is exact and
// single-shot, and a world transfer clears the map so a stale CastID can't leak across a
// zone/realm change onto an unrelated later cast.
public class PlayerForwardedCastIdsTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    [Fact]
    public void StoredCastId_RoundTripsExactly_AndIsConsumedOnRecall()
    {
        var session = NewSession();
        var forwardedCastId = new WowGuid128(0x123456789ABCDEF0, 0x0FEDCBA987654321);

        session.PlayerForwardedCastIds[13877] = forwardedCastId; // Blade Flurry

        // HandleSpellGo recalls via TryRemove: the GO gets the exact CastID the START forwarded.
        Assert.True(session.PlayerForwardedCastIds.TryRemove(13877, out var recovered));
        Assert.Equal(forwardedCastId, recovered);

        // Single-shot: a second GO (or a later proc GO of the same spell) must NOT reuse it.
        Assert.False(session.PlayerForwardedCastIds.TryRemove(13877, out _));
    }

    [Fact]
    public void ResetInFlightCastState_ClearsForwardedCastIds()
    {
        var session = NewSession();
        session.PlayerForwardedCastIds[15343] = new WowGuid128(1, 2); // Create Whipper Root Tubers
        session.PlayerForwardedCastIds[13877] = new WowGuid128(3, 4); // Blade Flurry

        // World transfer (BG entry, instance change, realm swap) must not carry CastIDs over.
        session.ResetInFlightCastState();

        Assert.Empty(session.PlayerForwardedCastIds);
    }
}
