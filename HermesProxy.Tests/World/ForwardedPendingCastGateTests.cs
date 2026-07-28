using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#442): HasForwardedPendingCast() gates the ForceHoldCast park at
// Server/SpellHandler.cs:619, which silently swallows EVERY on-GCD press (no SpellPrepare,
// DontReport on the displaced one) for as long as it returns true. It is not keyed by spell,
// arms no timer, and off-GCD entries — every item-use cast since #345 — are exempt from the
// ClearNonStartedNormalCasts sweep and never receive a WatchdogDeadlineMs. So a leaked
// off-GCD entry used to mean: all casting dead until relog. Observed live on Ragnaros
// (Mana Ruby + Robe of the Archmage, then 96s of spell.held_pending).
//
// These pin the fix: off-GCD/item entries no longer satisfy the gate, while real on-GCD
// forwarded casts still do — including when a leaked off-GCD entry sits alongside one.
public class ForwardedPendingCastGateTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest OnGcdCast(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        IsOffGcd = false,
    };

    private static ClientCastRequest OffGcdCast(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        IsOffGcd = true,
    };

    private static ClientCastRequest ItemUseCast(uint spellId, bool started = false) => new()
    {
        SpellId = spellId,
        HasStarted = started,
        IsOffGcd = true,                  // HandleUseItem tags item-use casts off-GCD (#345)
        ItemGUID = new WowGuid128(1, 2),  // non-empty => item-use cast
    };

    [Fact]
    public void EmptyQueue_NoForwardedCast()
    {
        var s = NewSession();
        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void ForwardedUnstartedOnGcdCast_IsCounted()
    {
        // The behaviour the gate exists for: a normal cast is in flight to the legacy server
        // and hasn't been confirmed yet, so the next press is parked to preserve ordering.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(OnGcdCast(133)); // Fireball, forwarded-unstarted

        Assert.True(s.HasForwardedPendingCast());
    }

    [Fact]
    public void StartedOnGcdCast_IsNotCounted()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(OnGcdCast(133, started: true));

        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void ForwardedUnstartedOffGcdCast_IsNotCounted()
    {
        // #442: an off-GCD cast coexists with a normal cast server-side, so it must not park
        // on-GCD presses. Before the fix this returned true and jammed casting until relog.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(OffGcdCast(11305)); // Sprint, forwarded-unstarted

        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void ForwardedUnstartedItemUseCast_IsNotCounted()
    {
        // The reported case: Mana Ruby / Robe of the Archmage leak into the queue and
        // previously parked every subsequent spell.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUseCast(10058)); // Mana Ruby

        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void LeakedItemUseCasts_DoNotJamOnGcdCasting()
    {
        // Exact shape of the Ragnaros log: two item-use entries left forwarded-unstarted.
        // The gate must be clear so the next Frostbolt press forwards instead of parking.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUseCast(10058)); // Mana Ruby
        s.PendingNormalCasts.Enqueue(ItemUseCast(18385)); // Robe of the Archmage

        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void LeakedOffGcdCast_DoesNotMaskARealForwardedOnGcdCast()
    {
        // The exemption must not blind the gate: a genuine in-flight on-GCD cast alongside a
        // leaked item entry still parks the next press.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUseCast(10058));  // leaked, ignored
        s.PendingNormalCasts.Enqueue(OnGcdCast(133));      // real forwarded cast

        Assert.True(s.HasForwardedPendingCast());
    }

    [Fact]
    public void StartedOffGcdCast_IsNotCounted()
    {
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(OffGcdCast(11305, started: true));

        Assert.False(s.HasForwardedPendingCast());
    }

    [Fact]
    public void LeakedItemUseCast_StillBlocksThatSameItem_KnownResidual()
    {
        // Documents the boundary of this fix. Spell casting recovers, but the leaked entry
        // still satisfies HasInFlightNormalCastForSpell(), which gates HandleUseItem at
        // Server/SpellHandler.cs:962 — so THAT item stays silently unusable for the rest of
        // the session (cast.dropped.duplicate, reason "in_flight_same_spell_use_item").
        // The symptom degrades from "no casting at all" to "one dead item"; the underlying
        // leak is still the thing to fix. If a later change reaps leaked item entries, this
        // assertion should flip and the residual note in #442 can be closed out.
        var s = NewSession();
        s.PendingNormalCasts.Enqueue(ItemUseCast(10058)); // leaked Mana Ruby

        Assert.False(s.HasForwardedPendingCast());              // spells flow again
        Assert.True(s.HasInFlightNormalCastForSpell(10058));    // but the gem stays blocked
    }
}
