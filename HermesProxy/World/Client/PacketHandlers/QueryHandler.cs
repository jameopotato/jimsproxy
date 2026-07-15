using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using static HermesProxy.World.Server.Packets.QueryPageTextResponse;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_QUERY_TIME_RESPONSE)]
    void HandleQueryTimeResponse(WorldPacket packet)
    {
        QueryTimeResponse response = new QueryTimeResponse();
        response.CurrentTime = packet.ReadInt32();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) && packet.CanRead())
            packet.ReadInt32(); // Next Daily Quest Reset Time
        SendPacketToClient(response);
    }
    [PacketHandler(Opcode.SMSG_QUERY_QUEST_INFO_RESPONSE)]
    void HandleQueryQuestInfoResponse(WorldPacket packet)
    {
        QueryQuestInfoResponse response = new QueryQuestInfoResponse();
        var id = packet.ReadEntry();
        response.QuestID = (uint)id.Key;
        // JimsProxy: pair with the CMSG_QUERY_QUEST_INFO dedupe gate in WorldSocket.
        // Clear in-flight so subsequent CMSGs for this id (after addon retries, etc.)
        // can re-query if needed; cache the negative response so we don't ever ask
        // the legacy server about this id again this session.
        GetSession().GameState.InFlightClientQuestInfoQueries.TryRemove(response.QuestID, out _);
        if (id.Value) // entry is masked
        {
            GetSession().GameState.NegativeQuestInfoCache.TryAdd(response.QuestID, 0);
            response.Allow = false;
            SendPacketToClient(response);
            return;
        }

        response.Allow = true;
        response.Info = new QuestTemplate();
        QuestTemplate quest = response.Info;

        quest.QuestID = response.QuestID;
        quest.QuestType = packet.ReadInt32();
        quest.QuestLevel = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.MinLevel = packet.ReadInt32();
        else
            quest.MinLevel = 1;

        quest.QuestSortID = packet.ReadInt32();
        quest.QuestInfoID = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            quest.SuggestedGroupNum = packet.ReadUInt32();

        sbyte objectiveCounter = 0;
        for (int i = 0; i < 2; i++)
        {
            int factionId = packet.ReadInt32(); // RequiredFactionID
            int factionValue = packet.ReadInt32(); // RequiredFactionValue
            if (factionId != 0 && factionValue != 0)
            {
                QuestObjective objective = new QuestObjective();
                objective.QuestID = response.QuestID;
                objective.Id = QuestObjective.QuestObjectiveCounter++;
                objective.StorageIndex = objectiveCounter++;
                objective.Type = QuestObjectiveType.MinReputation;
                objective.ObjectID = factionId;
                objective.Amount = factionValue;
                quest.Objectives.Add(objective);
            }
        }

        quest.RewardNextQuest = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.RewardXPDifficulty = packet.ReadUInt32();

        int rewOrReqMoney = packet.ReadInt32();
        if (rewOrReqMoney >= 0)
            quest.RewardMoney = rewOrReqMoney;
        else
        {
            QuestObjective objective = new QuestObjective();
            objective.QuestID = response.QuestID;
            objective.Id = QuestObjective.QuestObjectiveCounter++;
            objective.StorageIndex = objectiveCounter++;
            objective.Type = QuestObjectiveType.Money;
            objective.ObjectID = 0;
            objective.Amount = -rewOrReqMoney;
            quest.Objectives.Add(objective);
        }
        quest.RewardBonusMoney = packet.ReadUInt32();
        quest.RewardDisplaySpell[0] = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            quest.RewardSpell = packet.ReadUInt32();
            quest.RewardHonor = packet.ReadUInt32();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.RewardKillHonor = packet.ReadFloat();

        quest.StartItem = packet.ReadUInt32();
        quest.Flags = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_4_0_8089))
            quest.RewardTitle = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            int requiredPlayerKills = packet.ReadInt32();
            if (requiredPlayerKills != 0)
            {
                QuestObjective objective = new QuestObjective();
                objective.QuestID = response.QuestID;
                objective.Id = QuestObjective.QuestObjectiveCounter++;
                objective.StorageIndex = objectiveCounter++;
                objective.Type = QuestObjectiveType.PlayerKills;
                objective.ObjectID = 0;
                objective.Amount = requiredPlayerKills;
                quest.Objectives.Add(objective);
            }
            packet.ReadUInt32(); // RewardTalents
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.RewardArenaPoints = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            packet.ReadInt32(); // Unk

        for (int i = 0; i < 4; i++)
        {
            quest.RewardItems[i] = packet.ReadUInt32();
            quest.RewardAmount[i] = packet.ReadUInt32();
        }

        for (int i = 0; i < 6; i++)
        {
            QuestInfoChoiceItem choiceItem = new QuestInfoChoiceItem();
            choiceItem.ItemID = packet.ReadUInt32();
            choiceItem.Quantity = packet.ReadUInt32();

            uint displayId = GameData.GetItemDisplayId(choiceItem.ItemID);
            if (displayId != 0)
                choiceItem.DisplayID = displayId;

            quest.UnfilteredChoiceItems[i] = choiceItem;
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
        {
            for (int i = 0; i < 5; i++)
                quest.RewardFactionID[i] = packet.ReadUInt32();

            for (int i = 0; i < 5; i++)
                quest.RewardFactionValue[i] = packet.ReadInt32();

            for (int i = 0; i < 5; i++)
                quest.RewardFactionOverride[i] = (int)packet.ReadUInt32();
        }

        quest.POIContinent = packet.ReadUInt32();
        quest.POIx = packet.ReadFloat();
        quest.POIy = packet.ReadFloat();
        quest.POIPriority = packet.ReadUInt32();
        quest.LogTitle = packet.ReadCString();
        quest.LogDescription = packet.ReadCString();
        quest.QuestDescription = packet.ReadCString();
        quest.AreaDescription = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            quest.QuestCompletionLog = packet.ReadCString();

        var reqId = new KeyValuePair<int, bool>[4];
        var reqItemFieldCount = 4;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
            reqItemFieldCount = 5;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            reqItemFieldCount = 6;
        int[] requiredItemID = new int[reqItemFieldCount];
        int[] requiredItemCount = new int[reqItemFieldCount];

        for (int i = 0; i < 4; i++)
        {
            reqId[i] = packet.ReadEntry();
            bool isGo = reqId[i].Value;

            int creatureOrGoId = reqId[i].Key;
            int creatureOrGoAmount = packet.ReadInt32();

            if (creatureOrGoId != 0 && creatureOrGoAmount != 0)
            {
                // StorageIndex must equal the vanilla server's counter index, which is the
                // ABSOLUTE slot 'i' in ReqCreatureOrGOId[0..3] — vmangos credits via
                // SetQuestSlotCounter(slot, creatureOrGO_idx, count) where creatureOrGO_idx
                // is this loop's 'i' (Player.cpp::SendQuestUpdateAddCreatureOrGo). The
                // modern client reads ObjectiveProgress[StorageIndex] for progress, so if
                // we compressed via objectiveCounter++ it would mis-align whenever
                // ReqCreatureOrGOId has a zero gap (e.g. Twinstar quest #905 "The Angry
                // Scytheclaws" — slot 0 is empty, Blue/Yellow/Red at slots 1/2/3 — the
                // unticked counter[0] showed under whichever objective got compressed
                // StorageIndex 0, leaving Blue Raptor Nest perpetually 0/1 in the modern
                // client even though the quest was server-completable).
                QuestObjective objective = new QuestObjective();
                objective.QuestID = response.QuestID;
                objective.Id = QuestObjective.QuestObjectiveCounter++;
                objective.StorageIndex = (sbyte)i;
                objective.Type = isGo ? QuestObjectiveType.GameObject : QuestObjectiveType.Monster;
                objective.ObjectID = creatureOrGoId;
                objective.Amount = creatureOrGoAmount;
                quest.Objectives.Add(objective);
                if (objectiveCounter < i + 1)
                    objectiveCounter = (sbyte)(i + 1);
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                requiredItemID[i] = packet.ReadInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                requiredItemCount[i] = packet.ReadInt32();

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_8_9464))
            {
                requiredItemID[i] = packet.ReadInt32();
                requiredItemCount[i] = packet.ReadInt32();
            }
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
        {
            for (int i = 0; i < reqItemFieldCount; i++)
            {
                requiredItemID[i] = packet.ReadInt32();
                requiredItemCount[i] = packet.ReadInt32();
            }
        }

        for (int i = 0; i < reqItemFieldCount; i++)
        {
            if (requiredItemID[i] != 0 && requiredItemCount[i] != 0)
            {
                QuestObjective objective = new QuestObjective();
                objective.QuestID = response.QuestID;
                objective.Id = QuestObjective.QuestObjectiveCounter++;
                objective.StorageIndex = objectiveCounter++;
                objective.Type = QuestObjectiveType.Item;
                objective.ObjectID = requiredItemID[i];
                objective.Amount = requiredItemCount[i];
                quest.Objectives.Add(objective);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            string objectiveText = packet.ReadCString();
            if (string.IsNullOrEmpty(objectiveText))
                continue;
            // ObjectiveText[i] is keyed by ABSOLUTE wire slot in vmangos
            // (Quest.cpp::QuestQueryResponse::AppendBodyTo), matching ReqCreatureOrGOId[i].
            // With the StorageIndex=i alignment above, the description for slot i belongs
            // to whichever objective sits at StorageIndex == i (GO/Monster objectives
            // only — item/rep objectives keep their compressed StorageIndex). Falling
            // back to creation order only when no matching slot exists preserves
            // pre-existing behavior for non-GO objectives.
            QuestObjective? match = null;
            for (int oi = 0; oi < quest.Objectives.Count; oi++)
            {
                var obj = quest.Objectives[oi];
                if (obj.StorageIndex == i &&
                    (obj.Type == QuestObjectiveType.GameObject || obj.Type == QuestObjectiveType.Monster))
                {
                    match = obj;
                    break;
                }
            }
            if (match == null && quest.Objectives.Count > i)
                match = quest.Objectives[i];
            if (match != null)
                match.Description = objectiveText;
        }

        // Placeholders
        quest.QuestMaxScalingLevel = 255;
        quest.RewardXPMultiplier = 1;
        quest.RewardMoneyMultiplier = 1;
        quest.RewardArtifactXPMultiplier = 1;
        for (int i = 0; i < QuestConst.QuestRewardReputationsCount; i++)
            quest.RewardFactionCapIn[i] = 7;
        quest.AllowableRaces = 511;
        quest.AcceptedSoundKitID = 890;
        quest.CompleteSoundKitID = 878;

        GameData.StoreQuestTemplate(response.QuestID, quest);
        SendPacketToClient(response);

        //MIRASU - any SMSG_QUEST_UPDATE_ADD_ITEM credits buffered while this template was missing
        //MIRASU   can now resolve their QuestObjective. Replay them so the over-head toast doesn't
        //MIRASU   drop the first pickup of an unfamiliar quest item (off-by-1 bug).
        ReplayPendingQuestItemCredits();

        // Re-emit the player's quest log entry for this quest so the modern client
        // re-renders ObjectiveProgress with the now-resolvable item-objective overlay
        // applied (see ReadQuestLogEntry's item-overlay block). Without this re-emit,
        // a proxy-issued CMSG_QUERY_QUEST_INFO during login (UpdateHandler's quest
        // log walk) populates the template *after* the initial OBJECT_UPDATE has
        // already been serialized to the modern client at 0/N, leaving the visual
        // permanently stale until some unrelated event triggers another quest log
        // field update. Only re-emit when the quest actually has item objectives —
        // pure-kill quests don't need this round-trip.
        if (quest.Objectives.Exists(o => o.Type == QuestObjectiveType.Item))
        {
            var playerGuid = GetSession().GameState.CurrentPlayerGuid;
            var playerFields = GetSession().GameState.GetCachedObjectFieldsLegacy(playerGuid);
            if (playerFields != null)
            {
                int questLogSize = LegacyVersion.GetQuestLogSize();
                for (int slot = 0; slot < questLogSize; slot++)
                {
                    var logEntry = ReadQuestLogEntry(slot, null, playerFields);
                    if (logEntry == null || logEntry.QuestID != response.QuestID)
                        continue;

                    ObjectUpdate refresh = new ObjectUpdate(playerGuid, UpdateTypeModern.Values, GetSession());
                    refresh.PlayerData.QuestLog[slot] = logEntry;
                    UpdateObject refreshPacket = new UpdateObject(GetSession().GameState);
                    refreshPacket.ObjectUpdates.Add(refresh);
                    SendPacketToClient(refreshPacket);
                    break;
                }
            }
        }
    }

    [PacketHandler(Opcode.SMSG_QUERY_CREATURE_RESPONSE)]
    void HandleQueryCreatureResponse(WorldPacket packet)
    {
        QueryCreatureResponse response = new QueryCreatureResponse();
        var id = packet.ReadEntry();
        response.CreatureID = (uint)id.Key;
        if (id.Value) // entry is masked
        {
            response.Allow = false;
            SendPacketToClient(response);
            return;
        }

        response.Allow = true;
        response.Stats = new CreatureTemplate();
        CreatureTemplate creature = response.Stats;

        for (int i = 0; i < 4; i++)
            creature.Name[i] = packet.ReadCString();

        creature.Title = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            creature.CursorName = packet.ReadCString();

        creature.Flags[0] = packet.ReadUInt32(); // Type Flags
        creature.Type = packet.ReadInt32();
        creature.Family = packet.ReadInt32();
        creature.Classification = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
        {
            for (int i = 0; i < 2; ++i)
                creature.ProxyCreatureID[i] = packet.ReadUInt32();
        }
        else
        {
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.ReadInt32(); // unk
            creature.PetSpellDataId = packet.ReadUInt32();
        }

        int displayIdCount = LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) ? 4 : 1;
        for (int i = 0; i < displayIdCount; i++)
        {
            uint displayId = packet.ReadUInt32();
            if (displayId != 0)
                creature.Display.CreatureDisplay.Add(new CreatureXDisplay(displayId, 1, 0));
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            creature.HpMulti = packet.ReadFloat();
            creature.EnergyMulti = packet.ReadFloat();
        }
        else
        {
            creature.HpMulti = 1;
            creature.EnergyMulti = 1;
        }

        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            creature.Civilian = packet.ReadBool();
        creature.Leader = packet.ReadBool();

        int questItems = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192) ? 6 : 4;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
        {
            for (uint i = 0; i < questItems; ++i)
            {
                uint itemId = packet.ReadUInt32();
                if (itemId != 0)
                    creature.QuestItems.Add(itemId);
            }

            packet.ReadUInt32(); // Movement ID
        }

        // Placeholders
        creature.Flags[0] |= 134217728;
        creature.MovementInfoID = 1693;
        creature.Class = 1;

        GameData.StoreCreatureTemplate(response.CreatureID, creature);
        SendPacketToClient(response);

        // JimsProxy (pet-scale-resolve-race): drain any pet GUIDs that arrived
        // with SCALE_X before this template's family was known. Recompute K
        // from the now-resolved family and emit a SCALE_X-only values update so
        // the first-sight "big pet" snaps to correct size without a resummon.
        ResolvePendingPetScalesForEntry(response.CreatureID, creature.Family);
    }

    private void ResolvePendingPetScalesForEntry(uint entry, int familyId)
    {
        var pending = GetSession().GameState.PetScaleResolvePending;
        if (pending.Count == 0) return;

        // Collect matches first; we mutate the dict while iterating.
        var matches = new System.Collections.Generic.List<PendingPetScale>();
        foreach (var kv in pending)
        {
            if (kv.Value.Entry == entry)
                matches.Add(kv.Value);
        }
        if (matches.Count == 0) return;

        // JimsProxy: K lookup chain matches UpdateHandler's isLocalPet branch —
        // per-DisplayID vanilla CMS wins over family-table MaxScale, because
        // CSV/CreatureDisplayInfoVanilla.csv tunes specific display variants
        // (e.g. Imp displayId 4449 → 2.0) that family-table aggregates would
        // otherwise overwrite. Previously this resolver used family-table K
        // only — and re-emitted SCALE_X 200-300ms after summon with the
        // family value, shrinking the imp back from 2.0 to 0.5 once the
        // CMSG_QUERY_CREATURE response landed (user-visible "pet shrinks
        // when buff happens" — actually template-query timing).
        foreach (var p in matches)
        {
            float? vanillaCmsK = (p.DisplayId > 0 && GameData.VanillaCreatureModelScales.TryGetValue(p.DisplayId, out var vCms))
                ? vCms
                : (float?)null;
            float? familyTableK = (familyId > 0)
                ? GameData.GetPetFamilyScale(familyId)
                : (float?)null;
            // Mirror UpdateHandler's isLocalPet branch: per-ModelId M_native
            // multiplier on vanilla CMS_v (see M2NativeRatio).
            int modelIdForRatio = p.DisplayId > 0
                ? (int)GameData.GetDisplayInfo(p.DisplayId).ModelId
                : 0;
            float mRatio = 1f;
            bool modelRatioApplied = modelIdForRatio > 0
                                     && vanillaCmsK.HasValue
                                     && WorldClient.M2NativeRatio.TryGetValue(modelIdForRatio, out mRatio);
            float k = modelRatioApplied
                ? vanillaCmsK!.Value * mRatio
                : (vanillaCmsK ?? familyTableK ?? (p.IsWarlockPet ? 0.75f : 1.5f));

            // Server-pre-scaled summons (engineering trinket pets): strip the
            // pre-baked wire scale, mirroring UpdateHandler's pet branch — without
            // this, the creature-query resolver re-emits the oversized value
            // 200-300ms after summon and overrides the corrected first emit.
            // Gate on wire > 1.0 — see the matching comment in UpdateHandler's pet
            // branch. Small pets (wire < 1.0) keep their M2NativeRatio/family logic.
            float effectiveWire = p.RawScale;
            bool wirePreScaled = vanillaCmsK.HasValue
                && p.RawScale > 1.01f
                && p.RawScale >= vanillaCmsK.Value - 0.01f
                && p.RawScale < vanillaCmsK.Value * 1.5f;
            if (wirePreScaled)
                effectiveWire = 1.0f;

            float emit = (p.Cms > 0)
                ? (effectiveWire / p.Cms) * k
                : effectiveWire * k;

            // Synthesize a values-update carrying just OBJECT_FIELD_SCALE_X.
            UpdateObject updateObject = new UpdateObject(GetSession().GameState);
            ObjectUpdate update = new ObjectUpdate(p.Guid, UpdateTypeModern.Values, GetSession());
            update.ObjectData.Scale = emit;
            updateObject.ObjectUpdates.Add(update);
            SendPacketToClient(updateObject);

            Framework.Logging.Log.Event("unit.pet_scale.resolved_after_template", new
            {
                guid = p.Guid.ToString(),
                entry = p.Entry,
                display_id = p.DisplayId,
                family_id = familyId,
                k = k,
                k_source = modelRatioApplied ? "model-native-ratio"
                           : vanillaCmsK.HasValue ? "vanilla-dbc"
                           : familyTableK.HasValue ? "family-table"
                           : (p.IsWarlockPet ? "k-warlock-fallback" : "k-hunter-fallback"),
                raw_scale = p.RawScale,
                wire_pre_scaled = wirePreScaled,
                effective_wire = effectiveWire,
                emitted_scale = emit,
            });

            pending.Remove(p.Guid);
        }
    }
    [PacketHandler(Opcode.SMSG_QUERY_GAME_OBJECT_RESPONSE)]
    void HandleQueryGameObjectResposne(WorldPacket packet)
    {
        QueryGameObjectResponse response = new QueryGameObjectResponse();
        var id = packet.ReadEntry();
        response.GameObjectID = (uint)id.Key;
        response.Guid = WowGuid128.Empty;
        if (id.Value) // entry is masked
        {
            response.Allow = false;
            SendPacketToClient(response);
            return;
        }

        response.Allow = true;
        response.Stats = new GameObjectStats();
        GameObjectStats gameObject = response.Stats;

        gameObject.Type = packet.ReadUInt32();

        // Rookery Egg (175124): present vanilla TRAP (6) as GOOBER (10) so the 1.14 client lets
        // the Eggscilloscope target it (see UpdateHandler / #387). This is the value cached in
        // gameobjectcache.wdb, so players must clear that cache once to pick up the change.
        if (response.GameObjectID == 175124 && gameObject.Type == 6)
        {
            gameObject.Type = 10;
            Log.Event("gameobject.egg.typeoverride", new { entry = response.GameObjectID, from = 6, to = 10 });
        }

        gameObject.DisplayID = packet.ReadUInt32();

        for (int i = 0; i < 4; i++)
            gameObject.Name[i] = packet.ReadCString();

        gameObject.IconName = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            gameObject.CastBarCaption = packet.ReadCString();
            gameObject.UnkString = packet.ReadCString();
        }

        for (int i = 0; i < 24; i++)
            gameObject.Data[i] = packet.ReadInt32();

        // JimsProxy (#403 DIAGNOSTIC — remove once settled): hunter-trap disarm
        // data-shaping experiments. The 1.14 client blocks Disarm Trap pre-send with
        // a fabricated "Requires Disarm Trap (300)" for vanilla trap templates
        // (type 6, lockId 12, level 0, no ContentTuning). Variants reshape one
        // template field each to discriminate which one the client's lock-gate
        // reads. Type check runs AFTER the egg override above, so the Rookery Egg
        // (now GOOBER) is never touched. Data[0] != 0 excludes lockless
        // environmental/hazard traps.
        if (Framework.Settings.TrapDisarmDataShape != 0 && gameObject.Type == 6 && gameObject.Data[0] != 0)
        {
            int variant = Framework.Settings.TrapDisarmDataShape;
            int lockBefore = gameObject.Data[0];
            int levelBefore = gameObject.Data[1];
            if (variant == 1 || variant == 4)
                gameObject.Data[1] = 1;              // trap level 0 -> 1 (required = 5*level theory)
            if (variant == 2 || variant == 4)
                gameObject.ContentTuningId = 2180;   // 1.14.2's sole ContentTuning row (1-60)
            if (variant == 3 || variant == 4)
                gameObject.Data[0] = 13;             // lock 13: explicit Disarm skill 50
            Log.Event("gameobject.trap.shape", new
            {
                entry = response.GameObjectID,
                variant,
                lock_before = lockBefore,
                lock_after = gameObject.Data[0],
                level_before = levelBefore,
                level_after = gameObject.Data[1],
                content_tuning_after = gameObject.ContentTuningId,
            });
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            gameObject.Size = packet.ReadFloat();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
        {
            uint count = (uint)(LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192) ? 6 : 4);
            for (uint i = 0; i < count; i++)
            {
                uint itemId = packet.ReadUInt32();
                if (itemId != 0)
                    gameObject.QuestItems.Add(itemId);
            }
        }

        SendPacketToClient(response);
    }
    [PacketHandler(Opcode.SMSG_QUERY_PAGE_TEXT_RESPONSE)]
    void HandleQueryPageTextResponse(WorldPacket packet)
    {
        QueryPageTextResponse response = new QueryPageTextResponse();
        response.PageTextID = packet.ReadUInt32();
        response.Allow = true;
        PageTextInfo page = new PageTextInfo();
        page.Id = response.PageTextID;
        page.Text = packet.ReadCString();
        page.NextPageID = packet.ReadUInt32();
        response.Pages.Add(page);
        SendPacketToClient(response);
    }
    [PacketHandler(Opcode.SMSG_QUERY_NPC_TEXT_RESPONSE)]
    void HandleQueryNpcTextResponse(WorldPacket packet)
    {
        QueryNPCTextResponse response = new QueryNPCTextResponse();
        var id = packet.ReadEntry();
        response.TextID = (uint)id.Key;
        if (id.Value) // entry is masked
        {
            response.Allow = false;
            SendPacketToClient(response);
            return;
        }

        response.Allow = true;

        for (int i = 0; i < 8; i++)
        {
            response.Probabilities[i] = packet.ReadFloat();
            string maleText = packet.ReadCString().TrimEnd().Replace("\0", "");
            string femaleText = packet.ReadCString().TrimEnd().Replace("\0", "");
            uint language = packet.ReadUInt32();

            ushort[] emoteDelays = new ushort[3];
            ushort[]  emotes = new ushort[3];
            for (int j = 0; j < 3; j++)
            {
                emoteDelays[j] = (ushort)packet.ReadUInt32();
                emotes[j] = (ushort)packet.ReadUInt32();
            }

            const string placeholderGossip = "Greetings $N";

            if (String.IsNullOrEmpty(maleText) && String.IsNullOrEmpty(femaleText) ||
                maleText.Equals(placeholderGossip) && femaleText.Equals(placeholderGossip) && i != 0)
                response.BroadcastTextID[i] = 0;
            else
                response.BroadcastTextID[i] = GameData.GetBroadcastTextId(maleText, femaleText, language, emoteDelays, emotes);
        }

        SendPacketToClient(response);
    }

    // JimsProxy (Kronos Chronoboon): mint a fresh throwaway alias entry for a Chronoboon item GUID,
    // cloning the given (current-state) template and pre-building its Item + on-use ItemEffect hotfix
    // packets for HandleDbQueryBulk to deliver in the client's query window (building them here on the WC
    // thread keeps that WS-thread handler from racing the loot path's record-store writes). Frees the
    // prior alias for this GUID first. Returns the alias id. Used by BOTH the on-use refresh (which then
    // destroy+recreates the item) and the login-time proactive alias (which lets the natural item create
    // carry the alias — no recreate, so an active on-use cooldown survives).
    private uint MintChronoboonAlias(WowGuid128 guid, ItemTemplate template)
    {
        if (GameData.ItemEntryAlias.TryGetValue(guid, out var priorAlias))
        {
            GameData.EvictItemEntryAlias(priorAlias);
            GetSession().GameState.AliasPendingPackets.TryRemove(priorAlias, out _);
        }

        uint alias = GameData.NextItemEntryAlias();
        ItemTemplate aliasTemplate = template.CloneWithEntry(alias);
        GameData.StoreItemTemplate(alias, aliasTemplate);
        GameData.ItemEntryAlias[guid] = alias;

        var aliasPackets = new List<ServerPacket>();
        var aliasItemMsg = GameData.GenerateItemUpdateIfNeeded(aliasTemplate);
        if (aliasItemMsg != null)
            aliasPackets.Add(aliasItemMsg);
        for (byte slot = 0; slot < 5; slot++)
        {
            var effectMsg = GameData.GenerateItemEffectUpdateIfNeeded(aliasTemplate, slot);
            if (effectMsg == null)
                continue;
            aliasPackets.Add(effectMsg);
            var effectHotfix = effectMsg.Hotfixes[0];
            aliasPackets.Add(new DBReply
            {
                Status = effectHotfix.Status,
                Timestamp = (uint)Time.UnixTime,
                RecordID = effectHotfix.RecordId,
                TableHash = effectHotfix.TableHash,
                Data = effectHotfix.HotfixContent,
            });
        }
        GetSession().GameState.AliasPendingPackets[alias] = aliasPackets;
        return alias;
    }

    [PacketHandler(Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE)]
    void HandleItemQueryResponse(WorldPacket packet)
    {
        var entry = packet.ReadEntry();
        if (entry.Value)
        {
            if (GetSession().GameState.RequestedItemHotfixes.Contains((uint)entry.Key))
            {
                DBReply reply = new();
                reply.RecordID = (uint)entry.Key;
                reply.TableHash = DB2Hash.Item;
                reply.Status = HotfixStatus.Invalid;
                reply.Timestamp = (uint)Time.UnixTime;
                SendPacketToClient(reply);
            }
            if (GetSession().GameState.RequestedItemSparseHotfixes.Contains((uint)entry.Key))
            {
                DBReply reply2 = new();
                reply2.RecordID = (uint)entry.Key;
                reply2.TableHash = DB2Hash.ItemSparse;
                reply2.Status = HotfixStatus.Invalid;
                reply2.Timestamp = (uint)Time.UnixTime;
                SendPacketToClient(reply2);
            }
            return;
        }

        ItemTemplate item = new ItemTemplate();
        item.ReadFromLegacyPacket((uint)entry.Key, packet);

        // JimsProxy (Kronos Chronoboon): the modern client keys the bag pickup/putdown sound off the Item.db2 ItemGroupSoundsId (verified in the client's Item table: soul shards + empty/crystal/leaded vials = group 24 = the light glass "clink", full potions = 17, Misc class-15 = 16, a legacy-queried custom item = 0 = silent). Force 24 so the Chronoboon gets the soul-shard/empty-vial clink it has natively on 1.12. Set on the parsed template so the base item AND the alias clone (below) carry it; name shifts ("Empty"/"Supercharged...XL") as buffs store, so match on Contains.
        if (item.Name[0]?.Contains("Chronoboon Displacer") == true)
        {
            item.ItemGroupSoundsId = 24;
        }

        // JimsProxy (Kronos Chronoboon): this answers the proxy-issued re-query fired from
        // HandleUseItem after a store/restore. The server rebuilt the item's Name/Description
        // (the stored-world-buff list) for the current state. The 1.14 client already has this
        // item cached and won't re-query, so refresh its tooltip by live-pushing the updated
        // ItemSparse as a hotfix with a fresh id (UpdateHotfix reuses ids, which the client
        // treats as already-applied; only a fresh id takes mid-session). Bypasses the normal
        // SendItemUpdatesIfNeeded path, whose existing-record branch is a documented client
        // crash that defers to next login.
        if (GetSession().GameState.DynamicItemRefreshPending.TryGetValue((uint)entry.Key, out var chronoGuid))
        {
            GetSession().GameState.DynamicItemRefreshPending.Remove((uint)entry.Key);
            GameData.StoreItemTemplate((uint)entry.Key, item); // keep the REAL template current (use-side detection reads it)

            // Mint a throwaway alias entry carrying the just-queried Name/Description/icon + on-use,
            // and present the bag item to the client under it (see the alias substitution in
            // StoreObjectUpdateInternal). The client has never seen this id, so it fetches it clean
            // and renders the live tooltip — beating the per-id template cache that blocks every
            // in-place refresh. The REAL entry stays in the legacy field cache, so use-item still
            // forwards by slot/GUID untouched (HandleUseItem detection re-mints on the next use).
            MintChronoboonAlias(chronoGuid, item);

            // Swap the bag item to the alias so the client re-reads it (it ignores a create for an
            // object it already has and a Values entry change, so RecreateItemObjectForClient does a
            // destroy + re-create with the alias entry substituted in; same GUID keeps the slot).
            // Safe to do immediately here: this re-query was triggered by the on-use cast's SMSG_SPELL_GO
            // (HandleSpellGo), i.e. the cast already completed, so the destroy can't interrupt it.
            RecreateItemObjectForClient(chronoGuid);

            // Re-assert the on-use cooldown — the destroy+recreate wiped the recreated item's sweep and
            // the server's one-shot SMSG_COOLDOWN_EVENT was consumed by the now-destroyed object. An item
            // cooldown binds to the item GUID (which exists the moment the recreate above ran), so it
            // applies even before the alias resolves and shows the instant it does. Sent SYNCHRONOUSLY on
            // this (WC) thread: a Task.Delay continuation could spin a pool thread forever inside
            // SendPacketToClient's wait-for-socket loop if the player DC'd in the delay window.
            int onUseSpell = item.TriggeredSpellIds[0];
            int onUseCooldown = item.TriggeredSpellCooldowns[0];
            if (onUseSpell > 0 && onUseCooldown > 0)
            {
                ItemCooldown itemCooldownPkt = new();
                itemCooldownPkt.ItemGuid = chronoGuid;
                itemCooldownPkt.SpellID = (uint)onUseSpell;
                itemCooldownPkt.Cooldown = (uint)onUseCooldown;
                SendPacketToClient(itemCooldownPkt);

                SpellCooldownPkt spellCooldownPkt = new();
                spellCooldownPkt.Caster = GetSession().GameState.CurrentPlayerGuid;
                spellCooldownPkt.SpellCooldowns.Add(new SpellCooldownStruct
                {
                    SpellID = (uint)onUseSpell,
                    ForcedCooldown = (uint)onUseCooldown,
                });
                SendPacketToClient(spellCooldownPkt);

                // JimsProxy (Kronos Chronoboon): keep the captured end-time current after a use so a later
                // bank-open repaint (HandleShowBank) reflects the real remaining, not the stale login value.
                GetSession().GameState.ChronoboonOnUseCooldownEndMs[(uint)onUseSpell] = Environment.TickCount64 + onUseCooldown;
                GetSession().GameState.ChronoboonItemGuids.Add(chronoGuid);
            }

            return;
        }

        SendItemUpdatesIfNeeded(item);
        GameData.StoreItemTemplate((uint)entry.Key, item);
    }

    void SendItemUpdatesIfNeeded(ItemTemplate item)
    {
        Server.Packets.HotFixMessage? reply;

        reply = GameData.GenerateItemUpdateIfNeeded(item);
        if (reply != null)
            SendPacketToClient(reply);

        reply = GameData.GenerateItemSparseUpdateIfNeeded(item);
        if (reply != null)
        {
            // TODO!!! Something might be wrong here.
            // TODO: When I send the ItemSpare entry with HotFixMessage it does not work

            SendPacketToClient(reply); // TODO: <-- Optional??

            Server.Packets.DBReply replyA = new();
            replyA.Status = HotfixStatus.Valid;
            replyA.Timestamp = (uint)Time.UnixTime;
            replyA.RecordID = reply.Hotfixes[0].RecordId;
            replyA.TableHash = reply.Hotfixes[0].TableHash;
            replyA.Data = reply.Hotfixes[0].HotfixContent;
            SendPacketToClient(replyA);
        }

        for (byte i = 0; i < 5; i++)
        {
            reply = GameData.GenerateItemEffectUpdateIfNeeded(item, i);
            if (reply != null)
            {
                SendPacketToClient(reply);

                // Mirror the ItemSparse path: a paired DBReply is needed for the modern client
                // to actually apply the corrected ItemEffect record in the running session.
                // Without this, SoM 1.14.1+ renumbered on-use spells (e.g. Diamond Flask) stay
                // bound to their modern spell id even after the hotfix is sent.
                //
                // JimsProxy: mirror the hotfix's Status into the DBReply instead of hard-coding
                // Valid. For slot-remap items (CSV record at modern slot N, legacy server places
                // the spell at slot M ≠ N) the hotfix is RecordRemoved with empty content; the
                // old behavior trailed it with a contradictory DBReply (Status=Valid, empty
                // content) which is simply wrong on the wire even if 1.14.x clients tolerate
                // it. Mirroring keeps removals as removals end-to-end while Valid updates
                // (Diamond Flask path) continue unchanged.
                var hotfix = reply.Hotfixes[0];
                Server.Packets.DBReply replyA = new();
                replyA.Status = hotfix.Status;
                replyA.Timestamp = (uint)Time.UnixTime;
                replyA.RecordID = hotfix.RecordId;
                replyA.TableHash = hotfix.TableHash;
                replyA.Data = hotfix.HotfixContent;
                SendPacketToClient(replyA);

                Log.Event("hotfix.itemeffect.dbreply", new
                {
                    item_entry = item.Entry,
                    slot = i,
                    record_id = hotfix.RecordId,
                    table_hash = hotfix.TableHash.ToString(),
                    status = hotfix.Status.ToString(),
                    content_size = hotfix.HotfixContent.GetSize(),
                });
            }
        }

        if (!GameData.ItemCanHaveModel(item))
            return;

        reply = GameData.GenerateItemAppearanceUpdateIfNeeded(item);
        if (reply != null)
            SendPacketToClient(reply);

        reply = GameData.GenerateItemModifiedAppearanceUpdateIfNeeded(item);
        if (reply != null)
            SendPacketToClient(reply);
    }

    [PacketHandler(Opcode.SMSG_QUERY_PET_NAME_RESPONSE)]
    void HandleQueryPetNameResponse(WorldPacket packet)
    {
        uint petNumber = packet.ReadUInt32();
        WowGuid128 guid = GetSession().GameState.GetPetGuidByNumber(petNumber);
        string name = packet.ReadCString();
        Log.Event("pet.name_query.response", new
        {
            pet_number = petNumber,
            resolved_guid = guid == default ? "(none)" : guid.ToString(),
            name_empty = name.Length == 0,
            name = name,
        });
        if (guid == default)
        {
            Log.Print(LogType.Error, $"Pet name query response for unknown pet {petNumber}!");
            return;
        }

        QueryPetNameResponse response = new QueryPetNameResponse();
        response.UnitGUID = guid;
        response.Name = name;
        if (response.Name.Length == 0)
        {
            // Legacy server returned empty name (typically a petnumber/guid mismatch in
            // the CMSG). Still forward the response with Allow=false so the modern client
            // stops retrying — without a response it spins on the query and the unit
            // frame stays at "Unknown".
            response.Allow = false;
            SendPacketToClient(response);
            return;
        }

        response.Allow = true;
        response.Timestamp = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            var declined = packet.ReadBool();

            const int maxDeclinedNameCases = 5;

            if (declined)
            {
                for (var i = 0; i < maxDeclinedNameCases; i++)
                    response.DeclinedNames.name[i] = packet.ReadCString();
            }
        }
        SendPacketToClient(response);
    }
    [PacketHandler(Opcode.SMSG_ITEM_NAME_QUERY_RESPONSE)]
    void HandleItemNameQueryResponse(WorldPacket packet)
    {
        uint entry = packet.ReadUInt32();
        string name = packet.ReadCString();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.ReadUInt32(); // Inventory Type
        GameData.StoreItemName(entry, name);
    }
    [PacketHandler(Opcode.SMSG_WHO)]
    void HandleWhoResponse(WorldPacket packet)
    {
        WhoResponsePkt response = new WhoResponsePkt();
        response.RequestID = GetSession().GameState.LastWhoRequestId;
        var count = packet.ReadUInt32();
        packet.ReadUInt32(); // Online count
        for (var i = 0; i < count; ++i)
        {
            WhoEntry player = new();
            player.PlayerData.Name = packet.ReadCString();
            player.GuildName = packet.ReadCString();
            player.PlayerData.Level = (byte)packet.ReadUInt32();
            player.PlayerData.ClassID = (Class)packet.ReadUInt32();
            player.PlayerData.RaceID = (Race)packet.ReadUInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                player.PlayerData.Sex = (Gender)packet.ReadUInt8();
            player.AreaID = packet.ReadInt32();

            player.PlayerData.GuidActual = GetSession().GameState.GetPlayerGuidByName(player.PlayerData.Name);
            if (player.PlayerData.GuidActual == default)
                player.PlayerData.GuidActual = WowGuid128.CreateUnknownPlayerGuid();
            player.PlayerData.AccountID = GetSession().GetGameAccountGuidForPlayer(player.PlayerData.GuidActual);
            player.PlayerData.BnetAccountID = GetSession().GetBnetAccountGuidForPlayer(player.PlayerData.GuidActual);
            player.PlayerData.VirtualRealmAddress = GetSession().RealmId.GetAddress();

            if (!String.IsNullOrEmpty(player.GuildName))
            {
                player.GuildGUID = GetSession().GetGuildGuid(player.GuildName);
                player.GuildVirtualRealmAddress = player.PlayerData.VirtualRealmAddress;
            }
            response.Players.Add(player);
            Session.GameState.UpdatePlayerCache(player.PlayerData.GuidActual, new PlayerCache
            {
                Name = player.PlayerData.Name,
                RaceId = player.PlayerData.RaceID,
                ClassId = player.PlayerData.ClassID,
                SexId = player.PlayerData.Sex,
                Level = player.PlayerData.Level,
            });
        }
        SendPacketToClient(response);
    }
}
