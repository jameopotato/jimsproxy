using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (mount speed kept after Will of the Forsaken): the controller's acknowledgement of a controlled
// unit's speed change must reach the server ahead of the controller's release of that unit. Orders from the
// 2026-10-18 Kronos PTR captures, priest box (priest 42, Mind Controlled undead 57).
public class ControlReleaseAckOrderTests
{
    private static readonly WowGuid128 Priest = new(42, 1);
    private static readonly WowGuid128 Victim = new(57, 2);
    private const Opcode RunAck = Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK;
    private const Opcode RunBackAck = Opcode.CMSG_MOVE_FORCE_RUN_BACK_SPEED_CHANGE_ACK;

    // The gate never reads the opcode; the names only have to exist in the 1.12 table the constructor
    // maps through (MSG_MOVE_HEARTBEAT and MSG_MOVE_FALL_LAND, not their CMSG forms).
    private static WorldPacket C2S(Opcode opcode) => new(opcode);

    // The priest's client has taken the victim over (CMSG_SET_ACTIVE_MOVER(victim)).
    private static ControlReleaseAckOrder PriestMovingVictim()
    {
        var gate = new ControlReleaseAckOrder();
        gate.OnClientMoverChange(Priest);
        gate.OnClientMoverChange(Victim);
        return gate;
    }

