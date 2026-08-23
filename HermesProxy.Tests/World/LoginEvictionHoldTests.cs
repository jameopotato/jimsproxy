using System.Numerics;
using HermesProxy.World.Client;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (camp login-eviction merge): state machine for the instanced-login
// hold-and-merge. The over-cap eviction two-step (LOGIN_VERIFY into the instance,
// TRANSFER_PENDING + NEW_WORLD out ~10ms later) permanently hangs the 1.14 client's
// loading screen; the hold captures the client-facing login stream and either merges
// the eviction into ONE clean login (rewriting the held login-verify to the
// NEW_WORLD destination) or releases unmodified on the first UPDATE_OBJECT.
public class LoginEvictionHoldTests
{
    static LoginEvictionHoldTests()
    {
        // ServerPacket construction resolves opcodes through ModernVersion, whose
        // static ctor needs a real build — same guard every packet-constructing
        // test class uses so the class also runs green in isolation.
        if (global::Framework.Settings.ClientBuild == HermesProxy.Enums.ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = HermesProxy.Enums.ClientVersionBuild.V1_14_2_42597;
    }

    const uint Deadmines = 36;
    const uint Maraudon = 349;
    const uint EasternKingdoms = 0;
    const uint Kalimdor = 1;

    static LoginVerifyWorld MakeVerify(uint mapId, float x = -14.57f, float y = -385.48f, float z = 18.0f, float o = 1.5f)
    {
        var verify = new LoginVerifyWorld();
        verify.MapID = mapId;
        verify.Pos.X = x;
        verify.Pos.Y = y;
        verify.Pos.Z = z;
        verify.Pos.Orientation = o;
        return verify;
    }

    static LoginEvictionHold BeginHold(LoginVerifyWorld verify)
    {
        var hold = new LoginEvictionHold();
        hold.Begin(verify, nowTick: 1000);
        Assert.True(hold.TryEnqueue(verify));
        return hold;
    }

    // --- arming gate ---

    [Theory]
    [InlineData(Deadmines)]
    [InlineData(Maraudon)]
    public void ShouldBegin_InstancedMapFreshLogin_Arms(uint mapId)
    {
        Assert.True(LoginEvictionHold.ShouldBegin(enabled: true, alreadyInWorld: false, mapId));
    }

    // Continent logins can't be the doomed half of an eviction; holding them would
    // tax every ordinary login.
    [Theory]
    [InlineData(EasternKingdoms)]
    [InlineData(Kalimdor)]
    public void ShouldBegin_ContinentLogin_DoesNotArm(uint mapId)
    {
        Assert.False(LoginEvictionHold.ShouldBegin(enabled: true, alreadyInWorld: false, mapId));
    }

    // A seamless-reconnect login-verify arrives with the client already in world —
    // the hold must never touch that path.
    [Fact]
    public void ShouldBegin_AlreadyInWorld_DoesNotArm()
    {
        Assert.False(LoginEvictionHold.ShouldBegin(enabled: true, alreadyInWorld: true, Deadmines));
    }

    [Fact]
    public void ShouldBegin_KillSwitchOff_DoesNotArm()
    {
        Assert.False(LoginEvictionHold.ShouldBegin(enabled: false, alreadyInWorld: false, Deadmines));
    }

    // --- queueing ---

    [Fact]
    public void TryEnqueue_Inactive_PassesThrough()
    {
        var hold = new LoginEvictionHold();
        Assert.False(hold.TryEnqueue(new LoginVerifyWorld()));
        Assert.Equal(LoginEvictionHold.HoldPhase.Inactive, hold.Phase);
    }

    [Fact]
    public void TryReleaseOnFirstUpdateObject_HealthyLogin_ReleasesInArrivalOrder()
    {
        var verify = MakeVerify(Deadmines);
        var hold = BeginHold(verify);
        var second = new LoginVerifyWorld();
        var third = new LoginVerifyWorld();
        Assert.True(hold.TryEnqueue(second));
        Assert.True(hold.TryEnqueue(third));

        var released = hold.TryReleaseOnFirstUpdateObject();

        Assert.NotNull(released);
        Assert.Equal(3, released.Count);
        Assert.Same(verify, released[0]);
        Assert.Same(second, released[1]);
        Assert.Same(third, released[2]);
        // The healthy login is untouched — same map, same position.
        Assert.Equal(Deadmines, verify.MapID);
        Assert.Equal(-14.57f, verify.Pos.X);
        // Fully disarmed: later packets send directly, nothing releases twice.
        Assert.Equal(LoginEvictionHold.HoldPhase.Inactive, hold.Phase);
        Assert.False(hold.TryEnqueue(new LoginVerifyWorld()));
        Assert.Null(hold.TryReleaseOnFirstUpdateObject());
        Assert.Null(hold.TryReleaseAll());
    }

    // --- the eviction path ---

    [Fact]
    public void OnTransferPending_Inactive_PassesThrough()
    {
        var hold = new LoginEvictionHold();
        Assert.False(hold.OnTransferPending(EasternKingdoms));
    }

    [Fact]
    public void OnTransferPending_WhileHolding_SwallowsAndBlocksUpdateRelease()
    {
        var hold = BeginHold(MakeVerify(Deadmines));

        Assert.True(hold.OnTransferPending(EasternKingdoms));
        Assert.Equal(LoginEvictionHold.HoldPhase.TransferSeen, hold.Phase);
        Assert.Equal(EasternKingdoms, hold.PendingDestinationMapId);
        // Once the transfer announced itself it wins — an update object must NOT
        // release the doomed instance load the hold exists to prevent.
        Assert.Null(hold.TryReleaseOnFirstUpdateObject());
        Assert.Equal(LoginEvictionHold.HoldPhase.TransferSeen, hold.Phase);
    }

    [Fact]
    public void TryMergeOnNewWorld_Eviction_RewritesLoginToDestinationAndReleases()
    {
        var verify = MakeVerify(Deadmines);
        var hold = BeginHold(verify);
        var serverInfo = new WorldServerInfo { DifficultyID = 1, InstanceGroupSize = 5 };
        hold.RegisterWorldServerInfo(serverInfo);
        Assert.True(hold.TryEnqueue(serverInfo));
        var filler = new LoginVerifyWorld();
        Assert.True(hold.TryEnqueue(filler));
        Assert.True(hold.OnTransferPending(EasternKingdoms));

        var merged = hold.TryMergeOnNewWorld(EasternKingdoms, new Vector3(-11209.8f, 1666.4f, 24.7f), 3.1f);

        Assert.NotNull(merged);
        Assert.Equal(3, merged.Count);
        Assert.Same(verify, merged[0]);
        Assert.Same(serverInfo, merged[1]);
        Assert.Same(filler, merged[2]);
        // The held login-verify now IS the eviction destination (payload-driven).
        Assert.Equal(EasternKingdoms, verify.MapID);
        Assert.Equal(-11209.8f, verify.Pos.X);
        Assert.Equal(1666.4f, verify.Pos.Y);
        Assert.Equal(24.7f, verify.Pos.Z);
        Assert.Equal(3.1f, verify.Pos.Orientation);
        // Continent destination — instance difficulty fields cleared.
        Assert.Equal(0u, serverInfo.DifficultyID);
        Assert.Null(serverInfo.InstanceGroupSize);
        Assert.Equal(LoginEvictionHold.HoldPhase.Inactive, hold.Phase);
        Assert.False(hold.TryEnqueue(new LoginVerifyWorld()));
    }

    // A transfer at login into ANOTHER instanced map (generic transfer-at-login,
    // not the cap eviction) keeps instance difficulty fields.
    [Fact]
    public void TryMergeOnNewWorld_InstancedDestination_KeepsDifficultyFields()
    {
        var verify = MakeVerify(Deadmines);
        var hold = BeginHold(verify);
        var serverInfo = new WorldServerInfo { DifficultyID = 1, InstanceGroupSize = 5 };
        hold.RegisterWorldServerInfo(serverInfo);
        Assert.True(hold.TryEnqueue(serverInfo));
        Assert.True(hold.OnTransferPending(Maraudon));

        var merged = hold.TryMergeOnNewWorld(Maraudon, new Vector3(1016.8f, -458.5f, -43.4f), 0.5f);

        Assert.NotNull(merged);
        Assert.Equal(Maraudon, verify.MapID);
        Assert.Equal(1u, serverInfo.DifficultyID);
        Assert.Equal(5u, serverInfo.InstanceGroupSize);
    }

    // Defensive: a NEW_WORLD without its TRANSFER_PENDING still names where the
    // server is putting the player — merge rather than drop the relocation.
    [Fact]
    public void TryMergeOnNewWorld_BareNewWorldWhileHolding_StillMerges()
    {
        var verify = MakeVerify(Maraudon);
        var hold = BeginHold(verify);

        var merged = hold.TryMergeOnNewWorld(Kalimdor, new Vector3(-1468.0f, 2614.0f, 76.0f), 2.2f);

        Assert.NotNull(merged);
        Assert.Equal(Kalimdor, verify.MapID);
        Assert.Equal(LoginEvictionHold.HoldPhase.Inactive, hold.Phase);
    }

    [Fact]
    public void TryMergeOnNewWorld_Inactive_ReturnsNull()
    {
        var hold = new LoginEvictionHold();
        Assert.Null(hold.TryMergeOnNewWorld(EasternKingdoms, Vector3.Zero, 0f));
    }

    // --- transfer aborted ---

    [Fact]
    public void OnTransferAborted_AfterSwallowedTransfer_DropsBackToHoldingAndUpdateReleases()
    {
        var verify = MakeVerify(Deadmines);
        var hold = BeginHold(verify);
        Assert.True(hold.OnTransferPending(EasternKingdoms));

        Assert.True(hold.OnTransferAborted());

        Assert.Equal(LoginEvictionHold.HoldPhase.Holding, hold.Phase);
        Assert.Equal(0u, hold.PendingDestinationMapId);
        // The original login stands and now releases as healthy, unmodified.
        var released = hold.TryReleaseOnFirstUpdateObject();
        Assert.NotNull(released);
        Assert.Same(verify, released[0]);
        Assert.Equal(Deadmines, verify.MapID);
    }

    [Fact]
    public void OnTransferAborted_NoSwallowedTransfer_PassesThrough()
    {
        var inactive = new LoginEvictionHold();
        Assert.False(inactive.OnTransferAborted());

        var holding = BeginHold(MakeVerify(Deadmines));
        Assert.False(holding.OnTransferAborted());
        Assert.Equal(LoginEvictionHold.HoldPhase.Holding, holding.Phase);
    }

    // --- fail-open ---

    [Theory]
    [InlineData(false)] // legacy disconnect while plainly holding
    [InlineData(true)]  // legacy disconnect mid-eviction (after TRANSFER_PENDING)
    public void TryReleaseAll_ArmedPhases_FlushesUnmodified(bool transferSeen)
    {
        var verify = MakeVerify(Deadmines);
        var hold = BeginHold(verify);
        if (transferSeen)
            Assert.True(hold.OnTransferPending(EasternKingdoms));

        var released = hold.TryReleaseAll();

        Assert.NotNull(released);
        Assert.Same(verify, released[0]);
        Assert.Equal(Deadmines, verify.MapID);
        Assert.Equal(LoginEvictionHold.HoldPhase.Inactive, hold.Phase);
    }

    [Fact]
    public void TryReleaseAll_Inactive_ReturnsNull()
    {
        Assert.Null(new LoginEvictionHold().TryReleaseAll());
    }

    // --- registration edge ---

    // A WorldServerInfo registered while the hold is disarmed must not be captured:
    // a later, unrelated merge would otherwise rewrite a packet that already went out.
    [Fact]
    public void RegisterWorldServerInfo_WhileInactive_IsNotCaptured()
    {
        var hold = new LoginEvictionHold();
        var strayInfo = new WorldServerInfo { DifficultyID = 1, InstanceGroupSize = 5 };
        hold.RegisterWorldServerInfo(strayInfo);

        hold.Begin(MakeVerify(Deadmines), nowTick: 1000);
        var merged = hold.TryMergeOnNewWorld(EasternKingdoms, Vector3.Zero, 0f);

        Assert.NotNull(merged);
        Assert.Equal(1u, strayInfo.DifficultyID);
        Assert.Equal(5u, strayInfo.InstanceGroupSize);
    }
}
