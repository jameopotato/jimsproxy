using System;
using System.Collections.Generic;
using System.Linq;
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

// JimsProxy (a heal's cast bar turning into Greater Heal): a grouped shaman cast Lesser Healing
// Wave; the observer's cast bar showed it for a moment, then changed to Greater Heal. The server's
// own SMSG_SPELL_START reaches the observer first. The shaman's addon announces the heal a round
// trip later (HealComm-1.0 "Heal/<target>/<amount>/<castMs>/", which carries no spell), and the
// bridge synthesized a second SMSG_SPELL_START for it under a spell guessed from the class:
// Greater Heal for a priest or an unknown class. Now an announcement for a cast the client is
// already showing synthesizes nothing; a healer out of range (no real start) still gets the
// synthesized cast.
// The real start goes through the real SMSG_SPELL_START handler; the announcement through the
// bridge's inbound entry point, as the chat handler calls it.
public class HealCommRealCastAdoptionTests
{
    private const uint LesserHealingWave = 8004;
    private const uint FlashHeal = 2061;
    private const uint GreaterHealRank1 = 2060;
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

        public ClientHarness()
        {
            Session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            Session.GameState = State;
            Session.WorldClient = Client;
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
            // The healer is known by name only (a party list entry carries no class), the case
            // that guesses Greater Heal whatever the healer's class.
            State.UpdatePlayerCache(Healer, new PlayerCache { Name = "Healer" });
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

        public void ClearSent()
        {
            lock (_sent) _sent.Clear();
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

    private const string HealAnnouncement = "Heal/Target/900/1500/";

    // The report: the real start, then the announcement. One cast start reaches the client, the
    // real one, and the prediction for LibHealComm-4.0 names the spell actually being cast.
    [Fact]
    public void HealAnnouncedAfterRealCastStart_SendsNoSecondSpellStart()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, LesserHealingWave, CastTimeMs));
        h.NowMs += 180; // the healer's round trip

        var (prefix, body) = h.Announce(HealAnnouncement);

