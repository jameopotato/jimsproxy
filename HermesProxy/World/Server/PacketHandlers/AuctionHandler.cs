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

    // Minimum gap between forwarded CMSG_AUCTION_SELL_ITEM packets. Aux and similar
    // batch-posting addons can fire sells ~190 ms apart, which is well under the
    // cadence a human at the Blizzard UI could ever reach and risks tripping general
    // per-socket flood thresholds on vanilla emulators. Sells are queued (not dropped)
    // by sleeping the handler thread until the gap is satisfied.
    //
    // Scope: this gate is per-WorldSocket (per-character session), not per-auctioneer.
    // A player who hops from a neutral AH to a faction AH within 500 ms will see the
    // residual throttle apply to the first sell at the new auctioneer. That's a wash
    // in practice because the legacy server's own per-socket flood limit also doesn't
    // care which auctioneer the request originated from.
    private const int AuctionSellMinGapMs = 500;
    private DateTime _lastSellTime = DateTime.MinValue;

    // Maximum time to wait after a CMSG_SPLIT_ITEM / CMSG_SWAP_ITEM for the resulting
    // SMSG_ITEM_PUSH_RESULT / object update to arrive and update GameState before we
    // read the destination slot back. We poll adaptively (see WaitForInventoryChange)
    // and return the moment a change is observed; this 750 ms ceiling is the safety
    // net for a slow / stalled server. Typical update arrival is 50-200 ms.
    private const int AuctionSplitSettleMs = 750;
    private const int AuctionSplitPollMs = 25;

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
        // JimsProxy: while a Full Scan is in progress, the proxy is internally
        // paginating CMSG_AUCTION_LIST_ITEMS at the 6s cooldown. A user search
        // click during that window would race for the same Kronos query slot
        // and trip its anti-flood drop, kicking us. Reject with a chat-system
        // line so the player knows scan is running.
        var replicateState = GetSession().GameState;
        bool replicateActive;
        int replicatePage;
        int replicateAccum;
        lock (replicateState.AuctionReplicateLock)
        {
            replicateActive = replicateState.AuctionReplicateInProgress;
            replicatePage = replicateState.AuctionReplicatePage;
            replicateAccum = replicateState.AuctionReplicateAccumulator.Count;
        }
        if (replicateActive)
        {
            Log.Event("auction.list.rejected_replicate_in_progress", new
            {
                replicate_page = replicatePage,
                replicate_items_so_far = replicateAccum,
                name = auction.Name,
            });
            ChatPkt notice = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                $"Full Scan in progress (page {replicatePage}, {replicateAccum} items). Please wait for it to finish before searching.");
            GetSession().WorldClient!.SendPacketToClient(notice);
            return;
        }

        var now = DateTime.UtcNow;
        var elapsedMs = (now - _lastSearchTime).TotalMilliseconds;
        var cooldownMs = AuctionSearchCooldownSeconds * 1000.0;
        if (elapsedMs < cooldownMs)
        {
            // JimsProxy (aux-auction-cooldown-investigation): silent-drop diagnostic only.
            // No behavior change vs. prior code -- still returns without forwarding to the
            // legacy server. Hypothesis: this drop leaves the modern client's
            // CanSendAuctionQuery() permanently false (no SMSG_AUCTION_LIST_ITEMS_RESULT
            // ever arrives), which hangs Aux's pre-post search loop at scan.lua:117. If
            // this event fires when Aux hangs, theory confirmed and the fix is to queue
            // the CMSG until cooldown expires instead of dropping.
            Framework.Logging.Log.Event("auction.list.dropped_cooldown", new
            {
                elapsed_ms = (long)elapsedMs,
                cooldown_ms = (long)cooldownMs,
                remaining_ms = (long)(cooldownMs - elapsedMs),
                name = auction.Name,
                offset = auction.Offset,
                exact_match = auction.ExactMatch,
                only_usable = auction.OnlyUsable,
                quality = auction.Quality,
            });
            return;
        }
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

    private void SendMergePieceSplit(
        GameSessionData gameState,
        (byte containerSlot, byte slot) src,
        (byte containerSlot, byte slot) dst,
        uint count,
        string sourceGuid,
        int pieceIndex,
        string mode)
    {
        var preGuid = gameState.GetInventorySlotItem(dst.containerSlot, dst.slot);
        uint preStack = preGuid != WowGuid64.Empty
            ? gameState.GetItemStackCount(preGuid.To128(gameState))
            : 0;

        WorldPacket splitPacket = new WorldPacket(Opcode.CMSG_SPLIT_ITEM);
        splitPacket.WriteUInt8(src.containerSlot);
        splitPacket.WriteUInt8(src.slot);
        splitPacket.WriteUInt8(dst.containerSlot);
        splitPacket.WriteUInt8(dst.slot);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            splitPacket.WriteInt32((int)count);
        else
            splitPacket.WriteUInt8((byte)count);
        SendPacketToServer(splitPacket);

        Framework.Logging.Log.Event("auction.sell.merge_piece_sent", new
        {
            source_container = src.containerSlot,
            source_slot = src.slot,
            dest_container = dst.containerSlot,
            dest_slot = dst.slot,
            count,
            source_guid = sourceGuid,
            piece_index = pieceIndex,
            mode,
        });

        int waitedMs = WaitForInventoryChange(gameState, dst, preGuid, preStack, AuctionSplitSettleMs);
        Framework.Logging.Log.Event("auction.sell.merge_piece_settled", new
        {
            piece_index = pieceIndex,
            mode,
            waited_ms = waitedMs,
            timed_out = waitedMs >= AuctionSplitSettleMs - AuctionSplitPollMs,
        });
    }

    // Poll GameState until the destination slot reflects a change from the captured
    // pre-operation (guid, stack) tuple, or the timeout elapses. Returns the elapsed
    // time so the diagnostic can show how long the inventory update actually took.
    // Server inventory updates arrive on the WorldClient thread; this WorldSocket
    // thread sleeps in short intervals so it doesn't block update processing.
    private int WaitForInventoryChange(
        GameSessionData gameState,
        (byte containerSlot, byte slot) target,
        WowGuid64 beforeGuid,
        uint beforeStack,
        int maxMs)
    {
        var start = DateTime.UtcNow;
        var deadline = start.AddMilliseconds(maxMs);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(AuctionSplitPollMs);
            var nowGuid = gameState.GetInventorySlotItem(target.containerSlot, target.slot);
            uint nowStack = nowGuid != WowGuid64.Empty
                ? gameState.GetItemStackCount(nowGuid.To128(gameState))
                : 0;
            if (nowGuid != beforeGuid || nowStack != beforeStack)
                return (int)(DateTime.UtcNow - start).TotalMilliseconds;
        }
        return (int)(DateTime.UtcNow - start).TotalMilliseconds;
    }

    // CMSG_SWAP_ITEM with src holding a same-item-type stack and dst already
    // populated with the same item triggers vanilla Player::SwapItem's auto-stack
    // path — the server merges src into dst rather than swapping slot positions.
    // This is how we land the final unit of a "full-stack-into-target" merge that
    // CMSG_SPLIT_ITEM can't handle (split rejects count >= stack).
    private void SendMergePieceSwap(
        GameSessionData gameState,
        (byte containerSlot, byte slot) src,
        (byte containerSlot, byte slot) dst,
        string sourceGuid,
        int pieceIndex,
        string mode)
    {
        var preGuid = gameState.GetInventorySlotItem(dst.containerSlot, dst.slot);
        uint preStack = preGuid != WowGuid64.Empty
            ? gameState.GetItemStackCount(preGuid.To128(gameState))
            : 0;

        WorldPacket swapPacket = new WorldPacket(Opcode.CMSG_SWAP_ITEM);
        // Vanilla CMSG_SWAP_ITEM packet order: dstBag, dstSlot, srcBag, srcSlot.
        // This matches the modern->legacy byte order in the existing translator at
        // ItemHandler.cs:74-87 (HandleSwapItem), where the modern client's "B" slots
        // (destination) are written first and "A" slots (source) second.
        swapPacket.WriteUInt8(dst.containerSlot);
        swapPacket.WriteUInt8(dst.slot);
        swapPacket.WriteUInt8(src.containerSlot);
        swapPacket.WriteUInt8(src.slot);
        SendPacketToServer(swapPacket);

        Framework.Logging.Log.Event("auction.sell.merge_piece_sent", new
        {
            source_container = src.containerSlot,
            source_slot = src.slot,
            dest_container = dst.containerSlot,
            dest_slot = dst.slot,
            count = (uint)0,
            source_guid = sourceGuid,
            piece_index = pieceIndex,
            mode,
        });

        int waitedMs = WaitForInventoryChange(gameState, dst, preGuid, preStack, AuctionSplitSettleMs);
        Framework.Logging.Log.Event("auction.sell.merge_piece_settled", new
        {
            piece_index = pieceIndex,
            mode,
            waited_ms = waitedMs,
            timed_out = waitedMs >= AuctionSplitSettleMs - AuctionSplitPollMs,
        });
    }

    [PacketHandler(Opcode.CMSG_AUCTION_SELL_ITEM)]
        void HandleAuctionSellItem(AuctionSellItem auction)
        {
        var sellNow = DateTime.UtcNow;
        var sellElapsedMs = (sellNow - _lastSellTime).TotalMilliseconds;
        if (sellElapsedMs < AuctionSellMinGapMs)
        {
            int waitMs = (int)(AuctionSellMinGapMs - sellElapsedMs);
            Framework.Logging.Log.Event("auction.sell.throttled", new
            {
                elapsed_ms = (long)sellElapsedMs,
                wait_ms = waitMs,
                min_gap_ms = AuctionSellMinGapMs,
                item_count = auction.Items.Count,
            });
            Thread.Sleep(waitMs);
        }
        _lastSellTime = DateTime.UtcNow;

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

            // Pre-3.2.2a CMSG_AUCTION_SELL_ITEM has no per-item count field — the
            // server auctions the entire stack at the GUID. Modern (3.2.2a+) clients
            // send a list of (item, useCount) tuples and expect the server to combine
            // them into one stack for one auction. To honor that semantics on a vanilla
            // server, we have to do the combining client-side BEFORE sending the legacy
            // sell packet:
            //   * Multi-item CMSGs: split every item's useCount into ONE shared target
            //     slot (vanilla CMSG_SPLIT_ITEM auto-merges when the destination has the
            //     same item type), then auction the merged GUID once.
            //   * Single-item CMSG with useCount < stack: split useCount into a temp
            //     slot and auction the temp.
            //   * Single-item CMSG with useCount == stack: auction the source GUID
            //     directly (no split needed).
            if (auction.Items.Count > 1)
            {
                ChatPkt mergeNotice = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                    $"Merging {auction.Items.Count} item stacks for auction...");
                GetSession().WorldClient!.SendPacketToClient(mergeNotice);

                var mergeTarget = gameState.FindEmptyInventorySlot();
                if (mergeTarget == null)
                {
                    Log.Event("auction.sell.merge_failed", new
                    {
                        reason = "no_empty_slot",
                        item_count = auction.Items.Count,
                    });
                    ChatPkt chat = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                        "Auction posting cancelled — no empty bag slot for merge. Free up a slot and try again.");
                    GetSession().WorldClient!.SendPacketToClient(chat);
                    return;
                }

                uint totalRequested = 0;
                int piecesMoved = 0;
                uint expectedDestStack = 0;
                var pieceOutcomes = new System.Collections.Generic.List<object>();
                foreach (var item in auction.Items)
                {
                    if (item.UseCount == 0) continue;

                    var srcLocation = gameState.FindItemInInventory(item.Guid.To64());
                    if (srcLocation == null)
                    {
                        pieceOutcomes.Add(new
                        {
                            piece_index = piecesMoved,
                            use_count = item.UseCount,
                            skipped = true,
                            skip_reason = "src_not_in_inventory",
                        });
                        Log.Event("auction.sell.merge_skip_piece", new
                        {
                            reason = "src_not_in_inventory",
                            source_guid = item.Guid.ToString(),
                            use_count = item.UseCount,
                        });
                        continue;
                    }

                    uint srcStackCount = gameState.GetItemStackCount(item.Guid);

                    // Vanilla CMSG_SPLIT_ITEM is validated server-side as
                    // pSrcItem->GetCount() > count, i.e. it's rejected with
                    // EQUIP_ERR_TRIED_TO_SPLIT_MORE_THAN_COUNT when count >= stack.
                    // For full-stack-or-larger pieces:
                    //   * srcStack >= 2: SPLIT (srcStack-1) into target, then CMSG_SWAP_ITEM
                    //     the leftover 1-stack with target. Vanilla Player::SwapItem detects
                    //     same-item-type with stack space and auto-merges the final unit
                    //     in instead of swapping slot positions.
                    //   * srcStack == 1: skip SPLIT entirely; the SWAP alone moves/merges
                    //     the 1-stack into target.
                    if (item.UseCount < srcStackCount)
                    {
                        SendMergePieceSplit(gameState, srcLocation.Value, mergeTarget.Value,
                            item.UseCount, item.Guid.ToString(), piecesMoved, "single");
                    }
                    else if (srcStackCount >= 2)
                    {
                        SendMergePieceSplit(gameState, srcLocation.Value, mergeTarget.Value,
                            srcStackCount - 1, item.Guid.ToString(), piecesMoved, "n_minus_1");
                        SendMergePieceSwap(gameState, srcLocation.Value, mergeTarget.Value,
                            item.Guid.ToString(), piecesMoved, "swap_leftover");
                    }
                    else
                    {
                        SendMergePieceSwap(gameState, srcLocation.Value, mergeTarget.Value,
                            item.Guid.ToString(), piecesMoved, "swap_full_stack_of_1");
                    }

                    expectedDestStack += item.UseCount;

                    var destGuidNow = gameState.GetInventorySlotItem(
                        mergeTarget.Value.containerSlot, mergeTarget.Value.slot);
                    uint destStackNow = destGuidNow != WowGuid64.Empty
                        ? gameState.GetItemStackCount(destGuidNow.To128(gameState))
                        : 0;
                    bool stackMatches = destStackNow == expectedDestStack;
                    pieceOutcomes.Add(new
                    {
                        piece_index = piecesMoved,
                        use_count = item.UseCount,
                        dest_stack_after = destStackNow,
                        expected_stack = expectedDestStack,
                        ok = stackMatches,
                    });
                    Log.Event("auction.sell.merge_piece_resolved", new
                    {
                        dest_guid = destGuidNow.ToString(),
                        dest_stack = destStackNow,
                        expected_stack = expectedDestStack,
                        ok = stackMatches,
                        piece_index = piecesMoved,
                    });

                    // TOCTOU + correctness defense: dest slot's stack count after the
                    // SPLIT/SWAP should equal the running sum of useCounts moved into it.
                    // A mismatch means one of:
                    //   * an unrelated inventory mutation (loot, trade, quest reward) hit
                    //     the slot we picked from FindEmptyInventorySlot before our first
                    //     piece landed (vanilla SPLIT auto-merges into same-item-type slot
                    //     so dest_stack would exceed expected) or rejected our SPLIT
                    //     because dest holds a different item (dest_stack equals the
                    //     unrelated item's stack);
                    //   * the SPLIT/SWAP was rejected for some other server-side reason;
                    //   * the SMSG_ITEM_PUSH_RESULT settle window timed out so GameState
                    //     is stale.
                    // Either way we cannot safely auction this slot — auctioning would
                    // sell the wrong stack count or wrong item entirely. Bail.
                    if (!stackMatches)
                    {
                        Log.Event("auction.sell.merge_failed", new
                        {
                            reason = "dest_stack_unexpected",
                            piece_index = piecesMoved,
                            expected_stack = expectedDestStack,
                            actual_stack = destStackNow,
                            actual_dest_guid = destGuidNow.ToString(),
                            pieces_moved_so_far = piecesMoved,
                            total_requested = totalRequested + item.UseCount,
                            pieces = pieceOutcomes,
                        });
                        ChatPkt chat = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                            "Auction posting cancelled — items were moved but not posted. Sort your bags into clean stacks and try again.");
                        GetSession().WorldClient!.SendPacketToClient(chat);
                        return;
                    }

                    totalRequested += item.UseCount;
                    piecesMoved++;
                }

                var mergedGuid = gameState.GetInventorySlotItem(
                    mergeTarget.Value.containerSlot, mergeTarget.Value.slot);
                uint mergedStack = mergedGuid != WowGuid64.Empty
                    ? gameState.GetItemStackCount(mergedGuid.To128(gameState))
                    : 0;

                if (mergedGuid == WowGuid64.Empty)
                {
                    Log.Event("auction.sell.merge_failed", new
                    {
                        reason = "target_empty_after_merge",
                        pieces_moved = piecesMoved,
                        total_requested = totalRequested,
                        pieces = pieceOutcomes,
                    });
                    ChatPkt chat = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                        "Auction posting cancelled — items were moved but not posted. Sort your bags into clean stacks and try again.");
                    GetSession().WorldClient!.SendPacketToClient(chat);
                    return;
                }

                WorldPacket legacyPacket = new WorldPacket(Opcode.CMSG_AUCTION_SELL_ITEM);
                legacyPacket.WriteGuid(auction.Auctioneer.To64());
                legacyPacket.WriteGuid(mergedGuid);
                legacyPacket.WriteUInt32((uint)auction.MinBid);
                legacyPacket.WriteUInt32((uint)auction.BuyoutPrice);
                legacyPacket.WriteUInt32(expireTime);
                SendPacketToServer(legacyPacket);

                Log.Event("auction.sell.posted_merged", new
                {
                    auction_item_guid = mergedGuid.ToString(),
                    merged_stack_count = mergedStack,
                    total_requested = totalRequested,
                    item_count = auction.Items.Count,
                    pieces_moved = piecesMoved,
                });
                return;
            }

            // Single-item path below. Compute needsSplit / splitSlot for the lone item.
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
                WowGuid64 sourceItemGuid = item.Guid.To64();
                uint preSplitStackCount = gameState.GetItemStackCount(item.Guid);
                bool didSplit = false;

                if (item.UseCount > 0 && splitSlot != null)
                {
                    uint currentStackCount = preSplitStackCount;

                    if (item.UseCount < currentStackCount)
                    {
                        var itemLocation = gameState.FindItemInInventory(item.Guid.To64());

                        if (itemLocation == null)
                        {
                            Log.Event("auction.sell.split_failed", new
                            {
                                reason = "item_not_in_inventory",
                                item_guid = item.Guid.ToString(),
                                use_count = item.UseCount,
                                stack_count = currentStackCount,
                            });
                            continue;
                        }

                        // Snapshot dest slot pre-state so we can poll for the inventory
                        // update (SMSG_ITEM_PUSH_RESULT / object update) instead of
                        // waiting a fixed AuctionSplitSettleMs every time.
                        var preGuid = gameState.GetInventorySlotItem(
                            splitSlot.Value.containerSlot, splitSlot.Value.slot);
                        uint preStack = preGuid != WowGuid64.Empty
                            ? gameState.GetItemStackCount(preGuid.To128(gameState))
                            : 0;

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

                        Log.Event("auction.sell.split_sent", new
                        {
                            source_container = itemLocation.Value.containerSlot,
                            source_slot = itemLocation.Value.slot,
                            dest_container = splitSlot.Value.containerSlot,
                            dest_slot = splitSlot.Value.slot,
                            count = item.UseCount,
                            source_stack_count = currentStackCount,
                            source_guid = item.Guid.ToString(),
                        });

                        int waitedMs = WaitForInventoryChange(gameState, splitSlot.Value,
                            preGuid, preStack, AuctionSplitSettleMs);
                        Log.Event("auction.sell.split_settled", new
                        {
                            waited_ms = waitedMs,
                            timed_out = waitedMs >= AuctionSplitSettleMs - AuctionSplitPollMs,
                            source_guid = item.Guid.ToString(),
                        });

                        // Read the new item's GUID from the destination slot
                        auctionItemGuid = gameState.GetInventorySlotItem(
                            splitSlot.Value.containerSlot, splitSlot.Value.slot);

                        uint postSplitDestStack = auctionItemGuid != WowGuid64.Empty
                            ? gameState.GetItemStackCount(auctionItemGuid.To128(gameState))
                            : 0;
                        uint postSplitSourceStack = gameState.GetItemStackCount(item.Guid);

                        Log.Event("auction.sell.split_resolved", new
                        {
                            dest_container = splitSlot.Value.containerSlot,
                            dest_slot = splitSlot.Value.slot,
                            dest_guid_after = auctionItemGuid.ToString(),
                            dest_stack_after = postSplitDestStack,
                            source_stack_after = postSplitSourceStack,
                            source_guid = item.Guid.ToString(),
                            same_as_source = auctionItemGuid == sourceItemGuid,
                            empty_dest = auctionItemGuid == WowGuid64.Empty,
                        });

                        if (auctionItemGuid == WowGuid64.Empty)
                        {
                            Log.Event("auction.sell.split_failed", new
                            {
                                reason = "dest_slot_still_empty_after_500ms",
                                source_guid = item.Guid.ToString(),
                                use_count = item.UseCount,
                            });
                            continue;
                        }

                        didSplit = true;
                    }
                }

                // Send the legacy CMSG using the post-split GUID so the server auctions
                // the partial stack we just moved to the temp slot — not the original
                // (now-shrunk) source stack. Pre-3.2.2a CMSG_AUCTION_SELL_ITEM has no
                // count field, so the server always auctions the whole stack at the GUID.
                WorldPacket legacyPacket = new WorldPacket(Opcode.CMSG_AUCTION_SELL_ITEM);
                legacyPacket.WriteGuid(auction.Auctioneer.To64());
                legacyPacket.WriteGuid(auctionItemGuid);
                legacyPacket.WriteUInt32((uint)auction.MinBid);
                legacyPacket.WriteUInt32((uint)auction.BuyoutPrice);
                legacyPacket.WriteUInt32(expireTime);
                SendPacketToServer(legacyPacket);

                Log.Event("auction.sell.posted", new
                {
                    auction_item_guid = auctionItemGuid.ToString(),
                    source_item_guid = sourceItemGuid.ToString(),
                    use_count = item.UseCount,
                    pre_split_stack_count = preSplitStackCount,
                    did_split = didSplit,
                    auctioning_source_directly = auctionItemGuid == sourceItemGuid,
                });
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

    // JimsProxy: legacy 1.12 vanilla page size for SMSG_AUCTION_LIST_ITEMS_RESULT.
    // Mirrors Auctionator.Constants.MaxResultsPerPage; a returned page shorter than
    // this is the last page and ends the replicate walk.
    internal const int AuctionLegacyPageSize = 50;

    [PacketHandler(Opcode.CMSG_AUCTION_REPLICATE_ITEMS)]
    void HandleAuctionReplicateItems(AuctionReplicateItems req)
    {
        // CMSG_AUCTION_REPLICATE_ITEMS is the modern Full Scan opcode. Default
        // Browse-UI Search uses CMSG_AUCTION_LIST_ITEMS (different opcode,
        // different handler), so any REPLICATE_ITEMS arriving here came from an
        // addon (Auctionator / Auctioneer / etc.) explicitly calling
        // C_AuctionHouse.ReplicateItems(). Always walk the full AH.
        var gameState = GetSession().GameState;
        bool alreadyRunning = false;
        lock (gameState.AuctionReplicateLock)
        {
            if (gameState.AuctionReplicateInProgress)
            {
                alreadyRunning = true;
            }
            else
            {
                gameState.AuctionReplicateInProgress = true;
                gameState.AuctionReplicatePage = 0;
                gameState.AuctionReplicateAuctioneer = req.Auctioneer;
                gameState.AuctionReplicateChangeNumberGlobal = req.ChangeNumberGlobal;
                gameState.AuctionReplicateChangeNumberCursor = req.ChangeNumberCursor;
                gameState.AuctionReplicateChangeNumberTombstone = req.ChangeNumberTombstone;
                gameState.AuctionReplicateAccumulator.Clear();
                gameState.AuctionReplicateStartTime = DateTime.UtcNow;
            }
        }

        if (alreadyRunning)
        {
            Log.Event("auction.replicate.rejected_already_running", new
            {
                auctioneer = req.Auctioneer.ToString(),
            });
            // Send an empty (Result=0, 0 items) response so the modern client's
            // Full Scan frame doesn't hang waiting on AUCTION_ITEM_LIST_UPDATE.
            SendEmptyAuctionReplicateResponse(req);
            return;
        }

        Log.Event("auction.replicate.started", new
        {
            auctioneer = req.Auctioneer.ToString(),
            change_global = req.ChangeNumberGlobal,
            change_cursor = req.ChangeNumberCursor,
            change_tombstone = req.ChangeNumberTombstone,
            wire_tainted = req.TaintedBy != null,
            wire_tainted_by = req.TaintedBy?.Name ?? "",
        });

        // Stamp our cooldown so the very next user CMSG (if any slips through
        // before the first SMSG result re-asserts the in-progress flag in the
        // common path) won't double up.
        _lastSearchTime = DateTime.UtcNow;
        SendReplicatePageQuery(req.Auctioneer, page: 0);
    }

    internal void SendReplicatePageQuery(WowGuid128 auctioneer, uint page)
    {
        // The legacy server's CMSG_AUCTION_LIST_ITEMS offset field is an *item*
        // offset (rows-to-skip), not a page index. Confirmed against a 109-item
        // PTR AH: sending raw page index produced 50 near-identical rows per
        // "page" because the server window slid by 1 item instead of 50 (every
        // SMSG was 3210 bytes: 50 items × ~64 bytes, just shifted). Multiply by
        // the legacy 50-row page size so each query advances a full window.
        uint itemOffset = page * (uint)AuctionLegacyPageSize;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUCTION_LIST_ITEMS);
        packet.WriteGuid(auctioneer.To64());
        packet.WriteUInt32(itemOffset);
        packet.WriteCString("");
        packet.WriteUInt8(0);  // minLevel
        packet.WriteUInt8(0);  // maxLevel
        packet.WriteInt32(-1); // inventorySlot — any
        packet.WriteInt32(-1); // mainCategory
        packet.WriteInt32(-1); // subCategory
        packet.WriteInt32(-1); // quality — any
        packet.WriteBool(false); // onlyUsable
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteBool(false); // exactMatch
            packet.WriteUInt8(0);    // sortCount
        }
        SendPacketToServer(packet);
        Log.Event("auction.replicate.page_query_sent", new { page, item_offset = itemOffset });
    }

    private void SendEmptyAuctionReplicateResponse(AuctionReplicateItems req)
    {
        AuctionReplicateResponse resp = new AuctionReplicateResponse
        {
            Result = 0,
            DesiredDelay = 6000,
            ChangeNumberGlobal = req.ChangeNumberGlobal,
            ChangeNumberCursor = req.ChangeNumberCursor,
            ChangeNumberTombstone = req.ChangeNumberTombstone,
        };
        GetSession().WorldClient!.SendPacketToClient(resp);
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
