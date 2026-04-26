using Framework;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Linq;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_QUEST_GIVER_QUEST_DETAILS)]
    void HandleQuestGiverQuestDetails(WorldPacket packet)
    {
        QuestGiverQuestDetails quest = new();
        quest.QuestGiverGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = quest.QuestGiverGUID;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            quest.InformUnit = packet.ReadGuid().To128(GetSession().GameState);
        else
            quest.InformUnit = quest.QuestGiverGUID;

        quest.QuestID = packet.ReadUInt32();
        quest.QuestTitle = packet.ReadCString();
        quest.DescriptionText = packet.ReadCString();
        quest.LogDescription = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.AutoLaunched = packet.ReadBool();
        else
            quest.AutoLaunched = packet.ReadUInt32() != 0;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            quest.QuestFlags[0] = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            quest.SuggestedPartyMembers = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // Unknown

        if (LegacyVersion.InVersion(ClientVersionBuild.V3_1_0_9767, ClientVersionBuild.V3_3_3a_11723))
        {
            quest.StartCheat = packet.ReadBool();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_2_11403))
                quest.DisplayPopup = packet.ReadBool();
        }

        if (quest.QuestFlags[0].HasAnyFlag(QuestFlags.HiddenRewards) && LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_3_5a_12340))
        {
            packet.ReadUInt32(); // Hidden Chosen Items
            packet.ReadUInt32(); // Hidden Items
            quest.Rewards.Money = packet.ReadUInt32(); // Hidden Money

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_2_10482))
                quest.Rewards.XP = packet.ReadUInt32(); // Hidden XP
        }

        ReadExtraQuestInfo(packet, quest.Rewards, false);

        var emoteCount = packet.ReadUInt32();
        for (var i = 0; i < emoteCount; i++)
        {
            quest.DescEmotes[i].Type = packet.ReadUInt32();
            quest.DescEmotes[i].Delay = packet.ReadUInt32();
        }
        SendPacketToClient(quest);
    }

    void ReadExtraQuestInfo(WorldPacket packet, QuestRewards rewards, bool readFlags)
    {
        rewards.ChoiceItemCount = packet.ReadUInt32();
        for (var i = 0; i < rewards.ChoiceItemCount; i++)
        {
            rewards.ChoiceItems[i].Item.ItemID = packet.ReadUInt32();
            rewards.ChoiceItems[i].Quantity = packet.ReadUInt32();
            packet.ReadUInt32(); // Choice Item Display Id
        }

        var rewardCount = packet.ReadUInt32();
        for (var i = 0; i < rewardCount; i++)
        {
            rewards.ItemID[i] = packet.ReadUInt32();
            rewards.ItemQty[i] = packet.ReadUInt32();
            packet.ReadUInt32(); // Reward Item Display Id
        }

        rewards.Money = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_2_10482))
           rewards.XP = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
            rewards.Honor = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            packet.ReadFloat(); // Honor Multiplier

        if (readFlags)
            packet.ReadUInt32(); // Quest Flags

        rewards.SpellCompletionID = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.ReadUInt32(); // Spell Cast Id

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_4_0_8089))
            rewards.Title = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            rewards.NumSkillUps = packet.ReadUInt32(); // Bonus Talents

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
        {
            packet.ReadUInt32(); // Arena Points
            packet.ReadUInt32(); // Unk
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
        {
            for (var i = 0; i < 5; i++)
                rewards.FactionID[i] = packet.ReadUInt32(); // Reputation Faction

            for (var i = 0; i < 5; i++)
                rewards.FactionValue[i] = packet.ReadInt32(); // Reputation Value Id

            for (var i = 0; i < 5; i++)
                packet.ReadInt32(); // Reputation Value
        }
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_STATUS)]
    void HandleQuestGiverStatus(WorldPacket packet)
    {
        QuestGiverStatusPkt response = new QuestGiverStatusPkt();
        response.QuestGiver.Guid = packet.ReadGuid().To128(GetSession().GameState);
        response.QuestGiver.Status = LegacyVersion.ConvertQuestGiverStatus(packet.ReadUInt8());
        SendPacketToClient(response);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_STATUS_MULTIPLE)]
    void HandleQuestGiverStatusMultple(WorldPacket packet)
    {
        QuestGiverStatusMultiple response = new QuestGiverStatusMultiple();
        int count = packet.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            QuestGiverInfo info = new();
            info.Guid = packet.ReadGuid().To128(GetSession().GameState);
            info.Status = LegacyVersion.ConvertQuestGiverStatus(packet.ReadUInt8());
            response.QuestGivers.Add(info);
        }
        SendPacketToClient(response);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_QUEST_LIST_MESSAGE)]
    void HandleQuestGiverQuestListMessage(WorldPacket packet)
    {
        QuestGiverQuestListMessage quests = new QuestGiverQuestListMessage();
        quests.QuestGiverGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = quests.QuestGiverGUID;
        quests.Greeting = packet.ReadCString();
        quests.GreetEmoteDelay = packet.ReadUInt32();
        quests.GreetEmoteType = packet.ReadUInt32();

        byte count = packet.ReadUInt8();
        for (int i = 0; i < count; i++)
        {
            ClientGossipQuest quest = ReadGossipQuestOption(packet);
            quests.QuestOptions.Add(quest);
        }
        SendPacketToClient(quests);
    }

    ClientGossipQuest ReadGossipQuestOption(WorldPacket packet)
    {
        ClientGossipQuest quest = new();
        quest.QuestID = packet.ReadUInt32();
        QuestGiverStatusModern dialogStatus = LegacyVersion.ConvertQuestGiverStatus((byte)packet.ReadInt32());

        if (dialogStatus.HasAnyFlag(QuestGiverStatusModern.Available | QuestGiverStatusModern.AvailableCovenantCalling | QuestGiverStatusModern.AvailableJourney | QuestGiverStatusModern.AvailableLegendaryQuest | QuestGiverStatusModern.AvailableRep | QuestGiverStatusModern.LowLevelAvailable | QuestGiverStatusModern.LowLevelAvailableRep))
            quest.QuestType = 2; // available
        else
            quest.QuestType = 4; // complete

        quest.QuestLevel = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            quest.QuestFlags = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            quest.Repeatable = packet.ReadBool();

        quest.QuestTitle = packet.ReadCString();
        return quest;
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_REQUEST_ITEMS)]
    void HandleQuestGiverRequestItems(WorldPacket packet)
    {
        QuestGiverRequestItems quest = new QuestGiverRequestItems();
        quest.QuestGiverGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = quest.QuestGiverGUID;
        quest.QuestGiverCreatureID = quest.QuestGiverGUID.GetEntry();
        quest.QuestID = packet.ReadUInt32();
        quest.QuestTitle = packet.ReadCString();
        quest.CompletionText = packet.ReadCString();
        quest.CompEmoteDelay = packet.ReadUInt32();
        quest.CompEmoteType = packet.ReadUInt32();
        quest.AutoLaunched = packet.ReadUInt32() != 0;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            quest.QuestFlags[0] = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            quest.SuggestPartyMembers = packet.ReadUInt32();

        quest.MoneyToGet = packet.ReadInt32();

        uint itemsCount = packet.ReadUInt32();
        for (int i = 0; i < itemsCount; i++)
        {
            QuestObjectiveCollect item = new();
            item.ObjectID = packet.ReadUInt32();
            item.Amount = packet.ReadUInt32();
            packet.ReadUInt32(); // Item Display Id
            quest.Collect.Add(item);
        }

        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.ReadUInt32(); // unknown meaning, mangos sends always 2

        // flags
        uint statusFlags = packet.ReadUInt32();
        if ((statusFlags & 3) != 0)
            quest.StatusFlags = 223;
        else
            quest.StatusFlags = 219;
        packet.ReadUInt32(); // Unk flags 2
        packet.ReadUInt32(); // Unk flags 3
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.ReadUInt32(); // Unk flags 4
        SendPacketToClient(quest);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_OFFER_REWARD_MESSAGE)]
    void HandleQuestGiverOfferRewardMessage(WorldPacket packet)
    {
        QuestGiverOfferRewardMessage quest = new QuestGiverOfferRewardMessage();
        quest.QuestData.QuestGiverGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = quest.QuestData.QuestGiverGUID;
        quest.QuestData.QuestGiverCreatureID = quest.QuestData.QuestGiverGUID.GetEntry();
        quest.QuestData.QuestID = packet.ReadUInt32();
        quest.QuestTitle = packet.ReadCString();
        quest.RewardText = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.QuestData.AutoLaunched = packet.ReadBool();
        else
            quest.QuestData.AutoLaunched = packet.ReadUInt32() != 0;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            quest.QuestData.QuestFlags[0] = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            quest.QuestData.SuggestedPartyMembers = packet.ReadUInt32();

        uint emotesCount = packet.ReadUInt32();
        for (int i = 0; i < emotesCount; i++)
        {
            QuestDescEmote emote = new();
            emote.Delay = packet.ReadUInt32();
            emote.Type = packet.ReadUInt32();
        }

        ReadExtraQuestInfo(packet, quest.QuestData.Rewards, true);

        // JimsProxy: cache choice item IDs by questId so CMSG_QUEST_GIVER_CHOOSE_REWARD
        // can translate the modern client's chosen itemId back to a legacy choice index.
        // The 1.14 client often doesn't issue CMSG_QUERY_QUEST_INFO before clicking a
        // reward (it already cached the quest on accept), so relying on QuestTemplates
        // alone fails the first-click turn-in with "quest template is missing". This
        // captures the choice list directly from the server's offer-reward payload.
        if (quest.QuestData.Rewards.ChoiceItemCount > 0)
        {
            uint[] choiceIds = new uint[quest.QuestData.Rewards.ChoiceItemCount];
            for (uint i = 0; i < quest.QuestData.Rewards.ChoiceItemCount; i++)
                choiceIds[i] = quest.QuestData.Rewards.ChoiceItems[i].Item.ItemID;
            GameData.StoreOfferedRewardChoiceItems(quest.QuestData.QuestID, choiceIds);
        }

        SendPacketToClient(quest);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_QUEST_COMPLETE)]
    void HandleQuestGiverQuestComplete(WorldPacket packet)
    {
        QuestGiverQuestComplete quest = new QuestGiverQuestComplete();
        quest.QuestID = packet.ReadUInt32();

        GetSession().GameState.CurrentPlayerStorage.CompletedQuests.MarkQuestAsCompleted(quest.QuestID);
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt32(); // mangos sends always 3

        quest.XPReward = packet.ReadUInt32();
        quest.MoneyReward = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
            packet.ReadInt32(); // Honor

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            packet.ReadInt32(); // Talents
            packet.ReadInt32(); // Arena Points
        }

        uint itemId = 0;
        uint itemCount = 0;
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            uint itemsCount = packet.ReadUInt32();
            for (uint i = 0; i < itemsCount; ++i)
            {
                uint itemId2 = packet.ReadUInt32();
                uint itemCount2 = packet.ReadUInt32();

                if (itemId2 != 0 && itemCount2 != 0)
                {
                    itemId = itemId2;
                    itemCount = itemCount2;
                }
            }
        }

        quest.ItemReward.ItemID = itemId;

        QuestTemplate? questTemplate = GameData.GetQuestTemplate((uint)quest.QuestID);
        if (questTemplate != null && questTemplate.RewardNextQuest == 0)
        {
            quest.LaunchQuest = false;

            if (GetSession().GameState.CurrentInteractedWithNPC != default)
            {
                uint npcFlags = GetSession().GameState.GetLegacyFieldValueUInt32(GetSession().GameState.CurrentInteractedWithNPC, UnitField.UNIT_NPC_FLAGS);
                if (npcFlags.HasAnyFlag(NPCFlags.Gossip))
                    quest.LaunchGossip = true;
            }
        }
        
        SendPacketToClient(quest);

        DisplayToast toast = new();
        toast.QuestID = quest.QuestID;
        if (itemId != 0 && itemCount != 0)
        {
            toast.Quantity = 1;
            toast.Type = 0;
            toast.ItemReward.ItemID = itemId;
        }
        else
        {
            toast.Quantity = 60;
            toast.Type = 2;
        }
        SendPacketToClient(toast);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_QUEST_FAILED)]
    void HandleQuestGiverQuestFailed(WorldPacket packet)
    {
        QuestGiverQuestFailed quest = new QuestGiverQuestFailed();
        quest.QuestID = packet.ReadUInt32();
        quest.Reason = LegacyVersion.ConvertInventoryResult(packet.ReadUInt32());
        SendPacketToClient(quest);
    }

    [PacketHandler(Opcode.SMSG_QUEST_GIVER_INVALID_QUEST)]
    void HandleQuestGiverInvalidQuest(WorldPacket packet)
    {
        QuestGiverInvalidQuest quest = new QuestGiverInvalidQuest();
        quest.Reason = (QuestFailedReasons)packet.ReadUInt32();
        SendPacketToClient(quest);
    }

    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_COMPLETE)]
    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_FAILED)]
    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_FAILED_TIMER)]
    void HandleQuestUpdateStatus(WorldPacket packet)
    {
        QuestUpdateStatus quest = new QuestUpdateStatus(packet.GetUniversalOpcode(false));
        quest.QuestID = packet.ReadUInt32();
        SendPacketToClient(quest);

        //MIRASU - clear our per-objective running totals for this quest so a future re-accept
        //MIRASU   (or a parallel quest sharing the same item) starts fresh instead of inheriting
        //MIRASU   stale counts. Applies to COMPLETE / FAILED / FAILED_TIMER -- in all three cases
        //MIRASU   the quest is leaving the active log and any running total is no longer valid.
        var progressMap = GetSession().GameState.QuestItemObjectiveProgress;
        foreach (var k in progressMap.Keys.Where(k => k.QuestID == quest.QuestID).ToList())
            progressMap.Remove(k);
    }
    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_ADD_ITEM)]
    void HandleQuestUpdateAddItem(WorldPacket packet)
    {
        uint itemId = packet.ReadUInt32();
        uint count = packet.ReadUInt32(); //MIRASU - delta, not total

        QuestObjective? objective = GameData.GetQuestObjectiveForItem(itemId);

        if (objective == null)
        {
            //MIRASU - quest template not cached yet; fire off queries and bail. Next pickup will find it.
            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
            int questsCount = LegacyVersion.GetQuestLogSize();
            for (int i = 0; i < questsCount; i++)
            {
                QuestLog? logEntry = ReadQuestLogEntry(i, null, updateFields!);
                if (logEntry == null || logEntry.QuestID == null)
                    continue;

                if (GameData.GetQuestTemplate((uint)logEntry.QuestID) == null)
                {
                    WorldPacket packet2 = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
                    packet2.WriteUInt32((uint)logEntry.QuestID);
                    SendPacketToServer(packet2);
                }
            }
            return;
        }

        //MIRASU - track running total ourselves; legacy update-field cache isn't refreshed on partial updates
        var key = (objective.QuestID, objective.StorageIndex);
        if (!GetSession().GameState.QuestItemObjectiveProgress.TryGetValue(key, out uint stored))
        {
            //MIRASU - first time we see this objective: seed from the legacy quest log cache.
            //MIRASU   The cache holds the accept-time progress (e.g. 3/10 if the player logged in
            //MIRASU   mid-quest); without seeding, the running total starts at 0 and the toast
            //MIRASU   would render "1/10" instead of "4/10" on the next pickup.
            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
            if (updateFields != null)
            {
                int questsCount = LegacyVersion.GetQuestLogSize();
                for (int i = 0; i < questsCount; i++)
                {
                    QuestLog? logEntry = ReadQuestLogEntry(i, null, updateFields);
                    if (logEntry == null || logEntry.QuestID != objective.QuestID)
                        continue;
                    if (logEntry.ObjectiveProgress[objective.StorageIndex] != null)
                        stored = (uint)logEntry.ObjectiveProgress[objective.StorageIndex]!;
                    break;
                }
            }
        }
        uint runningTotal = stored + count;
        if (runningTotal > (uint)objective.Amount)
            runningTotal = (uint)objective.Amount;
        GetSession().GameState.QuestItemObjectiveProgress[key] = runningTotal;

        //MIRASU - send full QuestUpdateAddCredit (not SIMPLE) so the over-head toast has explicit
        //MIRASU   Count/Required values. SIMPLE makes the modern client read from the legacy
        //MIRASU   UNIT_FIELD_QUEST_LOG_x cache, which Kronos doesn't refresh on partial item-pickup
        //MIRASU   updates -- so the toast froze at whatever the cache held on quest accept (1/10).
        QuestUpdateAddCredit credit = new QuestUpdateAddCredit();
        credit.QuestID = objective.QuestID;
        credit.ObjectID = (int)itemId;
        credit.ObjectiveType = QuestObjectiveType.Item;
        credit.Count = (ushort)runningTotal;
        credit.Required = (ushort)objective.Amount;
        credit.VictimGUID = default; //MIRASU - no victim for item pickups; modern client ignores VictimGUID for Item objectives
        SendPacketToClient(credit);
    }

    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_ADD_KILL)]
    void HandleQuestUpdateAddKill(WorldPacket packet)
    {
        QuestUpdateAddCredit credit = new QuestUpdateAddCredit();
        credit.QuestID = packet.ReadUInt32();
        var entry = packet.ReadEntry();
        credit.ObjectID = entry.Key;
        credit.ObjectiveType = entry.Value ? QuestObjectiveType.GameObject : QuestObjectiveType.Monster;
        credit.Count = (ushort)packet.ReadUInt32();
        credit.Required = (ushort)packet.ReadUInt32();
        credit.VictimGUID = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(credit);
    }

    [PacketHandler(Opcode.SMSG_QUEST_CONFIRM_ACCEPT)]
    void HandleQuestConfirmAccept(WorldPacket packet)
    {
        QuestConfirmAccept quest = new QuestConfirmAccept();
        quest.QuestID = packet.ReadUInt32();
        quest.QuestTitle = packet.ReadCString();
        quest.InitiatedBy = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(quest);
    }

    [PacketHandler(Opcode.MSG_QUEST_PUSH_RESULT)]
    void HandleQuestPushResult(WorldPacket packet)
    {
        QuestPushResult quest = new QuestPushResult();
        quest.SenderGUID = packet.ReadGuid().To128(GetSession().GameState);
        quest.Result = (QuestPushReason)packet.ReadUInt8();
        SendPacketToClient(quest);
    }
}
