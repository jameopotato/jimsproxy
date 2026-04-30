using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
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
        //MIRASU   Clear from the saved snapshot too, so a logout/login can't restore stale entries.
        //MIRASU   ConcurrentDictionary's TryRemove is the atomic equivalent of Dictionary.Remove.
        var progressMap = GetSession().GameState.QuestItemObjectiveProgress;
        foreach (var k in progressMap.Keys.Where(k => k.QuestID == quest.QuestID).ToList())
            progressMap.TryRemove(k, out _);
        if (GetSession().SavedQuestItemProgressByCharacter.TryGetValue(GetSession().GameState.CurrentPlayerGuid, out var savedForPlayer))
            foreach (var k in savedForPlayer.Keys.Where(k => k.QuestID == quest.QuestID).ToList())
                savedForPlayer.TryRemove(k, out _);
        //MIRASU - persist the post-clear saved snapshot to disk so a crash before next graceful
        //MIRASU   logout can't restore a stale entry for the just-completed/failed quest on the
        //MIRASU   next proxy start.
        GetSession().PersistQuestItemProgressForCurrentPlayer();
        //MIRASU - drop pending buffered credits whose itemId matches an item-objective in the
        //MIRASU   ending quest's template, so a deferred ReplayPendingQuestItemCredits can't
        //MIRASU   misattribute the count to a re-accept (or another active quest sharing the
        //MIRASU   item). If the template isn't cached we conservatively skip -- that means the
        //MIRASU   pending entry was for a quest the proxy never learned about, so attribution
        //MIRASU   is already ambiguous and dropping would be guesswork.
        DropPendingQuestItemCreditsForQuest(quest.QuestID);
    }

    //MIRASU - shared helper: removes any (itemId, count) pending entries whose item appears in the
    //MIRASU   given quest's item-objectives. Lock-protected because callers run on different threads
    //MIRASU   (client thread for COMPLETE/FAILED/replay, server thread for abandon).
    public void DropPendingQuestItemCreditsForQuest(uint questId)
    {
        var template = GameData.GetQuestTemplate(questId);
        if (template == null)
            return;
        var itemIds = new HashSet<uint>();
        foreach (var obj in template.Objectives)
        {
            if (obj.Type == QuestObjectiveType.Item)
                itemIds.Add((uint)obj.ObjectID);
        }
        if (itemIds.Count == 0)
            return;

        int removed;
        lock (GetSession().GameState.PendingQuestItemCreditsLock)
        {
            removed = GetSession().GameState.PendingQuestItemCredits.RemoveAll(p => itemIds.Contains(p.ItemId));
        }
        if (removed > 0)
        {
            Log.Event("quest.item.pending.dropped", new
            {
                quest_id = questId,
                item_ids = itemIds.ToArray(),
                removed,
            });
        }
    }
    [PacketHandler(Opcode.SMSG_QUEST_UPDATE_ADD_ITEM)]
    void HandleQuestUpdateAddItem(WorldPacket packet)
    {
        uint itemId = packet.ReadUInt32();
        uint count = packet.ReadUInt32(); //MIRASU - delta, not total

        if (ProcessQuestItemCredit(itemId, count, replayed: false))
            return;

        //MIRASU - quest template not cached yet. Buffer the credit so it can be replayed once the
        //MIRASU   template arrives via SMSG_QUERY_QUEST_INFO_RESPONSE -- otherwise the toast count
        //MIRASU   is permanently off-by-one for the first pickup of any unfamiliar quest item, since
        //MIRASU   the legacy server only sends the credit packet once per pickup. Then fire the
        //MIRASU   template queries (modeled on the original "Next pickup will find it" path).
        int pendingCount;
        lock (GetSession().GameState.PendingQuestItemCreditsLock)
        {
            GetSession().GameState.PendingQuestItemCredits.Add((itemId, count));
            pendingCount = GetSession().GameState.PendingQuestItemCredits.Count;
        }
        Log.Event("quest.item.add.buffered", new
        {
            item_id = itemId,
            wire_count = count,
            pending_count = pendingCount,
        });

        var pendingFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
        int questsCount = LegacyVersion.GetQuestLogSize();
        for (int i = 0; i < questsCount; i++)
        {
            QuestLog? logEntry = ReadQuestLogEntry(i, null, pendingFields!);
            if (logEntry == null || logEntry.QuestID == null)
                continue;

            if (GameData.GetQuestTemplate((uint)logEntry.QuestID) == null)
            {
                WorldPacket packet2 = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
                packet2.WriteUInt32((uint)logEntry.QuestID);
                SendPacketToServer(packet2);
            }
        }
    }

    //MIRASU - replays any quest item credits that were buffered because their QuestTemplate hadn't
    //MIRASU   arrived yet. Called from HandleQueryQuestInfoResponse after StoreQuestTemplate, so
    //MIRASU   the now-cached objective metadata can produce the deferred toast credit. Re-buffers
    //MIRASU   any (itemId, count) pair that still doesn't resolve (template was for a different
    //MIRASU   quest) so it'll be tried again on the next QueryQuestInfoResponse.
    public void ReplayPendingQuestItemCredits()
    {
        var gameState = GetSession().GameState;
        //MIRASU - drain under lock to avoid racing with abandon-clear (server thread) or
        //MIRASU   another buffered Add (client thread). Replay processing then runs without
        //MIRASU   the lock so ProcessQuestItemCredit doesn't have to be lock-aware.
        List<(uint ItemId, uint Count)> snapshot;
        lock (gameState.PendingQuestItemCreditsLock)
        {
            if (gameState.PendingQuestItemCredits.Count == 0)
                return;
            snapshot = new List<(uint, uint)>(gameState.PendingQuestItemCredits);
            gameState.PendingQuestItemCredits.Clear();
        }
        var unresolved = new List<(uint ItemId, uint Count)>();
        foreach (var (itemId, count) in snapshot)
        {
            if (!ProcessQuestItemCredit(itemId, count, replayed: true))
                unresolved.Add((itemId, count));
        }
        if (unresolved.Count > 0)
        {
            lock (gameState.PendingQuestItemCreditsLock)
                gameState.PendingQuestItemCredits.AddRange(unresolved);
        }

        //MIRASU - any SMSG_ITEM_PUSH_RESULT packets that were deferred because their objective
        //MIRASU   template wasn't cached at arrival time can now be replayed. Drain under lock,
        //MIRASU   recompute QuantityInInventory from the now-populated dict, send. Items whose
        //MIRASU   objective STILL doesn't resolve (template was for a different quest) are
        //MIRASU   re-buffered so a subsequent QUERY_QUEST_INFO_RESPONSE catches them.
        List<HermesProxy.World.Server.Packets.ItemPushResult> heldPushResults;
        lock (gameState.PendingItemPushResultsLock)
        {
            if (gameState.PendingItemPushResults.Count == 0)
                return;
            heldPushResults = new List<HermesProxy.World.Server.Packets.ItemPushResult>(gameState.PendingItemPushResults);
            gameState.PendingItemPushResults.Clear();
        }
        var unresolvedPushResults = new List<HermesProxy.World.Server.Packets.ItemPushResult>();
        foreach (var heldItem in heldPushResults)
        {
            QuestObjective? heldObjective = GameData.GetQuestObjectiveForItem(heldItem.Item.ItemID);
            if (heldObjective == null)
            {
                unresolvedPushResults.Add(heldItem);
                continue;
            }
            var heldKey = (heldObjective.QuestID, heldObjective.StorageIndex);
            if (gameState.QuestItemObjectiveProgress.TryGetValue(heldKey, out uint heldRunningTotal))
                heldItem.QuantityInInventory = heldRunningTotal;
            Framework.Logging.Log.Event("item.push.replayed", new
            {
                item_id = heldItem.Item.ItemID,
                quantity_in_inventory = heldItem.QuantityInInventory,
                quest_id = heldObjective.QuestID,
                storage_index = (int)heldObjective.StorageIndex,
            });
            SendPacketToClient(heldItem);
        }
        if (unresolvedPushResults.Count > 0)
        {
            lock (gameState.PendingItemPushResultsLock)
                gameState.PendingItemPushResults.AddRange(unresolvedPushResults);
        }
    }

    //MIRASU - shared credit-processing path used by both the live SMSG_QUEST_UPDATE_ADD_ITEM
    //MIRASU   handler and the buffered-replay path. Returns true if the objective was resolved
    //MIRASU   and the toast credit was sent; false if the QuestTemplate still isn't cached.
    private bool ProcessQuestItemCredit(uint itemId, uint count, bool replayed)
    {
        QuestObjective? objective = GameData.GetQuestObjectiveForItem(itemId);
        if (objective == null)
            return false;

        //MIRASU - restore saved per-character running totals if this is the first item credit since
        //MIRASU   a logout-to-charselect relog. No-op if already restored or nothing was saved for
        //MIRASU   the current player guid.
        GetSession().EnsureQuestItemProgressRestored();

        //MIRASU - track running total ourselves; legacy update-field cache isn't refreshed on partial updates
        var key = (objective.QuestID, objective.StorageIndex);
        bool seededFromCache = false;
        uint cacheValue = 0;
        bool updateFieldsPresent = false;
        int updateFieldsCount = 0;
        int matchedSlot = -1;
        sbyte? matchedSlotProgressRaw = null;
        if (!GetSession().GameState.QuestItemObjectiveProgress.TryGetValue(key, out uint stored))
        {
            //MIRASU - first time we see this objective in this session: seed from the legacy quest log cache.
            //MIRASU   The cache holds the server's last-persisted progress (e.g. 5/10 if the player logged
            //MIRASU   in mid-quest with 5 items already collected). Without this seed, the running total
            //MIRASU   starts at 0 after every relog and the toast would render "1/10" instead of "6/10" on
            //MIRASU   the next pickup. NOTE: SavedQuestItemProgressByCharacter handles the proxy-internal
            //MIRASU   relog persistence; this seed is the second line of defense that catches mid-quest
            //MIRASU   logins where the proxy never saw the prior session at all (server-persisted progress).
            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
            updateFieldsPresent = updateFields != null;
            updateFieldsCount = updateFields?.Count ?? 0;
            if (updateFields != null)
            {
                int questsCount = LegacyVersion.GetQuestLogSize();
                for (int i = 0; i < questsCount; i++)
                {
                    QuestLog? logEntry = ReadQuestLogEntry(i, null, updateFields);
                    if (logEntry == null || logEntry.QuestID != objective.QuestID)
                        continue;
                    matchedSlot = i;
                    var progressNullable = logEntry.ObjectiveProgress[objective.StorageIndex];
                    matchedSlotProgressRaw = progressNullable.HasValue ? (sbyte)progressNullable.Value : (sbyte?)null;
                    if (progressNullable != null)
                    {
                        stored = (uint)progressNullable!;
                        cacheValue = stored;
                        seededFromCache = true;
                    }
                    break;
                }
            }
            Log.Event("quest.item.seed.attempt", new
            {
                quest_id = objective.QuestID,
                storage_index = (int)objective.StorageIndex,
                item_id = itemId,
                update_fields_present = updateFieldsPresent,
                update_fields_count = updateFieldsCount,
                quest_log_size = LegacyVersion.GetQuestLogSize(),
                matched_slot = matchedSlot,
                matched_slot_progress = matchedSlotProgressRaw,
                seeded_from_cache = seededFromCache,
                cache_value = cacheValue,
            });
        }
        uint runningTotal = stored + count;
        bool clamped = runningTotal > (uint)objective.Amount;
        if (clamped)
            runningTotal = (uint)objective.Amount;
        GetSession().GameState.QuestItemObjectiveProgress[key] = runningTotal;

        Log.Event("quest.item.add", new
        {
            item_id = itemId,
            quest_id = objective.QuestID,
            storage_index = (int)objective.StorageIndex,
            wire_count = count,
            stored_before = stored,
            running_total = runningTotal,
            required = objective.Amount,
            seeded_from_cache = seededFromCache,
            cache_value = cacheValue,
            clamped,
            replayed,
        });

        //MIRASU - send full QuestUpdateAddCredit (not SIMPLE) so the over-head toast has explicit
        //MIRASU   Count/Required values. SIMPLE makes the modern client increment its own internal
        //MIRASU   counter, which (a) starts at 0 on every relog instead of the server's persisted
        //MIRASU   progress, and (b) doesn't get re-seeded from PLAYER_QUEST_LOG_x_3 on login -- so
        //MIRASU   after a logout/login the toast restarts at 1/N regardless of true progress.
        QuestUpdateAddCredit credit = new QuestUpdateAddCredit();
        credit.QuestID = objective.QuestID;
        credit.ObjectID = (int)itemId;
        credit.ObjectiveType = QuestObjectiveType.Item;
        credit.Count = (ushort)runningTotal;
        credit.Required = (ushort)objective.Amount;
        credit.VictimGUID = default; //MIRASU - no victim for item pickups; modern client ignores VictimGUID for Item objectives
        SendPacketToClient(credit);

        //MIRASU - snapshot in-memory + schedule debounced disk persist. Disk write collapses rapid
        //MIRASU   pickups into a single I/O (5s debounce). Logout/disconnect paths flush immediately.
        GetSession().SnapshotQuestItemProgressForRestore();
        return true;
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
