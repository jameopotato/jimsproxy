using Framework;
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
                if (GetSession().GameState.QuestItemObjectiveProgress.TryGetValue(key, out uint runningTotal))
                {
                    quantityInInventory = runningTotal;
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
        fail.Reason = (BuyResult)packet.ReadUInt8();
        SendPacketToClient(fail);
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

        SendPacketToClient(failure);

        // Check if item use cast failed (queue-based)
        if (GetSession().GameState.TryDequeueItemCast(failure.Item[0], out var pendingCast))
        {
            GetSession().InstanceSocket.SendCastRequestFailed(pendingCast!, false);
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
    [PacketHandler(Opcode.SMSG_ITEM_ENCHANT_TIME_UPDATE)]
    void HandleItemEnchantTimeUpdate(WorldPacket packet)
    {
        ItemEnchantTimeUpdate enchant = new ItemEnchantTimeUpdate();
        enchant.ItemGuid = packet.ReadGuid().To128(GetSession().GameState);
        enchant.Slot = packet.ReadUInt32();
        enchant.DurationLeft = packet.ReadUInt32();
        enchant.OwnerGuid = packet.ReadGuid().To128(GetSession().GameState);
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
