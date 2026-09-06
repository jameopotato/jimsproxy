using Framework;
using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_MOVE_CHANGE_TRANSPORT)]
    [PacketHandler(Opcode.CMSG_MOVE_DISMISS_VEHICLE)]
    [PacketHandler(Opcode.CMSG_MOVE_FALL_LAND)]
    [PacketHandler(Opcode.CMSG_MOVE_FALL_RESET)]
    [PacketHandler(Opcode.CMSG_MOVE_HEARTBEAT)]
    [PacketHandler(Opcode.CMSG_MOVE_JUMP)]
    [PacketHandler(Opcode.CMSG_MOVE_REMOVE_MOVEMENT_FORCES)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_FACING)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_FACING_HEARTBEAT)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_FLY)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_PITCH)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_RUN_MODE)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_WALK_MODE)]
    [PacketHandler(Opcode.CMSG_MOVE_START_ASCEND)]
    [PacketHandler(Opcode.CMSG_MOVE_START_BACKWARD)]
    [PacketHandler(Opcode.CMSG_MOVE_START_DESCEND)]
    [PacketHandler(Opcode.CMSG_MOVE_START_FORWARD)]
    [PacketHandler(Opcode.CMSG_MOVE_START_PITCH_DOWN)]
    [PacketHandler(Opcode.CMSG_MOVE_START_PITCH_UP)]
    [PacketHandler(Opcode.CMSG_MOVE_START_SWIM)]
    [PacketHandler(Opcode.CMSG_MOVE_START_TURN_LEFT)]
    [PacketHandler(Opcode.CMSG_MOVE_START_TURN_RIGHT)]
    [PacketHandler(Opcode.CMSG_MOVE_START_STRAFE_LEFT)]
    [PacketHandler(Opcode.CMSG_MOVE_START_STRAFE_RIGHT)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP_ASCEND)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP_PITCH)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP_STRAFE)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP_SWIM)]
    [PacketHandler(Opcode.CMSG_MOVE_STOP_TURN)]
    [PacketHandler(Opcode.CMSG_MOVE_DOUBLE_JUMP)]
    void HandlePlayerMove(ClientPlayerMovement movement)
    {
        // JimsProxy (transport-clear source-gate, staleness fix): keep the gate's source
        // observation TRUE. DiagLastObservedPlayerTransportGuid was only written from the
        // player's own UpdateObject block — which never fires for mid-session boarding (own
        // movement is client-authoritative and arrives HERE, not via UpdateObject) — so a
        // player who logged in on land and then boarded a boat/zeppelin was still "observed
        // off-transport", and TransportClearGate skipped the dc39c39 stale-attach clear at
        // the map boundary: the #331-class wedge regressed for the first transport leg after
        // any login/teleport. Every own movement packet carries current transport state
        // (empty when off-transport); stamp it so the observation tracks truth continuously.
        // Stamped before the CHANGE_TRANSPORT drop below so boarding via that opcode counts.
        GetSession().GameState.DiagLastObservedPlayerTransportGuid = movement.MoveInfo.TransportGuid;

        // JimsProxy (charge strafe-latch cure 2026-08-28, re-anchored 2026-08-29):
        // fire the cure the moment the orphan is observed — the armed pend's real
        // strafe bit set with the pend bit gone in the client's own reported flags
        // (lands at SPLINE_DONE's same-ms MOVE_STOP or the FALL_LAND up to ~250ms
        // later; both shapes wire-observed). v1 anchored on the player's
        // post-charge SPLINE_UNROOT, which does not exist — Kronos spline-roots
        // the charge TARGET, never the charging player. The synth ROOT wipes the
        // client's movement flags and makes it emit the matching stop opcodes
        // (correcting Kronos's view too); the synth UNROOT rebuilds from physical
        // key state, so a genuinely-held strafe resumes same-frame. Both acks are
        // swallowed by counter in HandleMoveForceAck2.
        long pendLatchArmedAt = GetSession().GameState.ChargePendLatchArmedAtMs;
        if (pendLatchArmedAt != 0)
        {
            long nowMs = Environment.TickCount64;
            if (!World.Client.ChargePendLatchCure.IsArmed(pendLatchArmedAt, nowMs))
            {
                GetSession().GameState.ChargePendLatchArmedAtMs = 0;
            }
            else if (Framework.Settings.ChargePendLatchCure &&
                     World.Client.ChargePendLatchCure.ShouldFire(
                         GetSession().GameState.ChargePendLatchArmedFlags, movement.MoveInfo.Flags))
            {
                GetSession().GameState.ChargePendLatchArmedAtMs = 0;
                MoveSetFlag cureRoot = new MoveSetFlag(Opcode.SMSG_MOVE_ROOT);
                cureRoot.MoverGUID = GetSession().GameState.CurrentPlayerGuid;
                cureRoot.MoveCounter = World.Client.ChargePendLatchCure.SynthCounterRoot;
                SendPacket(cureRoot);
                MoveSetFlag cureUnroot = new MoveSetFlag(Opcode.SMSG_MOVE_UNROOT);
                cureUnroot.MoverGUID = GetSession().GameState.CurrentPlayerGuid;
                cureUnroot.MoveCounter = World.Client.ChargePendLatchCure.SynthCounterUnroot;
                SendPacket(cureUnroot);
                // DebugOutput-gated per the diagnostics rubric: this is the fix working,
                // not an unexpected-edge signature (review 2026-09-05).
                if (Framework.Settings.DebugOutput)
                    Framework.Logging.Log.Event("charge.pend_latch.cure_sent", new
                    {
                        armed_flags = GetSession().GameState.ChargePendLatchArmedFlags,
                        observed_flags = movement.MoveInfo.Flags,
                        trigger_opcode = movement.GetUniversalOpcode().ToString(),
                        armed_age_ms = nowMs - pendLatchArmedAt,
                    });
            }
        }

        bool isMoveStart = IsMovementStartOpcode(movement.GetUniversalOpcode());

        // JimsProxy (PR #161 follow-up — movement preemption): mark any in-flight
        // cast-time spell as movement-cancelled the moment the user starts moving.
        // Vanilla cancels cast-time spells on movement; the modern 1.14 client
        // predicts this client-side and dismisses its own cast bar before the
        // server's SMSG_SPELL_FAILURE round-trips. Without the marker, the
        // trailing failure surfaces as a misleading "You are in combat" popup
        // (vmangos hardcodes the wire reason in SendInterrupted) or a redundant
        // CastFailed that re-flashes the action button. Also arms the watchdog
        // so the entry doesn't leak if the legacy server response never arrives.
        if (isMoveStart)
        {
            // JimsProxy (strafe cancel-gap 2026-08-16): the 1.14 client sends
            // CMSG_CANCEL_CAST atomically with forward/back/jump starts (9/9 wire
            // trials) but never on strafe (5/5), so a strafe-cancelled cast waits
            // ~700ms for Kronos's heartbeat-position movement detection instead of
            // the ~190ms cancel-ack round trip. Collect the casts this movement
            // start newly marked so the strafe branch below can synthesize the
            // cancel the client didn't send.
            bool isStrafeStart = movement.GetUniversalOpcode() == Opcode.CMSG_MOVE_START_STRAFE_LEFT
                || movement.GetUniversalOpcode() == Opcode.CMSG_MOVE_START_STRAFE_RIGHT;
            List<ClientCastRequest>? newlyMarked =
                isStrafeStart && Settings.StrafeCancelPreempt ? new List<ClientCastRequest>() : null;

            int marked = GetSession().GameState.MarkStartedCastsMovementCancelled(
                Environment.TickCount64 + 2500, newlyMarked);
            if (marked > 0)
            {
                Framework.Logging.Log.Event("cast.movement_cancel_preempted", new
                {
                    casts_marked = marked,
                    trigger_opcode = movement.GetUniversalOpcode().ToString(),
                });
            }

            // Server-bound only — the client-bound stream stays exactly today's
            // (suppressed SPELL_FAILURE broadcast, trailing CastFailed(DontReport)),
            // just one cancel-ack RTT after the keypress instead of ~700ms. Emitted
            // BEFORE the movement forward at the bottom of this handler, mirroring
            // the client's own cancel+move burst order on forward/back/jump; the
            // legacy socket is FIFO, so the cancel can never hit a cast forwarded
            // after this strafe. ResolveStrafeCancelSpellId gates on the 1.12
            // movement-interrupt flag: a spell Kronos would not movement-interrupt
            // (ranged shots, novelty item casts, unknown server-custom ids) gets no
            // synth and keeps today's behavior. A stale cancel racing SPELL_GO is a
            // server no-op keyed by spell id — the same race the client's own
            // forward/back cancels produce routinely.
            if (newlyMarked != null)
            {
                foreach (var cast in newlyMarked)
                {
                    uint cancelSpellId = ResolveStrafeCancelSpellId(cast);
                    if (cancelSpellId == 0)
                        continue;
                    // Presentation parity: the 1.14 client never predicted this
                    // interrupt (it doesn't self-cancel on strafe), so flag the cast
                    // for HandleSpellFailure to FORWARD the interrupt broadcast rather
                    // than suppress it — otherwise the cast bar silently fizzles
                    // instead of showing the red "Interrupted" the client-sent cancels
                    // (forward/back/jump) render locally.
                    cast.StrafeSynthCancelled = true;
                    WorldPacket cancel = new WorldPacket(Opcode.CMSG_CANCEL_CAST);
                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                        cancel.WriteUInt8(0);
                    cancel.WriteUInt32(cancelSpellId);
                    SendPacketToServer(cancel);
                    if (Framework.Settings.DebugOutput)
                        Framework.Logging.Log.Event("cast.strafe_cancel_synth", new
                        {
                            spell_id = cast.SpellId,
                            legacy_spell_id = cancelSpellId,
                            client_cast_id = cast.ClientGUID.ToString(),
                            trigger_opcode = movement.GetUniversalOpcode().ToString(),
                        });
                }
            }
        }

        // JimsProxy (dance-stuck-on-movement 2026-05-07): if the player has an active
        // client-looping emote (e.g. /dance) and just initiated movement, synthesize a stop
        // SMSG_EMOTE to break the loop. Kronos/Twinstar never broadcast one for movement, so
        // without this the dance loops forever until another emote is used.
        if (GetSession().GameState.LastLoopingEmoteId != 0 && isMoveStart)
        {
            EmoteMessage stopEmote = new EmoteMessage();
            stopEmote.EmoteID = 0; // EMOTE_ONESHOT_NONE — clears the looping animation
            stopEmote.Guid = GetSession().GameState.CurrentPlayerGuid;
            SendPacket(stopEmote);
            Framework.Logging.Log.Event("emote.loop.broken_by_move", new
            {
                last_emote_id = GetSession().GameState.LastLoopingEmoteId,
                age_ms = Environment.TickCount64 - GetSession().GameState.LastLoopingEmoteTickMs,
                trigger_opcode = movement.GetUniversalOpcode().ToString(),
            });
            GetSession().GameState.LastLoopingEmoteId = 0;
        }

        // JimsProxy (wsg-change-transport-dc 2026-05-22): drop CMSG_MOVE_CHANGE_TRANSPORT.
        // It's a TBC+ opcode with no legacy equivalent, so the opcode==0 fallback below
        // would mis-forward it as MSG_MOVE_SET_FACING still carrying its OnTransport block —
        // a "set facing" packet claiming a transport absent on the current map, which
        // vmangos/Kronos kicks for (the ~8-min WSG "header" disconnect). Safe to drop:
        // transport state rides in every ordinary movement packet; 1.12 has no such opcode.
        if (movement.GetUniversalOpcode() == Opcode.CMSG_MOVE_CHANGE_TRANSPORT)
        {
            // JimsProxy (charge strafe-latch cure 2026-08-28): the client emits this
            // packet at every charge GO (spline≈pseudo-transport). A pending strafe
            // start in its flags is the wire-proven orphan signature — the mid-air
            // press whose release the spline will swallow and whose pend the spline
            // exit will apply as a keyless real strafe flag (stuck-strafing-after-
            // Charge, 3/3 field latches + 2/2 deliberate repros). Arm the one-shot
            // cure; it fires at the top of this handler on the first packet showing
            // the pend applied, and is harmless if the key is actually still held
            // (force-unroot rebuilds from physical key state).
            bool pendStrafeArmed = World.Client.ChargePendLatchCure.ShouldArm(movement.MoveInfo.Flags);
            if (pendStrafeArmed)
            {
                GetSession().GameState.ChargePendLatchArmedAtMs = Environment.TickCount64;
                GetSession().GameState.ChargePendLatchArmedFlags = movement.MoveInfo.Flags;
            }
            Framework.Logging.Log.Event("movement.change_transport.dropped", new
            {
                server_build = Framework.Settings.ServerBuild.ToString(),
                on_transport = !movement.MoveInfo.TransportGuid.IsEmpty(),
                pend_latch_armed = pendStrafeArmed,
            });
            return;
        }

        string opcodeName = movement.GetUniversalOpcode().ToString();
        opcodeName = opcodeName.Replace("CMSG", "MSG");
        uint opcode = Opcodes.GetOpcodeValueForVersion(opcodeName, Framework.Settings.ServerBuild);
        if (opcode == 0)
            opcode = Opcodes.GetOpcodeValueForVersion("MSG_MOVE_SET_FACING", Framework.Settings.ServerBuild);

        WorldPacket packet = new WorldPacket(opcode);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(movement.Guid.To64());
        movement.MoveInfo.WriteMovementInfoLegacy(packet);
        SendPacketToServer(packet);
    }

    /// <summary>
    /// JimsProxy: true for movement-start CMSG opcodes that should break a client-side
    /// looping emote. Excludes heartbeats (just position updates), stops, and turn/facing
    /// changes (turning in place shouldn't break /dance).
    /// </summary>
    private static bool IsMovementStartOpcode(Opcode opcode)
    {
        switch (opcode)
        {
            case Opcode.CMSG_MOVE_START_FORWARD:
            case Opcode.CMSG_MOVE_START_BACKWARD:
            case Opcode.CMSG_MOVE_START_STRAFE_LEFT:
            case Opcode.CMSG_MOVE_START_STRAFE_RIGHT:
            case Opcode.CMSG_MOVE_START_SWIM:
            case Opcode.CMSG_MOVE_START_ASCEND:
            case Opcode.CMSG_MOVE_START_DESCEND:
            case Opcode.CMSG_MOVE_JUMP:
            case Opcode.CMSG_MOVE_DOUBLE_JUMP:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// JimsProxy (strafe cancel-gap): pure decision half of the strafe cancel synth —
    /// the legacy spell id to cancel for a newly movement-marked cast, or 0 when the
    /// 1.12 data says the server would not movement-interrupt this spell (ranged shots
    /// like Arcane Shot / Serpent Sting, novelty item casts, unknown server-custom ids).
    /// Keyed by the legacy-effective id: that is both what CMSG_CANCEL_CAST must carry
    /// on the wire and what SpellMovementInterrupt1.csv is keyed by.
    /// </summary>
    internal static uint ResolveStrafeCancelSpellId(ClientCastRequest cast)
    {
        uint cancelSpellId = cast.LegacySpellId != 0 ? cast.LegacySpellId : cast.SpellId;
        return GameData.IsMovementInterruptible(cancelSpellId) ? cancelSpellId : 0;
    }

    [PacketHandler(Opcode.CMSG_MOVE_TELEPORT_ACK)]
    void HandleMoveTeleportAck(MoveTeleportAck teleport)
    {
        // JimsProxy (carried-root cure, same-map variant): the client just proved it
        // processed the teleport — deliver the missing unroot armed at the self
        // MoveTeleport. Sentinel counter; its ack is swallowed in HandleMoveForceAck2.
        if (GetSession().GameState.WorldEntryCureAfterTeleportAck &&
            teleport.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            GetSession().GameState.WorldEntryCureAfterTeleportAck = false;
            GetSession().GameState.ClientBelievesRooted = false;
            MoveSetFlag cureUnroot = new MoveSetFlag(Opcode.SMSG_MOVE_UNROOT);
            cureUnroot.MoverGUID = GetSession().GameState.CurrentPlayerGuid;
            cureUnroot.MoveCounter = World.Client.WorldEntryCeremonyTracker.SynthCounterUnroot;
            SendPacket(cureUnroot);
            Log.Event("worldentry.carried_root_cured", new
            {
                map_id = GetSession().GameState.CurrentMapId,
                path = "teleport_ack",
            });
        }

        // JimsProxy (zep-stuck-no-move 2026-05-14): if this ack corresponds to the
        // synthesized SMSG_MOVE_TELEPORT emitted by HandleNewWorld to clear stale
        // MOVEMENTFLAG_ONTRANSPORT, drop it — the legacy server never sent the
        // teleport and would treat the unsolicited MSG_MOVE_TELEPORT_ACK as
        // malformed input.
        uint pendingSentinel = GetSession().GameState.PendingSyntheticTransportClearAckCounter;
        if (pendingSentinel != 0 &&
            teleport.MoveCounter == pendingSentinel &&
            teleport.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            GetSession().GameState.PendingSyntheticTransportClearAckCounter = 0;
            Framework.Logging.Log.Event("movement.transport_clear.ack_dropped", new
            {
                player_low = teleport.MoverGUID.GetCounter(),
                move_counter = teleport.MoveCounter,
            });
            return;
        }

        WorldPacket packet = new WorldPacket(Opcode.MSG_MOVE_TELEPORT_ACK);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(teleport.MoverGUID.To64());
        else
            packet.WriteGuid(teleport.MoverGUID.To64());
        packet.WriteUInt32(teleport.MoveCounter);
        packet.WriteUInt32(teleport.MoveTime);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_WORLD_PORT_RESPONSE)]
    void HandleWorldPortResponse(WorldPortResponse teleport)
    {
        GetSession().GameState.IsWaitingForWorldPortAck = false;

        // JimsProxy (worldentry stage-0 tripwire): this ack is the client saying it
        // finished loading — close the window telemetry. duration_ms (NEW_WORLD →
        // ack) is the machine-dependent variable the investigation is after.
        var gameState = GetSession().GameState;
        if (gameState.WorldEntryNewWorldTick != 0)
        {
            if (Framework.Settings.DebugOutput)
            {
                int forwarded = gameState.WorldEntryWindowForwardCount;
                Framework.Logging.Log.Event("worldentry.window.closed", new
                {
                    seq = gameState.WorldEntryWindowSeq,
                    duration_ms = Environment.TickCount64 - gameState.WorldEntryNewWorldTick,
                    total_ms = gameState.WorldEntryTransferPendingTick != 0
                        ? Environment.TickCount64 - gameState.WorldEntryTransferPendingTick
                        : -1,
                    forwarded_in_window = forwarded,
                    forward_lines_suppressed = forwarded > WorldEntryForwardLineCap
                        ? forwarded - WorldEntryForwardLineCap
                        : 0,
                });
            }
            gameState.WorldEntryNewWorldTick = 0;
            gameState.WorldEntryTransferPendingTick = 0;
        }

        // JimsProxy (speed-stuck-after-bg-end-while-mounted): arm post-teleport reassert; see memory.
        GetSession().GameState.PendingPostTeleportRunSpeedReassert = true;
        WorldPacket packet = new WorldPacket(Opcode.MSG_MOVE_WORLDPORT_ACK);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_FLIGHT_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_PITCH_RATE_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_RUN_BACK_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_SWIM_BACK_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_SWIM_SPEED_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_TURN_RATE_CHANGE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_WALK_SPEED_CHANGE_ACK)]
    void HandleMoveForceSpeedChangeAck(MovementSpeedAck speed)
    {
        var opcode = speed.GetUniversalOpcode();
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180)
            && opcode is Opcode.CMSG_MOVE_FORCE_FLIGHT_SPEED_CHANGE_ACK
                      or Opcode.CMSG_MOVE_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK)
            return; // This is probably an ack by our swim to fly speed change for vanilla

        WorldPacket packet = new WorldPacket(opcode);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(speed.MoverGUID.To64());
        else
            packet.WriteGuid(speed.MoverGUID.To64());
        packet.WriteUInt32(speed.Ack.MoveCounter);
        speed.Ack.MoveInfo.WriteMovementInfoLegacy(packet);
        packet.WriteFloat(speed.Speed);
        SendPacketToServer(packet);
    }

    MovementFlagModern GetFlagForAckOpcode(Opcode opcode)
    {
        switch (opcode)
        {
            case Opcode.CMSG_MOVE_FEATHER_FALL_ACK:
                return MovementFlagModern.CanSafeFall;
            case Opcode.CMSG_MOVE_HOVER_ACK:
                return MovementFlagModern.Hover;
            case Opcode.CMSG_MOVE_SET_CAN_FLY_ACK:
                return MovementFlagModern.CanFly;
            case Opcode.CMSG_MOVE_WATER_WALK_ACK:
                return MovementFlagModern.Waterwalking;
        }
        return MovementFlagModern.None;
    }

    [PacketHandler(Opcode.CMSG_MOVE_FEATHER_FALL_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_HOVER_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_SET_CAN_FLY_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_WATER_WALK_ACK)]
    void HandleMoveForceAck1(MovementAckMessage movementAck)
    {
        var universalOpcode = movementAck.GetUniversalOpcode();
        uint legacyOpcode = LegacyVersion.GetCurrentOpcode(universalOpcode);
        if (legacyOpcode == 0)
        {
            // The modern client acks proxy-synthesized state changes (e.g. the
            // SMSG_MOVE_UNSET_CAN_FLY sent at the end of a taxi flight) with
            // CMSG_MOVE_SET_CAN_FLY_ACK, which doesn't exist on Vanilla legacy
            // servers. The legacy server never sent the change and expects no ack,
            // so discard it instead of crashing in the WorldPacket(Opcode) assert.
            Log.Event("movement.ack_discarded", new { opcode = universalOpcode.ToString() });
            return;
        }

        WorldPacket packet = new WorldPacket(legacyOpcode);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(movementAck.MoverGUID.To64());
        else
            packet.WriteGuid(movementAck.MoverGUID.To64());
        packet.WriteUInt32(movementAck.Ack.MoveCounter);
        movementAck.Ack.MoveInfo.WriteMovementInfoLegacy(packet);
        packet.WriteInt32(movementAck.Ack.MoveInfo.Flags.HasAnyFlag(GetFlagForAckOpcode(universalOpcode)) ? 1 : 0);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_FORCE_ROOT_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_FORCE_UNROOT_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_KNOCK_BACK_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_GRAVITY_DISABLE_ACK)]
    [PacketHandler(Opcode.CMSG_MOVE_GRAVITY_ENABLE_ACK)]
    void HandleMoveForceAck2(MovementAckMessage movementAck)
    {
        var universalOpcode = movementAck.GetUniversalOpcode();

        // JimsProxy (charge strafe-latch cure 2026-08-28): the ack legs of the synth
        // ROOT+UNROOT pulse. Swallowed for the same reason as the carried-root cure
        // ack below (the legacy server never sent these ops); the flags the client
        // reports in each ack are the field proof of the cure working — the root ack
        // should show the orphaned strafe already wiped. DebugOutput-gated per the
        // diagnostics rubric (fix-working breadcrumb; review 2026-09-05).
        if (World.Client.ChargePendLatchCure.IsCureCounter(movementAck.Ack.MoveCounter))
        {
            if (Framework.Settings.DebugOutput)
                Log.Event("charge.pend_latch.cure_acked", new
                {
                    opcode = universalOpcode.ToString(),
                    move_counter = movementAck.Ack.MoveCounter,
                    client_flags = movementAck.Ack.MoveInfo.Flags,
                });
            return;
        }

        // JimsProxy (carried-root cure): the ack for the proxy-synthesized cure
        // unroot carries a sentinel counter. The legacy server never sent that op,
        // so the ack must not cross (a spurious force-ack can feed Kronos's
        // malformed-input kick counters). The ack's presence is also the proof the
        // client APPLIED the cure — log it always-on.
        if (World.Client.WorldEntryCeremonyTracker.IsSynthCounter(movementAck.Ack.MoveCounter))
        {
            Log.Event("worldentry.carried_root.cure_acked", new
            {
                opcode = universalOpcode.ToString(),
                move_counter = movementAck.Ack.MoveCounter,
            });
            return;
        }

        // JimsProxy (worldentry root-ceremony instrumentation 2026-08-03): the ack
        // legs of the arrival ROOT/UNROOT ceremony. A forwarded-but-never-acked leg
        // is the discard fingerprint (stuck-stun golden capture) the always-on
        // breadcrumb keys on; see WorldEntryCeremony.cs.
        var ceremony = GetSession().GameState.WorldEntryCeremony;
        if (ceremony.Active && movementAck.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            if (universalOpcode == Opcode.CMSG_MOVE_FORCE_ROOT_ACK)
                System.Threading.Interlocked.Increment(ref ceremony.RootAcks);
            else if (universalOpcode == Opcode.CMSG_MOVE_FORCE_UNROOT_ACK)
                System.Threading.Interlocked.Increment(ref ceremony.UnrootAcks);
        }

        uint legacyOpcode = LegacyVersion.GetCurrentOpcode(universalOpcode);
        if (legacyOpcode == 0)
        {
            // CMSG_MOVE_GRAVITY_ENABLE_ACK / CMSG_MOVE_GRAVITY_DISABLE_ACK don't
            // exist on Vanilla/TBC legacy servers. The modern client sends them in
            // response to proxy-synthesized SMSG_MOVE_ENABLE_GRAVITY (e.g. at the
            // end of a taxi flight). The legacy server never sent a gravity packet
            // and expects no ack, so discard it instead of crashing in the
            // WorldPacket(Opcode) constructor's assert.
            Log.Event("movement.ack_discarded", new { opcode = universalOpcode.ToString() });
            return;
        }

        WorldPacket packet = new WorldPacket(legacyOpcode);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(movementAck.MoverGUID.To64());
        else
            packet.WriteGuid(movementAck.MoverGUID.To64());
        packet.WriteUInt32(movementAck.Ack.MoveCounter);
        movementAck.Ack.MoveInfo.WriteMovementInfoLegacy(packet);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_SET_COLLISION_HEIGHT_ACK)]
    void HandleMoveSetCollisionHeightAck(MoveSetCollisionHeightAck collisionHeightAck)
    {
        // This opcode doesn't exist in legacy servers (Vanilla/TBC/WotLK).
        // The modern client sends it in response to SMSG_MOVE_SET_COLLISION_HEIGHT,
        // but legacy servers don't expect or need it. Simply discard the packet.
    }

    [PacketHandler(Opcode.CMSG_SET_ACTIVE_MOVER)]
    void HandleMoveSetActiveMover(SetActiveMover move)
    {
        LogWorldEntryClientSignal("set_active_mover", new
        {
            mover_low = move.MoverGUID.GetCounter(),
            is_self = move.MoverGUID == GetSession().GameState.CurrentPlayerGuid,
        });
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTIVE_MOVER);
        packet.WriteGuid(move.MoverGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_INIT_ACTIVE_MOVER_COMPLETE)]
    void HandleMoveInitActiveMoverComplete(InitActiveMoverComplete move)
    {
        LogWorldEntryClientSignal("init_active_mover_complete");

        // JimsProxy (worldentry root-ceremony instrumentation 2026-08-03): the
        // client's mover re-init completion — the phase boundary the arrival
        // ceremony races (wire-verified: ROOT#1 lands at this boundary on nearly
        // every arrival). Stamp the breadcrumb.
        GetSession().GameState.WorldEntryCeremony.InitMoverCompleteSeen = true;

        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTIVE_MOVER);
        packet.WriteGuid(GetSession().GameState.CurrentPlayerGuid.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_SPLINE_DONE)]
    void HandleMoveSplineDone(MoveSplineDone movement)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_MOVE_SPLINE_DONE);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(movement.Guid.To64());
        movement.MoveInfo.WriteMovementInfoLegacy(packet);
        packet.WriteInt32(movement.SplineID);
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.WriteFloat(0); // Spline Type
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_MOVE_TIME_SKIPPED)]
    void HandleMoveSplineDone(MoveTimeSkipped movement)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_MOVE_TIME_SKIPPED);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
            packet.WritePackedGuid(movement.MoverGUID.To64());
        else
            packet.WriteGuid(movement.MoverGUID.To64());
        packet.WriteUInt32(movement.TimeSkipped);
        SendPacketToServer(packet);
    }
}
