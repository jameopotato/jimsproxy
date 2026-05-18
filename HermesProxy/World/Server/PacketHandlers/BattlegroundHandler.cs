using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_BATTLEMASTER_JOIN)]
    void HandleBattlefieldJoin(BattlemasterJoin join)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BATTLEMASTER_JOIN);
        packet.WriteGuid(join.BattlemasterGuid.To64());
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.WriteUInt32(GameData.GetMapIdFromBattlegroundId(join.BattlefieldListId));
        else
            packet.WriteUInt32(join.BattlefieldListId);
        packet.WriteInt32(join.BattlefieldInstanceID);
        packet.WriteBool(join.JoinAsGroup);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_BATTLEFIELD_PORT)]
    void HandleBattlefieldPort(BattlefieldPort port)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BATTLEFIELD_PORT);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteUInt8(2);
            packet.WriteUInt8(0);
            packet.WriteUInt32(GetSession().GameState.GetBattleFieldQueueType(port.Ticket.Id));
            packet.WriteUInt16(0x1F90);
            packet.WriteBool(port.AcceptedInvite);
        }
        else
        {
            packet.WriteUInt32(GetSession().GameState.GetBattleFieldQueueType(port.Ticket.Id));
            packet.WriteBool(port.AcceptedInvite);
        }
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_REQUEST_BATTLEFIELD_STATUS)]
    void HandleRequestBattlefieldStatus(RequestBattlefieldStatus log)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BATTLEFIELD_STATUS);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PVP_LOG_DATA)]
    void HandlePvPLogData(PVPLogDataRequest log)
    {
        // JimsProxy (pvp-log-data-throttle 2026-05-17): forward at most once
        // per 10 seconds per session. Kronos / vanilla-emu servers treat
        // sustained CMSG_PVP_LOG_DATA above ~10/min as a spam-bot signal and
        // silently queue a kick (observed BG-exit DC, log
        // jimsproxy-20260516-...-BGDC.jsonl). Common BG-addons hammer this:
        // BattlegroundEnemies' default 2s OnUpdate ticker = ~30/min;
        // enemyFrames calls RequestBattlefieldScoreData() every frame from
        // OnUpdate = ~3600/min at 60fps. Throttling proxy-side covers every
        // current and future misbehaving addon. Side effect for dropped
        // requests: the client won't fire UPDATE_BATTLEFIELD_SCORE for THIS
        // drop, but the last forwarded request's response still populated
        // the cache — 10s staleness is fine for BG scoreboards.
        const long ThrottleWindowMs = 10_000;
        long now = Environment.TickCount64;
        long last = GetSession().GameState.LastForwardedPvpLogDataTickMs;
        long deltaMs = last == 0 ? long.MaxValue : now - last;
        if (deltaMs < ThrottleWindowMs)
        {
            Log.Event("battleground.pvp_log_data.throttle_dropped", new
            {
                ms_since_last_forward = deltaMs,
                throttle_window_ms = ThrottleWindowMs,
            });
            return;
        }

        GetSession().GameState.LastForwardedPvpLogDataTickMs = now;
        WorldPacket packet = new WorldPacket(Opcode.MSG_PVP_LOG_DATA);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_BATTLEFIELD_LEAVE)]
    void HandleBattlefieldLeave(BattlefieldLeave leave)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BATTLEFIELD_LEAVE);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteUInt8(2);
            packet.WriteUInt8(0);
            packet.WriteUInt32(GetSession().GameState.GetBattleFieldQueueType(1));
            packet.WriteUInt16(0x1F90);
        }
        else
            packet.WriteUInt32((uint)GetSession().GameState.CurrentMapId!);
        SendPacketToServer(packet);
    }
}
