using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (class on sight): the player cache learned a player's class only from a name query
// (also who, guild and arena rosters). The client asks for a name only when it does not hold it
// from an earlier login, and a group list carries no class, so a party member could stand in
// view all session with no class on record; the HealComm bridge then took a grouped shaman for a
// priest. A player's create carries race, class and sex (UNIT_FIELD_BYTES_0): they are now kept.
// Driven through the real SMSG_UPDATE_OBJECT handler.
public class PlayerClassOnSightTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 57);
    private static readonly WowGuid64 LegacyShaman = new(HighGuidTypeLegacy.Player, 4242);

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
            Session.ThreatTracker = new ThreatTracker(Session);
            Session.HealCommBridge = new HealCommBridge(Session);
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = _ => { };
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);
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

    // UNIT_FIELD_BYTES_0: race, class, sex, power type, one byte each.
    private static uint Bytes0(Race race, Class playerClass, Gender sex) =>
        (uint)race | ((uint)playerClass << 8) | ((uint)sex << 16);

    // A 1.12 create for another player.
    private static WorldPacket PlayerCreate(WowGuid64 guid, uint bytes0) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.CreateObject1);
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
        WriteValues(w, new SortedDictionary<int, uint>
        {
            [LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_SCALE_X)] = BitConverter.SingleToUInt32Bits(1.0f),
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_BYTES_0)] = bytes0,
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID)] = 1479,
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_NATIVEDISPLAYID)] = 1479,
        });
    });

    // A player never seen before, whose name the client never asked for.
    [Fact]
    public void PlayerSeen_IsRememberedWithRaceClassAndSex()
    {
        var h = new ClientHarness();

        h.Dispatch(PlayerCreate(LegacyShaman, Bytes0(Race.Tauren, Class.Shaman, Gender.Female)));

        var cached = h.State.CachedPlayers[LegacyShaman.To128(h.State)];
        Assert.Equal(Class.Shaman, cached.ClassId);
        Assert.Equal(Race.Tauren, cached.RaceId);
        Assert.Equal(Gender.Female, cached.SexId);
        Assert.Equal(Class.Shaman, h.State.GetUnitClass(LegacyShaman.To128(h.State)));
    }

    // A party member known by name only (the group list): seeing them adds the class and keeps
    // the name.
    [Fact]
    public void PartyMemberKnownByNameOnly_GainsTheClassOnSight()
    {
        var h = new ClientHarness();
        var shaman = LegacyShaman.To128(h.State);
        h.State.UpdatePlayerCache(shaman, new PlayerCache { Name = "Shaman" });

        h.Dispatch(PlayerCreate(LegacyShaman, Bytes0(Race.Tauren, Class.Shaman, Gender.Male)));

        Assert.Equal("Shaman", h.State.GetPlayerName(shaman));
        Assert.Equal(Class.Shaman, h.State.CachedPlayers[shaman].ClassId);
    }

    // A class byte that is not a class leaves the cache alone.
    [Fact]
    public void PlayerSeenWithNoClass_IsNotRemembered()
    {
        var h = new ClientHarness();

        h.Dispatch(PlayerCreate(LegacyShaman, Bytes0(Race.Tauren, Class.None, Gender.Male)));

        Assert.False(h.State.CachedPlayers.ContainsKey(LegacyShaman.To128(h.State)));
    }
}
