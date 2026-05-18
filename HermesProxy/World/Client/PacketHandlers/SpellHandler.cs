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
    // JimsProxy (PR #161 follow-up): how long after HandleSpellFailure peeks a
    // pending cast we wait for the trailing SMSG_CAST_FAILED before assuming
    // the legacy server isn't sending one. 2.5s is comfortably longer than
    // any observed CAST_FAILED arrival on vmangos/Twinstar (~1ms after FAILURE)
    // and shorter than feels-laggy to the user when the pathological Kronos
    // case kicks in (target-dies-mid-cast, no trailing CAST_FAILED).
    private const long WatchdogWindowMs = 2500;

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
        {
            GetSession().GameState.CurrentPlayerKnownSpells.Clear();
            GetSession().GameState.SynthesizedTalentRanks.Clear();
        }
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
        // Talent-passive-rank injection (LibClassicDurations etc. break without this).
        // Inline into the initial SendKnownSpells list so the modern client treats every
        // synthesized predecessor as already-known at login — no spell-learned toast spam,
        // no separate SMSG_LEARNED_SPELLS round-trip. Reconcile after building the list so
        // SynthesizedTalentRanks reflects the same state we're about to emit.
        var realKnown = GetSession().GameState.CurrentPlayerKnownSpells;
        var synthesizedSet = GetSession().GameState.SynthesizedTalentRanks;
        foreach (var sid in realKnown)
        {
            if (!GameData.TalentRankPredecessors.TryGetValue(sid, out var preds))
                continue;
            foreach (var pred in preds)
            {
                if (realKnown.Contains(pred))
                    continue;
                if (synthesizedSet.Add(pred))
                    spells.KnownSpells.Add(pred);
            }
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
        // JimsProxy (cast-block-unknown-spells): keep CurrentPlayerKnownSpells in sync
        // so the outbound CMSG_CAST_SPELL guard sees the actual server-side known set.
        // Without this, a rank-up replaces the action-bar binding but the proxy still
        // thinks the old rank is known and never tracks the new one.
        var knownSpellsSuperseded = GetSession().GameState.CurrentPlayerKnownSpells;
        knownSpellsSuperseded.Remove(supercededId);
        knownSpellsSuperseded.Add(spellId);
        SendPacketToClient(spells);
        ReconcileTalentRankInjection();
    }

    [PacketHandler(Opcode.SMSG_LEARNED_SPELL)]
    void HandleLearnedSpell(WorldPacket packet)
    {
        LearnedSpells spells = new LearnedSpells();
        uint spellId = packet.ReadUInt32();
        spells.Spells.Add(spellId);
        // JimsProxy (cast-block-unknown-spells): track newly-learned spells so the
        // outbound CMSG_CAST_SPELL guard doesn't false-positive on trainer/talent grants.
        GetSession().GameState.CurrentPlayerKnownSpells.Add(spellId);
        SendPacketToClient(spells);
        ReconcileTalentRankInjection();
    }

    // Reconciles SynthesizedTalentRanks against the real CurrentPlayerKnownSpells set.
    // For every known spell that's a talent rank, the proxy needs the modern client's
    // known-spells set to also contain every LOWER-rank spell id of the same talent —
    // otherwise addons probing IsPlayerSpell(rank1Id) get false even when the player
    // has spent points there (vanilla server LearnTalent calls RemoveSpell on the
    // previous rank, so only the highest stays in the server-side known list).
    //
    // Computes the delta between the *desired* synthesized set (predecessors of every
    // real known spell, minus anything the server already grants directly) and the
    // *current* synthesized set, then emits SMSG_LEARNED_SPELLS / SMSG_UNLEARNED_SPELLS
    // to bring the modern client in line. Both packets cap at 8 spells per emit, so
    // large deltas (e.g. respec wiping 30+ ranks) are split across multiple packets.
    // The LEARNED packets carry SuppressMessaging so the client doesn't fire a
    // "new spell learned" toast or callback for synthesized ranks.
    private void ReconcileTalentRankInjection()
    {
        if (GameData.TalentRankPredecessors.Count == 0)
            return;

        var realKnown = GetSession().GameState.CurrentPlayerKnownSpells;
        var synthesized = GetSession().GameState.SynthesizedTalentRanks;

        var desired = new HashSet<uint>();
        foreach (var sid in realKnown)
        {
            if (!GameData.TalentRankPredecessors.TryGetValue(sid, out var preds))
                continue;
            foreach (var p in preds)
            {
                if (!realKnown.Contains(p))
                    desired.Add(p);
            }
        }

        var toAdd = new List<uint>();
        foreach (var d in desired)
            if (!synthesized.Contains(d))
                toAdd.Add(d);

        var toRemove = new List<uint>();
        foreach (var s in synthesized)
            if (!desired.Contains(s))
                toRemove.Add(s);

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return;

        // Apply tracking first so any callback re-entering through a packet handler sees
        // the post-reconcile state.
        foreach (var x in toAdd) synthesized.Add(x);
        foreach (var x in toRemove) synthesized.Remove(x);

        const int BatchSize = 8;
        for (int offset = 0; offset < toAdd.Count; offset += BatchSize)
        {
            var batch = new LearnedSpells();
            batch.SuppressMessaging = true;
            for (int j = offset; j < toAdd.Count && j < offset + BatchSize; j++)
                batch.Spells.Add(toAdd[j]);
            SendPacketToClient(batch);
        }
        for (int offset = 0; offset < toRemove.Count; offset += BatchSize)
        {
            var batch = new UnlearnedSpells();
            batch.SuppressMessaging = true;
            for (int j = offset; j < toRemove.Count && j < offset + BatchSize; j++)
                batch.Spells.Add(toRemove[j]);
            SendPacketToClient(batch);
        }

        Log.Event("spell.talent_rank_injection.reconciled", new
        {
            added = toAdd.Count,
            removed = toRemove.Count,
            real_known = realKnown.Count,
            synthesized = synthesized.Count,
        });
    }

    [PacketHandler(Opcode.SMSG_SEND_UNLEARN_SPELLS)]
    void HandleSendUnlearnSpells(WorldPacket packet)
    {
        SendUnlearnSpells spells = new SendUnlearnSpells();
        uint spellCount = packet.ReadUInt32();
        var knownSpellsSendUnlearn = GetSession().GameState.CurrentPlayerKnownSpells;
        for (uint i = 0; i < spellCount; i++)
        {
            uint spellId = packet.ReadUInt32();
            spells.Spells.Add(spellId);
            // JimsProxy (cast-block-unknown-spells): drop unlearned spells from the
            // proxy-side known set so the CMSG_CAST_SPELL guard matches the real server state.
            knownSpellsSendUnlearn.Remove(spellId);
        }
        SendPacketToClient(spells);
        ReconcileTalentRankInjection();
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
        // JimsProxy (cast-block-unknown-spells): GMs deleveling players (PTR) and talent
        // respecs (live) both unlearn spells via this opcode. Without removing from the
        // proxy-side known set, the outbound CMSG_CAST_SPELL guard would still allow casts
        // for unlearned spells — same autoban path Nellag confirmed (server treats CMSG_CAST_SPELL
        // for an unknown spell as cheating and bans).
        GetSession().GameState.CurrentPlayerKnownSpells.Remove(spellId);
        SendPacketToClient(spells);
        ReconcileTalentRankInjection();
    }

    [PacketHandler(Opcode.SMSG_CAST_FAILED)]
    void HandleCastFailed(WorldPacket packet)
    {
        // JimsProxy (PR #161 follow-up): clean up any prior peek that didn't
        // get its own trailing CAST_FAILED — happens occasionally on Kronos
        // for cast-time + target-dies. Self-healing on every cast event.
        GetSession().RunWatchdogEviction();

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

        // JimsProxy: optional suppression of transient cast errors (NotReady = GCD active,
        // SpellInProgress = cast bar active). Useful with Low Latency Mode where mid-GCD
        // presses reach the server and bounce back. The 1.14 client's "Suppress Error Speech"
        // setting covers audio but not the red error text; this covers both.
        if (Settings.SuppressSpellCastErrors &&
            (reason == (uint)SpellCastResultVanilla.NotReady || reason == (uint)SpellCastResultVanilla.SpellInProgress))
        {
            GetSession().GameState.TryDequeuePendingNormalCast(spellId, out _);
            Log.Event("cast.error_suppressed", new
            {
                spell_id = spellId,
                reason_id = reason,
            });
            return;
        }

        // JimsProxy HealComm bridge: SMSG_CAST_FAILED targets the local
        // caster directly, so any pending resurrection cast tracked for
        // us is now cancelled — emit HC-1.0 stop so 1.12-native listeners
        // clear the rez indicator on their unit frames.
        GetSession().HealCommBridge.OnLocalPlayerSpellStop(spellId);

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
            // JimsProxy (PR #161 follow-up — movement preemption): if this
            // cast was marked when the user started moving, the modern client
            // sent CMSG_CANCEL_CAST and is waiting for SMSG_CAST_FAILED to
            // confirm. Suppressing CastFailed entirely makes the bar linger
            // for the legacy round-trip (~150-200ms — observed as a "small
            // delay" by testers). Emit CastFailed with DontReport so the
            // client gets its ack instantly with no popup or error sound.
            // Skip the SpellPrepare (no point prepping a cast we're failing).
            bool movementSuppressed = pendingCast!.MovementCancelled;
            uint effectiveReason = movementSuppressed
                ? (uint)SpellCastResultClassic.DontReport
                : LegacyVersion.ConvertSpellCastResult(reason);
            if (movementSuppressed)
            {
                Log.Event("cast.movement_cancel_suppressed", new
                {
                    queue = "normal",
                    spell_id = pendingCast.SpellId,
                    client_cast_id = pendingCast.ClientGUID.ToString(),
                });
            }
            else if (!pendingCast.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = pendingCast.ClientGUID;
                prepare2.ServerCastID = pendingCast.ServerGUID;
                SendPacketToClient(prepare2);
            }

            CastFailed failed = new();
            failed.SpellID = pendingCast.SpellId;
            failed.SpellXSpellVisualID = pendingCast.SpellXSpellVisualId;
            failed.Reason = effectiveReason;
            failed.CastID = pendingCast.ServerGUID;
            failed.FailedArg1 = arg1;
            failed.FailedArg2 = arg2;
            SendPacketToClient(failed);

            var gameState = GetSession().GameState;
            var heldCastTimeDrop = gameState.ClearHeldCastTimeCast();
            if (heldCastTimeDrop != null)
                GetSession().InstanceSocket.SendCastRequestFailed(heldCastTimeDrop, false);
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
        // Vanilla SMSG_PET_CAST_FAILED wire format varies between server flavors:
        //   cMaNGOS / vmangos canonical:  uint32 SpellID, uint8 Result.
        //   Kronos 5 (TrinityCore-1.12):  uint32 SpellID only (4 body bytes).
        // The original implementation always read SpellID + uint8 status, gated on
        // status==2, then read another uint8 reason. On Kronos 5 that meant:
        //   (a) the very first ReadUInt8 ran past end-of-buffer when SpellID
        //       consumed the entire 4-byte body → IndexOutOfRangeException in
        //       the world-client receive loop → connection died → a follow-on
        //       CMSG_CHAT_ADDON_MESSAGE NRE'd HandleAddonMessage → process crash;
        //   (b) on cMaNGOS-style 5-byte bodies, reason!=2 silently dropped the
        //       failure (action button stayed lit), and reason==2 crashed on the
        //       second ReadUInt8.
        // Repro that surfaced the crash: warlock /cast Consume Shadows on a pet
        // that's full-HP via macro (Kronos 5, captured in
        // jimsproxy-20260510-115151.jsonl, packet body size 4).
        uint spellId = packet.ReadUInt32();
        bool hasReason = packet.CanRead();
        uint legacyReason = hasReason ? packet.ReadUInt8() : 0u;

        // Look up pending pet cast by SpellId (queue-based, FIFO order)
        if (!GetSession().GameState.TryDequeuePendingPetCast(spellId, out var pendingCast))
        {
            // JimsProxy: CMSG_PET_ACTION (player presses a pet ability button)
            // doesn't populate PendingPetCasts the way CMSG_PET_CAST_SPELL does,
            // so a failed pet action arrives here with no pending entry to match.
            // Previously we logged and dropped, leaving the modern client's pet
            // UI in a stuck "casting" state because it never received a failure
            // signal for the press it had locally predicted. Warlock testers
            // report this as "pet sound stuck" / "stuck pet animation" — the
            // modern client appears to loop the casting indicator + any tied
            // sound until /reload.
            //
            // Emit a defensive fallback PetCastFailed with a deterministic
            // CastID (matching the seed pattern HandleSpellStartOrGo uses for
            // pet casts so a future CastID match still works) and DontReport
            // reason — the client unwinds button + state cleanly without a
            // misleading popup. Also send CancelSpellVisual for any visual
            // kit anchored on press: pet auto-cast spells like 7809 / 17735 /
            // 17850 DO have valid SpellVisualKit entries in modern Classic
            // 1.14.2 (unlike spells 75 / 5019), so the cancel actually lands.
            var petGuid = GetSession().GameState.CurrentPetGuid;
            // Defense in depth: only emit the fallback when we have a real
            // spell ID and a real pet GUID to anchor the synthesized CastID
            // and CancelSpellVisual source. SpellID == 0 would produce a
            // wonky CastID the client almost certainly ignores; targeting an
            // empty pet GUID is meaningless.
            if (!petGuid.IsEmpty() && spellId != 0)
            {
                uint spellVisual = GameData.GetSpellVisual(spellId);
                uint resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
                if (resolvedSpellVisualId != 0)
                {
                    CancelSpellVisual cancelVisual = new CancelSpellVisual();
                    cancelVisual.Source = petGuid;
                    cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                    SendPacketToClient(cancelVisual);
                }

                PetCastFailed fallback = new PetCastFailed();
                fallback.SpellID = spellId;
                fallback.Reason = (uint)SpellCastResultClassic.DontReport;
                fallback.CastID = WowGuid128.Create(
                    HighGuidType703.Cast,
                    SpellCastSource.Normal,
                    (uint)(GetSession().GameState.CurrentMapId ?? 0),
                    spellId,
                    (ulong)spellId + petGuid.GetCounter());
                SendPacketToClient(fallback);

                Log.Event("pet.cast_failed.no_pending", new
                {
                    spell_id = spellId,
                    has_reason = hasReason,
                    legacy_reason = legacyReason,
                    fallback_sent = true,
                    pet_guid_low = petGuid.GetCounter(),
                    spell_visual = spellVisual,
                    resolved_visual_id = resolvedSpellVisualId,
                });
            }
            else
            {
                Log.Event("pet.cast_failed.no_pending", new
                {
                    spell_id = spellId,
                    has_reason = hasReason,
                    legacy_reason = legacyReason,
                    fallback_sent = false,
                    pet_guid_empty = petGuid.IsEmpty(),
                    spell_id_zero = spellId == 0,
                });
            }
            return;
        }

        // JimsProxy (PR #161 follow-up — movement preemption parity): mirror
        // the player-side suppression. If the pet's pending cast was marked
        // MovementCancelled, force the reason to DontReport so no popup or
        // error sound fires while still acking the cancel to the client.
        bool movementSuppressed = pendingCast!.MovementCancelled;
        if (!pendingCast.HasStarted && !movementSuppressed)
        {
            SpellPrepare prepare2 = new SpellPrepare();
            prepare2.ClientCastID = pendingCast.ClientGUID;
            prepare2.ServerCastID = pendingCast.ServerGUID;
            SendPacketToClient(prepare2);
        }

        PetCastFailed spell = new PetCastFailed();
        spell.SpellID = spellId;
        // If movement-suppressed OR the wire didn't carry a reason byte
        // (Kronos-style 4-byte body), send DontReport so the action button
        // releases without a misleading popup.
        spell.Reason = (movementSuppressed || !hasReason)
            ? (uint)SpellCastResultClassic.DontReport
            : LegacyVersion.ConvertSpellCastResult(legacyReason);
        spell.CastID = pendingCast.ServerGUID;
        SendPacketToClient(spell);

        Log.Event("pet.cast_failed.routed", new
        {
            spell_id = spellId,
            has_reason = hasReason,
            legacy_reason = legacyReason,
            translated_reason = spell.Reason,
            movement_suppressed = movementSuppressed,
            had_started = pendingCast.HasStarted,
            cast_id = pendingCast.ServerGUID.ToString(),
        });
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

        // JimsProxy (PR #161 follow-up — movement preemption parity): mirror
        // the player-side suppression. See HandlePetCastFailed for rationale.
        bool movementSuppressed = pendingCast!.MovementCancelled;
        if (!pendingCast.HasStarted && !movementSuppressed)
        {
            SpellPrepare prepare2 = new SpellPrepare();
            prepare2.ClientCastID = pendingCast.ClientGUID;
            prepare2.ServerCastID = pendingCast.ServerGUID;
            SendPacketToClient(prepare2);
        }

        PetCastFailed failed = new PetCastFailed();
        failed.SpellID = spellId;
        uint reason = packet.ReadUInt8();
        failed.Reason = movementSuppressed
            ? (uint)SpellCastResultClassic.DontReport
            : LegacyVersion.ConvertSpellCastResult(reason);
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

        // JimsProxy: dedup the auto-cast retry storm. Kronos pet auto-cast fires
        // SMSG_SPELL_FAILED_OTHER 5+/sec while a target is out of range / dying;
        // forwarding each one chains CancelSpellVisuals into a stuck cast sound on
        // the 1.14.2 client. The first failure carries all the state the client
        // needs; subsequent same-(caster, spell) failures within 500ms add nothing.
        const long DedupWindowMs = 500;
        long nowMs = Time.GetMSTime();
        var dedupKey = (casterUnit, spellId);
        if (GetSession().GameState.RecentlyForwardedSpellFailedOther.TryGetValue(dedupKey, out var lastMs) &&
            nowMs - lastMs < DedupWindowMs)
        {
            Log.Event("spell.failed_other.dedup_skipped", new
            {
                spellId,
                reason,
                casterCounter = casterUnit.GetCounter(),
                ms_since_last = nowMs - lastMs,
            });
            return;
        }
        GetSession().GameState.RecentlyForwardedSpellFailedOther[dedupKey] = nowMs;

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
            // JimsProxy: pet AUTO-CAST failure — pull the unique CastID stored at
            // SPELL_START in PetAutoCastActiveCastIds. Without this lookup, the
            // synthesized CancelSpellVisual below targets the deterministic seed
            // (spellId + casterCounter) instead of the in-flight cast, the 1.14.2
            // client doesn't recognize the dismiss, and Firebolt sound loops when
            // the target dies mid-cast.
            else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                     GetSession().GameState.PetAutoCastActiveCastIds.TryRemove(activeKey, out var petCastId))
                castId = petCastId;
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
        // JimsProxy (mob-channel-cleanup-diag 2026-05-07): capture the actual GUIDs and
        // BackfireSpellID we stamp on the synthesized packets so a bug bundle can prove
        // whether either is being filled with the wrong value (e.g. player GUID instead
        // of mob GUID, or BackfireSpellID=0). Default 0 = packet was not synthesized.
        ulong interruptLogCasterLow = 0;
        ulong interruptLogVictimLow = 0;
        int interruptLogBackfireSpellId = 0;
        ulong cancelVisualSourceLow = 0;
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
            interruptLogCasterLow = interruptLog.Caster.GetCounter();
            interruptLogVictimLow = interruptLog.Victim.GetCounter();
            interruptLogBackfireSpellId = interruptLog.BackfireSpellID;

            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
                cancelVisualSourceLow = cancelVisual.Source.GetCounter();
            }
        }

        // Pet auto-cast failure (mirrors HandleSpellFailure fix). Defensive: if the
        // server ever broadcasts pet auto-cast failure via FAILED_OTHER, dismiss visual.
        if (casterIsPet && !sentCancelVisual)
        {
            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
                cancelVisualSourceLow = cancelVisual.Source.GetCounter();
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
            // Diagnostic: actual content of synthesized cleanup packets
            interruptLogCasterLow,
            interruptLogVictimLow,
            interruptLogBackfireSpellId,
            cancelVisualSourceLow,
            playerGuidLow = GetSession().GameState.CurrentPlayerGuid.GetCounter(),
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
        // JimsProxy (PR #161 follow-up): clean up any prior peek that didn't
        // get a trailing CAST_FAILED within the watchdog window. Runs before
        // we set up a new watchdog so leaks can't accumulate across failures.
        GetSession().RunWatchdogEviction();

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

        // JimsProxy HealComm bridge: emit HC-1.0 stop for any in-flight
        // resurrection cast that just failed/got interrupted. No-op for
        // non-rez spells (the bridge tracks rez state internally).
        if (casterIsLocalPlayer)
        {
            GetSession().HealCommBridge.OnLocalPlayerSpellStop(spellId);
        }

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
        // SPELL_FAILURE is now forwarded for all casters including local player (matches
        // upstream Xian55/HermesProxy). The client needs it to cancel the animation that
        // SPELL_START started. PR #72 originally suppressed this for local player, but the
        // GCD-lock bug was caused by the dequeue (now changed to peek), not the forwarding.

        WowGuid128 castId;
        uint spellVisual;
        bool dequeued = false;
        bool wasStarted = false;
        bool foundActiveCastId = false;

        // JimsProxy: Twinstar's Spell::SendInterrupted hardcodes the wire reason
        // byte to vanilla 0 (= classic AffectingCombat=1 after translation). For
        // local-player/pet casts the real reason arrives ~1ms later in
        // SMSG_CAST_FAILED, but the misleading "You are in combat" popup from
        // SMSG_SPELL_FAILURE has already shown. Bundle 20260505-191020 caught
        // this on a Mage casting Remove Lesser Curse with no curse to remove
        // (real reason: AlreadyAtFullHealth). Override the broadcast reason to
        // DontReport (classic 30) for local casts so the cast-bar / bow-draw
        // dismiss still happens but no error popup fires; CastFailed (from
        // either this path's inline send or HandleCastFailed) carries the real
        // reason and renders the correct popup.
        bool overrideReasonForLocalBroadcast = false;
        bool skipBroadcastFailure = false;
        // 1.14 needs SMSG_SPELL_FAILURE to retract the bow-draw / wand-aim visual
        // for ranged auto-attacks; CastFailed + CANCEL_AUTO_REPEAT alone leaves the
        // bow stuck drawn. Other instants can drop the broadcast entirely -- there's
        // no animation to cancel.
        // Source of truth: AutoRepeatSpells CSV (same set HandleCastSpell uses for
        // the isAutoRepeat early-return). Avoids missing wand/shoot ranks that aren't
        // 5019 and Auto Shot variants that aren't 75.
        bool isRangedAutoAttack = GameData.AutoRepeatSpells.Contains((uint)spellId);

        // JimsProxy: PEEK (don't dequeue) so the trailing SMSG_CAST_FAILED — which
        // carries the real failure reason on vmangos/Twinstar after Spell::SendInterrupted
        // hardcodes the SpellFailure wire reason to 0 (= AffectingCombat after translation)
        // — can be matched by HandleCastFailed via TryDequeuePendingNormalCast and
        // delivered to the client. Sending an inline CastFailed here would (a) consume
        // the queue entry HandleCastFailed needs, leaving its dequeue empty, and (b)
        // ship the misleading hardcoded reason to the client, which the 1.14 popup +
        // button-state renderer reads regardless of whether the broadcast SpellFailure
        // was suppressed. Trade-off: on Kronos, where the trailing SMSG_CAST_FAILED can
        // be dropped (e.g. target dies mid-cast on cast-time spells), the cast can leak
        // in the queue. For the SendInterrupted hardcoded-zero path that triggers this
        // suppression — overwhelmingly instants on vmangos/Twinstar — the trailing
        // CAST_FAILED reliably arrives. ClearHeldCastTimeCast still fires to release
        // any cast-time-held press attached to a now-failed cast bar.
        if (GetSession().GameState.CurrentPlayerGuid == casterUnit &&
            GetSession().GameState.PendingNormalCasts.FirstOrDefault(c => c.SpellId == spellId || (c.LegacySpellId != 0 && c.LegacySpellId == spellId)) is { } pendingNormal)
        {
            castId = pendingNormal.ServerGUID;
            spellVisual = pendingNormal.SpellXSpellVisualId;
            if (pendingNormal.LegacySpellId != 0)
                spellId = pendingNormal.SpellId;
            wasStarted = pendingNormal.HasStarted;
            dequeued = false; // peeked — HandleCastFailed dequeues on the trailing SMSG_CAST_FAILED
            // JimsProxy (PR #161 follow-up): arm the watchdog. If the trailing
            // SMSG_CAST_FAILED arrives within 2.5s, HandleCastFailed dequeues
            // normally (deadline becomes irrelevant). If not (Kronos-style
            // target-dies-mid-cast drop), the next event runs RunWatchdogEviction
            // and force-dequeues with synthetic SpellPrepare + CastFailed(DontReport)
            // so HasStartedNormalCast doesn't permanently block subsequent casts.
            pendingNormal.WatchdogDeadlineMs = Environment.TickCount64 + WatchdogWindowMs;

            var heldCastTimeDrop = GetSession().GameState.ClearHeldCastTimeCast();
            if (heldCastTimeDrop != null)
                GetSession().InstanceSocket.SendCastRequestFailed(heldCastTimeDrop, false);

            // JimsProxy (PR #161 follow-up — movement preemption): if the user
            // started moving while this cast-time spell was in progress, the
            // modern client already cancelled its own cast bar via client-side
            // prediction. Suppress the broadcast SpellFailure entirely —
            // emitting it would surface a misleading "You are in combat" popup
            // (Spell::SendInterrupted hardcodes the wire reason to 0).
            if (pendingNormal.MovementCancelled)
                skipBroadcastFailure = true;
            // Instant cast that wasn't a ranged auto-attack: SPELL_START was
            // never forwarded, so there's no cast bar to dismiss. Skip the misleading
            // broadcast SpellFailure entirely. The trailing SMSG_CAST_FAILED via
            // HandleCastFailed sends SpellPrepare + CastFailed with the real reason,
            // which clears button-lit state and shows the correct popup.
            else if (!pendingNormal.HasStarted && !isRangedAutoAttack)
                skipBroadcastFailure = true;
            else
                overrideReasonForLocalBroadcast = true;
        }
        else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                 GetSession().GameState.PendingPetCasts.FirstOrDefault(c => c.SpellId == spellId || (c.LegacySpellId != 0 && c.LegacySpellId == spellId)) is { } pendingPet)
        {
            castId = pendingPet.ServerGUID;
            spellVisual = pendingPet.SpellXSpellVisualId;
            if (pendingPet.LegacySpellId != 0)
                spellId = pendingPet.SpellId;
            wasStarted = pendingPet.HasStarted;
            dequeued = false; // peeked — HandlePetCastFailed dequeues on the trailing SMSG_PET_CAST_FAILED
            pendingPet.WatchdogDeadlineMs = Environment.TickCount64 + WatchdogWindowMs;

            // Pet path: PetCastFailed handles the pet UI. Suppress the broadcast
            // SpellFailure for instants (no cast bar); for cast-time pet spells
            // forward with DontReport so the bar dismisses without popup spam.
            if (!pendingPet.HasStarted)
                skipBroadcastFailure = true;
            else
                overrideReasonForLocalBroadcast = true;
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
            // JimsProxy: pet AUTO-CAST failure path — same rationale as
            // HandleSpellFailedOther. Without this lookup the synthesized dismiss
            // packets target the deterministic seed and the client ignores them.
            else if (GetSession().GameState.CurrentPetGuid == casterUnit &&
                     GetSession().GameState.PetAutoCastActiveCastIds.TryRemove(activeKey, out var petCastId))
            {
                castId = petCastId;
                foundActiveCastId = true;
            }
            else
                castId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId!, spellId, spellId + casterUnit.GetCounter());
            spellVisual = GameData.GetSpellVisual(spellId);
        }

        byte broadcastReason = overrideReasonForLocalBroadcast ? (byte)SpellCastResultClassic.DontReport : reason;
        if (!skipBroadcastFailure)
        {
            SpellFailure spell = new SpellFailure();
            spell.CasterUnit = casterUnit;
            spell.CastID = castId;
            spell.SpellID = spellId;
            spell.SpellXSpellVisualID = spellVisual;
            spell.Reason = broadcastReason;
            SendPacketToClient(spell);
        }

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
        // JimsProxy (mob-channel-cleanup-diag 2026-05-07): see HandleSpellFailedOther for rationale.
        ulong interruptLogCasterLow = 0;
        ulong interruptLogVictimLow = 0;
        int interruptLogBackfireSpellId = 0;
        ulong cancelVisualSourceLow = 0;
        if (reason == 61 /* Interrupted */ && foundActiveCastId && !casterIsPlayer && !casterIsPet)
        {
            SpellInterruptLog interruptLog = new SpellInterruptLog();
            interruptLog.Caster = GetSession().GameState.CurrentPlayerGuid;
            interruptLog.Victim = casterUnit;
            interruptLog.InterruptedSpellID = (int)spellId;
            interruptLog.BackfireSpellID = (int)spellId;
            SendPacketToClient(interruptLog);
            sentInterruptLog = true;
            interruptLogCasterLow = interruptLog.Caster.GetCounter();
            interruptLogVictimLow = interruptLog.Victim.GetCounter();
            interruptLogBackfireSpellId = interruptLog.BackfireSpellID;

            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
                cancelVisualSourceLow = cancelVisual.Source.GetCounter();
            }
        }

        // Pet auto-cast failure: when a pet's AI-initiated spell fails (target dies,
        // LoS, range), the cast fell through to the else branch (no PendingPetCasts
        // entry for auto-casts). SpellFailure packet dismisses the cast bar, but the
        // visual kit (animation + looping sound) needs explicit CancelSpellVisual.
        if (casterIsPet && !dequeued && !sentCancelVisual)
        {
            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
                cancelVisualSourceLow = cancelVisual.Source.GetCounter();
            }
        }

        // JimsProxy (cast-failure-stuck-visual 2026-05-10): For local-player cast
        // failures where SPELL_START was forwarded, the modern 1.14 client does not
        // reliably cancel the caster-side visual kit (casting pose, looping channel
        // sound, bow-draw / wand-aim) from SMSG_SPELL_FAILURE alone. Two repros in
        // bundle 20260509-111449:
        //   1. Holy Light: server emits SPELL_START + SPELL_FAILURE in the same
        //      packet batch (vmangos/Twinstar post-START LoS/range rejection).
        //      The just-anchored visual kit stays running through the failure.
        //   2. Holy Light during movement: SpellFailure broadcast suppressed
        //      (client-side cast-bar prediction already unwound the bar) but the
        //      casting pose is not part of that prediction.
        // User reports the same stuck-visual on Auto Shot (75) every session.
        // Mirrors the pet block above — purely additive, idempotent on the client.
        //
        // 2026-05-11 follow-up (ranged-auto-repeat stuck sound): The original gate
        // also requires wasStarted=true (i.e. a matching PendingNormalCasts entry).
        // Wand/Auto-Shot AUTO-REPEAT ticks after the first shot have no such entry
        // — the client sends CMSG_CAST_SPELL once, then the server fires SPELL_START
        // + SPELL_GO autonomously for each repeat. After the first SPELL_GO clears
        // the queue, subsequent failures (target out of range, target dead) fall
        // through to the else-branch with wasStarted=false. Bundle
        // jimsproxy-20260511-114307 captured 28 such failures for spell 5019
        // (Shoot/wand), all with sentCancelVisual=false → looping wand-aim sound
        // until /reload. SPELL_START is forwarded for all local-player spells (PR
        // #72's instant-suppression was removed once SpellFailure switched to peek),
        // so for auto-repeat ticks the visual is live on the client regardless of
        // whether the pending queue tracked it.
        if (casterIsLocalPlayer && (wasStarted || isRangedAutoAttack) && !sentCancelVisual)
        {
            resolvedSpellVisualId = GameData.GetSpellVisualIdFromXSpellVisual(spellVisual);
            if (resolvedSpellVisualId != 0)
            {
                CancelSpellVisual cancelVisual = new CancelSpellVisual();
                cancelVisual.Source = casterUnit;
                cancelVisual.SpellVisualID = (int)resolvedSpellVisualId;
                SendPacketToClient(cancelVisual);
                sentCancelVisual = true;
                cancelVisualSourceLow = cancelVisual.Source.GetCounter();
            }
        }

        Log.Event("spell.failure.routed", new
        {
            spellId,
            reason,
            broadcast_reason = broadcastReason,
            broadcast_skipped = skipBroadcastFailure,
            broadcast_reason_overridden = overrideReasonForLocalBroadcast,
            is_ranged_auto_attack = isRangedAutoAttack,
            isCaster = GetSession().GameState.CurrentPlayerGuid == casterUnit,
            isPetCaster = GetSession().GameState.CurrentPetGuid == casterUnit,
            dequeued,
            wasStarted,
            foundActiveCastId,
            sentInterruptLog,
            sentCancelVisual,
            spellVisual,
            resolvedSpellVisualId,
            // Diagnostic: actual content of synthesized cleanup packets
            interruptLogCasterLow,
            interruptLogVictimLow,
            interruptLogBackfireSpellId,
            cancelVisualSourceLow,
            casterUnitLow = casterUnit.GetCounter(),
            playerGuidLow = GetSession().GameState.CurrentPlayerGuid.GetCounter(),
        });
    }

    [PacketHandler(Opcode.SMSG_SPELL_START)]
    void HandleSpellStart(WorldPacket packet)
    {
        if (GetSession().GameState.CurrentMapId == null)
            return;

        SpellStart spell = new SpellStart();
        try
        {
            spell.Cast = HandleSpellStartOrGo(packet, false);
        }
        catch (Exception e)
        {
            // Log + skip the cast instead of letting the parse exception
            // bubble up through ReceiveLoop and DC the user. See HandleSpellGo
            // for the in-the-wild repro and rationale.
            LogSpellStartGoParseFailure(packet, e, isSpellGo: false);
            DrainOrphanedStartedNormalCastsOnParseFailure(isSpellGo: false);
            return;
        }

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

            // Skip SpellPrepare if the hold-accept path (or off-GCD path) already sent
            // one for this cast. Without this guard, held casts get a duplicate
            // SpellPrepare (one at hold-accept, one here); the modern client treats
            // duplicates as idempotent, but suppressing the second is cleaner and makes
            // the contract explicit.
            if (!pendingCast.HasSentPrepare)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = pendingCast.ClientGUID;
                prepare.ServerCastID = spell.Cast.CastID;
                SendPacketToClient(prepare);
                pendingCast.HasSentPrepare = true;
            }

            // Clear non-started casts and send failures for them
            // (keeps the started cast so SPELL_GO can dequeue it)
            var failedCasts = GetSession().GameState.ClearNonStartedNormalCasts();
            foreach (var failed in failedCasts)
                GetSession().InstanceSocket.SendCastRequestFailed(failed, false);
        }
        bool petCastWasPlayerPressed = false;
        if (casterIsLocalPet &&
            GetSession().GameState.TryMarkPendingPetCastStarted((uint)spell.Cast.SpellID, out var pendingPetCast))
        {
            petCastWasPlayerPressed = true;
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
        bool isRangedAutoAttack = GameData.AutoRepeatSpells.Contains((uint)spell.Cast.SpellID);
        bool isChanneled = GameData.IsChanneledSpell((uint)spell.Cast.SpellID);
        // PR #72 originally suppressed SPELL_START for local player instants to prevent
        // GCD-lock-on-failure. Root cause was HandleSpellFailure dequeuing the pending cast
        // before CAST_FAILED could reach the client. Fixed by changing SPELL_FAILURE to peek
        // instead of dequeue. SPELL_START is now forwarded for all spells (matches upstream
        // Xian55/HermesProxy) — instant animations play immediately instead of ~RTT/2 late.
        if (isRangedAutoAttack && (casterIsLocalPlayer || casterIsLocalPet) && spell.Cast.CastTime == 0)
        {
            Log.Event("spell.start.ranged_auto_forwarded", new
            {
                spell_id = spell.Cast.SpellID,
                caster_is_player = casterIsLocalPlayer,
                caster_is_pet = casterIsLocalPet,
            });
            // Track the natural SPELL_START so HandleSpellGo can decide
            // whether to synthesize one for subsequent auto-repeat ticks
            // that arrive without a preceding START.
            if (casterIsLocalPlayer)
                GetSession().GameState.LastNaturalAutoShotSpellStartMs[(uint)spell.Cast.SpellID] = Time.GetMSTime();
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

        // JimsProxy HealComm bridge: dismiss any active synthesized SpellStart
        // for this remote caster + spell BEFORE forwarding the real one. The
        // bridge synthesizes SMSG_SPELL_START from inbound HC-1.0 addon comms
        // because vanilla servers gate-broadcast SPELL_START (out-of-range
        // raid healers' casts never reach the modern client). When the real
        // SPELL_START does arrive (caster came into update range mid-cast),
        // we need to clear the synth's cast bar so the client doesn't render
        // two overlapping bars for the same cast. Gated to non-player-non-pet
        // so the pet-instant-buff suppression below is unaffected.
        if (!casterIsLocalPlayer && !casterIsLocalPet)
        {
            GetSession().HealCommBridge.OnRealSpellStartFromOther(spell.Cast.CasterUnit, (uint)spell.Cast.SpellID);
        }

        // JimsProxy (pet-instant-buff-double-sound): suppress SMSG_SPELL_START for pet
        // server-driven AUTO-CASTS that are instant. For these, SPELL_START + SPELL_GO
        // arrive in the same millisecond — the modern Classic 1.14 client's SpellVisualKit
        // fires sound on each, producing a noticeably stuck/repeated sound (tester first
        // reported succubus Lesser Invisibility 7870; says all 4 warlock pets exhibit it
        // on their auto-cast spawn abilities). SPELL_GO arrives normally and plays the
        // single correct sound; the pet aura still applies via aura.slot.set independent
        // of whether SPELL_START reached the client.
        //
        // Heuristic: pet caster + CastTime == 0 + no matching pending CMSG_PET_CAST_SPELL.
        // The pending check distinguishes auto-casts from player-pressed pet-bar abilities
        // (Sacrifice, Spell Lock, manually-triggered Lesser Invisibility, etc.) where the
        // START visual matters for snappy feel — those still forward as before.
        if (casterIsLocalPet && spell.Cast.CastTime == 0 && !petCastWasPlayerPressed)
        {
            Log.Event("spell.start.suppressed_pet_auto_double_sound", new
            {
                spell_id = spell.Cast.SpellID,
                spell_visual_id = spell.Cast.SpellXSpellVisualID,
                caster_low = spell.Cast.CasterUnit.GetCounter(),
            });
            return;
        }

        SendPacketToClient(spell);

        // JimsProxy HealComm bridge: when local player begins a resurrection
        // cast, synthesize HC-1.0 "Resurrection/{name}/start/" addon outbound
        // so 1.12-native HealComm-1.0 listeners (Luna unit frames) light up
        // the incoming-rez indicator on the corpse. Modern peers ignore the
        // "HealComm" prefix; their Luna uses the native UnitHasIncomingRes
        // API driven by the SMSG_SPELL_START we just forwarded above.
        if (casterIsLocalPlayer)
        {
            GetSession().HealCommBridge.OnLocalPlayerSpellStart((uint)spell.Cast.SpellID, spell.Cast.Target.Unit);
        }

        // Send cast-time sideband for non-self casters so the addon gets
        // the server-reported cast time instead of GetSpellInfo() which
        // returns the observer's own modified value (wrong rank/talents).
        if (GetSession().GameState.JimsPlusSideband &&
            spell.Cast.CasterUnit != GetSession().GameState.CurrentPlayerGuid &&
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
        try
        {
            spell.Cast = HandleSpellStartOrGo(packet, true);
        }
        catch (Exception e)
        {
            // Some legacy servers emit a SMSG_SPELL_GO that the parser
            // misreads — most often around channeled-spell ticks (Drain
            // Soul on Kronos PTR was the original repro). Without this
            // guard the exception bubbles up through ReceiveLoop and
            // DCs the entire WorldClient session. Log full packet
            // contents so the next hit gives us bytes to fix the parser.
            LogSpellStartGoParseFailure(packet, e, isSpellGo: true);
            DrainOrphanedStartedNormalCastsOnParseFailure(isSpellGo: true);
            return;
        }

        // JimsProxy (synth-spell-start-for-autoshot): the 1.12 server only
        // emits SMSG_SPELL_START at toggle/retarget for ranged auto attacks
        // (Auto Shot 75, Shoot 5019). Every auto-repeat tick is a bare
        // SPELL_GO. Modern Classic 1.14 servers emit SPELL_START per tick,
        // so addons that listen for COMBAT_LOG_EVENT SPELL_CAST_START
        // (Kaedin's swing timer, Quartz, etc.) only fire once per series
        // through the proxy. Synthesize a SPELL_START before the GO when
        // no natural one was forwarded within AutoShotSynthSpellStartGapMs.
        bool isRangedAutoAttack = GameData.AutoRepeatSpells.Contains((uint)spell.Cast.SpellID);
        if (isRangedAutoAttack &&
            spell.Cast.CasterUnit == GetSession().GameState.CurrentPlayerGuid)
        {
            long now = Time.GetMSTime();
            long lastNaturalMs = GetSession().GameState.LastNaturalAutoShotSpellStartMs
                .GetValueOrDefault((uint)spell.Cast.SpellID, 0);
            long gapMs = now - lastNaturalMs;
            const long AutoShotSynthSpellStartGapMs = 1000;
            if (gapMs > AutoShotSynthSpellStartGapMs)
            {
                SpellStart synthStart = new SpellStart();
                synthStart.Cast = spell.Cast;
                SendPacketToClient(synthStart);
                Log.Event("spell.start.synth_for_autoshot", new
                {
                    spell_id = spell.Cast.SpellID,
                    gap_ms = gapMs,
                });
            }
        }

        // JimsProxy HealComm bridge: SMSG_SPELL_GO for the local player marks
        // natural completion of a tracked cast. If it's a resurrection, clear
        // the pending tracker so a later same-spell-id failure can't emit a
        // spurious HC-1.0 stop, and so stale entries don't accumulate across
        // successful rezzes in the session. No HC-1.0 stop is emitted for
        // natural completion — Luna clears its rez indicator on its own via
        // the target's SPELL_AURA_APPLIED for the Resurrection Request buff.
        if (GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit)
        {
            GetSession().HealCommBridge.OnLocalPlayerSpellCompleted((uint)spell.Cast.SpellID);
        }

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
            // before SpellGo so the client knows which cast completed.
            // Off-GCD casts already sent SpellPrepare at forward time (HasSentPrepare).
            if (!pendingCast.HasStarted && !pendingCast.HasSentPrepare)
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

                var gcdCooldown = new SpellCooldownPkt();
                gcdCooldown.Caster = spell.Cast.CasterUnit;
                gcdCooldown.Flags = 0x01; // SPELL_COOLDOWN_FLAG_INCLUDE_GCD
                gcdCooldown.SpellCooldowns.Add(new SpellCooldownStruct
                {
                    SpellID = (uint)spell.Cast.SpellID,
                    ForcedCooldown = (uint)gcdMs,
                    ModRate = 1.0f,
                });
                SendPacketToClient(gcdCooldown);

                Log.Event("gcd.cooldown_synth", new
                {
                    spell_id = spell.Cast.SpellID,
                    forced_cooldown_ms = gcdMs,
                    flags = 0x01,
                });
            }

            var gameStateAfter = GetSession().GameState;
            if (!gameStateAfter.HasForwardedPendingCast())
            {
                var heldCast = gameStateAfter.TakeHeldCastIfReady();
                if (heldCast != null)
                {
                    Log.Event("spell.held_fire_on_success", new
                    {
                        success_spell_id = spell.Cast.SpellID,
                        held_spell_id = heldCast.SpellId,
                    });
                    gameStateAfter.OnGcdHeldCastFire?.Invoke(heldCast);
                }
            }

            var heldCastTime = gameStateAfter.TakeHeldCastTimeCast();
            if (heldCastTime != null)
            {
                Log.Event("cast.held_fire_on_cast_complete", new
                {
                    completed_spell_id = spell.Cast.SpellID,
                    held_spell_id = heldCastTime.SpellId,
                });
                gameStateAfter.OnGcdHeldCastFire?.Invoke(heldCastTime);
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

        // JimsProxy (warrior-charge-revisit): vanilla Twinstar/Kronos broadcast a second
        // SMSG_SPELL_GO ~1ms after the parent Charge/Intercept GO for the triggered stun
        // sub-effect (7922 Charge Stun / 20615 Intercept Stun) with caster=local player.
        // The modern 1.14 client kicks the sub-effect's SpellVisualKit on the caster on
        // top of the Charge/Intercept lunge kit — two overlapping caster-side animations
        // read as the warrior twitching ("on crack"). We want both visuals: Charge lunge
        // on the warrior (parent spell 100/20252 GO already handled that), stun pose on
        // the mob. Solution: rewrite CasterGUID/CasterUnit to the hit target so the
        // modern client plays the stun kit's caster events on the mob instead of the
        // warrior. Stun aura/icon on the mob is unaffected (SMSG_AURA_UPDATE drives that
        // independently). CLEU will show spell 7922/20615 source=mob, but damage meters
        // attribute Charge/Intercept damage to the parent spell, not this sub-effect.
        if ((spell.Cast.SpellID == 7922 || spell.Cast.SpellID == 20615) &&
            GetSession().GameState.CurrentPlayerGuid == spell.Cast.CasterUnit &&
            spell.Cast.HitTargets.Count > 0)
        {
            WowGuid128 stunTarget = spell.Cast.HitTargets[0];
            Log.Event("spell.go.stun_subeffect_redirected", new
            {
                spell_id = spell.Cast.SpellID,
                spell_visual_id = spell.Cast.SpellXSpellVisualID,
                redirected_to = stunTarget.ToString(),
            });
            spell.Cast.CasterGUID = stunTarget;
            spell.Cast.CasterUnit = stunTarget;
        }

        SendPacketToClient(spell);

        // JimsProxy threat translation: route Hunter / Pet / class abilities
        // through the threat tracker so SMSG_THREAT_UPDATE reflects the cast.
        // Done after SendPacketToClient so the SpellGo arrives first and any
        // resulting THREAT_UPDATE follows it on the wire (matches the
        // server-driven ordering the modern client expects).
        GetSession().ThreatTracker.OnSpellCast(spell.Cast.CasterUnit, spell.Cast.SpellID, spell.Cast.HitTargets);
    }

    private static void LogSpellStartGoParseFailure(WorldPacket packet, Exception e, bool isSpellGo)
    {
        // Capture full packet bytes (capped) as a hex string so the next time
        // this fires we have exact bytes to trace the parse divergence.
        const int maxBytes = 512;
        byte[] data = packet.GetData();
        int dumpLen = data.Length < maxBytes ? data.Length : maxBytes;
        var sb = new System.Text.StringBuilder(dumpLen * 2);
        for (int i = 0; i < dumpLen; i++)
            sb.Append(data[i].ToString("x2"));

        Log.PrintNet(LogType.Error, LogNetDir.S2P,
            $"SMSG_SPELL_{(isSpellGo ? "GO" : "START")} parse failed (suppressed DC): {e.Message}");
        Log.Event("spell.parse_failed", new
        {
            phase = isSpellGo ? "go" : "start",
            packet_size = data.Length,
            dumped_bytes = dumpLen,
            packet_hex = sb.ToString(),
            error = e.Message,
            stack = e.StackTrace,
        });
    }

    // JimsProxy (synth-spell-start-for-autoshot follow-up): when a SPELL_START
    // or SPELL_GO packet fails to parse, the matching pending-cast queue entry
    // is orphaned — no normal completion path dequeues it. For HasStarted=true
    // entries this is bad: HasStartedNormalCast() permanently returns true and
    // blocks subsequent cast-time spells. Emit a synthetic CastFailed(DontReport)
    // for each orphaned entry so the modern client's cast bar dismisses
    // silently and the queue self-heals. We can't tell from a failed parse
    // whether the packet was for the local player or a foreign caster; on
    // average there's at most one HasStarted=true entry (the GCD gate ensures
    // serial cast-time spells), so worst-case spurious cleanup is bounded to
    // one cast — a small price to avoid the permanent lock.
    private void DrainOrphanedStartedNormalCastsOnParseFailure(bool isSpellGo)
    {
        var queue = GetSession().GameState.PendingNormalCasts;
        var keep = new List<ClientCastRequest>();
        int orphaned = 0;
        while (queue.TryDequeue(out var cast))
        {
            if (cast.HasStarted)
            {
                CastFailed failed = new();
                failed.SpellID = cast.SpellId;
                failed.SpellXSpellVisualID = cast.SpellXSpellVisualId;
                failed.Reason = (uint)SpellCastResultClassic.DontReport;
                failed.CastID = cast.ServerGUID;
                SendPacketToClient(failed);
                orphaned++;
            }
            else
            {
                keep.Add(cast);
            }
        }
        foreach (var c in keep)
            queue.Enqueue(c);

        if (orphaned > 0)
        {
            Log.Event("spell.parse_failed.queue_drained", new
            {
                phase = isSpellGo ? "go" : "start",
                orphaned_count = orphaned,
            });
        }
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
            // JimsProxy: pet AUTO-CASTs (Imp Firebolt auto-fire etc.) have no
            // PendingPetCast entry, so the downstream ServerGUID override in
            // HandleSpellStart / HandleSpellGo misses them. Using a deterministic
            // seed (spellId + casterCounter) means every auto-cast of the same
            // spell from the same pet shares the SAME CastID — the 1.14.2 client
            // treats this as updates to one in-flight cast and the audio pipeline
            // overlaps into a stuck cast sound. Mint a unique CastID at SPELL_START
            // and store it in PetAutoCastActiveCastIds so the matching SPELL_GO
            // can recall it; START/GO of one cast share one ID, distinct casts
            // get distinct IDs. Player-pressed pet casts still get overridden
            // with ServerGUID downstream, so the stored entry is a harmless
            // orphan in that case.
            var petKey = (dbdata.CasterUnit, (uint)dbdata.SpellID);
            if (isSpellGo)
            {
                if (casterIsPet && gameState.PetAutoCastActiveCastIds.TryRemove(petKey, out var startedCastId))
                {
                    // Pair this GO with the unique ID minted at the matching SPELL_START.
                    dbdata.CastID = startedCastId;
                }
                else
                {
                    // Player/pet SPELL_GO: unique CastID per packet. Channeled tick spells
                    // (Arcane Missiles 7269) don't match PendingNormalCasts — without unique
                    // IDs every tick shares the same CastID and the client drops a missile.
                    // For casts that DO match, HandleSpellGo overwrites with ServerGUID anyway.
                    uint seq = (uint)Interlocked.Increment(ref gameState.PlayerChildCastSequence);
                    dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, ((ulong)seq << 32) | (uint)((uint)dbdata.SpellID + dbdata.CasterUnit.GetCounter()));
                }
            }
            else if (casterIsPet)
            {
                // Pet SPELL_START: unique CastID, stored for the matching SPELL_GO.
                uint seq = (uint)Interlocked.Increment(ref gameState.PlayerChildCastSequence);
                var uniqueCastId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, ((ulong)seq << 32) | (uint)((uint)dbdata.SpellID + dbdata.CasterUnit.GetCounter()));
                dbdata.CastID = uniqueCastId;
                gameState.PetAutoCastActiveCastIds[petKey] = uniqueCastId;
            }
            else
            {
                // Player SPELL_START: deterministic seed, overridden by ServerGUID on GO
                // via PendingNormalCasts dequeue. Player CMSG_CAST_SPELL always
                // populates PendingNormalCasts so the override always fires.
                dbdata.CastID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)gameState.CurrentMapId!, (uint)dbdata.SpellID, (ulong)dbdata.SpellID + dbdata.CasterUnit.GetCounter());
            }
        }

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

        // Vanilla 1.12 SPELL_GO doesn't carry CastTime. Without a timestamp the
        // 1.14 client chains GCD starts (new = old + 1500ms) instead of anchoring
        // to server time, causing ~RTT drift per cast. Stamp with proxy time so the
        // client's TIME_SYNC offset can convert it to local time.
        if (isSpellGo && dbdata.CastTime == 0)
            dbdata.CastTime = Time.GetMSTime();

        // JimsProxy: emit structured spell.cast event so we can diagnose spell-ID
        // and visual-kit lookup issues without parsing .pkt files. Particularly
        // useful for comparing what different 1.12 servers (Kronos vs Ashen-wow)
        // send for the same spell -- on Kronos PW:Shield doesn't render the
        // bubble visual; on Ashen-wow it does. If spell_visual_id=0 here, the
        // CSV lookup failed (either wrong spellId from server or missing row).
        // Emitted for both SMSG_SPELL_START and SMSG_SPELL_GO (shared codepath).
        // Logged after CastFlags + CastTime are parsed so the bundle can grep
        // pet-only instant-cast SPELL_STARTs (warlock-pet spawn glow triage).
        Log.Event("spell.cast", new
        {
            direction = "s2c",
            phase = isSpellGo ? "go" : "start",
            spell_id = dbdata.SpellID,
            spell_visual_id = dbdata.SpellXSpellVisualID,
            visual_lookup_missing = dbdata.SpellXSpellVisualID == 0,
            caster_guid = dbdata.CasterGUID.ToString(),
            caster_is_player = dbdata.CasterGUID == GetSession().GameState.CurrentPlayerGuid,
            caster_is_pet = dbdata.CasterUnit == GetSession().GameState.CurrentPetGuid,
            cast_time = dbdata.CastTime,
            cast_flags = dbdata.CastFlags,
            casterCounter = dbdata.CasterUnit.GetCounter(), //MIRASU - lets us correlate with spell.failed_other.routed
            castIdCounter = dbdata.CastID.GetCounter(),     //MIRASU - this is the CastID the modern client tracks
        });

        if (isSpellGo)
        {
            var hitCount = packet.ReadUInt8();
            // JimsProxy: bounds-check the declared hitCount against actual remaining
            // packet bytes BEFORE entering the fixed-stride read loop. If hitCount * 8
            // (one full GUID per target) exceeds what's left, throw a descriptive
            // exception that the outer try/catch in HandleSpellGo logs along with the
            // packet hex bytes. Without this guard, ReadGuid() bottoms out in
            // BinaryPrimitives.ReadUInt64LittleEndian and throws a generic
            // ArgumentOutOfRangeException with no spell context — diagnostically
            // useless and (on builds that lack the outer try/catch) caused player DCs.
            // Most likely trigger: an earlier optional field was read with the wrong
            // LegacyVersion gate, shifting byte alignment so hitCount lands on a bogus
            // value like 0xFF, producing a 2040-byte target list demand.
            if (hitCount * 8 > packet.BytesRemaining)
                throw new InvalidOperationException(
                    $"SMSG_SPELL_GO parse overrun: spell={dbdata.SpellID} hitCount={hitCount} needs {hitCount * 8} bytes but only {packet.BytesRemaining} remain");
            for (var i = 0; i < hitCount; i++)
            {
                WowGuid128 hitTarget = packet.ReadGuid().To128(GetSession().GameState);
                dbdata.HitTargets.Add(hitTarget);
            }

            var missCount = packet.ReadUInt8();
            // Vanilla TC-1.12 forks (Kronos) emit an extra uint8 between the hit
            // section and targetFlags that the proxy reads as missCount. Treating
            // it as a real miss target sends the parser into a bogus 8-byte GUID +
            // missType read, shredding alignment and overrunning the Vector3 in
            // target data — which #180 catches but then drains the player's active
            // cast queue, making their cast bar vanish mid-cast.
            // The phantom byte varies (0x01, 0x03 observed). Detect it by either:
            //  (a) the would-be missType peek lands on an invalid value (>Reflect=11), OR
            //  (b) missCount demands more bytes than the packet can possibly hold —
            //      a real server never claims more misses than data.
            // Real misses (vmangos-style) have a valid missType and fit the packet.
            // Gated to vanilla only: TBC+ uses uint32 targetFlags and a different trailer
            // alignment, so the heuristic's invariants don't translate cleanly and could
            // mask unrelated parse drift instead of throwing.
            if (missCount > 0 && LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                bool isPhantom = false;
                string phantomReason = "";

                if (missCount * 9 > packet.BytesRemaining)
                {
                    isPhantom = true;
                    phantomReason = "overrun";
                }
                else
                {
                    packet.Skip(8); // past would-be miss GUID
                    byte peekMissType = packet.ReadUInt8();
                    packet.Skip(-9); // rewind to start of miss section
                    if (peekMissType > (byte)SpellMissInfo.Reflect)
                    {
                        isPhantom = true;
                        phantomReason = $"invalid_misstype_{peekMissType}";
                    }
                }

                if (isPhantom)
                {
                    Log.Event("spell.spell_go.phantom_misscount_skipped", new
                    {
                        spell_id = dbdata.SpellID,
                        misscount_byte = missCount,
                        reason = phantomReason,
                        cast_flags = dbdata.CastFlags,
                        hit_count = hitCount,
                        bytes_remaining = packet.BytesRemaining,
                    });
                    missCount = 0;
                }
            }

            // Same bounds check for miss-target loop. Floor 9 bytes per entry (GUID +
            // missType byte); reflect adds an optional byte, so 9 * missCount is the
            // minimum. Underestimating is fine — individual reads still succeed up to
            // the actual end, and the outer try/catch handles any residual overrun.
            if (missCount * 9 > packet.BytesRemaining)
                throw new InvalidOperationException(
                    $"SMSG_SPELL_GO parse overrun: spell={dbdata.SpellID} missCount={missCount} needs {missCount * 9} bytes but only {packet.BytesRemaining} remain (after {hitCount} hit targets)");
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

        // Threat translation: feed spell damage (direct hit) into the tracker.
        // Pass the spell id so the per-ability damage multiplier (Maul x1.75,
        // Earth Shock x2, Holy Nova x0, etc.) can apply.
        GetSession().ThreatTracker.OnDamage(spell.CasterGUID, spell.TargetGUID, (int)spell.SpellID, spell.Damage);
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

            bool verbose = Log.VerboseLogEnabled;
            if (verbose)
                Log.Print(LogType.Server, $"SpellExecuteLog: caster={casterGuid} spell={spellId} effects={effectCount}");

            for (uint i = 0; i < effectCount; i++)
            {
                dbgEffectIndex = i;
                uint effectType = packet.ReadUInt32();
                uint targetCount = packet.ReadUInt32();
                dbgEffectType = effectType;
                dbgTargetCount = targetCount;

                if (verbose)
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
                            if (verbose) Log.Print(LogType.Server, $"PowerDrain: target={pdGuid} amount={pdAmount} power={pdPowerType}");
                            break;
                        case 10: // HEAL
                            var hGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint hAmount = packet.ReadUInt32();
                            uint hCrit = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"Heal: target={hGuid} amount={hAmount} crit={hCrit}");
                            break;
                        case 30: // ENERGIZE
                            var eGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint eAmount = packet.ReadUInt32();
                            uint ePowerType = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"Energize: target={eGuid} amount={eAmount} power={ePowerType}");
                            break;
                        case 32: // EXTRA_ATTACKS (vanilla value 32)
                            var eaGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint eaCount = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"ExtraAttacks: target={eaGuid} count={eaCount}");
                            break;
                        case 24: // CREATE_ITEM
                            uint ciItemId = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"CreateItem: item={ciItemId}");
                            break;
                        case 41: // INTERRUPT_CAST
                            var icGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint icSpellId = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"InterruptCast: target={icGuid} spell={icSpellId}");
                            break;
                        case 3: // DUMMY — server-side script triggers the real mechanic.
                                // Paladin Holy Shock (20930), various Judgements, etc.
                                // No per-target payload follows.
                        case 50: // TRANS_DOOR — Lightwell, Mage Portal, etc. No per-target payload.
                        case 69: // DISTRACT — positional spell, no per-target payload.
                        case 77: // SCRIPT_EFFECT — Judgement (20271) and many other script-driven spells.
                        case 56:  // SUMMON_PET
                        case 63:  // TAMECREATURE
                        case 102: // Dismiss Pet
                        case 104: // Hunter abilities — no per-target payload.
                            if (verbose) Log.Print(LogType.Server, $"NoPayload(type={effectType}): no target payload");
                            break;
                        case 101: // FEED_PET
                            uint fpItemId = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"FeedPet: item={fpItemId}");
                            break;
                        case 113: // DURABILITY_DAMAGE
                            var ddGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            uint ddItemId = packet.ReadUInt32();
                            uint ddAmount = packet.ReadUInt32();
                            if (verbose) Log.Print(LogType.Server, $"DurabilityDmg: target={ddGuid} item={ddItemId} amount={ddAmount}");
                            break;
                        default: // INSTAKILL, RESURRECT, DISPEL, SUMMON, etc — just a GUID
                            var defaultGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                            if (verbose) Log.Print(LogType.Server, $"Default(type={effectType}): target={defaultGuid}");
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

        // Threat translation: heal threat = 0.5 x effective heal, distributed
        // across every mob in combat with the heal target. Overheal generates
        // no threat — feed only the effective amount.
        long effectiveHeal = (long)spell.HealAmount - (long)spell.OverHeal;
        if (effectiveHeal > 0)
        {
            GetSession().ThreatTracker.OnHeal(spell.CasterGUID, spell.TargetGUID, (int)spell.SpellID, effectiveHeal);
        }
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

        // Threat translation: feed periodic damage (DoT ticks) into the tracker.
        // Sum the damage portion of all PeriodicDamage / PeriodicDamagePercent
        // effect entries; heal ticks generate heal threat instead.
        double dotDamage = 0;
        double hotHeal = 0;
        foreach (var effect in spell.Effects)
        {
            if (effect.Effect == (uint)AuraType.PeriodicDamage ||
                effect.Effect == (uint)AuraType.PeriodicDamagePercent)
            {
                dotDamage += effect.Amount;
            }
            else if (effect.Effect == (uint)AuraType.PeriodicHeal)
            {
                hotHeal += effect.Amount;
            }
        }
        if (dotDamage > 0)
        {
            GetSession().ThreatTracker.OnDamage(spell.CasterGUID, spell.TargetGUID, (int)spell.SpellID, dotDamage);
        }
        if (hotHeal > 0)
        {
            // HoT ticks don't carry overheal info on the wire, so we feed
            // the raw amount. Slight overcount when the target is at max hp;
            // acceptable at this stage.
            GetSession().ThreatTracker.OnHeal(spell.CasterGUID, spell.TargetGUID, (int)spell.SpellID, hotHeal);
        }
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

        // Threat translation: damage-shield reflects (Thorns, Retribution Aura,
        // Lightning Shield) generate threat to the attacker who hit us. The
        // CasterGUID here is whoever owns the shield (us or our pet), the
        // VictimGUID is the attacker who got reflected on. Pass the shield
        // spell id so Holy Shield (x1.2) and friends pick up the right mult.
        GetSession().ThreatTracker.OnDamage(spell.CasterGUID, spell.VictimGUID, (int)spell.SpellID, spell.Damage);
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

        if (WorldClient.ShouldDropModScaleAura(guid, (uint)aura.AuraData.SpellID))
        {
            Framework.Logging.Log.Event("aura.slot.dropped_mod_scale_npc", new
            {
                target_low = guid.GetCounter(),
                high_type = guid.GetHighType().ToString(),
                slot = (int)slot,
                spell_id = aura.AuraData.SpellID,
                source = "extra_aura_info",
            });
            return;
        }

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

        if (WorldClient.ShouldDropModScaleAura(target, spellId))
        {
            Framework.Logging.Log.Event("aura.slot.dropped_mod_scale_npc", new
            {
                target_low = target.GetCounter(),
                high_type = target.GetHighType().ToString(),
                slot = (int)slot,
                spell_id = spellId,
                source = "refresh",
            });
            return;
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
