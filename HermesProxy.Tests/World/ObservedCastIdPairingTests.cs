using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#484/#485 observed-castid-pairing): the observed-caster CastID tracker was a
// single slot per (caster, spell) — a rapid same-spell recast overwrote the predecessor's
// CastID, so the predecessor's late cancel broadcast (field: FAILED_OTHER arriving 0-554ms
// AFTER the successor's SPELL_START — heal-snipe / chain-cast spam, 9 corpus instances)
// popped the SUCCESSOR's ID and the interrupt synthesis killed the new bar at 0ms. These
// drive the GameSessionData pairing methods directly — no clock, fully synchronous.
//
// Model: the server runs at most ONE live cast per unit, so tracked entries are
// [superseded predecessor?, live cast]. Terminators pair oldest-first (the predecessor's
// echo always arrives before any event of the successor's outcome); GO pairs with the
// newest (only the live cast can complete) and closes the predecessor's echo window.
public class ObservedCastIdPairingTests
{
    static ObservedCastIdPairingTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private const uint Frostbolt = 10181;
    private const uint Skinning = 10768;

    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();
    private static WowGuid128 Caster(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Player, 0, 12345, counter);
    private static WowGuid128 CastId(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Cast, 0, 99999, counter);

    // ---- The #484 defect shape -------------------------------------------------------

