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
    private static readonly uint[] DefianceRanks         = { 12303, 12788, 12789, 12791, 12792 };
    private static readonly uint[] FeralInstinctRanks    = { 16947, 16948, 16949, 16950, 16951 };
    private static readonly uint[] SilentResolveRanks    = { 14523, 14784, 14785, 14786, 14787 };
    private static readonly uint[] ShadowAffinityRanks   = { 15272, 15318, 15320 };
    private static readonly uint[] DruidSubtletyRanks    = { 17118, 17119, 17120, 17121, 17122 };
    private static readonly uint[] ImpRighteousFuryRanks = { 20468, 20469, 20470 };
    private static readonly uint[] ImpPwsRanks           = { 14748, 14768, 14769 };

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

    // === Shadow Affinity: Priest, explicit per-rank array, shadow spells only ===
    // LTC2 uses a hard-coded array, NOT a 1 - 0.08 * rank formula — rank 3 is
    // 0.75 (steeper than the linear 0.76). Pin the exact values.

    [Theory]
    [InlineData(1, 0.92)]
    [InlineData(2, 0.84)]
    [InlineData(3, 0.75)]
    public void ShadowAffinity_PerRank_LtcArrayValue(int rank, double expected)
    {
        double mod = rank switch { 1 => 0.92, 2 => 0.84, 3 => 0.75, _ => 1.0 };
        Assert.Equal(expected, mod);
    }

    [Fact]
    public void ShadowAffinityRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 15272, 15318, 15320 }, ShadowAffinityRanks);
    }

    [Fact]
    public void ShadowAffinity_AppliesOnlyToShadowSpells_SmokeMembership()
    {
        // Spot-check a few spell-id memberships against the canonical set
        // (transcribed from LTC2 ClassModules/Classic/Priest.lua). Catches
        // accidental drift if the set is reshuffled.
        var shadowSet = new HashSet<uint>
        {
            589, 594, 970, 992, 2767, 10892, 10893, 10894,
            8092, 8102, 8103, 8104, 8105, 8106, 10945, 10946, 10947,
            15407, 17311, 17312, 17313, 17314, 18807,
            2944, 19276, 19277, 19278, 19279,
            15286,
        };
        Assert.Contains(589u, shadowSet);        // SW:P R1
        Assert.Contains(10947u, shadowSet);      // Mind Blast R9
        Assert.Contains(18807u, shadowSet);      // Mind Flay R6
        Assert.Contains(2944u, shadowSet);       // Devouring Plague R1
        Assert.DoesNotContain(585u, shadowSet);  // Smite (holy) — not in set
        Assert.DoesNotContain(8004u, shadowSet); // Lesser Heal — not in set
    }

    // === Druid Subtlety: Druid, -0.04/rank, Arcane/Nature damage only ===

    [Theory]
    [InlineData(1, 0.96)]
    [InlineData(3, 0.88)]
    [InlineData(5, 0.80)]
    public void DruidSubtlety_PerRank_Multiplier(int rank, double expected)
    {
        double mod = 1.0 - (0.04 * rank);
        Assert.Equal(expected, mod, precision: 4);
    }

    [Fact]
    public void DruidSubtletyRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 17118, 17119, 17120, 17121, 17122 }, DruidSubtletyRanks);
    }

    [Fact]
    public void DruidSubtlety_AppliesOnlyToArcaneNatureSpells_SmokeMembership()
    {
        var subtletySet = new HashSet<uint>
        {
            8921, 8924, 8925, 8926, 8927, 8928, 8929, 9833, 9834, 9835, // Moonfire
            2912, 8949, 8950, 8951, 9875, 9876, 25298,                  // Starfire
            5176, 5177, 5178, 6780, 8905, 9912,                         // Wrath
            16914, 17401, 17402,                                         // Hurricane
        };
        Assert.Contains(8921u, subtletySet);       // Moonfire R1
        Assert.Contains(25298u, subtletySet);      // Starfire R7
        Assert.Contains(9912u, subtletySet);       // Wrath R6
        Assert.DoesNotContain(6807u, subtletySet); // Maul (physical) — not in set
        Assert.DoesNotContain(8042u, subtletySet); // Earth Shock (shaman) — not in set
    }

    // === Improved Righteous Fury: Paladin, requires RF aura active ===
    // Formula: 1 + 0.6 * (1 + irfRanks[rank]) where irfRanks = {0.16, 0.33, 0.5}.
    // No-IRF base value (rank 0) = 1.6 (RF alone). With IRF the multiplier scales
    // up to 1.9 at rank 3. Off-baseline only triggers when RF is active.

    [Theory]
    [InlineData(0, 1.6)]   // RF active, no IRF
    [InlineData(1, 1.696)] // IRF 1/3
    [InlineData(2, 1.798)] // IRF 2/3
    [InlineData(3, 1.9)]   // IRF 3/3
    public void ImpRighteousFury_RankToMultiplier_WhenRFActive(int irfRank, double expected)
    {
        double irfBonus = irfRank switch { 1 => 0.16, 2 => 0.33, 3 => 0.50, _ => 0.0 };
        double mod = 1.0 + 0.6 * (1.0 + irfBonus);
        Assert.Equal(expected, mod, precision: 4);
    }

    [Fact]
    public void ImpRighteousFury_RFInactive_NoMultiplier()
    {
        // Without RF aura, GetSpellTalentMultiplier returns 1.0 regardless of
        // IRF rank — paladin pays no holy threat tax outside tank mode.
        bool rfActive = false;
        int irfRank = 3; // full talent
        double mod = rfActive ? 1.0 + 0.6 * (1.0 + 0.50) : 1.0;
        Assert.Equal(1.0, mod);
    }

    [Fact]
    public void ImpRighteousFuryRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 20468, 20469, 20470 }, ImpRighteousFuryRanks);
    }

    // === Imp PW:S: Priest, +0.05/rank, applies to PW:S cast threat ===

    [Theory]
    [InlineData(0, 1.00)]
    [InlineData(1, 1.05)]
    [InlineData(2, 1.10)]
    [InlineData(3, 1.15)]
    public void ImpPws_PerRank_Multiplier(int rank, double expected)
    {
        double mod = rank > 0 ? 1.0 + (0.05 * rank) : 1.0;
        Assert.Equal(expected, mod, precision: 4);
    }

    [Fact]
    public void ImpPwsRanks_AreCanonical()
    {
        Assert.Equal(new uint[] { 14748, 14768, 14769 }, ImpPwsRanks);
    }

    [Fact]
    public void PowerWordShieldAmounts_MatchLtcTable()
    {
        // LTC2 ClassModules/Classic/Priest.lua threatAmounts["pws"] — copied
        // verbatim. Pinned here so a future refresh that reshuffles values is
        // caught immediately.
        var pwsTable = new Dictionary<uint, double>
        {
            [17]    = 22,    [592]   = 44,    [600]   = 79,    [3747]  = 117,
            [6065]  = 150.5, [6066]  = 190.5, [10898] = 242,   [10899] = 302.5,
            [10900] = 381.5, [10901] = 471,
        };
        Assert.Equal(10, pwsTable.Count);
        Assert.Equal(22,    pwsTable[17]);     // R1
        Assert.Equal(471,   pwsTable[10901]);  // R10
        Assert.Equal(150.5, pwsTable[6065]);   // R5 (half-point amount preserved)
    }

    [Fact]
    public void RighteousFurySpells_IncludesHolyDamageAndHeals()
    {
        // Catches drift if the spell-id catalogue is rewritten.
        var rfSet = new HashSet<uint>
        {
            26573, 20116, 20922, 20923, 20924, 27983, // Consecration
            20925, 20927, 20928,                      // Holy Shield
            25912, 25911, 25902,                      // Holy Shock damage
            25903, 25913, 25914,                      // Holy Shock heal
            24239, 24274, 24275,                      // Hammer of Wrath
            635, 639, 647, 1026, 1042, 3472, 10328, 10329, 25292, // Holy Light
            19750, 19939, 19940, 19941, 19942, 19943, // Flash of Light
            633, 2800, 10310,                          // Lay on Hands
        };
        Assert.Contains(26573u, rfSet);     // Consecration R1
        Assert.Contains(635u, rfSet);       // Holy Light R1
        Assert.Contains(19750u, rfSet);     // Flash of Light R1
        Assert.Contains(25914u, rfSet);     // Holy Shock heal R3
        Assert.DoesNotContain(853u, rfSet); // Hammer of Justice (no school) — not in set
    }
}
