using Framework.GameMath;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Returns true if the mob should be treated as hovering. Detection is OR of:
    //   - UNIT_FIELD_HOVERHEIGHT > 0 (works on cores that populate it)
    //   - KnownHoveringMobs membership (seeded when we see Flying spline / FixedZ flag)
    private bool IsHoveringMob(WowGuid128 guid)
    {
        if (GetSession().GameState.KnownHoveringMobs.Contains(guid))
            return true;
        return GetSession().GameState.GetLegacyFieldValueFloat(guid, UnitField.UNIT_FIELD_HOVERHEIGHT) > 0.0f;
    }

    // MIRASU (swim-mob basketball-bounce 2026-05-23): true if we've seen this mob
    // with MovementFlag.Swimming. Seeded in UpdateHandler.ReadMovementUpdateBlock.
    private bool IsSwimmingMob(WowGuid128 guid)
    {
        return GetSession().GameState.KnownSwimmingMobs.Contains(guid);
    }

    // MIRASU (onyxia-parked-hover-anim): single entry point for hover registry transitions; real changes synth PlayHoverAnim + gravity (vanilla legacy only — later cores drive hover/gravity natively).
    internal void SetHoverState(WowGuid128 guid, bool hovering, string source, bool synthesize = true)
    {
        if (guid == GetSession().GameState.CurrentPlayerGuid)
            return;
        bool changed = hovering
            ? GetSession().GameState.KnownHoveringMobs.Add(guid)
            : GetSession().GameState.KnownHoveringMobs.Remove(guid);
        if (!changed)
            return;
        Framework.Logging.Log.Event(hovering ? "hover.registry.set" : "hover.registry.cleared", new { guid = guid.ToString(), source });
        if (!synthesize || !LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            return;
        SetPlayHoverAnim hoverAnim = new SetPlayHoverAnim();
        hoverAnim.UnitGUID = guid;
        hoverAnim.PlayHoverAnim = hovering;
        SendPacketToClient(hoverAnim);
        MoveSplineSetFlag gravity = new MoveSplineSetFlag(hovering ? Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY : Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY);
        gravity.MoverGUID = guid;
        SendPacketToClient(gravity);
        Framework.Logging.Log.Event("hover.anim_synth", new { guid = guid.ToString(), hovering });
    }

    // Strips Falling/FallingFar and forces DisableGravity on a hovering mob's movement
    // flags. Call BEFORE casting WotLK flags to Modern. Vanilla servers don't have
    // AnimTier/HoverHeight in their movement protocol, so hovering mobs bleed Falling
    // into every heartbeat/spline-stop, causing the modern client to ground-snap and
    // basketball-bounce them between flight legs.
    private bool ApplyHoverOverrideIfNeeded(WowGuid128 guid, MovementInfo moveInfo)
    {
        if (!IsHoveringMob(guid))
            return false;

        moveInfo.Flags &= ~(uint)(MovementFlagWotLK.Falling | MovementFlagWotLK.FallingFar);
        moveInfo.Flags |= (uint)MovementFlagWotLK.DisableGravity;
        moveInfo.FallTime = 0;
        moveInfo.JumpVerticalSpeed = 0.0f;
        moveInfo.JumpHorizontalSpeed = 0.0f;
        return true;
    }

    // MIRASU (swim-mob basketball-bounce 2026-05-23): the flag bit positions differ
    // between WotLK (the proxy's internal storage format after Vanilla→WotLK cast)
    // and Modern. Specifically:
    //   WotLK.Swimming      = 0x200000  (bit 21)
    //   Modern.Swimming     = 0x100000  (bit 20)
    //   Modern bit 21       = Ascending
    // The proxy writes info.Flags raw to the modern wire (no name-based conversion),
    // so a swimming NPC ends up with the modern Ascending flag set — the 1.14 client
    // then renders it as continuously ascending = visible up/down bouncing on the
    // water surface with walk anim. This mirrors the workaround the hover override
    // uses: explicitly strip the wrong bits and set the right modern ones.
    // Also strip Falling/FallingFar so the client doesn't try to ground-snap.
    private bool ApplySwimOverrideIfNeeded(WowGuid128 guid, MovementInfo moveInfo)
    {
        if (!IsSwimmingMob(guid))
            return false;

        // Clear WotLK's Swimming bit position (which would show up as Ascending on modern)
        // and clear falling flags. Set modern Swimming bit (0x100000) directly.
        moveInfo.Flags &= ~(uint)(MovementFlagWotLK.Swimming | MovementFlagWotLK.Falling | MovementFlagWotLK.FallingFar);
        moveInfo.Flags |= (uint)MovementFlagModern.Swimming;
        // MIRASU (swim moving anim 2026-05-23): DisableGravity is the anti-gravity bit
        // that doesn't force a flight anim (unlike SplineFlagModern.Flying). Pairs with
        // UnitFlags.CanSwim on UNIT_FIELD_FLAGS so the client renders swim moving anim
        // instead of bouncing the unit on the water surface.
        moveInfo.Flags |= (uint)MovementFlagWotLK.DisableGravity;
        moveInfo.FallTime = 0;
        moveInfo.JumpVerticalSpeed = 0.0f;
        moveInfo.JumpHorizontalSpeed = 0.0f;
        return true;
    }

    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.MSG_MOVE_START_FORWARD)]
    [PacketHandler(Opcode.MSG_MOVE_START_BACKWARD)]
    [PacketHandler(Opcode.MSG_MOVE_STOP)]
    [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_LEFT)]
    [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_RIGHT)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_STRAFE)]
    [PacketHandler(Opcode.MSG_MOVE_START_ASCEND)]
    [PacketHandler(Opcode.MSG_MOVE_START_DESCEND)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_ASCEND)]
    [PacketHandler(Opcode.MSG_MOVE_JUMP)]
    [PacketHandler(Opcode.MSG_MOVE_START_TURN_LEFT)]
    [PacketHandler(Opcode.MSG_MOVE_START_TURN_RIGHT)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_TURN)]
    [PacketHandler(Opcode.MSG_MOVE_START_PITCH_UP)]
    [PacketHandler(Opcode.MSG_MOVE_START_PITCH_DOWN)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_PITCH)]
    [PacketHandler(Opcode.MSG_MOVE_SET_RUN_MODE)]
    [PacketHandler(Opcode.MSG_MOVE_SET_WALK_MODE)]
    [PacketHandler(Opcode.MSG_MOVE_TELEPORT)]
    [PacketHandler(Opcode.MSG_MOVE_SET_FACING)]
    [PacketHandler(Opcode.MSG_MOVE_SET_PITCH)]
    [PacketHandler(Opcode.MSG_MOVE_TOGGLE_COLLISION_CHEAT)]
    [PacketHandler(Opcode.MSG_MOVE_GRAVITY_CHNG)]
    [PacketHandler(Opcode.MSG_MOVE_ROOT)]
    [PacketHandler(Opcode.MSG_MOVE_UNROOT)]
    [PacketHandler(Opcode.MSG_MOVE_START_SWIM)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM)]
    [PacketHandler(Opcode.MSG_MOVE_START_SWIM_CHEAT)]
    [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM_CHEAT)]
    [PacketHandler(Opcode.MSG_MOVE_HEARTBEAT)]
    [PacketHandler(Opcode.MSG_MOVE_FALL_LAND)]
    [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_FLY)]
    [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_TRANSITION_BETWEEN_SWIM_AND_FLY)]
    [PacketHandler(Opcode.MSG_MOVE_HOVER)]
    [PacketHandler(Opcode.MSG_MOVE_FEATHER_FALL)]
    [PacketHandler(Opcode.MSG_MOVE_WATER_WALK)]
    void HandleMovementMessages(WorldPacket packet)
    {
        MoveUpdate moveUpdate = new MoveUpdate();
        moveUpdate.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        // JimsProxy (out-of-range-ghost): drop a stray movement packet for a unit we just destroyed/out-of-ranged so it can't re-ghost the unit running-in-place on the modern client. Runs BEFORE the observed-bow move edge — a destroyed unit isn't rendered, so its stray move must neither reach the client nor count as a stop edge (a dead latched shooter is retracted by the death edge; an out-of-ranged one despawns and its latch ages out via the sweep).
        if (moveUpdate.MoverGUID != GetSession().GameState.CurrentPlayerGuid &&
            GetSession().GameState.WasObjectRecentlyDestroyed(moveUpdate.MoverGUID, out long ghostAgoMs))
        {
            if (Framework.Settings.DebugOutput)
                Framework.Logging.Log.Event("movement.dropped_stray_after_destroy", new
                {
                    guid = moveUpdate.MoverGUID.GetCounter(),
                    ms_since_destroy = ghostAgoMs,
                    opcode = packet.GetUniversalOpcode(false).ToString(),
                });
            return;
        }
        // JimsProxy (observed-bow retract): a unit physically moving stops its auto-repeat server-side — lower a latched observed shooter's bow on a translational-start move (excludes the local player; turn/facing/heartbeat/stop are not stop edges). A still-firing shooter that briefly steps self-heals on its next shot's START.
        if (moveUpdate.MoverGUID != GetSession().GameState.CurrentPlayerGuid &&
            IsAutoRepeatStoppingMove(packet.GetUniversalOpcode(false)))
        {
            RetractObservedShooterOnStop(moveUpdate.MoverGUID);
        }
        moveUpdate.MoveInfo = new();
        moveUpdate.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
        ApplyHoverOverrideIfNeeded(moveUpdate.MoverGUID, moveUpdate.MoveInfo);
        // JimsProxy (Tallstrider-Fix): cache facing for the spline-angle check.
        if (!moveUpdate.MoverGUID.IsEmpty())
            GetSession().GameState.LastKnownOrientation[moveUpdate.MoverGUID] = moveUpdate.MoveInfo.Orientation;
        moveUpdate.MoveInfo.Flags = (uint)(((MovementFlagWotLK)moveUpdate.MoveInfo.Flags).CastFlags<MovementFlagModern>());
        moveUpdate.MoveInfo.ValidateMovementInfo();
        SendPacketToClient(moveUpdate);
    }

    // JimsProxy (observed-bow retract): translational-start moves mean the mover left its firing stance (auto-shot requires standing still); turn/facing/heartbeat/stop are excluded so a shooter that only turns to track its target isn't falsely retracted.
    private static bool IsAutoRepeatStoppingMove(Opcode op) => op switch
    {
        Opcode.MSG_MOVE_START_FORWARD or
        Opcode.MSG_MOVE_START_BACKWARD or
        Opcode.MSG_MOVE_START_STRAFE_LEFT or
        Opcode.MSG_MOVE_START_STRAFE_RIGHT or
        Opcode.MSG_MOVE_JUMP or
        Opcode.MSG_MOVE_START_SWIM => true,
        _ => false,
    };

    [PacketHandler(Opcode.MSG_MOVE_KNOCK_BACK)]
    void HandleMoveKnockBack(WorldPacket packet)
    {
        MoveUpdateKnockBack knockback = new MoveUpdateKnockBack();
        knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        knockback.MoveInfo = new();
        knockback.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
        knockback.MoveInfo.Flags = (uint)(((MovementFlagWotLK)knockback.MoveInfo.Flags).CastFlags<MovementFlagModern>());
        knockback.MoveInfo.JumpSinAngle = packet.ReadFloat();
        knockback.MoveInfo.JumpCosAngle = packet.ReadFloat();
        knockback.MoveInfo.JumpHorizontalSpeed = packet.ReadFloat();
        knockback.MoveInfo.JumpVerticalSpeed = packet.ReadFloat();
        knockback.MoveInfo.ValidateMovementInfo();
        SendPacketToClient(knockback);
    }

    [PacketHandler(Opcode.SMSG_MOVE_KNOCK_BACK)]
    void HandleMoveForceKnockBack(WorldPacket packet)
    {
        MoveKnockBack knockback = new MoveKnockBack();
        knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        knockback.MoveCounter = packet.ReadUInt32();
        knockback.Direction = packet.ReadVector2();
        knockback.HorizontalSpeed = packet.ReadFloat();
        knockback.VerticalSpeed = packet.ReadFloat();
        SendPacketToClient(knockback);
    }

    // JimsProxy (move-time-skipped translation): the legacy server relays
    // MSG_MOVE_TIME_SKIPPED (vanilla 0x319) as a peer clock-continuity signal — when
    // *another* nearby player's client hitches/alt-tabs/loads, its movement clock
    // jumps and the server tells observers so their per-unit movement-time base for
    // that mover stays aligned with the (now time-jumped) movement packets that
    // follow. Upstream dropped this as an unknown s2c opcode; the 1.14 client has a
    // live handler for the modern twin SMSG_MOVE_SKIP_TIME (0x2E18), so we translate
    // rather than drop. Wire: packed 64-bit guid + uint32 (all reference cores relay
    // this shape). The relay is observer-only on every core (the server excludes the
    // originator), so a self-targeted skip shouldn't arrive — drop it defensively so
    // it can't fight the client's own movement clock. Also drop for a just-destroyed
    // mover (mirrors the WasObjectRecentlyDestroyed gate in HandleMovementMessages),
    // so a stale skip can't touch a unit we already retired.
    //
    // Possible relation to #418 (observed players "move laggy / delayed") — WEAK and
    // unproven. That investigation exonerated the proxy's outbound leg and pinned the
    // dominant cause to client-side cross-engine jump-arc reconstruction, not this.
    // But it flagged one untested residual: "a laggy sender's TIME_SKIPPED path is
    // code-verified but not field-exercised" (0 skips seen in that low-latency
    // session). This handler is the observer-side (s2c) half of that path — before
    // it, a hitching/lagging peer's clock skip was dropped, so observers never
    // re-based that mover's movement time. Whether that feeds the #418 symptom is
    // unproven; the peer-hitch-under-lag A/B is exactly the field test #418 lacked.
    [PacketHandler(Opcode.MSG_MOVE_TIME_SKIPPED)]
    void HandleMoveTimeSkipped(WorldPacket packet)
    {
        MoveSkipTime skip = new MoveSkipTime();
        skip.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        skip.TimeSkipped = packet.ReadUInt32();

        if (skip.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
            return;
        if (GetSession().GameState.WasObjectRecentlyDestroyed(skip.MoverGUID, out _))
            return;

        SendPacketToClient(skip);
    }

    [PacketHandler(Opcode.SMSG_CONTROL_UPDATE)]
    void HandleControlUpdate(WorldPacket packet)
    {
        ControlUpdate control = new ControlUpdate();
        control.Guid = packet.ReadPackedGuid().To128(GetSession().GameState);
        control.HasControl = packet.ReadBool();

        // JimsProxy (camp stun lock, step 2): a self control-update arriving before
        // the login's first self create block is part of the wedge's lock recipe —
        // hold it (and the walk-fix speed reassert below, so the pair stays in its
        // emission order) for release right after the create forwards. Proxy-side
        // bookkeeping below runs at translate time regardless.
        bool heldPreCreate = control.Guid == GetSession().GameState.CurrentPlayerGuid &&
            GetSession().GameState.PreCreateOpHold.TryCapture(control);
        if (!heldPreCreate)
            SendPacketToClient(control);

        // --- Mirasu RP Walk Bug Fix ---
        // The 1.14 client forgets to un-toggle Walk mode after CC wears off.
        // When control is RESTORED (false → true transition) we explicitly force
        // a run speed update to reset the UI toggle.
        //
        // Two correctness gates:
        //
        // 1. Use the modern universal opcode SMSG_MOVE_SET_RUN_SPEED. MoveSetSpeed
        //    extends ServerPacket whose constructor calls
        //    ModernVersion.GetCurrentOpcode(). The legacy-only
        //    SMSG_FORCE_RUN_SPEED_CHANGE has no entry in the modern (1.14) opcode
        //    table so the lookup returns 0 and trips Trace.Assert(opcode != 0)
        //    in Packet.cs:73 (visible Linux Debug crash) and serializes 0x0000
        //    onto the wire on Release (silent — modern client drops the malformed
        //    packet, so the fix has historically been a no-op). The neighboring
        //    handler at MovementHandler.cs:~298 already does the
        //    SMSG_FORCE_*_CHANGE → SMSG_MOVE_SET_* translation when forwarding
        //    incoming legacy speed-change packets.
        //
        // 2. Only fire on a *transition* from no-control → has-control. The
        //    server emits SMSG_CONTROL_UPDATE(HasControl=true) on login and on
        //    /reload too, not just when CC ends. Without this gate the login
        //    handler hardcodes 7.0f and clobbers any active speed buff (mount,
        //    sprint, aspect of the cheetah, druid travel form) — observable as
        //    "log in mounted, walk at unmounted speed until I remount."
        bool isLocalPlayer = control.Guid == GetSession().GameState.CurrentPlayerGuid;
        bool justRegainedControl = control.HasControl && !GetSession().GameState.LastObservedHasControl;

        // JimsProxy (diag): SMSG_CONTROL_UPDATE body is one PackGUID + one byte
        // HasControl bool, neither captured by the existing packet.in event (size +
        // opcode only). Without HasControl in the log, post-incident triage of
        // movement-wedge bugs has to guess whether the proxy saw a freeze (false)
        // or a restore (true) — including for the BG-exit lockout family where the
        // entire question is "did the server send the restore packet or not." Emit
        // the parsed values + the transition signal so future logs answer that
        // directly. Zero behavior impact; one structured event per inbound packet.
        Framework.Logging.Log.Event("movement.control_update.observed", new
        {
            guid_low = control.Guid.GetCounter(),
            has_control = control.HasControl,
            is_local_player = isLocalPlayer,
            last_observed_before = GetSession().GameState.LastObservedHasControl,
            just_regained_control = justRegainedControl,
        });

        if (isLocalPlayer)
            GetSession().GameState.LastObservedHasControl = control.HasControl;

        if (isLocalPlayer && justRegainedControl)
        {
            // JimsProxy (speed-stuck-after-fear-while-mounted): reassert cached speed, not 7.0f; see memory.
            MoveSetSpeed runFix = new MoveSetSpeed(Opcode.SMSG_MOVE_SET_RUN_SPEED);
            runFix.MoverGUID = control.Guid;
            runFix.MoveCounter = 0;
            runFix.Speed = GetSession().GameState.LastKnownPlayerRunSpeed;
            // step-2 hold: ride behind the held control-update in emission order.
            if (!heldPreCreate || !GetSession().GameState.PreCreateOpHold.TryCapture(runFix))
                SendPacketToClient(runFix);
        }
    }

    // JimsProxy (taxi-resume-control-stuck #330): packets the 1.14 client needs to leave the taxi/passenger state — control restore + gravity + clear-fly + unroot. Mirrors the dismount Task; used by the resumed-taxi SPLINE_ENABLED-clear path in ReadMovementUpdateBlock which never reaches HandleMonsterMove. See memory.
    public void SendTaxiDismountRestore(WowGuid128 guid)
    {
        ControlUpdate control = new ControlUpdate();
        control.Guid = guid;
        control.HasControl = true;
        SendPacketToClient(control);

        MoveSetFlag enableGravity = new MoveSetFlag(Opcode.SMSG_MOVE_ENABLE_GRAVITY);
        enableGravity.MoverGUID = guid;
        SendPacketToClient(enableGravity);

        MoveSetFlag unsetFly = new MoveSetFlag(Opcode.SMSG_MOVE_UNSET_CAN_FLY);
        unsetFly.MoverGUID = guid;
        SendPacketToClient(unsetFly);

        MoveSetFlag unroot = new MoveSetFlag(Opcode.SMSG_MOVE_UNROOT);
        unroot.MoverGUID = guid;
        SendPacketToClient(unroot);
    }

    // JimsProxy (taxi-resume-control-stuck #330): a resumed taxi's flight spline arrives only in the player's CREATE block (UpdateHandler), bypassing HandleMonsterMove's dismount scheduling; schedule the same dismount off the create-spline's remaining duration so control/gravity restore fires at landing. Mirrors the HandleMonsterMove dismount Task (CTS + atomic claim + cancel-on-disconnect via TaxiDismountCts). See memory.
    public void ScheduleTaxiResumeDismount(WowGuid128 guid, uint remainingMs)
    {
        var gameState = GetSession().GameState;
        System.Threading.Volatile.Write(ref gameState.IsInTaxiFlight, true);
        gameState.CancelTaxiDismount("taxi_resume_reschedule");

        const uint TAXI_FLIGHT_MAX_MS = 600_000;
        int delayMs = (int)Math.Min(remainingMs, TAXI_FLIGHT_MAX_MS) + 250;
        var cts = new System.Threading.CancellationTokenSource();
        gameState.TaxiDismountCts = cts;
        gameState.TaxiDismountFiresAtTickMs = Environment.TickCount64 + delayMs;

        var capturedSession = GetSession();
        var capturedClient = this;
        var token = cts.Token;
        WowGuid128 playerGuid = guid;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await System.Threading.Tasks.Task.Delay(delayMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var prior = System.Threading.Interlocked.CompareExchange(ref capturedSession.GameState.TaxiDismountCts, null, cts);
            if (!ReferenceEquals(prior, cts))
                return;

            // We own the dismount; cts + bookkeeping cleanup is now our responsibility
            // (mirrors the HandleMonsterMove dismount Task's finally).
            try
            {
                if (!System.Threading.Volatile.Read(ref capturedSession.GameState.IsInTaxiFlight))
                    return;
                if (capturedSession.InstanceSocket == null)
                    return;

                capturedClient.SendTaxiDismountRestore(playerGuid);
                System.Threading.Volatile.Write(ref capturedSession.GameState.IsInTaxiFlight, false);
            }
            finally
            {
                capturedSession.GameState.TaxiDismountFiresAtTickMs = 0;
                cts.Dispose();
            }
        });
    }

    [PacketHandler(Opcode.MSG_MOVE_TELEPORT_ACK)]
    void HandleMoveTeleportAck(WorldPacket packet)
    {
        WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);

        if (System.Threading.Volatile.Read(ref GetSession().GameState.IsInTaxiFlight) &&
            GetSession().GameState.CurrentPlayerGuid == guid)
        {
            ControlUpdate control = new ControlUpdate();
            control.Guid = guid;
            control.HasControl = true;
            SendPacketToClient(control);
            System.Threading.Volatile.Write(ref GetSession().GameState.IsInTaxiFlight, false);
            // JimsProxy (taxi-flight-robustness): teleport-ack ended the flight (zone change,
            // early-arrival sync, etc.) — cancel the pending dismount Task so it doesn't fire
            // duplicate control/gravity packets after the player has already regained control.
            GetSession().GameState.CancelTaxiDismount("teleport_ack_during_flight");
        }

        MoveTeleport teleport = new MoveTeleport();
        teleport.MoverGUID = guid;
        teleport.MoveCounter = packet.ReadUInt32();
        MovementInfo moveInfo = new();
        moveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
        moveInfo.Flags = (uint)(((MovementFlagWotLK)moveInfo.Flags).CastFlags<MovementFlagModern>());
        moveInfo.ValidateMovementInfo();
        teleport.Position = moveInfo.Position;
        teleport.Orientation = moveInfo.Orientation;
        teleport.TransportGUID = moveInfo.TransportGuid;
        if (moveInfo.TransportSeat > 0)
        {
            teleport.Vehicle = new();
            teleport.Vehicle.VehicleSeatIndex = moveInfo.TransportSeat;
        }
        // JimsProxy (zep-stuck-no-move 2026-05-14, belt-and-suspenders): a real
        // teleport from the legacy server supersedes any pending synthetic
        // transport-clear ack we were watching for. Clear the sentinel so a future
        // legitimate ack carrying the same MoveCounter cannot be eaten.
        if (guid == GetSession().GameState.CurrentPlayerGuid)
        {
            GetSession().GameState.PendingSyntheticTransportClearAckCounter = 0;

            // JimsProxy (carried-root cure, same-map variant): hearth/tele/portal
            // within a map is a MoveTeleport, not a NEW_WORLD — a stranded root
            // crosses this loading screen too. Belief-only gate (the teleport's
            // MovementInfo flags are an echo of the client's own stuck state — see
            // ShouldCureCarriedRoot); deliver only once the client ACKS the
            // teleport (proof it processed it — an unroot delivered while the
            // teleport is pending could be lost). Armed BEFORE the pre-create hold
            // below: a held teleport is still delivered post-create, so its
            // eventual client ack — the cure's trigger — still comes.
            if (Framework.Settings.WorldEntryCarriedRootCure &&
                WorldEntryCeremonyTracker.ShouldCureCarriedRoot(GetSession().GameState.ClientBelievesRooted))
            {
                GetSession().GameState.WorldEntryCureAfterTeleportAck = true;
                if (Framework.Settings.DebugOutput)
                    Framework.Logging.Log.Event("worldentry.carried_root.armed", new { path = "move_teleport" });
            }

            // JimsProxy (camp stun lock, step 2): a SERVER-originated self teleport
            // before the login's first self create block joins the pre-create hold
            // (R56 op set). Our own transport-clear synth doesn't route through this
            // handler and stays unheld — it is present on every healthy login and
            // provably not part of the lock recipe.
            if (GetSession().GameState.PreCreateOpHold.TryCapture(teleport))
                return;
        }
        SendPacketToClient(teleport);
    }

    [PacketHandler(Opcode.SMSG_TRANSFER_PENDING)]
    void HandleTransferPending(WorldPacket packet)
    {
        uint transferMapId = packet.ReadUInt32();

        // JimsProxy (camp login-eviction merge): a transfer arriving while the login
        // stream is held is the over-cap eviction announcing itself. Swallow it
        // client-side (no TransferPending, no SuspendToken, no transfer flags) — the
        // NEW_WORLD that follows is merged into the held login-verify in
        // HandleNewWorld. Always-on event: this only fires on the bug.
        var evictionHold = GetSession().GameState.LoginEvictionHold;
        if (evictionHold.OnTransferPending(transferMapId))
        {
            GetSession().GameState.PendingTransferMapId = transferMapId;
            Log.Event("login.eviction_hold.transfer_pending", new
            {
                login_map_id = evictionHold.LoginMapId,
                transfer_map_id = transferMapId,
                ms_since_login_verify = Environment.TickCount64 - evictionHold.StartTick,
            });
            return;
        }

        if (GetSession().GameState.IsWaitingForWorldPortAck)
        {
            Log.Print(LogType.Error, "Skipping SMSG_TRANSFER_PENDING, client is already being teleported.");
            return;
        }

        // JimsProxy (worldentry root-ceremony breadcrumb): the previous arrival's
        // ceremony accounting ends where the next transition begins.
        FlushWorldEntryCeremony("transfer_pending");

        TransferPending transfer = new TransferPending();
        transfer.MapID = GetSession().GameState.PendingTransferMapId = transferMapId;
        transfer.OldMapPosition = Vector3.Zero;
        SendPacketToClient(transfer);
        GetSession().GameState.IsFirstEnterWorld = false;

        // JimsProxy (worldentry stage-0 tripwire): open the window telemetry BEFORE
        // raising the flag so every in-window forward line has a non-zero anchor.
        var gameState = GetSession().GameState;
        gameState.WorldEntryWindowSeq++;
        gameState.WorldEntryTransferPendingTick = Environment.TickCount64;
        gameState.WorldEntryNewWorldTick = 0;
        gameState.WorldEntryWindowForwardCount = 0;
        if (Framework.Settings.DebugOutput)
        {
            Log.Event("worldentry.window.opened", new
            {
                seq = gameState.WorldEntryWindowSeq,
                old_map = gameState.CurrentMapId,
                new_map = transfer.MapID,
            });
        }

        GetSession().GameState.IsWaitingForNewWorld = true;

        SuspendToken suspend = new();
        suspend.SequenceIndex = 3;
        suspend.Reason = 1;
        SendPacketToClient(suspend);
    }

    [PacketHandler(Opcode.SMSG_TRANSFER_ABORTED)]
    void HandleTransferAborted(WorldPacket packet)
    {
        TransferAborted transfer = new TransferAborted();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            transfer.MapID = packet.ReadUInt32();
        else
            transfer.MapID = GetSession().GameState.PendingTransferMapId;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            transfer.Reason = (TransferAbortReasonModern)packet.ReadUInt8();
        else
        {
            TransferAbortReasonLegacy legacyReason = (TransferAbortReasonLegacy)packet.ReadUInt8();
            transfer.Reason = legacyReason.CastEnum<TransferAbortReasonModern>();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            transfer.Arg = packet.ReadUInt8();

        // JimsProxy (camp login-eviction merge): the client never saw the swallowed
        // TRANSFER_PENDING, so its abort must be swallowed too. The hold drops back
        // to plain holding — the original login stands, and the first UPDATE_OBJECT
        // releases the held stream as a healthy login. Always-on: rare anomaly.
        if (GetSession().GameState.LoginEvictionHold.OnTransferAborted())
        {
            Log.Event("login.eviction_hold.transfer_aborted", new
            {
                aborted_map_id = transfer.MapID,
                reason = transfer.Reason.ToString(),
            });
            return;
        }

        SendPacketToClient(transfer);
        GetSession().GameState.IsWaitingForNewWorld = false;

        // JimsProxy (worldentry stage-0 tripwire): a transfer that aborts never gets
        // its CMSG_WORLD_PORT_RESPONSE — close out the telemetry anchors here so a
        // stale anchor can't leak into the next window's timing.
        var trackedState = GetSession().GameState;
        if (trackedState.WorldEntryTransferPendingTick != 0)
        {
            if (Framework.Settings.DebugOutput)
            {
                Log.Event("worldentry.window.aborted", new
                {
                    seq = trackedState.WorldEntryWindowSeq,
                    aborted_map_id = transfer.MapID,
                    reason = transfer.Reason.ToString(),
                    ms_since_transfer_pending = Environment.TickCount64 - trackedState.WorldEntryTransferPendingTick,
                    forwarded_in_window = trackedState.WorldEntryWindowForwardCount,
                });
            }
            trackedState.WorldEntryTransferPendingTick = 0;
            trackedState.WorldEntryNewWorldTick = 0;
        }

        var clearedCounts = GetSession().GameState.ResetInFlightCastState();
        var droppedGcdHold = GetSession().GameState.CancelGcdHold();
        var droppedCastTimeHold = GetSession().GameState.ClearHeldCastTimeCast();
        if (clearedCounts.normalCasts > 0 || clearedCounts.petCasts > 0 ||
            droppedGcdHold != null || droppedCastTimeHold != null)
        {
            Log.Event("session.transfer_aborted.cast_state_cleared", new
            {
                aborted_map_id = transfer.MapID,
                reason = transfer.Reason.ToString(),
                normal_casts_cleared = clearedCounts.normalCasts,
                pet_casts_cleared = clearedCounts.petCasts,
                other_caster_ids_cleared = clearedCounts.otherCasterIds,
                gcd_hold_dropped_spell_id = droppedGcdHold?.SpellId ?? 0,
                cast_time_hold_dropped_spell_id = droppedCastTimeHold?.SpellId ?? 0,
            });
        }
    }

    [PacketHandler(Opcode.SMSG_NEW_WORLD)]
    void HandleNewWorld(WorldPacket packet)
    {
        // JimsProxy (zep-stuck-no-move 2026-05-14, belt-and-suspenders): clear any
        // stale synthetic transport-clear ack sentinel. If the modern client failed
        // to ack our previous synth MoveTeleport, the sentinel would otherwise
        // linger forever and could eat a future legitimate teleport ack that
        // happened to carry the same MoveCounter value.
        GetSession().GameState.PendingSyntheticTransportClearAckCounter = 0;

        NewWorld teleport = new NewWorld();
        var previousMapId = GetSession().GameState.CurrentMapId;
        GetSession().GameState.CurrentMapId = teleport.MapID = packet.ReadUInt32();
        teleport.Position = packet.ReadVector3();
        teleport.Orientation = packet.ReadFloat();
        teleport.Reason = 4;

        // JimsProxy (camp login-eviction merge): the eviction's NEW_WORLD while the
        // login stream is held — merge instead of forwarding. The held login-verify
        // is rewritten to this destination (from the payload — different dungeons
        // evict to map 0 or 1) and the held stream flushed, so the client does ONE
        // clean load: exactly the shape the healthy post-eviction login proves
        // works. The client never sees a transfer, so the MSG_MOVE_WORLDPORT_ACK
        // its CMSG_WORLD_PORT_RESPONSE would normally produce (Server-side
        // MovementHandler.HandleWorldPortResponse) is synthesized here instead.
        // IsFirstEnterWorld deliberately stays TRUE — the client is still doing its
        // first world entry, and the login-initial handlers gated on it (the
        // SMSG_INITIALIZE_FACTIONS TimeSyncRequest synth the client needs to be
        // able to move, SMSG_LOGIN_SET_TIME_SPEED) may run after the merge. The
        // deferred transport synth likewise stays in Login mode. No
        // IsWaitingForWorldPortAck: the client will never ack a transfer it never
        // saw.
        var evictionHold = GetSession().GameState.LoginEvictionHold;
        var mergedHold = evictionHold.TryMergeOnNewWorld(teleport.MapID, teleport.Position, teleport.Orientation);
        if (mergedHold != null)
        {
            // Same cast-state sweep a real transfer performs. A fresh login should
            // have nothing in flight; parity keeps the two paths equivalent.
            var mergeClearedCounts = GetSession().GameState.ResetInFlightCastState();
            var mergeDroppedGcdHold = GetSession().GameState.CancelGcdHold();
            var mergeDroppedCastTimeHold = GetSession().GameState.ClearHeldCastTimeCast();

            foreach (var held in mergedHold)
                SendPacketToClientDirect(held);

            SendPacketToServer(new WorldPacket(Opcode.MSG_MOVE_WORLDPORT_ACK));

            // Always-on: fires only on the bug — the field signature that the merge ran.
            Log.Event("login.eviction_merge.merged", new
            {
                login_map_id = evictionHold.LoginMapId,
                new_map_id = teleport.MapID,
                x = teleport.Position.X,
                y = teleport.Position.Y,
                z = teleport.Position.Z,
                held_packets = mergedHold.Count,
                hold_ms = Environment.TickCount64 - evictionHold.StartTick,
                normal_casts_cleared = mergeClearedCounts.normalCasts,
                pet_casts_cleared = mergeClearedCounts.petCasts,
                gcd_hold_dropped_spell_id = mergeDroppedGcdHold?.SpellId ?? 0,
                cast_time_hold_dropped_spell_id = mergeDroppedCastTimeHold?.SpellId ?? 0,
            });
            return;
        }

        GetSession().GameState.IsFirstEnterWorld = false;

        if (GetSession().GameState.IsWaitingForNewWorld)
        {
            GetSession().GameState.IsWaitingForNewWorld = false;
            GetSession().GameState.IsWaitingForWorldPortAck = true;

            // JimsProxy (worldentry stage-0 tripwire): anchor the ack-wait phase.
            // NEW_WORLD itself is forwarded ~50ms later (scheduling breath below), so
            // window durations measured from here include that fixed offset.
            var trackedState = GetSession().GameState;
            trackedState.WorldEntryNewWorldTick = Environment.TickCount64;
            if (Framework.Settings.DebugOutput)
            {
                Log.Event("worldentry.window.new_world", new
                {
                    seq = trackedState.WorldEntryWindowSeq,
                    old_map = previousMapId,
                    new_map = teleport.MapID,
                    ms_since_transfer_pending = trackedState.WorldEntryTransferPendingTick != 0
                        ? Environment.TickCount64 - trackedState.WorldEntryTransferPendingTick
                        : -1,
                });
            }

            // JimsProxy (zone-transfer cast-state cleanup): the source map's server
            // state is torn down on transition (BG entry, instance change, zep arrival,
            // GM teleport). Any CMSG_CAST_SPELL we forwarded just before NEW_WORLD
            // will not get its SPELL_START / SPELL_GO / CAST_FAILED reply, or the
            // reply will be keyed to a guid that no longer matches the destination
            // map's bookkeeping. Without sweeping these orphans, a !HasStarted entry
            // survives the transition and HasForwardedPendingCast() returns true on
            // the destination map — the OUTER hold path at Server/SpellHandler.cs:443
            // then swallows every subsequent cast for the rest of the session
            // (warlock BG-entry total lockout observed 2026-05-24, 6h session).
            // Same cleanup that ResetInFlightCastState does for the unplanned-
            // reconnect path; held-slot drops mirror CancelGcdHold's silent drop on
            // OnDisconnect / HandleLogoutComplete (no ack to modern client — the
            // loading screen resets visible button state anyway).
            var clearedCounts = GetSession().GameState.ResetInFlightCastState();
            var droppedGcdHold = GetSession().GameState.CancelGcdHold();
            var droppedCastTimeHold = GetSession().GameState.ClearHeldCastTimeCast();
            Log.Event("session.world_transfer.cast_state_cleared", new
            {
                new_map_id = teleport.MapID,
                normal_casts_cleared = clearedCounts.normalCasts,
                pet_casts_cleared = clearedCounts.petCasts,
                other_caster_ids_cleared = clearedCounts.otherCasterIds,
                gcd_hold_dropped_spell_id = droppedGcdHold?.SpellId ?? 0,
                cast_time_hold_dropped_spell_id = droppedCastTimeHold?.SpellId ?? 0,
            });

            // --- START FIX: Map Transition Race Condition ---
            // JimsProxy (zep-transfer-stuck 2026-05-15): trimmed 2000ms → 50ms.
            // The long sleep was suspected of contributing to the post-transfer
            // stuck-movement repro (synth-clear arriving deep into the modern
            // client's loading-screen window). 50ms keeps a tiny scheduling
            // breath for the 1.12 server to start streaming destination-map
            // object updates without forcing a multi-second delay. Revert to
            // 2000 if desync (missing players/NPCs at destination on heavy
            // transports) returns.
            System.Threading.Thread.Sleep(50);
            // --- END FIX ---

            SendPacketToClient(teleport);

            // JimsProxy (worldentry root-ceremony breadcrumb): the arrival ceremony
            // (ROOT ×2 + UNROOT, wire-verified on every Kronos arrival) begins after
            // the worldport ack; open the accounting here.
            GetSession().GameState.WorldEntryCeremony.Begin("new_world", Environment.TickCount64);

            // JimsProxy (carried-root cure): arm the destination-side check. If the
            // client crosses this boundary believing itself rooted, the missing
            // unroot is synthesized at the player's first destination update.
            GetSession().GameState.WorldEntryPendingCarriedRootCheck = true;

            // JimsProxy (zep-stuck-low-latency-race 2026-05-17): defer the
            // transport-clear synth until the player's first post-NEW_WORLD
            // UpdateObject lands. Firing inline here at NEW_WORLD time worked at
            // ~150ms latency (synth happened to arrive at the modern client AFTER
            // the destination COMPRESSED_UPDATE_OBJECT) but raced at ~35ms (synth
            // arrived BEFORE the destination state, the server's subsequent
            // player-update re-attached the transport flag, and the rapid
            // clear→re-attach wedged the modern client into a no-MOVE_START_*
            // state when the destination position was on a transport — Grom'gol
            // zep tower platform, Ratchet boat deck). The deferred path in
            // UpdateHandler.ReadMovementUpdateBlock inspects moveInfo.TransportGuid
            // and only synths when the destination has the player OFF a transport,
            // preserving the dc39c39 original use case (cross-continent land
            // destinations) without breaking the on-transport destinations.
            GetSession().GameState.PendingDeferredTransportSynth = DeferredTransportSynthMode.NewWorld;

            if (teleport.MapID > 1)
            {
                UpdateLastInstance instance = new();
                instance.MapID = teleport.MapID;
                SendPacketToClient(instance);

                if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                    SendPacketToClient(new TimeSyncRequest());

                ResumeToken resume = new();
                resume.SequenceIndex = 3;
                resume.Reason = 1;
                SendPacketToClient(resume);
            }
        }
    }

    // JimsProxy (zep-stuck-low-latency-race 2026-05-17): synthesizes a player-targeted
    // SMSG_MOVE_TELEPORT with TransportGUID=default to force the modern client to clear
    // any stale MOVEMENTFLAG_ONTRANSPORT carried across from the source map's transport.
    // Fired from the deferred path in UpdateHandler.ReadMovementUpdateBlock only when the
    // destination's player UpdateObject confirms the player is NOT on a transport (zep
    // tower / boat deck) at the destination. The MoveCounter sentinel is dropped by the
    // server-side HandleMoveTeleportAck so the legacy server never sees the spurious ack.
    internal void FireDeferredTransportClearSynth(WowGuid128 playerGuid, Vector3 position, float orientation, string mode)
    {
        if (playerGuid.IsEmpty())
            return;

        const uint SyntheticTeleportAckSentinel = 0xFFFFFFFFu;
        MoveTeleport transportClear = new MoveTeleport();
        transportClear.MoverGUID = playerGuid;
        transportClear.MoveCounter = SyntheticTeleportAckSentinel;
        transportClear.Position = position;
        transportClear.Orientation = orientation;
        transportClear.PreloadWorld = 0;
        transportClear.TransportGUID = default;
        // Leave transportClear.Vehicle at its default null! sentinel — assigning
        // null trips the non-nullable-ref-type check; the existing MoveTeleport
        // path uses this same pattern for non-vehicle teleports.

        GetSession().GameState.PendingSyntheticTransportClearAckCounter =
            SyntheticTeleportAckSentinel;

        Framework.Logging.Log.Event("movement.transport_clear.synthesized", new
        {
            map_id = GetSession().GameState.CurrentMapId,
            player_low = playerGuid.GetCounter(),
            position = $"{position.X:F2},{position.Y:F2},{position.Z:F2}",
            deferred = true,
            mode = mode,
        });

        try
        {
            SendPacketToClient(transportClear);
            Framework.Logging.Log.Event("movement.transport_clear.send_completed", new
            {
                player_low = playerGuid.GetCounter(),
                sentinel_counter = SyntheticTeleportAckSentinel,
            });
        }
        catch (System.Exception ex)
        {
            Framework.Logging.Log.Event("movement.transport_clear.send_failed", new
            {
                player_low = playerGuid.GetCounter(),
                exception_type = ex.GetType().Name,
                exception_message = ex.Message,
            });
        }
    }

    // for server controlled units
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_BACK_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_PITCH_RATE)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_BACK_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_BACK_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_TURN_RATE)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_BACK_SPEED)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_SPEED)]
    void HandleMoveSplineSetSpeed(WorldPacket packet)
    {
        MoveSplineSetSpeed speed = new MoveSplineSetSpeed(packet.GetUniversalOpcode(false));
        speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        speed.Speed = packet.ReadFloat();
        SendPacketToClient(speed);

        // JimsProxy (speed-stuck-cc-spline): when CC ends mid-spline the server restores run speed via the spline opcode, not FORCE; mirror the FORCE cache so the regain-control reassert restores the buffed speed. See memory.
        if (packet.GetUniversalOpcode(false) == Opcode.SMSG_MOVE_SPLINE_SET_RUN_SPEED &&
            speed.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            GetSession().GameState.LastKnownPlayerRunSpeed = speed.Speed;
        }
    }

    // for own player
    [PacketHandler(Opcode.SMSG_FORCE_WALK_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_RUN_BACK_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_SWIM_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_TURN_RATE_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE)]
    [PacketHandler(Opcode.SMSG_FORCE_PITCH_RATE_CHANGE)]
    void HandleMoveForceSpeedChange(WorldPacket packet)
    { // for own player
        string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("SMSG_FORCE_", "SMSG_MOVE_SET_").Replace("_CHANGE", "");
        Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);

        MoveSetSpeed speed = new MoveSetSpeed(universalOpcode);
        speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        speed.MoveCounter = packet.ReadUInt32();

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) &&
            packet.GetUniversalOpcode(false) == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
        {
            packet.ReadUInt8(); // unk byte
        }

        speed.Speed = packet.ReadFloat();
        SendPacketToClient(speed);

        // JimsProxy (speed-stuck-after-fear-while-mounted): cache for CC-end reassert.
        if (universalOpcode == Opcode.SMSG_MOVE_SET_RUN_SPEED &&
            speed.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            GetSession().GameState.LastKnownPlayerRunSpeed = speed.Speed;
        }

        // Convenience in vanilla to use SwimSpeed as FlySpeed
        if (universalOpcode is Opcode.SMSG_MOVE_SET_SWIM_SPEED
                            or Opcode.SMSG_MOVE_SET_SWIM_BACK_SPEED &&
            LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
            MoveSetSpeed flySpeed = new MoveSetSpeed(flyOpcode);
            flySpeed.MoverGUID = speed.MoverGUID;
            flySpeed.MoveCounter = speed.MoveCounter;
            flySpeed.Speed = speed.Speed;
            SendPacketToClient(flySpeed);
        }
    }

    // for other players
    [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_BACK_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_PITCH_RATE)]
    [PacketHandler(Opcode.MSG_MOVE_SET_RUN_BACK_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_RUN_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_BACK_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_SPEED)]
    [PacketHandler(Opcode.MSG_MOVE_SET_TURN_RATE)]
    [PacketHandler(Opcode.MSG_MOVE_SET_WALK_SPEED)]
    void HandleMoveUpdateSpeed(WorldPacket packet)
    { // for other players
        string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("MSG_MOVE_SET", "SMSG_MOVE_UPDATE");
        Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);

        MoveUpdateSpeed speed = new MoveUpdateSpeed(universalOpcode);
        speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        speed.MoveInfo = new MovementInfo();
        speed.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
        ApplyHoverOverrideIfNeeded(speed.MoverGUID, speed.MoveInfo);
        var newFlags = ((MovementFlagWotLK)speed.MoveInfo.Flags).CastFlags<MovementFlagModern>();
        speed.MoveInfo.Flags = (uint)(newFlags);
        speed.MoveInfo.ValidateMovementInfo();
        speed.Speed = packet.ReadFloat();
        SendPacketToClient(speed);

        // Convenience in vanilla to use SwimSpeed as FlySpeed
        if (universalOpcode is Opcode.SMSG_MOVE_UPDATE_SWIM_SPEED
                            or Opcode.SMSG_MOVE_UPDATE_SWIM_BACK_SPEED &&
            LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
            MoveUpdateSpeed flySpeed = new MoveUpdateSpeed(flyOpcode);
            flySpeed.MoverGUID = speed.MoverGUID;
            flySpeed.MoveInfo = speed.MoveInfo;
            flySpeed.Speed = speed.Speed;
            SendPacketToClient(flySpeed);
        }
    }

    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ROOT)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNROOT)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FEATHER_FALL)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_NORMAL_FALL)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_HOVER)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_HOVER)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WATER_WALK)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_LAND_WALK)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_START_SWIM)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_STOP_SWIM)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_MODE)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_MODE)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLYING)]
    [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_FLYING)]
    void HandleSplineMovementMessages(WorldPacket packet)
    {
        Opcode universalOpcode = packet.GetUniversalOpcode(false);
        MoveSplineSetFlag spline = new MoveSplineSetFlag(universalOpcode);
        spline.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);

        // JimsProxy (worldentry root-ceremony breadcrumb, R40 branch (c)): a
        // spline-family root leg addressed to the PLAYER is the wrong-family
        // signature (server emits it instead of the force op while
        // CLIENT_CONTROL_LOST is up — e.g. the BG-end window an instant Leave
        // click races). Zero occurrences in the healthy corpus; count them so the
        // ceremony breadcrumb can name the family, not just the absence.
        var splineCeremony = GetSession().GameState.WorldEntryCeremony;
        if (spline.MoverGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            if (universalOpcode == Opcode.SMSG_MOVE_SPLINE_ROOT)
            {
                if (splineCeremony.Active)
                    System.Threading.Interlocked.Increment(ref splineCeremony.SplineRootsForwarded);
                // Carried-root belief: conservative — treat a self spline-root as
                // rooting (if the client ignores it, the stale belief only costs a
                // harmless no-op unroot at the next arrival).
                GetSession().GameState.ClientBelievesRooted = true;
            }
            else if (universalOpcode == Opcode.SMSG_MOVE_SPLINE_UNROOT)
            {
                if (splineCeremony.Active)
                    System.Threading.Interlocked.Increment(ref splineCeremony.SplineUnrootsForwarded);
                // R5-proven: the client accepts a spline-family unroot as clearing.
                GetSession().GameState.ClientBelievesRooted = false;
            }
        }

        SendPacketToClient(spline);
        // MIRASU (onyxia-landed-still-hovering): explicit hover toggles drive the registry + anim/gravity synth, else a landed mob keeps hover anim forever and a parked flyer stands and bounces.
        if (universalOpcode is Opcode.SMSG_MOVE_SPLINE_SET_HOVER or Opcode.SMSG_MOVE_SPLINE_UNSET_HOVER)
            SetHoverState(spline.MoverGUID, universalOpcode == Opcode.SMSG_MOVE_SPLINE_SET_HOVER, "explicit_toggle");
    }

    [PacketHandler(Opcode.SMSG_MOVE_ROOT)]
    [PacketHandler(Opcode.SMSG_MOVE_UNROOT)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_WATER_WALK)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_LAND_WALK)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_HOVERING)]
    [PacketHandler(Opcode.SMSG_MOVE_UNSET_HOVERING)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_CAN_FLY)]
    [PacketHandler(Opcode.SMSG_MOVE_UNSET_CAN_FLY)]
    [PacketHandler(Opcode.SMSG_MOVE_ENABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
    [PacketHandler(Opcode.SMSG_MOVE_DISABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
    [PacketHandler(Opcode.SMSG_MOVE_DISABLE_GRAVITY)]
    [PacketHandler(Opcode.SMSG_MOVE_ENABLE_GRAVITY)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_FEATHER_FALL)]
    [PacketHandler(Opcode.SMSG_MOVE_SET_NORMAL_FALL)]
    void HandleMoveForceFlagChange(WorldPacket packet)
    {
        Opcode universalOpcode = packet.GetUniversalOpcode(false);
        MoveSetFlag flag = new MoveSetFlag(universalOpcode);
        flag.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        flag.MoveCounter = packet.ReadUInt32();

        // JimsProxy (worldentry root-ceremony breadcrumb + carried-root cure
        // 2026-08-03): count the player's arrival ROOT/UNROOT ceremony legs for the
        // always-on unclosed-ceremony breadcrumb, and maintain the client-root
        // belief model — the proxy's record of what the client was last told about
        // its root state, which the carried-root cure gates on. No-op for non-self
        // movers and for the other force flags in this handler. See
        // WorldEntryCeremony.cs. Runs BEFORE the pre-create op hold below: a held
        // op is still delivered to the client (post-create), and on the rare
        // login-failure discard the stale belief errs fail-safe (one no-op synth
        // unroot at the next boundary; the next login's fresh GameSessionData
        // clears it).
        bool selfRoot = universalOpcode == Opcode.SMSG_MOVE_ROOT &&
                        flag.MoverGUID == GetSession().GameState.CurrentPlayerGuid;
        bool selfUnroot = universalOpcode == Opcode.SMSG_MOVE_UNROOT &&
                          flag.MoverGUID == GetSession().GameState.CurrentPlayerGuid;
        var ceremony = GetSession().GameState.WorldEntryCeremony;
        if (ceremony.Active && selfRoot)
            System.Threading.Interlocked.Increment(ref ceremony.RootsForwarded);
        if (ceremony.Active && selfUnroot)
            System.Threading.Interlocked.Increment(ref ceremony.UnrootsForwarded);
        if (selfRoot)
            GetSession().GameState.ClientBelievesRooted = true;
        else if (selfUnroot)
            GetSession().GameState.ClientBelievesRooted = false;

        // JimsProxy (camp stun lock, step 2): a self root/unroot arriving before the
        // login's first self create block is the wedge's lock recipe — hold it for
        // in-order release right after the create forwards (PreCreateOpHold; the
        // other force flags are not part of the arrival control ceremony and pass).
        if ((selfRoot || selfUnroot) &&
            GetSession().GameState.PreCreateOpHold.TryCapture(flag))
            return;

        SendPacketToClient(flag);
    }

    // JimsProxy (worldentry root-ceremony breadcrumb 2026-08-03): close out the
    // previous arrival's ceremony accounting. An opened-but-not-observably-closed
    // ceremony logs ONE always-on line (worldentry.ceremony.unclosed) so any field
    // Export Diagnostics carries the movement-lockup discriminator: missing unroot =
    // server never sent it; unroot forwarded but never acked = the client rejected
    // or could not apply it (the stuck-stun golden capture's discard fingerprint);
    // root acks short = a root leg was discarded; spline legs = the wrong-family
    // dialect. Healthy ceremonies log only under DebugOutput.
    internal void FlushWorldEntryCeremony(string reason)
    {
        var gameState = GetSession().GameState;
        var ceremony = gameState.WorldEntryCeremony;
        if (!ceremony.Active)
            return;

        bool anomalous = WorldEntryCeremonyTracker.IsAnomalous(
            ceremony.RootsForwarded, ceremony.RootAcks,
            ceremony.UnrootsForwarded, ceremony.UnrootAcks,
            ceremony.SplineRootsForwarded, ceremony.SplineUnrootsForwarded);
        if (anomalous || Framework.Settings.DebugOutput)
        {
            Framework.Logging.Log.Event(
                anomalous ? "worldentry.ceremony.unclosed" : "worldentry.ceremony.closed",
                new
                {
                    anchor = ceremony.Anchor,
                    flush_reason = reason,
                    ms_since_anchor = Environment.TickCount64 - ceremony.AnchorTickMs,
                    roots_forwarded = ceremony.RootsForwarded,
                    root_acks = ceremony.RootAcks,
                    unroots_forwarded = ceremony.UnrootsForwarded,
                    unroot_acks = ceremony.UnrootAcks,
                    spline_roots_forwarded = ceremony.SplineRootsForwarded,
                    spline_unroots_forwarded = ceremony.SplineUnrootsForwarded,
                    init_mover_complete_seen = ceremony.InitMoverCompleteSeen,
                });
        }
        ceremony.Reset();
    }

    [PacketHandler(Opcode.SMSG_COMPRESSED_MOVES)]
    void HandleCompressedMoves(WorldPacket packet)
    {
        var uncompressedSize = packet.ReadInt32();

        WorldPacket pkt = packet.Inflate(uncompressedSize);

        while (pkt.CanRead())
        {
            var size = pkt.ReadUInt8();
            var opc = pkt.ReadUInt16();
            var data = pkt.ReadBytes((uint)(size - 2));

            var pkt2 = new WorldPacket(opc, data);
            pkt2.SetReceiveTime(pkt.GetReceivedTime());
            HandlePacket(pkt2);
        }
    }

    [PacketHandler(Opcode.SMSG_ON_MONSTER_MOVE)]
    [PacketHandler(Opcode.SMSG_MONSTER_MOVE_TRANSPORT)]
    void HandleMonsterMove(WorldPacket packet)
    {
        WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);
        // JimsProxy (out-of-range-ghost): drop a stray monster-move for a unit we just destroyed/out-of-ranged so it can't re-ghost moving-in-place on the modern client.
        if (guid != GetSession().GameState.CurrentPlayerGuid &&
            GetSession().GameState.WasObjectRecentlyDestroyed(guid, out long ghostAgoMs))
        {
            if (Framework.Settings.DebugOutput)
                Framework.Logging.Log.Event("movement.dropped_stray_after_destroy", new
                {
                    guid = guid.GetCounter(),
                    ms_since_destroy = ghostAgoMs,
                    opcode = "monster_move",
                });
            return;
        }
        ServerSideMovement moveSpline = new();

        if (packet.GetUniversalOpcode(false) == Opcode.SMSG_MONSTER_MOVE_TRANSPORT)
        {
            moveSpline.TransportGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                moveSpline.TransportSeat = packet.ReadInt8();
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767)) // no idea when this was added exactly
            packet.ReadBool(); // "Toggle AnimTierInTrans"

        moveSpline.StartPosition = packet.ReadVector3();
        moveSpline.SplineId = packet.ReadUInt32();
        SplineTypeLegacy type = (SplineTypeLegacy)packet.ReadUInt8();
        switch (type)
        {
            case SplineTypeLegacy.FacingSpot:
            {
                moveSpline.SplineType = SplineTypeModern.FacingSpot;
                moveSpline.FinalFacingSpot = packet.ReadVector3();
                break;
            }
            case SplineTypeLegacy.FacingTarget:
            {
                moveSpline.SplineType = SplineTypeModern.FacingTarget;
                moveSpline.FinalFacingGuid = packet.ReadGuid().To128(GetSession().GameState);
                break;
            }
            case SplineTypeLegacy.FacingAngle:
            {
                moveSpline.SplineType = SplineTypeModern.FacingAngle;
                moveSpline.FinalOrientation = packet.ReadFloat();
                MovementInfo.ClampOrientation(ref moveSpline.FinalOrientation);
                break;
            }
            case SplineTypeLegacy.Stop:
            {
                moveSpline.SplineType = SplineTypeModern.None;
                // Hovering mobs: tell the client to settle in hover state on stop, otherwise
                // it falls back to cached MovementInfo flags (which may still have Falling)
                // and basketball-bounces between flight legs.
                if (IsHoveringMob(guid))
                {
                    moveSpline.SplineFlags |= SplineFlagModern.Flying | SplineFlagModern.AnimTierHover;
                    Framework.Logging.Log.Event("hover.spline_stop_override", new
                    {
                        guid = guid.ToString(),
                    });
                }
                // MIRASU (swim-mob basketball-bounce 2026-05-23): same pattern as hover
                // but for swimming mobs. Without AnimTierSwim on stop, the modern client
                // reverts to ground anim and ground-snaps Z, causing big up-down hops on
                // swimming bosses (Rotgrip) and patrolling water mobs.
                else if (IsSwimmingMob(guid))
                {
                    moveSpline.SplineFlags &= ~SplineFlagModern.Flying;
                    moveSpline.SplineFlags |= SplineFlagModern.AnimTierSwim | SplineFlagModern.CanSwim;
                    Framework.Logging.Log.Event("swim.spline_stop_override", new
                    {
                        guid = guid.ToString(),
                    });
                }
                MonsterMove moveStop = new MonsterMove(guid, moveSpline);
                SendPacketToClient(moveStop);
                return;
            }
        }

        bool hasAnimTier;
        bool hasTrajectory;
        bool hasCatmullRom;
        bool hasTaxiFlightFlags;
        // JimsProxy (Tallstrider-Fix): true if this is a vanilla default-Runmode spline
        // for a non-combat Normal-type move. Steering is held in suspense until path
        // points are parsed below; we only apply it when SplineCount > 1 (multi-segment
        // patrol path), never on point-to-point direct moves which are typical of
        // aggro/state transitions where the legacy server hasn't yet pushed InCombat.
        bool steeringCandidate = false;
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            var splineFlags = (SplineFlagVanilla)packet.ReadUInt32();
            hasAnimTier = false;
            hasTrajectory = false;
            hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagVanilla.Flying);
            hasTaxiFlightFlags = splineFlags == (SplineFlagVanilla.Runmode | SplineFlagVanilla.Flying);

            // Seed hover detection: any vanilla spline with Flying flag means this is a
            // hovering/flying mob. Mark it so all future packets (heartbeats, Stop, etc.)
            // for this guid carry hover state, even if HOVERHEIGHT field is never set.
            if (splineFlags.HasAnyFlag(SplineFlagVanilla.Flying))
                SetHoverState(guid, true, "flying_spline");

            if (splineFlags == SplineFlagVanilla.Runmode) // Default spline flags used by Vanilla and TBC servers
            {
                moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                UnitFlagsVanilla unitFlags = (UnitFlagsVanilla)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                if (unitFlags.HasFlag(UnitFlagsVanilla.CanSwim))
                    moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlagsVanilla.InCombat))
                    steeringCandidate = true; // resolved after path parsing — see Steering apply block below
            }
            else
            {
                moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }
            if (IsHoveringMob(guid))
            {
                moveSpline.SplineFlags &= ~(SplineFlagModern.Unknown5 | SplineFlagModern.Falling | SplineFlagModern.FallingSlow | SplineFlagModern.SmoothGroundPath | SplineFlagModern.CatmullRom);
                moveSpline.SplineFlags |= SplineFlagModern.Flying | SplineFlagModern.AnimTierHover;
                Framework.Logging.Log.Event("hover.spline_override", new
                {
                    guid = guid.ToString(),
                    spline_type = moveSpline.SplineType.ToString(),
                });
            }
            // MIRASU (swim-mob basketball-bounce 2026-05-23): same shape as hover override
            // but for swimming mobs. Strip ground-snapping / falling flags so the modern
            // client doesn't yank the mob's Z to ground level each spline tick. Add Flying
            // (means "moves freely in 3D, don't ground-follow") + AnimTierSwim. Without
            // Flying, the client still ground-snaps and warps between waypoints because
            // the smooth-3D path mode isn't enabled.
            else if (IsSwimmingMob(guid))
            {
                moveSpline.SplineFlags &= ~(SplineFlagModern.Unknown5 | SplineFlagModern.Falling | SplineFlagModern.FallingSlow | SplineFlagModern.SmoothGroundPath | SplineFlagModern.Flying);
                // MIRASU (swim moving anim 2026-05-23): Flying spline flag forced the
                // client to play flight glide. With UnitFlags.CanSwim now on
                // UNIT_FIELD_FLAGS (synthesized in UpdateHandler), the client treats
                // the unit as a swimmer for both physics and anim selection — Flying
                // is no longer needed and was actually preventing the swim moving anim.
                moveSpline.SplineFlags |= SplineFlagModern.AnimTierSwim | SplineFlagModern.CanSwim;
                Framework.Logging.Log.Event("swim.spline_override", new
                {
                    guid = guid.ToString(),
                    spline_type = moveSpline.SplineType.ToString(),
                });
            }
        }
        else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            var splineFlags = (SplineFlagTBC)packet.ReadUInt32();
            hasAnimTier = false;
            hasTrajectory = false;
            hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagTBC.Flying);
            hasTaxiFlightFlags = splineFlags == (SplineFlagTBC.Runmode | SplineFlagTBC.Flying);

            if (splineFlags == SplineFlagTBC.Runmode) // Default spline flags used by Vanilla and TBC servers
            {
                moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                UnitFlags unitFlags = (UnitFlags)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                if (unitFlags.HasFlag(UnitFlags.CanSwim))
                    moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlags.InCombat))
                    moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
            }
            else
                moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
        }
        else
        {
            var splineFlags = (SplineFlagWotLK)packet.ReadUInt32();
            hasAnimTier = splineFlags.HasAnyFlag(SplineFlagWotLK.AnimationTier);
            hasTrajectory = splineFlags.HasAnyFlag(SplineFlagWotLK.Trajectory);
            hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagWotLK.Flying | SplineFlagWotLK.CatmullRom);
            hasTaxiFlightFlags = splineFlags == (SplineFlagWotLK.WalkMode | SplineFlagWotLK.Flying);
            moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
        }

        if (hasAnimTier)
        {
            packet.ReadUInt8(); // Animation State
            packet.ReadInt32(); // Async-time in ms
        }

        moveSpline.SplineTimeFull = packet.ReadUInt32();

        if (hasTrajectory)
        {
            packet.ReadFloat(); // Vertical Speed
            packet.ReadInt32(); // Async-time in ms
        }

        moveSpline.SplineCount = packet.ReadUInt32();

        if (hasCatmullRom)
        {
            for (var i = 0; i < moveSpline.SplineCount; i++)
            {
                Vector3 vec = packet.ReadVector3();
                moveSpline.SplinePoints.Add(vec);
            }
            moveSpline.SplineFlags |= SplineFlagModern.UncompressedPath;
        }
        else
        {
            moveSpline.EndPosition = packet.ReadVector3();

            Vector3 mid = (moveSpline.StartPosition + moveSpline.EndPosition) * 0.5f;

            for (var i = 1; i < moveSpline.SplineCount; i++)
            {
                var vec = packet.ReadPackedVector3();

                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    vec = mid - vec;
                else
                    vec = moveSpline.EndPosition - vec;

                moveSpline.SplinePoints.Add(vec);
            }
        }

        // JimsProxy (Tallstrider-Fix): decide Steering using two gates that together
        // separate patrol corners (Steering = smooth, looks vanilla) from aggro chases
        // (no Steering = snap to heading, also looks vanilla).
        //
        //   Gate 1 — angle change at first segment, threshold 90°. Patrol corner
        //   turns at intersections rarely exceed this; large state-transition turns
        //   (creature was facing N, now charges S to reach player) blow past it.
        //
        //   Gate 2 — total path distance, threshold 20 yd. Patrol segments are short
        //   (~5-10 yd between waypoints); aggro chases are typically 15-40 yd to the
        //   target. The distance gate catches aggro paths whose first-segment angle
        //   happens to be small (creature already facing-ish toward player) — those
        //   would otherwise pass Gate 1 and re-trigger the sideways-running bug.
        //
        // Fallback: skip Steering when we have no cached orientation. Snap is the
        // vanilla default and the safer mis-classification.
        if (steeringCandidate && moveSpline.SplineCount >= 1)
        {
            Vector3 firstSegmentEnd = moveSpline.SplinePoints.Count > 0
                ? moveSpline.SplinePoints[0]
                : moveSpline.EndPosition;
            Vector3 firstDelta = firstSegmentEnd - moveSpline.StartPosition;
            Vector3 totalDelta = moveSpline.EndPosition - moveSpline.StartPosition;
            float firstDist2D = MathF.Sqrt(firstDelta.X * firstDelta.X + firstDelta.Y * firstDelta.Y);
            float totalDist2D = MathF.Sqrt(totalDelta.X * totalDelta.X + totalDelta.Y * totalDelta.Y);
            const float STEERING_ANGLE_THRESHOLD_RAD = 1.5708f; // 90°
            const float STEERING_DISTANCE_THRESHOLD_YD = 20.0f;
            if (firstDist2D > 0.1f
                && totalDist2D < STEERING_DISTANCE_THRESHOLD_YD
                && GetSession().GameState.LastKnownOrientation.TryGetValue(guid, out float currentOri))
            {
                float pathHeading = MathF.Atan2(firstDelta.Y, firstDelta.X);
                float diff = pathHeading - currentOri;
                while (diff > MathF.PI) diff -= 2 * MathF.PI;
                while (diff < -MathF.PI) diff += 2 * MathF.PI;
                if (MathF.Abs(diff) < STEERING_ANGLE_THRESHOLD_RAD)
                    moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
            }
        }

        // JimsProxy (taxi-flight-robustness): explicit leg-2+ detection. The rejoin
        // window (1000ms after CurrentPlayerCreateTime) only happens to fire on a leg
        // transition because the legacy server bumps the player CREATE timestamp. Treat
        // an active taxi flight as authoritative — every taxi-flagged spline that arrives
        // while IsInTaxiFlight is true is a leg of the same flight. Avoids the case
        // where leg-2's rejoin window misses by >1000ms and the spline gets handled as
        // a non-taxi move (would leave the player ungoverned mid-air).
        bool isAlreadyInTaxiFlight = System.Threading.Volatile.Read(ref GetSession().GameState.IsInTaxiFlight);
        bool isTaxiFlight = (hasTaxiFlightFlags &&
                            (GetSession().GameState.IsWaitingForTaxiStart ||
                             isAlreadyInTaxiFlight ||
                             Math.Abs(packet.GetReceivedTime() - GetSession().GameState.CurrentPlayerCreateTime) <= 1000) &&
                             GetSession().GameState.CurrentPlayerGuid == guid);
        bool isFirstLegOfFlight = isTaxiFlight && !isAlreadyInTaxiFlight;

        if (isTaxiFlight)
        {
            if (isFirstLegOfFlight)
            {
                // Exact sequence of packets from sniff. Required to transition the
                // modern client from grounded → flying-on-taxi state. Client instantly
                // teleports to destination if anything is left out.
                //
                // JimsProxy (taxi-flight-robustness): only emit on the FIRST leg of a
                // multi-hop flight. On leg 2+, the modern client is already in flying-
                // on-taxi state — sending stop-spline + control-loss tells it to snap-
                // stop at leg 2's start position, which is the visible "gryphon warp"
                // bundle 20260504-035621 captured during the second leg of an express
                // flight. Subsequent legs just get a clean MonsterMove for smooth
                // spline-to-spline transition.
                ServerSideMovement stopSpline = new();
                stopSpline.StartPosition = moveSpline.StartPosition;
                stopSpline.SplineId = moveSpline.SplineId - 2;
                MonsterMove moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                ControlUpdate update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);

                stopSpline.SplineId = moveSpline.SplineId - 1;
                moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);
            }

            // Spline flags + catmull-rom endpoint apply to every taxi spline (first leg
            // and follow-ons alike) so the client renders continuous flight movement.
            moveSpline.SplineFlags = SplineFlagModern.Flying |
                                     SplineFlagModern.CatmullRom |
                                     SplineFlagModern.CanSwim |
                                     SplineFlagModern.UncompressedPath |
                                     SplineFlagModern.Unknown5 |
                                     SplineFlagModern.Steering |
                                     SplineFlagModern.Unknown10;

            if (!hasCatmullRom && moveSpline.EndPosition != Vector3.Zero)
                moveSpline.SplinePoints.Add(moveSpline.EndPosition);
        }

        MonsterMove monsterMove = new MonsterMove(guid, moveSpline);
        SendPacketToClient(monsterMove);

        if (isTaxiFlight)
        {
            var session = GetSession();
            var gameState = session.GameState;

            if (gameState.IsWaitingForTaxiStart)
            {
                ActivateTaxiReplyPkt taxi = new();
                taxi.Reply = ActivateTaxiReply.Ok;
                SendPacketToClient(taxi);
                gameState.IsWaitingForTaxiStart = false;
            }
            System.Threading.Volatile.Write(ref gameState.IsInTaxiFlight, true);

            // JimsProxy (taxi-flight-robustness): a fresh taxi spline supersedes any pending
            // dismount Task — multi-segment chained flights re-enter this branch per leg and
            // we must not let an earlier leg's timer fire mid-next-leg. CancelTaxiDismount is
            // idempotent and a no-op when nothing is pending.
            gameState.CancelTaxiDismount("new_taxi_spline");

            // Clamp the server-reported spline duration to a sane upper bound. Longest vanilla
            // flight is well under 10 minutes; anything larger is a malformed/buggy server packet.
            // Without the clamp, a corrupt SplineTimeFull near uint.MaxValue would either wrap
            // negative on the (int) cast (Task.Delay throws ArgumentOutOfRange — silently swallowed
            // by the unawaited Task, dismount never fires) or stall the Task for weeks.
            const uint TAXI_FLIGHT_MAX_MS = 600_000;
            uint flightDuration = Math.Min(moveSpline.SplineTimeFull, TAXI_FLIGHT_MAX_MS);
            int delayMs = (int)flightDuration + 250;

            var attemptId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var cts = new System.Threading.CancellationTokenSource();
            // Capture the scheduled fire-time in the closure so `late_by_ms` stays correct
            // for THIS attempt even if a subsequent leg overwrites GameState.TaxiDismountFiresAtTickMs.
            // (Bundle 20260504-035621 showed late_by_ms=-3968 because leg 2 overwrote leg 1's
            // schedule before leg 1 fired.)
            long firesAtTickMs = Environment.TickCount64 + delayMs;
            gameState.TaxiDismountCts = cts;
            gameState.TaxiDismountFiresAtTickMs = firesAtTickMs;
            gameState.TaxiAttemptId = attemptId;

            Framework.Logging.Log.Event("taxi.flight.scheduled", new
            {
                attempt_id = attemptId,
                player_guid = guid.ToString(),
                spline_time_full_ms = moveSpline.SplineTimeFull,
                clamped = moveSpline.SplineTimeFull > TAXI_FLIGHT_MAX_MS,
                delay_ms = delayMs,
                is_first_leg = isFirstLegOfFlight,
            });

            // Capture session/playerGuid by value so the Task does not re-resolve GetSession()
            // on a possibly-replaced session post-reconnect (PR #119 spawns a new WorldClient
            // on unplanned DC). The CTS guards against firing after legitimate cancellation.
            WowGuid128 playerGuid = guid;
            var capturedSession = session;
            var capturedClient = this; // for SendPacketToClient — use captured-WorldClient routing
            var token = cts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // CancelTaxiDismount logged the cancellation event and disposed cts.
                    return;
                }

                // JimsProxy (taxi-flight-robustness): atomic claim of the right to fire.
                // CompareExchange(slot, null, cts) succeeds only if `slot` still references
                // OUR cts; on success it nulls the slot in the same operation. If a newer
                // leg already replaced the slot (multi-leg flight) or CancelTaxiDismount
                // ran between Task.Delay completing and reaching here, we lose the swap and
                // skip — eliminates the TOCTOU race that on long flights would otherwise
                // fire mid-flight dismount packets right as the next leg started.
                var prior = System.Threading.Interlocked.CompareExchange(
                    ref capturedSession.GameState.TaxiDismountCts, null, cts);
                if (!ReferenceEquals(prior, cts))
                {
                    Framework.Logging.Log.Event("taxi.flight.dismount_skipped", new
                    {
                        attempt_id = attemptId,
                        reason = "superseded",
                    });
                    return;
                }

                // We own the dismount; cleanup of cts/bookkeeping is now our responsibility.
                try
                {
                    if (!System.Threading.Volatile.Read(ref capturedSession.GameState.IsInTaxiFlight))
                    {
                        Framework.Logging.Log.Event("taxi.flight.dismount_skipped", new
                        {
                            attempt_id = attemptId,
                            reason = "not_in_taxi_flight",
                        });
                        return;
                    }
                    if (capturedSession.InstanceSocket == null)
                    {
                        Framework.Logging.Log.Event("taxi.flight.dismount_skipped", new
                        {
                            attempt_id = attemptId,
                            reason = "no_instance_socket",
                        });
                        return;
                    }

                    ControlUpdate control = new ControlUpdate();
                    control.Guid = playerGuid;
                    control.HasControl = true;
                    capturedClient.SendPacketToClient(control);

                    MoveSetFlag enableGravity = new MoveSetFlag(Opcode.SMSG_MOVE_ENABLE_GRAVITY);
                    enableGravity.MoverGUID = playerGuid;
                    capturedClient.SendPacketToClient(enableGravity);

                    MoveSetFlag unsetFly = new MoveSetFlag(Opcode.SMSG_MOVE_UNSET_CAN_FLY);
                    unsetFly.MoverGUID = playerGuid;
                    capturedClient.SendPacketToClient(unsetFly);

                    MoveSetFlag unroot = new MoveSetFlag(Opcode.SMSG_MOVE_UNROOT);
                    unroot.MoverGUID = playerGuid;
                    capturedClient.SendPacketToClient(unroot);

                    System.Threading.Volatile.Write(ref capturedSession.GameState.IsInTaxiFlight, false);
                    long firedAt = Environment.TickCount64;
                    Framework.Logging.Log.Event("taxi.flight.dismount_fired", new
                    {
                        attempt_id = attemptId,
                        player_guid = playerGuid.ToString(),
                        late_by_ms = firedAt - firesAtTickMs,
                    });
                }
                finally
                {
                    // CompareExchange already nulled TaxiDismountCts; clear the rest.
                    capturedSession.GameState.TaxiDismountFiresAtTickMs = 0;
                    capturedSession.GameState.TaxiAttemptId = null;
                    cts.Dispose();
                }
            });
        }
    }
}
