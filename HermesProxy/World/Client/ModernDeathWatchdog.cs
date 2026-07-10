namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (gap-A ghost-character teardown): pure decision for whether the modern-side death
/// watchdog (on <see cref="WorldClient"/>'s keepalive tick) should tear the legacy connection down.
///
/// The ghost: when the modern client dies abruptly (hard crash / taskkill / network partition) it
/// never sends CMSG_LOG_DISCONNECT, so nothing disconnects the legacy WorldClient -- it keeps
/// pinging the server forever and the character lingers in-world (raid slot held, heals wasted)
/// until the player relogs. The watchdog notices, on the 30s keepalive tick, that both modern
/// sockets are gone and tears the legacy side down so the server logs the character out.
///
/// Extracted as a pure predicate so the fire/skip truth table is unit-testable (see
/// <c>HermesProxy.Tests/World/ModernDeathWatchdogTests</c>) and decoupled from the socket and
/// session plumbing at the call site.
///
///   wasEverAlive        - both modern sockets have been observed open at least once (guards the
///                         login/handshake window, before the sockets are assigned to the session)
///   modernGone          - both modern sockets are now dead (null, or no longer open)
///   isLogoutIntentional - the player logged out cleanly (CMSG_LOGOUT_REQUEST / CMSG_LOG_DISCONNECT);
///                         this is also the state during a realm/char switch, so it gates out switches
///   isActiveWorldClient - this WorldClient still owns the session slot (not a swapped-out corpse)
/// </summary>
internal static class ModernDeathWatchdog
{
    internal static bool ShouldTearDown(bool wasEverAlive, bool modernGone, bool isLogoutIntentional, bool isActiveWorldClient)
        => wasEverAlive && modernGone && !isLogoutIntentional && isActiveWorldClient;
}
