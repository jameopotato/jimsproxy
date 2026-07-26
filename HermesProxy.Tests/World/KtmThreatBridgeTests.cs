using System.Collections.Generic;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the KTM (KLHThreatMeter) outbound interop — the half that makes our
// engine's threat number win on the 1.12 "KLHTM" wire when a 1.14 player also
// runs a KTM addon. Two surfaces:
//   RewriteOutbound            — the pure string wire-rewrite contract
//   ComputeKtmBroadcastThreat  — the value source (target selection / floor /
//                                untracked-pair lookup) the rewrite is fed. The
//                                engine-off / battleground gate lives in the
//                                GetKtmBroadcastThreat caller via the shared
//                                ThreatDisabled property, exercised elsewhere.
public class KtmThreatBridgeTests
{
    // ---- RewriteOutbound: the wire-rewrite contract ----------------------

    [Fact]
    public void RewriteOutbound_ThreatLine_ReplacedWithOurNumber()
    {
        Assert.Equal("t 12345", KtmThreatBridge.RewriteOutbound("KLHTM", "t 5000", 12345));
    }

    [Fact]
    public void RewriteOutbound_AddonReportsZero_OurPositiveNumberStillWins()
    {
        // The addon broadcasting "t 0" (its own estimate hasn't spun up) must
        // not stop our real number reaching the raid — our data wins.
        Assert.Equal("t 8200", KtmThreatBridge.RewriteOutbound("KLHTM", "t 0", 8200));
    }

    [Theory]
    // Officer coordination grammar — never a value we own; must pass through
    // even though the engine has a live number to broadcast.
    [InlineData("target Thrall")]
    [InlineData("cleartarget")]
    [InlineData("clear")]
    public void RewriteOutbound_OfficerCommand_PassesThrough(string body)
    {
        Assert.Equal(body, KtmThreatBridge.RewriteOutbound("KLHTM", body, 12345));
    }

    [Theory]
    // A PallyPower / HealComm / DBM body that happens to look like a threat
    // line must be untouched — we only own the KLHTM prefix.
    [InlineData("PLPWR")]
    [InlineData("LHC40")]
    [InlineData("D4")]
    public void RewriteOutbound_NonKtmPrefix_PassesThrough(string prefix)
    {
        Assert.Equal("t 5000", KtmThreatBridge.RewriteOutbound(prefix, "t 5000", 12345));
    }

    [Fact]
    public void RewriteOutbound_NoData_PassesAddonNumberThrough()
    {
        // ourThreat == 0 means engine-off / no-data: defer to the addon's own
        // number rather than blanking the raider's meter with a 0.
        Assert.Equal("t 5000", KtmThreatBridge.RewriteOutbound("KLHTM", "t 5000", 0));
    }

    [Theory]
    // "t" without the trailing space is the "target" family, not a threat line;
    // anything whose value token isn't a bare non-negative integer is a shape we
    // don't fully understand and must not mangle.
    [InlineData("t")]
    [InlineData("t abc")]
    [InlineData("t 50 60")]
    [InlineData("t -5")]
    [InlineData("")]
    public void RewriteOutbound_MalformedOrNonThreat_PassesThrough(string body)
    {
        Assert.Equal(body, KtmThreatBridge.RewriteOutbound("KLHTM", body, 12345));
    }

    [Fact]
    public void RewriteOutbound_ThreatLineTrailingSpace_NormalizesAndRewrites()
    {
        // AceComm shouldn't frame short messages, but tolerate a trailing space
        // defensively — the value token is trimmed and we emit the canonical form.
        Assert.Equal("t 12345", KtmThreatBridge.RewriteOutbound("KLHTM", "t 5000 ", 12345));
    }

    // ---- ComputeKtmBroadcastThreat: target selection / floor / lookup -----

    // Distinct player-type GUIDs; the lookup keys the threat list by GUID and
    // never inspects the high-guid type, so a second player-type GUID stands in
    // for the targeted mob without needing a creature high-guid enum.
    private static WowGuid128 Guid(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Player, 0, 0, counter);

    private static Dictionary<WowGuid128, Dictionary<WowGuid128, double>> Lists(
        WowGuid128 mob, WowGuid128 threater, double threat) =>
        new() { [mob] = new() { [threater] = threat } };

    [Fact]
    public void ComputeKtmBroadcastThreat_TargetTracked_ReturnsFlooredThreat()
    {
        var player = Guid(1);
        var mob = Guid(100);
        // floor(), not round() — KTM's getThreatStringKTM floors.
        Assert.Equal(5000, ThreatTracker.ComputeKtmBroadcastThreat(Lists(mob, player, 5000.9), mob, player));
    }

    [Fact]
    public void ComputeKtmBroadcastThreat_NoTarget_ReturnsZero()
    {
        var player = Guid(1);
        var mob = Guid(100);
        // Player has threat on the mob, but isn't targeting anything — KTM only
        // broadcasts current-target threat.
        Assert.Equal(0, ThreatTracker.ComputeKtmBroadcastThreat(Lists(mob, player, 5000.0), WowGuid128.Empty, player));
    }

    [Fact]
    public void ComputeKtmBroadcastThreat_TargetWeAreNotTracking_ReturnsZero()
    {
        var player = Guid(1);
        var trackedMob = Guid(100);
        var otherMob = Guid(200);
        // Targeting a mob we hold no threat list for.
        Assert.Equal(0, ThreatTracker.ComputeKtmBroadcastThreat(Lists(trackedMob, player, 5000.0), otherMob, player));
    }

    [Fact]
    public void ComputeKtmBroadcastThreat_PlayerNotOnTargetList_ReturnsZero()
    {
        var player = Guid(1);
        var otherRaider = Guid(2);
        var mob = Guid(100);
        // We track the mob, but only another raider's threat — not the local player's.
        Assert.Equal(0, ThreatTracker.ComputeKtmBroadcastThreat(Lists(mob, otherRaider, 5000.0), mob, player));
    }

    [Fact]
    public void ComputeKtmBroadcastThreat_ZeroThreat_ReturnsZero()
    {
        var player = Guid(1);
        var mob = Guid(100);
        // A tracked-but-zero entry is "no data", not a broadcastable value.
        Assert.Equal(0, ThreatTracker.ComputeKtmBroadcastThreat(Lists(mob, player, 0.0), mob, player));
    }
}
