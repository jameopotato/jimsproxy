using System.Linq;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (feared-while-sitting, issue #479): gates behind the synthesized
// stand-up for a seated local player targeted by fear. Red-first.
public class FearStandSynthTests
{
    private static readonly WowGuid128 Self = new WowGuid128(0x000000000000000A, 0x0800040000000000);
    private static readonly WowGuid128 Other = new WowGuid128(0x000000000000002C, 0x0800040000000000);

    private const uint Seated = 1;   // UNIT_STAND_STATE_SIT
    private const uint Standing = 0; // UNIT_STAND_STATE_STAND

    private const uint WarlockFearR3 = 6215;
    private const uint Fleeing = (uint)UnitFlagsVanilla.Fleeing;
    private const uint Confused = (uint)UnitFlagsVanilla.Confused;

    // --- spell table sanity (derivation anchors) ---

    [Theory]
    [InlineData(5782u)]  // Fear rank 1
    [InlineData(6213u)]  // Fear rank 2
    [InlineData(6215u)]  // Fear rank 3
    [InlineData(5484u)]  // Howl of Terror rank 1
    [InlineData(17928u)] // Howl of Terror rank 2
    [InlineData(8122u)]  // Psychic Scream rank 1
    [InlineData(10890u)] // Psychic Scream rank 4
    [InlineData(5246u)]  // Intimidating Shout
    [InlineData(6605u)]  // mob fear captured in the 2026-08-17 A/B run
    [InlineData(12542u)] // creature Fear from the 2005 vanilla sniff exhibit
    public void FearTable_ContainsKnownFearAuraSpells(uint spellId)
    {
        Assert.True(FearStandSynth.FearAuraSpellIds.Contains(spellId));
    }

    [Theory]
    [InlineData(116u)]   // Frostbolt
    [InlineData(8129u)]  // Mana Burn (adjacent id to Psychic Scream ranks)
    [InlineData(0u)]
    public void FearTable_ExcludesNonFearSpells(uint spellId)
    {
        Assert.False(FearStandSynth.FearAuraSpellIds.Contains(spellId));
    }

    [Fact]
    public void FearTable_HasExpectedRowCount()
    {
        Assert.Equal(74, FearStandSynth.FearAuraSpellIds.Count);
    }

    [Fact]
    public void FearConfuseMask_IsFleeingOrConfused()
    {
        Assert.Equal(0x00C00000u, FearStandSynth.FearConfuseUnitFlagsMask);
    }

    // --- trigger 1: pre-stand at incoming fear cast ---

    [Fact]
    public void PreStand_Fires_WhenSeatedSelfTargetedByFearCast()
    {
        Assert.True(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, Self, Self, Seated));
    }

    [Fact]
    public void PreStand_Ignores_WhenAlreadyStanding()
    {
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, Self, Self, Standing));
    }

    [Fact]
    public void PreStand_Ignores_WhenTargetIsAnotherUnit()
    {
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, Other, Self, Seated));
    }

    [Fact]
    public void PreStand_Ignores_NonFearSpell()
    {
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, 116, Self, Self, Seated));
    }

    [Fact]
    public void PreStand_Ignores_WhenLeverOff()
    {
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(false, WarlockFearR3, Self, Self, Seated));
    }

    [Fact]
    public void PreStand_Ignores_NullOrEmptyGuids()
    {
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, null, Self, Seated));
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, Self, null, Seated));
        Assert.False(FearStandSynth.ShouldPreStandOnIncomingFear(true, WarlockFearR3, WowGuid128.Empty, WowGuid128.Empty, Seated));
    }

    // --- trigger 2: CC-onset fallback ---

    [Fact]
    public void CcOnset_Fires_OnFleeingRisingEdgeWhileSeated()
    {
        Assert.True(FearStandSynth.ShouldStandOnCcOnset(true, 0, Fleeing, Seated));
    }

    [Fact]
    public void CcOnset_Fires_OnConfusedRisingEdgeWhileSeated()
    {
        Assert.True(FearStandSynth.ShouldStandOnCcOnset(true, 0, Confused, Seated));
    }

    [Fact]
    public void CcOnset_Ignores_WhenAlreadyCcd()
    {
        // FLAGS re-writes during a held fear (e.g. combat bit toggles) must not re-fire.
        Assert.False(FearStandSynth.ShouldStandOnCcOnset(true, Fleeing, Fleeing, Seated));
        Assert.False(FearStandSynth.ShouldStandOnCcOnset(true, Fleeing, Fleeing | Confused, Seated));
    }

    [Fact]
    public void CcOnset_Ignores_FallingEdge()
    {
        Assert.False(FearStandSynth.ShouldStandOnCcOnset(true, Fleeing, 0, Seated));
    }

    [Fact]
    public void CcOnset_Ignores_WhenStanding()
    {
        Assert.False(FearStandSynth.ShouldStandOnCcOnset(true, 0, Fleeing, Standing));
    }

    [Fact]
    public void CcOnset_Ignores_WhenLeverOff()
    {
        Assert.False(FearStandSynth.ShouldStandOnCcOnset(false, 0, Fleeing, Seated));
    }
}
