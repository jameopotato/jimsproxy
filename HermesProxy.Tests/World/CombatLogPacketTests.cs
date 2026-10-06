using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (2026-10-06): the three 1.12 combat-log packets that had no handler. Legacy bytes
// follow the cmangos Unit.cpp writers and the sizes seen on Kronos (28, 23 and 23 bytes with
// the opcode); the modern side follows WowPacketParser V6 CombatLogHandler.
public class CombatLogPacketTests
{
    const ulong Warrior = 27032ul;                                  // player, high 0x0000
    const ulong Mob = (0xF130ul << 48) | (1234ul << 24) | 56ul;     // creature entry 1234, counter 56

    static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    static WorldPacket Legacy(Opcode opcode, params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts)
            all.AddRange(p);
        return new WorldPacket((uint)opcode, all.ToArray());
    }

    static GameSessionData Session() => GameSessionData.CreateForTesting();

    static void AssertCreature(WowGuid128 guid)
    {
        Assert.Equal(HighGuidType.Creature, guid.GetHighType());
        Assert.Equal(1234u, guid.GetEntry());
        Assert.Equal(56ul, guid.GetCounter());
    }

    static void AssertWarrior(WowGuid128 guid)
    {
        Assert.Equal(HighGuidType.Player, guid.GetHighType());
        Assert.Equal(Warrior, guid.GetCounter());
    }

    // SMSG_SPELLLOGMISS: [spell][caster][log format][count][target][miss]. 26 bytes, one target.
    [Fact]
    public void SpellMissLog_ParsesOneResistedTarget()
    {
        var packet = Legacy(Opcode.SMSG_SPELL_MISS_LOG,
            U32(25771), U64(Warrior), new byte[] { 0 }, U32(1), U64(Mob), new byte[] { (byte)SpellMissInfo.Resist });

        var log = WorldClient.ParseSpellMissLog(packet, Session());

        Assert.Equal(25771u, log.SpellID);
        AssertWarrior(log.Caster);
        var entry = Assert.Single(log.Entries);
        AssertCreature(entry.Victim);
        Assert.Equal(SpellMissInfo.Resist, entry.MissReason);
    }

    [Fact]
    public void SpellMissLog_WritesModernLayout()
    {
        var log = new SpellMissLog();
        log.SpellID = 25771;
        log.Caster = WowGuid128.Create(HighGuidType703.Player, Warrior);
        log.Entries.Add(new SpellLogMissEntry { Victim = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 56), MissReason = SpellMissInfo.Resist });

        log.WritePacketData();
        var wire = new WorldPacket(0, log.GetData()!);

        Assert.Equal(25771u, wire.ReadUInt32());
        Assert.Equal(log.Caster, wire.ReadPackedGuid128());
        Assert.Equal(1, wire.ReadInt32());
        Assert.Equal(log.Entries[0].Victim, wire.ReadPackedGuid128());
        Assert.Equal((byte)SpellMissInfo.Resist, wire.ReadUInt8());
        Assert.False(wire.ReadBit()); // no debug block
        Assert.False(wire.CanRead());
    }

    // SMSG_PROCRESIST: [caster][target][spell][log format]. 21 bytes.
    [Fact]
    public void ProcResist_ParsesCasterTargetSpell()
    {
        var packet = Legacy(Opcode.SMSG_PROC_RESIST, U64(Warrior), U64(Mob), U32(12970), new byte[] { 0 });

        var resist = WorldClient.ParseProcResist(packet, Session());

        AssertWarrior(resist.Caster);
        AssertCreature(resist.Target);
        Assert.Equal(12970u, resist.SpellID);
    }

    [Fact]
    public void ProcResist_WritesModernLayout()
    {
        var resist = new ProcResist();
        resist.Caster = WowGuid128.Create(HighGuidType703.Player, Warrior);
        resist.Target = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 56);
        resist.SpellID = 12970;

        resist.WritePacketData();
        var wire = new WorldPacket(0, resist.GetData()!);

        Assert.Equal(resist.Caster, wire.ReadPackedGuid128());
        Assert.Equal(resist.Target, wire.ReadPackedGuid128());
        Assert.Equal(12970u, wire.ReadUInt32());
        Assert.False(wire.ReadBit()); // no Rolled
        Assert.False(wire.ReadBit()); // no Needed
        Assert.False(wire.CanRead());
    }

    // SMSG_SPELLORDAMAGE_IMMUNE: [caster][target][spell][log format]. 21 bytes.
    [Fact]
    public void SpellOrDamageImmune_ParsesCasterVictimSpell()
    {
        var packet = Legacy(Opcode.SMSG_SPELL_OR_DAMAGE_IMMUNE, U64(Warrior), U64(Mob), U32(1833), new byte[] { 0 });

        var immune = WorldClient.ParseSpellOrDamageImmune(packet, Session());

        AssertWarrior(immune.CasterGUID);
        AssertCreature(immune.VictimGUID);
        Assert.Equal(1833u, immune.SpellID);
        Assert.False(immune.IsPeriodic);
    }

    [Fact]
    public void SpellOrDamageImmune_WritesModernLayout()
    {
        var immune = new SpellOrDamageImmune();
        immune.CasterGUID = WowGuid128.Create(HighGuidType703.Player, Warrior);
        immune.VictimGUID = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 56);
        immune.SpellID = 1833;

        immune.WritePacketData();
        var wire = new WorldPacket(0, immune.GetData()!);

        Assert.Equal(immune.CasterGUID, wire.ReadPackedGuid128());
        Assert.Equal(immune.VictimGUID, wire.ReadPackedGuid128());
        Assert.Equal(1833u, wire.ReadUInt32());
        Assert.False(wire.ReadBit()); // IsPeriodic
        Assert.False(wire.CanRead());
    }
}
