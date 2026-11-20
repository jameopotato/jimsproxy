using System;
using System.Collections.Generic;
using System.Linq;
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

// JimsProxy (announced heals): what the HealComm bridge does with a heal a group member's addon
// announces ("Heal/<target>/<amount>/<castMs>/").
//  - A healer in range: the server's own cast packets are the truth. The heal's prediction ends
//    when the server says the cast completed or failed, and an announcement that arrives after
//    that is dropped. Opted in, the announced amount is also attached to the server's cast for
//    the client's own incoming-heal number, by sending that cast's start a second time.
//  - A healer out of range: the spell is the one the healer's own proxy named (a fifth field), or
//    the one class and cast time leave no doubt about, or a stand-in. JimsPlus is told the target
//    and whether the spell is known. The stand-in is the priest spell named plain "Heal". Our
//    own announcement carries the fifth field only when opted in.
//  - pfUI's "HealStop" stops a heal; a pushed-back heal keeps its prediction until its new end.
// Server packets go through the real handlers; announcements through the bridge's inbound entry
// point, as the chat handler calls it.
public class HealCommAnnouncedHealTests
{
    private const uint FlashHealRank7 = 10917;
    private const uint FlashHealRank1 = 2061;
    private const uint FlashOfLightRank1 = 19750;
    private const uint HolyLightRank1 = 635;
    private const uint HealingTouchRank1 = 5185;
    private const uint HealingWaveRank1 = 331;
    private const uint PriestHeal = 2054;
    private const uint InnerFocus = 14751;
    private const uint Fireball = 133;
    private const uint CastTimeMs = 1500;

    private static readonly WowGuid64 LegacyPlayer = new(HighGuidTypeLegacy.Player, 57);
    private static readonly WowGuid64 LegacyHealer = new(HighGuidTypeLegacy.Player, 4242);
    private static readonly WowGuid64 LegacyTarget = new(HighGuidTypeLegacy.Player, 77);

    private sealed class ClientHarness
    {
        public readonly WorldClient Client = new();
        public readonly GlobalSessionData Session;
        public readonly GameSessionData State = GameSessionData.CreateForTesting();
        public readonly HealCommBridge Bridge;
        public readonly WowGuid128 Healer;
        public long NowMs = 1_000_000;
        private readonly List<ServerPacket> _sent = new();

        public ClientHarness(Class healerClass = Class.None)
        {
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Client;
            Session.ThreatTracker = new ThreatTracker(Session);
            Bridge = new HealCommBridge(Session) { ClockMsForTest = () => NowMs };
            Session.HealCommBridge = Bridge;
            State.CurrentPlayerGuid = LegacyPlayer.To128(State);
            State.CurrentMapId = 0;
            State.CurrentPlayerStorage = new CurrentPlayerStorage(Session);
            typeof(CurrentPlayerStorage).GetProperty(nameof(CurrentPlayerStorage.Settings))!
                .SetValue(State.CurrentPlayerStorage, new PlayerSettings(Session));
            Client.AttachSessionForTest(Session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };

            Healer = LegacyHealer.To128(State);
            State.UpdatePlayerCache(Healer, new PlayerCache { Name = "Healer", ClassId = healerClass });
            State.UpdatePlayerCache(LegacyTarget.To128(State), new PlayerCache { Name = "Target" });
        }

        public List<ServerPacket> Sent
        {
            get { lock (_sent) return new List<ServerPacket>(_sent); }
        }

        public List<SpellStart> SpellStarts => Sent.OfType<SpellStart>().ToList();

        // The LHC40 stop messages the bridge made for the client ("S::<spell>:<interrupted>:").
        public List<string> Lhc40Stops => Sent.OfType<ChatPkt>()
            .Where(c => c.Prefix == HealCommBridge.LhcPrefix && c.ChatText.StartsWith("S:"))
            .Select(c => c.ChatText).ToList();

