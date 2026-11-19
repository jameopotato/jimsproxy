using System;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (next-melee slot wedge, 2026-11-18): the next-melee slot (CurrentClientNextMeleeCast)
// holds the one Heroic Strike / Cleave / Maul / Raptor Strike press the server has queued, and
// HandleCastSpell rejects every next-melee press while it is set. It is cleared by the swing's
// SPELL_GO, the same-spell CAST_FAILED, or a session reset. A charm on the player (Molten Core,
// Lucifron's Flamewaker Protector, Dominate Mind) makes Kronos end the queued swing with
// SPELL_FAILURE alone: no CAST_FAILED ever follows, the slot stayed set, and every later press was
// rejected locally until relog (684 Maul presses over 20 minutes in the reporter's capture). The fix
// marks the slot on that SPELL_FAILURE and lets the next press release it.
public class NextMeleeSlotServerFailureTests
{
    private const uint Maul = 9881;
    private const uint Cleave = 20569;
    private const uint AutoShot = 75;

    private static ClientCastRequest MakePress(uint spellId) => new ClientCastRequest
    {
        SpellId = spellId,
        Timestamp = Environment.TickCount,
        HasSentPrepare = true,
        ClientGUID = new WowGuid128(43750, 0xBC0000000000002Aul),
        ServerGUID = new WowGuid128(113226, 0xBC0000000000002Bul),
    };

    [Fact]
    public void LiveSlot_IsNotReleased()
    {
        // A queued swing with no server failure is still occupying the slot: the occupied-slot
        // reject must keep working exactly as before (cross-spell swaps, re-presses in flight).
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(press, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void SpellFailureForTheSlotSpell_ThenNextPress_ReleasesTheSlot()
    {
        // The reporter's sequence: Maul queued, Dominate Mind lands, SPELL_FAILURE(Maul) arrives and
        // no CAST_FAILED follows. The next press must find the slot free.
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        Assert.True(state.MarkNextMeleeServerFailure(Maul));
        var released = state.TryReleaseServerFailedNextMeleeSlot();

        Assert.Same(press, released);
        Assert.Null(state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void SpellFailureAlone_DoesNotClearTheSlotByItself()
    {
        // The mark does not clear the slot: the normal failure pair's trailing CAST_FAILED must still
        // find and resolve it (the special-slot branch answers on the server id with the real reason).
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        state.MarkNextMeleeServerFailure(Maul);

        Assert.Same(press, state.CurrentClientNextMeleeCast);
        Assert.True(press.ServerFailureSeen);
    }

    [Fact]
    public void SpellFailureForAnotherSpell_DoesNotMarkTheSlot()
    {
        // Heroic Strike queued, a SPELL_FAILURE for a different spell (here Cleave) must not free it.
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(25286);
        state.CurrentClientNextMeleeCast = press;

        Assert.False(state.MarkNextMeleeServerFailure(Cleave));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(press, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void EmptySlot_MarkAndReleaseAreNoOps()
    {
        var state = GameSessionData.CreateForTesting();

        Assert.False(state.MarkNextMeleeServerFailure(Maul));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Null(state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void Release_IsOneShot_AndANewPressIsLive()
    {
        // After the release the caller installs the new press in the slot; that press carries no mark
        // and is protected by the reject again.
        var state = GameSessionData.CreateForTesting();
        state.CurrentClientNextMeleeCast = MakePress(Maul);
        state.MarkNextMeleeServerFailure(Maul);
        Assert.NotNull(state.TryReleaseServerFailedNextMeleeSlot());

        var next = MakePress(Maul);
        state.CurrentClientNextMeleeCast = next;

        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(next, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void AutoRepeatSlot_IsNotTouched()
    {
        // Only the next-melee slot is in scope; the auto-repeat slot (Auto Shot / Shoot) keeps its own
        // lifecycle (retries, CMSG_CANCEL_AUTO_REPEAT_SPELL).
        var state = GameSessionData.CreateForTesting();
        var autoRepeat = MakePress(AutoShot);
        state.CurrentClientAutoRepeatCast = autoRepeat;

        Assert.False(state.MarkNextMeleeServerFailure(AutoShot));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(autoRepeat, state.CurrentClientAutoRepeatCast);
        Assert.False(autoRepeat.ServerFailureSeen);
    }
}
