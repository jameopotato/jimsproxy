using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#382): a player charmed by ANOTHER PLAYER FPS-locks 1.14 clients in BGs while
// the charm is active. The suspect state is a HYBRID no modern server produces on one unit:
// charm control (CHARMEDBY-player, PLAYER_CONTROLLED, shim-POSSESSED) coexisting with
// UNIT_FLAG_PET_IN_COMBAT (0x800) — vanilla pins that flag on the charmed unit itself
// (cmangos/vmangos SetInCombatState: any unit with a charmer), while modern TC pins it on
// the CHARMER. Both severe captured caps delivered the full hybrid to the client; the fix
// strips 0x800 from a player for exactly the duration of a player-charm.
//
// The warlock corner (James, 2026-07-24): a pet-class player LEGITIMATELY carries 0x800
// (own pet fighting) BEFORE being charmed — and a charm apply can arrive with NO flags
// update in the block (cap2's apply was CHARMEDBY+faction only). A passive strip never
// fires there; the client keeps its held 0x800 and enters the hybrid anyway. So the strip
// must synthesize a flags re-send on the charm edges from the cached last-known raw flags.
public class PetInCombatCharmStripTests
{
    static readonly WowGuid128 Victim = WowGuid128.Create(HighGuidType703.Player, 21654);
    static readonly WowGuid128 Charmer = WowGuid128.Create(HighGuidType703.Player, 15773);
    static readonly WowGuid128 NpcCharmer = WowGuid128.Create(HighGuidType703.Creature, 0, 12118, 500); // Lucifron
    static readonly WowGuid128 NpcVictim = WowGuid128.Create(HighGuidType703.Creature, 0, 100, 501);

    const uint PetInCombat = (uint)UnitFlags.PetInCombat;          // 0x800
    const uint PlayerControlled = (uint)UnitFlags.PlayerControlled; // 0x8
    const uint InCombat = (uint)UnitFlags.InCombat;                 // 0x80000
    const uint Possessed = (uint)UnitFlags.Possessed;               // 0x1000000

    // ---- tracking predicate: ALL perspectives (self/charmer sessions included — the victim
    // drops too, and the hybrid is wrong from every viewpoint). NPC charmers excluded:
    // raid MC (Lucifron) is long-stable content.

    [Fact]
    public void CharmTracking_PlayerCharmedByPlayer_True()
    {
        Assert.True(WorldClient.IsPlayerCharmedByPlayer(Victim, Charmer));
    }

    [Fact]
    public void CharmTracking_NpcCharmer_False()
    {
        Assert.False(WorldClient.IsPlayerCharmedByPlayer(Victim, NpcCharmer));
    }

    [Fact]
    public void CharmTracking_NpcVictim_False()
    {
        Assert.False(WorldClient.IsPlayerCharmedByPlayer(NpcVictim, Charmer));
    }

    [Fact]
    public void CharmTracking_EmptyCharmedBy_False()
    {
        Assert.False(WorldClient.IsPlayerCharmedByPlayer(Victim, WowGuid128.Empty));
    }

    // ---- passive strip (flags update present in the block) ----

    [Fact]
    public void Strip_TrackedCharm_ClearsOnlyPetInCombat()
    {
        uint flags = PlayerControlled | PetInCombat | InCombat | Possessed;
        uint result = WorldClient.ApplyPetInCombatCharmStrip(flags, leverOn: true, isCharmTracked: true);
        Assert.Equal(PlayerControlled | InCombat | Possessed, result);
    }

    [Fact]
    public void Strip_NotTracked_Untouched()
    {
        uint flags = PlayerControlled | PetInCombat | InCombat;
        Assert.Equal(flags, WorldClient.ApplyPetInCombatCharmStrip(flags, leverOn: true, isCharmTracked: false));
    }

    [Fact]
    public void Strip_LeverOff_Untouched()
    {
        uint flags = PlayerControlled | PetInCombat;
        Assert.Equal(flags, WorldClient.ApplyPetInCombatCharmStrip(flags, leverOn: false, isCharmTracked: true));
    }

    [Fact]
    public void Strip_NoPetInCombatBit_NoOp()
    {
        uint flags = PlayerControlled | InCombat;
        Assert.Equal(flags, WorldClient.ApplyPetInCombatCharmStrip(flags, leverOn: true, isCharmTracked: true));
    }

    // ---- proactive flags re-sync on charm edges (THE warlock corner) ----

    // Pet-class player carries 0x800 pre-charm; charm apply block has NO flags write
    // (cap2 pattern) → must synthesize, or the client holds the hybrid untouched.
    [Fact]
    public void SynthResync_CharmEdgeNoFlagsInBlock_CachedHasPetInCombat_Fires()
    {
        Assert.True(WorldClient.ShouldSynthPetInCombatFlagsResync(
            leverOn: true, charmEdgeThisBlock: true, flagsInUpdateMask: false, cachedRawHasPetInCombat: true));
    }

    // Flags update present in the same block → the passive strip handles it; no synth.
    [Fact]
    public void SynthResync_FlagsAlreadyInBlock_DoesNotFire()
    {
        Assert.False(WorldClient.ShouldSynthPetInCombatFlagsResync(
            leverOn: true, charmEdgeThisBlock: true, flagsInUpdateMask: true, cachedRawHasPetInCombat: true));
    }

    // No cached 0x800 (rogue/mage victim, or pet idle) → nothing to clear/restore; no synth.
    [Fact]
    public void SynthResync_CachedFlagsClean_DoesNotFire()
    {
        Assert.False(WorldClient.ShouldSynthPetInCombatFlagsResync(
            leverOn: true, charmEdgeThisBlock: true, flagsInUpdateMask: false, cachedRawHasPetInCombat: false));
    }

    // Mid-charm update with no flags (heartbeats etc.) — not an edge; no synth spam.
    [Fact]
    public void SynthResync_NotACharmEdge_DoesNotFire()
    {
        Assert.False(WorldClient.ShouldSynthPetInCombatFlagsResync(
            leverOn: true, charmEdgeThisBlock: false, flagsInUpdateMask: false, cachedRawHasPetInCombat: true));
    }

    [Fact]
    public void SynthResync_LeverOff_DoesNotFire()
    {
        Assert.False(WorldClient.ShouldSynthPetInCombatFlagsResync(
            leverOn: false, charmEdgeThisBlock: true, flagsInUpdateMask: false, cachedRawHasPetInCombat: true));
    }
}
