using Framework.Constants;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System.Threading;
using System.Threading.Tasks;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_QUERY_TIME)]
    void HandleQueryTime(EmptyClientPacket queryTime)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_TIME);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUERY_QUEST_INFO)]
    void HandleQueryQuestInfo(QueryQuestInfo queryQuest)
    {
        // JimsProxy: cache-first + dedupe + negative-cache.
        // Questie's filter-toggle reset (and any other addon that iterates the quest DB)
        // bursts thousands of CMSG_QUERY_QUEST_INFO in seconds — Kronos's anti-flood drops
        // the connection if these all reach the server. The modern client's quest-info
        // cache is keyed by quest id only, so a single SMSG response satisfies any number
        // of outstanding client-side requests for that id.
        uint questId = queryQuest.QuestID;
        var state = GetSession().GameState;

        QuestTemplate? cached = GameData.GetQuestTemplate(questId);
        if (cached != null)
        {
            QueryQuestInfoResponse cachedResponse = new QueryQuestInfoResponse();
            cachedResponse.QuestID = questId;
            cachedResponse.Allow = true;
            cachedResponse.Info = cached;
            SendPacket(cachedResponse);
            Framework.Logging.Log.Event("quest.query.cache_hit", new { quest_id = questId });
            return;
        }

        if (state.NegativeQuestInfoCache.ContainsKey(questId))
        {
            QueryQuestInfoResponse negResponse = new QueryQuestInfoResponse();
            negResponse.QuestID = questId;
            negResponse.Allow = false;
            SendPacket(negResponse);
            Framework.Logging.Log.Event("quest.query.negative_cache_hit", new { quest_id = questId });
            return;
        }

        if (!state.InFlightClientQuestInfoQueries.TryAdd(questId, 0))
        {
            Framework.Logging.Log.Event("quest.query.in_flight_drop", new { quest_id = questId });
            return;
        }

        // Token bucket: forward immediately if a token is available; otherwise
        // enqueue and let the (single, per-session) async drainer pump at rate.
        if (TryConsumeQuestQueryToken(state))
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
            packet.WriteUInt32(questId);
            SendPacketToServer(packet);
            return;
        }

        state.PendingQuestQueryQueue.Enqueue(questId);
        Framework.Logging.Log.Event("quest.query.bucket_enqueue", new
        {
            quest_id = questId,
            queue_depth = state.PendingQuestQueryQueue.Count,
        });
        if (Interlocked.CompareExchange(ref state.QuestQueryDrainerRunning, 1, 0) == 0)
            _ = Task.Run(DrainQuestQueryBucketAsync);
    }

    private const double QuestQueryTokensPerSec = 10.0;
    private const double QuestQueryBurstCapacity = 20.0;

    private static bool TryConsumeQuestQueryToken(GameSessionData state)
    {
        lock (state.QuestQueryBucketLock)
        {
            long now = System.Environment.TickCount64;
            if (state.QuestQueryLastRefillMs == 0) state.QuestQueryLastRefillMs = now;
            double elapsedSec = (now - state.QuestQueryLastRefillMs) / 1000.0;
            state.QuestQueryTokens = System.Math.Min(
                QuestQueryBurstCapacity,
                state.QuestQueryTokens + elapsedSec * QuestQueryTokensPerSec);
            state.QuestQueryLastRefillMs = now;
            if (state.QuestQueryTokens >= 1.0)
            {
                state.QuestQueryTokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    // Returns ms until the next whole token is available (0 if available now).
    private static int MsUntilNextQuestQueryToken(GameSessionData state)
    {
        lock (state.QuestQueryBucketLock)
        {
            long now = System.Environment.TickCount64;
            double elapsedSec = (now - state.QuestQueryLastRefillMs) / 1000.0;
            double tokensNow = System.Math.Min(
                QuestQueryBurstCapacity,
                state.QuestQueryTokens + elapsedSec * QuestQueryTokensPerSec);
            if (tokensNow >= 1.0) return 0;
            double needed = 1.0 - tokensNow;
            return (int)System.Math.Ceiling(needed * 1000.0 / QuestQueryTokensPerSec);
        }
    }

    private async Task DrainQuestQueryBucketAsync()
    {
        var state = GetSession().GameState;
        try
        {
            while (state.PendingQuestQueryQueue.TryPeek(out _))
            {
                int waitMs = MsUntilNextQuestQueryToken(state);
                if (waitMs > 0) await Task.Delay(waitMs).ConfigureAwait(false);
                if (!state.PendingQuestQueryQueue.TryDequeue(out uint questId))
                    break;
                // Cache may have been populated by a parallel proxy-issued query
                // while this id was waiting; skip a redundant outbound send if so.
                QuestTemplate? warmTemplate = GameData.GetQuestTemplate(questId);
                if (warmTemplate != null)
                {
                    state.InFlightClientQuestInfoQueries.TryRemove(questId, out _);
                    QueryQuestInfoResponse warm = new QueryQuestInfoResponse();
                    warm.QuestID = questId;
                    warm.Allow = true;
                    warm.Info = warmTemplate;
                    SendPacket(warm);
                    Framework.Logging.Log.Event("quest.query.bucket_drain_cache_hit", new { quest_id = questId });
                    continue;
                }
                if (!TryConsumeQuestQueryToken(state))
                {
                    // Lost a race; re-enqueue and loop (wait will recompute).
                    state.PendingQuestQueryQueue.Enqueue(questId);
                    continue;
                }
                WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
                packet.WriteUInt32(questId);
                SendPacketToServer(packet);
                Framework.Logging.Log.Event("quest.query.bucket_drain_send", new
                {
                    quest_id = questId,
                    queue_depth = state.PendingQuestQueryQueue.Count,
                });
            }
        }
        catch (System.Exception ex)
        {
            // Drainer runs on a fire-and-forget Task; if the session disconnects mid-drain,
            // SendPacketToServer throws and the exception would otherwise go unobserved.
            Framework.Logging.Log.Event("quest.query.bucket_drain_error", new { error = ex.ToString() });
        }
        finally
        {
            Interlocked.Exchange(ref state.QuestQueryDrainerRunning, 0);
            // Race: an enqueue could have happened between the empty-check and
            // releasing the flag. Re-check and restart if needed.
            if (!state.PendingQuestQueryQueue.IsEmpty &&
                Interlocked.CompareExchange(ref state.QuestQueryDrainerRunning, 1, 0) == 0)
            {
                _ = Task.Run(DrainQuestQueryBucketAsync);
            }
        }
    }
    [PacketHandler(Opcode.CMSG_QUERY_CREATURE)]
    void HandleQueryCreature(QueryCreature queryCreature)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_CREATURE);
        packet.WriteUInt32(queryCreature.CreatureID);
        packet.WriteGuid(new WowGuid64(HighGuidTypeLegacy.Creature, queryCreature.CreatureID, 1));
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUERY_GAME_OBJECT)]
    void HandleQueryGameObject(QueryGameObject queryGo)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_GAME_OBJECT);
        packet.WriteUInt32(queryGo.GameObjectID);
        packet.WriteGuid(queryGo.Guid.To64());
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUERY_PAGE_TEXT)]
    void HandleQueryPageText(QueryPageText queryText)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_PAGE_TEXT);
        packet.WriteUInt32(queryText.PageTextID);
        packet.WriteGuid(queryText.ItemGUID.To64());
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUERY_NPC_TEXT)]
    void HandleQueryNpcText(QueryNPCText queryText)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_NPC_TEXT);
        packet.WriteUInt32(queryText.TextID);
        packet.WriteGuid(queryText.Guid.To64());
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUERY_PET_NAME)]
    void HandleQueryPetName(QueryPetName queryName)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUERY_PET_NAME);
        packet.WriteUInt32(queryName.UnitGUID.GetEntry());
        packet.WriteGuid(queryName.UnitGUID.To64());
        // Modern pet guids embed the pet number as their entry — register the mapping now
        // so the response resolves even for pets we never saw a unit create for.
        GetSession().GameState.CachedPetNumbers[queryName.UnitGUID.GetEntry()] = queryName.UnitGUID;
        Framework.Logging.Log.Event("pet.name_query.sent", new
        {
            client_guid = queryName.UnitGUID.ToString(),
            entry = queryName.UnitGUID.GetEntry(),
            counter = queryName.UnitGUID.GetCounter(),
            high_type = queryName.UnitGUID.GetHighType().ToString(),
        });
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_WHO)]
    void HandleWhoRequest(WhoRequestPkt who)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_WHO);
        packet.WriteInt32(who.Request.MinLevel);
        packet.WriteInt32(who.Request.MaxLevel);
        packet.WriteCString(who.Request.Name);
        packet.WriteCString(who.Request.Guild);
        packet.WriteInt32((int)who.Request.RaceFilter);
        packet.WriteInt32(who.Request.ClassFilter);

        packet.WriteInt32(who.Areas.Count);
        foreach (int area in who.Areas)
            packet.WriteInt32(area);

        packet.WriteInt32(who.Request.Words.Count);
        foreach (string word in who.Request.Words)
            packet.WriteCString(word);

        SendPacketToServer(packet);
        GetSession().GameState.LastWhoRequestId = who.RequestID;
    }
}