    [Fact]
    public void Terminator_WithSupersededPredecessor_ConsumesPredecessorNotLiveCast()
    {
        // START(A) → recast START(B) → predecessor A's cancel echo arrives.
        // It must pop A (the cast it terminates), and report NOT-live so the
        // interrupt-kit synthesis can't dismiss B's on-screen bar.
        var session = NewSession();
        var caster = Caster(14143);
        var a = CastId(1);
        var b = CastId(2);
        session.EnqueueObservedStartCastId(caster, Frostbolt, a);
        session.EnqueueObservedStartCastId(caster, Frostbolt, b);

        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out var popped, out var pairedLive));
        Assert.Equal(a, popped);
        Assert.False(pairedLive);

        // B is still tracked live: its GO pairs with B.
        Assert.True(session.TryPairObservedGoCastId(caster, Frostbolt, out var goId));
        Assert.Equal(b, goId);
    }

    [Fact]
    public void Terminator_WithSingleLiveCast_PairsLive()
    {
        // The bread-and-butter legit interrupt: one live cast, its terminator pops it
        // and reports live — the interrupt synthesis fires exactly as before.
        var session = NewSession();
        var caster = Caster(1);
        var a = CastId(1);
        session.EnqueueObservedStartCastId(caster, Skinning, a);

        Assert.True(session.TryPairObservedTerminatorCastId(caster, Skinning, out var popped, out var pairedLive));
        Assert.Equal(a, popped);
        Assert.True(pairedLive);
        Assert.False(session.HasLiveObservedCast(caster, Skinning));
    }

    [Fact]
    public void SecondTerminator_AfterPredecessorConsumed_PairsTheLiveCast()
    {
        // Double-echo residual (documented): after the predecessor's echo consumed A,
        // a second terminator within the window pairs with B and IS live — behaviorally
        // identical to today's single-slot outcome, never worse.
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(2));

        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out _, out _));
        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out var second, out var pairedLive));
        Assert.Equal(CastId(2), second);
        Assert.True(pairedLive);
    }

    // ---- GO pairing ------------------------------------------------------------------

    [Fact]
    public void Go_PairsNewest_AndPurgesSupersededPredecessor()
    {
        // If the predecessor's echo never arrives before B completes, B's GO closes the
        // echo window: the stale A entry is purged with it and can never eat a later
        // cast's terminator.
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(2));

        Assert.True(session.TryPairObservedGoCastId(caster, Frostbolt, out var goId));
        Assert.Equal(CastId(2), goId);
        Assert.False(session.HasLiveObservedCast(caster, Frostbolt));
        Assert.False(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out _, out _));
    }

    [Fact]
    public void Go_WithNothingTracked_ReportsMiss()
    {
        var session = NewSession();
        Assert.False(session.TryPairObservedGoCastId(Caster(1), Frostbolt, out _));
    }

    // ---- The #485 killed-then-fired recovery -----------------------------------------

    [Fact]
    public void Go_AfterTerminatorConsumedTheCast_RecoversTerminatedCastId()
    {
        // Kronos killed-then-fired: START(A) → non-terminal FAILED_OTHER pops A → the
        // cast completes anyway. The GO must reference A, not a fresh ID the client
        // never saw start.
        var session = NewSession();
        var caster = Caster(14143);
        var a = CastId(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, a);
        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out _, out _));

        Assert.False(session.TryPairObservedGoCastId(caster, Frostbolt, out _));
        Assert.True(session.TryRecoverTerminatedObservedCastId(caster, Frostbolt, out var recovered));
        Assert.Equal(a, recovered);
        // Single-shot: consumed.
        Assert.False(session.TryRecoverTerminatedObservedCastId(caster, Frostbolt, out _));
    }

    [Fact]
    public void TerminatedStash_IsInvalidatedByNextStart()
    {
        // A new same-key START opens a new cast — a later GO belongs to IT, never to the
        // stashed terminated predecessor.
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out _, out _));

        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(2));
        Assert.True(session.TryPairObservedGoCastId(caster, Frostbolt, out var goId));
        Assert.Equal(CastId(2), goId);
        Assert.False(session.TryRecoverTerminatedObservedCastId(caster, Frostbolt, out _));
    }

    [Fact]
    public void TerminatedStash_IsInvalidatedByGo()
    {
        // [A(pred), B(live)]: echo consumes A (stashed), B's GO completes normally — the
        // stale stash of A must not leak into a later same-key instant GO.
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(2));
        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out _, out _)); // pops A, stashes A
        Assert.True(session.TryPairObservedGoCastId(caster, Frostbolt, out _));                // B completes

        Assert.False(session.TryRecoverTerminatedObservedCastId(caster, Frostbolt, out _));
    }

    // ---- Hygiene ---------------------------------------------------------------------

    [Fact]
    public void ThirdStart_DropsEntriesOlderThanDirectPredecessor()
    {
        // Anything older than the direct predecessor has had a full cast cycle for its
        // echo — it is dropped so a stale zombie can never eat a later terminator.
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(2));
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(3));

        Assert.True(session.TryPairObservedTerminatorCastId(caster, Frostbolt, out var oldest, out var pairedLive));
        Assert.Equal(CastId(2), oldest);
        Assert.False(pairedLive);
        Assert.True(session.TryPairObservedGoCastId(caster, Frostbolt, out var goId));
        Assert.Equal(CastId(3), goId);
    }

    [Fact]
    public void Keys_AreIndependentAcrossCastersAndSpells()
    {
        var session = NewSession();
        var mage = Caster(1);
        var priest = Caster(2);
        session.EnqueueObservedStartCastId(mage, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(priest, Frostbolt, CastId(2));
        session.EnqueueObservedStartCastId(mage, Skinning, CastId(3));

        Assert.True(session.TryPairObservedTerminatorCastId(mage, Frostbolt, out var popped, out var pairedLive));
        Assert.Equal(CastId(1), popped);
        Assert.True(pairedLive);
        Assert.True(session.HasLiveObservedCast(priest, Frostbolt));
        Assert.True(session.HasLiveObservedCast(mage, Skinning));
    }

    [Fact]
    public void ResetInFlightCastState_ClearsLiveAndTerminatedTracking()
    {
        var session = NewSession();
        var caster = Caster(1);
        session.EnqueueObservedStartCastId(caster, Frostbolt, CastId(1));
        session.EnqueueObservedStartCastId(caster, Skinning, CastId(2));
        Assert.True(session.TryPairObservedTerminatorCastId(caster, Skinning, out _, out _)); // stash Skinning

        var (_, _, otherCount) = session.ResetInFlightCastState();
        Assert.Equal(1, otherCount);
        Assert.False(session.HasLiveObservedCast(caster, Frostbolt));
        Assert.False(session.TryRecoverTerminatedObservedCastId(caster, Skinning, out _));
    }
}
