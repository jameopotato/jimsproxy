using HermesProxy.World;
using System.Collections.Generic;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (flight refused while mounted): the 1.12 server refuses a taxi activation from a mounted
// player and the 1.14 client never dismounts for a flight, so the proxy cancels the mount aura before
// forwarding the activation. These tests lock in the selection of mount auras from the aura slots
// that both the CMSG_CANCEL_MOUNT_AURA translation and the taxi path rely on.
public class TaxiDismountBeforeActivateTests
{
    private static readonly HashSet<uint> Mounts = new() { 6898, 458, 23227 };

    [Fact]
    public void MountAurasAmong_MountPresent_ReturnsIt()
    {
        var slots = new uint[] { 1243, 6898, 21562 };

        var result = GameSessionData.MountAurasAmong(slots, Mounts.Contains);

        Assert.Equal(new List<uint> { 6898 }, result);
    }

    [Fact]
    public void MountAurasAmong_NoMount_Empty()
    {
        var slots = new uint[] { 1243, 21562, 13165 };

        var result = GameSessionData.MountAurasAmong(slots, Mounts.Contains);

        Assert.Empty(result);
    }

    [Fact]
    public void MountAurasAmong_NoAuras_Empty()
    {
        var result = GameSessionData.MountAurasAmong(new uint[0], Mounts.Contains);

        Assert.Empty(result);
    }

    [Fact]
    public void MountAurasAmong_TwoMountAuras_BothInSlotOrder()
    {
        // Two mount auras in the slots (a stale slot and the live one): both are cancelled, as the
        // CMSG_CANCEL_MOUNT_AURA translation always did.
        var slots = new uint[] { 23227, 1243, 6898 };

        var result = GameSessionData.MountAurasAmong(slots, Mounts.Contains);

        Assert.Equal(new List<uint> { 23227, 6898 }, result);
    }
}
