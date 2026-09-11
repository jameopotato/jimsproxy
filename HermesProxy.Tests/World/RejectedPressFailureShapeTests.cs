using System;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (stuck action button, RE round 15, 2026-09-08): a CAST_FAILED answering a client
// press must carry the id the 1.14 client's cast object is keyed by at that moment. The client
// re-keys the object from its own cast id to the server's when it receives SpellPrepare, which
// the proxy sends at SPELL_START (on-GCD) or at forward time (off-GCD). A press that was never
// re-keyed must fail on the CLIENT id with no PREPARE: re-keying it only to fail it on the
// server id left the object pinned in its casting state with the action button lit until relog
// (three live PTR specimens, about one in ten rejected heal-spam frames). All four proxy
// emitters that fail a pending press (WorldSocket.SendCastRequestFailed, the SMSG_CAST_FAILED
// handler, and the destroy and watchdog evictions in GlobalSessionData) route through
// FailureCastId and NeedsPrepareBeforeFailure, so pinning them pins the wire shape.
public class RejectedPressFailureShapeTests
{
    private static readonly WowGuid128 ClientId = new WowGuid128(5, 0xBC0000000005EC02);
    private static readonly WowGuid128 ServerId = new WowGuid128(10005, 0xBC0004000005EC03);

    private static ClientCastRequest MakeCast(bool started = false, bool prepareSent = false)
    {
        return new ClientCastRequest
        {
            SpellId = 6064, // Heal rank 4, the first live specimen
            Timestamp = Environment.TickCount,
            HasStarted = started,
            HasSentPrepare = prepareSent,
            ClientGUID = ClientId,
            ServerGUID = ServerId,
        };
    }

    [Fact]
    public void NeverStartedNeverPrepared_FailsOnClientId()
    {
        // The specimen: press A rejected with SpellInProgress before any START. The client still
        // holds the object under the client id, so that is the id the failure must carry.
        var press = MakeCast();

        Assert.False(press.PrepareSentToClient);
        Assert.Equal(ClientId, press.FailureCastId);
        Assert.NotEqual(ServerId, press.FailureCastId);
    }

    [Fact]
    public void Started_FailsOnServerId()
    {
        // A real failure of a cast that STARTED: the client was re-keyed by the START-time
        // PREPARE, so the failure keeps the server id (unchanged behaviour).
        var cast = MakeCast(started: true);

        Assert.True(cast.PrepareSentToClient);
        Assert.Equal(ServerId, cast.FailureCastId);
    }

    [Fact]
    public void OffGcdPreparedAtForward_FailsOnServerId()
    {
        // An off-GCD press gets its PREPARE at forward time, before any START. Its client object
        // is already keyed by the server id, so a client-id failure would miss it and strand the
        // button. It must keep the server id even though it never started.
        var press = MakeCast(started: false, prepareSent: true);

        Assert.True(press.PrepareSentToClient);
        Assert.Equal(ServerId, press.FailureCastId);
    }

    [Fact]
    public void StartedAndPrepared_FailsOnServerId()
    {
        var cast = MakeCast(started: true, prepareSent: true);

        Assert.True(cast.PrepareSentToClient);
        Assert.Equal(ServerId, cast.FailureCastId);
    }

    [Fact]
    public void ForwardTimePrepareFlipsTheRule()
    {
        // The same press before and after the off-GCD forward path marks its PREPARE sent.
        var press = MakeCast();
        Assert.Equal(ClientId, press.FailureCastId);

        press.HasSentPrepare = true;

        Assert.Equal(ServerId, press.FailureCastId);
    }

    // === Eviction emitters (destroy eviction, watchdog eviction) share the shape ===
    // Both used to send a PREPARE for any not-started press and then fail it on the server id,
    // the exact stranding shape. They now read the same two members as the other emitters.

    [Fact]
    public void Evicted_NeverPrepared_GetsNoPrepareAndClientId()
    {
        // A never-started press whose target was destroyed, or whose SPELL_FAILURE armed the
        // watchdog and whose trailing CAST_FAILED never came: the client still holds it under
        // the client id, so no PREPARE and the client id on the failure.
        var press = MakeCast();

        Assert.False(press.NeedsPrepareBeforeFailure);
        Assert.Equal(ClientId, press.FailureCastId);
    }

    [Fact]
    public void Evicted_OffGcdPrepared_KeepsRepeatPrepareAndServerId()
    {
        // An off-GCD press was re-keyed at forward time: the emitter repeats the PREPARE (the
        // shape that has always been used for it) and fails on the server id.
        var press = MakeCast(started: false, prepareSent: true);

        Assert.True(press.NeedsPrepareBeforeFailure);
        Assert.Equal(ServerId, press.FailureCastId);
    }

    [Fact]
    public void Evicted_Started_GetsNoPrepareAndServerId()
    {
        // A started cast was re-keyed by the START-time PREPARE; the watchdog force-close must
        // not repeat it and must fail on the server id the FIFO release above it also uses.
        var cast = MakeCast(started: true, prepareSent: true);

        Assert.False(cast.NeedsPrepareBeforeFailure);
        Assert.Equal(ServerId, cast.FailureCastId);
    }
}
