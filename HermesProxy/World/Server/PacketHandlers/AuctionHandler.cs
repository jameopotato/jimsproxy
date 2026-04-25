using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Linq;
using System.Threading;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Track the last search time to mimic the Vanilla 6-second cooldown
    private DateTime _lastSearchTime = DateTime.MinValue;

    [PacketHandler(Opcode.CMSG_AUCTION_HELLO_REQUEST)]
    void HandleAuctionHelloRequest(InteractWithNPC interact)
    {
        WorldPacket packet = new WorldPacket(Opcode.MSG_AUCTION_HELLO);
        packet.WriteGuid(interact.CreatureGUID.To64());
        SendPacketToServer(packet);
    }

    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_AUCTION_LIST_BIDDED_ITEMS)]
    void HandleAuctionListBidderItems(AuctionListBidderItems auction)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_LIST_BIDDED_ITEMS);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.Offset);
        packet.WriteInt32(auction.AuctionItemIDs.Count);
        foreach (var itemId in auction.AuctionItemIDs)
            packet.WriteUInt32(itemId);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS)]
    void HandleAuctionListOwnerItems(AuctionListOwnerItems auction)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.Offset);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUCTION_LIST_ITEMS)]
    void HandleAuctionListItems(AuctionListItems auction)
    {
        

        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_LIST_ITEMS);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.Offset);
        packet.WriteCString(auction.Name);
        packet.WriteUInt8(auction.MinLevel);
        packet.WriteUInt8(auction.MaxLevel);

        if (auction.ClassFilters.Count > 0)
        {
            if (auction.ClassFilters[0].SubClassFilters.Count == 1)
            {
                packet.WriteInt32(-1); // Force "Any Slot" so TrinityCore doesn't get confused!
                packet.WriteInt32(auction.ClassFilters[0].ItemClass);
                packet.WriteInt32(auction.ClassFilters[0].SubClassFilters[0].ItemSubclass);
            }
            else
            {
                packet.WriteInt32(-1); // inventorySlotId
                packet.WriteInt32(auction.ClassFilters[0].ItemClass);
                packet.WriteInt32(-1); // auctionSubCategory
            }
        }
        else
        {
            packet.WriteInt32(-1); // inventorySlotId
            packet.WriteInt32(-1); // auctionMainCategory
            packet.WriteInt32(-1); // auctionSubCategory
        }

        packet.WriteInt32(auction.Quality);
        packet.WriteBool(auction.OnlyUsable);

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteBool(auction.ExactMatch);
            packet.WriteUInt8((byte)auction.Sorts.Count);

            foreach (var sort in auction.Sorts)
            {
                packet.WriteUInt8(sort.Type);
                packet.WriteUInt8(sort.Direction);
            }
        }

        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUCTION_SELL_ITEM)]
        void HandleAuctionSellItem(AuctionSellItem auction)
        {
        
        
        
        uint expireTime = auction.ExpireTime;

            // auction durations were increased in tbc
            // ... (the rest of the code continues down)

            // server ignores packet if you send wrong duration
            if (LegacyVersion.ExpansionVersion <= 1 &&
            ModernVersion.ExpansionVersion > 1)
        {
            switch (expireTime)
            {
                case 1 * 12 * 60: // 720
                {
                    expireTime = 1 * 2 * 60; // 120
                    break;
                }
                case 2 * 12 * 60: // 1440
                {
                    expireTime = 4 * 2 * 60; // 480
                    break;
                }
                case 4 * 12 * 60: // 2880
                {
                    expireTime = 12 * 2 * 60; // 1440
                    break;
                }
            }
        }
        else if (LegacyVersion.ExpansionVersion > 1 &&
                 ModernVersion.ExpansionVersion <= 1)
        {
            switch (expireTime)
            {
                case 1 * 2 * 60:
                {
                    expireTime = 1 * 12 * 60;
                    break;
                }
                case 4 * 2 * 60:
                {
                    expireTime = 2 * 12 * 60;
                    break;
                }
                case 12 * 2 * 60:
                {
                    expireTime = 4 * 12 * 60;
                    break;
                }
            }
        }

        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_2_2a_10505))
        {
            var gameState = GetSession().GameState;

            // Pre-3.2.2a servers have no quantity field — they auction the entire item.
            // If the player wants a partial stack, split UseCount to a temp slot and
            // auction that instead. The original item keeps the remainder, which is
            // what the modern client expects (original stack shrinks by UseCount).
            bool needsSplit = auction.Items.Any(i => i.UseCount > 0 &&
                i.UseCount < gameState.GetItemStackCount(i.Guid));

            (byte containerSlot, byte slot)? splitSlot = null;
            if (needsSplit)
            {
                splitSlot = gameState.FindEmptyInventorySlot();
                if (splitSlot == null)
                {
                    Log.Print(LogType.Error,
                        "AuctionSellItem: Cannot split stack — no empty bag slot");
                }
            }

            foreach (var item in auction.Items)
            {
                WowGuid64 auctionItemGuid = item.Guid.To64();

                if (item.UseCount > 0 && splitSlot != null)
                {
                    uint currentStackCount = gameState.GetItemStackCount(item.Guid);

                    if (item.UseCount < currentStackCount)
                    {
                        var itemLocation = gameState.FindItemInInventory(item.Guid.To64());

                        if (itemLocation == null)
                        {
                            Log.Print(LogType.Error,
                                "AuctionSellItem: Cannot split stack — item not found in inventory");
                            continue;
                        }

                        // Split the desired quantity to the temp slot
                        WorldPacket splitPacket = new WorldPacket(Opcode.CMSG_SPLIT_ITEM);
                        splitPacket.WriteUInt8(itemLocation.Value.containerSlot);
                        splitPacket.WriteUInt8(itemLocation.Value.slot);
                        splitPacket.WriteUInt8(splitSlot.Value.containerSlot);
                        splitPacket.WriteUInt8(splitSlot.Value.slot);
                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                            splitPacket.WriteInt32((int)item.UseCount);
                        else
                            splitPacket.WriteUInt8((byte)item.UseCount);
                        SendPacketToServer(splitPacket);

                        Thread.Sleep(500);

                        // Read the new item's GUID from the destination slot
                        auctionItemGuid = gameState.GetInventorySlotItem(
                            splitSlot.Value.containerSlot, splitSlot.Value.slot);

                        if (auctionItemGuid == WowGuid64.Empty)
                        {
                            Log.Print(LogType.Error,
                                "AuctionSellItem: Split item not found in destination slot");
                            continue;
                        }
                    }
                }

                // --- BULK SELL LOOP ---
                // Loop through every single item the 1.14 client wants to sell in this batch
                foreach (var itemToSell in auction.Items)
                {
                    WorldPacket legacyPacket = new WorldPacket(Opcode.CMSG_AUCTION_SELL_ITEM);

                    // 1. Auctioneer GUID (8 bytes)
                    legacyPacket.WriteGuid(auction.Auctioneer.To64());

                    // 2. This specific Item's GUID (8 bytes)
                    legacyPacket.WriteGuid(itemToSell.Guid.To64());

                    // 3. Bid Price (4 bytes) - Downcast modern 64-bit to Vanilla 32-bit
                    legacyPacket.WriteUInt32((uint)auction.MinBid);

                    // 4. Buyout Price (4 bytes) - Downcast modern 64-bit to Vanilla 32-bit
                    legacyPacket.WriteUInt32((uint)auction.BuyoutPrice);

                    // 5. Translated Duration (4 bytes)
                    legacyPacket.WriteUInt32(expireTime);

                    // Send this single item to TrinityCore (28 bytes)
                    SendPacketToServer(legacyPacket);

                    // --- ANTI-SPAM DELAY ---
                    // Pause the proxy thread for 50 milliseconds to let the server process
                    System.Threading.Thread.Sleep(50);
                }

                return;
            }
        }
        else
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_SELL_ITEM);
            packet.WriteGuid(auction.Auctioneer.To64());
            packet.WriteInt32(auction.Items.Count);
            foreach (var item in auction.Items)
            {
                packet.WriteGuid(item.Guid.To64());
                packet.WriteUInt32(item.UseCount);
            }
            packet.WriteUInt32((uint)auction.MinBid);
            packet.WriteUInt32((uint)auction.BuyoutPrice);
            packet.WriteUInt32(expireTime);
            SendPacketToServer(packet);
        }
    }

    [PacketHandler(Opcode.CMSG_AUCTION_REMOVE_ITEM)]
    void HandleAuctionRemoveItem(AuctionRemoveItem auction)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_REMOVE_ITEM);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.AuctionID);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_AUCTION_PLACE_BID)]
    void HandleAuctionPlaceBId(AuctionPlaceBid auction)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_PLACE_BID);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.AuctionID);
        packet.WriteUInt32((uint)auction.BidAmount);
        SendPacketToServer(packet);
    }
}
