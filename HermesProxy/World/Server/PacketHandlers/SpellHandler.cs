using Framework;
using Framework.Constants;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using Framework.Logging;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    SpellCastTargetFlags ConvertSpellTargetFlags(SpellTargetData target)
    {
        SpellCastTargetFlags targetFlags = SpellCastTargetFlags.None;
        if (target.Unit != default && !target.Unit.IsEmpty())
        {
            if (target.Flags.HasFlag(SpellCastTargetFlags.Unit))
                targetFlags |= SpellCastTargetFlags.Unit;
            if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseEnemy))
                targetFlags |= SpellCastTargetFlags.CorpseEnemy;
            if (target.Flags.HasFlag(SpellCastTargetFlags.GameObject))
                targetFlags |= SpellCastTargetFlags.GameObject;
            if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseAlly))
                targetFlags |= SpellCastTargetFlags.CorpseAlly;
            if (target.Flags.HasFlag(SpellCastTargetFlags.UnitMinipet))
                targetFlags |= SpellCastTargetFlags.UnitMinipet;
        }
        if (target.Item != default & !target.Item.IsEmpty())
        {
            if (target.Flags.HasFlag(SpellCastTargetFlags.Item))
                targetFlags |= SpellCastTargetFlags.Item;
            if (target.Flags.HasFlag(SpellCastTargetFlags.TradeItem))
                targetFlags |= SpellCastTargetFlags.TradeItem;
        }
        if (target.SrcLocation != null)
            targetFlags |= SpellCastTargetFlags.SourceLocation;
        if (target.DstLocation != null)
            targetFlags |= SpellCastTargetFlags.DestLocation;
        if (!String.IsNullOrEmpty(target.Name))
            targetFlags |= SpellCastTargetFlags.String;
        return targetFlags;
    }
    void WriteSpellTargets(SpellTargetData target, SpellCastTargetFlags targetFlags, WorldPacket packet)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            targetFlags = (SpellCastTargetFlags)((uint)targetFlags & 0x0000FFFF);
            packet.WriteUInt16((ushort)targetFlags);
        }
        else
            packet.WriteUInt32((uint)targetFlags);

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.GameObject |
            SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet))
            packet.WritePackedGuid(target.Unit.To64());

        // Check if the user wants to target the "Will not be traded" slot
        if (targetFlags.HasFlag(SpellCastTargetFlags.TradeItem) && target.Item == WowGuid128.Create(HighGuidType703.Uniq, 10))
            packet.WritePackedGuid(new WowGuid64((ulong) TradeSlots.NonTraded));
        else if (targetFlags.HasFlag(SpellCastTargetFlags.Item))
            packet.WritePackedGuid(target.Item.To64());

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(target.SrcLocation.Transport.To64());
            packet.WriteVector3(target.SrcLocation.Location);
        }

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                packet.WritePackedGuid(target.DstLocation.Transport.To64());
            packet.WriteVector3(target.DstLocation.Location);
        }

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
            packet.WriteCString(target.Name);
    }
    public void SendCastRequestFailed(ClientCastRequest castRequest, bool isPet)
    {
        if (!castRequest.HasStarted)
        {
            SpellPrepare prepare2 = new SpellPrepare();
            prepare2.ClientCastID = castRequest.ClientGUID;
            prepare2.ServerCastID = castRequest.ServerGUID;
            SendPacket(prepare2);
        }

        if (isPet)
        {
            PetCastFailed failed = new();
            failed.SpellID = castRequest.SpellId;
            failed.Reason = (uint)SpellCastResultClassic.SpellInProgress;
            failed.CastID = castRequest.ServerGUID;
            SendPacket(failed);
        }
        else
        {
            CastFailed failed = new();
            failed.SpellID = castRequest.SpellId;
            failed.SpellXSpellVisualID = castRequest.SpellXSpellVisualId;
            failed.Reason = (uint)SpellCastResultClassic.SpellInProgress;
            failed.CastID = castRequest.ServerGUID;
            SendPacket(failed);
        }    
    }
    // JimsProxy: Hunter tame-class spell IDs in vanilla — used to log tame attempts
    // for the pet deep-dive. 1515 = "Tame Beast" player ability; the rest are
    // quest-tame variants ("Capture Beast Aspects" etc.) that target specific
    // creature families. List is not exhaustive; if we see new tame variants in
    // the wild, add them here.
    private static readonly System.Collections.Generic.HashSet<uint> TameSpellIds = new()
    {
        1515u,   // Tame Beast (Hunter)
        13481u,  // Tame Hyena (quest variant)
        19484u,  // Tame Wolf (quest variant)
        19597u, 19676u, 19677u, 19678u, // Tame quest variants
        19684u, 19685u, 19686u, 19687u, // Confirmed quest tame variants on Kronos
        19688u, 19689u, 19690u, 19691u, 19692u, 19693u,
        19694u, 19696u, 19697u, 19698u, 19699u, 19700u,
    };

    [PacketHandler(Opcode.CMSG_CAST_SPELL)]
    void HandleCastSpell(CastSpell cast)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            GetSession().GameState.LastDispellSpellId = (uint)cast.Cast.SpellID;

        if (TameSpellIds.Contains(cast.Cast.SpellID))
        {
            Log.Event("pet.tame.cast", new
            {
                spell_id = cast.Cast.SpellID,
                cast_id_counter = cast.Cast.CastID.GetCounter(),
            });
        }

        bool isNextMelee = GameData.NextMeleeSpells.Contains(cast.Cast.SpellID);
        bool isAutoRepeat = GameData.AutoRepeatSpells.Contains(cast.Cast.SpellID);
        // JimsProxy (issue #43): the GCD hold-and-fire path is vanilla-only. On TBC+ the
        // OffGcdSpells whitelist is empty (LoadOffGcdSpells is gated) and haste-adjusted
        // server GCDs make the blanket 1500ms assumption wrong.
        bool isVanillaServer = LegacyVersion.ExpansionVersion == 1;
        bool isOffGcd = isVanillaServer && GameData.IsOffGcd((uint)cast.Cast.SpellID);

        // JimsProxy (issue #43): wire up the GCD-expiry callback once per session so the Timer
        // in GameSessionData can forward held casts via this socket.
        if (isVanillaServer && GetSession().GameState.OnGcdHeldCastFire == null)
            GetSession().GameState.OnGcdHeldCastFire = ForwardHeldGcdCast;

        if (isNextMelee || isAutoRepeat)
        {
            // Next melee and auto repeat spells are tracked separately - they can coexist
            // (e.g., hunter can have Raptor Strike queued AND Auto Shot active)
            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = cast.Cast.SpellID;
            castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = cast.Cast.CastID;

            // Get the appropriate tracking variable based on spell type
            ref ClientCastRequest? currentCast = ref (isAutoRepeat
                ? ref GetSession().GameState.CurrentClientAutoRepeatCast
                : ref GetSession().GameState.CurrentClientNextMeleeCast);

            if (currentCast != null)
            {
                // Already have one of this type in progress - reject
                castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());
                SendCastRequestFailed(castRequest, false);
                return;
            }
            else
            {
                castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, cast.Cast.SpellID, cast.Cast.SpellID + GetSession().GameState.CurrentPlayerGuid.GetCounter());

                SpellPrepare prepare = new SpellPrepare();
                prepare.ClientCastID = cast.Cast.CastID;
                prepare.ServerCastID = castRequest.ServerGUID;
                SendPacket(prepare);

                currentCast = castRequest;
            }
        }
        else
        {
            // Normal casts - use queue for proper FIFO handling of rapid casts
            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = cast.Cast.SpellID;
            castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = cast.Cast.CastID;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

            // JimsProxy (issue #43): off-GCD spells (Sprint, Evasion, Trinket, racials, etc)
            // bypass both the HasStartedNormalCast cast-bar gate and the GCD hold path. A real
            // 1.12 client would fire these mid-cast-bar and mid-GCD, so we match that.
            if (!isOffGcd)
            {
                // Silently drop re-clicks while a cast is in progress. Sending
                // CastFailed(SpellInProgress) causes the 1.14 client to dismiss
                // the active cast bar even though the server-side cast continues.
                if (GetSession().GameState.HasStartedNormalCast())
                    return;

                // JimsProxy (issue #43): if we're inside a GCD hold window, build the CMSG_CAST_SPELL
                // packet now but don't forward it — store it as the pending held cast. The Timer
                // set up in SMSG_SPELL_GO will release it at (estimated GCD expiry - early offset).
                // Mashing during GCD overwrites the held slot so only the most recent press fires.
                if (GetSession().GameState.IsGcdHoldActive())
                {
                    WorldPacket heldPacket = BuildCastSpellPacket(cast);
                    castRequest.HeldPacketForReplay = heldPacket;
                    // Capture remaining GCD before TryHold so the diagnostic event reflects
                    // the value at decision time, not after the hold was installed.
                    long gcdRemainingBeforeHold = GetSession().GameState.GetGcdRemainingMs();
                    castRequest.HeldAtTickMs = Environment.TickCount64;
                    if (GetSession().GameState.TryHoldCastDuringGcd(castRequest, out var displaced))
                    {
                        // JimsProxy: spell.held — emit before the displaced/ack work so the
                        // event sequence reads top-down: hold → displaced → ack.
                        Log.Event("spell.held", new
                        {
                            spell_id = cast.Cast.SpellID,
                            gcd_remaining_ms = gcdRemainingBeforeHold,
                            displaced_spell_id = displaced?.SpellId ?? 0,
                            client_cast_id = castRequest.ClientGUID.ToString(),
                        });

                        // If mashing displaces a previously-held cast, we still need to resolve
                        // its ClientGUID/ServerGUID pair on the client (SpellPrepare was sent
                        // when it was first held). Silently dropping it leaks client-side cast
                        // tracking per displaced press. Mirrors ClearNonStartedNormalCasts in
                        // HandleSpellStart — reason is SpellInProgress, which the 1.14 client
                        // treats as "overridden by newer cast".
                        if (displaced != null)
                        {
                            // JimsProxy: spell.held_displaced — separate event so we can count
                            // displacements (mashing rate) independent of holds (press rate).
                            Log.Event("spell.held_displaced", new
                            {
                                displaced_spell_id = displaced.SpellId,
                                replaced_by_spell_id = cast.Cast.SpellID,
                                displaced_held_for_ms = displaced.HeldAtTickMs > 0
                                    ? Environment.TickCount64 - displaced.HeldAtTickMs
                                    : 0L,
                            });
                            SendCastRequestFailed(displaced, false);
                        }

                        // Acknowledge the keypress immediately so the 1.14 client's UI doesn't feel
                        // unresponsive while we hold the cast.
                        SpellPrepare heldPrepare = new SpellPrepare();
                        heldPrepare.ClientCastID = castRequest.ClientGUID;
                        heldPrepare.ServerCastID = castRequest.ServerGUID;
                        SendPacket(heldPrepare);
                        return;
                    }
                    // Else: GCD expired between IsGcdHoldActive() and TryHoldCastDuringGcd() —
                    // fall through and forward normally.
                    // JimsProxy: spell.held_race_fallthrough — should be rare. If we see this
                    // event firing often, the IsGcdHoldActive→TryHoldCastDuringGcd window is
                    // tighter than expected and the 0ms fire offset is bleeding holds.
                    Log.Event("spell.held_race_fallthrough", new
                    {
                        spell_id = cast.Cast.SpellID,
                        gcd_remaining_ms_at_check = gcdRemainingBeforeHold,
                    });
                }

                // Guard 3: a cast was forwarded to the server but hasn't received
                // SPELL_START/SPELL_GO yet. Hold the new press so it fires when the
                // server responds (BeginGcd creates a new timer that picks it up).
                // NOTE: PR #86's HasInFlightNormalCastForSpell() guard belongs AFTER
                // this block (position 4). GCD hold checks must run first so instant-
                // cast spam is held rather than dropped.
                if (GetSession().GameState.HasForwardedPendingCast())
                {
                    WorldPacket heldPacket = BuildCastSpellPacket(cast);
                    castRequest.HeldPacketForReplay = heldPacket;
                    castRequest.HeldAtTickMs = Environment.TickCount64;
                    var displaced = GetSession().GameState.ForceHoldCast(castRequest);
                    Log.Event("spell.held_pending", new
                    {
                        spell_id = cast.Cast.SpellID,
                        displaced_spell_id = displaced?.SpellId ?? 0,
                        client_cast_id = castRequest.ClientGUID.ToString(),
                    });
                    if (displaced != null)
                        SendCastRequestFailed(displaced, false);
                    SpellPrepare heldPrepare = new SpellPrepare();
                    heldPrepare.ClientCastID = castRequest.ClientGUID;
                    heldPrepare.ServerCastID = castRequest.ServerGUID;
                    SendPacket(heldPrepare);
                    return;
                }

                // Enqueue the cast - responses will be matched by SpellId in FIFO order
                GetSession().GameState.PendingNormalCasts.Enqueue(castRequest);
            }
            else
            {
                // Off-GCD path: still enqueue so SMSG_SPELL_GO can match back to the ClientGUID,
                // but skip the cast-bar gate and the GCD hold.
                GetSession().GameState.PendingNormalCasts.Enqueue(castRequest);
            }
        }

        WorldPacket packet = BuildCastSpellPacket(cast);
        SendPacketToServer(packet);
    }

    /// <summary>
    /// JimsProxy (issue #43): build the outbound CMSG_CAST_SPELL wire packet from a CastSpell.
    /// Extracted so the GCD hold path can construct the packet up front and the timer callback
    /// can send it verbatim when the GCD expires.
    /// </summary>
    private WorldPacket BuildCastSpellPacket(CastSpell cast)
    {
        SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);

        WorldPacket packet = new WorldPacket(Opcode.CMSG_CAST_SPELL);
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteUInt32(cast.Cast.SpellID);
        }
        else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            packet.WriteUInt32(cast.Cast.SpellID);
            packet.WriteUInt8(0); // cast count
        }
        else
        {
            packet.WriteUInt8(0); // cast count
            packet.WriteUInt32(cast.Cast.SpellID);
            packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
        }
        WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
        return packet;
    }

    /// <summary>
    /// JimsProxy (issue #43): invoked by the GCD-expiry Timer (ThreadPool thread) when a held
    /// cast should be released. Enqueues the cast into PendingNormalCasts (so the eventual
    /// SMSG_SPELL_GO can match it back via SpellId/ClientGUID) and forwards the pre-built
    /// CMSG_CAST_SPELL packet to Kronos. The SpellPrepare ACK was already sent when the cast
    /// was first held. Takes PendingCastsLock so the Enqueue doesn't interleave with a
    /// drain-filter-rebuild on the WorldClient thread — otherwise a concurrent drain could
    /// pick up the newly-enqueued cast and wrongly match it against an unrelated SMSG_SPELL_GO.
    /// </summary>
    internal void ForwardHeldGcdCast(ClientCastRequest cast)
    {
        if (cast.HeldPacketForReplay == null)
            return;

        var gameState = GetSession().GameState;
        lock (gameState.PendingCastsLock)
        {
            gameState.PendingNormalCasts.Enqueue(cast);
        }
        // JimsProxy: spell.held_fire — captures total hold duration (press → server forward).
        // Compare against gcd_remaining_ms in the matching spell.held to reconstruct GCD timing.
        Log.Event("spell.held_fire", new
        {
            spell_id = cast.SpellId,
            held_for_ms = cast.HeldAtTickMs > 0 ? Environment.TickCount64 - cast.HeldAtTickMs : 0L,
            client_cast_id = cast.ClientGUID.ToString(),
        });
        SendPacketToServer(cast.HeldPacketForReplay);
    }
    [PacketHandler(Opcode.CMSG_PET_CAST_SPELL)]
    void HandlePetCastSpell(PetCastSpell cast)
    {
        // Pet casts - use queue for proper FIFO handling
        ClientCastRequest castRequest = new ClientCastRequest();
        castRequest.Timestamp = Environment.TickCount;
        castRequest.SpellId = cast.Cast.SpellID;
        castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
        castRequest.ClientGUID = cast.Cast.CastID;
        castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

        // Check if there's already a pet cast in progress - reject without forwarding to server
        if (GetSession().GameState.HasStartedPetCast())
        {
            SendCastRequestFailed(castRequest, true);
            return;
        }

        // Enqueue the cast - responses will be matched by SpellId in FIFO order
        GetSession().GameState.PendingPetCasts.Enqueue(castRequest);

        SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);

        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_CAST_SPELL);
        packet.WriteGuid(cast.PetGUID.To64());
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.WriteUInt8(0); // cast count
        packet.WriteUInt32(cast.Cast.SpellID);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
        WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_USE_ITEM)]
    void HandleUseItem(UseItem use)
    {
        // Item use - use queue for proper FIFO handling
        ClientCastRequest castRequest = new ClientCastRequest();
        castRequest.Timestamp = Environment.TickCount;
        castRequest.SpellId = use.Cast.SpellID;
        castRequest.SpellXSpellVisualId = use.Cast.SpellXSpellVisualID;
        castRequest.ClientGUID = use.Cast.CastID;
        castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, use.Cast.SpellID, 10000 + use.Cast.CastID.GetCounter());
        castRequest.ItemGUID = use.CastItem;

        // Silently drop re-clicks while a cast is in progress (same as HandleCastSpell).
        if (GetSession().GameState.HasStartedNormalCast())
            return;

        // Some items had their on-use spell id renumbered in SoM 1.14.1+ (e.g. Diamond Flask 17626 → 363880).
        // The 1.12 emulator only knows the legacy id, so resolve it now and remember both
        // so SMSG_SPELL_START / SPELL_GO can match the queued cast.
        uint legacySpellId = GetSession().GameState.GetLegacyItemSpellId(use.CastItem, use.Cast.SpellID);
        if (legacySpellId != 0)
            castRequest.LegacySpellId = legacySpellId;

        // Enqueue the cast - responses will be matched by SpellId (or LegacySpellId) in FIFO order
        GetSession().GameState.PendingNormalCasts.Enqueue(castRequest);

        WorldPacket packet = new WorldPacket(Opcode.CMSG_USE_ITEM);
        byte containerSlot = use.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.PackSlot) : use.PackSlot;
        byte slot = use.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.Slot) : use.Slot;
        packet.WriteUInt8(containerSlot);
        packet.WriteUInt8(slot);
        packet.WriteUInt8(GetSession().GameState.GetItemSpellSlot(use.CastItem, legacySpellId != 0 ? legacySpellId : use.Cast.SpellID));
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteUInt8(0); // cast count;
            packet.WriteGuid(use.CastItem.To64());
        }
        SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(use.Cast.Target);
        WriteSpellTargets(use.Cast.Target, targetFlags, packet);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_CANCEL_CAST)]
    void HandleCancelCast(CancelCast cast)
    {
        // JimsProxy (issue #43): if the client cancels while we have a held cast waiting for
        // GCD expiry, drop the held cast so it doesn't fire after the cancel. Because
        // HandleCastSpell already sent SpellPrepare binding ClientCastID↔ServerCastID when
        // the cast was first held, we must route the dropped cast through SendCastRequestFailed
        // so the 1.14 client's cast-tracking map releases the entry. Silently dropping it
        // would leak the ClientGUID/ServerGUID mapping.
        ClientCastRequest? dropped = GetSession().GameState.CancelGcdHold();
        if (dropped != null)
        {
            // JimsProxy: spell.held_cancel — emitted when the client cancels a cast while
            // we were still holding one. Lets us distinguish "user changed their mind during
            // GCD" from genuine displacement/firing in bug bundles.
            Log.Event("spell.held_cancel", new
            {
                dropped_spell_id = dropped.SpellId,
                cancelled_spell_id = cast.SpellID,
                held_for_ms = dropped.HeldAtTickMs > 0
                    ? Environment.TickCount64 - dropped.HeldAtTickMs
                    : 0L,
            });
            SendCastRequestFailed(dropped, false);
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CAST);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.WriteUInt8(0);
        packet.WriteUInt32(cast.SpellID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_CANCEL_CHANNELLING)]
    void HandleCancelChannelling(CancelChannelling cast)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CHANNELLING);
        packet.WriteInt32(cast.SpellID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL)]
    void HandleCancelAutoRepeatSpell(CancelAutoRepeatSpell spell)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_CANCEL_AURA)]
    void HandleCancelAura(CancelAura aura)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
        packet.WriteUInt32(aura.SpellID);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_CANCEL_MOUNT_AURA)]
    void HandleCancelMountAura(EmptyClientPacket cancel)
    {
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_MOUNT_AURA);
            SendPacketToServer(packet);
        }
        else
        {
            WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
            if (updateFields == null)
                return;

            for (byte i = 0; i < 32; i++)
            {
                var aura = GetSession().WorldClient!.ReadAuraSlot(i, guid, updateFields);
                if (aura == null)
                    continue;

                if (GameData.MountAuras.Contains(aura.SpellID))
                {
                    WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
                    packet.WriteUInt32(aura.SpellID);
                    SendPacketToServer(packet);
                }
            }
        }
    }
    [PacketHandler(Opcode.CMSG_LEARN_TALENT)]
    void HandleLearnTalent(LearnTalent talent)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LEARN_TALENT);
        packet.WriteUInt32(talent.TalentID);
        packet.WriteUInt32(talent.Rank);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_RESURRECT_RESPONSE)]
    void HandleResurrectResponse(ResurrectResponse revive)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_RESURRECT_RESPONSE);
        packet.WriteGuid(revive.CasterGUID.To64());
        packet.WriteUInt8((byte)(revive.Response != 0 ? 0 : 1));
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_SELF_RES)]
    void HandleSelfRes(SelfRes revive)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SELF_RES);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_TOTEM_DESTROYED)]
    void HandleTotemDestroyed(TotemDestroyed totem)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            return;

        WorldPacket packet = new WorldPacket(Opcode.CMSG_TOTEM_DESTROYED);
        packet.WriteUInt8(totem.Slot);
        SendPacketToServer(packet);
    }
}
