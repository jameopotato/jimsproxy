using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (next-melee slot wedge, 2026-11-18): the next-melee slot (CurrentClientNextMeleeCast)
// holds the one Heroic Strike / Cleave / Maul / Raptor Strike press the server has queued, and
// HandleCastSpell rejects every next-melee press while it is set. It is cleared by the swing's
// SPELL_GO, the same-spell CAST_FAILED, or a session reset. A charm on the player (Molten Core,
// Lucifron's Flamewaker Protector, Dominate Mind) makes Kronos end the queued swing with
// SPELL_FAILURE alone: no CAST_FAILED ever follows, the slot stayed set, and every later press was
// rejected locally until relog (684 Maul presses over 20 minutes in the reporter's capture). The fix
// marks the slot on that SPELL_FAILURE and lets the next press release it.
public class NextMeleeSlotServerFailureTests
{
    private const uint Maul = 9881;
    private const uint Cleave = 20569;
    private const uint AutoShot = 75;

    private static ClientCastRequest MakePress(uint spellId) => new ClientCastRequest
    {
        SpellId = spellId,
        Timestamp = Environment.TickCount,
        HasSentPrepare = true,
        ClientGUID = new WowGuid128(43750, 0xBC0000000000002Aul),
        ServerGUID = new WowGuid128(113226, 0xBC0000000000002Bul),
    };

