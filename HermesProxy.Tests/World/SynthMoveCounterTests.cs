using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (P1-2, checks D-01 and D-06), driven through the real handlers.
//
// (a) Three proxy-synthesized SET-class movement packets went to the client with MoveCounter 0,
//     the value the 1.12 server puts on every SET it sends, so the client's acknowledgement was
//     forwarded to the server as an ack for a change the server never sent. Each site now
//     carries its own counter (SynthMoveCounters) and the ack handlers keep those acks.
// (b) The transport-clear teleport and the post-teleport run-speed reassert are deleted. Both
//     were built inside the movement block of the player's own create and left ahead of the
//     UPDATE_OBJECT that creates the player, so the client never applied them (the field
//     record has the teleport sent 752 times and acknowledged 0 times), and the reassert only
//     copied the create's own run speed. A world port's self create brings neither now.
public class SynthMoveCounterTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 4242);

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
            // A real login loads these from the account store; the self create's player-field
            // translation reads Settings, so give it an empty one.
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };
        }

        public WowGuid128 Player => State.CurrentPlayerGuid;

        public List<ServerPacket> Sent
        {
            get { lock (_sent) return new List<ServerPacket>(_sent); }
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);
    }

    // A legacy (1.12) packet as the WorldClient receive loop hands it to a handler.
    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    // The MoveCounter as it goes on the wire: every packet here starts with the packed mover guid.
    private static uint WireCounter(ServerPacket packet)
    {
        packet.WritePacketData();
        var wire = new WorldPacket(packet.GetOpcode(), packet.GetData()!);
        wire.ReadPackedGuid128();
        return wire.ReadUInt32();
    }

    private static int IndexOf(List<ServerPacket> sent, Opcode opcode)
        => sent.FindIndex(p => p.GetUniversalOpcode() == opcode);

    // 1.12 MSG_MOVE_SET_RUN_SPEED (the broadcast form the server uses for a unit its client does not
    // control, a charmed player included): packed mover guid, MovementInfo, speed.
    private static WorldPacket BroadcastRunSpeed(WowGuid64 mover, float speed) => Legacy(Opcode.MSG_MOVE_SET_RUN_SPEED, w =>
    {
        w.WritePackedGuid(mover);
        new MovementInfo().WriteMovementInfoLegacy(w);
        w.WriteFloat(speed);
    });

    // User report 2026-10-17: mind-controlled while mounted, then Will of the Forsaken. The dismount the
    // client requested while charmed reached the proxy only as the broadcast speed form, the cache kept
    // the mounted speed, and the control-regain reassert put the client at mount speed. The broadcast
    // form for the player's own guid must feed the cache.
    [Fact]
    public void BroadcastRunSpeed_ForTheOwnPlayer_FeedsTheRegainCache()
    {
        var h = new ClientHarness();
        h.State.LastKnownPlayerRunSpeed = 14.0f;                  // mounted speed cached before the charm

        h.Dispatch(BroadcastRunSpeed(LegacyPlayer, 7.0f));        // the dismount, seen while charmed

        Assert.Equal(7.0f, h.State.LastKnownPlayerRunSpeed);
        Assert.Contains(h.Sent, p => p.GetUniversalOpcode() == Opcode.SMSG_MOVE_UPDATE_RUN_SPEED);
    }

    [Fact]
    public void BroadcastRunSpeed_ForAnotherUnit_LeavesTheRegainCache()
    {
        var h = new ClientHarness();
        h.State.LastKnownPlayerRunSpeed = 14.0f;

        h.Dispatch(BroadcastRunSpeed(new WowGuid64(HighGuidTypeLegacy.Player, 9999), 7.0f));

        Assert.Equal(14.0f, h.State.LastKnownPlayerRunSpeed);
    }

    // 1.12 self create: one CREATE_OBJECT2 block, Self|Living movement block, no values set.
    private static WorldPacket SelfCreate(float runSpeed) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);                                     // one block
        w.WriteUInt8(0);                                      // has transport (pre-3.0 header)
        w.WriteUInt8((byte)UpdateTypeLegacy.CreateObject2);
        w.WritePackedGuid(LegacyPlayer);
        w.WriteUInt8((byte)ObjectTypeLegacy.Player);
        w.WriteUInt8((byte)(UpdateFlag.Self | UpdateFlag.Living));
        w.WriteUInt32(0);                                     // movement flags: not on a transport
        w.WriteUInt32(0);                                     // move time
        w.WriteVector3(new Vector3(-8949.95f, -132.49f, 83.53f));
        w.WriteFloat(0f);                                     // facing
        w.WriteUInt32(0);                                     // fall time
        w.WriteFloat(2.5f);                                   // walk
        w.WriteFloat(runSpeed);                               // run
        w.WriteFloat(4.5f);                                   // run back
        w.WriteFloat(4.722222f);                              // swim
        w.WriteFloat(2.5f);                                   // swim back
        w.WriteFloat(3.141594f);                              // turn rate
        // Values: a full-width player mask (as a real create sends) with no field set.
        int maskBlocks = (LegacyVersion.GetUpdateField(PlayerField.PLAYER_END) + 31) / 32;
        w.WriteUInt8((byte)maskBlocks);
        for (int i = 0; i < maskBlocks; i++)
            w.WriteUInt32(0);
    });

    // ---------------------------------------------------------------- (a) sentinels

    [Fact]
    public void Sentinels_AreDistinct_InTheReservedRange_AndClearOfTheOtherSynths()
    {
        uint[] mine =
        {
            SynthMoveCounters.RegainControlRunSpeed,
            SynthMoveCounters.TaxiResumeDismountUnroot,
            SynthMoveCounters.TaxiDismountUnroot,
        };

        Assert.Equal(mine.Length, mine.Distinct().Count());
        foreach (uint counter in mine)
        {
            Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(counter));
            Assert.True(SynthMoveCounters.IsSynthAckCounter(counter));
            Assert.NotEqual(WorldEntryCeremonyTracker.SynthCounterUnroot, counter);
            Assert.False(ChargePendLatchCure.IsCureCounter(counter));
        }
        // The server's own counter, the other synths' sentinels and the values of the deleted
        // synths (0xFFFFFF08 run-speed reassert, 0xFFFFFFFF transport-clear) are not swallowed here.
        Assert.False(SynthMoveCounters.IsSynthAckCounter(0));
        Assert.False(SynthMoveCounters.IsSynthAckCounter(0xFFFFFF08));
        Assert.False(SynthMoveCounters.IsSynthAckCounter(0xFFFFFFFF));
        Assert.False(SynthMoveCounters.IsSynthAckCounter(WorldEntryCeremonyTracker.SynthCounterUnroot));
        Assert.False(SynthMoveCounters.IsSynthAckCounter(ChargePendLatchCure.SynthCounterRoot));
        Assert.False(SynthMoveCounters.IsSynthAckCounter(ChargePendLatchCure.SynthCounterUnroot));
    }

    public enum Site { RegainControl, TaxiResumeLanding, TaxiLanding }

    // Drive one synth site through its real handler and return the synthesized packet.
    private static async Task<(ClientHarness Harness, ServerPacket Synth)> SynthFrom(Site site)
    {
        var h = new ClientHarness();
        switch (site)
        {
            // Site 1: SMSG_CONTROL_UPDATE false -> true restores the cached run speed.
            case Site.RegainControl:
                h.State.LastObservedHasControl = false;
                h.State.LastKnownPlayerRunSpeed = 14f;
                h.Dispatch(Legacy(Opcode.SMSG_CONTROL_UPDATE, w =>
                {
                    w.WritePackedGuid(LegacyPlayer);
                    w.WriteBool(true);
                }));
                return (h, Assert.Single(h.Sent.OfType<MoveSetSpeed>()));

            // Site 2: the resumed-taxi landing restore.
            case Site.TaxiResumeLanding:
                h.Client.SendTaxiDismountRestore(h.Player);
                return (h, Assert.Single(h.Sent, p => p.GetUniversalOpcode() == Opcode.SMSG_MOVE_UNROOT));

            // Site 3: the landing scheduled when a taxi flight spline arrives (HandleMonsterMove).
            case Site.TaxiLanding:
            {
                h.State.IsWaitingForTaxiStart = true;
                h.Session.InstanceSocket = WorldSocket.CreateForTest(h.Session); // the landing needs one open
                h.Dispatch(Legacy(Opcode.SMSG_ON_MONSTER_MOVE, w =>
                {
                    w.WritePackedGuid(LegacyPlayer);
                    w.WriteVector3(new Vector3(100f, 200f, 50f));      // start
                    w.WriteUInt32(7);                                  // spline id
                    w.WriteUInt8((byte)SplineTypeLegacy.Normal);
                    w.WriteUInt32((uint)(SplineFlagVanilla.Runmode | SplineFlagVanilla.Flying)); // the taxi shape
                    w.WriteUInt32(0);                                  // duration 0: lands after the 250 ms margin
                    w.WriteUInt32(1);                                  // one point (catmull-rom: full vector)
                    w.WriteVector3(new Vector3(110f, 210f, 55f));
                }));
                ServerPacket? unroot = null;
                for (int i = 0; i < 60 && unroot == null; i++)
                {
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                    unroot = h.Sent.FirstOrDefault(p => p.GetUniversalOpcode() == Opcode.SMSG_MOVE_UNROOT);
                }
                Assert.NotNull(unroot);
                return (h, unroot!);
            }
        }
        throw new ArgumentOutOfRangeException(nameof(site));
    }

    [Fact]
    public async Task RegainControlRunSpeed_CarriesItsOwnCounter()
    {
        var (_, synth) = await SynthFrom(Site.RegainControl);

        var runFix = Assert.IsType<MoveSetSpeed>(synth);
        Assert.Equal(Opcode.SMSG_MOVE_SET_RUN_SPEED, runFix.GetUniversalOpcode());
        Assert.Equal(14f, runFix.Speed);
        Assert.Equal(SynthMoveCounters.RegainControlRunSpeed, WireCounter(runFix));
    }

    [Fact]
    public async Task TaxiResumeDismountUnroot_CarriesItsOwnCounter()
    {
        var (_, unroot) = await SynthFrom(Site.TaxiResumeLanding);

        Assert.Equal(SynthMoveCounters.TaxiResumeDismountUnroot, WireCounter(unroot));
    }

    [Fact]
    public async Task TaxiDismountUnroot_CarriesItsOwnCounter()
    {
        var (_, unroot) = await SynthFrom(Site.TaxiLanding);

        Assert.Equal(SynthMoveCounters.TaxiDismountUnroot, WireCounter(unroot));
    }

    // ---------------------------------------------------------------- (a) ack swallow

    private sealed class ServerHarness : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public readonly WorldClient Legacy = new();
        public readonly WorldSocket Socket;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();

        public ServerHarness()
        {
            // An authenticated, unconnected legacy connection: every forward counts one write
            // attempt (the send then fails harmlessly on the unconnected socket).
            Legacy.EnterConnectPendingForTest(_socket);
            Legacy.MarkAuthenticatedForTest();
            var session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            session.GameState = State;
            session.WorldClient = Legacy;
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            Socket = WorldSocket.CreateForTest(session);
        }

        public void Receive(WorldPacket modern) => ReceiveModern(Socket, modern);

        public void Dispose() => _socket.Dispose();
    }

    // Route a modern packet through the handler the c2s table would pick for its opcode (same
    // attribute scan as WorldSocket.InitializePacketHandlers).
    private static void ReceiveModern(WorldSocket socket, WorldPacket modern)
    {
        var opcode = ModernVersion.GetUniversalOpcode(modern.GetOpcode());
        foreach (var method in typeof(WorldSocket).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (method.GetCustomAttributes<PacketHandlerAttribute>().Any(a => a.Opcode == opcode))
            {
                new WorldSocket.PacketHandler(method, method.GetParameters()[0].ParameterType).Invoke(socket, modern);
                return;
            }
        }
        throw new InvalidOperationException($"no c2s handler for {opcode}");
    }

    // The 1.14 ack body as the proxy reads it: mover guid + movement info, the ack index, then
    // the speed for a speed-change ack.
    private static WorldPacket ModernAck(Opcode opcode, WowGuid128 mover, uint ackIndex, float? speed)
    {
        var writer = new WorldPacket(ModernVersion.GetCurrentOpcode(opcode));
        new MovementInfo().WriteMovementInfoModern(writer, mover);
        writer.WriteUInt32(ackIndex);
        if (speed is float s)
            writer.WriteFloat(s);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    [Theory]
    [InlineData(Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK, SynthMoveCounters.RegainControlRunSpeed)]
    [InlineData(Opcode.CMSG_MOVE_FORCE_UNROOT_ACK, SynthMoveCounters.TaxiResumeDismountUnroot)]
    [InlineData(Opcode.CMSG_MOVE_FORCE_UNROOT_ACK, SynthMoveCounters.TaxiDismountUnroot)]
    public void SentinelAck_IsSwallowed_NothingReachesTheServer(Opcode ackOpcode, uint counter)
    {
        using var h = new ServerHarness();
        bool isSpeedAck = ackOpcode == Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK;

        h.Receive(ModernAck(ackOpcode, h.State.CurrentPlayerGuid, counter, isSpeedAck ? 7f : null));

        Assert.Equal(0, h.Legacy.WriteAttemptsForTest);
    }

    // Control: the server's own counter 0 still crosses, so the harness can see a forward.
    [Theory]
    [InlineData(Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK)]
    [InlineData(Opcode.CMSG_MOVE_FORCE_UNROOT_ACK)]
    public void ServerCounterAck_IsStillForwarded(Opcode ackOpcode)
    {
        using var h = new ServerHarness();
        bool isSpeedAck = ackOpcode == Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK;

        h.Receive(ModernAck(ackOpcode, h.State.CurrentPlayerGuid, 0, isSpeedAck ? 7f : null));

        Assert.Equal(1, h.Legacy.WriteAttemptsForTest);
    }

    // End to end: the client echoes the counter the site wrote; that ack must stay in the proxy.
    // With the old counter 0 every one of these crossed to the server as an ack for a change of
    // its own.
    [Theory]
    [InlineData(Site.RegainControl)]
    [InlineData(Site.TaxiResumeLanding)]
    [InlineData(Site.TaxiLanding)]
    public async Task SynthAck_EndToEnd_NothingReachesTheServer(Site site)
    {
        var (_, synth) = await SynthFrom(site);
        bool isSpeed = synth.GetUniversalOpcode() == Opcode.SMSG_MOVE_SET_RUN_SPEED;
        var ackOpcode = isSpeed ? Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK : Opcode.CMSG_MOVE_FORCE_UNROOT_ACK;
        using var server = new ServerHarness();

        server.Receive(ModernAck(ackOpcode, server.State.CurrentPlayerGuid, WireCounter(synth), isSpeed ? 7f : null));

        Assert.Equal(0, server.Legacy.WriteAttemptsForTest);
    }

    // ---------------------------------------------------------------- (b) deleted world-port synths

    public enum SourceTransport { OnTransport, Unobserved, OffTransport }

    // A world port through the real handlers: SMSG_NEW_WORLD, the client's
    // CMSG_WORLD_PORT_RESPONSE, then the destination self create. Before P1-2 that create's
    // movement block also produced a transport-clear SMSG_MOVE_TELEPORT (source on a transport
    // or unobserved, destination off one) and an SMSG_MOVE_SET_RUN_SPEED copy of the create's own
    // run speed (every port), both ahead of the create. Neither is synthesized now, before or after
    // the create; the run speed still seeds the regain-control cache.
    [Theory]
    [InlineData(SourceTransport.OnTransport)]
    [InlineData(SourceTransport.Unobserved)]
    [InlineData(SourceTransport.OffTransport)]
    public void WorldPortSelfCreate_SendsNoTeleportAndNoRunSpeed(SourceTransport source)
    {
        var h = new ClientHarness();
        h.State.LastObservedPlayerTransportGuid = source switch
        {
            SourceTransport.OnTransport => WowGuid128.Create(HighGuidType703.Transport, 0, 176495, 1),
            SourceTransport.OffTransport => WowGuid128.Empty,
            _ => null,
        };
        h.State.IsWaitingForNewWorld = true;                     // the transfer is pending
        h.Dispatch(Legacy(Opcode.SMSG_NEW_WORLD, w =>
        {
            w.WriteUInt32(1);                                    // destination map
            w.WriteVector3(new Vector3(1676.21f, -4315.29f, 61.52f));
            w.WriteFloat(0f);
        }));
        Assert.True(h.State.IsWaitingForWorldPortAck);
        var portResponse = new WorldPacket(ModernVersion.GetCurrentOpcode(Opcode.CMSG_WORLD_PORT_RESPONSE));
        ReceiveModern(WorldSocket.CreateForTest(h.Session), new WorldPacket(portResponse.GetOpcode(), portResponse.GetData()));
        Assert.False(h.State.IsWaitingForWorldPortAck);          // the port response handler ran

        h.Dispatch(SelfCreate(runSpeed: 11.648f));

        var sent = h.Sent;
        int newWorld = IndexOf(sent, Opcode.SMSG_NEW_WORLD);
        int create = IndexOf(sent, Opcode.SMSG_UPDATE_OBJECT);
        Assert.True(newWorld >= 0, "the NEW_WORLD was not sent");
        Assert.True(create > newWorld, $"self create at {create}, NEW_WORLD at {newWorld}");
        int teleport = IndexOf(sent, Opcode.SMSG_MOVE_TELEPORT);
        Assert.True(teleport == -1, $"a transport-clear teleport was sent at {teleport} (self create at {create})");
        int speed = IndexOf(sent, Opcode.SMSG_MOVE_SET_RUN_SPEED);
        Assert.True(speed == -1, $"a run-speed set was sent at {speed} (self create at {create})");
        Assert.Equal(11.648f, h.State.LastKnownPlayerRunSpeed);
    }
}
