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

// JimsProxy (stuck moving after a mind control ends): the server relays the controller's movement for a
// controlled player to that player's client as well; a relay that lands after control is back must not
// reach the client, while the relays during the control still do.
public class OwnMovementRelayTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 57);
    private static readonly WowGuid64 OtherPlayer = new(HighGuidTypeLegacy.Player, 42);

    private sealed class ClientHarness
    {
        public readonly WorldClient Client = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();
        private readonly List<ServerPacket> _sent = new();

        public ClientHarness()
        {
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Client;
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };
        }

        public int MoveUpdates
        {
            get { lock (_sent) return _sent.Count(p => p.GetUniversalOpcode() == Opcode.SMSG_MOVE_UPDATE); }
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);
    }

    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    // A relayed 1.12 MSG_MOVE_START_FORWARD: packed mover guid, then MovementInfo with Forward set.
    private static WorldPacket RelayedForward(WowGuid64 mover) => Legacy(Opcode.MSG_MOVE_START_FORWARD, w =>
    {
        w.WritePackedGuid(mover);
        new MovementInfo { Flags = 0x1 }.WriteMovementInfoLegacy(w);
    });

    private static WorldPacket PlayerControl(bool on) => Legacy(Opcode.SMSG_CONTROL_UPDATE, w =>
    {
        w.WritePackedGuid(LegacyPlayer);
        w.WriteBool(on);
    });

    [Fact]
    public void OwnRelay_WhileThePlayerHasControl_IsDropped()
    {
        var h = new ClientHarness();
        h.State.LastObservedHasControl = true;

        h.Dispatch(RelayedForward(LegacyPlayer));

        Assert.Equal(0, h.MoveUpdates);
    }

    [Fact]
    public void OwnRelay_WhileSomeoneElseControlsThePlayer_IsForwarded()
    {
        var h = new ClientHarness();
        h.State.LastObservedHasControl = false;

        h.Dispatch(RelayedForward(LegacyPlayer));

        Assert.Equal(1, h.MoveUpdates);
    }

    [Fact]
    public void AnotherUnitsMovement_IsForwardedWhateverTheControlState()
    {
        var h = new ClientHarness();
        h.State.LastObservedHasControl = true;

        h.Dispatch(RelayedForward(OtherPlayer));

        Assert.Equal(1, h.MoveUpdates);
    }

    // The 2026-10-18 18:27:01 order: a relay while controlled reaches the client, the server gives control
    // back, and the controller's last relayed forward, still in flight, must not reach it.
    [Fact]
    public void RelayAfterControlComesBack_IsDropped_TheOneBeforeIsNot()
    {
        var h = new ClientHarness();
        h.Dispatch(PlayerControl(false));
        h.Dispatch(RelayedForward(LegacyPlayer));
        Assert.Equal(1, h.MoveUpdates);

        h.Dispatch(PlayerControl(true));
        h.Dispatch(RelayedForward(LegacyPlayer));

        Assert.Equal(1, h.MoveUpdates);
    }
}
