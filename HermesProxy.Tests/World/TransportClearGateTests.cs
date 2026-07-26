using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#329/#331/#332): the deferred transport-clear source/destination truth table.
/// See <see cref="TransportClearGate"/>.
/// </summary>
public class TransportClearGateTests
{
    private static WowGuid128 Transport() => WowGuid128.Create(HighGuidType703.Transport, 1);

    // The wedge (#329/#331/#332): player was observed OFF a transport at the source and the
    // destination is also off a transport -> the fabricated transport-clear must NOT fire
    // (this is the case the fix newly suppresses; it previously fired and wedged movement).
    [Fact]
    public void SourceOffTransport_DestOffTransport_DoesNotFire()
    {
        Assert.False(TransportClearGate.ShouldFire(WowGuid128.Empty, WowGuid128.Empty));
    }

    // Boat/zep stale-attach clear (dc39c39): player carried a transport at the source but the
    // destination UpdateObject lost the attach -> fire.
    [Fact]
    public void SourceOnTransport_DestOffTransport_Fires()
    {
        Assert.True(TransportClearGate.ShouldFire(Transport(), WowGuid128.Empty));
    }

    // Login gate-release: source transport state unobserved (null) -> fail-safe fire.
    [Fact]
    public void SourceUnobserved_DestOffTransport_Fires()
    {
        Assert.True(TransportClearGate.ShouldFire(null, WowGuid128.Empty));
    }

    // Destination legitimately on a transport (zep tower / boat deck / mid-flight) -> never fire,
    // regardless of source state (this was already the behavior before the fix).
    [Fact]
    public void DestOnTransport_NeverFires()
    {
        Assert.False(TransportClearGate.ShouldFire(WowGuid128.Empty, Transport()));
        Assert.False(TransportClearGate.ShouldFire(Transport(), Transport()));
        Assert.False(TransportClearGate.ShouldFire(null, Transport()));
    }
}