    [Fact]
    public void LiveSlot_IsNotReleased()
    {
        // A queued swing with no server failure is still occupying the slot: the occupied-slot
        // reject must keep working exactly as before (cross-spell swaps, re-presses in flight).
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(press, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void SpellFailureForTheSlotSpell_ThenNextPress_ReleasesTheSlot()
    {
        // The reporter's sequence: Maul queued, Dominate Mind lands, SPELL_FAILURE(Maul) arrives and
        // no CAST_FAILED follows. The next press must find the slot free.
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        Assert.True(state.MarkNextMeleeServerFailure(Maul));
        var released = state.TryReleaseServerFailedNextMeleeSlot();

        Assert.Same(press, released);
        Assert.Null(state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void SpellFailureAlone_DoesNotClearTheSlotByItself()
    {
        // The mark does not clear the slot: the normal failure pair's trailing CAST_FAILED must still
        // find and resolve it (the special-slot branch answers on the server id with the real reason).
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(Maul);
        state.CurrentClientNextMeleeCast = press;

        state.MarkNextMeleeServerFailure(Maul);

        Assert.Same(press, state.CurrentClientNextMeleeCast);
        Assert.True(press.ServerFailureSeen);
    }

    [Fact]
    public void SpellFailureForAnotherSpell_DoesNotMarkTheSlot()
    {
        // Heroic Strike queued, a SPELL_FAILURE for a different spell (here Cleave) must not free it.
        var state = GameSessionData.CreateForTesting();
        var press = MakePress(25286);
        state.CurrentClientNextMeleeCast = press;

        Assert.False(state.MarkNextMeleeServerFailure(Cleave));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(press, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void EmptySlot_MarkAndReleaseAreNoOps()
    {
        var state = GameSessionData.CreateForTesting();

        Assert.False(state.MarkNextMeleeServerFailure(Maul));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Null(state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void Release_IsOneShot_AndANewPressIsLive()
    {
        // After the release the caller installs the new press in the slot; that press carries no mark
        // and is protected by the reject again.
        var state = GameSessionData.CreateForTesting();
        state.CurrentClientNextMeleeCast = MakePress(Maul);
        state.MarkNextMeleeServerFailure(Maul);
        Assert.NotNull(state.TryReleaseServerFailedNextMeleeSlot());

        var next = MakePress(Maul);
        state.CurrentClientNextMeleeCast = next;

        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(next, state.CurrentClientNextMeleeCast);
    }

    [Fact]
    public void AutoRepeatSlot_IsNotTouched()
    {
        // Only the next-melee slot is in scope; the auto-repeat slot (Auto Shot / Shoot) keeps its own
        // lifecycle (retries, CMSG_CANCEL_AUTO_REPEAT_SPELL).
        var state = GameSessionData.CreateForTesting();
        var autoRepeat = MakePress(AutoShot);
        state.CurrentClientAutoRepeatCast = autoRepeat;

        Assert.False(state.MarkNextMeleeServerFailure(AutoShot));
        Assert.Null(state.TryReleaseServerFailedNextMeleeSlot());
        Assert.Same(autoRepeat, state.CurrentClientAutoRepeatCast);
        Assert.False(autoRepeat.ServerFailureSeen);
    }
}

// JimsProxy (next-melee slot wedge, review of #576): the tests above drive GameSessionData directly,
// so they stay green if either call site is deleted. These run the real handlers: the 1.12
// SMSG_SPELL_FAILURE and SMSG_CAST_FAILED through the legacy packet table (WorldClient), and the
// press through HandleCastSpell (WorldSocket). Packets for the modern client are caught by both send
// spies; a press forwarded to the server counts one write attempt on the unconnected legacy socket.
public class NextMeleeSlotServerFailureHandlerTests
{
    private const uint Maul = 9881;
    private const uint HeroicStrike = 25286;
    private const uint Cleave = 20569;
    private const uint MoltenCore = 409;
    private const byte VanillaInterrupted = 35; // SpellCastResultVanilla.Interrupted, the 1.12 wire value

    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 10);
    private static readonly WowGuid64 LegacyOther = new(HighGuidTypeLegacy.Player, 40);

    private sealed class Harness : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly FrozenSet<uint> _savedNextMeleeSpells = GameData.NextMeleeSpells;
        private readonly List<ServerPacket> _toClient = new();
        public readonly WorldClient Legacy = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();
        public readonly WorldSocket Socket;

        public Harness()
        {
            // The CSV that fills this set is not loaded in tests; restored in Dispose.
            GameData.NextMeleeSpells = new[] { Maul, HeroicStrike, Cleave }.ToFrozenSet();

            // An authenticated, unconnected legacy connection: every forward to the server counts
            // one write attempt (the send then fails harmlessly on the unconnected socket).
            Legacy.EnterConnectPendingForTest(_socket);
            Legacy.MarkAuthenticatedForTest();
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Legacy;
            Session.HealCommBridge = new HealCommBridge(Session); // both failure handlers call it
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentMapId = MoltenCore;
            Legacy.AttachSessionForTest(Session);
            Legacy.ClientSendSpyForTest = packet => _toClient.Add(packet);
            Socket = WorldSocket.CreateForTest(Session);
            Socket.ClientSendSpyForTest = packet => _toClient.Add(packet);
        }

        public int ServerWrites => Legacy.WriteAttemptsForTest;

        public IReadOnlyList<ServerPacket> ToClient => _toClient;

        // A CMSG_CAST_SPELL press, handed to the WorldSocket handler the c2s table dispatches it to.
        public void Press(uint spellId, ulong clientCounter)
        {
            var cast = (CastSpell)RuntimeHelpers.GetUninitializedObject(typeof(CastSpell));
            cast.Cast = new SpellCastRequest { SpellID = spellId, CastID = ClientCastId(spellId, clientCounter) };
            typeof(WorldSocket).GetMethod("HandleCastSpell", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Socket, new object[] { cast });
        }

        // 1.12 SMSG_SPELL_FAILURE: caster guid, spell, reason (Kronos sends 0; the real reason is in
        // the CAST_RESULT that normally follows).
        public void SpellFailure(WowGuid64 caster, uint spellId) =>
            Legacy.DispatchLegacyPacketForTest(LegacyPacket(Opcode.SMSG_SPELL_FAILURE, w =>
            {
                w.WriteGuid(caster);
                w.WriteUInt32(spellId);
                w.WriteUInt8(0);
            }));

        // 1.12 SMSG_CAST_FAILED (CAST_RESULT): spell, status 2 = failed, reason.
        public void CastFailed(uint spellId, byte reason) =>
            Legacy.DispatchLegacyPacketForTest(LegacyPacket(Opcode.SMSG_CAST_FAILED, w =>
            {
                w.WriteUInt32(spellId);
                w.WriteUInt8(2);
                w.WriteUInt8(reason);
            }));

        public void Dispose()
        {
            GameData.NextMeleeSpells = _savedNextMeleeSpells;
            _socket.Dispose();
        }
    }

    private static WowGuid128 ClientCastId(uint spellId, ulong counter) =>
        WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, MoltenCore, spellId, counter);

    private static WorldPacket LegacyPacket(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    [Fact]
    public void Handler_CharmFailureAlone_NextPressIsForwarded()
    {
        // The reporter's sequence: Maul queued, the charm's SPELL_FAILURE with no CAST_FAILED after
        // it, then a press. The press must reach the server and own the slot.
        using var h = new Harness();
        h.Press(Maul, 1);
        var queued = h.State.CurrentClientNextMeleeCast;
        Assert.NotNull(queued);
        Assert.Equal(1, h.ServerWrites);

        h.SpellFailure(LegacyPlayer, Maul);
        Assert.Same(queued, h.State.CurrentClientNextMeleeCast);
        Assert.True(queued!.ServerFailureSeen);

        h.Press(Maul, 2);

        Assert.Equal(ClientCastId(Maul, 2), h.State.CurrentClientNextMeleeCast!.ClientGUID);
        Assert.Equal(2, h.ServerWrites);
        Assert.DoesNotContain(h.ToClient.OfType<CastFailed>(),
            f => f.Reason == (uint)SpellCastResultClassic.SpellInProgress);
    }

    [Fact]
    public void Handler_NormalFailurePair_StillResolvesTheSlotOnTheServerId()
    {
        // A fear, stun or target death: SPELL_FAILURE then CAST_FAILED. The mark must not change the
        // pair: the CAST_FAILED answers the press on the slot's server id with the real reason and
        // frees the slot.
        using var h = new Harness();
        h.Press(Maul, 1);
        var queued = h.State.CurrentClientNextMeleeCast!;

        h.SpellFailure(LegacyPlayer, Maul);
        h.CastFailed(Maul, VanillaInterrupted);

        Assert.Null(h.State.CurrentClientNextMeleeCast);
        var failed = Assert.Single(h.ToClient.OfType<CastFailed>());
        Assert.Equal(queued.ServerGUID, failed.CastID);
        Assert.Equal((uint)SpellCastResultClassic.Interrupted, failed.Reason);
    }

    [Fact]
    public void Handler_LiveSlot_RejectsTheNextPressAsBefore()
    {
        // No server failure: the queued swing is live, and a second next-melee press (Heroic Strike
        // over a queued Cleave) is still rejected by the proxy on its client id, never forwarded.
        using var h = new Harness();
        h.Press(Cleave, 1);
        h.Press(HeroicStrike, 2);

        Assert.Equal(ClientCastId(Cleave, 1), h.State.CurrentClientNextMeleeCast!.ClientGUID);
        Assert.Equal(1, h.ServerWrites);
        var rejected = Assert.Single(h.ToClient.OfType<CastFailed>());
        Assert.Equal(ClientCastId(HeroicStrike, 2), rejected.CastID);
        Assert.Equal((uint)SpellCastResultClassic.SpellInProgress, rejected.Reason);
    }

    [Fact]
    public void Handler_AnotherPlayersFailure_DoesNotFreeTheSlot()
    {
        // Kronos also sends SPELL_FAILURE to observers: another druid's Maul failing must not free ours.
        using var h = new Harness();
        h.Press(Maul, 1);
        h.SpellFailure(LegacyOther, Maul);
        h.Press(Maul, 2);

        Assert.Equal(ClientCastId(Maul, 1), h.State.CurrentClientNextMeleeCast!.ClientGUID);
        Assert.False(h.State.CurrentClientNextMeleeCast!.ServerFailureSeen);
        Assert.Equal(1, h.ServerWrites);
    }
}