        // The JimsPlus sideband lines about announced heals.
        public List<string> Sideband => Sent.OfType<ChatPkt>()
            .Where(c => c.SlashCmd == ChatMessageTypeModern.System && c.ChatText.StartsWith("JP_HC:"))
            .Select(c => c.ChatText).ToList();

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
        }

        public void EnableSideband()
        {
            State.JimsPlusSideband = true;
            State.JimsPlusSidebandAffirmedMs = Environment.TickCount64;
        }

        public void Dispatch(WorldPacket legacy) => Client.DispatchLegacyPacketForTest(legacy);

        // The healer's HealComm-1.0 message, as the chat handler hands it to the bridge.
        // Returns the addon prefix and body that go on to the client.
        public (string Prefix, string Body) Announce(string body)
        {
            string prefix = HealCommBridge.HcPrefix;
            Assert.True(Bridge.TryTranslateInboundAddon(Healer, "Healer", ref prefix, ref body));
            return (prefix, body);
        }
    }

    private const string DroppedPrefix = "_HC_DROP";

    private static WorldPacket Legacy(Opcode opcode, Action<WorldPacket> body)
    {
        var writer = new WorldPacket(opcode);
        body(writer);
        return new WorldPacket(writer.GetOpcode(), writer.GetData());
    }

    // 1.12 SMSG_SPELL_START: packed caster guid twice, spell, cast flags, cast time, then the
    // target block (flags and, for a unit target, its packed guid).
    private static WorldPacket SpellStartPacket(WowGuid64 caster, uint spellId, uint castTimeMs) =>
        Legacy(Opcode.SMSG_SPELL_START, w =>
        {
            w.WritePackedGuid(caster);
            w.WritePackedGuid(caster);
            w.WriteUInt32(spellId);
            w.WriteUInt16(0);
            w.WriteUInt32(castTimeMs);
            w.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
            w.WritePackedGuid(LegacyTarget);
        });

    // 1.12 SMSG_SPELL_GO: packed caster guid twice, spell, cast flags, the hit list (count and
    // full guids), the miss count, then the target block.
    private static WorldPacket SpellGoPacket(WowGuid64 caster, uint spellId) =>
        Legacy(Opcode.SMSG_SPELL_GO, w =>
        {
            w.WritePackedGuid(caster);
            w.WritePackedGuid(caster);
            w.WriteUInt32(spellId);
            w.WriteUInt16(0);
            w.WriteUInt8(1);
            w.WriteGuid(LegacyTarget);
            w.WriteUInt8(0);
            w.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
            w.WritePackedGuid(LegacyTarget);
        });

    // 1.12 SMSG_SPELL_FAILURE as Kronos broadcasts it to observers: the caster's full guid, the
    // spell and a reason byte (always 0). An interrupt sends an observer this and then
    // SMSG_SPELL_FAILED_OTHER.
    private static WorldPacket SpellFailurePacket(WowGuid64 caster, uint spellId) =>
        Legacy(Opcode.SMSG_SPELL_FAILURE, w =>
        {
            w.WriteGuid(caster);
            w.WriteUInt32(spellId);
            w.WriteUInt8(0);
        });

    // 1.12 SMSG_SPELL_FAILED_OTHER: the caster's full guid and the spell.
    private static WorldPacket SpellFailedOtherPacket(WowGuid64 caster, uint spellId) =>
        Legacy(Opcode.SMSG_SPELL_FAILED_OTHER, w =>
        {
            w.WriteGuid(caster);
            w.WriteUInt32(spellId);
        });

    private const string HealAnnouncement = "Heal/Target/900/1500/";

    // ---- A healer in range: the server's packets end the heal ----------------------------------

    // The server says the cast completed: the prediction ends there, as a completion, and the
    // healer's own stop (HealComm-1.0 sends none for a completed heal) has nothing left to end.
    [Fact]
    public void RealCastCompletes_EndsTheAdoptedHeal()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellGoPacket(LegacyHealer, FlashHealRank7));

        Assert.Equal(new[] { $"S::{FlashHealRank7}:0:" }, h.Lhc40Stops);
        h.ClearSent();
        h.Announce("Healstop");
        Assert.Empty(h.Lhc40Stops);
    }

    // The server says the cast failed (the healer moved, was interrupted): the prediction ends
    // there, as an interrupt, whatever the healer's addon goes on to send. An observer gets two
    // packets for one interrupt; the heal ends once.
    [Fact]
    public void RealCastFails_EndsTheAdoptedHealAsInterrupted()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellFailurePacket(LegacyHealer, FlashHealRank7));
        h.Dispatch(SpellFailedOtherPacket(LegacyHealer, FlashHealRank7));

        Assert.Equal(new[] { $"S::{FlashHealRank7}:1:" }, h.Lhc40Stops);
    }

    // A 1.12 failure names only caster and spell, and the server also sends one as the late echo
    // of an EARLIER cast of the same spell, after the next cast has begun (44 logged casts of one
    // healer, about 80 ms in). The proxy pairs it to the earlier cast; the live heal's prediction
    // must stay, and end when that heal lands.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureOfAnEarlierCastOfTheSameSpell_DoesNotEndTheLiveHeal(bool failureOpcode)
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs)); // recast before its end was reported
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(failureOpcode ? SpellFailurePacket(LegacyHealer, FlashHealRank7) : SpellFailedOtherPacket(LegacyHealer, FlashHealRank7));
        Assert.Empty(h.Lhc40Stops);

        h.Dispatch(SpellGoPacket(LegacyHealer, FlashHealRank7));
        Assert.Equal(new[] { $"S::{FlashHealRank7}:0:" }, h.Lhc40Stops);
    }

    // An instant used mid-cast (a trinket, Inner Focus) completes without ending the heal.
    [Fact]
    public void AnotherSpellCompleting_DoesNotEndTheAdoptedHeal()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellGoPacket(LegacyHealer, InnerFocus));

        Assert.Empty(h.Lhc40Stops);
    }

    // The announcement trails the cast by the healer's round trip. If the cast has ended by then
    // the client has seen all of it: nothing is synthesized and nothing is forwarded.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HealAnnouncedAfterTheRealCastEnded_IsDropped(bool completed)
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.NowMs += 60;
        h.Dispatch(completed ? SpellGoPacket(LegacyHealer, FlashHealRank7) : SpellFailedOtherPacket(LegacyHealer, FlashHealRank7));
        h.NowMs += 60;
        h.ClearSent();

        var (prefix, _) = h.Announce(HealAnnouncement);

        Assert.Equal(DroppedPrefix, prefix);
        Assert.Empty(h.SpellStarts);
        Assert.Empty(h.Lhc40Stops);
    }

    // A healer who came into range after an announced heal started: the server sends that heal's
    // completion with no start before it. The synthesized cast ends there.
    [Fact]
    public void RealHealCompletes_EndsASynthesizedCast()
    {
        var h = new ClientHarness(Class.Priest);
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellGoPacket(LegacyHealer, FlashHealRank7));

        Assert.Single(h.Sent.OfType<SpellFailedOther>());
        Assert.Equal(new[] { $"S::{FlashHealRank1}:0:" }, h.Lhc40Stops);
    }

    // ---- A healer in range: the client's own incoming-heal number (opt-in) -----------------------

    // The client takes that number only from a cast-start packet, and the server's start went
    // out before the amount was announced. Read from the 1.14.2 client: a second start with the
    // same cast id records the prediction on the running cast and, for another unit, costs only a
    // second combat log line, provided it repeats the visual already sent.
    [Fact]
    public void AdoptedHeal_SendsTheCastStartAgainWithTheAmount_WhenOptedIn()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        var first = Assert.Single(h.SpellStarts);
        h.NowMs += 180; // the healer's round trip
        h.ClearSent();

        h.Announce(HealAnnouncement);

        var repeat = Assert.Single(h.SpellStarts);
        Assert.Equal(first.Cast.CastID, repeat.Cast.CastID);
        Assert.Equal(first.Cast.SpellID, repeat.Cast.SpellID);
        Assert.Equal(first.Cast.SpellXSpellVisualID, repeat.Cast.SpellXSpellVisualID);
        Assert.Equal(h.Healer, repeat.Cast.CasterUnit);
        Assert.Equal(first.Cast.CastFlags | (uint)CastFlag.HealPrediction, repeat.Cast.CastFlags);
        Assert.Equal(CastTimeMs - 180, repeat.Cast.CastTime); // the time left, so the cast still ends on time
        Assert.Equal(LegacyTarget.To128(h.State), repeat.Cast.Target.Unit);
        Assert.Equal(900u, repeat.Cast.Predict.Points);
    }

    // JimsPlus is told before the repeat, so the second combat log line does not restart its bar.
    [Fact]
    public void RepeatedCastStart_IsAnnouncedToJimsPlusFirst()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.EnableSideband();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);

        Assert.Equal(new[] { $"JP_HC:R:{h.Healer.ToClientGuidString()}" }, h.Sideband);
        var sent = h.Sent;
        Assert.True(sent.FindIndex(p => p is ChatPkt c && c.ChatText.StartsWith("JP_HC:R:")) < sent.FindIndex(p => p is SpellStart));
    }

    // The server's own finish removes the entry on the client: the bridge sends nothing more for
    // it, then or later.
    [Fact]
    public void ServerEndOfTheCast_NeedsNoEndFromTheBridge()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellGoPacket(LegacyHealer, FlashHealRank7));
        h.Bridge.OnUnitLeavingView(h.Healer);

        Assert.Empty(h.Sent.OfType<SpellFailedOther>());
    }

    // The client removes the entry only on the cast's finish or failure, and a failure only
    // while the caster's unit exists. A healer who leaves view mid-cast gets the failure first.
    [Fact]
    public void HealerLeavingViewMidCast_EndsTheAttachedAmount()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        var castId = Assert.Single(h.SpellStarts).Cast.CastID;
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Bridge.OnUnitLeavingView(h.Healer);

        var failed = Assert.Single(h.Sent.OfType<SpellFailedOther>());
        Assert.Equal(castId, failed.CastID);
        Assert.Equal(h.Healer, failed.CasterUnit);
        Assert.NotEqual(0, failed.Reason); // reason 0 skips the client's prediction cleanup
    }

    // The same through the real handler: the failure goes out ahead of the update that takes the
    // unit off the client.
    [Fact]
    public void HealerDestroyedMidCast_GetsTheFailureBeforeTheDestroy()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(Legacy(Opcode.SMSG_DESTROY_OBJECT, w => w.WriteGuid(LegacyHealer)));

        var sent = h.Sent;
        int failure = sent.FindIndex(p => p is SpellFailedOther);
        int destroy = sent.FindIndex(p => p is UpdateObject u && u.DestroyedGuids.Contains(h.Healer));
        Assert.True(failure >= 0 && destroy >= 0 && failure < destroy);
    }

    // No finish and no failure from the server (the packet was missed, or paired to another cast):
    // the bridge ends the entry itself, well after the cast should have ended.
    [Fact]
    public async Task NoEndFromTheServer_EndsTheAttachedAmountAfterAGrace()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true, NativeAmountCleanupGrace = TimeSpan.FromMilliseconds(150) } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, 200));
        var castId = Assert.Single(h.SpellStarts).Cast.CastID;
        h.Announce("Heal/Target/900/200/");
        h.ClearSent();

        await Task.Delay(200, TestContext.Current.CancellationToken); // the cast's expected end
        Assert.Empty(h.Sent.OfType<SpellFailedOther>());

        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(castId, Assert.Single(h.Sent.OfType<SpellFailedOther>()).CastID);
    }

    // The healer's addon saying stop ends the prediction LibHealComm-4.0 shows. The client's own
    // entry belongs to the server's cast and waits for the server's word (a failure sent for a
    // cast that is still running would end it on the client).
    [Fact]
    public void HealersOwnStop_DoesNotEndTheAttachedAmount()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Announce("Healstop");

        Assert.Equal(new[] { $"S::{FlashHealRank7}:1:" }, h.Lhc40Stops);
        Assert.Empty(h.Sent.OfType<SpellFailedOther>());
    }

    // A new cast by the same healer while an amount is still attached to the earlier one (its end
    // was never reported): the client does not remove an entry for a new start, so the bridge does.
    [Fact]
    public void NewCastStart_EndsAnAmountLeftOnTheEarlierCast()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        var firstCastId = Assert.Single(h.SpellStarts).Cast.CastID;
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));

        Assert.Equal(firstCastId, Assert.Single(h.Sent.OfType<SpellFailedOther>()).CastID);
    }

    // ---- The stop's channel ----------------------------------------------------------------------

    // LibHealComm-4.0 drops a message that is not on the channel it sends on itself, and the client
    // names the channel from the chat type. In a battleground the library is on INSTANCE_CHAT, which
    // the 1.14.2 client gives only to the battleground chat types; a stop sent as party or raid
    // chat was dropped there, and the heal it should have ended never expires on its own.
    [Theory]
    [InlineData(true, false, ChatMessageTypeModern.Battleground)]
    [InlineData(true, true, ChatMessageTypeModern.Battleground)]
    [InlineData(false, true, ChatMessageTypeModern.Raid)]
    [InlineData(false, false, ChatMessageTypeModern.Party)]
    public void GroupAddonChatType_FollowsTheChannelLibHealCommIsOn(bool inBattleground, bool inRaid, ChatMessageTypeModern expected)
    {
        Assert.Equal(expected, HealCommBridge.GroupAddonChatType(inBattleground, inRaid));
    }

    // The same healer's failure, with no cast start to pair it to, cannot be told from the echo of
    // an earlier cast: the synthesized cast is left to the healer's own stop.
    [Fact]
    public void RealFailureWithNoCastStart_DoesNotEndASynthesizedCast()
    {
        var h = new ClientHarness(Class.Priest);
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellFailedOtherPacket(LegacyHealer, FlashHealRank7));

        Assert.Empty(h.Lhc40Stops);
        h.Announce("Healstop");
        Assert.Equal(new[] { $"S::{FlashHealRank1}:1:" }, h.Lhc40Stops);
    }

    // ---- A healer out of range: which spell -----------------------------------------------------

    [Theory]
    [InlineData(Class.Paladin, 1500, FlashOfLightRank1)]
    [InlineData(Class.Paladin, 2500, HolyLightRank1)]
    [InlineData(Class.Priest, 1500, FlashHealRank1)]
    [InlineData(Class.Priest, 1400, FlashHealRank1)]
    [InlineData(Class.Druid, 2500, HealingTouchRank1)]
    [InlineData(Class.Druid, 3000, HealingTouchRank1)]
    [InlineData(Class.Druid, 3500, HealingTouchRank1)]
    [InlineData(Class.Shaman, 2000, HealingWaveRank1)]
    [InlineData(Class.Shaman, 3000, HealingWaveRank1)]
    // More than one spell fits: Heal, Greater Heal or Lesser Heal; Healing Touch or Regrowth;
    // Lesser Healing Wave or Healing Wave; Healing Wave or Chain Heal.
    [InlineData(Class.Priest, 2500, 0u)]
    [InlineData(Class.Priest, 3000, 0u)]
    [InlineData(Class.Druid, 2000, 0u)]
    [InlineData(Class.Druid, 1500, 0u)]
    [InlineData(Class.Shaman, 1500, 0u)]
    [InlineData(Class.Shaman, 2500, 0u)]
    // A cast time nobody was seen casting at (haste), a class not known, a class that does not heal.
    [InlineData(Class.Paladin, 1275, 0u)]
    [InlineData(Class.None, 1500, 0u)]
    [InlineData(Class.Warrior, 1500, 0u)]
    public void IdentifyDirectHeal_NamesASpellOnlyWhenOneFits(Class healerClass, int castTimeMs, uint expected)
    {
        Assert.Equal(expected, HealCommBridge.IdentifyDirectHeal(healerClass, castTimeMs));
    }

    // Class and cast time leave one spell: the synthesized cast and the prediction carry it.
    [Fact]
    public void HealFromAPaladinOutOfRange_IsCastAsFlashOfLight()
    {
        var h = new ClientHarness(Class.Paladin);

        var (prefix, body) = h.Announce(HealAnnouncement);

        var start = Assert.Single(h.SpellStarts);
        Assert.Equal((int)FlashOfLightRank1, start.Cast.SpellID);
        Assert.Equal(CastTimeMs, start.Cast.CastTime);
        Assert.Equal(900u, start.Cast.Predict.Points);
        Assert.Equal(HealCommBridge.LhcPrefix, prefix);
        Assert.StartsWith($"D:1.5:{FlashOfLightRank1}:900:", body);
    }

    // The reported case: a healer whose class is not on record. It used to be cast as Greater
    // Heal. It is now cast as the stand-in for an unknown heal, by default the priest spell named
    // plain "Heal", with the announced cast time and amount.
    [Fact]
    public void HealFromAHealerOfUnknownClass_IsCastAsTheUnknownStandIn()
    {
        var h = new ClientHarness(Class.None);

        var (_, body) = h.Announce(HealAnnouncement);

        var start = Assert.Single(h.SpellStarts);
        Assert.Equal((int)PriestHeal, start.Cast.SpellID);
        Assert.Equal((uint)CastFlag.HealPrediction, start.Cast.CastFlags);
        Assert.StartsWith($"D:1.5:{PriestHeal}:900:", body);
    }

    [Fact]
    public void HealThatFitsSeveralSpells_IsCastAsTheUnknownStandIn()
    {
        var h = new ClientHarness(Class.Priest);

        h.Announce("Heal/Target/1400/2500/");

        Assert.Equal((int)PriestHeal, Assert.Single(h.SpellStarts).Cast.SpellID);
    }

    // ---- The spell a proxied healer's side names ------------------------------------------------

    // By default our own announcement goes out in the plain 1.12 form: every 1.12 player in the
    // group receives it, and only three of their addons' parsers have been read.
    [Fact]
    public void OutboundDirectHeal_GoesOutInThePlainFormByDefault()
    {
        var h = new ClientHarness();
        string prefix = HealCommBridge.LhcPrefix;
        string body = $"D:1.500:{FlashHealRank7}:1100:1-0000004D";

        Assert.False(global::Framework.Settings.HealCommAnnounceSpellId);
        Assert.True(h.Bridge.TryTranslateOutboundAddon(ref prefix, ref body));

        Assert.Equal(HealCommBridge.HcPrefix, prefix);
        Assert.Equal("Heal/Target/1100/1500/", body);
    }

    // Opted in (Settings.HealCommAnnounceSpellId), the spell our own client's LibHealComm-4.0
    // message names rides as a fifth field. Only a direct heal is named.
    [Theory]
    [InlineData(FlashHealRank7, true, "Heal/Target/1100/1500/10917/")]
    [InlineData(FlashHealRank7, false, "Heal/Target/1100/1500/")]
    [InlineData(Fireball, true, "Heal/Target/1100/1500/")]
    public void FormatDirectHeal_CarriesTheSpellAsAFifthFieldOnlyWhenOptedIn(uint spellId, bool withSpell, string expected)
    {
        Assert.Equal(expected, HealCommBridge.FormatDirectHeal("Target", 1100, 1500, spellId, withSpell));
    }

    // The receiving side: the healer's own proxy saw the cast, so this is the spell, rank
    // included, whatever the healer's class is on record as.
    [Fact]
    public void HealAnnouncedWithItsSpell_IsCastAsThatSpell()
    {
        var h = new ClientHarness(Class.None);

        var (_, body) = h.Announce($"Heal/Target/900/1500/{FlashHealRank7}/");

        Assert.Equal((int)FlashHealRank7, Assert.Single(h.SpellStarts).Cast.SpellID);
        Assert.StartsWith($"D:1.5:{FlashHealRank7}:900:", body);
    }

    // A fifth field that is not a direct heal is not believed: the message could come from anyone.
    [Theory]
    [InlineData("133")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("Fireball")]
    public void HealAnnouncedWithSomethingElseAsItsSpell_IsIdentifiedAsUsual(string fifthField)
    {
        var h = new ClientHarness(Class.Paladin);

        h.Announce($"Heal/Target/900/1500/{fifthField}/");

        Assert.Equal((int)FlashOfLightRank1, Assert.Single(h.SpellStarts).Cast.SpellID);
    }

    // ---- Stops and pushback ----------------------------------------------------------------------

    // pfUI spells its stop "HealStop"; it used to be ignored and the heal stayed until its timer.
    [Fact]
    public void PfuiHealStop_StopsTheHeal()
    {
        var h = new ClientHarness(Class.Paladin);
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Announce("HealStop");

        Assert.Single(h.Sent.OfType<SpellFailedOther>());
        Assert.Equal(new[] { $"S::{FlashOfLightRank1}:1:" }, h.Lhc40Stops);
    }

    // A pushed-back heal used to lose its prediction at its original end; it now keeps it until
    // the end the pushback moved it to.
    [Fact]
    public async Task PushedBackHeal_KeepsItsPredictionUntilItsNewEnd()
    {
        var h = new ClientHarness(Class.Paladin);
        h.Announce("Heal/Target/900/100/"); // a cast time no paladin heal has: the unknown stand-in
        h.Announce("Healdelay/700/"); // now ends 800 ms after the start, not 100
        h.ClearSent();

        await Task.Delay(550, TestContext.Current.CancellationToken); // past the original end and its 250 ms tolerance
        Assert.Empty(h.Lhc40Stops);

        await Task.Delay(900, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { $"S::{PriestHeal}:0:" }, h.Lhc40Stops);
    }

    // ---- What JimsPlus is told -------------------------------------------------------------------

    [Fact]
    public void SynthesizedHeal_TellsJimsPlusTheTargetAndThatTheSpellIsKnown()
    {
        var h = new ClientHarness(Class.Paladin);
        h.EnableSideband();

        h.Announce(HealAnnouncement);

        Assert.Equal(new[] { $"JP_HC:S:{h.Healer.ToClientGuidString()}:{FlashOfLightRank1}:1500:1:Target" }, h.Sideband);
        // after the cast start, like the cast-time sideband
        var sent = h.Sent;
        Assert.True(sent.FindIndex(p => p is SpellStart) < sent.FindIndex(p => p is ChatPkt c && c.ChatText.StartsWith("JP_HC:")));
    }

    [Fact]
    public void SynthesizedUnknownHeal_TellsJimsPlusTheSpellIsNotKnown()
    {
        var h = new ClientHarness(Class.None);
        h.EnableSideband();

        h.Announce(HealAnnouncement);

        // certain = 0: JimsPlus labels the bar "Unknown Heal" whatever spell stands in on the wire
        Assert.Equal(new[] { $"JP_HC:S:{h.Healer.ToClientGuidString()}:{PriestHeal}:1500:0:Target" }, h.Sideband);
    }

    [Fact]
    public void DelayAndStopOfASynthesizedHeal_ReachJimsPlus()
    {
        var h = new ClientHarness(Class.Paladin);
        h.EnableSideband();
        h.Announce(HealAnnouncement);
        h.ClearSent();
        string healer = h.Healer.ToClientGuidString();

        h.Announce("Healdelay/400/");
        h.Announce("Healstop");

        Assert.Equal(new[] { $"JP_HC:D:{healer}:400", $"JP_HC:X:{healer}:1" }, h.Sideband);
    }

    // The server's own cast start for the healer takes over the bar: JimsPlus is told the
    // announced cast was replaced, not interrupted.
    [Fact]
    public void RealCastStartAfterASynthesizedHeal_TellsJimsPlusItWasReplaced()
    {
        var h = new ClientHarness(Class.Paladin);
        h.EnableSideband();
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));

        Assert.Contains($"JP_HC:X:{h.Healer.ToClientGuidString()}:2", h.Sideband);
    }

    // Without the addon's handshake the lines would show as chat.
    [Fact]
    public void WithoutJimsPlus_NoSidebandIsSent()
    {
        var h = new ClientHarness(Class.Paladin);

        h.Announce(HealAnnouncement);
        h.Announce("Healdelay/400/");
        h.Announce("Healstop");

        Assert.Empty(h.Sideband);
    }

    // A heal the client already shows (a healer in range) is the server's cast: nothing about it
    // goes over the sideband.
    [Fact]
    public void AdoptedHeal_SendsNoSideband()
    {
        var h = new ClientHarness(Class.Paladin);
        h.EnableSideband();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);
        h.Announce("Healstop");

        Assert.Empty(h.Sideband);
    }

    // ---- The JimsPlus switch for the client's own number (off by default) ------------------------

    // A JimsPlus "JP" addon message, as WorldSocket.HandleAddonMessage hands it on.
    private static void JimsPlusMessage(WorldSocket socket, string body) =>
        typeof(WorldSocket).GetMethod("HandleJimsPlusSideband", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(socket, new object[] { body });

    // Nobody turned it on in JimsPlus and the config key is off (both defaults): a heal from a
    // healer in range gets no repeated cast start. Only LibHealComm frames show it.
    [Fact]
    public void AdoptedHeal_SendsNoRepeat_WhenNobodyTurnedItOn()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = false } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);

        Assert.Empty(h.SpellStarts);
    }

    // The player ticks the JimsPlus checkbox: its H message reaches the bridge, and the next heal
    // from a healer in range carries the amount. Unticked, the next heal does not.
    [Fact]
    public void JimsPlusSwitch_TurnsTheAmountOnAndOff()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = false } };
        var socket = WorldSocket.CreateForTest(h.Session);

        JimsPlusMessage(socket, "H\tAMOUNT\t1");
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();
        h.Announce(HealAnnouncement);
        Assert.Equal(900u, Assert.Single(h.SpellStarts).Cast.Predict.Points);

        h.Dispatch(SpellGoPacket(LegacyHealer, FlashHealRank7));
        JimsPlusMessage(socket, "H\tAMOUNT\t0");
        h.NowMs += 2000;
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();
        h.Announce(HealAnnouncement);
        Assert.Empty(h.SpellStarts);
    }

    // The config key turns it on for every player on the proxy, whatever the checkbox says.
    [Fact]
    public void ConfigKey_TurnsItOnWhateverJimsPlusSays()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        JimsPlusMessage(WorldSocket.CreateForTest(h.Session), "H\tAMOUNT\t0");
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);

        Assert.Single(h.SpellStarts);
    }

    // Anything but AMOUNT 0 or 1 changes nothing.
    [Theory]
    [InlineData("H\tAMOUNT\tyes")]
    [InlineData("H\tAMOUNT")]
    [InlineData("H\tLOUDER\t1")]
    public void JimsPlusSwitch_IgnoresAnythingElse(string body)
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = false } };
        JimsPlusMessage(WorldSocket.CreateForTest(h.Session), body);
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);

        Assert.Empty(h.SpellStarts);
    }

    // In a battleground HealComm-1.0 posts the same announcement to raid and to battleground chat.
    // The cast start is repeated for the first copy only: a second repeat would add one more
    // "begins to cast" line and nothing else.
    [Fact]
    public void SameAnnouncementTwice_RepeatsTheCastStartOnce()
    {
        var h = new ClientHarness { Bridge = { NativeAmountInRangeForTest = true } };
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHealRank7, CastTimeMs));
        h.ClearSent();

        h.Announce(HealAnnouncement);
        h.Announce(HealAnnouncement);

        Assert.Single(h.SpellStarts);
    }
}
