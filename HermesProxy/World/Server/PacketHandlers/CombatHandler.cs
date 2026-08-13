using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_ATTACK_SWING)]
    void HandleAttackSwing(AttackSwing attack)
    {
        var victim64 = attack.Victim.To64();
        var state = GetSession().GameState;

        // De-dupe guard + handshake bookkeeping live on GameSessionData so they can be
        // unit-tested without a socket. False means we already believe we are auto-attacking
        // this exact victim, so the swing is swallowed rather than forwarded.
        if (!state.TryBeginLocalPlayerAttackSwing(victim64))
            return;

        // TEMP-DIAG (#464 follow-up) — REMOVE with the breadcrumb in Client/PacketHandlers/CombatHandler.cs.
        // Diagnostics only: lets the empty-victim breadcrumb report how long
        // after our swing the server's refusal arrived (219ms in the capture that found the bug).
        state.LastAttackSwingSentTick = System.Environment.TickCount64;

        WorldPacket packet = new WorldPacket(Opcode.CMSG_ATTACK_SWING);
        packet.WriteGuid(victim64);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_ATTACK_STOP)]
    void HandleAttackSwing(AttackStop attack)
    {
        var state = GetSession().GameState;

        // Only defer ATTACK_STOP while waiting for server to acknowledge our SWING.
        // During this window, a rapid STOP→SWING (target switch) would corrupt cMangos
        // combat state. Once SMSG_ATTACK_START has arrived, server state is stable and
        // can handle a clean stop (ESC, /stopattack, etc.)
        if (state.WaitingForAttackStart)
        {
            state.DeferredAttackStop = true;
            return;
        }

        state.CurrentAttackTarget = default;
        WorldPacket packet = new WorldPacket(Opcode.CMSG_ATTACK_STOP);
        SendPacketToServer(packet);
    }
    [PacketHandler(Opcode.CMSG_SET_SHEATHED)]
    void HandleSetSheathed(SetSheathed sheath)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_SHEATHED);
        packet.WriteInt32(sheath.SheathState);
        SendPacketToServer(packet);
    }
}
