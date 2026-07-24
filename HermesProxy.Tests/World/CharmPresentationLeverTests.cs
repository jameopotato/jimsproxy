using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#382 charm-presentation levers): a player charmed by ANOTHER PLAYER in a BG
// FPS-locks 1.14 clients (victim and bystanders) for the charm's duration. Capture analysis
// (2026-07-23) exonerated the flags lane wholesale (PET_IN_COMBAT = routine pet-owner state;
// cap1/cap2 charm windows carried no flags updates at all) and narrowed the held observer
// state to three legs: CHARMEDBY(player), the faction flip, and the charm aura itself.
// These tests lock the scope predicates of the config levers that let a reporter A/B each
// leg independently. NPC charmers (Lucifron's Dominate Mind 20604) must never be touched —
// raid MC is long-stable content.
public class CharmPresentationLeverTests
{
    static readonly WowGuid128 Victim = WowGuid128.Create(HighGuidType703.Player, 21654);
    static readonly WowGuid128 Charmer = WowGuid128.Create(HighGuidType703.Player, 15773);
    static readonly WowGuid128 LocalPlayer = WowGuid128.Create(HighGuidType703.Player, 28648);
    static readonly WowGuid128 NpcCharmer = WowGuid128.Create(HighGuidType703.Creature, 0, 12118, 500); // Lucifron
    static readonly WowGuid128 NpcVictim = WowGuid128.Create(HighGuidType703.Creature, 0, 100, 501);

    const uint GnomishMindControlCap = 13181; // MOD_CHARM
    const uint PriestMindControl = 605;       // MOD_POSSESS
    const uint DominateMind = 20604;          // Lucifron (NPC charmer)
    const uint UnrelatedAura = 1833;          // Cheap Shot

    // ---- scope predicate (shared by the shim and levers L1/L2) ----

    [Fact]
    public void ObservedPredicate_PlayerCharmedByPlayer_Bystander_True()
    {
        Assert.True(WorldClient.IsObservedPlayerCharmedByPlayer(Victim, Charmer, LocalPlayer));
    }

    [Fact]
    public void ObservedPredicate_LocalPlayerIsVictim_False()
    {
        Assert.False(WorldClient.IsObservedPlayerCharmedByPlayer(LocalPlayer, Charmer, LocalPlayer));
    }

    [Fact]
    public void ObservedPredicate_LocalPlayerIsCharmer_False()
    {
        Assert.False(WorldClient.IsObservedPlayerCharmedByPlayer(Victim, LocalPlayer, LocalPlayer));
    }

    [Fact]
    public void ObservedPredicate_NpcCharmer_False()
    {
        Assert.False(WorldClient.IsObservedPlayerCharmedByPlayer(Victim, NpcCharmer, LocalPlayer));
    }

    [Fact]
    public void ObservedPredicate_NpcVictim_False()
    {
        Assert.False(WorldClient.IsObservedPlayerCharmedByPlayer(NpcVictim, Charmer, LocalPlayer));
    }

    // Charm end: CHARMEDBY empties — predicate must go false so the clear is forwarded
    // and the tracking set drops the guid.
    [Fact]
    public void ObservedPredicate_EmptyCharmedBy_False()
    {
        Assert.False(WorldClient.IsObservedPlayerCharmedByPlayer(Victim, WowGuid128.Empty, LocalPlayer));
    }

    // ---- L3: charm-aura suppression (spell-id gated — ordering-proof vs the CHARMEDBY packet) ----

    [Fact]
    public void AuraSuppress_LeverOn_GnomishCapOnObservedPlayer_Suppressed()
    {
        Assert.True(WorldClient.ShouldSuppressCharmAuraForObserver(true, Victim, LocalPlayer, GnomishMindControlCap));
    }

    [Fact]
    public void AuraSuppress_LeverOn_PriestMcOnObservedPlayer_Suppressed()
    {
        Assert.True(WorldClient.ShouldSuppressCharmAuraForObserver(true, Victim, LocalPlayer, PriestMindControl));
    }

    // Lucifron's Dominate Mind: NPC-charmer aura — never suppressed, raid MC is stable content.
    [Fact]
    public void AuraSuppress_LeverOn_DominateMind_NotSuppressed()
    {
        Assert.False(WorldClient.ShouldSuppressCharmAuraForObserver(true, Victim, LocalPlayer, DominateMind));
    }

    [Fact]
    public void AuraSuppress_LeverOn_UnrelatedAura_NotSuppressed()
    {
        Assert.False(WorldClient.ShouldSuppressCharmAuraForObserver(true, Victim, LocalPlayer, UnrelatedAura));
    }

    // Victim path is deliberately untouched (deferred): self keeps its own charm aura.
    [Fact]
    public void AuraSuppress_LeverOn_SelfIsVictim_NotSuppressed()
    {
        Assert.False(WorldClient.ShouldSuppressCharmAuraForObserver(true, LocalPlayer, LocalPlayer, GnomishMindControlCap));
    }

    [Fact]
    public void AuraSuppress_LeverOn_CreatureUnit_NotSuppressed()
    {
        Assert.False(WorldClient.ShouldSuppressCharmAuraForObserver(true, NpcVictim, LocalPlayer, GnomishMindControlCap));
    }

    [Fact]
    public void AuraSuppress_LeverOff_NotSuppressed()
    {
        Assert.False(WorldClient.ShouldSuppressCharmAuraForObserver(false, Victim, LocalPlayer, GnomishMindControlCap));
    }

    // ---- L2: faction hold (hold only when tracked AND a pre-charm faction was seen) ----

    [Fact]
    public void FactionHold_LeverOnTrackedCached_Holds()
    {
        Assert.True(WorldClient.ShouldHoldFactionForCharm(leverOn: true, isTrackedObservedCharm: true, hasCachedFaction: true));
    }

    // No cached pre-charm faction (unit walked into range mid-charm): forward raw — we
    // cannot hold a value we never saw.
    [Fact]
    public void FactionHold_NoCachedFaction_ForwardsRaw()
    {
        Assert.False(WorldClient.ShouldHoldFactionForCharm(leverOn: true, isTrackedObservedCharm: true, hasCachedFaction: false));
    }

    [Fact]
    public void FactionHold_NotTracked_ForwardsRaw()
    {
        Assert.False(WorldClient.ShouldHoldFactionForCharm(leverOn: true, isTrackedObservedCharm: false, hasCachedFaction: true));
    }

    [Fact]
    public void FactionHold_LeverOff_ForwardsRaw()
    {
        Assert.False(WorldClient.ShouldHoldFactionForCharm(leverOn: false, isTrackedObservedCharm: true, hasCachedFaction: true));
    }
}
