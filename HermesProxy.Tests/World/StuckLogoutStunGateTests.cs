using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (stuck-logout-stun): gate for the artificial-logout-stun login fix. The detector
// may only trip when the local player's create block carries UNIT_FLAG_STUNNED with ZERO
// debuff auras — every genuine vanilla stun is a MOD_STUN debuff occupying a debuff slot in
// the same create block, so a real stun must never open the gate (it would let the synthesized
// CMSG_LOGOUT_CANCEL free a legitimately stunned player).
public class StuckLogoutStunGateTests
{
    const uint Stunned = (uint)UnitFlagsVanilla.Stunned;
    const uint InCombat = (uint)UnitFlagsVanilla.InCombat;
    const uint PlayerControlled = (uint)UnitFlagsVanilla.PlayerControlled;

    [Fact]
    public void IsArtificialLogoutStun_StunnedFlagNoDebuffs_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned, debuffAuraCount: 0));
    }

    // The 2026-07-17 incident wire state: stunned + in-combat on a player, zero debuffs
    // (only Battle Stance / Find Minerals in the buff slots).
    [Fact]
    public void IsArtificialLogoutStun_IncidentFlagCombination_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned | InCombat | PlayerControlled, debuffAuraCount: 0));
    }

    // Re-attach mid genuine stun: the stun's own debuff (e.g. Kidney Shot, War Stomp) is in
    // the create block — gate must stay closed.
    [Fact]
    public void IsArtificialLogoutStun_StunnedWithDebuffPresent_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(Stunned | InCombat, debuffAuraCount: 1));
    }

    [Fact]
    public void IsArtificialLogoutStun_NoStunnedFlag_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(InCombat | PlayerControlled, debuffAuraCount: 0));
        Assert.False(WorldClient.IsArtificialLogoutStun(0, debuffAuraCount: 0));
    }

    // Non-stun debuffs (Dazed 1604 was live mid-session in the incident) close the gate too:
    // a false negative is safe — no cure that login — while a false positive is not.
    [Fact]
    public void IsArtificialLogoutStun_StunnedWithHarmlessDebuff_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(Stunned, debuffAuraCount: 2));
    }

    // Locks the vanilla slot layout the detector's buff/debuff partition assumes:
    // 32 buffs then 16 debuffs (incident's Dazed landed in slot 32, the first debuff slot).
    [Fact]
    public void VanillaAuraSlotLayout_Is32BuffsPlus16Debuffs()
    {
        Assert.Equal(48, WorldClient.VanillaAuraSlotCount);
        Assert.Equal(32, WorldClient.VanillaFirstDebuffSlot);
    }
}
