using System.Runtime.CompilerServices;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#450): the kill tombstone that stops combat events trailing a mob's own
// SMSG_PARTY_KILL_LOG (Kronos sends the melee killing blow's ATTACKER_STATE_UPDATE after
// the kill log) from re-creating the dead mob's threat list. In the #450 capture the
// trailing 215-damage hit re-added the player at threat 172 on the corpse and emitted
// THREAT_UPDATE + HIGHEST_THREAT_UPDATE for a dead unit. Contract:
//   OnMobKilled            — kill-log clear arms the tombstone (plain ClearMob must NOT:
//                            evaded mobs re-enter combat immediately)
//   AddThreat/SetThreat/SetToTop — no-ops while tombstoned
//   any lifecycle signal for the guid — combat-state observation (either edge), destroy,
//                            leave-combat wipe, Reset — disarms, so a respawn reusing the
//                            same creature guid is never blocked
public class ThreatDeadMobTombstoneTests
{
    private static readonly WowGuid128 Mob = new(0xF5C, 0x2000040000005400);
    private static readonly WowGuid128 Player = new(0x100, 0x0800040000000000);

    private static ThreatTracker NewTracker()
    {
        // Uninitialized session (same reflection shape as WowGuidTestHelper): the paths
        // under test are pure list/set operations that never touch the session. Tests
        // arm/clear on mobs WITHOUT a live threat list so ClearMob early-returns before
        // constructing a ThreatClearPkt — ServerPacket's ctor needs the ModernVersion
        // static state a test process doesn't have. The tombstone gate itself is
        // list-independent (a bare Contains check), so coverage is unchanged.
        var session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
        return new ThreatTracker(session);
    }

    [Fact]
    public void OnMobKilled_TrailingDamage_DoesNotResurrectThreatList()
    {
        // The #450 wire shape: kill log clears, the trailing killing-blow ASU tries to re-add.
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.AddThreat(Mob, Player, 215);

        Assert.True(tracker.IsMobKillTombstoned(Mob));
        Assert.False(tracker.HasThreatList(Mob));
    }

    [Fact]
    public void OnMobKilled_SetThreatAndSetToTop_AlsoBlocked()
    {
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.SetThreat(Mob, Player, 500);
        tracker.SetToTop(Mob, Player);

        Assert.False(tracker.HasThreatList(Mob));
    }

    [Fact]
    public void ClearMob_PlainEvadeClear_DoesNotTombstone()
    {
        // Evade/leash clear: the mob is alive and can be re-engaged instantly — threat
        // must flow again with no lifecycle signal in between.
        var tracker = NewTracker();
        tracker.ClearMob(Mob);

        tracker.AddThreat(Mob, Player, 50);

        Assert.False(tracker.IsMobKillTombstoned(Mob));
        Assert.True(tracker.HasThreatList(Mob));
    }

    [Fact]
    public void OnUnitCombatStateObserved_DisarmsTombstone()
    {
        // The death values-update (~20ms after the kill log) carries the combat-flag
        // observation — the tombstone's natural expiry. A respawned mob re-engaging
        // (flag set) takes the same path.
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.OnUnitCombatStateObserved(Mob, inCombat: true);

        Assert.False(tracker.IsMobKillTombstoned(Mob));
        tracker.AddThreat(Mob, Player, 50);
        Assert.True(tracker.HasThreatList(Mob));
    }

    [Fact]
    public void OnUnitDestroyed_DisarmsTombstone()
    {
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.OnUnitDestroyed(Mob);

        Assert.False(tracker.IsMobKillTombstoned(Mob));
    }

    [Fact]
    public void OnLocalPlayerLeftCombat_DisarmsAllTombstones()
    {
        // Leave-combat wipe with no live lists: must still clear the tombstones (the
        // early-return on empty lists sits after the tombstone wipe by design).
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.OnLocalPlayerLeftCombat();

        Assert.False(tracker.IsMobKillTombstoned(Mob));
    }

    [Fact]
    public void Reset_DisarmsAllTombstones()
    {
        var tracker = NewTracker();
        tracker.OnMobKilled(Mob);

        tracker.Reset();

        Assert.False(tracker.IsMobKillTombstoned(Mob));
    }
}
