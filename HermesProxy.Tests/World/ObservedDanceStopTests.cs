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

// JimsProxy (another player stuck dancing, 2026-10-17 live Stratholme): the client loops another
// unit's /dance (SMSG_EMOTE 10) until another looping emote; the unit's movement does not end it. On
// that unit's next translational move start the proxy gives the client SMSG_EMOTE 0 for it first.
// Driven through the real SMSG_EMOTE and movement-relay handlers.
public class ObservedDanceStopTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 27032);
    private static readonly WowGuid64 LegacyWarrior = new(HighGuidTypeLegacy.Player, 87579);

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
            Session.ThreatTracker = new ThreatTracker(Session);
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };
        }

        public List<ServerPacket> Sent
        {
            get { lock (_sent) return new List<ServerPacket>(_sent); }
        }

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);
    }

    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    // 1.12 SMSG_EMOTE: emote id, then the unit's full guid.
    private static WorldPacket Emote(WowGuid64 unit, uint emoteId) => Legacy(Opcode.SMSG_EMOTE, w =>
    {
        w.WriteUInt32(emoteId);
        w.WriteGuid(unit);
    });

    // A relayed 1.12 movement packet: packed guid, then MovementInfo.
    private static WorldPacket Move(Opcode opcode, WowGuid64 unit, uint flags) => Legacy(opcode, w =>
    {
        w.WritePackedGuid(unit);
        new MovementInfo { Flags = flags }.WriteMovementInfoLegacy(w);
    });

    private static WorldPacket OutOfRange(WowGuid64 unit) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.FarObjects);
        w.WriteInt32(1);
        w.WritePackedGuid(unit);
    });

    // The SMSG_EMOTE 0 packets the proxy made for this unit, and the order of everything sent.
    private static List<string> Trace(ClientHarness h, WowGuid128 unit) =>
        h.Sent.Select(p => p switch
        {
            EmoteMessage e when e.Guid == unit => "emote " + e.EmoteID,
            MoveUpdate m when m.MoverGUID == unit => "move",
            _ => null,
        }).Where(s => s != null).ToList()!;

    // The 22:50:14 case: the warrior danced, then started moving. The stop comes before the move.
    [Fact]
    public void DanceThenMoveStart_GetsEmote0BeforeTheMove_Once()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);
        h.Dispatch(Emote(LegacyWarrior, 10));
        h.ClearSent();

        h.Dispatch(Move(Opcode.MSG_MOVE_START_FORWARD, LegacyWarrior, 0x1));
        h.Dispatch(Move(Opcode.MSG_MOVE_STOP, LegacyWarrior, 0));
        h.Dispatch(Move(Opcode.MSG_MOVE_START_FORWARD, LegacyWarrior, 0x1));

        Assert.Equal(new[] { "emote 0", "move", "move", "move" }, Trace(h, warrior));
    }

    // One-off emotes (eat, flinch) play beside the loop and do not end it, so the stop is still owed.
    [Fact]
    public void OneOffEmoteInBetween_TheStopIsStillSent()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);
        h.Dispatch(Emote(LegacyWarrior, 10));
        h.Dispatch(Emote(LegacyWarrior, 7));
        h.ClearSent();

        h.Dispatch(Move(Opcode.MSG_MOVE_JUMP, LegacyWarrior, 0x2000));

        Assert.Equal(new[] { "emote 0", "move" }, Trace(h, warrior));
    }

    // A server emote 0 already ended the loop: nothing more is sent.
    [Fact]
    public void ServerEmote0_EndsIt_NoStopIsAdded()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);
        h.Dispatch(Emote(LegacyWarrior, 10));
        h.Dispatch(Emote(LegacyWarrior, 0));
        h.ClearSent();

        h.Dispatch(Move(Opcode.MSG_MOVE_START_FORWARD, LegacyWarrior, 0x1));

        Assert.Equal(new[] { "move" }, Trace(h, warrior));
    }

    // Turning in place is not a move start: the dance is left alone until the unit moves.
    [Fact]
    public void TurnOnly_DoesNotStopTheDance()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);
        h.Dispatch(Emote(LegacyWarrior, 10));
        h.ClearSent();

        h.Dispatch(Move(Opcode.MSG_MOVE_START_TURN_LEFT, LegacyWarrior, 0x10));
        h.Dispatch(Move(Opcode.MSG_MOVE_START_STRAFE_RIGHT, LegacyWarrior, 0x8));

        Assert.Equal(new[] { "move", "emote 0", "move" }, Trace(h, warrior));
    }

    // Leaving view ends the loop on the client (its teardown), so no stop is owed when the unit is back.
    [Fact]
    public void AfterLeavingView_NoStopIsSent()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);
        h.Dispatch(Emote(LegacyWarrior, 10));
        h.Dispatch(OutOfRange(LegacyWarrior));
        h.ClearSent();

        Assert.False(h.State.ObservedLoopingEmotes.ContainsKey(warrior));
    }

    // A unit that never danced is never sent a stop.
    [Fact]
    public void NoDance_NoStop()
    {
        var h = new ClientHarness();
        var warrior = LegacyWarrior.To128(h.State);

        h.Dispatch(Move(Opcode.MSG_MOVE_START_FORWARD, LegacyWarrior, 0x1));

        Assert.Equal(new[] { "move" }, Trace(h, warrior));
    }

    [Fact]
    public void KillSwitchOff_NoStop()
    {
        bool before = global::Framework.Settings.ObservedDanceStopSynth;
        global::Framework.Settings.ObservedDanceStopSynth = false;
        try
        {
            var h = new ClientHarness();
            var warrior = LegacyWarrior.To128(h.State);
            h.Dispatch(Emote(LegacyWarrior, 10));
            h.ClearSent();

            h.Dispatch(Move(Opcode.MSG_MOVE_START_FORWARD, LegacyWarrior, 0x1));

            Assert.Equal(new[] { "move" }, Trace(h, warrior));
        }
        finally
        {
            global::Framework.Settings.ObservedDanceStopSynth = before;
        }
    }
}
