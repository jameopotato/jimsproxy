using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#193): an exploration quest's objective line showed complete but its title was not
// tagged "(Complete)" until turn-in. The quest reaches the 1.14 client with no objectives (the proxy
// builds none for it) and the 1.12 Exploration flag 0x4; the client shows its single objective line
// complete from the log slot's completion bit alone, but tags the title of a quest with no
// objectives only when Flags also carry 0x2 (completion by a server event). The quest query
// translation now adds 0x2 to an objective-less quest that has 0x4. Escort quests already carry
// 0x2 from the server; deliveries (no objectives, neither bit) and quests with objectives are left
// as the server sent them. Flag values below are the ones Kronos sends for the named quests
// (proxy captures, 2026).
public class QuestCompletionEventFlagTests
{
    private const uint StayAlive = (uint)QuestFlags.StayAlive;        // 0x1
    private const uint Event = (uint)QuestFlags.CompletionEvent;      // 0x2
    private const uint Exploration = (uint)QuestFlags.Exploration;    // 0x4
    private const uint Sharable = (uint)QuestFlags.Sharable;          // 0x8
    private const uint Raid = (uint)QuestFlags.Raid;                  // 0x40

    private sealed class ClientHarness
    {
        public readonly WorldClient Client = new();
        private readonly List<ServerPacket> _sent = new();

        public ClientHarness()
        {
            var session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
            session.GameState = GameSessionData.CreateForTesting();
            session.WorldClient = Client;
            Client.AttachSessionForTest(session);
            Client.ClientSendSpyForTest = packet => { lock (_sent) _sent.Add(packet); };
        }

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

    // 1.12 SMSG_QUEST_QUERY_RESPONSE in the field order the handler reads it. With every requirement
    // left at 0 the quest has no objectives. creatureId/creatureCount add one kill objective, a
    // negative rewOrReqMoney a money objective.
    private static WorldPacket LegacyQuestQuery(uint questId, uint flags, int creatureId = 0, int creatureCount = 0, int rewOrReqMoney = 0)
        => Legacy(Opcode.SMSG_QUERY_QUEST_INFO_RESPONSE, w =>
        {
            w.WriteUInt32(questId);
            w.WriteInt32(2);                  // QuestType: normal
            w.WriteInt32(16);                 // QuestLevel
            w.WriteInt32(148);                // QuestSortID: Darkshore
            w.WriteUInt32(0);                 // QuestInfoID
            for (int i = 0; i < 2; i++)
            {
                w.WriteInt32(0);              // required reputation faction
                w.WriteInt32(0);              // required reputation value
            }
            w.WriteUInt32(0);                 // RewardNextQuest
            w.WriteInt32(rewOrReqMoney);      // reward money, or required money when negative
            w.WriteUInt32(0);                 // money at max level
            w.WriteUInt32(0);                 // RewardDisplaySpell
            w.WriteUInt32(0);                 // StartItem
            w.WriteUInt32(flags);             // Flags
            for (int i = 0; i < 4; i++)
            {
                w.WriteUInt32(0);             // reward item
                w.WriteUInt32(0);             // reward item count
            }
            for (int i = 0; i < 6; i++)
            {
                w.WriteUInt32(0);             // choice item
                w.WriteUInt32(0);             // choice item count
            }
            w.WriteUInt32(1);                 // POI continent
            w.WriteFloat(0f);                 // POI x
            w.WriteFloat(0f);                 // POI y
            w.WriteUInt32(0);                 // POI priority
            w.WriteCString("How Big a Threat?");
            w.WriteCString("Find a corrupt furbolg camp in Darkshore and return to Terenthis in Auberdine.");
            w.WriteCString("Details.");
            w.WriteCString("End text.");
            for (int i = 0; i < 4; i++)
            {
                w.WriteUInt32(i == 0 ? (uint)creatureId : 0u);   // ReqCreatureOrGOId
                w.WriteInt32(i == 0 ? creatureCount : 0);        // ReqCreatureOrGOCount
                w.WriteInt32(0);                                 // ReqItemId
                w.WriteInt32(0);                                 // ReqItemCount
            }
            for (int i = 0; i < 4; i++)
                w.WriteCString("");                              // ObjectiveText
        });

