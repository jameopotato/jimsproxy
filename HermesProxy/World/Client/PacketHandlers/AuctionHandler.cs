using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // JimsProxy: gap between proxy-internal CMSG_AUCTION_LIST_ITEMS during a Full
    // Scan walk. 200ms slack above the canonical 6s vanilla cooldown so we never
    // graze Kronos's per-session anti-flood drop (documented in Kronos AH cooldown
    // memory: rapid AH queries silently drop and risk a session kick).
    private const int AuctionReplicatePageGapMs = 6200;
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.MSG_AUCTION_HELLO)]
    void HandleAuctionHello(WorldPacket packet)
    {
        AuctionHelloResponse auction = new AuctionHelloResponse();
        auction.Guid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = auction.Guid;
        auction.AuctionHouseID = packet.ReadUInt32();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            auction.OpenForBusiness = packet.ReadBool();
        SendPacketToClient(auction);

        // Have to send this again here, or server does not reply for some reason.
        WorldPacket packet2 = new WorldPacket(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS);
        packet2.WriteGuid(auction.Guid.To64());
        packet2.WriteUInt32(0);
        SendPacketToServer(packet2);
    }

    AuctionItem ReadAuctionItem(WorldPacket packet)
    {
        AuctionItem item = new AuctionItem();
        item.AuctionID = packet.ReadUInt32();
        item.Item = new();
        item.Item.ItemID = packet.ReadUInt32();

        byte enchantmentCount;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            enchantmentCount = 7;
        else if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            enchantmentCount = 6;
        else
            enchantmentCount = 1;

        for (byte j = 0; j < enchantmentCount; ++j)
        {
            ItemEnchantData enchant = new ItemEnchantData();
            enchant.Slot = j;
            enchant.ID = packet.ReadUInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                enchant.Expiration = packet.ReadUInt32();
                enchant.Charges = packet.ReadInt32();
            }
            if (enchant.ID != 0)
                item.Enchantments.Add(enchant);
        }

        item.Item.RandomPropertiesID = packet.ReadUInt32();
        item.Item.RandomPropertiesSeed = packet.ReadUInt32();
        item.Count = packet.ReadInt32();
        item.Charges = packet.ReadInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
           item.Flags = packet.ReadUInt32();

        item.Owner = packet.ReadGuid().To128(GetSession().GameState);
        item.OwnerAccountID = GetSession().GetGameAccountGuidForPlayer(item.Owner);
        item.MinBid = packet.ReadUInt32();
        item.MinIncrement = packet.ReadUInt32();
        item.BuyoutPrice = packet.ReadUInt32();
        item.DurationLeft = packet.ReadInt32();
        item.Bidder = packet.ReadGuid().To128(GetSession().GameState);
        item.BidAmount = packet.ReadUInt32();

        if (item.Item.ItemID == 0)
            item.Item = null!;

        return item;
    }

    [PacketHandler(Opcode.SMSG_AUCTION_LIST_BIDDED_ITEMS_RESULT)]
    [PacketHandler(Opcode.SMSG_AUCTION_LIST_OWNED_ITEMS_RESULT)]
    void HandleAuctionListMyItemsResult(WorldPacket packet)
    {
        AuctionListMyItemsResult auction = new AuctionListMyItemsResult(packet.GetUniversalOpcode(false));
        uint count = packet.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            AuctionItem item = ReadAuctionItem(packet);
            auction.Items.Add(item);
        }
        auction.TotalItemsCount = packet.ReadInt32();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
            auction.DesiredDelay = packet.ReadUInt32();
        SendPacketToClient(auction);
    }

    [PacketHandler(Opcode.SMSG_AUCTION_LIST_ITEMS_RESULT)]
    void HandleAuctionListItemsResult(WorldPacket packet)
    {
        AuctionListItemsResult auction = new AuctionListItemsResult();
        uint count = packet.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            AuctionItem item = ReadAuctionItem(packet);
            item.CensorServerSideInfo = true;
            auction.Items.Add(item);
        }
        auction.TotalItemsCount = packet.ReadInt32();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_3_0_7561))
            auction.DesiredDelay = packet.ReadUInt32();

        //MIRASU: Floor DesiredDelay at the proxy's own auction-search cooldown so the modern
        //MIRASU: client's Search-button grey-out never re-enables before the proxy will
        //MIRASU: actually accept the next query. Kronos's value (when present) tends to be
        //MIRASU: lower than the canonical 6s vanilla cooldown we enforce.
        const uint MinDesiredDelayMs = 6000;
        if (auction.DesiredDelay < MinDesiredDelayMs)
            auction.DesiredDelay = MinDesiredDelayMs;

        // JimsProxy: while a Full Scan / GetAll is in progress the proxy is the
        // one driving the per-page CMSG_AUCTION_LIST_ITEMS — the modern client
        // never sent these and must not see them. Accumulate instead, then
        // either schedule the next page (if the page came back full) or
        // finalize into one SMSG_AUCTION_REPLICATE_RESPONSE.
        var gameState = GetSession().GameState;
        bool replicating;
        lock (gameState.AuctionReplicateLock)
        {
            replicating = gameState.AuctionReplicateInProgress;
        }
        if (replicating)
        {
            HandleReplicatePageResult(auction);
            return;
        }

        Log.Event("auction.list.result", new
        {
            items_returned = auction.Items.Count,
            total_items_count = auction.TotalItemsCount,
            desired_delay_ms = auction.DesiredDelay,
        });

        SendPacketToClient(auction);
    }

    private void HandleReplicatePageResult(AuctionListItemsResult page)
    {
        var gameState = GetSession().GameState;
        int currentPage;
        int totalSoFar;
        WowGuid128 auctioneer;
        bool isLastPage;

        lock (gameState.AuctionReplicateLock)
        {
            // The session may have been aborted (logout / disconnect) while the
            // legacy result was in flight — drop on the floor in that case.
            if (!gameState.AuctionReplicateInProgress)
                return;

            gameState.AuctionReplicateAccumulator.AddRange(page.Items);
            currentPage = gameState.AuctionReplicatePage;
            totalSoFar = gameState.AuctionReplicateAccumulator.Count;
            auctioneer = gameState.AuctionReplicateAuctioneer;

            // Short page = server says "no more rows".
            isLastPage = page.Items.Count < Server.WorldSocket.AuctionLegacyPageSize;

            if (!isLastPage)
                gameState.AuctionReplicatePage = currentPage + 1;
        }

        Log.Event("auction.replicate.page_received", new
        {
            page = currentPage,
            items_in_page = page.Items.Count,
            items_total = totalSoFar,
            is_last = isLastPage,
        });

        if (isLastPage)
        {
            FinalizeReplicate();
        }
        else
        {
            // Periodic progress update — every 5 pages ≈ 30s on the 6s cooldown.
            if ((currentPage + 1) % 5 == 0)
            {
                ChatPkt progress = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                    $"Full Scan: page {currentPage + 1}...");
                SendPacketToClient(progress);
            }
            ScheduleNextReplicatePage(auctioneer, (uint)(currentPage + 1));
        }
    }

    private void ScheduleNextReplicatePage(WowGuid128 auctioneer, uint nextPage)
    {
        Task.Delay(AuctionReplicatePageGapMs).ContinueWith(_ =>
        {
            try
            {
                var gameState = GetSession().GameState;
                lock (gameState.AuctionReplicateLock)
                {
                    if (!gameState.AuctionReplicateInProgress)
                        return;
                }
                var worldClient = GetSession().WorldClient;
                if (worldClient == null || !worldClient.IsConnected())
                {
                    Log.Event("auction.replicate.aborted", new { reason = "world_client_disconnected" });
                    AbortReplicate();
                    return;
                }
                var socket = GetSession().InstanceSocket;
                if (socket == null)
                {
                    Log.Event("auction.replicate.aborted", new { reason = "no_instance_socket" });
                    AbortReplicate();
                    return;
                }
                socket.SendReplicatePageQuery(auctioneer, nextPage);
            }
            catch (Exception ex)
            {
                Log.Event("auction.replicate.aborted", new { reason = "exception", message = ex.Message });
                AbortReplicate();
            }
        });
    }

    private void FinalizeReplicate()
    {
        var gameState = GetSession().GameState;
        AuctionReplicateResponse resp;
        int finalCount;
        int finalPage;
        TimeSpan elapsed;

        lock (gameState.AuctionReplicateLock)
        {
            if (!gameState.AuctionReplicateInProgress)
                return;

            resp = new AuctionReplicateResponse
            {
                Result = 0,
                DesiredDelay = 6000,
                ChangeNumberGlobal = gameState.AuctionReplicateChangeNumberGlobal,
                ChangeNumberCursor = gameState.AuctionReplicateChangeNumberCursor,
                ChangeNumberTombstone = gameState.AuctionReplicateChangeNumberTombstone,
                Items = new List<AuctionItem>(gameState.AuctionReplicateAccumulator),
            };
            finalCount = resp.Items.Count;
            finalPage = gameState.AuctionReplicatePage;
            elapsed = DateTime.UtcNow - gameState.AuctionReplicateStartTime;
            gameState.AuctionReplicateInProgress = false;
            gameState.AuctionReplicateAccumulator.Clear();
        }

        SendPacketToClient(resp);

        ChatPkt done = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
            $"Full Scan complete: {finalPage + 1} pages in {(long)elapsed.TotalSeconds}s.");
        SendPacketToClient(done);

        Log.Event("auction.replicate.complete", new
        {
            items = finalCount,
            pages = finalPage + 1,
            elapsed_seconds = (long)elapsed.TotalSeconds,
        });
    }

    private void AbortReplicate()
    {
        var gameState = GetSession().GameState;
        lock (gameState.AuctionReplicateLock)
        {
            if (!gameState.AuctionReplicateInProgress)
                return;
            gameState.AuctionReplicateInProgress = false;
            gameState.AuctionReplicateAccumulator.Clear();
        }
    }

    [PacketHandler(Opcode.SMSG_AUCTION_COMMAND_RESULT)]
    void HandleAuctionCommandResult(WorldPacket packet)
    {
        AuctionCommandResult auction = new AuctionCommandResult();
        auction.AuctionID = packet.ReadUInt32();
        auction.Command = (AuctionHouseAction)packet.ReadUInt32();
        auction.ErrorCode = (AuctionHouseError)packet.ReadUInt32();
        switch (auction.ErrorCode)
        {
            case AuctionHouseError.Ok:
                if (auction.Command == AuctionHouseAction.Bid)
                   auction.MinIncrement = packet.ReadUInt32();
                break;
            case AuctionHouseError.Inventory:
                auction.BagResult = LegacyVersion.ConvertInventoryResult(packet.ReadUInt32());
                break;
            case AuctionHouseError.HigherBid:
                auction.Guid = packet.ReadGuid().To128(GetSession().GameState);
                auction.Money = packet.ReadUInt32();
                auction.MinIncrement = packet.ReadUInt32();
                break;
        }
        SendPacketToClient(auction);
        //MIRASU: Auction House refresh fix for 1.14.2 client on Kronos (1.12 TrinityCore).
        //MIRASU: The modern client does not auto-refresh the owned items list after a successful
        //MIRASU: SellItem/RemoveItem/Bid. Native 1.12 clients refresh themselves; 1.14.2 waits for
        //MIRASU: the server to push. We re-request the owned list so Kronos sends fresh data through.
        if (auction.ErrorCode == AuctionHouseError.Ok &&
            (auction.Command == AuctionHouseAction.Sell ||
             auction.Command == AuctionHouseAction.Cancel))
        {
            WowGuid128 auctioneer = GetSession().GameState.CurrentInteractedWithNPC;
            if (!auctioneer.IsEmpty())
            {
                WorldPacket refreshOwned = new WorldPacket(Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS);
                refreshOwned.WriteGuid(auctioneer.To64());
                refreshOwned.WriteUInt32(0);
                SendPacketToServer(refreshOwned);
            }
        }
        //MIRASU: Bid refresh - requests the bidder list so 1.14.2 client shows updated bid state.
        if (auction.ErrorCode == AuctionHouseError.Ok &&
            auction.Command == AuctionHouseAction.Bid)
        {
            WowGuid128 auctioneer = GetSession().GameState.CurrentInteractedWithNPC;
            if (!auctioneer.IsEmpty())
            {
                WorldPacket refreshBidder = new WorldPacket(Opcode.CMSG_AUCTION_LIST_BIDDED_ITEMS);
                refreshBidder.WriteGuid(auctioneer.To64());
                refreshBidder.WriteUInt32(0);
                refreshBidder.WriteInt32(0); //MIRASU: AuctionItemIDs count - 0 means refresh all
                SendPacketToServer(refreshBidder);
            }
        }
    }
    

    [PacketHandler(Opcode.SMSG_AUCTION_OWNER_NOTIFICATION)]
    void HandleAuctionOwnerNotification(WorldPacket packet)
    {
        AuctionOwnerNotification info = new AuctionOwnerNotification();
        info.AuctionID = packet.ReadUInt32();
        info.BidAmount = packet.ReadUInt32();
        uint minIncrement = packet.ReadUInt32();
        WowGuid64 buyer = packet.ReadGuid();
        info.Item.ItemID = packet.ReadUInt32();
        info.Item.RandomPropertiesID = packet.ReadUInt32();

        float mailDelay;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            mailDelay = packet.ReadFloat();
        else
            mailDelay = 3600;

        if (buyer.IsEmpty())
        {
            // BidAmount != 0 -> Your auction of X sold.
            // BidAmount == 0 -> Your auction of X has expired.
            AuctionClosedNotification auction = new AuctionClosedNotification();
            auction.Info = info;
            auction.Sold = info.BidAmount != 0;
            auction.ProceedsMailDelay = mailDelay;
            SendPacketToClient(auction);
        }
        else
        {
            // A buyer has been found for your auction of X.
            AuctionOwnerBidNotification auction = new AuctionOwnerBidNotification();
            auction.Info = info;
            auction.MinIncrement = minIncrement;
            auction.Bidder = buyer.To128(GetSession().GameState);
            SendPacketToClient(auction);
        }
    }

    [PacketHandler(Opcode.SMSG_AUCTION_BIDDER_NOTIFICATION)]
    void HandleAuctionBidderNotification(WorldPacket packet)
    {
        AuctionBidderNotification info = new AuctionBidderNotification();
        uint auctionHouseId = packet.ReadUInt32();
        info.AuctionID = packet.ReadUInt32();
        info.Bidder = packet.ReadGuid().To128(GetSession().GameState);
        uint bidAmount = packet.ReadUInt32();
        uint minIncrement = packet.ReadUInt32();
        info.Item.ItemID = packet.ReadUInt32();
        info.Item.RandomPropertiesID = packet.ReadUInt32();

        if (bidAmount == 0)
        {
            // You won an auction for X.
            AuctionWonNotification auction = new AuctionWonNotification();
            auction.Info = info;
            SendPacketToClient(auction);
        }
        else
        {
            // You have been outbid on X.
            AuctionOutbidNotification auction = new AuctionOutbidNotification();
            auction.Info = info;
            auction.BidAmount = bidAmount;
            auction.MinIncrement = minIncrement;
            SendPacketToClient(auction);
            //MIRASU: Outbid refresh - 1.14.2 client doesn't auto-refresh the bid list when
            //MIRASU: outbid by another player. Re-request the bidded items so the UI updates
            //MIRASU: in real-time (item disappears from "Bids" tab since money was refunded).
            WowGuid128 auctioneer = GetSession().GameState.CurrentInteractedWithNPC;
            if (!auctioneer.IsEmpty())
            {
                WorldPacket refreshBidder = new WorldPacket(Opcode.CMSG_AUCTION_LIST_BIDDED_ITEMS);
                refreshBidder.WriteGuid(auctioneer.To64());
                refreshBidder.WriteUInt32(0);
                refreshBidder.WriteInt32(0); //MIRASU: AuctionItemIDs count - 0 means refresh all
                SendPacketToServer(refreshBidder);
            }
        }
    }
}
