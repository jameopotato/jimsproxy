using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (stuck-logout-stun): gate for the artificial-logout-stun login fix. The detector
// may only trip when the local player's create block carries UNIT_FLAG_STUNNED with no debuff
// beyond the known-benign login set (Resurrection Sickness, Deserter) — every genuine vanilla
// stun is a MOD_STUN debuff occupying a debuff slot in the same create block, and a stun's
// debuff is never on the allow-list, so a real stun must never open the gate (it would let the
// synthesized CMSG_LOGOUT_CANCEL free a legitimately stunned player).
public class StuckLogoutStunGateTests
{
    const uint Stunned = (uint)UnitFlagsVanilla.Stunned;
    const uint InCombat = (uint)UnitFlagsVanilla.InCombat;
    const uint PlayerControlled = (uint)UnitFlagsVanilla.PlayerControlled;

    const uint ResurrectionSickness = 15007;
    const uint Deserter = 26013;
    const uint KidneyShot = 408;   // a real MOD_STUN debuff — stands in for any unrecognized debuff
    const uint Dazed = 1604;       // non-stun but NOT on the allow-list — must stay conservative

    static readonly uint[] NoDebuffs = new uint[0];

    [Fact]
    public void IsArtificialLogoutStun_StunnedFlagNoDebuffs_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned, NoDebuffs));
    }

    // The 2026-07-17 incident wire state: stunned + in-combat on a player, zero debuffs
    // (only Battle Stance / Find Minerals in the buff slots).
    [Fact]
    public void IsArtificialLogoutStun_IncidentFlagCombination_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned | InCombat | PlayerControlled, NoDebuffs));
    }

    // #431 follow-up: the artificial stun coinciding with a benign login debuff was a false
    // negative under the old debuffAuraCount==0 gate — the player stayed half-rooted all
    // session. Res Sickness / Deserter must not veto the cure.
    [Fact]
    public void IsArtificialLogoutStun_StunnedWithResurrectionSickness_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned, new[] { ResurrectionSickness }));
    }

    [Fact]
    public void IsArtificialLogoutStun_StunnedWithDeserter_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned, new[] { Deserter }));
    }

    [Fact]
    public void IsArtificialLogoutStun_StunnedWithBothBenignDebuffs_Trips()
    {
        Assert.True(WorldClient.IsArtificialLogoutStun(Stunned, new[] { ResurrectionSickness, Deserter }));
    }

    // Re-attach mid genuine stun: the stun's own debuff (e.g. Kidney Shot, War Stomp) is in
    // the create block — gate must stay closed, even when a benign debuff is also present.
    [Fact]
    public void IsArtificialLogoutStun_StunnedWithRealStunDebuff_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(Stunned | InCombat, new[] { KidneyShot }));
        Assert.False(WorldClient.IsArtificialLogoutStun(Stunned, new[] { ResurrectionSickness, KidneyShot }));
    }

    // Non-stun debuffs OFF the allow-list (Dazed 1604 was live mid-session in the incident)
    // close the gate too: a false negative is safe — no cure that login — while a false
    // positive is not. Only ids explicitly verified benign may trip the gate.
    [Fact]
    public void IsArtificialLogoutStun_StunnedWithUnrecognizedDebuff_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(Stunned, new[] { Dazed }));
    }

    [Fact]
    public void IsArtificialLogoutStun_NoStunnedFlag_DoesNotTrip()
    {
        Assert.False(WorldClient.IsArtificialLogoutStun(InCombat | PlayerControlled, NoDebuffs));
        Assert.False(WorldClient.IsArtificialLogoutStun(0, NoDebuffs));
        Assert.False(WorldClient.IsArtificialLogoutStun(0, new[] { ResurrectionSickness }));
    }

    // The allow-list is deliberately tiny and explicit — growing it is a reviewed decision,
    // not a data-load side effect.
    [Fact]
    public void BenignLoginDebuffAllowList_IsExactlyTheVerifiedSet()
    {
        Assert.Equal(2, WorldClient.BenignLoginDebuffSpellIds.Count);
        Assert.True(WorldClient.BenignLoginDebuffSpellIds.Contains(ResurrectionSickness));
        Assert.True(WorldClient.BenignLoginDebuffSpellIds.Contains(Deserter));
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
