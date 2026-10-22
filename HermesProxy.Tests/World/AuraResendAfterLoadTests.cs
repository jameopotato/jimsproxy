using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (an aura's looping animation stuck after a loading screen, 2026-10-21): on every
// CMSG_LOADING_SCREEN_NOTIFY(Showing = 0) the proxy re-sends the player's auras as a full list.
// After a login or a far teleport that repeated the list the player's create had just given, and
// the repeat (a new CastID per entry) left the Skull of Impending Doom's looping pose playing after
// the buff ended. Now the re-send is skipped when the player's create came inside the loading
// screen, and after a /reload (no create) an empty full list goes first. Driven through the real
// SMSG_UPDATE_OBJECT handler and the real loading-screen handler.
public class AuraResendAfterLoadTests
{
    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 10);
    private static readonly WowGuid64 LegacyOther = new(HighGuidTypeLegacy.Player, 40);
    private const uint Skull = 5024;

    private sealed class Harness
    {
        public readonly WorldClient Client = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();
        public readonly WorldSocket Socket;
        private readonly List<ServerPacket> _sent = new();

        public Harness()
        {
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Client;
            Session.ThreatTracker = new ThreatTracker(Session);
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            State.CurrentMapId = 529;
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };
            Socket = WorldSocket.CreateForTest(Session);
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);

        public void LoadingScreen(bool showing)
        {
            var notify = (LoadingScreenNotify)RuntimeHelpers.GetUninitializedObject(typeof(LoadingScreenNotify));
            notify.MapID = 529;
            notify.Showing = showing;
            typeof(WorldSocket).GetMethod("HandleLoadScreen", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Socket, new object[] { notify });
        }

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
        }

        // Every aura update the client got for the player, in order: "all [5024]", "all []", "part [...]".
        public List<string> PlayerAuraUpdates()
        {
            List<ServerPacket> sent;
            lock (_sent) sent = new List<ServerPacket>(_sent);
            var player = State.CurrentPlayerGuid;
            return sent.OfType<AuraUpdate>().Where(u => u.UnitGUID == player)
                .Select(u => (u.UpdateAll ? "all [" : "part [") +
                             string.Join(",", u.Auras.Where(a => a.AuraData != null).Select(a => a.AuraData!.SpellID)) + "]")
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

    // A 1.12 CreateObject2 for a player, with the given spell in aura slot 0 (none when 0).
    private static WorldPacket PlayerCreate(WowGuid64 guid, uint auraSpell) => Legacy(Opcode.SMSG_UPDATE_OBJECT, w =>
    {
        w.WriteUInt32(1);
        w.WriteUInt8(0);
        w.WriteUInt8((byte)UpdateTypeLegacy.CreateObject2);
        w.WritePackedGuid(guid);
        w.WriteUInt8((byte)ObjectTypeLegacy.Player);
        w.WriteUInt8((byte)UpdateFlag.Living);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteVector3(new Vector3(1282.58f, 1278.36f, -17.22f));
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
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID)] = 1478,
            [LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_NATIVEDISPLAYID)] = 1478,
        };
        if (auraSpell != 0)
            fields[LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA)] = auraSpell;
        var mask = new uint[PlayerMaskBlocks];
        foreach (int field in fields.Keys)
            mask[field / 32] |= 1u << (field % 32);
        w.WriteUInt8((byte)mask.Length);
        foreach (uint block in mask)
            w.WriteUInt32(block);
        foreach (uint value in fields.Values)
            w.WriteUInt32(value);
    });

    // Login and far teleport: the create inside the loading screen lists the aura; no re-send follows.
    [Fact]
    public void CreateInsideTheLoadingScreen_TheReSendIsSkipped()
    {
        var h = new Harness();
        h.LoadingScreen(true);
        h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all [5024]" }, h.PlayerAuraUpdates());
    }

    // The 21:55:11 BG leave: a second loading screen, a second create; still one list per create.
    [Fact]
    public void TwoTeleports_EachCreateIsTheOnlyList()
    {
        var h = new Harness();
        h.LoadingScreen(true);
        h.Dispatch(PlayerCreate(LegacyPlayer, 0));
        h.LoadingScreen(false);
        h.LoadingScreen(true);
        h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all [5024]" }, h.PlayerAuraUpdates());
    }

    // /reload: no create inside the loading screen. An empty full list, then the full list.
    [Fact]
    public void Reload_EmptyListFirst_ThenTheFullList()
    {
        var h = new Harness();
        h.LoadingScreen(true);
        h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
        h.LoadingScreen(false);
        h.ClearSent();

        h.LoadingScreen(true);
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all []", "all [5024]" }, h.PlayerAuraUpdates());
    }

    // A create before the loading screen does not count: Showing = 1 clears the mark.
    [Fact]
    public void CreateBeforeTheLoadingScreen_DoesNotSkipTheReSend()
    {
        var h = new Harness();
        h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
        h.ClearSent();

        h.LoadingScreen(true);
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all []", "all [5024]" }, h.PlayerAuraUpdates());
    }

    // Another player's create inside the loading screen does not count.
    [Fact]
    public void AnotherPlayersCreate_DoesNotSkipTheReSend()
    {
        var h = new Harness();
        h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
        h.ClearSent();

        h.LoadingScreen(true);
        h.Dispatch(PlayerCreate(LegacyOther, Skull));
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all []", "all [5024]" }, h.PlayerAuraUpdates());
    }

    // /reload with no auras: the one empty full list, as before.
    [Fact]
    public void ReloadWithNoAuras_OneEmptyList()
    {
        var h = new Harness();
        h.Dispatch(PlayerCreate(LegacyPlayer, 0));
        h.ClearSent();

        h.LoadingScreen(true);
        h.LoadingScreen(false);

        Assert.Equal(new[] { "all []" }, h.PlayerAuraUpdates());
    }

    // Kill switch off: the re-send as before, after a create too.
    [Fact]
    public void KillSwitchOff_TheReSendAsBefore()
    {
        bool before = global::Framework.Settings.AuraResendAfterLoadFix;
        global::Framework.Settings.AuraResendAfterLoadFix = false;
        try
        {
            var h = new Harness();
            h.LoadingScreen(true);
            h.Dispatch(PlayerCreate(LegacyPlayer, Skull));
            h.LoadingScreen(false);
            h.LoadingScreen(true);
            h.LoadingScreen(false);

            Assert.Equal(new[] { "all [5024]", "all [5024]", "all [5024]" }, h.PlayerAuraUpdates());
        }
        finally
        {
            global::Framework.Settings.AuraResendAfterLoadFix = before;
        }
    }
}