        var starts = h.SpellStarts;
        Assert.Single(starts);
        Assert.Equal((int)LesserHealingWave, starts[0].Cast.SpellID);
        Assert.Equal(HealCommBridge.LhcPrefix, prefix);
        Assert.StartsWith($"D:1.5:{LesserHealingWave}:900:", body);
    }

    [Fact]
    public void GroupHealAnnouncedAfterRealCastStart_SendsNoSecondSpellStart()
    {
        const uint prayerOfHealingRank3 = 10960;
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, prayerOfHealingRank3, 3000));
        h.NowMs += 180;

        var (prefix, body) = h.Announce("GrpHeal/700/3000/Target/");

        Assert.Single(h.SpellStarts);
        Assert.Equal(HealCommBridge.LhcPrefix, prefix);
        Assert.StartsWith($"D:3:{prayerOfHealingRank3}:700:", body);
    }

    // What the synthesis is for: a healer out of range, whose casts the server never sends.
    [Fact]
    public void HealAnnouncedWithNoRealCast_SynthesizesTheStart()
    {
        var h = new ClientHarness();

        var (prefix, body) = h.Announce(HealAnnouncement);

        var start = Assert.Single(h.SpellStarts);
        Assert.Equal(h.Healer, start.Cast.CasterUnit);
        Assert.Equal((int)GreaterHealRank1, start.Cast.SpellID);
        Assert.Equal(CastTimeMs, start.Cast.CastTime);
        Assert.Equal((uint)CastFlag.HealPrediction, start.Cast.CastFlags);
        Assert.Equal(900u, start.Cast.Predict.Points);
        Assert.Equal(HealCommBridge.LhcPrefix, prefix);
        Assert.StartsWith($"D:1.5:{GreaterHealRank1}:900:", body);
    }

    // The announcement trails the cast, so it can land just after a short cast completed; an
    // earlier cast the healer made while in range says nothing about a later one.
    [Theory]
    [InlineData(1500 + 1000, true)]
    [InlineData(1500 + 1001, false)]
    [InlineData(60_000, false)]
    public void HealAnnounced_AdoptsTheRealCastOnlyWhileItCanBeThatCast(int msAfterRealStart, bool adopted)
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHeal, CastTimeMs));
        h.ClearSent();
        h.NowMs += msAfterRealStart;

        h.Announce(HealAnnouncement);

        Assert.Equal(adopted ? 0 : 1, h.SpellStarts.Count);
    }

    // An adopted cast has no native cast of the bridge's to end or delay: the server's own
    // packets do that. LibHealComm-4.0 still gets the stop and the delay, under the real spell.
    [Fact]
    public void StopOfAnAdoptedCast_StopsThePredictionOnly()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, LesserHealingWave, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Announce("Healstop");

        Assert.Empty(h.Sent.OfType<SpellFailedOther>());
        Assert.Equal(new[] { $"S::{LesserHealingWave}:1:" }, h.Lhc40Stops);
    }

    [Fact]
    public void DelayOfAnAdoptedCast_DelaysThePredictionOnly()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, LesserHealingWave, CastTimeMs));
        h.Announce(HealAnnouncement);
        h.ClearSent();

        var (prefix, body) = h.Announce("Healdelay/500/");

        Assert.Empty(h.Sent.OfType<SpellDelayed>());
        Assert.Equal(HealCommBridge.LhcPrefix, prefix);
        Assert.StartsWith($"F::{LesserHealingWave}:", body);
    }

    // The synthesized cast keeps its native dismiss.
    [Fact]
    public void StopOfASynthesizedCast_StillDismissesIt()
    {
        var h = new ClientHarness();
        h.Announce(HealAnnouncement);
        h.ClearSent();

        h.Announce("Healstop");

        var failed = Assert.Single(h.Sent.OfType<SpellFailedOther>());
        Assert.Equal(GreaterHealRank1, failed.SpellID);
        Assert.Equal(new[] { $"S::{GreaterHealRank1}:1:" }, h.Lhc40Stops);
    }

    // The fallback end-of-cast timer of one adopted cast must not end the next one. (Adopted
    // casts have no CastId, so the timer cannot tell them apart by it.)
    [Fact]
    public async Task EndTimerOfAnAdoptedCast_DoesNotEndTheNextAdoptedCast()
    {
        var h = new ClientHarness();
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHeal, CastTimeMs));
        h.Announce("Heal/Target/900/1/"); // its timer fires about 250 ms from now
        h.Announce("Healstop");
        h.Dispatch(SpellStartPacket(LegacyHealer, FlashHeal, CastTimeMs));
        h.Announce("Heal/Target/900/60000/");
        h.ClearSent();

        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.Empty(h.Lhc40Stops);
    }

    // Only a player's cast-time cast can be announced as a heal; nothing else is remembered.
    [Fact]
    public void InstantAndCreatureCasts_AreNotRemembered()
    {
        var h = new ClientHarness();
        var creature = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 99);

        h.Bridge.OnRealSpellStartFromOther(h.Healer, LesserHealingWave, 0);
        h.Bridge.OnRealSpellStartFromOther(creature, LesserHealingWave, CastTimeMs);

        Assert.Equal(0, h.Bridge.RealCastCountForTest);
    }

    [Fact]
    public void RememberedCasts_AreSweptOnceExpired()
    {
        var h = new ClientHarness();
        for (uint i = 0; i < 200; i++)
            h.Bridge.OnRealSpellStartFromOther(new WowGuid64(HighGuidTypeLegacy.Player, 10_000 + i).To128(h.State), FlashHeal, CastTimeMs);
        Assert.Equal(200, h.Bridge.RealCastCountForTest); // all still casting: none dropped

        h.NowMs += 60_000;
        h.Bridge.OnRealSpellStartFromOther(h.Healer, FlashHeal, CastTimeMs);

        Assert.Equal(1, h.Bridge.RealCastCountForTest);
    }
}
