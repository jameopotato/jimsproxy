using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (camp instance-reset stun lock, step 2): pre-create self-op hold.
// The wedge login's self-create is server-stalled, so the arrival control ops
// (ROOT/CONTROL_UPDATE/UNROOT) reach the client before the player object exists and
// the client constructs it input-locked (wire-proven: the wedge login is the only
// corpus entry with ops-before-create; all healthy logins/transfers are
// creates-first). The hold captures self ops from login-verify until the first self
// create block forwards, then releases them in arrival order.
public class PreCreateOpHoldTests
{
    static PreCreateOpHoldTests()
    {
        // ServerPacket construction resolves opcodes through ModernVersion, whose
        // static ctor needs a real build — same guard every packet-constructing
        // test class uses so the class also runs green in isolation.
        if (global::Framework.Settings.ClientBuild == HermesProxy.Enums.ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = HermesProxy.Enums.ClientVersionBuild.V1_14_2_42597;
    }

    static PreCreateOpHold ArmedHold()
    {
        var hold = new PreCreateOpHold();
        hold.Arm(nowTick: 1000);
        return hold;
    }

    static ServerPacket Root() => new MoveSetFlag(Opcode.SMSG_MOVE_ROOT);
    static ServerPacket Unroot() => new MoveSetFlag(Opcode.SMSG_MOVE_UNROOT);
    static ServerPacket Ctl() => new ControlUpdate();

    // --- arming gate ---

    [Fact]
    public void ShouldArm_FreshLogin_Arms()
    {
        Assert.True(PreCreateOpHold.ShouldArm(enabled: true, alreadyInWorld: false));
    }

    // A seamless-reconnect verify arrives with the client already in world — its
    // player object is already constructed; there is no ordering to protect.
    [Fact]
    public void ShouldArm_AlreadyInWorld_DoesNotArm()
    {
        Assert.False(PreCreateOpHold.ShouldArm(enabled: true, alreadyInWorld: true));
    }

    [Fact]
    public void ShouldArm_KillSwitchOff_DoesNotArm()
    {
        Assert.False(PreCreateOpHold.ShouldArm(enabled: false, alreadyInWorld: false));
    }

    // --- capture ---

    [Fact]
    public void TryCapture_Inactive_PassesThrough()
    {
        var hold = new PreCreateOpHold();
        Assert.False(hold.TryCapture(Root()));
        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
    }

    // The wedge shape: root, control-update, unroot land before the create; the
    // release hands them back in exactly that arrival order.
    [Fact]
    public void TakeForRelease_WedgeOrdering_ReleasesInArrivalOrder()
    {
        var hold = ArmedHold();
        var root1 = Root();
        var ctl1 = Ctl();
        var unroot = Unroot();
        Assert.True(hold.TryCapture(root1));
        Assert.True(hold.TryCapture(ctl1));
        Assert.True(hold.TryCapture(unroot));

        hold.NoteSelfCreateForwarding();
        var released = hold.TakeForRelease();

        Assert.NotNull(released);
        Assert.Equal(3, released.Count);
        Assert.Same(root1, released[0]);
        Assert.Same(ctl1, released[1]);
        Assert.Same(unroot, released[2]);
        // Fully disarmed: post-create ops pass straight through, nothing double-releases.
        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
        Assert.False(hold.TryCapture(Root()));
        Assert.Null(hold.TakeForRelease());
        Assert.Null(hold.ReleaseAll());
    }

    // The healthy shape: armed, but every op arrived after the create — empty
    // release (the caller emits no event for it).
    [Fact]
    public void TakeForRelease_HealthyLogin_EmptyRelease()
    {
        var hold = ArmedHold();
        hold.NoteSelfCreateForwarding();

        var released = hold.TakeForRelease();

        Assert.NotNull(released);
        Assert.Empty(released);
        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
    }

    // Release strictly requires the self create — an armed hold never leaks its ops
    // on a bare Take (no timers, packet-driven only).
    [Fact]
    public void TakeForRelease_NoSelfCreateSeen_ReturnsNull()
    {
        var hold = ArmedHold();
        Assert.True(hold.TryCapture(Root()));

        Assert.Null(hold.TakeForRelease());

        Assert.Equal(PreCreateOpHold.HoldPhase.Armed, hold.Phase);
        Assert.Equal(1, hold.HeldCount);
    }

    // Mid-session self creates (far teleports) arrive with the hold long inactive.
    [Fact]
    public void NoteSelfCreateForwarding_Inactive_NoOp()
    {
        var hold = new PreCreateOpHold();
        hold.NoteSelfCreateForwarding();
        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
        Assert.Null(hold.TakeForRelease());
    }

    // Ops cannot be captured between the create's mark and its end-of-packet flush
    // (single-threaded legacy processing makes this unreachable; the phase check
    // keeps the semantics honest anyway).
    [Fact]
    public void TryCapture_ReleasePending_PassesThrough()
    {
        var hold = ArmedHold();
        hold.NoteSelfCreateForwarding();
        Assert.False(hold.TryCapture(Root()));
    }

    // --- fail-open ---

    [Theory]
    [InlineData(false)] // legacy disconnect while armed
    [InlineData(true)]  // legacy disconnect after the create marked but before the flush
    public void ReleaseAll_ArmedPhases_ReturnsHeldOps(bool selfCreateSeen)
    {
        var hold = ArmedHold();
        var root = Root();
        Assert.True(hold.TryCapture(root));
        if (selfCreateSeen)
            hold.NoteSelfCreateForwarding();

        var released = hold.ReleaseAll();

        Assert.NotNull(released);
        Assert.Same(root, released[0]);
        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
    }

    [Fact]
    public void ReleaseAll_Inactive_ReturnsNull()
    {
        Assert.Null(new PreCreateOpHold().ReleaseAll());
    }

    // --- re-arm (next login on the same GameSessionData lifetime is impossible
    // today — fresh instance per login — but Arm must still start clean) ---

    [Fact]
    public void Arm_AfterPreviousCycle_StartsClean()
    {
        var hold = ArmedHold();
        Assert.True(hold.TryCapture(Root()));
        hold.NoteSelfCreateForwarding();
        Assert.NotNull(hold.TakeForRelease());

        hold.Arm(nowTick: 2000);

        Assert.Equal(PreCreateOpHold.HoldPhase.Armed, hold.Phase);
        Assert.Equal(0, hold.HeldCount);
        Assert.Equal(2000, hold.ArmTick);
    }

    // --- P1-2 (D-06): self synths built while the self create is translated ---

    static ServerPacket Teleport() => new MoveTeleport();
    static ServerPacket RunSpeed() => new MoveSetSpeed(Opcode.SMSG_MOVE_SET_RUN_SPEED);

    // Login: the synth is deferred while the hold is still armed (it is built in the create's
    // movement block, before the create is marked). It waits for the create, then follows
    // every held op: emission order.
    [Fact]
    public void DeferPastSelfCreate_Armed_FollowsTheHeldOpsAfterTheCreate()
    {
        var hold = ArmedHold();
        var root = Root();
        var teleport = Teleport();
        Assert.True(hold.TryCapture(root));

        hold.DeferPastSelfCreate(teleport);
        Assert.Null(hold.TakeDeferredForRelease()); // create not marked yet

        hold.NoteSelfCreateForwarding();
        Assert.Null(hold.TakeDeferredForRelease()); // held ops not released yet
        var ops = hold.TakeForRelease();
        var synths = hold.TakeDeferredForRelease();

        Assert.Same(root, Assert.Single(ops!));
        Assert.Same(teleport, Assert.Single(synths!));
        Assert.Equal(0, hold.DeferredCount);
        Assert.Null(hold.TakeDeferredForRelease()); // released once
    }

    // World port (the hold is long inactive): released at the end of the same update, in the
    // order deferred, and the control-op hold itself is not touched.
    [Fact]
    public void DeferPastSelfCreate_Inactive_ReleasedAtTheEndOfTheUpdate_HoldUntouched()
    {
        var hold = new PreCreateOpHold();
        var teleport = Teleport();
        var speed = RunSpeed();

        hold.DeferPastSelfCreate(teleport);
        hold.DeferPastSelfCreate(speed);

        Assert.Equal(PreCreateOpHold.HoldPhase.Inactive, hold.Phase);
        Assert.False(hold.TryCapture(Root()));
        Assert.Null(hold.TakeForRelease());
        var synths = hold.TakeDeferredForRelease();
        Assert.NotNull(synths);
        Assert.Equal(new[] { teleport, speed }, synths);
    }

    // Deferring never feeds the held list, so a non-empty TakeForRelease (the always-on wedge
    // event) still means held control ops only.
    [Fact]
    public void DeferPastSelfCreate_DoesNotCountAsAHeldOp()
    {
        var hold = ArmedHold();
        hold.DeferPastSelfCreate(Teleport());
        hold.NoteSelfCreateForwarding();

        var ops = hold.TakeForRelease();

        Assert.NotNull(ops);
        Assert.Empty(ops);
        Assert.Equal(0, hold.HeldCount);
        Assert.Equal(1, hold.DeferredCount);
    }

    // Nothing deferred: nothing to release on any update.
    [Fact]
    public void TakeDeferredForRelease_NothingDeferred_ReturnsNull()
    {
        Assert.Null(new PreCreateOpHold().TakeDeferredForRelease());
    }

    // Login failed / legacy disconnect: the proxy's own synths are dropped, not flushed, in
    // every phase; the held ops keep their fail-open release.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseAll_DropsDeferredSynths(bool armed)
    {
        var hold = armed ? ArmedHold() : new PreCreateOpHold();
        var root = Root();
        if (armed)
            Assert.True(hold.TryCapture(root));
        hold.DeferPastSelfCreate(Teleport());

        var released = hold.ReleaseAll();

        if (armed)
            Assert.Same(root, Assert.Single(released!));
        else
            Assert.Null(released);
        Assert.Equal(0, hold.DeferredCount);
        Assert.Null(hold.TakeDeferredForRelease());
    }

    [Fact]
    public void Arm_DropsStaleDeferredSynths()
    {
        var hold = new PreCreateOpHold();
        hold.DeferPastSelfCreate(Teleport());

        hold.Arm(nowTick: 3000);

        Assert.Equal(0, hold.DeferredCount);
    }
}
