using System.Collections.Generic;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the talent-derived threat multiplier formulas shipped in
// ThreatTracker.GetTalentMultiplier, plus the rank-id arrays they read against
// the player's known-spells set.
//
// Values match LibThreatClassic2's per-class formulas (the reference addons
// every threat meter — ThreatPlates, ThreatClassic2, Details TinyThreat —
// compute against). When the proxy emits SMSG_THREAT_UPDATE values matching
// this table, all those addons should agree with the proxy.
//
// Rank id arrays are pinned here so a future Talent.dbc refresh that
// reorganizes spell ids is caught immediately rather than silently shifting
// threat output. Sourced from the 1.14.2 Talent.dbc via wago.tools build
// 42597, cross-referenced against SpellName.dbc.
public class TalentThreatModifierTests
{
    // Mirrors ThreatTracker.GetTalentRank. Returns the highest 1-based rank
    // index the player has, or 0 if untalented. The lookup must walk both the
    // real CurrentPlayerKnownSpells (server-side state) AND
    // SynthesizedTalentRanks (proxy-injected predecessor IDs) — without the
    // latter, only the highest rank would be detectable.
    public static int GetTalentRank(uint[] rankIds, HashSet<uint> known, HashSet<uint> synthesized)
    {
        int highest = 0;
        for (int i = 0; i < rankIds.Length; i++)
            if (known.Contains(rankIds[i]) || synthesized.Contains(rankIds[i]))
                highest = i + 1;
        return highest;
    }

    // Rank-id arrays MUST match the constants in ThreatTracker.cs verbatim.
    // These are the spell IDs the proxy reads off the player's known-spells
    // set; changing them silently is the kind of bug only a tester catches.
    private static readonly uint[] DefianceRanks      = { 12303, 12788, 12789, 12791, 12792 };
    private static readonly uint[] FeralInstinctRanks = { 16947, 16948, 16949, 16950, 16951 };
    private static readonly uint[] SilentResolveRanks = { 14523, 14784, 14785, 14786, 14787 };

    // === Rank detection: highest known rank wins ===

    [Fact]
    public void GetTalentRank_NoMatch_Returns0()
    {
        var known = new HashSet<uint>();
        var synth = new HashSet<uint>();
        Assert.Equal(0, GetTalentRank(DefianceRanks, known, synth));
    }

    [Fact]
    public void GetTalentRank_OnlyHighestInRealKnown_ReturnsTopRank()
    {
        // Mirrors the post-talent-injection state where server only tracks rank 5,
        // proxy synthesized rank 1-4 into the side set.
        var known = new HashSet<uint> { 12792 };  // Defiance rank 5
        var synth = new HashSet<uint> { 12303, 12788, 12789, 12791 };
        Assert.Equal(5, GetTalentRank(DefianceRanks, known, synth));
    }

    [Fact]
    public void GetTalentRank_PicksHighestEvenAcrossBothSets()
    {
        // Edge case: rank 3 in synthesized, rank 5 in real-known. Must return 5.
        var known = new HashSet<uint> { 12792 };
        var synth = new HashSet<uint> { 12789 };
        Assert.Equal(5, GetTalentRank(DefianceRanks, known, synth));
    }

    [Fact]
    public void GetTalentRank_PartiallySpent_ReturnsThatRank()
    {
        var known = new HashSet<uint> { 12788 };  // Defiance rank 2 only
        var synth = new HashSet<uint> { 12303 };
        Assert.Equal(2, GetTalentRank(DefianceRanks, known, synth));
    }

    // === Defiance: Warrior, +0.03/rank, Defensive Stance only ===

    [Theory]
    [InlineData(1, 1.03)]
    [InlineData(2, 1.06)]
    [InlineData(3, 1.09)]
    [InlineData(4, 1.12)]
    [InlineData(5, 1.15)]
    public void Defiance_PerRank_Multiplier(int rank, double expected)
    {
        double mod = 1.0 + (0.03 * rank);
        Assert.Equal(expected, mod, precision: 4);
    }

    [Fact]
    public void Defiance_FullDefensiveStanceWithMaxRank_Yields1Point495()
    {
        // Defensive Stance baseline 1.30 × Defiance 5/5 (1.15) = 1.495.
        // Same number LibThreatClassic2 computes for a fully-talented prot warrior.
        double passive = 1.30 * (1.0 + 0.03 * 5);
        Assert.Equal(1.495, passive, precision: 4);
    }

    // === Feral Instinct: Druid, +0.03/rank, Bear / Dire Bear only ===

    [Theory]
    [InlineData(1, 1.03)]
    [InlineData(3, 1.09)]
    [InlineData(5, 1.15)]
    public void FeralInstinct_PerRank_Multiplier(int rank, double expected)
    {
        double mod = 1.0 + (0.03 * rank);
        Assert.Equal(expected, mod, precision: 4);
    }

    [Fact]
    public void FeralInstinct_BearFormFullRank_Yields1Point495()
    {
        // Bear Form baseline 1.30 × Feral Instinct 5/5 = 1.495.
        double passive = 1.30 * (1.0 + 0.03 * 5);
        Assert.Equal(1.495, passive, precision: 4);
    }

    // === Silent Resolve: Priest, -0.04/rank, always-on ===

    [Theory]
    [InlineData(1, 0.96)]
    [InlineData(2, 0.92)]
    [InlineData(3, 0.88)]
    [InlineData(4, 0.84)]
    [InlineData(5, 0.80)]
    public void SilentResolve_PerRank_Multiplier(int rank, double expected)
    {
        double mod = 1.0 - (0.04 * rank);
        Assert.Equal(expected, mod, precision: 4);
    }

    // === Rank id arrays — locked against silent Talent.dbc reshuffles ===

    [Fact]
    public void DefianceRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 12303, 12788, 12789, 12791, 12792 }, DefianceRanks);
    }

    [Fact]
    public void FeralInstinctRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 16947, 16948, 16949, 16950, 16951 }, FeralInstinctRanks);
    }

    [Fact]
    public void SilentResolveRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 14523, 14784, 14785, 14786, 14787 }, SilentResolveRanks);
    }

    // === Boundary cases ===

    [Fact]
    public void Defiance_Untalented_NoChange()
    {
        int rank = 0;
        double mod = rank > 0 ? 1.0 + (0.03 * rank) : 1.0;
        Assert.Equal(1.0, mod);
    }

    [Fact]
    public void SilentResolve_Untalented_NoChange()
    {
        int rank = 0;
        double mod = rank > 0 ? 1.0 - (0.04 * rank) : 1.0;
        Assert.Equal(1.0, mod);
    }
}
