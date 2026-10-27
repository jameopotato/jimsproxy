using System.Collections.Generic;
using System.Linq;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (stealth cooldown after a stealthed login): the held-cooldown packet sent after the
/// local player's create for aura-active spells whose cooldown starts when the aura ends.
/// </summary>
public class OnHoldCooldownsAtLoginTests
{
    static OnHoldCooldownsAtLoginTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private const uint StealthR4 = 1787;
    private const uint ProwlR3 = 9913;
    private const uint Shadowmeld = 20580;
    private const uint Fortitude = 10938;
    private const uint Vanish = 1857;

    [Fact]
    public void TheSet_HoldsTheStealthFamilyProwlAndShadowmeld()
    {
        foreach (var id in new uint[] { 1784, 1785, 1786, 1787, 5215, 6783, 9913, 20580 })
            Assert.True(GameData.CooldownStartsOnAuraRemoval(id), $"{id}");
        Assert.False(GameData.CooldownStartsOnAuraRemoval(Fortitude));
        Assert.False(GameData.CooldownStartsOnAuraRemoval(Vanish));
    }

    [Fact]
    public void Select_PicksOnlySpellsOfThatKind_InSlotOrder_OnceEach()
    {
        var auras = new uint[] { Fortitude, 0, StealthR4, 0, ProwlR3, StealthR4, Shadowmeld };

        var picked = OnHoldCooldownsAtLogin.Select(auras, GameData.CooldownStartsOnAuraRemoval);

        Assert.Equal(new List<uint> { StealthR4, ProwlR3, Shadowmeld }, picked);
    }

    [Fact]
    public void Select_EmptyWhenNothingQualifies()
    {
        Assert.Empty(OnHoldCooldownsAtLogin.Select(new uint[] { Fortitude, 0, 0 }, GameData.CooldownStartsOnAuraRemoval));
    }

    [Fact]
    public void BuildPacket_HeldEntries_NoForcedDuration_RateOne()
    {
        var player = WowGuid128.Create(HighGuidType703.Player, 12);

        var packet = OnHoldCooldownsAtLogin.BuildPacket(player, new uint[] { StealthR4, Shadowmeld });

        Assert.Equal(player, packet.Caster);
        Assert.Equal(OnHoldCooldownsAtLogin.SpellCooldownFlagOnHold, packet.Flags);
        Assert.Equal(new uint[] { StealthR4, Shadowmeld }, packet.SpellCooldowns.Select(c => c.SpellID).ToArray());
        Assert.All(packet.SpellCooldowns, c => Assert.Equal(0u, c.ForcedCooldown));
        Assert.All(packet.SpellCooldowns, c => Assert.Equal(1.0f, c.ModRate));
    }
}