    private static QueryQuestInfoResponse Translate(WorldPacket legacy)
    {
        var h = new ClientHarness();
        h.Dispatch(legacy);
        var response = Assert.IsType<QueryQuestInfoResponse>(Assert.Single(h.Sent));
        Assert.True(response.Allow);
        return response;
    }

    [Fact]
    public void CompletionEvent_IsTheModernBit0x2()
    {
        Assert.Equal(0x2u, (uint)QuestFlags.CompletionEvent);
    }

    [Theory]
    [InlineData(9_840_001u, Sharable | Exploration)]               // 984 How Big a Threat? (0xC)
    [InlineData(9_840_002u, Exploration)]                          // 578 The Stone of the Tides (0x4)
    [InlineData(9_840_003u, StayAlive | Exploration)]              // 1699 The Rethban Gauntlet (0x5)
    public void ExplorationQuestWithoutObjectives_GetsTheCompletionEventFlag(uint questId, uint legacyFlags)
    {
        var response = Translate(LegacyQuestQuery(questId, legacyFlags));

        Assert.Empty(response.Info.Objectives);
        Assert.Equal(legacyFlags | Event, response.Info.Flags);
    }

    [Theory]
    [InlineData(9_840_011u, Event)]                                // 155 The Defias Brotherhood, escort (0x2)
    [InlineData(9_840_012u, Event | Sharable)]                     // 28 Trial of the Lake (0xA)
    public void EscortQuestWithoutObjectives_KeepsTheServerFlags(uint questId, uint legacyFlags)
    {
        var response = Translate(LegacyQuestQuery(questId, legacyFlags));

        Assert.Equal(legacyFlags, response.Info.Flags);
    }

    [Theory]
    [InlineData(9_840_021u, Sharable)]                             // 35 Further Concerns, speak with (0x8)
    [InlineData(9_840_022u, 0u)]                                   // 594 Message in a Bottle (0x0)
    [InlineData(9_840_023u, Raid | Sharable)]                      // 6402 Stormwind Rendezvous (0x48)
    public void DeliveryQuestWithoutObjectives_KeepsTheServerFlags(uint questId, uint legacyFlags)
    {
        var response = Translate(LegacyQuestQuery(questId, legacyFlags));

        Assert.Empty(response.Info.Objectives);
        Assert.Equal(legacyFlags, response.Info.Flags);
    }

    [Fact]
    public void QuestWithAKillObjective_KeepsTheServerFlags()
    {
        var response = Translate(LegacyQuestQuery(9_840_031u, Sharable | Exploration, creatureId: 2163, creatureCount: 8));

        Assert.Single(response.Info.Objectives);
        Assert.Equal(Sharable | Exploration, response.Info.Flags);
    }

    [Fact]
    public void QuestWithAMoneyObjective_KeepsTheServerFlags()
    {
        var response = Translate(LegacyQuestQuery(9_840_032u, Sharable | Exploration, rewOrReqMoney: -500));

        Assert.Single(response.Info.Objectives);
        Assert.Equal(Sharable | Exploration, response.Info.Flags);
    }

    // The proxy answers later CMSG_QUERY_QUEST_INFO for the same quest from its template cache, so
    // the cached template must carry the flag too.
    [Fact]
    public void CachedTemplate_CarriesTheCompletionEventFlag()
    {
        Translate(LegacyQuestQuery(9_840_041u, Sharable | Exploration));

        var cached = GameData.GetQuestTemplate(9_840_041u);
        Assert.NotNull(cached);
        Assert.Equal(Sharable | Exploration | Event, cached!.Flags);
    }
}
