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
    // Track the last search time to mimic the Vanilla 6-second cooldown. Kronos enforces
    // its own per-session cooldown server-side and will drop the WorldClient if too many
    // rapid queries arrive (observed 5.7s spacing causing a kick). Use the canonical 6s
    // window so we never trip Kronos's anti-flood.
    private const double AuctionSearchCooldownSeconds = 6.0;
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
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalSeconds < AuctionSearchCooldownSeconds)
            return;
        _lastSearchTime = now;

        // Kronos has its own per-session search cooldown (canonical vanilla 6s) and
        // silently drops queries that arrive too quickly — kicking the WorldClient if
        // we keep hammering — so we can only ever send ONE CMSG_AUCTION_LIST_ITEMS per
        // click. Translation rules for the legacy triple
        // (inventorySlot, itemMainCategory, itemSubCategory):
        //   - Armor sub-slot buttons (Head, Shoulders, Legs, Feet, Hands, Wrists, Waist,
        //     Trinket, Cloak, Tabard, Shirt) — modern client sends a single-bit
        //     InvTypeMask. We forward that bit as the legacy INVTYPE so the server
        //     narrows correctly.
        //   - Multi-bit slots (Chest = CHEST + ROBE, MainHand = WEAPON + 2HWEAPON +
        //     WEAPONMAINHAND, OffHand, Ranged) — legacy server only takes one INVTYPE
        //     and Kronos's throttle rules out splitting into multiple queries, so we
        //     fall back to slot = -1 and accept that the result is broader than the
        //     filter button suggests.
        //   - Weapons categories — modern client sets InvTypeMask = 0xFFFFFFFF (any
        //     slot), which trips the multi-bit fallback to -1; that's intentional and
        //     matches the existing working behavior because the weapon ItemSubclass
        //     (1H Sword, 2H Axe, etc.) is already specific enough on its own.
        int legacyMainCategory = -1;
        int legacySubCategory = -1;
        int legacyInventorySlot = -1;
        uint unionInvTypeMask = 0;

        if (auction.ClassFilters.Count > 0)
        {
            var firstClass = auction.ClassFilters[0];
            legacyMainCategory = firstClass.ItemClass;

            if (firstClass.SubClassFilters.Count > 0)
            {
                int commonSubclass = firstClass.SubClassFilters[0].ItemSubclass;
                foreach (var sub in firstClass.SubClassFilters)
                {
                    if (sub.ItemSubclass != commonSubclass)
                        commonSubclass = -1;
                    unionInvTypeMask |= sub.InvTypeMask;
                }
                legacySubCategory = commonSubclass;

                // Single-bit mask -> use it as the legacy INVTYPE for proper slot
                // narrowing. Multi-bit (or all-bits) -> leave -1 (any slot) by default.
                if (unionInvTypeMask != 0 && (unionInvTypeMask & (unionInvTypeMask - 1)) == 0)
                {
                    legacyInventorySlot = BitOperations.TrailingZeroCount(unionInvTypeMask);
                }
                else
                {
                    // Targeted heuristic for the Armor "Chest" button which the modern
                    // client sends as CHEST(5) | ROBE(20) = 0x100020. Kronos can't accept
                    // both INVTYPEs in one query, so pick whichever is most likely to
                    // hold the user's expected results given the chosen subclass:
                    //   Cloth (subclass 1) -> INVTYPE_ROBE (vanilla cloth chests are
                    //     overwhelmingly robes; misses the rare cloth INVTYPE_CHEST tunic)
                    //   Leather/Mail/Plate -> INVTYPE_CHEST (the inverse — these almost
                    //     never use INVTYPE_ROBE)
                    // Other multi-bit masks (MainHand/OffHand/Ranged) keep the -1
                    // fallback because there's no clean single-INVTYPE heuristic.
                    const uint chestRobeMask = (1u << 5) | (1u << 20);
                    if (unionInvTypeMask == chestRobeMask && legacyMainCategory == 4)
                        legacyInventorySlot = (legacySubCategory == 1) ? 20 : 5;
                }
            }
        }

        Log.Event("auction.list.filter", new
        {
            modern_class_filters = auction.ClassFilters.Count,
            modern_item_class = auction.ClassFilters.Count > 0
                ? (int?)auction.ClassFilters[0].ItemClass : null,
            modern_subclass_count = auction.ClassFilters.Count > 0
                ? auction.ClassFilters[0].SubClassFilters.Count : 0,
            modern_first_subclass = (auction.ClassFilters.Count > 0
                                     && auction.ClassFilters[0].SubClassFilters.Count > 0)
                ? (int?)auction.ClassFilters[0].SubClassFilters[0].ItemSubclass : null,
            modern_invtype_mask_union = unionInvTypeMask,
            legacy_inventory_slot = legacyInventorySlot,
            legacy_main_category = legacyMainCategory,
            legacy_sub_category = legacySubCategory,
            quality = auction.Quality,
            only_usable = auction.OnlyUsable,
            name = auction.Name
        });

        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_LIST_ITEMS);
        packet.WriteGuid(auction.Auctioneer.To64());
        packet.WriteUInt32(auction.Offset);
        packet.WriteCString(auction.Name);
        packet.WriteUInt8(auction.MinLevel);
        packet.WriteUInt8(auction.MaxLevel);
        packet.WriteInt32(legacyInventorySlot);
        packet.WriteInt32(legacyMainCategory);
        packet.WriteInt32(legacySubCategory);
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
