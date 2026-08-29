using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_SET_PROFICIENCY)]
    void HandleSetProficiency(WorldPacket packet)
    {
        SetProficiency proficiency = new SetProficiency();
        proficiency.ProficiencyClass = packet.ReadUInt8();
        proficiency.ProficiencyMask = packet.ReadUInt32();

        // #410 (item-proficiency tooltip): Kronos front-loads the whole proficiency burst BEFORE
        // SMSG_LOGIN_VERIFY_WORLD -- confirmed 2026-07-15 in both the legacy JSONL and the modern
        // .pkt (9/9 SMSG_SET_PROFICIENCY arrive pre-verify, 0 after), matching twinhead #13649
        // "item proficiency packet is sent at incorrect time". The 1.14 client isn't in world yet,
        // so it appears to drop them: items it can't use don't turn red and wearable armor shows a
        // spurious "Requires <ArmorType>" line. Hold each packet until the client is in world (the
        // delay queue flushes right after we forward login-verify) so it can actually apply it.
        //
        // The !IsInWorld gate is load-bearing: a MID-SESSION proficiency change (training a new
        // weapon type at a trainer) arrives AFTER login-verify, and there is no future login-verify
        // to flush a delayed copy against -- delaying it would strand it forever. Relogin is already
        // safe: the client-bound delay queue is intentionally not migrated across a WorldClient
        // recreate (#384), and proficiency is re-sent fresh on each login.
        //
        // VERIFIED 2026-07-15 (Kronos V, TooltipFix disabled, Mage + Rogue): delivered in world,
        // the 1.14 client DOES apply proficiency -- unusable items go red and the spurious
        // "Requires <ArmorType>" line disappears on wearable armor. The earlier "the client ignores
        // this packet" belief was an artifact of the pre-verify timing. NOTE: this only holds with
        // the forward-order flush in SendDelayedPacketsToClientOnOpcode -- SMSG_SET_PROFICIENCY is a
        // cumulative absolute mask (only the last, full one is correct), and the queue's old reverse
        // flush collapsed multi-proficiency classes to the smallest mask (a Rogue to Leather-only /
        // Sword1H-only, wrongly reddening cloth/thrown). Both are needed together.
        if (!GetSession().GameState.IsInWorld)
            SendPacketToClient(proficiency, Opcode.SMSG_LOGIN_VERIFY_WORLD);
        else
            SendPacketToClient(proficiency);
    }
    [PacketHandler(Opcode.SMSG_BUY_SUCCEEDED)]
    void HandleBuySucceeded(WorldPacket packet)
    {
        BuySucceeded buy = new BuySucceeded();
        buy.VendorGUID = packet.ReadGuid().To128(GetSession().GameState);
        buy.Slot = packet.ReadUInt32();
        buy.NewQuantity = packet.ReadInt32();
        buy.QuantityBought = packet.ReadUInt32();
        SendPacketToClient(buy);

        // Modern 1.14 client greys a sold-out vendor slot based on the Quantity
        // field in SMSG_VENDOR_INVENTORY, not on SMSG_BUY_SUCCEEDED's NewQuantity.
        // Buying the last stack leaves the icon visually available until the next
        // full list refresh. Proxy-issue a CMSG_LIST_INVENTORY so the server
        // resends the list with the correct Quantity (0) for the just-emptied
        // slot, and the modern client greys it without the player having to
        // close and reopen the vendor window.
        if (buy.NewQuantity == 0)
        {
            WorldPacket refresh = new WorldPacket(Opcode.CMSG_LIST_INVENTORY);
            refresh.WriteGuid(buy.VendorGUID.To64());
            SendPacketToServer(refresh);
            Log.Event("vendor.list.refresh", new
            {
                vendor_guid_low = buy.VendorGUID.GetCounter(),
                reason = "buy_succeeded_zero_remaining",
                slot = buy.Slot,
            });
        }
    }
    [PacketHandler(Opcode.SMSG_ITEM_PUSH_RESULT)]
    void HandleItemPushResult(WorldPacket packet)
    {
        ItemPushResult item = new ItemPushResult();
        item.PlayerGUID = packet.ReadGuid().To128(GetSession().GameState);
        bool fromNPC = packet.ReadUInt32() == 1;
        item.Created = packet.ReadUInt32() == 1;
        bool showInChat = packet.ReadUInt32() == 1;
        
        if (fromNPC && !item.Created)
        {
            item.DisplayText = ItemPushResult.DisplayType.Received;
            item.Pushed = true;
        }
        else if (!showInChat)
            item.DisplayText = ItemPushResult.DisplayType.Hidden;
        else
            item.DisplayText = ItemPushResult.DisplayType.Loot;

        item.Slot = packet.ReadUInt8();
        item.SlotInBag = packet.ReadInt32();
        item.Item.ItemID = packet.ReadUInt32();
        item.Item.RandomPropertiesSeed = packet.ReadUInt32();
        item.Item.RandomPropertiesID = packet.ReadUInt32();
        item.Quantity = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            item.QuantityInInventory = packet.ReadUInt32();
        else
        {
            //MIRASU - Vanilla legacy SMSG_ITEM_PUSH_RESULT doesn't carry running QuantityInInventory.
            //MIRASU   Original code read the count from the player's PLAYER_QUEST_LOG_x_3 cache, but
            //MIRASU   Kronos never refreshes that field on item pickups -- so QuantityInInventory was
            //MIRASU   always equal to the per-pickup delta (1), and the modern over-head quest toast
            //MIRASU   rendered "Item: 1/N" forever. Prefer the proxy's QuestItemObjectiveProgress
            //MIRASU   running total (kept in sync by HandleQuestUpdateAddItem, which fires before
            //MIRASU   SMSG_ITEM_PUSH_RESULT in Kronos's pickup burst). Fall back to legacy cache +
            //MIRASU   delta when the running total hasn't been populated yet (e.g. first pickup of
            //MIRASU   a quest whose template wasn't cached).
            uint quantityInInventory = item.Quantity;
            QuestObjective? objective = GameData.GetQuestObjectiveForItem(item.Item.ItemID);
            if (objective != null)
            {
                var key = (objective.QuestID, objective.StorageIndex);
                //MIRASU - if SMSG_QUEST_UPDATE_ADD_ITEM was buffered (template not cached at the
                //MIRASU   moment it arrived), the credit is sitting in PendingQuestItemCredits and
                //MIRASU   QuestItemObjectiveProgress reflects PRE-pickup state (or is empty). Sum
                //MIRASU   any matching pending deltas so quantityInInventory reflects the POST-
                //MIRASU   pickup count even when the credit is still buffered.
                uint pendingDeltaForThisItem = 0;
                lock (GetSession().GameState.PendingQuestItemCreditsLock)
                {
                    foreach (var pending in GetSession().GameState.PendingQuestItemCredits)
                    {
                        if (pending.ItemId == item.Item.ItemID)
                            pendingDeltaForThisItem += pending.Count;
                    }
                }

                if (GetSession().GameState.QuestItemObjectiveProgress.TryGetValue(key, out uint runningTotal))
                {
                    quantityInInventory = runningTotal + pendingDeltaForThisItem;
                }
                else
                {
                    var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
                    int questsCount = LegacyVersion.GetQuestLogSize();
                    for (int i = 0; i < questsCount; i++)
                    {
                        QuestLog? logEntry = ReadQuestLogEntry(i, null, updateFields!);
                        if (logEntry == null || logEntry.QuestID != objective.QuestID)
                            continue;
                        if (logEntry.ObjectiveProgress[objective.StorageIndex] != null)
                            quantityInInventory = item.Quantity + (uint)logEntry.ObjectiveProgress[objective.StorageIndex]!;
                        break;
                    }
                }
            }
            item.QuantityInInventory = quantityInInventory;
        }

        if (item.Slot == Enums.Classic.InventorySlots.Bag0 && item.SlotInBag >= 0 &&
            item.PlayerGUID == GetSession().GameState.CurrentPlayerGuid)
            item.ItemGUID = GetSession().GameState.GetInventorySlotItem(item.SlotInBag).To128(GetSession().GameState);
        else
            item.ItemGUID = WowGuid128.Empty;

        //MIRASU - if this is a quest item whose template wasn't cached yet (objective lookup failed
        //MIRASU   above) AND its credit is sitting in PendingQuestItemCredits, defer the inventory
        //MIRASU   packet. ReplayPendingQuestItemCredits will replay it after the credit is processed
        //MIRASU   (template cached, dict populated). Forwarding now would render the modern over-head
        //MIRASU   quest toast as "1/N" until the buffered credit catches up ~150ms later -- the
        //MIRASU   exact post-cold-start flicker the user sees.
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            QuestObjective? itemObjective = GameData.GetQuestObjectiveForItem(item.Item.ItemID);
            if (itemObjective == null)
            {
                bool isPendingQuestItem;
                lock (GetSession().GameState.PendingQuestItemCreditsLock)
                {
                    isPendingQuestItem = false;
                    foreach (var pending in GetSession().GameState.PendingQuestItemCredits)
                    {
                        if (pending.ItemId == item.Item.ItemID)
                        {
                            isPendingQuestItem = true;
                            break;
                        }
                    }
                }
                if (isPendingQuestItem)
                {
                    lock (GetSession().GameState.PendingItemPushResultsLock)
                        GetSession().GameState.PendingItemPushResults.Add(item);
                    Framework.Logging.Log.Event("item.push.deferred", new
                    {
                        item_id = item.Item.ItemID,
                        reason = "objective_template_not_cached_credit_pending",
                    });
                    return;
                }
            }
        }

        SendPacketToClient(item);
    }
    [PacketHandler(Opcode.SMSG_READ_ITEM_RESULT_OK)]
    void HandleReadItemResultOk(WorldPacket packet)
    {
        ReadItemResultOK read = new ReadItemResultOK();
        read.ItemGUID = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(read);
    }
    [PacketHandler(Opcode.SMSG_READ_ITEM_RESULT_FAILED)]
    void HandleReadItemResultFailed(WorldPacket packet)
    {
        ReadItemResultFailed read = new ReadItemResultFailed();
        read.ItemGUID = packet.ReadGuid().To128(GetSession().GameState);
        read.Subcode = 2;
        SendPacketToClient(read);
    }
    [PacketHandler(Opcode.SMSG_BUY_FAILED)]
    void HandleBuyFailed(WorldPacket packet)
    {
        BuyFailed fail = new BuyFailed();
        fail.VendorGUID = packet.ReadGuid().To128(GetSession().GameState);
        fail.Slot = packet.ReadUInt32();
        byte rawReason = packet.ReadUInt8();
        fail.Reason = (BuyResult)rawReason;
        SendPacketToClient(fail);

        Log.Event("vendor.buy.failed", new
        {
            vendor_guid_low = fail.VendorGUID.GetCounter(),
            slot = fail.Slot,
            reason_raw = rawReason,
            reason_name = fail.Reason.ToString(),
        });

        // The modern 1.14 client renders the red "currently sold out" tooltip
        // text from this reason code, but it does NOT grey the icon — that's
        // driven by SMSG_VENDOR_INVENTORY's Quantity field, which the legacy
        // server doesn't proactively resend on a failed buy. Tester JSONL
        // 20260513-043134 shows 3 consecutive CMSG_BUY_ITEM → SMSG_BUY_FAILED
        // pairs with zero intervening SMSG_VENDOR_INVENTORY refreshes.
        //
        // Refresh on any reason that implies the vendor's stock or slot table
        // is now wrong relative to what the modern client thinks it knows:
        // CantFindItem (0), ItemAlreadySold (1), ItemSoldOut (7). Skip the
        // reasons where stock is fine and only the player state is the
        // problem (NotEnoughtMoney/SellerDontLikeYou/DistanceTooFar/
        // CantCarryMore/RankRequire/ReputationRequire) — no point burning a
        // round-trip there.
        if (fail.Reason == BuyResult.ItemSoldOut ||
            fail.Reason == BuyResult.ItemAlreadySold ||
            fail.Reason == BuyResult.CantFindItem)
        {
            WorldPacket refresh = new WorldPacket(Opcode.CMSG_LIST_INVENTORY);
            refresh.WriteGuid(fail.VendorGUID.To64());
            SendPacketToServer(refresh);
            Log.Event("vendor.list.refresh", new
            {
                vendor_guid_low = fail.VendorGUID.GetCounter(),
                reason = "buy_failed",
                buy_result = fail.Reason.ToString(),
                slot = fail.Slot,
            });
        }
    }
    [PacketHandler(Opcode.SMSG_INVENTORY_CHANGE_FAILURE, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleInventoryChangeFailureVanilla(WorldPacket packet)
    {
        InventoryChangeFailure failure = new();
        failure.BagResult = LegacyVersion.ConvertInventoryResult(packet.ReadUInt8());
        if (failure.BagResult == InventoryResult.Ok)
            return;

        switch (failure.BagResult)
        {
            case InventoryResult.CantEquipLevel:
                failure.Level = packet.ReadInt32();
                break;
        }

        failure.Item[0] = packet.ReadGuid().To128(GetSession().GameState);
        failure.Item[1] = packet.ReadGuid().To128(GetSession().GameState);
        failure.ContainerBSlot = packet.ReadUInt8();

        // JimsProxy (#442): the fallback pairing below must see whether the WIRE carried an empty
        // GUID — the move-backfill next rewrites failure.Item[0], and gating on the rewritten
        // value would silently disable the fallback for 2 s after every forwarded bag move.
        bool wireItem0Empty = failure.Item[0].IsEmpty();

        // JimsProxy: Kronos sends invalid-slot rejections (e.g. the modern client's phantom
        // keyring slots 13-32 that the server lacks) with empty item GUIDs, so the client
        // can't unlock the item the player picked up and it stays stuck until relog. Backfill
        // the GUIDs from the move we just forwarded so the client releases the item.
        if (failure.Item[0].IsEmpty() && failure.Item[1].IsEmpty())
        {
            var state = GetSession().GameState;
            if (Environment.TickCount - state.LastMoveItemsTickMs < 2000)
            {
                failure.Item[0] = state.LastMoveItem0;
                failure.Item[1] = state.LastMoveItem1;
            }
        }

        SendPacketToClient(failure);

        // Check if item use cast failed (queue-based)
        if (GetSession().GameState.TryDequeueItemCast(failure.Item[0], out var pendingCast))
        {
            GetSession().InstanceSocket.SendCastRequestFailed(pendingCast!, false);
            Log.Event("cast.item_use_rejected_by_inventory", new
            {
                result = (uint)failure.BagResult,
                dequeued_via = "guid",
                evicted_spell_id = pendingCast!.SpellId,
            });
        }
        // JimsProxy (#442): Kronos also rejects USE_ITEM itself this way — with EMPTY item GUIDs
        // (observed live 2026-07-29: a duplicate use straddling its predecessor's SPELL_GO,
        // rejected with result 23/ItemNotFound, both GUIDs zero). The GUID dequeue above can't
        // match those, and the orphaned entry then jams HasForwardedPendingCast (every on-GCD
        // press silently parked until relog/map change) and keeps that item unusable via
        // HasInFlightNormalCastForSpell. Pair the anonymous rejection with the oldest unresolved
        // item-use entry instead and release its button silently. See
        // TryDequeueOldestUnstartedItemCast for the FIFO rationale and the two-items edge.
        // Gated on the pre-backfill wire GUID (wireItem0Empty), NOT failure.Item[0] — see above.
        else if (wireItem0Empty &&
                 GetSession().GameState.TryDequeueOldestUnstartedItemCast(out var orphanedItemCast))
        {
            GetSession().InstanceSocket.SendCastRequestFailed(orphanedItemCast!, false, SpellCastResultClassic.DontReport);
            Log.Event("cast.item_use_rejected_by_inventory", new
            {
                result = (uint)failure.BagResult,
                dequeued_via = "fifo_fallback",
                evicted_spell_id = orphanedItemCast!.SpellId,
                evicted_item_guid = orphanedItemCast.ItemGUID.ToString(),
            });
        }
    }
    [PacketHandler(Opcode.SMSG_INVENTORY_CHANGE_FAILURE, ClientVersionBuild.V2_0_1_6180)]
    void HandleInventoryChangeFailure(WorldPacket packet)
    {
        InventoryChangeFailure failure = new();
        failure.BagResult = LegacyVersion.ConvertInventoryResult(packet.ReadUInt8());
        if (failure.BagResult == InventoryResult.Ok)
            return;

        failure.Item[0] = packet.ReadGuid().To128(GetSession().GameState);
        failure.Item[1] = packet.ReadGuid().To128(GetSession().GameState);
        failure.ContainerBSlot = packet.ReadUInt8();

        switch (failure.BagResult)
        {
            case InventoryResult.CantEquipLevel:
            case InventoryResult.PurchaseLevelTooLow:
                failure.Level = packet.ReadInt32();
                break;
            case InventoryResult.EventAutoEquipBindConfirm:
                failure.SrcContainer = packet.ReadGuid().To128(GetSession().GameState);
                failure.SrcSlot = packet.ReadInt32();
                failure.DstContainer = packet.ReadGuid().To128(GetSession().GameState);
                break;
            case InventoryResult.ItemMaxLimitCategoryCountExceeded:
            case InventoryResult.ItemMaxLimitCategorySocketedExceeded:
            case InventoryResult.ItemMaxLimitCategoryEquippedExceeded:
                failure.LimitCategory = packet.ReadInt32();
                break;
        }
        SendPacketToClient(failure);

        // Check if item use cast failed (queue-based)
        if (GetSession().GameState.TryDequeueItemCast(failure.Item[0], out var pendingCast))
        {
            GetSession().InstanceSocket.SendCastRequestFailed(pendingCast!, false);
            Log.Event("cast.item_use_rejected_by_inventory", new
            {
                result = (uint)failure.BagResult,
                dequeued_via = "guid",
                evicted_spell_id = pendingCast!.SpellId,
            });
        }
        // JimsProxy (#442): same empty-GUID rejection fallback as the vanilla handler above.
        else if (failure.Item[0].IsEmpty() &&
                 GetSession().GameState.TryDequeueOldestUnstartedItemCast(out var orphanedItemCast))
        {
            GetSession().InstanceSocket.SendCastRequestFailed(orphanedItemCast!, false, SpellCastResultClassic.DontReport);
            Log.Event("cast.item_use_rejected_by_inventory", new
            {
                result = (uint)failure.BagResult,
                dequeued_via = "fifo_fallback",
                evicted_spell_id = orphanedItemCast!.SpellId,
                evicted_item_guid = orphanedItemCast.ItemGUID.ToString(),
            });
        }
    }
    [PacketHandler(Opcode.SMSG_DURABILITY_DAMAGE_DEATH)]
    void HandleDurabilityDamageDeath(WorldPacket packet)
    {
        DurabilityDamageDeath death = new DurabilityDamageDeath();
        death.Percent = 10;
        SendPacketToClient(death);
    }
    [PacketHandler(Opcode.SMSG_ITEM_COOLDOWN)]
    void HandleItemCooldown(WorldPacket packet)
    {
        ItemCooldown item = new ItemCooldown();
        item.ItemGuid = packet.ReadGuid().To128(GetSession().GameState);
        item.SpellID = packet.ReadUInt32();
        item.Cooldown = 30000;
        SendPacketToClient(item);
    }
    [PacketHandler(Opcode.SMSG_SELL_RESPONSE)]
    void HandleSellResponse(WorldPacket packet)
    {
        SellResponse sell = new SellResponse();
        sell.VendorGUID = packet.ReadGuid().To128(GetSession().GameState);
        sell.ItemGUID = packet.ReadGuid().To128(GetSession().GameState);
        sell.Reason = packet.ReadUInt8();
        SendPacketToClient(sell);
    }
    // (temp-enchant-0s-after-relogin): legacy Prop slots sit at different indices than
    // the modern layout (vanilla 3.., TBC 6.., WotLK 7.. → modern 8..); every slot
    // below the Prop block (Perm/Temp/Sock/Bonus/Prismatic) shares its index. Timed
    // enchants live in Temp=1, so the old raw passthrough happened to work for them.
    internal static uint TranslateEnchantmentSlotToModern(uint legacySlot)
    {
        int firstProp, max;
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            firstProp = Enums.Vanilla.EnchantmentSlot.Prop0;
            max = Enums.Vanilla.EnchantmentSlot.Max;
        }
        else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            firstProp = Enums.TBC.EnchantmentSlot.Prop0;
            max = Enums.TBC.EnchantmentSlot.Max;
        }
        else
        {
            firstProp = Enums.WotLK.EnchantmentSlot.Prop0;
            max = Enums.WotLK.EnchantmentSlot.Max;
        }

        if (legacySlot >= firstProp && legacySlot < max)
            return (uint)(Enums.Classic.EnchantmentSlot.Prop0 + ((int)legacySlot - firstProp));
        return legacySlot;
    }

    [PacketHandler(Opcode.SMSG_ITEM_ENCHANT_TIME_UPDATE)]
    void HandleItemEnchantTimeUpdate(WorldPacket packet)
    {
        ItemEnchantTimeUpdate enchant = new ItemEnchantTimeUpdate();
        enchant.ItemGuid = packet.ReadGuid().To128(GetSession().GameState);
        uint legacySlot = packet.ReadUInt32();
        enchant.Slot = TranslateEnchantmentSlotToModern(legacySlot);
        enchant.DurationLeft = packet.ReadUInt32();
        enchant.OwnerGuid = packet.ReadGuid().To128(GetSession().GameState);

        // (temp-enchant-0s-after-relogin): Kronos can push this BEFORE the item's
        // create block — it is the only carrier of the enchant's remaining time the
        // client's weapon-buff timer reads (the create's duration field is a stale
        // save-time snapshot no client generation uses for the countdown), and the
        // client discards it for guids it has not constructed, leaving the buff
        // flashing a permanent "0s". Stash pre-create pushes only; the item-create
        // translation re-emits them to the client after the create is forwarded
        // ("must be after add to map", per every mangos-lineage core). Forward when
        // the item already exists client-side — the forward path never needs the
        // stash, and storing there would leave a never-consumed entry per enchanted
        // item until logout.
        if (GetSession().GameState.GetCachedObjectFieldsLegacy(enchant.ItemGuid) == null)
        {
            GetSession().GameState.StorePendingItemEnchantDuration(enchant.ItemGuid, legacySlot, enchant.DurationLeft, Environment.TickCount);
            if (Framework.Settings.DebugOutput)
            {
                Log.Event("enchant.duration.stashed_precreate", new
                {
                    item_guid = enchant.ItemGuid.ToString(),
                    legacy_slot = legacySlot,
                    duration_seconds = enchant.DurationLeft,
                });
            }
            return;
        }

        SendPacketToClient(enchant);
    }

    [PacketHandler(Opcode.SMSG_ENCHANTMENT_LOG)]
    void HandleEnchantmentLog(WorldPacket packet)
    {
        EnchantmentLog enchantment = new EnchantmentLog();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            enchantment.Owner = packet.ReadPackedGuid().To128(GetSession().GameState);
            enchantment.Caster = packet.ReadPackedGuid().To128(GetSession().GameState);
        }
        else
        {
            enchantment.Owner = packet.ReadGuid().To128(GetSession().GameState);
            enchantment.Caster = packet.ReadGuid().To128(GetSession().GameState);
        }
        enchantment.ItemID = packet.ReadInt32();
        var session = GetSession().GameState;

        for (int i = 0; i < 23; i++)
        {
            if (session.GetItemId(session.GetInventorySlotItem(i).To128(session)).Equals((uint)enchantment.ItemID))
            {
                enchantment.ItemGUID = session.GetInventorySlotItem(i).To128(session);
                break;
            }
        }
        if (enchantment.ItemGUID == default)
            return;

        enchantment.Enchantment = packet.ReadInt32();
        SendPacketToClient(enchantment);
    }
}
