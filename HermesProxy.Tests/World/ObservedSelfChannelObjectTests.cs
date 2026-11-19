using System;
using System.Collections.Frozen;
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

// JimsProxy (observer self-channel visual): another player's Evocation showed no animation on this
// client (a party mage, 2026-10-20). Kronos sends the caster's UNIT_CHANNEL_SPELL but leaves
// UNIT_FIELD_CHANNEL_OBJECT at 0 when the caster channels on itself, so the client got the channel
// spell with an empty channel object list. A native server lists the caster as its own channel object
// for a self-channeled spell. Driven through the real SMSG_UPDATE_OBJECT handler.
[Collection("GameDataStaticState")]
public class ObservedSelfChannelObjectTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 10);
    private static readonly WowGuid64 LegacyMage = new(HighGuidTypeLegacy.Player, 40);
    private static readonly WowGuid64 LegacyFriend = new(HighGuidTypeLegacy.Player, 41);
    private static readonly WowGuid64 LegacyMob = new(HighGuidTypeLegacy.Creature, 1234, 77);

    private const uint Evocation = 12051;      // self-channeled (AttributesEx 0x40)
    private const uint HeavyRuneclothBandage = 18610; // First Aid: self-channeled, also cast on others
    private const uint ArcaneMissiles = 5143;  // channeled on a target, not self-channeled

    private sealed class ClientHarness : IDisposable
    {
        public readonly WorldClient Client = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();
        private readonly List<ServerPacket> _sent = new();
        private readonly FrozenSet<uint> _previousSelfChanneled = GameData.SelfChanneledSpells;

        public ClientHarness()
        {
            GameData.SelfChanneledSpells = new HashSet<uint> { Evocation, HeavyRuneclothBandage }.ToFrozenSet();
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

        public void Dispose() => GameData.SelfChanneledSpells = _previousSelfChanneled;

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);

        // The channel object every UPDATE_OBJECT since the last clear gave the unit: null when absent.
        public List<WowGuid128?> ChannelObjectsGiven(WowGuid64 unit)
        {
            List<ServerPacket> sent;
            lock (_sent) sent = new List<ServerPacket>(_sent);
            var guid = unit.To128(State);
            return sent.OfType<UpdateObject>()
                       .SelectMany(p => p.ObjectUpdates.Where(u => u.Guid == guid))
                       .Select(u => u.UnitData.ChannelObject)
                       .ToList();
        }
    }

    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    private static int PlayerMaskBlocks => (LegacyVersion.GetUpdateField(PlayerField.PLAYER_END) + 31) / 32;

    private static void WriteValues(WorldPacket w, SortedDictionary<int, uint> fields)
    {
        var mask = new uint[PlayerMaskBlocks];
        foreach (int field in fields.Keys)
            mask[field / 32] |= 1u << (field % 32);
        w.WriteUInt8((byte)mask.Length);
        foreach (uint block in mask)
            w.WriteUInt32(block);
        foreach (uint value in fields.Values)
            w.WriteUInt32(value);
    }

    private static int ChannelSpellField => LegacyVersion.GetUpdateField(UnitField.UNIT_CHANNEL_SPELL);
    private static int ChannelObjectField => LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_CHANNEL_OBJECT);

    private static void SetChannelObject(SortedDictionary<int, uint> fields, WowGuid64 target)
    {
        fields[ChannelObjectField] = (uint)(target.Low & 0xFFFFFFFF);
        fields[ChannelObjectField + 1] = (uint)(target.Low >> 32);
    }

    // A 1.12 create for a player, channeling the given spell (none when 0). The session player's own
    // create is a CreateObject2, as Kronos sends it.
    private static WorldPacket PlayerCreate(WowGuid64 guid, uint channelSpell = 0) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)(guid == LegacyPlayer ? UpdateTypeLegacy.CreateObject2 : UpdateTypeLegacy.CreateObject1));
        w.WritePackedGuid(guid);
        w.WriteUInt8((byte)ObjectTypeLegacy.Player);
        w.WriteUInt8((byte)UpdateFlag.Living);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteVector3(new Vector3(-8913.23f, 554.63f, 93.79f));
        w.WriteFloat(0f);
        w.WriteUInt32(0);
        w.WriteFloat(2.5f);
        w.WriteFloat(7.0f);
        w.WriteFloat(4.5f);
        w.WriteFloat(4.722222f);
        w.WriteFloat(2.5f);
        w.WriteFloat(3.141594f);
        var fields = new SortedDictionary<int, uint>
        {
            [LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_SCALE_X)] = BitConverter.SingleToUInt32Bits(1.0f),
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID)] = 1479,
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_NATIVEDISPLAYID)] = 1479,
        };
        if (channelSpell != 0)
            fields[ChannelSpellField] = channelSpell;
        WriteValues(w, fields);
    });

    // A 1.12 values update: the channel spell, and the channel object when given.
    private static WorldPacket Channel(WowGuid64 guid, uint spellId, WowGuid64? channelObject = null) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.Values);
        w.WritePackedGuid(guid);
        var fields = new SortedDictionary<int, uint> { [ChannelSpellField] = spellId };
        if (channelObject != null)
            SetChannelObject(fields, channelObject.Value);
        WriteValues(w, fields);
    });

    private static WorldPacket ChannelObjectOnly(WowGuid64 guid, WowGuid64 channelObject) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.Values);
        w.WritePackedGuid(guid);
        var fields = new SortedDictionary<int, uint>();
        SetChannelObject(fields, channelObject);
        WriteValues(w, fields);
    });

    [Fact]
    public void AnotherPlayersEvocation_ListsTheCasterAsItsChannelObject()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyMage));
        h.ClearSent();

        h.Dispatch(Channel(LegacyMage, Evocation));

        Assert.Equal(new WowGuid128?[] { LegacyMage.To128(h.State) }, h.ChannelObjectsGiven(LegacyMage));
    }

    [Fact]
    public void TheEndOfAnotherPlayersEvocation_ClearsTheChannelObject()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyMage));
        h.Dispatch(Channel(LegacyMage, Evocation));
        h.ClearSent();

        h.Dispatch(Channel(LegacyMage, 0));

        Assert.Equal(new WowGuid128?[] { WowGuid128.Empty }, h.ChannelObjectsGiven(LegacyMage));
    }

    [Fact]
    public void APlayerComingIntoViewMidEvocation_IsCreatedWithItselfAsTheChannelObject()
    {
        using var h = new ClientHarness();

        h.Dispatch(PlayerCreate(LegacyMage, Evocation));

        Assert.Equal(new WowGuid128?[] { LegacyMage.To128(h.State) }, h.ChannelObjectsGiven(LegacyMage));
    }

    // A bandage on someone else: Kronos sends that player as the channel object; it is kept.
    [Fact]
    public void ASelfChanneledSpellCastOnAnotherPlayer_KeepsTheServersChannelObject()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyMage));
        h.ClearSent();

        h.Dispatch(Channel(LegacyMage, HeavyRuneclothBandage, LegacyFriend));
        h.Dispatch(Channel(LegacyMage, 0, WowGuid64.Empty));

        Assert.Equal(new WowGuid128?[] { LegacyFriend.To128(h.State), WowGuid128.Empty },
                     h.ChannelObjectsGiven(LegacyMage));
    }

    [Fact]
    public void AChannelObjectTheServerAlreadySent_IsNotReplaced()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyMage));
        h.Dispatch(ChannelObjectOnly(LegacyMage, LegacyMob));
        h.ClearSent();

        h.Dispatch(Channel(LegacyMage, Evocation));
        h.Dispatch(Channel(LegacyMage, 0));

        Assert.Equal(new WowGuid128?[] { null, null }, h.ChannelObjectsGiven(LegacyMage));
    }

    [Fact]
    public void AChannelThatIsNotSelfChanneled_IsLeftAsTheServerSentIt()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyMage));
        h.ClearSent();

        h.Dispatch(Channel(LegacyMage, ArcaneMissiles, LegacyMob));
        h.Dispatch(Channel(LegacyMage, 0, WowGuid64.Empty));
        h.Dispatch(Channel(LegacyMage, ArcaneMissiles));
        h.Dispatch(Channel(LegacyMage, 0));

        Assert.Equal(new WowGuid128?[] { LegacyMob.To128(h.State), WowGuid128.Empty, null, null },
                     h.ChannelObjectsGiven(LegacyMage));
    }

    // The player's own Evocation already shows: this client's own channel is not changed.
    [Fact]
    public void ThePlayersOwnEvocation_IsNotChanged()
    {
        using var h = new ClientHarness();
        h.Dispatch(PlayerCreate(LegacyPlayer));
        h.ClearSent();

        h.Dispatch(Channel(LegacyPlayer, Evocation));
        h.Dispatch(Channel(LegacyPlayer, 0));

        Assert.Equal(new WowGuid128?[] { null, null }, h.ChannelObjectsGiven(LegacyPlayer));
    }
}
