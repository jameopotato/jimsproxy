using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_DUEL_REQUESTED)]
    void HandleDuelRequested(WorldPacket packet)
    {
        DuelRequested duel = new DuelRequested();
        duel.ArbiterGUID = packet.ReadGuid().To128(GetSession().GameState);
        duel.RequestedByGUID = packet.ReadGuid().To128(GetSession().GameState);
        duel.RequestedByWowAccount = GetSession().GetGameAccountGuidForPlayer(duel.RequestedByGUID);
        SendPacketToClient(duel);
    }

    [PacketHandler(Opcode.SMSG_DUEL_COUNTDOWN)]
    void HandleDuelCountdown(WorldPacket packet)
    {
        DuelCountdown duel = new DuelCountdown();
        duel.Countdown = packet.ReadUInt32();
        SendPacketToClient(duel);
    }

    [PacketHandler(Opcode.SMSG_DUEL_COMPLETE)]
    void HandleDuelComplete(WorldPacket packet)
    {
        DuelComplete duel = new DuelComplete();

        try
        {
            // Attempt to read the Vanilla payload
            duel.Started = packet.ReadBool();
        }
        catch
        {
            // If Kronos sends an empty/truncated packet due to a logout forfeit,
            // catch the error and default to false to safely end the duel state.
            duel.Started = false;
        }

        // GUARANTEED to fire, forcing the 1.14 UI to drop combat
        SendPacketToClient(duel);
    }

    [PacketHandler(Opcode.SMSG_DUEL_WINNER)]
    void HandleDuelWinner(WorldPacket packet)
    {
        DuelWinner duel = new DuelWinner();

        try
        {
            duel.Fled = packet.ReadBool();
            duel.BeatenName = packet.ReadCString();
            duel.WinnerName = packet.ReadCString();
        }
        catch
        {
            // If the losing player logged out, their character object is gone
            // and Kronos might fail to send strings. Provide safe fallbacks.
            duel.Fled = true;
            duel.BeatenName = "Unknown";
            duel.WinnerName = "Unknown";
        }

        duel.BeatenVirtualRealmAddress = GetSession().RealmId.GetAddress();
        duel.WinnerVirtualRealmAddress = GetSession().RealmId.GetAddress();

        // GUARANTEED to fire
        SendPacketToClient(duel);
    }

    [PacketHandler(Opcode.SMSG_DUEL_IN_BOUNDS)]
    void HandleDuelInBounds(WorldPacket packet)
    {
        DuelInBounds duel = new DuelInBounds();
        SendPacketToClient(duel);
    }

    [PacketHandler(Opcode.SMSG_DUEL_OUT_OF_BOUNDS)]
    void HandleDuelOutOfBounds(WorldPacket packet)
    {
        DuelOutOfBounds duel = new DuelOutOfBounds();
        SendPacketToClient(duel);
    }
}
