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

// Unit scale v1 through the real SMSG_UPDATE_OBJECT handler: a player's or a humanoid NPC's
// 1.12 SCALE_X reaches the modern client divided by the client's own display factor; a
// beast and the session's pet keep the May 2026 per-case result exactly.
[Collection("GameDataStaticState")]
public class UnitScaleDispatchTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 10);
    private static readonly WowGuid64 LegacyGnome = new(HighGuidTypeLegacy.Player, 40);
    private static readonly WowGuid64 LegacyGnomeNpc = new(HighGuidTypeLegacy.Creature, 5100, 77);
    private static readonly WowGuid64 LegacyBat = new(HighGuidTypeLegacy.Creature, 1984, 78);
    private static readonly WowGuid64 LegacyTaurenNpc = new(HighGuidTypeLegacy.Creature, 3074, 79);

    private const uint GnomeMale = 1563;        // player race model, CMS 1.15
    private const uint PirateCostume = 7113;    // goblin model, CMS 1.5, race row -> goblin at 1.0
    private const uint GnomeNpcDisplay = 3040;  // Extra row: race gnome -> race model 1563 (1.15)
    private const uint BatDisplay = 4734;       // Greater Duskbat, CMS 0.15, no Extra row
    private const uint TaurenFemaleNpc = 1322;  // Extra row: tauren female, CMS 1.2 (factor 1.125)

    static UnitScaleDispatchTests() => UnitScaleTestData.EnsureLoaded();

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

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);

        // The ObjectData.Scale every UPDATE_OBJECT since the last clear gave the unit: null when absent.
        public List<float?> ScalesGiven(WowGuid64 unit)
        {
            List<ServerPacket> sent;
            lock (_sent) sent = new List<ServerPacket>(_sent);
            var guid = unit.To128(State);
            return sent.OfType<UpdateObject>()
                       .SelectMany(p => p.ObjectUpdates.Where(u => u.Guid == guid))
                       .Select(u => u.ObjectData.Scale)
                       .ToList();
        }
    }

    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    private static int MaskBlocks(ObjectTypeLegacy type) => type == ObjectTypeLegacy.Player
        ? (LegacyVersion.GetUpdateField(PlayerField.PLAYER_END) + 31) / 32
        : (LegacyVersion.GetUpdateField(UnitField.UNIT_END) + 31) / 32;

    private static void WriteValues(WorldPacket w, ObjectTypeLegacy type, SortedDictionary<int, uint> fields)
    {
        var mask = new uint[MaskBlocks(type)];
        foreach (int field in fields.Keys)
            mask[field / 32] |= 1u << (field % 32);
        w.WriteUInt8((byte)mask.Length);
        foreach (uint block in mask)
            w.WriteUInt32(block);
        foreach (uint value in fields.Values)
            w.WriteUInt32(value);
    }

    private static int Field<T>(T field) where T : Enum => LegacyVersion.GetUpdateField(field);

    // A 1.12 create: a player or a creature, living, with the given display and SCALE_X.
    private static WorldPacket Create(WowGuid64 guid, ObjectTypeLegacy type, uint displayId, float scale, uint entry = 0,
                                      WowGuid64? summonedBy = null) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)(guid == LegacyPlayer ? UpdateTypeLegacy.CreateObject2 : UpdateTypeLegacy.CreateObject1));
        w.WritePackedGuid(guid);
        w.WriteUInt8((byte)type);
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
            [Field(ObjectField.OBJECT_FIELD_SCALE_X)] = BitConverter.SingleToUInt32Bits(scale),
            [Field(UnitField.UNIT_FIELD_DISPLAYID)] = displayId,
            [Field(UnitField.UNIT_FIELD_NATIVEDISPLAYID)] = displayId,
            [Field(UnitField.UNIT_FIELD_LEVEL)] = 30,
        };
        if (entry != 0)
            fields[Field(ObjectField.OBJECT_FIELD_ENTRY)] = entry;
        if (summonedBy != null)
        {
            fields[Field(UnitField.UNIT_FIELD_SUMMONEDBY)] = (uint)(summonedBy.Value.Low & 0xFFFFFFFF);
            fields[Field(UnitField.UNIT_FIELD_SUMMONEDBY) + 1] = (uint)(summonedBy.Value.Low >> 32);
        }
        WriteValues(w, type, fields);
    });

    // A 1.12 values update carrying only a new DISPLAYID.
    private static WorldPacket DisplayChange(WowGuid64 guid, ObjectTypeLegacy type, uint displayId) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.Values);
        w.WritePackedGuid(guid);
        WriteValues(w, type, new SortedDictionary<int, uint> { [Field(UnitField.UNIT_FIELD_DISPLAYID)] = displayId });
    });

    private static void AssertScale(float expected, float? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, 0.0001f);
    }

    [Fact]
    public void AGnomePlayerCreate_ReachesTheClientAtTheServersSize()
    {
        var h = new ClientHarness();

        h.Dispatch(Create(LegacyGnome, ObjectTypeLegacy.Player, GnomeMale, 1.15f));

        // 1.15 / 1.15: the client draws its own 1.15 on top.
        AssertScale(1.0f, Assert.Single(h.ScalesGiven(LegacyGnome)));
    }

    [Fact]
    public void AGnomeNpcCreate_ReachesTheClientAtTheServersSize()
    {
        var h = new ClientHarness();

        h.Dispatch(Create(LegacyGnomeNpc, ObjectTypeLegacy.Unit, GnomeNpcDisplay, 1.15f, entry: 5100));

        // Display 3040 is CMS 1.0 with a gnome race row: the client draws 1.15 from the race model.
        AssertScale(1.0f, Assert.Single(h.ScalesGiven(LegacyGnomeNpc)));
    }

    [Fact]
    public void ABeastCreate_KeepsTheMay2026Result()
    {
        var h = new ClientHarness();

        h.Dispatch(Create(LegacyBat, ObjectTypeLegacy.Unit, BatDisplay, 0.43f, entry: 1984));

        // The May NPC branch strips a pre-scaled wire (0.43 within [CMS_v 0.15, 5 x CMS_v)) to 1.0
        // and emits 1.0 / CMS_m x CMS_v = 1.0. Not 0.43 (a plain forward), not 2.87 (a translation).
        AssertScale(1.0f, Assert.Single(h.ScalesGiven(LegacyBat)));
    }

    [Fact]
    public void ADisplayOnlyUpdateOnAPlayer_ResendsTheScaleWithTheNewFactor()
    {
        var h = new ClientHarness();
        h.Dispatch(Create(LegacyGnome, ObjectTypeLegacy.Player, GnomeMale, 1.15f));
        h.ClearSent();

        h.Dispatch(DisplayChange(LegacyGnome, ObjectTypeLegacy.Player, PirateCostume));

        // No new SCALE_X, a new display: 1.15 / 1.5.
        AssertScale(1.15f / 1.5f, Assert.Single(h.ScalesGiven(LegacyGnome)));
    }

    [Fact]
    public void AHumanoidUnitThatIsTheSessionsPet_KeepsTheMay2026PetResult()
    {
        var h = new ClientHarness();
        h.State.CurrentPetGuid = LegacyTaurenNpc.To128(h.State);

        h.Dispatch(Create(LegacyTaurenNpc, ObjectTypeLegacy.Unit, TaurenFemaleNpc, 1.5f, entry: 3074));

        // The May pet branch strips the pre-scaled 1.5 (within [CMS_v 1.2, 1.5 x CMS_v)) to 1.0 and
        // emits 1.0 / CMS_m 1.2 x CMS_v 1.2 = 1.0. A translation would have sent 1.5 / 1.125.
        AssertScale(1.0f, Assert.Single(h.ScalesGiven(LegacyTaurenNpc)));
    }

    [Fact]
    public void AHumanoidUnitSummonedByThePlayer_KeepsTheMay2026PetResult()
    {
        var h = new ClientHarness();

        h.Dispatch(Create(LegacyTaurenNpc, ObjectTypeLegacy.Unit, TaurenFemaleNpc, 1.5f, entry: 3074, summonedBy: LegacyPlayer));

        AssertScale(1.0f, Assert.Single(h.ScalesGiven(LegacyTaurenNpc)));
    }
}
