using System;
using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (observed-pose strand, 2026-08-14): the SMSG_SPELL_FAILED_OTHER retry-storm
// dedup must never swallow the terminator of a live tracked cast instance. Field capture:
// a party member spam-recasting Skinning (10768) had the cancel of one in-flight cast
// classified as a storm duplicate (411ms after the previous routed failure); the next
// SPELL_START overwrote the tracked CastID and the cast-hold kit ("crafting hands" pose)
// stayed frozen on the 1.14.2 client until the unit despawned. These drive
// GameSessionData.ShouldDedupSpellFailedOther directly — no clock, fully synchronous.
public class SpellFailedOtherDedupTests
{
    static SpellFailedOtherDedupTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private const uint Skinning = 10768;
    private const uint Firebolt = 3110;
    private const long Window = 500;

    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();
    private static WowGuid128 Caster(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);
    private static WowGuid128 SomeCastId(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 99999, counter);

    [Fact]
    public void FirstFailure_NoHistory_Forwards()
    {
        var session = NewSession();
        Assert.False(session.ShouldDedupSpellFailedOther(Caster(1), Skinning, 1000, Window, out var msSince));
        Assert.Equal(-1, msSince);
    }

    [Fact]
    public void RepeatWithinWindow_NoLiveInstance_Skips()
    {
        // The storm the dedup exists for (imp Firebolt retry spam): repeat failures with
        // no intervening SPELL_START must still be dropped.
        var session = NewSession();
        var pet = Caster(1);
        session.RecentlyForwardedSpellFailedOther[(pet, Firebolt)] = 1000;

        Assert.True(session.ShouldDedupSpellFailedOther(pet, Firebolt, 1400, Window, out var msSince));
        Assert.Equal(400, msSince);
    }

    [Fact]
    public void RepeatOutsideWindow_Forwards()
    {
        var session = NewSession();
        var pet = Caster(1);
        session.RecentlyForwardedSpellFailedOther[(pet, Firebolt)] = 1000;

        Assert.False(session.ShouldDedupSpellFailedOther(pet, Firebolt, 1500, Window, out var msSince));
        Assert.Equal(-1, msSince);
    }

    [Fact]
    public void RepeatWithinWindow_LiveOtherCasterInstance_Forwards()
    {
        // The strand: a SPELL_START minted a fresh CastID after the last routed failure.
        // This failure is that instance's only terminator — never a duplicate.
        var session = NewSession();
        var member = Caster(73772);
        session.RecentlyForwardedSpellFailedOther[(member, Skinning)] = 1000;
        session.EnqueueObservedStartCastId(member, Skinning, SomeCastId(5));

        Assert.False(session.ShouldDedupSpellFailedOther(member, Skinning, 1411, Window, out var msSince));
        Assert.Equal(411, msSince); // within the window: callers log the bypass
    }

    [Fact]
    public void RepeatWithinWindow_LivePetAutoCastInstance_Forwards()
    {
        var session = NewSession();
        var pet = Caster(1);
        session.RecentlyForwardedSpellFailedOther[(pet, Firebolt)] = 1000;
        session.PetAutoCastActiveCastIds[(pet, Firebolt)] = SomeCastId(6);

        Assert.False(session.ShouldDedupSpellFailedOther(pet, Firebolt, 1400, Window, out var msSince));
        Assert.Equal(400, msSince);
    }

    [Fact]
    public void RepeatWithinWindow_LiveInstanceForDifferentSpell_StillSkips()
    {
        // The bypass keys on exactly (caster, spell) — an unrelated in-flight cast by the
        // same unit must not defeat the storm dedup for this spell.
        var session = NewSession();
        var member = Caster(73772);
        session.RecentlyForwardedSpellFailedOther[(member, Firebolt)] = 1000;
        session.EnqueueObservedStartCastId(member, Skinning, SomeCastId(7));

        Assert.True(session.ShouldDedupSpellFailedOther(member, Firebolt, 1400, Window, out _));
    }

    [Fact]
    public void RapidRecastSequence_MatchesFieldCapture()
    {
        // The 2026-08-14 sequence for (member 73772, Skinning 10768), collapsed:
        //   t=1000 FAILED routed  -> handler removes the tracked instance, records t
        //   t=1150 SPELL_START    -> handler mints + tracks a fresh CastID
        //   t=1411 FAILED         -> old logic skipped this (411ms < 500ms) = strand;
        //                            must now forward because the instance is live.
        var session = NewSession();
        var member = Caster(73772);

        session.EnqueueObservedStartCastId(member, Skinning, SomeCastId(4));
        Assert.False(session.ShouldDedupSpellFailedOther(member, Skinning, 1000, Window, out _));
        Assert.True(session.TryPairObservedTerminatorCastId(member, Skinning, out _, out _));
        session.RecentlyForwardedSpellFailedOther[(member, Skinning)] = 1000;

        session.EnqueueObservedStartCastId(member, Skinning, SomeCastId(5));

        Assert.False(session.ShouldDedupSpellFailedOther(member, Skinning, 1411, Window, out var msSince));
        Assert.Equal(411, msSince);
    }

    [Fact]
    public void StormAfterRoutedTerminator_Skips()
    {
        // After a routed failure consumed the live instance, follow-up storm repeats
        // within the window (no new SPELL_START) are still deduped.
        var session = NewSession();
        var pet = Caster(1);

        session.PetAutoCastActiveCastIds[(pet, Firebolt)] = SomeCastId(8);
        Assert.False(session.ShouldDedupSpellFailedOther(pet, Firebolt, 1000, Window, out _));
        session.PetAutoCastActiveCastIds.TryRemove((pet, Firebolt), out _);
        session.RecentlyForwardedSpellFailedOther[(pet, Firebolt)] = 1000;

        Assert.True(session.ShouldDedupSpellFailedOther(pet, Firebolt, 1100, Window, out var msSince));
        Assert.Equal(100, msSince);
    }
}
