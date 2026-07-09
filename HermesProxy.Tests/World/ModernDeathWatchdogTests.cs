using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (gap-A ghost-character teardown): the modern-death watchdog fire/skip truth table.
/// See <see cref="ModernDeathWatchdog"/>.
/// </summary>
public class ModernDeathWatchdogTests
{
    // The one case that fires: we saw the client alive, both its sockets are now dead, it was not an
    // intentional logout, and this WorldClient still owns the slot -> the client died abruptly, tear down.
    [Fact]
    public void SeenAlive_BothSocketsDead_NotIntentional_Active_Fires()
    {
        Assert.True(ModernDeathWatchdog.ShouldTearDown(
            wasEverAlive: true, modernGone: true, isLogoutIntentional: false, isActiveWorldClient: true));
    }

    // Never observed alive yet (login/handshake window, before the sockets are assigned) -> not a death.
    [Fact]
    public void NeverSeenAlive_DoesNotFire()
    {
        Assert.False(ModernDeathWatchdog.ShouldTearDown(
            wasEverAlive: false, modernGone: true, isLogoutIntentional: false, isActiveWorldClient: true));
    }

    // A clean logout / realm-or-char switch sets IsLogoutIntentional -> never treated as a death.
    // This is the swap-safety gate.
    [Fact]
    public void IntentionalLogout_DoesNotFire()
    {
        Assert.False(ModernDeathWatchdog.ShouldTearDown(
            wasEverAlive: true, modernGone: true, isLogoutIntentional: true, isActiveWorldClient: true));
    }

    // A swapped-out corpse WorldClient no longer owns the session slot -> it must not tear the session down.
    [Fact]
    public void NotActiveWorldClient_DoesNotFire()
    {
        Assert.False(ModernDeathWatchdog.ShouldTearDown(
            wasEverAlive: true, modernGone: true, isLogoutIntentional: false, isActiveWorldClient: false));
    }

    // Modern sockets still alive -> nothing to do.
    [Fact]
    public void ModernStillAlive_DoesNotFire()
    {
        Assert.False(ModernDeathWatchdog.ShouldTearDown(
            wasEverAlive: true, modernGone: false, isLogoutIntentional: false, isActiveWorldClient: true));
    }
}
