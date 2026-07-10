using HermesProxy.World;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (#329/#331/#332 transport-clear wedge): pure decision for whether the deferred
/// transport-clear synth should fire, extracted so the source/destination truth table is
/// unit-testable (see <c>HermesProxy.Tests/World/TransportClearGateTests</c>) and decoupled from
/// the <c>DiagLastObservedPlayerTransportGuid</c> field it happens to read at the call site.
///
/// The fabricated transport-clear (a MoveTeleport with TransportGUID=empty) is only legitimate
/// when the player is leaving a transport whose attach the destination UpdateObject lost. Firing
/// it when the player was observed OFF a transport at the source is the wedge: a mage-teleport /
/// cross-continent / BG-exit transition gets a spurious teleport that leaves the client
/// (especially at EU latency) unable to walk until /reload.
///
/// Truth table (dest = whether the destination UpdateObject is on a transport):
///   source observed empty, dest empty  -> SKIP  (the #329/#331/#332 wedge)
///   source non-empty,       dest empty  -> FIRE  (boat/zep stale-attach clear, dc39c39)
///   source null (unobserved), dest empty -> FIRE  (login gate-release; fail-safe)
///   any source,             dest on transport -> SKIP (destination is legitimately on a transport)
/// </summary>
internal static class TransportClearGate
{
    internal static bool ShouldFire(WowGuid128? sourceTransport, WowGuid128 destTransport)
        => destTransport.IsEmpty()
           && (sourceTransport == null || !sourceTransport.Value.IsEmpty());
}
