using System;
using System.Buffers.Binary;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (2026-10-06): the legacy SMSG_ITEM_TIME_UPDATE is [item guid, 8 bytes][seconds, 4
// bytes], 14 bytes with the opcode, as captured on 2026-09-26 behind a looted Shadowforge Torch
// (a 5-minute quest item). It was never translated, so timed items showed no countdown.
public class ItemTimeUpdateTests
{
    static WorldPacket Legacy(ulong itemGuid, uint seconds)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, itemGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), seconds);
        return new WorldPacket((uint)Opcode.SMSG_ITEM_TIME_UPDATE, bytes);
    }

    [Fact]
    public void TranslatesItemGuidAndSecondsLeft()
    {
        ulong legacyItem = (0x4000ul << 48) | 123456ul; // HighGuidTypeLegacy.Item, counter 123456

        var update = WorldClient.ParseItemTimeUpdate(Legacy(legacyItem, 300), GameSessionData.CreateForTesting());

        Assert.Equal(HighGuidType.Item, update.ItemGuid.GetHighType());
        Assert.Equal(123456ul, update.ItemGuid.GetCounter());
        Assert.Equal(300u, update.DurationLeft);
    }
    // The modern packet is the packed 128-bit guid followed by the seconds, nothing else.
    [Fact]
    public void ModernPacketIsPackedGuidThenSeconds()
    {
        var update = new HermesProxy.World.Server.Packets.ItemTimeUpdate();
        update.ItemGuid = WowGuid128.Create(HighGuidType703.Item, 123456);
        update.DurationLeft = 300;

        update.WritePacketData();
        var wire = new WorldPacket(0, update.GetData()!);

        Assert.Equal(update.ItemGuid, wire.ReadPackedGuid128());
        Assert.Equal(300u, wire.ReadUInt32());
        Assert.False(wire.CanRead());
    }
}
