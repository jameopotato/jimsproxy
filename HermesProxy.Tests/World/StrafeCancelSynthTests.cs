using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (strafe cancel-gap): pure-layer tests for the strafe cancel synth.
// The 1.14 client sends CMSG_CANCEL_CAST atomically with forward/back/jump
// movement starts but never on strafe, so MovementHandler synthesizes the
// missing legacy cancel for casts that a strafe start NEWLY marks as
// movement-cancelled. Two pure decisions are covered here:
//   1. the false→true transition accumulator on MarkStartedCastsMovementCancelled
//      (once per cast, never for a cast an earlier movement key already marked);
//   2. ResolveStrafeCancelSpellId — the 1.12 movement-interrupt gate keyed by the
//      legacy-effective spell id (ranged shots and unknown ids return 0 = no synth).
// The wire emission itself needs a live WorldSocket and isn't reachable from this
// harness (same limitation as PlayerForwardedCastIdsTests).
public class StrafeCancelSynthTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId, uint castTimeMs, bool hasStarted,
        uint legacySpellId = 0)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            LegacySpellId = legacySpellId,
            Timestamp = Environment.TickCount,
            HasStarted = hasStarted,
            StartedCastTimeMs = castTimeMs,
        };
    }

    // ---- transition accumulator ----

    [Fact]
    public void MarkStartedCasts_Accumulator_CollectsNewlyMarkedCastTimeCast()
    {
        var session = NewSession();
        var cast = MakeCast(10181, castTimeMs: 3000, hasStarted: true); // Frostbolt r11 mid-cast
        session.PendingNormalCasts.Enqueue(cast);
        var newlyMarked = new List<ClientCastRequest>();

        int marked = session.MarkStartedCastsMovementCancelled(
            Environment.TickCount64 + 2500, newlyMarked);

        Assert.Equal(1, marked);
        Assert.Single(newlyMarked);
        Assert.Same(cast, newlyMarked[0]);
    }

    [Fact]
    public void MarkStartedCasts_Accumulator_ExcludesAlreadyMarkedOnRemark()
    {
        // First movement key marks the cast (forward — its cancel came from the
        // client). A strafe arriving within the same cast must NOT re-collect it:
        // the synth fires only on the false→true transition.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(10181, castTimeMs: 3000, hasStarted: true));
        session.MarkStartedCastsMovementCancelled(Environment.TickCount64 + 2500);

        var newlyMarked = new List<ClientCastRequest>();
        int marked = session.MarkStartedCastsMovementCancelled(
            Environment.TickCount64 + 2500, newlyMarked);

        Assert.Equal(1, marked); // re-mark still counts for diagnostics
        Assert.Empty(newlyMarked); // but the transition already happened
    }

    [Fact]
    public void MarkStartedCasts_Accumulator_SkipsInstantNotStartedAndChanneled()
    {
        var prevChanneled = GameData.ChanneledSpells;
        try
        {
            const uint channeledSpellId = 2575; // Mining (vanilla 1.12, real channel)
            GameData.ChanneledSpells = new HashSet<uint> { channeledSpellId }.ToFrozenSet();

            var session = NewSession();
            var started = MakeCast(10181, castTimeMs: 3000, hasStarted: true);
            session.PendingNormalCasts.Enqueue(started);
            session.PendingNormalCasts.Enqueue(MakeCast(1953, castTimeMs: 0, hasStarted: true)); // instant
            session.PendingNormalCasts.Enqueue(MakeCast(133, castTimeMs: 3500, hasStarted: false)); // pre-START
            session.PendingNormalCasts.Enqueue(MakeCast(channeledSpellId, castTimeMs: 5000, hasStarted: true));
            var newlyMarked = new List<ClientCastRequest>();

            int marked = session.MarkStartedCastsMovementCancelled(
                Environment.TickCount64 + 2500, newlyMarked);

            Assert.Equal(1, marked);
            Assert.Single(newlyMarked);
            Assert.Same(started, newlyMarked[0]);
        }
        finally
        {
            GameData.ChanneledSpells = prevChanneled;
        }
    }

    // ---- movement-interrupt gate ----

    [Fact]
    public void ResolveStrafeCancelSpellId_MovementInterruptible_ReturnsSpellId()
    {
        var prev = GameData.MovementInterruptibleSpells;
        try
        {
            GameData.MovementInterruptibleSpells = new HashSet<uint> { 10181 }.ToFrozenSet();

            uint resolved = WorldSocket.ResolveStrafeCancelSpellId(
                MakeCast(10181, castTimeMs: 3000, hasStarted: true));

            Assert.Equal(10181u, resolved);
        }
        finally
        {
            GameData.MovementInterruptibleSpells = prev;
        }
    }

    [Fact]
    public void ResolveStrafeCancelSpellId_NotMovementInterruptible_ReturnsZero()
    {
        // Arcane Shot r1: cast-time per DBC (ranged wind-up) but NO movement
        // interrupt bit — usable while moving in vanilla. Synthesizing a deliberate
        // cancel would kill a shot the server would never interrupt.
        var prev = GameData.MovementInterruptibleSpells;
        try
        {
            GameData.MovementInterruptibleSpells = new HashSet<uint> { 10181 }.ToFrozenSet();

            uint resolved = WorldSocket.ResolveStrafeCancelSpellId(
                MakeCast(3044, castTimeMs: 500, hasStarted: true));

            Assert.Equal(0u, resolved);
        }
        finally
        {
            GameData.MovementInterruptibleSpells = prev;
        }
    }

    [Fact]
    public void ResolveStrafeCancelSpellId_RenumberedSpell_KeysOnLegacyId()
    {
        // A renumbered (SoM-style) cast: the wire cancel and the 1.12 data are both
        // keyed by the LEGACY id, never the modern one.
        var prev = GameData.MovementInterruptibleSpells;
        try
        {
            var cast = MakeCast(999999, castTimeMs: 3000, hasStarted: true, legacySpellId: 10181);

            GameData.MovementInterruptibleSpells = new HashSet<uint> { 10181 }.ToFrozenSet();
            Assert.Equal(10181u, WorldSocket.ResolveStrafeCancelSpellId(cast));

            GameData.MovementInterruptibleSpells = new HashSet<uint> { 999999 }.ToFrozenSet();
            Assert.Equal(0u, WorldSocket.ResolveStrafeCancelSpellId(cast));
        }
        finally
        {
            GameData.MovementInterruptibleSpells = prev;
        }
    }

    [Fact]
    public void ResolveStrafeCancelSpellId_EmptySet_ReturnsZero()
    {
        // Missing SpellMovementInterrupt CSV leaves the set empty — the synth must
        // be fully inert (safe fallback to server-side movement detection).
        var prev = GameData.MovementInterruptibleSpells;
        try
        {
            GameData.MovementInterruptibleSpells = FrozenSet<uint>.Empty;

            uint resolved = WorldSocket.ResolveStrafeCancelSpellId(
                MakeCast(10181, castTimeMs: 3000, hasStarted: true));

            Assert.Equal(0u, resolved);
        }
        finally
        {
            GameData.MovementInterruptibleSpells = prev;
        }
    }
}