    // 18:52:57.766, the failing case: the dismount speed and the end of the control in one batch; the client
    // sends SET_ACTIVE_MOVER(0), FAR_SIGHT 0, SET_ACTIVE_MOVER(priest), SPLINE_DONE and FALL_LAND, then the
    // acknowledgement. The acknowledgement must go first, then the five in their order.
    [Fact]
    public void AckAfterTheRelease_GoesFirst_ThenTheReleaseInOrder()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);

        var sent = new List<WorldPacket>();
        void Send(WorldPacket p) { if (!gate.TryHold(p, out _)) sent.Add(p); }

        Assert.True(gate.OnClientMoverChange(default));
        var moverNone = C2S(Opcode.CMSG_SET_ACTIVE_MOVER); Send(moverNone);
        Assert.False(gate.OnClientMoverChange(null));
        var farSight = C2S(Opcode.CMSG_FAR_SIGHT); Send(farSight);
        Assert.False(gate.OnClientMoverChange(Priest));
        var moverPriest = C2S(Opcode.CMSG_SET_ACTIVE_MOVER); Send(moverPriest);
        var splineDone = C2S(Opcode.CMSG_MOVE_SPLINE_DONE); Send(splineDone);
        var fallLand = C2S(Opcode.MSG_MOVE_FALL_LAND); Send(fallLand);
        Assert.Empty(sent);

        Assert.True(gate.OnAck(Victim, RunAck, 0, out var release));
        Assert.Equal(new[] { moverNone, farSight, moverPriest, splineDone, fallLand }, release);

        var later = C2S(Opcode.MSG_MOVE_HEARTBEAT);
        Assert.False(gate.TryHold(later, out _));
    }

    // 18:26:39.797 and 18:30:19.594, the corrected cases: the client acknowledged before its release.
    // Nothing is held, nothing changes.
    [Fact]
    public void AckBeforeTheRelease_HoldsNothing()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);

        Assert.False(gate.OnAck(Victim, RunAck, 0, out var release));
        Assert.Null(release);
        Assert.False(gate.OnClientMoverChange(default));
        Assert.False(gate.TryHold(C2S(Opcode.CMSG_SET_ACTIVE_MOVER), out _));
    }

    // The far-sight-first release (the Farsight field ending before the control update): the hold starts at
    // CMSG_FAR_SIGHT.
    [Fact]
    public void FarSightFirst_StartsTheHold()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);

        Assert.True(gate.OnClientMoverChange(null));
        var farSight = C2S(Opcode.CMSG_FAR_SIGHT);
        Assert.True(gate.TryHold(farSight, out _));

        Assert.True(gate.OnAck(Victim, RunAck, 0, out var release));
        Assert.Equal(new[] { farSight }, release);
    }

    // Two changes for the unit: the hold ends with the last acknowledgement, and each acknowledgement for the
    // unit goes out ahead of the held packets.
    [Fact]
    public void TwoChanges_EachAckGoesOutAhead_TheHoldEndsWithTheLast()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);
        gate.ExpectAck(Victim, RunBackAck, 0);
        Assert.True(gate.OnClientMoverChange(default));
        var mover = C2S(Opcode.CMSG_SET_ACTIVE_MOVER);
        Assert.True(gate.TryHold(mover, out _));

        Assert.True(gate.OnAck(Victim, RunAck, 0, out var first));
        Assert.Null(first);
        Assert.True(gate.OnAck(Victim, RunBackAck, 0, out var second));
        Assert.Equal(new[] { mover }, second);
    }

    // No outstanding acknowledgement, or one for the player's own unit, which the server accepts whenever it
    // comes (the handler never registers those): a release is never held.
    [Fact]
    public void ReleaseWithNothingOwedForTheUnit_IsNotHeld()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Priest, RunAck, 0);

        Assert.False(gate.OnClientMoverChange(default));
        Assert.False(gate.TryHold(C2S(Opcode.CMSG_SET_ACTIVE_MOVER), out _));
    }

    // Taking the unit over again forgets what was expected for it, so an acknowledgement lost earlier cannot
    // hold a later release.
    [Fact]
    public void TakingTheUnitOverAgain_ForgetsWhatWasExpectedForIt()
    {
        var gate = new ControlReleaseAckOrder();
        gate.OnClientMoverChange(Priest);
        gate.ExpectAck(Victim, RunAck, 0);

        gate.OnClientMoverChange(Victim);

        Assert.False(gate.OnClientMoverChange(default));
    }

    // An acknowledgement that never comes: the hold lets go at MaxHeld packets, in order, and forgets the unit.
    [Fact]
    public void HoldReachingMaxHeld_LetsGoInOrder()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);
        Assert.True(gate.OnClientMoverChange(default));

        var packets = Enumerable.Range(0, ControlReleaseAckOrder.MaxHeld).Select(_ => C2S(Opcode.MSG_MOVE_HEARTBEAT)).ToList();
        List<WorldPacket>? overflow = null;
        foreach (var p in packets)
            Assert.True(gate.TryHold(p, out overflow));

        Assert.Equal(packets, overflow);
        Assert.False(gate.TryHold(C2S(Opcode.MSG_MOVE_HEARTBEAT), out _));
        Assert.False(gate.OnAck(Victim, RunAck, 0, out _));
    }

    // A transfer pauses the client's movement: the gate forgets, and a hold in progress is handed back.
    [Fact]
    public void Reset_ReturnsTheHeldPacketsAndForgets()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);
        Assert.True(gate.OnClientMoverChange(default));
        var mover = C2S(Opcode.CMSG_SET_ACTIVE_MOVER);
        Assert.True(gate.TryHold(mover, out _));

        Assert.Equal(new[] { mover }, gate.Reset());
        Assert.False(gate.TryHold(C2S(Opcode.MSG_MOVE_HEARTBEAT), out _));
        Assert.False(gate.OnAck(Victim, RunAck, 0, out _));
    }

    // An acknowledgement for another unit during a hold is not the one the hold waits for: it stays in order.
    [Fact]
    public void AckForAnotherUnitDuringAHold_StaysInOrder()
    {
        var gate = PriestMovingVictim();
        gate.ExpectAck(Victim, RunAck, 0);
        Assert.True(gate.OnClientMoverChange(default));

        Assert.False(gate.OnAck(Priest, RunAck, 0, out var release));
        Assert.Null(release);
    }

    // ---- through the real server-to-client handler ----

    private static readonly WowGuid64 LegacyPriest = new(HighGuidTypeLegacy.Player, 42);
    private static readonly WowGuid64 LegacyVictim = new(HighGuidTypeLegacy.Player, 57);

    private sealed class ClientHarness
    {
        public readonly WorldClient Client = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();

        public ClientHarness()
        {
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Client;
            State.CurrentPlayerGuid = LegacyPriest.To128(State);
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = _ => { };
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);
    }

    // A 1.12 SMSG_FORCE_RUN_SPEED_CHANGE: packed guid, counter, speed.
    private static WorldPacket ForceRunSpeed(WowGuid64 unit, float speed)
    {
        var writer = new WorldPacket(Opcode.SMSG_FORCE_RUN_SPEED_CHANGE);
        writer.WritePackedGuid(unit);
        writer.WriteUInt32(0);
        writer.WriteFloat(speed);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    [Fact]
    public void ForcedSpeedChangeForTheVictim_IsExpected_ForThePlayerItIsNot()
    {
        var h = new ClientHarness();
        var gate = h.State.ReleaseAckOrder;
        var victim = LegacyVictim.To128(h.State);
        gate.OnClientMoverChange(h.State.CurrentPlayerGuid);
        gate.OnClientMoverChange(victim);

        h.Dispatch(ForceRunSpeed(LegacyPriest, 7.0f));
        h.Dispatch(ForceRunSpeed(LegacyVictim, 7.0f));

        Assert.True(gate.OnClientMoverChange(default));
        Assert.False(gate.OnAck(h.State.CurrentPlayerGuid, RunAck, 0, out _));
        Assert.True(gate.OnAck(victim, RunAck, 0, out var release));
        Assert.NotNull(release);
    }
}
