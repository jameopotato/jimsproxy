using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_SEND_KNOWN_SPELLS)]
    void HandleSendKnownSpells(WorldPacket packet)
    {
        SendKnownSpells spells = new SendKnownSpells();
        spells.InitialLogin = packet.ReadBool();
        ushort spellCount = packet.ReadUInt16();
        // JimsProxy (vanilla synthesized spell crit): rebuild the known-spells set on every
        // SMSG_SEND_KNOWN_SPELLS so the synthesis path can walk talent passive auras (Holy
        // Power, Critical Mass, Tidal Mastery, etc.) that the legacy server doesn't surface
        // as visible auras. InitialLogin == true sends the full list; subsequent updates may
        // be deltas, so we don't clear unconditionally.
        if (spells.InitialLogin)
            GetSession().GameState.CurrentPlayerKnownSpells.Clear();
        for (ushort i = 0; i < spellCount; i++)
        {
            uint spellId;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                spellId = packet.ReadUInt32();
            else
                spellId = packet.ReadUInt16();
            spells.KnownSpells.Add(spellId);
            GetSession().GameState.CurrentPlayerKnownSpells.Add(spellId);
            packet.ReadInt16();
        }
        SendPacketToClient(spells);

        ushort cooldownCount = packet.ReadUInt16();
        if (cooldownCount != 0)
        {
            SendSpellHistory histories = new SendSpellHistory();
            for (ushort i = 0; i < cooldownCount; i++)
            {
                SpellHistoryEntry history = new SpellHistoryEntry();

                uint spellId;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    spellId = packet.ReadUInt32();
                else
                    spellId = packet.ReadUInt16();
                history.SpellID = spellId;

                uint itemId;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V4_2_2_14545))
                    itemId = packet.ReadUInt32();
                else
                    itemId = packet.ReadUInt16();
                history.ItemID = itemId;

                history.Category = packet.ReadUInt16();
                history.RecoveryTime = packet.ReadInt32();
                history.CategoryRecoveryTime = packet.ReadInt32();

                histories.Entries.Add(history);
            }
            SendPacketToClient(histories, Opcode.SMSG_SEND_UNLEARN_SPELLS);
        }

        // These packets don't exist in Vanilla.
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            SendPacketToClient(new SendUnlearnSpells());
            SendPacketToClient(new SendSpellCharges());
        }
    }

    [PacketHandler(Opcode.SMSG_SUPERCEDED_SPELLS)]
    void HandleSupercededSpells(WorldPacket packet)
    {
        SupercededSpells spells = new SupercededSpells();
        uint spellId;
        uint supercededId;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            supercededId = packet.ReadUInt32();
            spellId = packet.ReadUInt32();
        }
        else
        {
            supercededId = packet.ReadUInt16();
            spellId = packet.ReadUInt16();
        }
        spells.SpellID.Add(spellId);
        spells.Superceded.Add(supercededId);
        SendPacketToClient(spells);
    }

    [PacketHandler(Opcode.SMSG_LEARNED_SPELL)]
    void HandleLearnedSpell(WorldPacket packet)
    {
        LearnedSpells spells = new LearnedSpells();
        uint spellId = packet.ReadUInt32();
        spells.Spells.Add(spellId);
        SendPacketToClient(spells);
    }

    [PacketHandler(Opcode.SMSG_SEND_UNLEARN_SPELLS)]
    void HandleSendUnlearnSpells(WorldPacket packet)
    {
        SendUnlearnSpells spells = new SendUnlearnSpells();
        uint spellCount = packet.ReadUInt32();
        for (uint i = 0; i < spellCount; i++)
        {
            uint spellId = packet.ReadUInt32();
            spells.Spells.Add(spellId);
        }
        SendPacketToClient(spells);
    }

    [PacketHandler(Opcode.SMSG_UNLEARNED_SPELLS)]
    void HandleUnlearnedSpells(WorldPacket packet)
    {
        UnlearnedSpells spells = new UnlearnedSpells();
        uint spellId;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
            spellId = packet.ReadUInt32();
        else
            spellId = packet.ReadUInt16();
        spells.Spells.Add(spellId);
        SendPacketToClient(spells);
    }

    [PacketHandler(Opcode.SMSG_CAST_FAILED)]
    void HandleCastFailed(WorldPacket packet)
    {
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // cast count

        uint spellId = packet.ReadUInt32();
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            var status = packet.ReadUInt8();
            if (status != 2)
                return;
        }

        uint reason = packet.ReadUInt8();
        if (LegacyVersion.InVersion(ClientVersionBuild.V2_0_1_6180, ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // cast count
        int arg1 = 0;
        int arg2 = 0;
        if (packet.CanRead())
            arg1 = packet.ReadInt32();
        if (packet.CanRead())
            arg2 = packet.ReadInt32();

        // Check special casts first - try next melee, then auto repeat
        ClientCastRequest? specialCast = null;
        bool isAutoRepeat = false;

        if (GetSession().GameState.CurrentClientNextMeleeCast != null &&
            GetSession().GameState.CurrentClientNextMeleeCast!.SpellId == spellId)
        {
            specialCast = GetSession().GameState.CurrentClientNextMeleeCast;
        }
        else if (GetSession().GameState.CurrentClientAutoRepeatCast != null &&
                 GetSession().GameState.CurrentClientAutoRepeatCast!.SpellId == spellId)
        {
            specialCast = GetSession().GameState.CurrentClientAutoRepeatCast;
            isAutoRepeat = true;
        }

        if (specialCast != null)
        {
            CastFailed failed = new();
            failed.SpellID = specialCast.SpellId;
            failed.SpellXSpellVisualID = specialCast.SpellXSpellVisualId;
            failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
            failed.CastID = specialCast.ServerGUID;
            failed.FailedArg1 = arg1;
            failed.FailedArg2 = arg2;
            SendPacketToClient(failed);

            if (isAutoRepeat)
                GetSession().GameState.CurrentClientAutoRepeatCast = null;
            else
                GetSession().GameState.CurrentClientNextMeleeCast = null;
        }
        // Look up pending normal cast by SpellId (queue-based, FIFO order)
        else if (GetSession().GameState.TryDequeuePendingNormalCast(spellId, out var pendingCast))
        {
            if (!pendingCast!.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = pendingCast.ClientGUID;
                prepare2.ServerCastID = pendingCast.ServerGUID;
                SendPacketToClient(prepare2);
            }

            CastFailed failed = new();
            failed.SpellID = pendingCast.SpellId;
            failed.SpellXSpellVisualID = pendingCast.SpellXSpellVisualId;
            failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
            failed.CastID = pendingCast.ServerGUID;
            failed.FailedArg1 = arg1;
            failed.FailedArg2 = arg2;
            SendPacketToClient(failed);

            var gameState = GetSession().GameState;
            if (!gameState.IsGcdHoldActive() && !gameState.HasForwardedPendingCast())
            {
                var heldCast = gameState.TakeHeldCastIfReady();
                if (heldCast != null)
                {
                    Log.Event("spell.held_fire_on_failure", new
                    {
                        failed_spell_id = spellId,
                        held_spell_id = heldCast.SpellId,
                    });
                    gameState.OnGcdHeldCastFire?.Invoke(heldCast);
                }
            }
        }
    }

    [PacketHandler(Opcode.SMSG_PET_CAST_FAILED, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandlePetCastFailed(WorldPacket packet)
    {
        uint spellId = packet.ReadUInt32();
        var status = packet.ReadUInt8();
        if (status != 2)
            return;

        // Look up pending pet cast by SpellId (queue-based, FIFO order)
        if (!GetSession().GameState.TryDequeuePendingPetCast(spellId, out var pendingCast))
            return;

        if (!pendingCast!.HasStarted)
        {
            SpellPrepare prepare2 = new SpellPrepare();
            prepare2.ClientCastID = pendingCast.ClientGUID;
            prepare2.ServerCastID = pendingCast.ServerGUID;
            SendPacketToClient(prepare2);
        }

        PetCastFailed spell = new PetCastFailed();
        spell.SpellID = spellId;
        uint reason = packet.ReadUInt8();
        spell.Reason = LegacyVersion.ConvertSpellCastResult(reason);
        spell.CastID = pendingCast.ServerGUID;
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_PET_CAST_FAILED, ClientVersionBuild.V2_0_1_6180)]
    void HandlePetCastFailedTBC(WorldPacket packet)
    {
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // cast count

        uint spellId = packet.ReadUInt32();

        // Look up pending pet cast by SpellId (queue-based, FIFO order)
        if (!GetSession().GameState.TryDequeuePendingPetCast(spellId, out var pendingCast))
            return;

        if (!pendingCast!.HasStarted)
        {
            SpellPrepare prepare2 = new SpellPrepare();
            prepare2.ClientCastID = pendingCast.ClientGUID;
            prepare2.ServerCastID = pendingCast.ServerGUID;
            SendPacketToClient(prepare2);
        }

        PetCastFailed failed = new PetCastFailed();
        failed.SpellID = spellId;
        uint reason = packet.ReadUInt8();
        failed.Reason = LegacyVersion.ConvertSpellCastResult(reason);
        failed.CastID = pendingCast.ServerGUID;

        if (packet.CanRead())
            failed.FailedArg1 = packet.ReadInt32();
        if (packet.CanRead())
            failed.FailedArg2 = packet.ReadInt32();

        SendPacketToClient(failed);
    }

    [PacketHandler(Opcode.SMSG_SPELL_FAILED_OTHER)]
    void HandleSpellFailedOther(WorldPacket packet)
    {
        //MIRASU - capture raw payload for wire-format diagnosis (mob-cast-bar interrupt bug).
        //MIRASU   GetData() returns the full body buffer; we hex-dump the first ~32 bytes so we
        //MIRASU   can compare what Kronos sends vs what HandleSpellStartOrGo's packed-guid read
        //MIRASU   was working with. If GUID format differs, the synthesized CastID won't match
        //MIRASU   the cast bar's CastID and the modern client won't reset it.
        byte[] rawData = packet.GetData() ?? Array.Empty<byte>();
        string rawHex = Convert.ToHexString(rawData, 0, Math.Min(rawData.Length, 32));

        WowGuid128 casterUnit;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            casterUnit = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            casterUnit = packet.ReadGuid().To128(GetSession().GameState);

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // Cast Count

        uint spellId = packet.ReadUInt32();
        byte reason = 61;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            reason = (byte)LegacyVersion.ConvertSpellCastResult(packet.ReadUInt8());

        WowGuid128 castId;
        uint spellVisual;
        // Try to find pending cast info (peek, don't remove - this is informational).
        // Match by either modern SpellId or LegacySpellId for SoM-renumbered items.
        if (GetSession().GameState.CurrentPlayerGuid == casterUnit &&
            GetSession().GameState.PendingNormalCasts.FirstOrDefault(c => c.SpellId == spellId || (c.LegacySpellId != 0 && c.LegacySpellId == spellId)) is { } pendingNormal)
        {
            castId = pendingNormal.ServerGUID;
            spellVisual = pendingNormal.SpellXSpellVisualId;
            if (pendingNormal.LegacySpellId != 0)
                spellId = pendingNormal.SpellId;
        }
        else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                 GetSession().GameState.PendingPetCasts.FirstOrDefault(c => c.SpellId == spellId || (c.LegacySpellId != 0 && c.LegacySpellId == spellId)) is { } pendingPet)
        {
            castId = pendingPet.ServerGUID;
            spellVisual = pendingPet.SpellXSpellVisualId;
            if (pendingPet.LegacySpellId != 0)
                spellId = pendingPet.SpellId;
        }
        else
        {
            //MIRASU - Non-player caster: prefer the unique CastID minted at SPELL_START so
            //MIRASU   the dismiss references the same in-flight cast the modern client is
            //MIRASU   tracking. Falls back to the deterministic seed if no active cast was
            //MIRASU   recorded (e.g. SPELL_START was missed or arrived out of order).
            var activeKey = (casterUnit, spellId);
            if (GetSession().GameState.OtherCasterActiveCastIds.TryRemove(activeKey, out var trackedCastId))
                castId = trackedCastId;
            else
                castId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, spellId, spellId + casterUnit.GetCounter());
            spellVisual = GameData.GetSpellVisual(spellId);
        }

        bool casterIsPlayer = GetSession().GameState.CurrentPlayerGuid == casterUnit;
        bool casterIsPet = GetSession().GameState.CurrentPetGuid == casterUnit;

        SpellFailure spell = new SpellFailure();
        spell.CasterUnit = casterUnit;
        spell.CastID = castId;
        spell.SpellID = spellId;
        spell.SpellXSpellVisualID = spellVisual;
        spell.Reason = reason;
        SendPacketToClient(spell);

        SpellFailedOther spell2 = new SpellFailedOther();
        spell2.CasterUnit = casterUnit;
        spell2.CastID = castId;
        spell2.SpellID = spellId;
        spell2.SpellXSpellVisualID = spellVisual;
        spell2.Reason = reason;
        SendPacketToClient(spell2);

        //MIRASU - For interrupted (kicked/counterspelled) mob casts, modern clients dismiss
        //MIRASU   the target-frame cast bar via SMSG_SPELL_INTERRUPT_LOG and a
        //MIRASU   SMSG_CANCEL_SPELL_VISUAL terminating the active visual kit. Vanilla 1.12
        //MIRASU   has neither opcode -- the proxy synthesizes both. We attribute the
        //MIRASU   interrupt to the local player (reliably the Kick/Counterspell source on
        //MIRASU   the JSONL stream) and resolve the SpellVisualID from the wrapper ID via
        //MIRASU   the SpellXSpellVisualToSpellVisual table loaded at startup.
        bool sentInterruptLog = false;
        bool sentCancelVisual = false;
        uint resolvedSpellVisualId = 0;
        if (reason == 61 /* Interrupted */ && !casterIsPlayer && !casterIsPet)
        {
            SpellInterruptLog interruptLog = new SpellInterruptLog();
            interruptLog.Caster = GetSession().GameState.CurrentPlayerGuid;
            interruptLog.Victim = casterUnit;
            interruptLog.InterruptedSpellID = (int)spellId;
            //MIRASU - BackfireSpellID is the school-lockout dummy spell in retail. We don't
            //MIRASU   know the actual lockout ID from 1.12 wire (Kronos doesn't send it), so
            //MIRASU   loop the interrupted spell back. Theory: client may gate the
            //MIRASU   target-frame cast-bar dismiss on BackfireSpellID != 0, treating 0 as
            //MIRASU   "informational interrupt" that doesn't actually break the bar.
            interruptLog.BackfireSpellID = (int)spellId;
            SendPacketToClient(interruptLog);
            sentInterruptLog = true;

            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
            }
        }

        Log.Event("spell.failed_other.routed", new
        {
            spellId,
            reason,
            rawHex,
            casterCounter = casterUnit.GetCounter(),
            castIdCounter = castId.GetCounter(),
            casterIsPlayer,
            casterIsPet,
            sentInterruptLog,
            sentCancelVisual,
            resolvedSpellVisualId,
        });
    }

    // JimsProxy: SMSG_SPELL_FAILURE handler. Block 2 gameplay testing on Kronos
    // surfaced this as packet.untranslated 7x in a 10-min priest leveling session
    // (Smite/Heal failing for OOM, OOR, LoS). Without this, the modern client
    // never sees the cast failure -- cast bar doesn't reset, button stays in
    // "casting" state until the next valid cast. Spells feel unresponsive.
    //
    // SMSG_SPELL_FAILURE is the caster's direct notification (always sent to
    // the caster); SMSG_SPELL_FAILED_OTHER is the broadcast variant for nearby
    // players. Both opcodes carry essentially the same data; mirror the
    // FAILED_OTHER handler logic but read the 1.12 wire format which includes
    // the reason byte (FAILED_OTHER drops it because the broadcast variant in
    // 1.12 didn't carry it consistently).
    [PacketHandler(Opcode.SMSG_SPELL_FAILURE)]
    void HandleSpellFailure(WorldPacket packet)
    {
        WowGuid128 casterUnit;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            casterUnit = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            casterUnit = packet.ReadGuid().To128(GetSession().GameState);

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // Cast Count

        uint spellId = packet.ReadUInt32();

        // 1.12 SMSG_SPELL_FAILURE wire format does include the reason byte
        // (caster-direct notification, not the broadcast variant). Read and
        // translate it via the same conversion table the modern client expects.
        byte reason = 61; // SPELL_FAILED_UNKNOWN fallback
        if (packet.CanRead())
            reason = (byte)LegacyVersion.ConvertSpellCastResult(packet.ReadUInt8());

        // Resolve cast/visual ID from pending cast bookkeeping. For the caster path,
        // DEQUEUE the pending cast (don't peek): SMSG_SPELL_GO won't arrive for an
        // interrupted/failed cast, so leaving it in the queue causes subsequent casts
        // of the same spell to inherit the stale ServerGUID via FirstOrDefault FIFO
        // matching -- the modern client's active cast bar (keyed on the NEW ServerGUID)
        // then doesn't match the SpellFailure CastID and the bar finishes filling
        // visually instead of resetting on interrupt. Mirrors HandleCastFailed.
        // JimsProxy: vmangos / Twinstar's Spell::SendInterrupted hardcodes the SMSG_SPELL_FAILURE
        // reason byte to 0 (a generic "interrupted" signal) and follows it ~0-5ms later with a
        // SMSG_CAST_FAILED carrying the real reason. On the modern 1.14 client, SMSG_SPELL_FAILURE
        // for the LOCAL caster is interpreted as "in-flight cast was interrupted" -- which latches
        // the action-bar GCD anticipation as committed. By the time SMSG_CAST_FAILED arrives with
        // pre-cast-failure semantics that would cancel the GCD, the lock has already been
        // committed and the button stays greyed for the full 1.5s. Skip the SpellFailure forward
        // for local-player/pet casts so the canonical SMSG_CAST_FAILED is the only signal the
        // client receives -- it dismisses the cast bar AND cancels the GCD anticipation. Other
        // casters (mobs, remote players) still flow through; the mob-interrupt fix from PR #65
        // depends on that path for target-frame cast-bar dismiss on Kick / Counterspell.
        bool casterIsLocalPlayer = GetSession().GameState.CurrentPlayerGuid == casterUnit;
        bool casterIsLocalPet    = GetSession().GameState.CurrentPetGuid    == casterUnit;
        // Ranged auto-attack exception (mirrors the SPELL_START allowlist in
        // HandleSpellStart): for Auto Shot (75) / Shoot (5019), the modern
        // client requires SMSG_SPELL_FAILURE to cancel the bow-draw / wand-aim
        // animation. Suppressing it leaves the bow drawn forever after a single
        // out-of-range / LoS rejection, which the user sees as
        //   "animation ready but the shot never fires".
        // CAST_FAILED + CANCEL_AUTO_REPEAT alone are not enough to retract the
        // ranged-attack visual on 1.14 — observed in the 2026-04-28 hunter
        // bundle where every Auto Shot SPELL_START was paired with a suppressed
        // SPELL_FAILURE reason=1 and the bow stuck drawn.
        bool isRangedAutoAttack = spellId == 75 || spellId == 5019;
        if ((casterIsLocalPlayer || casterIsLocalPet) && !isRangedAutoAttack)
        {
            Log.Event("spell.failure.suppressed_for_caster", new
            {
                spellId,
                raw_reason_byte = reason,
                is_pet = casterIsLocalPet,
            });
            return;
        }
        if (isRangedAutoAttack && (casterIsLocalPlayer || casterIsLocalPet))
        {
            Log.Event("spell.failure.ranged_auto_forwarded", new
            {
                spellId,
                raw_reason_byte = reason,
                is_pet = casterIsLocalPet,
            });
        }

        WowGuid128 castId;
        uint spellVisual;
        bool dequeued = false;
        bool wasStarted = false;
        bool foundActiveCastId = false;
        if (GetSession().GameState.CurrentPlayerGuid == casterUnit &&
            GetSession().GameState.TryDequeuePendingNormalCast(spellId, out var pendingNormal))
        {
            castId = pendingNormal!.ServerGUID;
            spellVisual = pendingNormal.SpellXSpellVisualId;
            if (pendingNormal.LegacySpellId != 0)
                spellId = pendingNormal.SpellId;
            dequeued = true;
            wasStarted = pendingNormal.HasStarted;

            // Pre-cast failure (rare via SPELL_FAILURE -- usually goes through
            // CAST_FAILED, but cover it for parity): client needs SpellPrepare
            // to match the upcoming failure to its pending cast slot.
            if (!pendingNormal.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = pendingNormal.ClientGUID;
                prepare.ServerCastID = pendingNormal.ServerGUID;
                SendPacketToClient(prepare);
            }
        }
        else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                 GetSession().GameState.TryDequeuePendingPetCast(spellId, out var pendingPet))
        {
            castId = pendingPet!.ServerGUID;
            spellVisual = pendingPet.SpellXSpellVisualId;
            if (pendingPet.LegacySpellId != 0)
                spellId = pendingPet.SpellId;
            dequeued = true;
            wasStarted = pendingPet.HasStarted;

            if (!pendingPet.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = pendingPet.ClientGUID;
                prepare.ServerCastID = pendingPet.ServerGUID;
                SendPacketToClient(prepare);
            }
        }
        else
        {
            //MIRASU - Non-local caster (enemy player in PvP, or any third-party unit). Vanilla
            //MIRASU   1.12 broadcasts SMSG_SPELL_FAILURE to ALL nearby observers via
            //MIRASU   SendMessageToSet(true), not just the caster. So when you Counterspell/Kick
            //MIRASU   an enemy player, this handler runs for THEIR cast. Pull the unique CastID
            //MIRASU   minted at SPELL_START from OtherCasterActiveCastIds so the dismiss
            //MIRASU   references the same in-flight cast the modern client is tracking;
            //MIRASU   otherwise the deterministic seed mismatches and the target-frame cast bar
            //MIRASU   keeps filling until movement triggers a separate dismiss path.
            var activeKey = (casterUnit, spellId);
            foundActiveCastId = GetSession().GameState.OtherCasterActiveCastIds.TryRemove(activeKey, out var trackedCastId);
            if (foundActiveCastId)
                castId = trackedCastId;
            else
                castId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, spellId, spellId + casterUnit.GetCounter());
            spellVisual = GameData.GetSpellVisual(spellId);
        }

        SpellFailure spell = new SpellFailure();
        spell.CasterUnit = casterUnit;
        spell.CastID = castId;
        spell.SpellID = spellId;
        spell.SpellXSpellVisualID = spellVisual;
        spell.Reason = reason;
        SendPacketToClient(spell);

        //MIRASU - Mirror the FAILED_OTHER interrupt synthesis for the broadcasted FAILURE path.
        //MIRASU   PvP scenario: you Counterspell an enemy player -> Kronos broadcasts
        //MIRASU   SMSG_SPELL_FAILURE for the victim to nearby observers. Without these synthesized
        //MIRASU   modern packets the 1.14 target-frame cast bar doesn't dismiss until movement.
        //MIRASU   Gate on foundActiveCastId so we don't double-fire if FAILED_OTHER already
        //MIRASU   consumed the entry and synthesized the same packets.
        bool casterIsPlayer = GetSession().GameState.CurrentPlayerGuid == casterUnit;
        bool casterIsPet = GetSession().GameState.CurrentPetGuid == casterUnit;
        bool sentInterruptLog = false;
        bool sentCancelVisual = false;
        uint resolvedSpellVisualId = 0;
        if (reason == 61 /* Interrupted */ && foundActiveCastId && !casterIsPlayer && !casterIsPet)
        {
            SpellInterruptLog interruptLog = new SpellInterruptLog();
            interruptLog.Caster = GetSession().GameState.CurrentPlayerGuid;
            interruptLog.Victim = casterUnit;
            interruptLog.InterruptedSpellID = (int)spellId;
            interruptLog.BackfireSpellID = (int)spellId;
            SendPacketToClient(interruptLog);
            sentInterruptLog = true;

            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
            }
        }

        Log.Event("spell.failure.routed", new
        {
            spellId,
            reason,
            isCaster = GetSession().GameState.CurrentPlayerGuid == casterUnit,
            isPetCaster = GetSession().GameState.CurrentPetGuid == casterUnit,
            dequeued,
            wasStarted,
            foundActiveCastId,
            sentInterruptLog,
            sentCancelVisual,
            resolvedSpellVisualId,
        });
    }

    [PacketHandler(Opcode.SMSG_SPELL_START)]
    void HandleSpellStart(WorldPacket packet)
    {
        if (GetSession().GameState.CurrentMapId == null)
            return;

        SpellStart spell = new SpellStart();
        spell.Cast = HandleSpellStartOrGo(packet, false);

        bool casterIsLocalPlayer = GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit;
        bool casterIsLocalPet    = GetSession().GameState.CurrentPetGuid    == spell.Cast.CasterUnit;

        // Mark pending cast as started (queue-based, FIFO order)
        if (casterIsLocalPlayer &&
            GetSession().GameState.TryMarkPendingNormalCastStarted((uint)spell.Cast.SpellID, out var pendingCast))
        {
            spell.Cast.CastID = pendingCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = pendingCast.SpellXSpellVisualId;
            // SoM-renumbered item: rewrite the legacy spell id back to the modern one the client expects.
            if (pendingCast.LegacySpellId != 0)
                spell.Cast.SpellID = (int)pendingCast.SpellId;
            // JimsProxy issue #43 follow-up: capture the wire-reported cast time so the
            // GCD-hold gate in HandleSpellGo can tell instant casts (Kronos emits
            // SMSG_SPELL_START even for these) apart from real cast-time spells.
            pendingCast.StartedCastTimeMs = spell.Cast.CastTime;

            SpellPrepare prepare = new();
            prepare.ClientCastID = pendingCast.ClientGUID;
            prepare.ServerCastID = spell.Cast.CastID;
            SendPacketToClient(prepare);

            // Clear non-started casts and send failures for them
            // (keeps the started cast so SPELL_GO can dequeue it)
            var failedCasts = GetSession().GameState.ClearNonStartedNormalCasts();
            foreach (var failed in failedCasts)
                GetSession().InstanceSocket.SendCastRequestFailed(failed, false);
        }
        else if (casterIsLocalPet &&
                 GetSession().GameState.TryMarkPendingPetCastStarted((uint)spell.Cast.SpellID, out var pendingPetCast))
        {
            spell.Cast.CastID = pendingPetCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = pendingPetCast.SpellXSpellVisualId;
            if (pendingPetCast.LegacySpellId != 0)
                spell.Cast.SpellID = (int)pendingPetCast.SpellId;

            SpellPrepare prepare = new();
            prepare.ClientCastID = pendingPetCast.ClientGUID;
            prepare.ServerCastID = spell.Cast.CastID;
            SendPacketToClient(prepare);

            // Clear non-started pet casts and send failures for them
            var failedPetCasts = GetSession().GameState.ClearNonStartedPetCasts();
            foreach (var failed in failedPetCasts)
                GetSession().InstanceSocket.SendCastRequestFailed(failed, true);
        }

        // JimsProxy: suppress SMSG_SPELL_START forward for the LOCAL player/pet's INSTANT
        // casts (CastTime == 0). Twinstar / vmangos emit SPELL_START before running the
        // LoS / range / target validation that ultimately rejects the cast a few hundred
        // ms later; once the modern 1.14 client processes a SPELL_START for an in-flight
        // cast it commits the action-bar GCD anticipation and won't roll it back when the
        // subsequent SMSG_CAST_FAILED arrives. By withholding SPELL_START for instants
        // we let the failure path (HandleCastFailed → SpellPrepare + CastFailed) cancel
        // the GCD cleanly. Successful instants still apply real GCD via SPELL_GO. Cast-
        // time spells (CastTime > 0) and NPC casts forward as before — for them, GCD has
        // genuinely been committed server-side and the cast-bar visual is needed.
        // Ranged auto-attack exception: spells like Auto Shot (75) and Shoot (5019)
        // are instant casts where SMSG_SPELL_START is what plays the visible bow
        // draw / wand aim animation. Suppressing it leaves the player firing
        // invisibly — the shots still hit (via SPELL_GO) but the character never
        // animates, which is what the user observed for Hunter Auto Shot.
        // Whitelist these so they bypass the issue-#43 instant-suppression.
        bool isRangedAutoAttack = spell.Cast.SpellID == 75      // Auto Shot
                                  || spell.Cast.SpellID == 5019; // Shoot (Wand)
        bool isChanneled = GameData.IsChanneledSpell((uint)spell.Cast.SpellID);
        bool suppressInstantStart = (casterIsLocalPlayer || casterIsLocalPet)
                                    && spell.Cast.CastTime == 0
                                    && !isRangedAutoAttack
                                    && !isChanneled;
        if (suppressInstantStart)
        {
            Log.Event("spell.start.instant_suppressed", new
            {
                spell_id = spell.Cast.SpellID,
                cast_time_ms = spell.Cast.CastTime,
                caster_is_player = casterIsLocalPlayer,
                caster_is_pet = casterIsLocalPet,
            });
            return;
        }
        if (isRangedAutoAttack && (casterIsLocalPlayer || casterIsLocalPet) && spell.Cast.CastTime == 0)
        {
            Log.Event("spell.start.ranged_auto_forwarded", new
            {
                spell_id = spell.Cast.SpellID,
                caster_is_player = casterIsLocalPlayer,
                caster_is_pet = casterIsLocalPet,
            });
        }
        if (isChanneled && (casterIsLocalPlayer || casterIsLocalPet) && spell.Cast.CastTime == 0)
        {
            Log.Event("spell.start.channel_forwarded", new
            {
                spell_id = spell.Cast.SpellID,
                caster_is_player = casterIsLocalPlayer,
                caster_is_pet = casterIsLocalPet,
            });
        }

        SendPacketToClient(spell);

        // Send cast-time sideband for non-self casters so the addon gets
        // the server-reported cast time instead of GetSpellInfo() which
        // returns the observer's own modified value (wrong rank/talents).
        if (spell.Cast.CasterUnit != GetSession().GameState.CurrentPlayerGuid &&
            spell.Cast.CasterUnit != GetSession().GameState.CurrentPetGuid &&
            spell.Cast.CastTime > 0)
        {
            string guidStr = spell.Cast.CasterUnit.ToClientGuidString();
            var chatPkt = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                $"JP_CS:{guidStr}:{spell.Cast.SpellID}:{spell.Cast.CastTime}");
            SendPacketToClient(chatPkt);
        }
    }

    [PacketHandler(Opcode.SMSG_SPELL_GO)]
    void HandleSpellGo(WorldPacket packet)
    {
        if (GetSession().GameState.CurrentMapId == null)
            return;

        SpellGo spell = new SpellGo();
        spell.Cast = HandleSpellStartOrGo(packet, true);

        // Dequeue completed cast (queue-based, FIFO order)
        if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
            GetSession().GameState.TryDequeuePendingNormalCast((uint)spell.Cast.SpellID, out var pendingCast))
        {
            spell.Cast.CastID = pendingCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = pendingCast.SpellXSpellVisualId;
            // SoM-renumbered item: rewrite the legacy spell id back to the modern one the client expects.
            if (pendingCast.LegacySpellId != 0)
                spell.Cast.SpellID = (int)pendingCast.SpellId;

            // For instant spells that skip SPELL_START, we need to send SpellPrepare
            // before SpellGo so the client knows which cast completed
            if (!pendingCast.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = pendingCast.ClientGUID;
                prepare.ServerCastID = spell.Cast.CastID;
                SendPacketToClient(prepare);
            }

            // JimsProxy (issue #43): start the local GCD hold window only for INSTANT on-GCD
            // player casts on vanilla. Matching a pending queue entry filters out proc /
            // triggered SMSG_SPELL_GOs (e.g. Windfury Weapon, weapon enchant procs, Thunderfury
            // chain lightning, Hand of Justice) — those have no CMSG_CAST_SPELL from the
            // client, so PendingNormalCasts has no matching entry. Real cast-time spells
            // (CastTime > 0) had their server-side GCD start at SMSG_SPELL_START, so by the
            // time SMSG_SPELL_GO arrives the GCD has already expired during the cast — starting
            // a fresh 1500ms hold here would be a spurious post-cast delay. Finally, gating on
            // ExpansionVersion==1 keeps this vanilla-only; the whitelist CSV is vanilla-only,
            // and haste-adjusted GCDs on TBC+ would make the blanket 1500ms wrong.
            //
            // JimsProxy issue #43 follow-up: gate on StartedCastTimeMs == 0 (instant) instead
            // of !HasStarted. Kronos 1.12 emits SMSG_SPELL_START even for instants (Arcane
            // Explosion, Counterspell, etc.), so the old !HasStarted gate caused BeginGcd to
            // be skipped for every instant cast → no GCD hold → mid-GCD mashes flooded Kronos
            // and bounced back as SMSG_SPELL_FAILURE. Bug bundle showed 4 NOT_READY failures
            // per AE GCD on 2026-04-26.
            //
            // JimsProxy (issue #43): use the legacy (1.12) spell id for whitelist / GCD-duration
            // lookups when the cast was SoM-renumbered. The rewrite a few lines above already
            // changed spell.Cast.SpellID to the modern id for the outbound packet, but
            // OffGcdSpells and Spell1sGcd are keyed on legacy ids (the CSVs are vanilla Spell.dbc
            // extracts). This is defensive: any future SoM-renumbered spell that the DBC marks
            // off-GCD would silently miss the whitelist without this fallback. (Most currently
            // renumbered on-use items, e.g. Diamond Flask 24427, are on-GCD per the 1.12 DBC
            // — so today this branch is mostly an insurance policy, not load-bearing.)
            uint gcdLookupId = pendingCast.LegacySpellId != 0
                ? pendingCast.LegacySpellId
                : (uint)spell.Cast.SpellID;

            if (LegacyVersion.ExpansionVersion == 1 &&
                pendingCast.StartedCastTimeMs == 0 &&
                !GameData.IsOffGcd(gcdLookupId))
            {
                long gcdMs = GameData.GetGcdDurationMs(gcdLookupId);
                long now = Environment.TickCount64;
                long expireAt = now + gcdMs;
                int adaptiveOffset = GetSession().GameState.GetAdaptiveFireOffsetMs();
                long fireAt = expireAt - adaptiveOffset;
                GetSession().GameState.BeginGcd(expireAt, fireAt);

                Log.Event("gcd.begin", new
                {
                    spell_id = spell.Cast.SpellID,
                    gcd_ms = gcdMs,
                    fire_offset_ms = adaptiveOffset,
                    smoothed_rtt_ms = GetSession().GameState.GetSmoothedRttMs(),
                    legacy_lookup_id = pendingCast.LegacySpellId,
                });
            }
        }
        else if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
            GetSession().GameState.CurrentClientNextMeleeCast != null &&
            GetSession().GameState.CurrentClientNextMeleeCast!.SpellId == spell.Cast.SpellID)
        {
            spell.Cast.CastID = GetSession().GameState.CurrentClientNextMeleeCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = GetSession().GameState.CurrentClientNextMeleeCast!.SpellXSpellVisualId;
            GetSession().GameState.CurrentClientNextMeleeCast = null;
        }
        else if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
            GetSession().GameState.CurrentClientAutoRepeatCast != null &&
            GetSession().GameState.CurrentClientAutoRepeatCast!.SpellId == spell.Cast.SpellID)
        {
            spell.Cast.CastID = GetSession().GameState.CurrentClientAutoRepeatCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = GetSession().GameState.CurrentClientAutoRepeatCast!.SpellXSpellVisualId;
            // Note: Don't clear auto-repeat cast here - it stays active until cancelled
        }
        else if (GetSession().GameState.CurrentPetGuid == spell.Cast.CasterUnit &&
                 GetSession().GameState.TryDequeuePendingPetCast((uint)spell.Cast.SpellID, out var pendingPetCast))
        {
            spell.Cast.CastID = pendingPetCast!.ServerGUID;
            spell.Cast.SpellXSpellVisualID = pendingPetCast.SpellXSpellVisualId;
            if (pendingPetCast.LegacySpellId != 0)
                spell.Cast.SpellID = (int)pendingPetCast.SpellId;

            // For instant pet spells that skip SPELL_START
            if (!pendingPetCast.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = pendingPetCast.ClientGUID;
                prepare.ServerCastID = spell.Cast.CastID;
                SendPacketToClient(prepare);
            }
        }

        if (!spell.Cast.CasterUnit.IsEmpty() && GameData.AuraSpells.Contains((uint)spell.Cast.SpellID))
        {
            uint spellId = (uint)spell.Cast.SpellID;

            foreach (WowGuid128 target in spell.Cast.HitTargets)
            {
                // Check if this is an aura refresh (target already has this aura)
                var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(target);
                if (updateFields != null)
                {
                    int existingSlot = FindAuraSlotBySpellId(target, spellId, updateFields);
                    if (existingSlot >= 0)
                    {
                        // Aura refresh detected - send AuraUpdate to refresh the duration timer
                        SendAuraRefreshUpdate(target, spellId, spell.Cast.CasterUnit, (byte)existingSlot, updateFields);
                    }
                }

                GetSession().GameState.StoreLastAuraCasterOnTarget(target, spellId, spell.Cast.CasterUnit);
            }
        }

        // Vanilla 1.12 doesn't include SpellCastLogData in SMSG_SPELL_GO, but the
        // modern 1.14 client needs it present to populate CLEU spell IDs. Without it,
        // CombatLogGetCurrentEventInfo() returns spellId=0 for non-self spells, breaking
        // every addon that reads CLEU spell IDs (damage meters, cast bars, WeakAuras).
        if (spell.LogData == null)
            spell.LogData = new SpellCastLogData();

        SendPacketToClient(spell);
    }

    SpellCastData HandleSpellStartOrGo(WorldPacket packet, bool isSpellGo)
    {
        SpellCastData dbdata = new SpellCastData();

        dbdata.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        dbdata.CasterUnit = packet.ReadPackedGuid().To128(GetSession().GameState);

        // Queue-based spell tracking replaces the need for artificial delay.
        // The old Thread.Sleep workaround was needed because single-variable tracking
        // would get overwritten when spamming spells, causing CastID mismatches.

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.ReadUInt8(); // cast count

        dbdata.SpellID = packet.ReadInt32();
        dbdata.SpellXSpellVisualID = GameData.GetSpellVisual((uint)dbdata.SpellID);

        //MIRASU - For non-player/non-pet casters, we previously generated a deterministic
        //MIRASU   CastID (spellId+casterCounter) which was identical for every cast of the
        //MIRASU   same spell by the same mob. Modern clients assume CastIDs are unique per
        //MIRASU   cast; reusing the ID caused visual chunks to drift, sounds to clip, and
        //MIRASU   target-frame cast bars to ignore the dismiss on Kick interrupts.
        //MIRASU   We now mint a unique CastID per SPELL_START and recall it on SPELL_GO so
        //MIRASU   the entire cast lifecycle references one consistent ID.
        var gameState = GetSession().GameState;
        bool casterIsPlayer = dbdata.CasterUnit == gameState.CurrentPlayerGuid;
        bool casterIsPet = dbdata.CasterUnit == gameState.CurrentPetGuid;
        if (!casterIsPlayer && !casterIsPet)
        {
            var key = (dbdata.CasterUnit, (uint)dbdata.SpellID);
            if (isSpellGo && gameState.OtherCasterActiveCastIds.TryRemove(key, out var existingCastId))
            {
                // Cast started before; reuse the same CastID assigned at SPELL_START.
                dbdata.CastID = existingCastId;
            }
            else
            {
                // SPELL_START (or instant SPELL_GO with no prior start): mint a fresh unique ID.
                uint sequence = (uint)Interlocked.Increment(ref gameState.OtherCastSequenceCounter);
                ulong uniqueLow = ((ulong)sequence << 32) | (uint)((uint)dbdata.SpellID + dbdata.CasterUnit.GetCounter());
                dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, uniqueLow);
                if (!isSpellGo)
                    gameState.OtherCasterActiveCastIds[key] = dbdata.CastID;
            }
        }
        else
        {
            if (isSpellGo)
            {
                // Player/pet SPELL_GO: unique CastID per packet. Channeled tick spells
                // (Arcane Missiles 7269) don't match PendingNormalCasts — without unique
                // IDs every tick shares the same CastID and the client drops a missile.
                // For casts that DO match, HandleSpellGo overwrites with ServerGUID anyway.
                uint seq = (uint)Interlocked.Increment(ref gameState.PlayerChildCastSequence);
                dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, ((ulong)seq << 32) | (uint)((uint)dbdata.SpellID + dbdata.CasterUnit.GetCounter()));
            }
            else
            {
                // Player/pet SPELL_START: deterministic seed, overridden by ServerGUID on GO.
                dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, (ulong)dbdata.SpellID + dbdata.CasterUnit.GetCounter());
            }
        }

        // JimsProxy: emit structured spell.cast event so we can diagnose spell-ID
        // and visual-kit lookup issues without parsing .pkt files. Particularly
        // useful for comparing what different 1.12 servers (Kronos vs Ashen-wow)
        // send for the same spell -- on Kronos PW:Shield doesn't render the
        // bubble visual; on Ashen-wow it does. If spell_visual_id=0 here, the
        // CSV lookup failed (either wrong spellId from server or missing row).
        // Emitted for both SMSG_SPELL_START and SMSG_SPELL_GO (shared codepath).
        Log.Event("spell.cast", new
        {
            direction = "s2c",
            phase = isSpellGo ? "go" : "start",
            spell_id = dbdata.SpellID,
            spell_visual_id = dbdata.SpellXSpellVisualID,
            visual_lookup_missing = dbdata.SpellXSpellVisualID == 0,
            caster_guid = dbdata.CasterGUID.ToString(),
            caster_is_player = dbdata.CasterGUID == GetSession().GameState.CurrentPlayerGuid,
            casterCounter = dbdata.CasterUnit.GetCounter(), //MIRASU - lets us correlate with spell.failed_other.routed
            castIdCounter = dbdata.CastID.GetCounter(),     //MIRASU - this is the CastID the modern client tracks
        });

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) && LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056) && !isSpellGo)
            packet.ReadUInt8(); // cast count

        uint flags;
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            flags = packet.ReadUInt32();
        else
            flags = packet.ReadUInt16();
        dbdata.CastFlags = flags;

        if (!isSpellGo || LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            dbdata.CastTime = packet.ReadUInt32();

        if (isSpellGo)
        {
            var hitCount = packet.ReadUInt8();
            for (var i = 0; i < hitCount; i++)
            {
                WowGuid128 hitTarget = packet.ReadGuid().To128(GetSession().GameState);
                dbdata.HitTargets.Add(hitTarget);
            }

            var missCount = packet.ReadUInt8();
            for (var i = 0; i < missCount; i++)
            {
                WowGuid128 missTarget = packet.ReadGuid().To128(GetSession().GameState);
                SpellMissInfo missType = (SpellMissInfo)packet.ReadUInt8();
                SpellMissInfo reflectType = SpellMissInfo.None;
                if (missType == SpellMissInfo.Reflect)
                    reflectType = (SpellMissInfo)packet.ReadUInt8();

                dbdata.MissTargets.Add(missTarget);
                dbdata.MissStatus.Add(new SpellMissStatus(missType, reflectType));
            }
        }

        var targetFlags = LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) ?
            (SpellCastTargetFlags)packet.ReadUInt32() : (SpellCastTargetFlags)packet.ReadUInt16();
        dbdata.Target.Flags = targetFlags;

        WowGuid128 unitTarget = WowGuid128.Empty;
        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.GameObject |
            SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet))
            unitTarget = packet.ReadPackedGuid().To128(GetSession().GameState);
        dbdata.Target.Unit = unitTarget;

        WowGuid128 itemTarget = WowGuid128.Empty;
        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem))
            itemTarget = packet.ReadPackedGuid().To128(GetSession().GameState);
        dbdata.Target.Item = itemTarget;

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
        {
            dbdata.Target.SrcLocation = new TargetLocation();
            dbdata.Target.SrcLocation.Transport = WowGuid128.Empty;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                dbdata.Target.SrcLocation.Transport = packet.ReadPackedGuid().To128(GetSession().GameState);

            dbdata.Target.SrcLocation.Location = packet.ReadVector3();
        }

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
        {
            dbdata.Target.DstLocation = new TargetLocation();
            dbdata.Target.DstLocation.Transport = WowGuid128.Empty;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                dbdata.Target.DstLocation.Transport = packet.ReadPackedGuid().To128(GetSession().GameState);

            dbdata.Target.DstLocation.Location = packet.ReadVector3();
        }

        if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
            dbdata.Target.Name = packet.ReadCString();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            if (flags.HasAnyFlag(CastFlag.PredictedPower))
            {
                packet.ReadInt32(); // Rune Cooldown
            }

            if (flags.HasAnyFlag(CastFlag.RuneInfo))
            {
                var spellRuneState = packet.ReadUInt8();
                var playerRuneState = packet.ReadUInt8();

                for (var i = 0; i < 6; i++)
                {
                    var mask = 1 << i;
                    if ((mask & spellRuneState) == 0)
                        continue;

                    if ((mask & playerRuneState) != 0)
                        continue;

                    packet.ReadUInt8(); // Rune Cooldown Passed
                }
            }

            if (isSpellGo)
            {
                if (flags.HasAnyFlag(CastFlag.AdjustMissile))
                {
                    dbdata.MissileTrajectory.Pitch = packet.ReadFloat(); // Elevation
                    dbdata.MissileTrajectory.TravelTime = packet.ReadUInt32(); // Delay time
                }
            }
        }

        if (flags.HasAnyFlag(CastFlag.Projectile))
        {
            dbdata.AmmoDisplayId = packet.ReadInt32();
            dbdata.AmmoInventoryType = packet.ReadInt32();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            if (isSpellGo)
            {
                if (flags.HasAnyFlag(CastFlag.VisualChain))
                {
                    packet.ReadInt32();
                    packet.ReadInt32();
                }

                if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
                    packet.ReadInt8(); // Some count

                if (targetFlags.HasAnyFlag(SpellCastTargetFlags.ExtraTargets))
                {
                    var targetCount = packet.ReadInt32();
                    if (targetCount > 0)
                    {
                        TargetLocation location = new();
                        for (var i = 0; i < targetCount; i++)
                        {
                            location.Location = packet.ReadVector3();
                            location.Transport = packet.ReadGuid().To128(GetSession().GameState);
                        }
                        dbdata.TargetPoints.Add(location);
                    }
                }
            }
            else
            {
                if (flags.HasAnyFlag(CastFlag.Immunity))
                {
                    dbdata.Immunities.School = packet.ReadUInt32();
                    dbdata.Immunities.Value = packet.ReadUInt32();
                }

                if (flags.HasAnyFlag(CastFlag.HealPrediction))
                {
                    packet.ReadInt32(); // Predicted Spell ID

                    if (packet.ReadUInt8() == 2)
                        packet.ReadPackedGuid();
                }
            }
        }

        return dbdata;
    }

    [PacketHandler(Opcode.SMSG_CANCEL_AUTO_REPEAT)]
    void HandleCancelAutoRepeat(WorldPacket packet)
    {
        // Clear the auto-repeat cast tracking
        GetSession().GameState.CurrentClientAutoRepeatCast = null;

        CancelAutoRepeat cancel = new CancelAutoRepeat();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            cancel.Guid = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            cancel.Guid = GetSession().GameState.CurrentPlayerGuid;
        SendPacketToClient(cancel);
    }

    [PacketHandler(Opcode.SMSG_SPELL_COOLDOWN)]
    void HandleSpellCooldown(WorldPacket packet)
    {
        SpellCooldownPkt cooldown = new();
        try
        {
            cooldown.Caster = packet.ReadGuid().To128(GetSession().GameState);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                cooldown.Flags = packet.ReadUInt8();
            while (packet.CanRead())
            {
                SpellCooldownStruct cd = new();
                cd.SpellID = packet.ReadUInt32();
                cd.ForcedCooldown = packet.ReadUInt32();
                cooldown.SpellCooldowns.Add(cd);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // wrong structure from arcemu
            // https://github.com/arcemu/arcemu/blob/2_4_3/src/arcemu-world/Spell.cpp#L1554
            packet.ResetReadPos();
            SpellCooldownStruct cd = new();
            cd.SpellID = packet.ReadUInt32();
            cooldown.Caster = packet.ReadPackedGuid().To128(GetSession().GameState);
            cd.ForcedCooldown = packet.ReadUInt32();
            cooldown.SpellCooldowns.Add(cd);
        }
        SendPacketToClient(cooldown);
    }

    [PacketHandler(Opcode.SMSG_COOLDOWN_EVENT)]
    void HandleCooldownEvent(WorldPacket packet)
    {
        CooldownEvent cooldown = new();
        cooldown.SpellID = packet.ReadUInt32();
        WowGuid64 guid = packet.ReadGuid();
        cooldown.IsPet = guid.GetHighType() == HighGuidType.Pet;
        SendPacketToClient(cooldown);
    }

    [PacketHandler(Opcode.SMSG_CLEAR_COOLDOWN)]
    void HandleClearCooldown(WorldPacket packet)
    {
        ClearCooldown cooldown = new();
        cooldown.SpellID = packet.ReadUInt32();
        WowGuid64 guid = packet.ReadGuid();
        cooldown.IsPet = guid.GetHighType() == HighGuidType.Pet;
        SendPacketToClient(cooldown);
    }

    [PacketHandler(Opcode.SMSG_COOLDOWN_CHEAT)]
    void HandleCooldownCheat(WorldPacket packet)
    {
        CooldownCheat cooldown = new();
        cooldown.Guid = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(cooldown);
    }

    [PacketHandler(Opcode.SMSG_SPELL_NON_MELEE_DAMAGE_LOG)]
    void HandleSpellNonMeleeDamageLog(WorldPacket packet)
    {
        SpellNonMeleeDamageLog spell = new();
        spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.SpellID = packet.ReadUInt32();
        spell.SpellXSpellVisualID = GameData.GetSpellVisual(spell.SpellID);
        spell.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, spell.SpellID, spell.SpellID + spell.CasterGUID.GetCounter());
        spell.Damage = packet.ReadInt32();
        spell.OriginalDamage = spell.Damage;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183))
            spell.Overkill = packet.ReadInt32();
        else
            spell.Overkill = -1;

        byte school = packet.ReadUInt8();
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            school = (byte)(1u << school);

        spell.SchoolMask = school;
        spell.Absorbed = packet.ReadInt32();
        spell.Resisted = packet.ReadInt32();
        spell.Periodic = packet.ReadBool();
        packet.ReadUInt8(); // unused
        spell.ShieldBlock = packet.ReadInt32();
        spell.Flags = (SpellHitType)packet.ReadUInt32();

        bool debugOutput = packet.ReadBool();
        if (debugOutput)
        {
            if (!spell.Flags.HasAnyFlag(SpellHitType.Split))
            {
                if (spell.Flags.HasAnyFlag(SpellHitType.CritDebug))
                {
                    packet.ReadFloat(); // roll
                    packet.ReadFloat(); // needed
                }

                if (spell.Flags.HasAnyFlag(SpellHitType.HitDebug))
                {
                    packet.ReadFloat(); // roll
                    packet.ReadFloat(); // needed
                }

                if (spell.Flags.HasAnyFlag(SpellHitType.AttackTableDebug))
                {
                    packet.ReadFloat(); // miss chance
                    packet.ReadFloat(); // dodge chance
                    packet.ReadFloat(); // parry chance
                    packet.ReadFloat(); // block chance
                    packet.ReadFloat(); // glance chance
                    packet.ReadFloat(); // crush chance
                }
            }
        }

        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_SPELL_EXECUTE_LOG)]
    void HandleSpellExecuteLog(WorldPacket packet)
    {
        // SMSG_SPELL_EXECUTE_LOG is diagnostic-only — nothing here is forwarded to
        // the modern client. So if any byte misalignment / unknown effect format
        // trips a parse exception, we MUST NOT let it bubble up — it would tear
        // down the entire ReceiveLoop and force a random disconnect.
        //
        // Two known triggers found in the wild, both effects that report targetCount
        // > 0 on the wire but emit no per-target payload — falling through to the
        // default GUID-read branch reads off the end of the packet:
        //   - Effect 50 (TRANS_DOOR): Priest Lightwell, Mage portals, etc.
        //   - Effect 69 (DISTRACT): Rogue spell 1725 — anyone in range casting it
        //     would DC every other player.
        // Try/catch wrapper is the durable fix for the next surprise effect type;
        // explicit cases just keep the error log clean.
        // Breadcrumbs for the parse-failed event — capture as much state as we
        // got through before the exception so future captures tell us which spell
        // / effect type we need to whitelist next.
        uint dbgSpellId = 0;
        uint dbgEffectIndex = 0;
        uint dbgEffectType = 0;
        uint dbgTargetCount = 0;
        uint dbgTargetIndex = 0;
        try
        {
            var casterGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
            uint spellId = packet.ReadUInt32();
            dbgSpellId = spellId;
            uint effectCount = packet.ReadUInt32();

            Log.Print(LogType.Server, $"SpellExecuteLog: caster={casterGuid} spell={spellId} effects={effectCount}");

            for (uint i = 0; i < effectCount; i++)
            {
                dbgEffectIndex = i;
                uint effectType = packet.ReadUInt32();
                uint targetCount = packet.ReadUInt32();
                dbgEffectType = effectType;
                dbgTargetCount = targetCount;

                Log.Print(LogType.Server, $"  Effect[{i}]: type={effectType} targets={targetCount}");

                for (uint t = 0; t < targetCount; t++)
                {
                    dbgTargetIndex = t;
                    switch (effectType)
                    {
                        case 8: // POWER_DRAIN
                            var pdGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint pdAmount = packet.ReadUInt32();
                            uint pdPowerType = packet.ReadUInt32();
                            float pdMultiplier = packet.ReadFloat();
                            Log.Print(LogType.Server, $"PowerDrain: target={pdGuid} amount={pdAmount} power={pdPowerType}");
                            break;
                        case 10: // HEAL
                            var hGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint hAmount = packet.ReadUInt32();
                            uint hCrit = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"Heal: target={hGuid} amount={hAmount} crit={hCrit}");
                            break;
                        case 30: // ENERGIZE
                            var eGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint eAmount = packet.ReadUInt32();
                            uint ePowerType = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"Energize: target={eGuid} amount={eAmount} power={ePowerType}");
                            break;
                        case 32: // EXTRA_ATTACKS (vanilla value 32)
                            var eaGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint eaCount = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"ExtraAttacks: target={eaGuid} count={eaCount}");
                            break;
                        case 24: // CREATE_ITEM
                            uint ciItemId = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"CreateItem: item={ciItemId}");
                            break;
                        case 41: // INTERRUPT_CAST
                            var icGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint icSpellId = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"InterruptCast: target={icGuid} spell={icSpellId}");
                            break;
                        case 3: // DUMMY — server-side script triggers the real mechanic.
                                // Paladin Holy Shock (20930), various Judgements, etc.
                                // No per-target payload follows.
                            Log.Print(LogType.Server, $"Dummy(type={effectType}): no target payload");
                            break;
                        case 50: // TRANS_DOOR — Lightwell, Mage Portal, etc. No per-target payload.
                            Log.Print(LogType.Server, $"TransDoor(type={effectType}): no target payload");
                            break;
                        case 69: // DISTRACT — positional spell, no per-target payload.
                            Log.Print(LogType.Server, $"Distract(type={effectType}): no target payload");
                            break;
                        case 77: // SCRIPT_EFFECT — Judgement (20271) and many other script-driven
                                 // spells. Like DUMMY, the real mechanic is server-side; no
                                 // per-target payload on the wire.
                            Log.Print(LogType.Server, $"ScriptEffect(type={effectType}): no target payload");
                            break;
                        case 56:  // SUMMON_PET — pet summon, server-side, no per-target payload
                                  // (observed for spell 883 = Call Pet).
                        case 63:  // TAMECREATURE (Kronos numbering) — observed for spell 1515 =
                                  // Tame Beast. The tamed creature is summoned via the normal
                                  // pet path; this log entry has no per-target payload.
                        case 102: // observed for spell 2641 = Dismiss Pet — server-side, no payload.
                        case 104: // observed for spell 14311 / various Hunter abilities — Kronos
                                  // emits these without a per-target GUID payload.
                            Log.Print(LogType.Server, $"NoPayload(type={effectType}): no target payload");
                            break;
                        case 101: // FEED_PET
                            uint fpItemId = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"FeedPet: item={fpItemId}");
                            break;
                        case 113: // DURABILITY_DAMAGE
                            var ddGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint ddItemId = packet.ReadUInt32();
                            uint ddAmount = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"DurabilityDmg: target={ddGuid} item={ddItemId} amount={ddAmount}");
                            break;
                        default: // INSTAKILL, RESURRECT, DISPEL, SUMMON, etc — just a GUID
                            var defaultGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            Log.Print(LogType.Server, $"Default(type={effectType}): target={defaultGuid}");
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // SMSG_SPELL_EXECUTE_LOG is read for diagnostic logging only; an unknown
            // effect format here must never disconnect the player. Log and drop.
            Log.Print(LogType.Error, $"SpellExecuteLog parse failed (non-fatal, packet dropped): spell={dbgSpellId} effectType={dbgEffectType} {ex.GetType().Name}: {ex.Message}");
            Log.Event("combat.execute_log.parse_failed", new
            {
                exception_type = ex.GetType().Name,
                exception_message = ex.Message,
                spell_id = dbgSpellId,
                effect_index = dbgEffectIndex,
                effect_type = dbgEffectType,
                target_count = dbgTargetCount,
                target_index = dbgTargetIndex,
                packet_size = packet.GetSize(),
            });
        }
    }

    [PacketHandler(Opcode.SMSG_SPELL_HEAL_LOG)]
    void HandleSpellHealLog(WorldPacket packet)
    {
        SpellHealLog spell = new();
        spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.SpellID = packet.ReadUInt32();
        spell.HealAmount = packet.ReadInt32();
        spell.OriginalHealAmount = spell.HealAmount;

        bool wireHasOverheal = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183);
        bool wireHasAbsorbed = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192);

        if (wireHasOverheal)
            spell.OverHeal = packet.ReadUInt32();

        if (wireHasAbsorbed)
            spell.Absorbed = packet.ReadUInt32();

        spell.Crit = packet.ReadBool();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            bool debugOutput = packet.ReadBool();
            if (debugOutput)
            {
                spell.CritRollMade = packet.ReadFloat();
                spell.CritRollNeeded = packet.ReadFloat();
            }
        }

        ComputeOverHealFromCache(spell.TargetGUID, spell.HealAmount, wireHasOverheal,
            out uint computedOverHeal, out bool cacheHit, out int cachedHp, out int cachedMaxHp);
        if (!wireHasOverheal)
            spell.OverHeal = computedOverHeal;

        Log.Event("combat.heal.log", new
        {
            spell_id = spell.SpellID,
            target = spell.TargetGUID.ToString(),
            caster = spell.CasterGUID.ToString(),
            heal_amount = spell.HealAmount,
            over_heal_sent = spell.OverHeal,
            absorbed_sent = spell.Absorbed,
            crit = spell.Crit,
            wire_has_overheal = wireHasOverheal,
            wire_has_absorbed = wireHasAbsorbed,
            cache_hit = cacheHit,
            cached_hp = cachedHp,
            cached_max_hp = cachedMaxHp,
        });

        SendPacketToClient(spell);
    }

    // Computes overhealing for a heal event by looking up the target's current HP
    // in our cache (populated from SMSG_UPDATE_OBJECT). On 1.12 servers the wire
    // doesn't carry overheal, so we synthesize it: overheal = max(0, heal - (maxHp - hp)).
    // After computing, we bump the cached HP forward by the effective amount so
    // back-to-back heals (faster than UPDATE_OBJECT can resync) compute accurately.
    // If the cache has no entry for the target (e.g., never received a UPDATE_OBJECT
    // for them), we leave overheal at 0 and don't touch the cache.
    private void ComputeOverHealFromCache(
        WowGuid128 target, int healAmount, bool wireHadOverheal,
        out uint computedOverHeal, out bool cacheHit, out int cachedHp, out int cachedMaxHp)
    {
        computedOverHeal = 0;
        cacheHit = false;
        cachedHp = 0;
        cachedMaxHp = 0;

        if (wireHadOverheal || healAmount <= 0)
            return;

        var cache = GetSession().GameState.UnitHealthCache;
        if (!cache.TryGetValue(target, out var state) || state.MaxHp <= 0)
            return;

        cacheHit = true;
        cachedHp = state.Hp;
        cachedMaxHp = state.MaxHp;

        int missing = state.MaxHp - state.Hp;
        if (missing < 0) missing = 0;
        int effective = Math.Min(healAmount, missing);
        int overheal = healAmount - effective;
        computedOverHeal = (uint)overheal;

        int newHp = state.Hp + effective;
        if (newHp > state.MaxHp) newHp = state.MaxHp;
        cache[target] = (newHp, state.MaxHp);
    }

    [PacketHandler(Opcode.SMSG_SPELL_PERIODIC_AURA_LOG)]
    void HandleSpellPeriodicAuraLog(WorldPacket packet)
    {
        SpellPeriodicAuraLog spell = new();
        spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.SpellID = packet.ReadUInt32();

        // JimsProxy (Rupture-DoT-Lingering-Icon): tag every periodic tick with spell + target so
        // we can pinpoint last-tick-timestamp vs aura-removal-timestamp in the bundle. Remove
        // once the lingering bug is rooted.
        Framework.Logging.Log.Event("aura.tick", new
        {
            target_low = spell.TargetGUID.GetCounter(),
            caster_low = spell.CasterGUID.GetCounter(),
            spell_id = spell.SpellID,
            caster_is_player = spell.CasterGUID == GetSession().GameState.CurrentPlayerGuid,
        });

        var count = packet.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var aura = (AuraType)packet.ReadUInt32();
            switch (aura)
            {
                case AuraType.PeriodicDamage:
                case AuraType.PeriodicDamagePercent:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.Amount = packet.ReadInt32();
                        effect.OriginalDamage = effect.Amount;

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                            effect.OverHealOrKill = packet.ReadUInt32();

                        uint school = packet.ReadUInt32();
                        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                            school = (1u << (byte)school);

                        effect.SchoolMaskOrPower = school;
                        effect.AbsorbedOrAmplitude = packet.ReadUInt32();
                        effect.Resisted = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_2_9901))
                            effect.Crit = packet.ReadBool();

                        spell.Effects.Add(effect);
                        break;
                    }
                case AuraType.PeriodicHeal:
                case AuraType.ObsModHealth:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.Amount = packet.ReadInt32();
                        effect.OriginalDamage = effect.Amount;

                        bool wireHasOverhealHot = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056);

                        if (wireHasOverhealHot)
                            effect.OverHealOrKill = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                            // no idea when this was added exactly
                            effect.AbsorbedOrAmplitude = packet.ReadUInt32();

                        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_2_9901))
                            effect.Crit = packet.ReadBool();

                        ComputeOverHealFromCache(spell.TargetGUID, effect.Amount, wireHasOverhealHot,
                            out uint computedOverHealHot, out bool cacheHitHot, out int cachedHpHot, out int cachedMaxHotHp);
                        if (!wireHasOverhealHot)
                            effect.OverHealOrKill = computedOverHealHot;

                        Log.Event("combat.heal.periodic", new
                        {
                            spell_id = spell.SpellID,
                            target = spell.TargetGUID.ToString(),
                            caster = spell.CasterGUID.ToString(),
                            aura = aura.ToString(),
                            amount = effect.Amount,
                            over_heal_sent = effect.OverHealOrKill,
                            crit = effect.Crit,
                            wire_has_overheal = wireHasOverhealHot,
                            cache_hit = cacheHitHot,
                            cached_hp = cachedHpHot,
                            cached_max_hp = cachedMaxHotHp,
                        });

                        spell.Effects.Add(effect);
                        break;
                    }
                case AuraType.ObsModPower:
                case AuraType.PeriodicEnergize:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.SchoolMaskOrPower = packet.ReadUInt32();
                        effect.Amount = packet.ReadInt32();
                        spell.Effects.Add(effect);
                        break;
                    }
                case AuraType.PeriodicManaLeech:
                    {
                        SpellPeriodicAuraLog.SpellLogEffect effect = new();
                        effect.Effect = (uint)aura;
                        effect.SchoolMaskOrPower = packet.ReadUInt32();
                        effect.Amount = packet.ReadInt32();
                        packet.ReadFloat(); // Gain multiplier
                        spell.Effects.Add(effect);
                        break;
                    }
            }
        }
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_SPELL_ENERGIZE_LOG)]
    void HandleSpellEnergizeLog(WorldPacket packet)
    {
        SpellEnergizeLog spell = new();
        spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        spell.SpellID = packet.ReadUInt32();
        spell.Type = (PowerType)packet.ReadUInt32();
        spell.Amount = packet.ReadInt32();
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_SPELL_DELAYED)]
    void HandleSpellDelayed(WorldPacket packet)
    {
        SpellDelayed delay = new();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            delay.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            delay.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
        delay.Delay = packet.ReadInt32();
        SendPacketToClient(delay);
    }

    [PacketHandler(Opcode.MSG_CHANNEL_START)]
    void HandleSpellChannelStart(WorldPacket packet)
    {
        SpellChannelStart channel = new();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            channel.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            channel.CasterGUID = GetSession().GameState.CurrentPlayerGuid;
        channel.SpellID = packet.ReadUInt32();
        channel.SpellXSpellVisualID = GameData.GetSpellVisual(channel.SpellID);
        channel.Duration = packet.ReadUInt32();
        SendPacketToClient(channel);
    }

    [PacketHandler(Opcode.MSG_CHANNEL_UPDATE)]
    void HandleSpellChannelUpdate(WorldPacket packet)
    {
        SpellChannelUpdate channel = new();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            channel.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        else
            channel.CasterGUID = GetSession().GameState.CurrentPlayerGuid;
        channel.TimeRemaining = packet.ReadInt32();
        SendPacketToClient(channel);
    }

    [PacketHandler(Opcode.SMSG_SPELL_DAMAGE_SHIELD)]
    void HandleSpellDamageShield(WorldPacket packet)
    {
        SpellDamageShield spell = new();
        spell.VictimGUID = packet.ReadGuid().To128(GetSession().GameState);
        spell.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            spell.SpellID = packet.ReadUInt32();
        else
            spell.SpellID = 7294; // Retribution Aura

        spell.Damage = packet.ReadInt32();
        spell.OriginalDamage = spell.Damage;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            spell.OverKill = packet.ReadUInt32();

        uint school = packet.ReadUInt32();
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            school = (1u << (byte)school);

        spell.SchoolMask = school;
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_ENVIRONMENTAL_DAMAGE_LOG)]
    void HandleEnvironmentalDamageLog(WorldPacket packet)
    {
        EnvironmentalDamageLog damage = new();
        damage.Victim = packet.ReadGuid().To128(GetSession().GameState);
        damage.Type = (EnvironmentalDamage)packet.ReadUInt8();
        damage.Amount = packet.ReadInt32();
        damage.Absorbed = packet.ReadInt32();
        damage.Resisted = packet.ReadInt32();
        SendPacketToClient(damage);
    }

    [PacketHandler(Opcode.SMSG_SPELL_INSTAKILL_LOG)]
    void HandleSpellInstakillLog(WorldPacket packet)
    {
        SpellInstakillLog spell = new();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            spell.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
            spell.TargetGUID = packet.ReadGuid().To128(GetSession().GameState);
        }
        else
            spell.CasterGUID = spell.TargetGUID = packet.ReadGuid().To128(GetSession().GameState);
        spell.SpellID = packet.ReadUInt32();
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_SPELL_DISPELL_LOG)]
    void HandleSpellDispellLog(WorldPacket packet)
    {
        // Kronos's vanilla format for this opcode is not fully understood —
        // neither the standard mangos layout nor the collaborator's assumption
        // matches the actual wire bytes. Wrap in try-catch so a parse failure
        // doesn't kill the session (dispel log is cosmetic).
        try
        {
            SpellDispellLog spell = new();
            spell.TargetGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            spell.CasterGUID = packet.ReadPackedGuid().To128(GetSession().GameState);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                spell.DispelledBySpellID = GameData.GetModernSpellId(packet.ReadUInt32());
            }
            else
            {
                spell.DispelledBySpellID = GetSession().GameState.LastDispellSpellId;
            }

            bool hasDebug;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                hasDebug = packet.ReadBool();
            else
                hasDebug = false;

            int count = packet.ReadInt32();
            if (count < 0 || count > 100)
                return; // Sanity check — bad parse, bail without crashing

            for (int i = 0; i < count; i++)
            {
                SpellDispellData dispel = new SpellDispellData();
                dispel.SpellID = GameData.GetModernSpellId(packet.ReadUInt32());
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    dispel.Harmful = packet.ReadBool();
                spell.DispellData.Add(dispel);
            }

            if (hasDebug)
            {
                packet.ReadInt32();
                packet.ReadInt32();
            }

            SendPacketToClient(spell);
        }
        catch
        {
            // Parse failed — Kronos wire format unknown. Dispel still works
            // gameplay-wise (buff is removed server-side); only the combat log
            // entry is lost.
        }
    }

    [PacketHandler(Opcode.SMSG_PLAY_SPELL_VISUAL)]
    void HandlePlaySpellVisualKit(WorldPacket packet)
    {
        PlaySpellVisualKit spell = new();
        spell.Unit = packet.ReadGuid().To128(GetSession().GameState);
        spell.KitRecID = packet.ReadUInt32();
        SendPacketToClient(spell);
    }

    [PacketHandler(Opcode.SMSG_UPDATE_AURA_DURATION)]
    void HandleUpdateAuraDuration(WorldPacket packet)
    {
        byte slot = packet.ReadUInt8();
        int duration = packet.ReadInt32();

        WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
        if (guid == default)
            return;

        GetSession().GameState.StoreAuraDurationLeft(guid, slot, duration, (int)packet.GetReceivedTime());
        GetSession().GameState.StoreAuraDurationFull(guid, slot, duration);
        if (duration <= 0)
            return;

        var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
        if (updateFields == null)
            return;

        AuraInfo aura = new AuraInfo();
        aura.Slot = slot;
        aura.AuraData = ReadAuraSlot(slot, guid, updateFields)!;
        if (aura.AuraData == null)
            return;

        aura.AuraData.Flags |= AuraFlagsModern.Duration;
        aura.AuraData.Duration = duration;
        aura.AuraData.Remaining = duration;

        //MIRASU: Populate CastUnit and set NoCaster when caster is unknown — same rule as
        //MIRASU: the UpdateHandler aura loop. SMSG_UPDATE_AURA_DURATION arrives during login
        //MIRASU: (Kronos refreshes durations on every player-own aura right after
        //MIRASU: SMSG_UPDATE_OBJECT) and ReadAuraSlot doesn't fill CastUnit itself. Without
        //MIRASU: this block the delta SMSG_AURA_UPDATE(updateAll=false) we emit writes
        //MIRASU: CastUnit=default with NoCaster=false — which the 1.14.2 client silently
        //MIRASU: mishandles in its /reload-survivable aura cache. Symptom is buff icons
        //MIRASU: visible during the session but gone after /reload, until the next fresh
        //MIRASU: cast repopulates UnitAuraCaster and a subsequent delta overwrites the
        //MIRASU: polluted slot with a display-valid entry.
        var castUnit = GetSession().GameState.GetAuraCaster(guid, slot, aura.AuraData.SpellID);
        aura.AuraData.CastUnit = castUnit;
        if (castUnit == default)
            aura.AuraData.Flags |= AuraFlagsModern.NoCaster;

        AuraUpdate update = new AuraUpdate(guid, false);
        update.Auras.Add(aura);
        SendPacketToClient(update);
    }

    [PacketHandler(Opcode.SMSG_SET_EXTRA_AURA_INFO)]
    [PacketHandler(Opcode.SMSG_SET_EXTRA_AURA_INFO_NEED_UPDATE)]
    void HandleSetExtraAuraInfo(WorldPacket packet)
    {
        WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);
        if (!packet.CanRead())
            return;

        byte slot = packet.ReadUInt8();
        uint spellId = packet.ReadUInt32();
        int durationFull = packet.ReadInt32();
        int durationLeft = packet.ReadInt32();

        GetSession().GameState.StoreAuraDurationFull(guid, slot, durationFull);
        GetSession().GameState.StoreAuraDurationLeft(guid, slot, durationLeft, (int)packet.GetReceivedTime());

        if (packet.GetUniversalOpcode(false) == Opcode.SMSG_SET_EXTRA_AURA_INFO_NEED_UPDATE)
            GetSession().GameState.StoreAuraCaster(guid, slot, GetSession().GameState.CurrentPlayerGuid);

        if (durationFull <= 0 && durationLeft <= 0)
            return;

        var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
        if (updateFields == null)
            return;

        AuraInfo aura = new AuraInfo();
        aura.Slot = slot;
        aura.AuraData = ReadAuraSlot(slot, guid, updateFields)!;
        if (aura.AuraData == null)
            return;
        if (aura.AuraData.SpellID != spellId)
            return;

        //MIRASU: Set NoCaster when caster lookup returns empty — same rule as the
        //MIRASU: UpdateHandler aura loop and HandleUpdateAuraDuration. SMSG_SET_EXTRA_AURA_INFO
        //MIRASU: is a TBC-style duration push that fires on target aura slots (mob debuffs,
        //MIRASU: party buffs). Without the NoCaster flag on an empty-caster delta the modern
        //MIRASU: client's /reload-survivable aura cache drops the entry.
        var castUnit = GetSession().GameState.GetAuraCaster(guid, slot, spellId);
        aura.AuraData.CastUnit = castUnit;
        if (castUnit == default)
            aura.AuraData.Flags |= AuraFlagsModern.NoCaster;
        aura.AuraData.Flags |= AuraFlagsModern.Duration;
        aura.AuraData.Duration = durationFull;
        aura.AuraData.Remaining = durationLeft;

        AuraUpdate update = new AuraUpdate(guid, false);
        update.Auras.Add(aura);
        SendPacketToClient(update);
    }

    [PacketHandler(Opcode.SMSG_CLEAR_EXTRA_AURA_INFO)]
    void HandleClearExtraAuraInfo(WorldPacket packet)
    {
        // This TBC opcode clears aura duration info for a target.
        // The modern client doesn't use this mechanism - it uses update fields instead.
        // Simply acknowledge the packet without forwarding to the client.
        packet.ReadPackedGuid(); // target guid
    }

    [PacketHandler(Opcode.SMSG_RESURRECT_REQUEST)]
    void HandleResurrectRequest(WorldPacket packet)
    {
        ResurrectRequest revive = new();
        revive.CasterGUID = packet.ReadGuid().To128(GetSession().GameState);
        revive.CasterVirtualRealmAddress = GetSession().RealmId.GetAddress();
        packet.ReadUInt32(); // Name Length
        revive.Name = packet.ReadCString();
        revive.Sickness = packet.ReadBool();
        revive.UseTimer = packet.ReadBool();
        SendPacketToClient(revive);
    }

    [PacketHandler(Opcode.SMSG_TOTEM_CREATED)]
    void HandleTotemCreated(WorldPacket packet)
    {
        TotemCreated totem = new();
        totem.Slot = packet.ReadUInt8();
        totem.Totem = packet.ReadGuid().To128(GetSession().GameState);
        totem.Duration = packet.ReadUInt32();
        totem.SpellId = packet.ReadUInt32();
        SendPacketToClient(totem);
    }

    [PacketHandler(Opcode.SMSG_SET_FLAT_SPELL_MODIFIER)]
    [PacketHandler(Opcode.SMSG_SET_PCT_SPELL_MODIFIER)]
    void HandleSetSpellModifier(WorldPacket packet)
    {
        byte classIndex = packet.ReadUInt8();
        byte modIndex = packet.ReadUInt8();
        int modValue = packet.ReadInt32();

        if (GetSession().GameState.CurrentPlayerCreateTime != 0)
        {
            SetSpellModifier spell = new SetSpellModifier(packet.GetUniversalOpcode(false));
            SpellModifierInfo mod = new SpellModifierInfo();
            SpellModifierData data = new SpellModifierData();
            data.ClassIndex = classIndex;
            mod.ModIndex = modIndex;
            data.ModifierValue = modValue;
            mod.ModifierData.Add(data);
            spell.Modifiers.Add(mod);
            SendPacketToClient(spell);
        }

        if (packet.GetUniversalOpcode(false) == Opcode.SMSG_SET_FLAT_SPELL_MODIFIER)
            GetSession().GameState.SetFlatSpellMod(modIndex, classIndex, modValue);
        else
            GetSession().GameState.SetPctSpellMod(modIndex, classIndex, modValue);
    }

    /// <summary>
    /// Finds the aura slot containing the specified spell on a target.
    /// Returns -1 if the spell is not found in any aura slot.
    /// </summary>
    private int FindAuraSlotBySpellId(WowGuid128 target, uint spellId, Dictionary<int, UpdateField> updateFields)
    {
        int UNIT_FIELD_AURA = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
        if (UNIT_FIELD_AURA < 0)
            return -1;

        int aurasCount = LegacyVersion.GetAuraSlotsCount();
        for (int i = 0; i < aurasCount; i++)
        {
            if (updateFields.TryGetValue(UNIT_FIELD_AURA + i, out var field) && field.UInt32Value == spellId)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Sends an AuraUpdate packet to refresh the duration of an existing aura on a target.
    /// Called when an aura spell is recast on a target that already has the aura.
    /// </summary>
    private void SendAuraRefreshUpdate(WowGuid128 target, uint spellId, WowGuid128 caster, byte slot, Dictionary<int, UpdateField> updateFields)
    {
        AuraDataInfo? auraData = ReadAuraSlot(slot, target, updateFields);
        if (auraData == null || auraData.SpellID != spellId)
        {
            return;
        }

        auraData.CastUnit = caster;

        // For the current player, SMSG_UPDATE_AURA_DURATION will follow with the
        // correct server-authoritative duration (accounting for combo points, talents
        // like Improved Slice and Dice, etc.). Don't include a stale cached duration
        // in this proactive refresh update — it would show the wrong value briefly
        // and compound errors on subsequent refreshes.
        bool isCurrentPlayer = (target == GetSession().GameState.CurrentPlayerGuid);

        if (!isCurrentPlayer)
        {
            // For other targets (enemy debuffs, party buffs), use best-guess duration
            // since they don't receive SMSG_UPDATE_AURA_DURATION.

            // JimsProxy (Rupture-DoT-Lingering-Icon): for vanilla CP-scaling finishers
            // (Rupture, Kidney Shot), the snapshot must win over the cache because each
            // cast can consume a different CP count and rewrite the aura. The cache holds
            // the *previous* cast's duration; without this override a 3 CP recast on a
            // mob that already had a 5 CP Rupture would inherit the stale 16 s. Snapshot
            // returns null for non-finishers, so non-CP-scaling spells skip this branch.
            int? finisherDurationMs = GetSession().GameState.TryGetPendingFinisherDurationMs(spellId, target);
            int durationLeft;
            int durationFull;
            if (finisherDurationMs is int snapMs && snapMs > 0)
            {
                durationFull = snapMs;
                durationLeft = snapMs;
            }
            else
            {
                GetSession().GameState.GetAuraDuration(target, slot, out durationLeft, out durationFull);
                if (durationFull <= 0)
                    durationFull = GameData.GetAuraSpellDuration(spellId);
            }

            if (durationFull > 0)
            {
                auraData.Flags |= AuraFlagsModern.Duration;
                auraData.Duration = durationFull;
                auraData.Remaining = durationLeft > 0 ? durationLeft : durationFull;

                GetSession().GameState.StoreAuraDurationLeft(target, slot, durationFull, Environment.TickCount);
                GetSession().GameState.StoreAuraDurationFull(target, slot, durationFull);
            }
        }

        AuraInfo aura = new AuraInfo();
        aura.Slot = slot;
        aura.AuraData = auraData;

        // 1. The Flicker: Tell the client the slot is temporarily empty
        AuraUpdate clearUpdate = new AuraUpdate(target, false);
        AuraInfo clearAura = new AuraInfo();
        clearAura.Slot = slot;
        clearUpdate.Auras.Add(clearAura);
        SendPacketToClient(clearUpdate);

        // 2. The Reapplication: Send our fully loaded duration packet
        AuraUpdate update = new AuraUpdate(target, false);
        update.Auras.Add(aura);
        SendPacketToClient(update);
    }
}
