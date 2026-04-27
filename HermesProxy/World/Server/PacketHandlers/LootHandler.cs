using Framework.Constants;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_LOOT_RELEASE)]
    void HandleLootRelease(LootRelease loot)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOOT_RELEASE);
        packet.WriteGuid(loot.Owner.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_LOOT_ITEM)]
    void HandleLootItem(LootItemPkt loot)
    {
        foreach (var item in loot.Loot)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_AUTOSTORE_LOOT_ITEM);
            packet.WriteUInt8(item.LootListID);
            SendPacketToServer(packet);
        }
    }

    [PacketHandler(Opcode.CMSG_LOOT_UNIT)]
    void HandleLootUnit(LootUnit loot)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOOT_UNIT);
        packet.WriteGuid(loot.Unit.To64());
        SendPacketToServer(packet);
        GetSession().GameState.LastLootTargetGuid = loot.Unit.To64();
    }

    [PacketHandler(Opcode.CMSG_LOOT_MONEY)]
    void HandleLootMoney(LootMoney loot)
    {
        //MIRASU - Synthesize SMSG_LOOT_MONEY_NOTIFY BEFORE forwarding to Kronos so the chat line isn't gated
        //MIRASU   behind the synchronous encrypted write to the legacy server socket. Native 1.12 clients
        //MIRASU   printed this line locally on CMSG send (zero latency); reordering here matches that timing.
        uint coins = GetSession().GameState.CurrentLootCoins;
        if (coins > 0 && !LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            LootMoneyNotify notify = new();
            notify.Money = coins;
            notify.SoleLooter = true;
            SendPacket(notify);
            GetSession().GameState.CurrentLootCoins = 0; //MIRASU - consume so we don't double-print if client sends CMSG_LOOT_MONEY again
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOOT_MONEY);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SET_LOOT_METHOD)]
    void HandleSetLootMethod(SetLootMethod loot)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_LOOT_METHOD);
        packet.WriteUInt32((uint)loot.LootMethod);
        packet.WriteGuid(loot.LootMasterGUID.To64());
        packet.WriteUInt32(loot.LootThreshold);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_OPT_OUT_OF_LOOT)]
    void HandleOptOutOfLoot(OptOutOfLoot loot)
    {
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_OPT_OUT_OF_LOOT);
            packet.WriteInt32(loot.PassOnLoot ? 1 : 0);
            SendPacketToServer(packet);
        }
        else
            GetSession().GameState.IsPassingOnLoot = loot.PassOnLoot;
    }

    [PacketHandler(Opcode.CMSG_LOOT_ROLL)]
    void HandleLootRoll(LootRoll loot)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOOT_ROLL);
        packet.WriteGuid(loot.LootObj.To64());
        packet.WriteUInt32(loot.LootListID);
        packet.WriteUInt8((byte)loot.RollType);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_LOOT_MASTER_GIVE)]
    void HandleLootMasterGive(LootMasterGive loot)
    {
        foreach (var item in loot.Loot)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_LOOT_MASTER_GIVE);
            packet.WriteGuid(item.LootObj.To64());
            packet.WriteUInt8(item.LootListID);
            packet.WriteGuid(loot.TargetGUID.To64());
            SendPacketToServer(packet);
        }
    }
}
