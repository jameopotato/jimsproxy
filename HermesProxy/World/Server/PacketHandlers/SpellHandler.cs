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
    [PacketHandler(Opcode.CMSG_CAST_SPELL)]
    void HandleCastSpell(CastSpell cast)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            GetSession().GameState.LastDispellSpellId = (uint)cast.Cast.SpellID;

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
                // Check if there's already a cast in progress - reject without forwarding to server
                // This prevents interrupting the current cast (player gets "Another action is in progress")
                if (GetSession().GameState.HasStartedNormalCast())
                {
                    SendCastRequestFailed(castRequest, false);
                    return;
                }

                // JimsProxy (issue #43): if we're inside a GCD hold window, build the CMSG_CAST_SPELL
                // packet now but don't forward it — store it as the pending held cast. The Timer
                // set up in SMSG_SPELL_GO will release it at (estimated GCD expiry - early offset).
                // Mashing during GCD overwrites the held slot so only the most recent press fires.
                if (GetSession().GameState.IsGcdHoldActive())
                {
                    WorldPacket heldPacket = BuildCastSpellPacket(cast);
                    castRequest.HeldPacketForReplay = heldPacket;
                    if (GetSession().GameState.TryHoldCastDuringGcd(castRequest, out var displaced))
                    {
                        // If mashing displaces a previously-held cast, we still need to resolve
                        // its ClientGUID/ServerGUID pair on the client (SpellPrepare was sent
                        // when it was first held). Silently dropping it leaks client-side cast
                        // tracking per displaced press. Mirrors ClearNonStartedNormalCasts in
                        // HandleSpellStart — reason is SpellInProgress, which the 1.14 client
                        // treats as "overridden by newer cast".
                        if (displaced != null)
                            SendCastRequestFailed(displaced, false);

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
            SendCastRequestFailed(dropped, false);

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
