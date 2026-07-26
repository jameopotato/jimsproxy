using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the raid-wide threat-enchant read (ThreatSetBonuses.GetGearEnchantMultiplier).
// Threat enchants ride in the public per-GUID permanent-enchant cache
// (CachedPlayerEnchants, populated for the inspect UI from the visible-item
// enchant field), so they resolve for EVERY raider, not just the local player —
// which is the point of the raid-wide gear/enchant pass. Enchant ids and their
// factors match LibThreatClassic2 (cloak Subtlety 2621 = -2%, gloves Threat
// 2613 = +2%), so the proxy's SMSG_THREAT_UPDATE agrees with the 1.12 KTM meters.
public class ThreatEnchantTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    // A non-local raider GUID (distinct from the empty CurrentPlayerGuid a fresh
    // test session carries), so these exercise the OTHER-player path specifically.
    private static WowGuid128 Raider(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Player, 0, 0, counter);

    [Fact]
    public void GetGearEnchantMultiplier_UnknownGuid_ReturnsNeutral()
    {
        var session = NewSession();
        Assert.Equal(1.0, ThreatSetBonuses.GetGearEnchantMultiplier(session, Raider(1)), 6);
    }

    [Fact]
    public void GetGearEnchantMultiplier_NoThreatEnchants_ReturnsNeutral()
    {
        var session = NewSession();
        var g = Raider(2);
        session.CachedPlayerEnchants[g] = new uint[19]; // all zero
        Assert.Equal(1.0, ThreatSetBonuses.GetGearEnchantMultiplier(session, g), 6);
    }

    [Fact]
    public void GetGearEnchantMultiplier_CloakSubtlety_ReducesTwoPercent()
    {
        var session = NewSession();
        var g = Raider(3);
        session.CachedPlayerEnchants[g] = new uint[19];
        session.CachedPlayerEnchants[g][14] = 2621; // EQUIPMENT_SLOT_BACK, Subtlety
        Assert.Equal(0.98, ThreatSetBonuses.GetGearEnchantMultiplier(session, g), 6);
    }

    [Fact]
    public void GetGearEnchantMultiplier_GlovesThreat_AddsTwoPercent()
    {
        var session = NewSession();
        var g = Raider(4);
        session.CachedPlayerEnchants[g] = new uint[19];
        session.CachedPlayerEnchants[g][9] = 2613; // EQUIPMENT_SLOT_HANDS, Threat
        Assert.Equal(1.02, ThreatSetBonuses.GetGearEnchantMultiplier(session, g), 6);
    }

    [Fact]
    public void GetGearEnchantMultiplier_BothEnchants_StackMultiplicatively()
    {
        var session = NewSession();
        var g = Raider(5);
        session.CachedPlayerEnchants[g] = new uint[19];
        session.CachedPlayerEnchants[g][14] = 2621;
        session.CachedPlayerEnchants[g][9] = 2613;
        Assert.Equal(0.98 * 1.02, ThreatSetBonuses.GetGearEnchantMultiplier(session, g), 6);
    }

    [Fact]
    public void GetGearEnchantMultiplier_OtherEnchant_Ignored()
    {
        var session = NewSession();
        var g = Raider(6);
        session.CachedPlayerEnchants[g] = new uint[19];
        session.CachedPlayerEnchants[g][14] = 1888; // Greater Agility — not a threat enchant
        Assert.Equal(1.0, ThreatSetBonuses.GetGearEnchantMultiplier(session, g), 6);
    }
}
