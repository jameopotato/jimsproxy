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

// JimsProxy (#417 symptom 2): the client keeps a player's object for a while after the player leaves its
// view and reuses it when the player comes back, mount cache included, and ignores a mount display equal
// to that cache. The 2026-10-18 two-box order: the observer last saw the dwarf mounted (14346), took a
// portal, the dwarf came back unmounted and mounted the same mount, which the observer never showed.
public class MountCacheResetTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 10);
    private static readonly WowGuid64 LegacyDwarf = new(HighGuidTypeLegacy.Player, 40);

    private const int Dwarf = 53;
    private const int Ram = 14346;
    private const int OtherRam = 14347;

    // ------------------------------------------------------------- the tracker alone

    private static readonly WowGuid128 Unit = new(40, 1);

    [Fact]
    public void Tracker_RemountAfterAnUnmountedRecreate_NeedsAReset()
    {
        var t = new MountCacheResetTracker();
        Assert.False(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
        Assert.False(t.OnUpdate(Unit, isCreate: true, mountInBlock: false, 0));

        Assert.True(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
        Assert.False(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
    }

    [Fact]
    public void Tracker_NoResetWhenTheClientLastSawThePlayerUnmounted()
    {
        var t = new MountCacheResetTracker();
        t.OnUpdate(Unit, isCreate: true, mountInBlock: true, Ram);
        t.OnUpdate(Unit, isCreate: false, mountInBlock: true, 0);
        t.OnUpdate(Unit, isCreate: true, mountInBlock: false, 0);

        Assert.False(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
    }

    [Fact]
    public void Tracker_NoResetForADifferentMountOrADismount()
    {
        var t = new MountCacheResetTracker();
        t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram);
        t.OnUpdate(Unit, isCreate: true, mountInBlock: false, 0);
        Assert.False(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, OtherRam));

        var d = new MountCacheResetTracker();
        d.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram);
        d.OnUpdate(Unit, isCreate: true, mountInBlock: false, 0);
        Assert.False(d.OnUpdate(Unit, isCreate: false, mountInBlock: true, 0));
        Assert.False(d.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
    }

    [Fact]
    public void Tracker_ACreateThatCarriesTheMountIsNeverHeld()
    {
        var t = new MountCacheResetTracker();
        t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram);
        t.OnUpdate(Unit, isCreate: true, mountInBlock: false, 0);

        Assert.False(t.OnUpdate(Unit, isCreate: true, mountInBlock: true, Ram));
        Assert.False(t.OnUpdate(Unit, isCreate: false, mountInBlock: true, Ram));
    }

    // ------------------------------------------------------------- through the real handler

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
            // The out-of-range path drops the unit's threat lists too.
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

    private static WorldPacket PlayerCreate(WowGuid64 guid, int mountDisplayId) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.CreateObject1);
        w.WritePackedGuid(guid);
        w.WriteUInt8((byte)ObjectTypeLegacy.Player);
        w.WriteUInt8((byte)UpdateFlag.Living);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteVector3(new Vector3(-4981.25f, -881.54f, 501.66f));
        w.WriteFloat(0f);
        w.WriteUInt32(0);
        w.WriteFloat(2.5f);
        w.WriteFloat(mountDisplayId != 0 ? 14.0f : 7.0f);
        w.WriteFloat(4.5f);
        w.WriteFloat(4.722222f);
        w.WriteFloat(2.5f);
        w.WriteFloat(3.141594f);
        var fields = new SortedDictionary<int, uint>
        {
            [LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_SCALE_X)] = BitConverter.SingleToUInt32Bits(1.0f),
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID)] = Dwarf,
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_NATIVEDISPLAYID)] = Dwarf,
        };
        if (mountDisplayId != 0)
            fields[LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_MOUNTDISPLAYID)] = (uint)mountDisplayId;
        WriteValues(w, fields);
    });

    private static WorldPacket MountChange(WowGuid64 guid, int mountDisplayId) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.Values);
        w.WritePackedGuid(guid);
        WriteValues(w, new SortedDictionary<int, uint>
        {
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_MOUNTDISPLAYID)] = (uint)mountDisplayId,
        });
    });

    private static WorldPacket OutOfRange(WowGuid64 guid) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.FarObjects);
        w.WriteInt32(1);
        w.WritePackedGuid(guid);
    });

    // The mount display each UPDATE_OBJECT the client got gives the dwarf: null when absent.
    private static List<int?> MountsGiven(ClientHarness h, WowGuid128 dwarf) =>
        h.Sent.OfType<UpdateObject>()
              .SelectMany(p => p.ObjectUpdates.Where(u => u.Guid == dwarf))
              .Select(u => u.UnitData.MountDisplayID)
              .ToList();

    private static void LastSeenMountedThenBackUnmounted(ClientHarness h)
    {
        h.Dispatch(PlayerCreate(LegacyDwarf, 0));
        h.Dispatch(MountChange(LegacyDwarf, Ram));
        h.Dispatch(OutOfRange(LegacyDwarf));
        h.Dispatch(PlayerCreate(LegacyDwarf, 0));
        h.ClearSent();
    }

    [Fact]
    public void RemountAfterThePortal_GetsA0First_InItsOwnPacket_ThenTheMount()
    {
        var h = new ClientHarness();
        var dwarf = LegacyDwarf.To128(h.State);
        LastSeenMountedThenBackUnmounted(h);

        h.Dispatch(MountChange(LegacyDwarf, Ram));

        Assert.Equal(new int?[] { null, 0, Ram }, MountsGiven(h, dwarf));
        Assert.Equal(3, h.Sent.OfType<UpdateObject>().Count(p => p.ObjectUpdates.Any(u => u.Guid == dwarf)));
    }

    [Fact]
    public void FirstMountInView_IsSentAsIs()
    {
        var h = new ClientHarness();
        var dwarf = LegacyDwarf.To128(h.State);
        h.Dispatch(PlayerCreate(LegacyDwarf, 0));
        h.ClearSent();

        h.Dispatch(MountChange(LegacyDwarf, Ram));

        Assert.Equal(new int?[] { Ram }, MountsGiven(h, dwarf));
    }

    [Fact]
    public void RemountAfterAPortal_WhenTheClientLastSawADismount_IsSentAsIs()
    {
        var h = new ClientHarness();
        var dwarf = LegacyDwarf.To128(h.State);
        h.Dispatch(PlayerCreate(LegacyDwarf, Ram));
        h.Dispatch(MountChange(LegacyDwarf, 0));
        h.Dispatch(OutOfRange(LegacyDwarf));
        h.Dispatch(PlayerCreate(LegacyDwarf, 0));
        h.ClearSent();

        h.Dispatch(MountChange(LegacyDwarf, Ram));

        Assert.Equal(new int?[] { Ram }, MountsGiven(h, dwarf));
    }

    [Fact]
    public void KillSwitchOff_SendsTheMountAsIs()
    {
        bool before = global::Framework.Settings.MountCacheResetSynth;
        global::Framework.Settings.MountCacheResetSynth = false;
        try
        {
            var h = new ClientHarness();
            var dwarf = LegacyDwarf.To128(h.State);
            LastSeenMountedThenBackUnmounted(h);

            h.Dispatch(MountChange(LegacyDwarf, Ram));

            Assert.Equal(new int?[] { Ram }, MountsGiven(h, dwarf));
        }
        finally
        {
            global::Framework.Settings.MountCacheResetSynth = before;
        }
    }
}
