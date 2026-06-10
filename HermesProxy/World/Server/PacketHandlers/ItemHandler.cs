using Framework.Constants;
using System;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_BUY_ITEM)]
    void HandleBuyItem(BuyItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BUY_ITEM);
        packet.WriteGuid(item.VendorGUID.To64());
        packet.WriteUInt32(item.Item.ItemID);
        uint quantity = item.Quantity / GetSession().GameState.GetItemBuyCount(item.Item.ItemID);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
        {
            packet.WriteUInt32(item.Slot);
            packet.WriteUInt32(quantity);
        }
        else
            packet.WriteUInt8((byte)quantity);
        packet.WriteUInt8((byte)item.BagSlot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SELL_ITEM)]
    void HandleSellItem(SellItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SELL_ITEM);
        packet.WriteGuid(item.VendorGUID.To64());
        packet.WriteGuid(item.ItemGUID.To64());
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192)) // not sure when this was changed exactly
            packet.WriteUInt32(item.Amount);
        else
            packet.WriteUInt8((byte)item.Amount);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SPLIT_ITEM)]
    void HandleSplitItem(SplitItem item)
    {
        RememberMoveItems(item.FromPackSlot, item.FromSlot, item.ToPackSlot, item.ToSlot);
        if (BlockNonKeySplitIntoKeyring(item.ToPackSlot, item.ToSlot))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SPLIT_ITEM);
        byte containerSlot1 = item.FromPackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.FromPackSlot) : item.FromPackSlot;
        byte slot1 = item.FromPackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.FromSlot) : item.FromSlot;
        byte containerSlot2 = item.ToPackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ToPackSlot) : item.ToPackSlot;
        byte slot2 = item.ToPackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ToSlot) : item.ToSlot;
        packet.WriteUInt8(containerSlot1);
        packet.WriteUInt8(slot1);
        packet.WriteUInt8(containerSlot2);
        packet.WriteUInt8(slot2);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WriteInt32(item.Quantity);
        else
            packet.WriteUInt8((byte)item.Quantity);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SWAP_INV_ITEM)]
    void HandleSwapInvItem(SwapInvItem item)
    {
        RememberMoveItems(Enums.Classic.InventorySlots.Bag0, item.Slot1, Enums.Classic.InventorySlots.Bag0, item.Slot2);
        if (BlockNonKeyIntoKeyring(Enums.Classic.InventorySlots.Bag0, item.Slot1, Enums.Classic.InventorySlots.Bag0, item.Slot2))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SWAP_INV_ITEM);
        byte slot1 = ModernVersion.AdjustInventorySlot(item.Slot1);
        byte slot2 = ModernVersion.AdjustInventorySlot(item.Slot2);
        packet.WriteUInt8(slot1);
        packet.WriteUInt8(slot2);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SWAP_ITEM)]
    void HandleSwapItem(SwapItem item)
    {
        RememberMoveItems(item.ContainerSlotA, item.SlotA, item.ContainerSlotB, item.SlotB);
        if (BlockNonKeyIntoKeyring(item.ContainerSlotA, item.SlotA, item.ContainerSlotB, item.SlotB))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SWAP_ITEM);
        byte containerSlotB = item.ContainerSlotB != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ContainerSlotB) : item.ContainerSlotB;
        byte slotB = item.ContainerSlotB == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.SlotB) : item.SlotB;
        byte containerSlotA = item.ContainerSlotA != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ContainerSlotA) : item.ContainerSlotA;
        byte slotA = item.ContainerSlotA == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.SlotA) : item.SlotA;
        packet.WriteUInt8(containerSlotB);
        packet.WriteUInt8(slotB);
        packet.WriteUInt8(containerSlotA);
        packet.WriteUInt8(slotA);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_DESTROY_ITEM)]
    void HandleDestroyItem(DestroyItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_DESTROY_ITEM);
        byte containerSlot = item.ContainerId != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ContainerId) : item.ContainerId;
        byte slot = item.ContainerId == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.SlotNum) : item.SlotNum;
        packet.WriteUInt8(containerSlot);
        packet.WriteUInt8(slot);
        packet.WriteUInt32(item.Count);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUTO_EQUIP_ITEM)]
    [PacketHandler(Opcode.CMSG_AUTOSTORE_BANK_ITEM)]
    [PacketHandler(Opcode.CMSG_AUTOBANK_ITEM)]
    void HandleAutoEquipItem(AutoEquipItem item)
    {
        WorldPacket packet = new WorldPacket(item.GetUniversalOpcode());
        byte containerSlot = item.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.PackSlot) : item.PackSlot;
        byte slot = item.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.Slot) : item.Slot;
        packet.WriteUInt8(containerSlot);
        packet.WriteUInt8(slot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUTO_EQUIP_ITEM_SLOT)]
    void HandleAutoEquipItemSlot(AutoEquipItemSlot item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUTO_EQUIP_ITEM_SLOT);
        packet.WriteGuid(item.Item.To64());
        byte slot = ModernVersion.AdjustInventorySlot(item.ItemDstSlot);
        packet.WriteUInt8(slot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_READ_ITEM)]
    void HandleReadItem(ReadItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_READ_ITEM);
        byte containerSlot = item.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.PackSlot) : item.PackSlot;
        byte slot = item.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.Slot) : item.Slot;
        packet.WriteUInt8(containerSlot);
        packet.WriteUInt8(slot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_BUY_BACK_ITEM)]
    void HandleBuyBackItem(BuyBackItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BUY_BACK_ITEM);
        packet.WriteGuid(item.VendorGUID.To64());
        byte slot = ModernVersion.AdjustInventorySlot((byte)item.Slot);
        packet.WriteUInt32(slot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_REPAIR_ITEM)]
    void HandleRepairItem(RepairItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_REPAIR_ITEM);
        packet.WriteGuid(item.VendorGUID.To64());
        packet.WriteGuid(item.ItemGUID.To64());
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.WriteBool(item.UseGuildBank);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SOCKET_GEMS)]
    void HandleSocketGems(SocketGems gems)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SOCKET_GEMS);
        packet.WriteGuid(gems.ItemGuid.To64());
        for (int i = 0; i < ItemConst.MaxGemSockets; ++i)
            packet.WriteGuid(gems.Gems[i].To64());
        SendPacketToServer(packet);

        // Packet does not exist in old clients.
        SocketGemsSuccess success = new SocketGemsSuccess();
        success.ItemGuid = gems.ItemGuid;
        SendPacket(success);
    }

    [PacketHandler(Opcode.CMSG_OPEN_ITEM)]
    void HandleOpenItem(OpenItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_OPEN_ITEM);
        byte containerSlot = item.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.PackSlot) : item.PackSlot;
        byte slot = item.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.Slot) : item.Slot;
        packet.WriteUInt8(containerSlot);
        packet.WriteUInt8(slot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SET_AMMO)]
    void HandleSetAmmo(SetAmmo ammo)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_AMMO);
        packet.WriteUInt32(ammo.ItemId);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_CANCEL_TEMP_ENCHANTMENT)]
    void HandleCancelTempEnchantment(CancelTempEnchantment cancel)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            return;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_TEMP_ENCHANTMENT);
        packet.WriteUInt32(cancel.EnchantmentSlot);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_WRAP_ITEM)]
    void HandleWrapItem(WrapItem item)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_WRAP_ITEM);
        byte giftBag = item.GiftBag != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.GiftBag) : item.GiftBag;
        byte giftSlot = item.GiftBag == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.GiftSlot) : item.GiftSlot;
        byte itemBag = item.ItemBag != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ItemBag) : item.ItemBag;
        byte itemSlot = item.ItemBag == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(item.ItemSlot) : item.ItemSlot;
        packet.WriteUInt8(giftBag);
        packet.WriteUInt8(giftSlot);
        packet.WriteUInt8(itemBag);
        packet.WriteUInt8(itemSlot);
        SendPacketToServer(packet);
    }

    // JimsProxy: remember the items at both endpoints of an item move (looked up from the
    // cached player inventory at the legacy slots we're about to forward), so the failure
    // handler can repair an empty-GUID InventoryChangeFailure and unlock the source item.
    // Kronos sends empty item GUIDs for invalid-slot rejections (e.g. the modern client's
    // phantom keyring slots 13-32 that the server lacks), which otherwise leaves the picked-
    // up item locked until relog.
    void RememberMoveItems(byte containerA, byte slotA, byte containerB, byte slotB)
    {
        var state = GetSession().GameState;
        byte lcA = containerA != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(containerA) : containerA;
        byte lsA = containerA == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(slotA) : slotA;
        byte lcB = containerB != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(containerB) : containerB;
        byte lsB = containerB == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(slotB) : slotB;
        state.LastMoveItem0 = state.GetInventorySlotItem(lcA, lsA).To128(state);
        state.LastMoveItem1 = state.GetInventorySlotItem(lcB, lsB).To128(state);
        state.LastMoveItemsTickMs = Environment.TickCount;
    }

    // JimsProxy: Kronos only enforces keys-only on its real (level-gated) keyring slots; the
    // modern client's keyring slots past that size — and Bagnon, which treats the keyring as a
    // 12/32-slot bag — map to regular vanilla slots that accept ANY item, so non-keys leak in and
    // orphan there. Block any move dropping a known non-key into a client keyring slot (94-125) and
    // bounce a clean WrongBagType with the item's GUID, so the client unlocks it (no stuck). Unknown
    // items (template not loaded) pass through so we never false-block a real key — Kronos still
    // enforces keys-only on the real slots in that rare case. Returns true if the move was blocked.
    static bool IsKeyringSlot(byte container, byte slot)
        => container == Enums.Classic.InventorySlots.Bag0
           && slot >= Enums.Classic.InventorySlots.KeyringStart
           && slot < Enums.Classic.InventorySlots.KeyringEnd;

    bool IsKnownNonKey(WowGuid128 item)
    {
        if (item.IsEmpty())
            return false;
        uint itemId = GetSession().GameState.GetItemId(item);
        if (itemId == 0)
            return false;
        var template = GameData.GetItemTemplate(itemId);
        return template != null && template.Class != 13; // 13 = ItemClass.Key
    }

    // Bounce a blocked keyring move with a clean WrongBagType. Unlocks BOTH move endpoints so a
    // swap never leaves the dragged item OR the swapped item locked (stuck).
    void SendKeyringBounce()
    {
        var state = GetSession().GameState;
        InventoryChangeFailure failure = new();
        failure.BagResult = InventoryResult.WrongBagType;
        failure.Item[0] = state.LastMoveItem0;
        failure.Item[1] = state.LastMoveItem1;
        SendPacket(failure);
    }

    // Swap/move: the item from A lands in B (LastMoveItem0) and the item from B lands in A
    // (LastMoveItem1). Block if either lands a known non-key in a client keyring slot.
    bool BlockNonKeyIntoKeyring(byte containerA, byte slotA, byte containerB, byte slotB)
    {
        var state = GetSession().GameState;
        if ((IsKeyringSlot(containerB, slotB) && IsKnownNonKey(state.LastMoveItem0))
            || (IsKeyringSlot(containerA, slotA) && IsKnownNonKey(state.LastMoveItem1)))
        {
            SendKeyringBounce();
            return true;
        }
        return false;
    }

    // Split: only the destination receives the split portion (no swap-back), so check the dest only.
    bool BlockNonKeySplitIntoKeyring(byte toContainer, byte toSlot)
    {
        if (IsKeyringSlot(toContainer, toSlot) && IsKnownNonKey(GetSession().GameState.LastMoveItem0))
        {
            SendKeyringBounce();
            return true;
        }
        return false;
    }
}
