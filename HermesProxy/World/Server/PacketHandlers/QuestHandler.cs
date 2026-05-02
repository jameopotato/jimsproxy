using Framework.Constants;
using Framework.Logging;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // JimsProxy: c2s coalescing for the quest-giver CMSG family. The modern (1.14)
    // retail client can spam the entire HELLO → COMPLETE_QUEST → REQUEST_REWARD →
    // CHOOSE_REWARD chain at ~100ms intervals when it doesn't recognize the legacy
    // turn-in confirmation as final (see WorldClient.HandleQuestGiverQuestComplete
    // for the LaunchQuest fix). Twinstar/Kronos drops the socket as anti-flood after
    // ~15 retries in <2s — bundle 2026-05-02 073927 captured this. Even with the
    // LaunchQuest fix in place, defense-in-depth: never relay more than one of the
    // same (opcode, NPC, quest) tuple per cooldown window. Cooldown is 750ms — wider
    // than any plausible double-click but short enough that a legitimate "user closed
    // dialog and reopened" still goes through.
    private readonly Dictionary<(Opcode op, ulong npcGuidLow, uint questId), long> _questCmsgLastSent = new();
    private const int QuestCmsgCoalesceWindowMs = 750;

    private bool TryCoalesceQuestCmsg(Opcode op, WowGuid128 npcGuid, uint questId)
    {
        var key = (op, npcGuid.Low, questId);
        long now = Environment.TickCount64;
        if (_questCmsgLastSent.TryGetValue(key, out long last) && now - last < QuestCmsgCoalesceWindowMs)
        {
            Log.Event("quest.cmsg.coalesced", new
            {
                opcode = op.ToString(),
                npc_guid_low = key.Item2,
                quest_id = questId,
                ms_since_last = now - last,
                window_ms = QuestCmsgCoalesceWindowMs,
            });
            return true;
        }
        _questCmsgLastSent[key] = now;
        return false;
    }

    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_QUERY_QUEST)]
    void HandleQuestGiverQueryQuest(QuestGiverQueryQuest quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_QUERY_QUEST);
        packet.WriteGuid(quest.QuestGiverGUID.To64());
        packet.WriteUInt32(quest.QuestID);
        if (LegacyVersion.AddedInVersion(HermesProxy.Enums.ClientVersionBuild.V2_0_1_6180))
            packet.WriteBool(quest.RespondToGiver);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_ACCEPT_QUEST)]
    void HandleQuestGiverAcceptQuest(QuestGiverAcceptQuest quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_ACCEPT_QUEST);
        packet.WriteGuid(quest.QuestGiverGUID.To64());
        packet.WriteUInt32(quest.QuestID);
        if (LegacyVersion.AddedInVersion(HermesProxy.Enums.ClientVersionBuild.V3_1_2_9901))
            packet.WriteInt32(quest.StartCheat ? 1 : 0);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_LOG_REMOVE_QUEST)]
    void HandleQuestLogRemoveQuest(QuestLogRemoveQuest quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_LOG_REMOVE_QUEST);
        packet.WriteUInt8(quest.Slot);
        SendPacketToServer(packet);

        //MIRASU - clear running-total entries for the quest in this slot so a future re-accept of
        //MIRASU   the same quest doesn't inherit stale progress (toast was reading 7/10 on first
        //MIRASU   pickup of a freshly-accepted quest because the prior abandon left running totals
        //MIRASU   in QuestItemObjectiveProgress). Slot index matches the legacy quest log slot.
        var worldClient = GetSession().WorldClient;
        if (worldClient == null)
            return;
        var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
        if (updateFields == null)
            return;
        var logEntry = worldClient.ReadQuestLogEntry(quest.Slot, null, updateFields);
        if (logEntry?.QuestID == null)
            return;

        uint questId = (uint)logEntry.QuestID;
        //MIRASU - ConcurrentDictionary.TryRemove for cross-thread safety (abandon runs on server thread,
        //MIRASU   item-credit handlers run on client thread).
        var progressMap = GetSession().GameState.QuestItemObjectiveProgress;
        foreach (var k in progressMap.Keys.Where(k => k.QuestID == questId).ToList())
            progressMap.TryRemove(k, out _);
        //MIRASU - also clear from the saved snapshot so a logout/login can't restore stale entries
        //MIRASU   for an abandoned quest (otherwise a re-accept would inherit the pre-abandon total).
        if (GetSession().SavedQuestItemProgressByCharacter.TryGetValue(GetSession().GameState.CurrentPlayerGuid, out var savedForPlayer))
            foreach (var k in savedForPlayer.Keys.Where(k => k.QuestID == questId).ToList())
                savedForPlayer.TryRemove(k, out _);
        //MIRASU - persist the post-abandon saved snapshot to disk so a crash before the next
        //MIRASU   graceful logout can't restore a stale entry for the just-abandoned quest on
        //MIRASU   the next proxy start (otherwise a re-accept would inherit the pre-abandon total).
        GetSession().PersistQuestItemProgressForCurrentPlayer();
        //MIRASU - drop any buffered credits whose itemId belongs to this abandoned quest's objectives.
        //MIRASU   Without this, a buffered credit from before the abandon could be replayed against a
        //MIRASU   re-accept (or a different active quest sharing the item) when its template arrives.
        worldClient.DropPendingQuestItemCreditsForQuest(questId);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_STATUS_QUERY)]
    void HandleQuestGiverStatusQuery(QuestGiverStatusQuery query)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_STATUS_QUERY);
        packet.WriteGuid(query.QuestGiverGUID.To64());
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_STATUS_MULTIPLE_QUERY)]
    void HandleQuestGiverStatusMultipleQuery(QuestGiverStatusMultipleQuery query)
    {
        if (LegacyVersion.AddedInVersion(HermesProxy.Enums.ClientVersionBuild.V2_0_1_6180))
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_STATUS_MULTIPLE_QUERY);
            SendPacketToServer(packet);
        }
        else
        {
            int UNIT_NPC_FLAGS = ModernVersion.GetUpdateField(UnitField.UNIT_NPC_FLAGS);
            if (UNIT_NPC_FLAGS < 0)
                return;

            List<WowGuid128> npcGuids = new List<WowGuid128>();
            lock (GetSession().GameState.ObjectCacheLock)
            {
                foreach (var obj in GetSession().GameState.ObjectCacheModern)
                {
                    if (obj.Key.GetObjectType() == ObjectType.Unit &&
                        obj.Value.GetUpdateField<uint>(UNIT_NPC_FLAGS).HasAnyFlag(NPCFlags.QuestGiver))
                        npcGuids.Add(obj.Key);
                }
            }

            foreach (var guid in npcGuids)
            {
                WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_STATUS_QUERY);
                packet.WriteGuid(guid.To64());
                SendPacketToServer(packet);
            }
        }
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_HELLO)]
    void HandleQuestGiverHello(QuestGiverHello hello)
    {
        if (TryCoalesceQuestCmsg(Opcode.CMSG_QUEST_GIVER_HELLO, hello.QuestGiverGUID, 0))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_HELLO);
        packet.WriteGuid(hello.QuestGiverGUID.To64());
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_REQUEST_REWARD)]
    void HandleQuestGiverRequestReward(QuestGiverRequestReward quest)
    {
        if (TryCoalesceQuestCmsg(Opcode.CMSG_QUEST_GIVER_REQUEST_REWARD, quest.QuestGiverGUID, quest.QuestID))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_REQUEST_REWARD);
        packet.WriteGuid(quest.QuestGiverGUID.To64());
        packet.WriteUInt32(quest.QuestID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_CHOOSE_REWARD)]
    void HandleQuestGiverChooseReward(QuestGiverChooseReward quest)
    {
        if (TryCoalesceQuestCmsg(Opcode.CMSG_QUEST_GIVER_CHOOSE_REWARD, quest.QuestGiverGUID, quest.QuestID))
            return;
        int choiceIndex = 0;

        if (quest.Choice.Item.ItemID != 0)
        {
            // JimsProxy: prefer the offer-reward cache (populated when the server sent
            // SMSG_QUEST_GIVER_OFFER_REWARD_MESSAGE with the choice item list). This
            // avoids depending on a full QuestTemplate existing via CMSG_QUERY_QUEST_INFO,
            // which the 1.14 client doesn't always issue before clicking a reward.
            // Fallback to the QuestTemplate path for backwards compatibility.
            uint[]? offeredItems = GameData.GetOfferedRewardChoiceItems(quest.QuestID);
            if (offeredItems != null)
            {
                for (int i = 0; i < offeredItems.Length; i++)
                {
                    if (offeredItems[i] == quest.Choice.Item.ItemID)
                    {
                        choiceIndex = i;
                        break;
                    }
                }
            }
            else
            {
                QuestTemplate? questTemplate = GameData.GetQuestTemplate(quest.QuestID);
                if (questTemplate == null)
                {
                    Log.Print(LogType.Error, "Unable to select quest reward because quest template is missing. Try again.");
                    WorldPacket packet2 = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
                    packet2.WriteUInt32(quest.QuestID);
                    SendPacketToServer(packet2);
                    QuestGiverQuestFailed fail = new QuestGiverQuestFailed();
                    fail.QuestID = quest.QuestID;
                    fail.Reason = InventoryResult.ItemNotFound;
                    SendPacket(fail);
                    return;
                }

                for (int i = 0; i < questTemplate.UnfilteredChoiceItems.Length; i++)
                {
                    if (questTemplate.UnfilteredChoiceItems[i].ItemID == quest.Choice.Item.ItemID)
                    {
                        choiceIndex = i;
                        break;
                    }
                }
            }
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_CHOOSE_REWARD);
        packet.WriteGuid(quest.QuestGiverGUID.To64());
        packet.WriteUInt32(quest.QuestID);
        packet.WriteInt32(choiceIndex);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_GIVER_COMPLETE_QUEST)]
    void HandleQuestGiverCompleteQuest(QuestGiverCompleteQuest quest)
    {
        if (TryCoalesceQuestCmsg(Opcode.CMSG_QUEST_GIVER_COMPLETE_QUEST, quest.QuestGiverGUID, quest.QuestID))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_GIVER_COMPLETE_QUEST);
        packet.WriteGuid(quest.QuestGiverGUID.To64());
        packet.WriteUInt32(quest.QuestID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_CONFIRM_ACCEPT)]
    void HandleQuestConfirmAcceptResponse(QuestConfirmAcceptResponse quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_QUEST_CONFIRM_ACCEPT);
        packet.WriteUInt32(quest.QuestID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_PUSH_QUEST_TO_PARTY)]
    void HandlePushQuestToParty(PushQuestToParty quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PUSH_QUEST_TO_PARTY);
        packet.WriteUInt32(quest.QuestID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_QUEST_PUSH_RESULT)]
    void HandleQuestPushResult(QuestPushResultResponse quest)
    {
        WorldPacket packet = new WorldPacket(Opcode.MSG_QUEST_PUSH_RESULT);
        packet.WriteGuid(quest.SenderGUID.To64());
        packet.WriteUInt8((byte)quest.Result);
        SendPacketToServer(packet);
    }
}
