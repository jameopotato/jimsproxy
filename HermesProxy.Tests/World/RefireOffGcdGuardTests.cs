using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// RefireSpellGo off-GCD guard: the duplicate SPELL_GO must never be sent for off-GCD spells.
/// Cooldown-on-event spells (Feign Death, Stealth, Vanish, Presence of Mind...) grey their button on
/// GO and only release it on SMSG_COOLDOWN_EVENT; when the aura breaks in the cast's own server tick
/// that event lands before the duplicate GO, which re-arms the grey for good (field-caught on Feign
/// Death). These tests pin the data assumption the guard rests on: every player cooldown-on-event
/// spell is in SpellOffGcd1.csv, and every known loop target the refire exists for is NOT.
/// </summary>
public class RefireOffGcdGuardTests
{
    static RefireOffGcdGuardTests()
    {
        GameData.LoadOffGcdSpells();
    }

    [Theory]
    [InlineData(5384u)]  // Feign Death — the field-caught case
    [InlineData(1784u)]  // Stealth
    [InlineData(1787u)]  // Stealth (max rank)
    [InlineData(5215u)]  // Prowl
    [InlineData(9913u)]  // Prowl (max rank)
    [InlineData(20580u)] // Shadowmeld
    [InlineData(12043u)] // Presence of Mind
    [InlineData(16188u)] // Nature's Swiftness (druid)
    [InlineData(17116u)] // Nature's Swiftness (shaman)
    [InlineData(14751u)] // Inner Focus
    [InlineData(14177u)] // Cold Blood
    [InlineData(20216u)] // Divine Favor
    [InlineData(18288u)] // Amplify Curse
    [InlineData(11129u)] // Combustion
    [InlineData(16166u)] // Elemental Mastery
    [InlineData(8788u)]  // Lightning Shield (rank 1)
    public void CooldownOnEventSpells_AreOffGcd_SoRefireSkipsThem(uint spellId)
    {
        Assert.True(GameData.IsOffGcd(spellId), $"spell {spellId} must be off-GCD so RefireSpellGo skips it");
    }

    [Theory]
    [InlineData(13877u)] // Blade Flurry
    [InlineData(7386u)]  // Sunder Armor
    [InlineData(6673u)]  // Battle Shout
    [InlineData(19750u)] // Flash of Light — the live-caught loop
    [InlineData(686u)]   // Shadow Bolt
    [InlineData(13819u)] // Summon Warhorse — the WSG mount loop
    public void KnownLoopTargets_StayOnGcd_SoRefireStillCoversThem(uint spellId)
    {
        Assert.False(GameData.IsOffGcd(spellId), $"spell {spellId} is a refire target and must stay on-GCD");
    }
}
