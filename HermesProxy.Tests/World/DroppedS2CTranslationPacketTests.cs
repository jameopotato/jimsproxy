using System;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// Byte-layout tests for the ServerPacket structs added by the dropped-s2c
// translation pass (DROPPED-S2C-AUDIT.md): write via the ISpanWritable fast
// path, read back with WorldPacket (the modern-side reader), and assert the
// field order/types match the sourced wire shapes (WPP 3.4-classic parsers /
// TrinityCore master Write() implementations).
public class DroppedS2CTranslationPacketTests
{
    static DroppedS2CTranslationPacketTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static WowGuid128 PlayerGuid => WowGuid128.Create(HighGuidType703.Player, 4242);
    private static WowGuid128 CreatureGuid => WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 777);

    private static WorldPacket WriteViaSpan(ISpanWritable packet)
    {
        byte[] buffer = new byte[Math.Max(packet.MaxSize, 1)];
        int written = packet.WriteToSpan(buffer);
        Assert.InRange(written, 0, packet.MaxSize);
        return new WorldPacket(0, buffer.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void PetMode_Layout_GuidThenCommandFlagReact()
    {
        var packet = new PetMode
        {
            PetGUID = CreatureGuid,
            CommandState = CommandStates.Follow,
            Flag = 0x8,
            ReactState = ReactStates.Aggressive,
        };

        WorldPacket reader = WriteViaSpan(packet);

        Assert.Equal(CreatureGuid, reader.ReadPackedGuid128());
        Assert.Equal((byte)CommandStates.Follow, reader.ReadUInt8());
        Assert.Equal((byte)0x8, reader.ReadUInt8());
        Assert.Equal((byte)ReactStates.Aggressive, reader.ReadUInt8());
        Assert.False(reader.CanRead());
    }

    [Fact]
    public void QuestLogFull_Layout_EmptyBody()
    {
        var packet = new QuestLogFull();

        byte[] buffer = new byte[1];
        Assert.Equal(0, packet.WriteToSpan(buffer));
        Assert.Equal(0, packet.MaxSize);
    }

    [Fact]
    public void ItemTimeUpdate_Layout_GuidThenDuration()
    {
        var packet = new ItemTimeUpdate { ItemGuid = PlayerGuid, DurationLeft = 900 };

        WorldPacket reader = WriteViaSpan(packet);

        Assert.Equal(PlayerGuid, reader.ReadPackedGuid128());
        Assert.Equal(900u, reader.ReadUInt32());
        Assert.False(reader.CanRead());
    }

    [Fact]
    public void SpellOrDamageImmune_Layout_GuidsSpellThenPeriodicBit()
    {
        var packet = new SpellOrDamageImmune
        {
            CasterGUID = CreatureGuid,
            VictimGUID = PlayerGuid,
            SpellID = 25990,
            IsPeriodic = false,
        };

        WorldPacket reader = WriteViaSpan(packet);

        Assert.Equal(CreatureGuid, reader.ReadPackedGuid128());
        Assert.Equal(PlayerGuid, reader.ReadPackedGuid128());
        Assert.Equal(25990u, reader.ReadUInt32());
        Assert.False(reader.ReadBit());
        Assert.False(reader.CanRead());
    }

    [Fact]
    public void ProcResist_Layout_GuidsSpellThenTwoFalseOptionalBits()
    {
        var packet = new ProcResist
        {
            Caster = PlayerGuid,
            Target = CreatureGuid,
            SpellID = 21992,
        };

        WorldPacket reader = WriteViaSpan(packet);

        Assert.Equal(PlayerGuid, reader.ReadPackedGuid128());
        Assert.Equal(CreatureGuid, reader.ReadPackedGuid128());
        Assert.Equal(21992u, reader.ReadUInt32());
        Assert.False(reader.ReadBit()); // HasRolled
        Assert.False(reader.ReadBit()); // HasNeeded
        Assert.False(reader.CanRead());
    }

    [Fact]
    public void UpdateLastInstance_Layout_MapIdOnly()
    {
        var packet = new UpdateLastInstance { MapID = 409 };

        WorldPacket reader = WriteViaSpan(packet);

        Assert.Equal(409u, reader.ReadUInt32());
        Assert.False(reader.CanRead());
    }
}
